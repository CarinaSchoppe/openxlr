// SPDX-License-Identifier: GPL-3.0-only
// A bounded dry-microphone loop upstream of the live insert chain.
//
// Who writes what. The audio thread owns the samples, the cursor, the
// waveform bins and the output meters `frames`, `mode` and `cursor`: the mode
// it reports is the one it ran. The command pipe and the main-thread tick
// write only `command` and `raw`, the requests. The audio thread writes
// `command` once, back to live, when the buffer is full; the tick does the
// same when a recording request has gone unserved for the whole bound, so a
// suspended input cannot start recording minutes after the click.
//
// Two outputs. The main one feeds the channel's chain; the tap goes straight
// to the channel, past the chain. `raw` moves the signal from one to the
// other with a short linear crossfade, so the same words can be heard with
// and without the processing at once, with no chain rebuilt.
//
// Where the loop starts over. The last `fade` samples of the take fade out
// linearly while the first `fade` fade in under them, and the next pass
// carries on from sample `fade`, so the wrap has no step to click. The first
// pass starts at the true start of the take; every pass after it is `fade`
// samples shorter than the take.
//
// The take's waveform. While recording, the audio thread keeps the peak of
// each of PEAK_BINS stretches of the buffer. When a recording ends with
// samples in it, it freezes the bins into PEAK_POINTS peaks, publishes the
// length and counts the take. The tick sets the meters p0 to p199, the
// completed length and then `take`. They are printed in that order, so a
// reader that sees the new `take` already holds its peaks. The next recording
// reuses only the active bins, leaving the completed snapshot untouched.
#include "host.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

enum {
  SOUND_CHECK_SECONDS = 10,
  LIVE = 0,
  RECORD = 1,
  LOOP = 2,
  PEAK_POINTS = 200,
  PEAK_BINS = 2000,
};
// The crossfade between the processed and the raw path, in seconds.
#define RAW_CROSSFADE 0.02
// The crossfade where the loop starts over, in seconds. A take shorter than
// four of these fades over a quarter of its length.
#define LOOP_CROSSFADE 0.05

typedef struct {
  float *samples;
  uint32_t capacity, length, cursor;  // audio thread only
  uint32_t fade;                      // audio thread only: the crossfade of the take that loops
  uint32_t fade_frames;               // set at load: the longest crossfade
  int previous;                       // audio thread only
  float raw_gain, raw_step;           // audio thread only: 0 processed, 1 raw
  float bins[PEAK_BINS];             // audio thread only
  _Atomic float finished_peaks[PEAK_POINTS]; // completed take, independent of the next recording
  _Atomic uint32_t waveform_version;  // odd while the audio thread replaces the snapshot
  _Atomic uint32_t take_length;       // the last finished take, then...
  _Atomic uint32_t takes;             // ...counted, by the audio thread
  uint32_t published;                 // tick only: the take now in p0..p199
  bool timed_record;                  // tick only
  struct timespec record_started;     // tick only
  Control *command, *raw, *frames, *mode, *playhead, *take_frames, *take;
  Control *peaks[PEAK_POINTS];
  char names[PEAK_POINTS][8];
} SoundCheck;

static bool sound_check_load(Host *h, char **arguments) {
  if (h->channels != 1) return false;
  SoundCheck *s = calloc(1, sizeof(*s));
  if (!s) return false;
  h->impl = s;
  s->capacity = h->rate * SOUND_CHECK_SECONDS;
  s->samples = calloc(s->capacity, sizeof(float));
  s->raw_step = (float)(1.0 / (RAW_CROSSFADE * h->rate));
  s->fade_frames = (uint32_t)(LOOP_CROSSFADE * h->rate);
  s->command = host_add_control(h, "command", LIVE, LOOP, LIVE, false, NULL);
  s->raw = host_add_control(h, "raw", 0, 1, 0, false, NULL);
  s->frames = host_add_control(h, "frames", 0, (float)s->capacity, 0, true, NULL);
  s->mode = host_add_control(h, "mode", LIVE, LOOP, LIVE, true, NULL);
  s->playhead = host_add_control(h, "cursor", 0, (float)s->capacity, 0, true, NULL);
  bool ok = s->samples && s->command && s->raw && s->frames && s->mode && s->playhead;
  for (unsigned i = 0; ok && i < PEAK_POINTS; i++) {
    snprintf(s->names[i], sizeof(s->names[i]), "p%u", i);
    s->peaks[i] = host_add_control(h, s->names[i], 0, 1, 0, true, NULL);
    ok = s->peaks[i] != NULL;
  }
  s->take_frames = ok ? host_add_control(h, "takeFrames", 0, (float)s->capacity, 0, true, NULL) : NULL;
  // After the peaks, so it is printed after them.
  s->take = s->take_frames ? host_add_control(h, "take", 0, 1e9f, 0, true, NULL) : NULL;
  return ok && s->take;
}
static bool sound_check_activate(Host *h) { return true; }
static void sound_check_deactivate(Host *h) {}
static void sound_check_unload(Host *h) {
  SoundCheck *s = h->impl;
  if (!s) return;
  free(s->samples);
  free(s);
  h->impl = NULL;
}

// A recording ended: publish its length, then count it.
static void finish_take(SoundCheck *s) {
  if (s->length == 0) return;
  atomic_fetch_add(&s->waveform_version, 1);
  unsigned used = (unsigned)(((uint64_t)s->length * PEAK_BINS + s->capacity - 1) / s->capacity);
  if (used == 0) used = 1;
  if (used > PEAK_BINS) used = PEAK_BINS;
  for (unsigned j = 0; j < PEAK_POINTS; j++) {
    unsigned from = j * used / PEAK_POINTS, to = (j + 1) * used / PEAK_POINTS;
    if (to <= from) to = from + 1;
    float peak = 0;
    for (unsigned b = from; b < to; b++)
      peak = fmaxf(peak, s->bins[b]);
    atomic_store_explicit(&s->finished_peaks[j], fminf(peak, 1), memory_order_relaxed);
  }
  atomic_store(&s->take_length, s->length);
  atomic_fetch_add(&s->takes, 1);
  atomic_fetch_add(&s->waveform_version, 1);
}

static void sound_check_process(Host *h, uint32_t frames, float *const *in, float *const *out) {
  SoundCheck *s = h->impl;
  float requested = atomic_load(&s->command->desired);
  int mode = requested == RECORD ? RECORD : requested == LOOP ? LOOP : LIVE;
  if (mode != s->previous) {
    if (s->previous == RECORD) finish_take(s);
    if (mode == RECORD) {
      s->length = 0;
      memset(s->bins, 0, sizeof(s->bins));
    }
    if (mode == LOOP) {
      s->cursor = 0;
      s->fade = s->length / 4 < s->fade_frames ? s->length / 4 : s->fade_frames;
    }
    s->previous = mode;
  }
  if (mode == LOOP && s->length == 0) mode = LIVE;
  float target = atomic_load(&s->raw->desired) >= 0.5f ? 1.0f : 0.0f;
  float *tap = h->taps[0];
  for (uint32_t i = 0; i < frames; i++) {
    float x = in[0][i];
    if (mode == RECORD) {
      unsigned bin = (unsigned)((uint64_t)s->length * PEAK_BINS / s->capacity);
      float level = fabsf(x);
      if (level > s->bins[bin]) s->bins[bin] = level;
      s->samples[s->length++] = x;
      if (s->length == s->capacity) {
        mode = LIVE;
        s->previous = LIVE;
        finish_take(s);
        host_control_moved_rt(h, s->command, LIVE);
      }
    } else if (mode == LOOP) {
      x = s->samples[s->cursor];
      uint32_t tail = s->length - s->fade;
      if (s->cursor >= tail) {
        // The end fading out, the start fading in: a + (b - a) * t keeps a
        // signal both ends share, such as a hum or an offset, at its level.
        uint32_t k = s->cursor - tail;
        float t = (float)(k + 1) / (float)(s->fade + 1);
        x += (s->samples[k] - x) * t;
      }
      if (++s->cursor == s->length) s->cursor = s->fade;
    }
    if (s->raw_gain < target) s->raw_gain = fminf(target, s->raw_gain + s->raw_step);
    else if (s->raw_gain > target) s->raw_gain = fmaxf(target, s->raw_gain - s->raw_step);
    out[0][i] = x * (1 - s->raw_gain);
    if (tap) tap[i] = x * s->raw_gain;
  }
  atomic_store(&s->frames->observed, (float)s->length);
  atomic_store(&s->mode->observed, (float)mode);
  atomic_store(&s->playhead->observed, mode == LOOP ? (float)s->cursor : 0);
}

static void sound_check_tick(Host *h) {
  SoundCheck *s = h->impl;
  uint32_t version = atomic_load(&s->waveform_version);
  uint32_t takes = atomic_load(&s->takes);
  if (takes != s->published && !(version & 1)) {
    float peaks[PEAK_POINTS];
    uint32_t length = atomic_load(&s->take_length);
    for (unsigned j = 0; j < PEAK_POINTS; j++)
      peaks[j] = atomic_load_explicit(&s->finished_peaks[j], memory_order_relaxed);
    // A completion overlapping this read is retried on the next tick. The
    // audio thread never waits for the reader and never allocates here.
    if (version == atomic_load(&s->waveform_version)) {
      for (unsigned j = 0; j < PEAK_POINTS; j++) atomic_store(&s->peaks[j]->observed, peaks[j]);
      atomic_store(&s->take_frames->observed, (float)length);
      s->published = takes;
      atomic_store(&s->take->observed, (float)takes);
    }
  }
  if (atomic_load(&s->command->desired) != RECORD) {
    s->timed_record = false;
    return;
  }
  struct timespec now;
  clock_gettime(CLOCK_MONOTONIC, &now);
  if (!s->timed_record) { s->record_started = now; s->timed_record = true; }
  double elapsed = (double)(now.tv_sec - s->record_started.tv_sec)
      + (now.tv_nsec - s->record_started.tv_nsec) / 1e9;
  if (elapsed >= SOUND_CHECK_SECONDS) host_control_moved(h, s->command, LIVE);
}

// The tap past the chain, one mono port.
static const TapOutput sound_check_taps[] = {{"tap_0", "MONO"}, {NULL, NULL}};

const Backend sound_check_backend = {
  .name = "Sound Check", .argument_count = 0,
  .load = sound_check_load, .activate = sound_check_activate,
  .deactivate = sound_check_deactivate, .process = sound_check_process,
  .main_thread = sound_check_tick, .unload = sound_check_unload,
  .tap_outputs = sound_check_taps,
};
