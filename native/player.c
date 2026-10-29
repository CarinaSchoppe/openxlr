// SPDX-License-Identifier: GPL-3.0-only
// The soundboard player: decoded clips held in memory and a fixed pool of
// voices mixed into one stereo output. The daemon links that output into a
// channel; playing a clip changes nothing in the graph.
//
// Who owns what. A worker thread decodes one clip at a time, so command lines
// keep flowing while a long file loads; it reads files and allocates the
// clip it decodes, and hands it to the main thread, which owns every clip
// from then on and frees it. The audio thread owns which clip each
// slot holds, the voices and the clips a voice still reads after its slot was
// given another. The two talk through two single-producer rings and never
// share a clip otherwise: the main thread hands a clip over with an event and
// gets it back through the return ring once no voice reads it, and only then
// frees it. Nothing on the audio thread allocates, locks, logs or reads a
// file.
//
// Clip files are untrusted. Every size is checked before the memory for it is
// asked for, decoding stops one frame past the length limit, and a file that
// cannot be read costs a `clip SLOT error` line, never the process.
#include "host.h"
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <math.h>
#include <sndfile.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

// The limits. Each is one constant, and the daemon applies the same ones.
enum {
  PLAYER_SLOTS = 64,         // clips the player holds at once
  PLAYER_VOICES = 8,         // clips heard at once; one more steals the oldest
  PLAYER_MAX_SECONDS = 30,   // the longest clip
  PLAYER_MIN_RATE = 8000,    // a clip's own sample rate, as the helper's
  PLAYER_MAX_RATE = 384000,
  PLAYER_MAX_CHANNELS = 2,   // mono or stereo clips
};
#define PLAYER_MAX_FILE_BYTES ((off_t)64 << 20)       // a clip file on disk
#define PLAYER_MAX_DECODED_BYTES ((size_t)96 << 20)   // every slot's samples
// Every start and stop fades over this many seconds, so nothing clicks.
#define PLAYER_RAMP 0.005
// Where a looping clip starts over, its end fades into its start over this
// many seconds, or a quarter of the clip when that is shorter.
#define PLAYER_LOOP_CROSSFADE 0.02
// A press the audio thread reaches later than this is dropped: the node was
// not running, unlinked or waiting for its graph, and a press that late
// would play out of time, with every other one queued behind it.
#define PLAYER_STALE_NS 250000000ull
// A press may carry a number, 1 to this, which the `ended_N` meter reports
// back. The daemon counts its presses and starts over at 1 past this, the
// largest whole number a float meter holds exactly.
#define PLAYER_MAX_PRESS 16777215u

enum { MODE_OVERLAP, MODE_RESTART, MODE_TOGGLE, MODE_HOLD };
// What `play SLOT` does without a mode word.
enum { PLAYER_DEFAULT_MODE = MODE_OVERLAP };
static const char *const mode_words[] = {"overlap", "restart", "toggle", "hold"};

enum {
  // Room for the voices that fade out beside the ones that play.
  PHYSICAL_VOICES = 2 * PLAYER_VOICES,
  EVENT_CAPACITY = 256,
  // Clips alive at once: in a slot, still read by a fading voice, or on their
  // way back to the main thread. The return ring holds this many, so it can
  // never overflow.
  MAX_LIVE = 128,
  READ_BLOCK = 4096,  // frames per read when only counting
};

typedef struct {
  float *samples;      // interleaved, `channels` per frame, at the helper's rate
  uint32_t frames;
  unsigned channels;
  uint32_t fade;       // the loop crossfade
  uint32_t end_ramp;   // the fade at a clip's natural end
  uint32_t decide;     // where a pass settles whether it loops
  size_t bytes;
} Clip;

enum { EVENT_SWAP, EVENT_PLAY, EVENT_RELEASE, EVENT_STOP, EVENT_STOP_ALL };

typedef struct {
  uint8_t type, slot, mode;
  uint32_t press;  // EVENT_PLAY: the press's number, or 0 when it came without one
  Clip *clip;      // EVENT_SWAP: the slot's new clip, or NULL to empty it
  uint64_t at;     // EVENT_PLAY: when the main thread queued it, CLOCK_MONOTONIC
} Event;

static uint64_t monotonic_ns(void) {
  struct timespec now;
  clock_gettime(CLOCK_MONOTONIC, &now);
  return (uint64_t)now.tv_sec * 1000000000ull + (uint64_t)now.tv_nsec;
}

// One load, handed from the main thread to the worker and back.
typedef struct {
  unsigned slot;
  unsigned long token;
  char path[PATH_MAX];
  size_t limit;      // the most bytes of samples the clip may take
  Clip *clip;        // the worker's result, or NULL with error
  char error[256];
  bool cancelled;    // main thread: an unload or a newer load replaced it
} Job;

typedef struct {
  const Clip *clip;  // NULL while the voice is free
  unsigned slot;
  uint32_t cursor;
  float envelope;    // 0 to 1: the start and stop ramps
  float gain;        // follows the slot's gain at the ramp's pace
  bool releasing;    // fading out, then free
  bool held;         // started by a `hold` press: a release ends it
  bool loop_pass;    // this pass wraps instead of ending
  uint32_t press;    // the number of the press that started it, or 0
  uint64_t order;    // when it started, for stealing the oldest
} Voice;

typedef struct {
  // Main thread to audio thread.
  Event events[EVENT_CAPACITY];
  _Atomic uint32_t event_head, event_tail;
  // Audio thread to main thread: clips no voice reads any more.
  Clip *returned[MAX_LIVE];
  _Atomic uint32_t return_head, return_tail;
  // Audio thread only.
  Clip *current[PLAYER_SLOTS];
  Clip *fading[PHYSICAL_VOICES];  // replaced clips a fading voice still reads
  Voice voices[PHYSICAL_VOICES];
  uint32_t pressed[PLAYER_SLOTS];  // the number of the last numbered press each slot took
  uint64_t started;
  float ramp_step;
  // Main thread only.
  uint32_t rate;
  Clip *live[MAX_LIVE];
  unsigned live_count;
  size_t assigned[PLAYER_SLOTS];  // bytes of the clip each slot was last given
  size_t budget;                  // PLAYER_MAX_DECODED_BYTES; a test lowers it
  // The worker. `job` belongs to the main thread while `busy` is false, and
  // to the worker from `ready` until it sets `done`, under `lock`.
  pthread_t worker;
  bool worker_started, busy;
  pthread_mutex_t lock;
  pthread_cond_t wake;
  bool ready, done, quit;
  Job job;
  // Controls and meters.
  Control *gain[PLAYER_SLOTS], *loop[PLAYER_SLOTS];
  Control *playing[PLAYER_SLOTS], *position[PLAYER_SLOTS], *ended[PLAYER_SLOTS];
  char names[PLAYER_SLOTS][5][16];
} Player;

// --- the audio thread -------------------------------------------------------

static void give_back(Player *p, Clip *clip) {
  uint32_t head = atomic_load_explicit(&p->return_head, memory_order_relaxed);
  p->returned[head % MAX_LIVE] = clip;
  atomic_store_explicit(&p->return_head, head + 1, memory_order_release);
}

static bool read_by_a_voice(const Player *p, const Clip *clip) {
  for (unsigned i = 0; i < PHYSICAL_VOICES; i++)
    if (p->voices[i].clip == clip) return true;
  return false;
}

// Hand back every replaced clip that no voice reads any more.
static void sweep_fading(Player *p) {
  for (unsigned i = 0; i < PHYSICAL_VOICES; i++)
    if (p->fading[i] && !read_by_a_voice(p, p->fading[i])) {
      give_back(p, p->fading[i]);
      p->fading[i] = NULL;
    }
}

static void retire(Player *p, Clip *clip) {
  if (!read_by_a_voice(p, clip)) {
    give_back(p, clip);
    return;
  }
  sweep_fading(p);
  for (unsigned i = 0; i < PHYSICAL_VOICES; i++)
    if (!p->fading[i]) {
      p->fading[i] = clip;
      return;
    }
  // A reader of each fading clip is a voice, and there are as many places
  // as voices, so this is never reached; if it were, cut the readers.
  for (unsigned i = 0; i < PHYSICAL_VOICES; i++)
    if (p->voices[i].clip == clip) p->voices[i].clip = NULL;
  give_back(p, clip);
}

static void release_voice(Voice *v) { v->releasing = true; }

static void start_voice(Player *p, unsigned slot, bool held, uint32_t press) {
  const Clip *clip = p->current[slot];
  if (!clip) return;
  unsigned playing = 0;
  Voice *oldest = NULL;
  for (unsigned i = 0; i < PHYSICAL_VOICES; i++) {
    Voice *v = &p->voices[i];
    if (!v->clip || v->releasing) continue;
    playing++;
    if (!oldest || v->order < oldest->order) oldest = v;
  }
  if (playing >= PLAYER_VOICES && oldest) release_voice(oldest);
  Voice *free_voice = NULL, *quietest = NULL;
  for (unsigned i = 0; i < PHYSICAL_VOICES && !free_voice; i++) {
    Voice *v = &p->voices[i];
    if (!v->clip) free_voice = v;
    else if (v->releasing && (!quietest || v->envelope < quietest->envelope))
      quietest = v;
  }
  // Every place taken means more voices fading at once than play: the
  // quietest of them ends a few samples early.
  if (!free_voice) free_voice = quietest;
  if (!free_voice) return;
  *free_voice = (Voice){
      .clip = clip,
      .slot = slot,
      .held = held,
      .press = press,
      .gain = atomic_load(&p->gain[slot]->desired),
      .loop_pass = atomic_load(&p->loop[slot]->desired) >= 0.5f,
      .order = ++p->started,
  };
}

static bool slot_playing(const Player *p, unsigned slot) {
  for (unsigned i = 0; i < PHYSICAL_VOICES; i++) {
    const Voice *v = &p->voices[i];
    if (v->clip && !v->releasing && v->slot == slot) return true;
  }
  return false;
}

static void apply(Player *p, const Event *e, uint64_t now) {
  switch (e->type) {
  case EVENT_SWAP: {
    Clip *old = p->current[e->slot];
    p->current[e->slot] = e->clip;
    if (!old) break;
    for (unsigned i = 0; i < PHYSICAL_VOICES; i++)
      if (p->voices[i].clip == old) release_voice(&p->voices[i]);
    retire(p, old);
    break;
  }
  case EVENT_PLAY:
    // Taken even when it is dropped or starts nothing: a press the slot has
    // taken and that left no hold voice behind has ended, as `ended_N` says.
    if (e->press) p->pressed[e->slot] = e->press;
    if (now - e->at > PLAYER_STALE_NS) break;
    if (e->mode == MODE_RESTART || (e->mode == MODE_TOGGLE && slot_playing(p, e->slot))) {
      for (unsigned i = 0; i < PHYSICAL_VOICES; i++)
        if (p->voices[i].clip && p->voices[i].slot == e->slot) release_voice(&p->voices[i]);
      if (e->mode == MODE_TOGGLE) break;
    }
    start_voice(p, e->slot, e->mode == MODE_HOLD, e->press);
    break;
  case EVENT_RELEASE:
    for (unsigned i = 0; i < PHYSICAL_VOICES; i++) {
      Voice *v = &p->voices[i];
      if (v->clip && v->held && v->slot == e->slot) release_voice(v);
    }
    break;
  case EVENT_STOP:
  case EVENT_STOP_ALL:
    for (unsigned i = 0; i < PHYSICAL_VOICES; i++) {
      Voice *v = &p->voices[i];
      if (v->clip && (e->type == EVENT_STOP_ALL || v->slot == e->slot)) release_voice(v);
    }
    break;
  }
}

static inline void frame_at(const Clip *c, uint32_t at, float *left, float *right) {
  if (c->channels == 1) {
    *left = *right = c->samples[at];
  } else {
    *left = c->samples[2 * at];
    *right = c->samples[2 * at + 1];
  }
}

static void render(Voice *v, float step, float gain, bool loop, uint32_t frames,
                   float *left, float *right) {
  const Clip *c = v->clip;
  for (uint32_t i = 0; i < frames; i++) {
    float target = v->releasing ? 0.0f : 1.0f;
    if (v->envelope < target) v->envelope = fminf(target, v->envelope + step);
    else if (v->envelope > target) v->envelope = fmaxf(target, v->envelope - step);
    if (v->envelope == 0 && v->releasing) {
      v->clip = NULL;
      return;
    }
    if (v->gain < gain) v->gain = fminf(gain, v->gain + step);
    else if (v->gain > gain) v->gain = fmaxf(gain, v->gain - step);
    uint32_t at = v->cursor;
    if (at == c->decide) v->loop_pass = loop;
    float l, r, edge = 1;
    frame_at(c, at, &l, &r);
    if (v->loop_pass) {
      // The end fading out, the start fading in, as Sound Check's loop.
      uint32_t tail = c->frames - c->fade;
      if (c->fade && at >= tail) {
        uint32_t k = at - tail;
        float t = (float)(k + 1) / (float)(c->fade + 1), l0, r0;
        frame_at(c, k, &l0, &r0);
        l += (l0 - l) * t;
        r += (r0 - r) * t;
      }
    } else {
      uint32_t remaining = c->frames - at;
      if (remaining <= c->end_ramp) edge = (float)remaining / (float)(c->end_ramp + 1);
    }
    float amount = v->envelope * v->gain * edge;
    left[i] += l * amount;
    right[i] += r * amount;
    if (++v->cursor == c->frames) {
      if (!v->loop_pass) {
        v->clip = NULL;
        return;
      }
      v->cursor = c->fade;
      v->loop_pass = loop;
    }
  }
}

static void player_process(Host *h, uint32_t frames, float *const *in, float *const *out) {
  Player *p = h->impl;
  uint32_t head = atomic_load_explicit(&p->event_head, memory_order_acquire);
  uint32_t tail = atomic_load_explicit(&p->event_tail, memory_order_relaxed);
  uint64_t now = head != tail ? monotonic_ns() : 0;
  for (; tail != head; tail++) apply(p, &p->events[tail % EVENT_CAPACITY], now);
  atomic_store_explicit(&p->event_tail, tail, memory_order_release);
  memset(out[0], 0, sizeof(float) * frames);
  memset(out[1], 0, sizeof(float) * frames);
  for (unsigned i = 0; i < PHYSICAL_VOICES; i++) {
    Voice *v = &p->voices[i];
    if (!v->clip) continue;
    float gain = atomic_load(&p->gain[v->slot]->desired);
    bool loop = atomic_load(&p->loop[v->slot]->desired) >= 0.5f;
    render(v, p->ramp_step, gain, loop, frames, out[0], out[1]);
  }
  sweep_fading(p);
  // The meters: voices playing each slot, how far its newest one is, and
  // the newest press whose hold voice, and every older one's, has ended.
  // Hold voices of a slot end in the order they started: they play the same
  // clip, a release or a stop ends them all, and a steal takes the oldest.
  // So while one plays, every press before the oldest of them has ended,
  // and once none plays, every press the slot has taken has. Only loop
  // turned off under looping voices can end a newer one first, which this
  // reports late, never early. One value, stored once a block, so a reader
  // never sees a press counted as taken with the voices of an earlier block.
  unsigned counts[PLAYER_SLOTS] = {0};
  const Voice *newest[PLAYER_SLOTS] = {0};
  const Voice *oldest_held[PLAYER_SLOTS] = {0};
  for (unsigned i = 0; i < PHYSICAL_VOICES; i++) {
    const Voice *v = &p->voices[i];
    if (!v->clip || v->releasing) continue;
    counts[v->slot]++;
    if (!newest[v->slot] || v->order > newest[v->slot]->order) newest[v->slot] = v;
    if (v->held && v->press && (!oldest_held[v->slot] || v->order < oldest_held[v->slot]->order))
      oldest_held[v->slot] = v;
  }
  for (unsigned s = 0; s < PLAYER_SLOTS; s++) {
    const Voice *v = newest[s];
    const Voice *held = oldest_held[s];
    atomic_store(&p->playing[s]->observed, (float)counts[s]);
    atomic_store(&p->position[s]->observed,
                 v ? (float)v->cursor / (float)v->clip->frames : 0.0f);
    uint32_t ended = !held ? p->pressed[s] : held->press == 1 ? PLAYER_MAX_PRESS : held->press - 1;
    atomic_store(&p->ended[s]->observed, (float)ended);
  }
}

// --- decoding, on the worker and in probes ----------------------------------

typedef struct {
  int fd;
  SNDFILE *file;
  SF_INFO info;
  uint64_t limit;  // the most frames the clip may have at its own rate
} Source;

// Rounded up to a tenth, so a figure just past a limit never reads as the limit.
static double tenths_up(double value) { return ceil(value * 10) / 10; }

static void close_source(Source *s) {
  if (s->file) sf_close(s->file);
  if (s->fd >= 0) close(s->fd);
  s->file = NULL;
  s->fd = -1;
}

// Open PATH under the limits that can be checked before anything is decoded.
static bool open_source(const char *path, Source *s, char *error, size_t size) {
  *s = (Source){.fd = -1};
  if (!*path) {
    snprintf(error, size, "no file was named");
    return false;
  }
  if (strlen(path) >= PATH_MAX) {
    snprintf(error, size, "the path is too long");
    return false;
  }
  // Not blocking, so a pipe with no writer cannot hold the helper here.
  s->fd = open(path, O_RDONLY | O_CLOEXEC | O_NOCTTY | O_NONBLOCK);
  if (s->fd < 0) {
    snprintf(error, size, errno == ENOENT    ? "the file does not exist"
                          : errno == EACCES ? "the file cannot be read: permission denied"
                                            : "the file cannot be opened");
    return false;
  }
  struct stat st;
  if (fstat(s->fd, &st) != 0 || !S_ISREG(st.st_mode)) {
    snprintf(error, size, "the path is not a regular file");
    goto fail;
  }
  if (st.st_size > PLAYER_MAX_FILE_BYTES) {
    snprintf(error, size, "the file is %.1f MiB; the limit is %d MiB",
             tenths_up((double)st.st_size / (1 << 20)), (int)(PLAYER_MAX_FILE_BYTES >> 20));
    goto fail;
  }
  if (st.st_size == 0) {
    snprintf(error, size, "the file is empty");
    goto fail;
  }
  int flags = fcntl(s->fd, F_GETFL);
  if (flags < 0 || fcntl(s->fd, F_SETFL, flags & ~O_NONBLOCK) != 0) {
    snprintf(error, size, "the file cannot be opened");
    goto fail;
  }
  // The descriptor stays ours: libsndfile reads it and never opens a name.
  s->file = sf_open_fd(s->fd, SFM_READ, &s->info, SF_FALSE);
  if (!s->file) {
    snprintf(error, size, "the file is not audio this build can decode");
    goto fail;
  }
  if (s->info.channels < 1 || s->info.channels > PLAYER_MAX_CHANNELS) {
    snprintf(error, size, "the clip has %d channels; clips are mono or stereo", s->info.channels);
    goto fail;
  }
  if (s->info.samplerate < PLAYER_MIN_RATE || s->info.samplerate > PLAYER_MAX_RATE) {
    snprintf(error, size, "the clip's sample rate of %d Hz is outside %d to %d Hz",
             s->info.samplerate, PLAYER_MIN_RATE, PLAYER_MAX_RATE);
    goto fail;
  }
  s->limit = (uint64_t)PLAYER_MAX_SECONDS * (uint64_t)s->info.samplerate;
  // A length the header gives is refused at once; one it does not give, or
  // gives wrong, is found by decoding no further than one frame past.
  if (s->info.frames > 0 && (uint64_t)s->info.frames > s->limit &&
      s->info.frames != SF_COUNT_MAX) {
    snprintf(error, size, "the clip is %.1f s long; the limit is %d s",
             tenths_up((double)s->info.frames / s->info.samplerate),
             PLAYER_MAX_SECONDS);
    goto fail;
  }
  return true;
fail:
  close_source(s);
  return false;
}

// Decode at most limit + 1 frames: into a buffer that grows within the limit
// when samples is not NULL, through a fixed buffer when only counting.
static bool read_source(Source *s, float **samples, uint64_t *frames, char *error, size_t size) {
  // Used only to count, which only a probe does, on the main thread.
  static float scratch[READ_BLOCK * PLAYER_MAX_CHANNELS];
  unsigned channels = (unsigned)s->info.channels;
  uint64_t count = 0, capacity = 0;
  float *buffer = NULL;
  if (samples) {
    capacity = s->info.frames > 0 && (uint64_t)s->info.frames <= s->limit
                   ? (uint64_t)s->info.frames + 1
                   : (uint64_t)s->info.samplerate;
    buffer = malloc(capacity * channels * sizeof(float));
    if (!buffer) goto memory;
  }
  while (count <= s->limit) {
    float *into = scratch;
    uint64_t want = s->limit + 1 - count;
    if (want > READ_BLOCK) want = READ_BLOCK;
    if (samples) {
      if (count == capacity) {
        uint64_t grown = capacity * 2 > s->limit + 1 ? s->limit + 1 : capacity * 2;
        float *larger = realloc(buffer, grown * channels * sizeof(float));
        if (!larger) goto memory;
        buffer = larger;
        capacity = grown;
      }
      into = buffer + count * channels;
      want = capacity - count;
    }
    sf_count_t got = sf_readf_float(s->file, into, (sf_count_t)want);
    if (got <= 0) break;
    count += (uint64_t)got;
  }
  if (count > s->limit) {
    snprintf(error, size, "the clip is longer than %d s", PLAYER_MAX_SECONDS);
    free(buffer);
    return false;
  }
  if (count == 0) {
    // A decoder error with nothing decoded is a damaged file; what decoded
    // before an error is kept, as a player would play it.
    snprintf(error, size, sf_error(s->file) != SF_ERR_NO_ERROR
                              ? "the file is damaged and could not be decoded"
                              : "the file holds no audio");
    free(buffer);
    return false;
  }
  if (samples) {
    // A float file may hold anything: non-finite samples become silence, and
    // the rest is held to full scale, so no clip can poison the sum.
    for (uint64_t i = 0; i < count * channels; i++) {
      float x = buffer[i];
      buffer[i] = !isfinite(x) ? 0.0f : x > 1 ? 1.0f : x < -1 ? -1.0f : x;
    }
    *samples = buffer;
  }
  *frames = count;
  return true;
memory:
  free(buffer);
  snprintf(error, size, "the player is out of memory");
  return false;
}

// --- resampling -------------------------------------------------------------

// A windowed sinc, tabulated once: SINC_ZEROS zero crossings each side,
// SINC_PHASES points between crossings, read with linear interpolation.
enum { SINC_ZEROS = 16, SINC_PHASES = 512 };
// The cutoff as a fraction of the lower Nyquist frequency, leaving the
// Blackman window's transition band room below it.
#define SINC_ROLLOFF 0.94
#define PLAYER_PI 3.14159265358979323846
static float sinc_table[SINC_ZEROS * SINC_PHASES + 2];

static void fill_sinc(void) {
  for (unsigned i = 0; i < SINC_ZEROS * SINC_PHASES + 2; i++) {
    double x = (double)i / SINC_PHASES;
    if (x >= SINC_ZEROS) {
      sinc_table[i] = 0;
      continue;
    }
    double sinc = i == 0 ? 1 : sin(PLAYER_PI * x) / (PLAYER_PI * x);
    double window = 0.42 + 0.5 * cos(PLAYER_PI * x / SINC_ZEROS) +
                    0.08 * cos(2 * PLAYER_PI * x / SINC_ZEROS);
    sinc_table[i] = (float)(sinc * window);
  }
}

static void build_sinc(void) {
  static pthread_once_t once = PTHREAD_ONCE_INIT;
  pthread_once(&once, fill_sinc);
}

// x in zero crossings
static double sinc_at(double x) {
  x = fabs(x) * SINC_PHASES;
  if (x >= SINC_ZEROS * SINC_PHASES) return 0;
  size_t i = (size_t)x;
  double f = x - (double)i;
  return sinc_table[i] + (sinc_table[i + 1] - sinc_table[i]) * f;
}

static uint64_t resampled_frames(uint64_t frames, uint32_t from, uint32_t to) {
  uint64_t n = from == to ? frames : frames * to / from;
  return n ? n : 1;
}

// Offline, on the worker: out holds resampled_frames(...) frames.
static void resample(const float *in, uint64_t frames, unsigned channels, uint32_t from,
                     uint32_t to, float *out, uint64_t out_frames) {
  build_sinc();
  double cutoff = SINC_ROLLOFF * (to < from ? (double)to / from : 1.0);
  int64_t width = (int64_t)ceil(SINC_ZEROS / cutoff);
  for (uint64_t n = 0; n < out_frames; n++) {
    // Exactly: the output frame n falls at n * from / to source frames.
    uint64_t position = n * from;
    int64_t center = (int64_t)(position / to);
    double fraction = (double)(position % to) / to;
    int64_t first = center - width + 1, last = center + width;
    if (first < 0) first = 0;
    if (last > (int64_t)frames - 1) last = (int64_t)frames - 1;
    double sum[PLAYER_MAX_CHANNELS] = {0};
    for (int64_t k = first; k <= last; k++) {
      double c = cutoff * sinc_at(cutoff * ((double)(k - center) - fraction));
      for (unsigned j = 0; j < channels; j++) sum[j] += c * in[k * channels + j];
    }
    for (unsigned j = 0; j < channels; j++) out[n * channels + j] = (float)sum[j];
  }
}

// --- clips --------------------------------------------------------------------

// The file at PATH at the helper's RATE, in at most BUDGET bytes of samples.
static Clip *decode_clip(const char *path, uint32_t rate, size_t budget, char *error, size_t size) {
  Source s;
  if (!open_source(path, &s, error, size)) return NULL;
  float *samples = NULL;
  uint64_t frames = 0;
  bool ok = read_source(&s, &samples, &frames, error, size);
  unsigned channels = (unsigned)s.info.channels;
  uint32_t from = (uint32_t)s.info.samplerate;
  close_source(&s);
  if (!ok) return NULL;
  uint64_t out_frames = resampled_frames(frames, from, rate);
  size_t bytes = (size_t)out_frames * channels * sizeof(float);
  if (bytes > budget) {
    snprintf(error, size, "the loaded clips would take more than %zu MiB of memory",
             (size_t)PLAYER_MAX_DECODED_BYTES >> 20);
    free(samples);
    return NULL;
  }
  Clip *clip = calloc(1, sizeof(*clip));
  float *final = NULL;
  if (clip && from == rate) {
    // The buffer grew past the clip; give the rest back.
    final = realloc(samples, bytes);
    if (final) samples = NULL;
  } else if (clip) {
    final = malloc(bytes);
  }
  if (!final) {
    free(clip);
    free(samples);
    snprintf(error, size, "the player is out of memory");
    return NULL;
  }
  if (samples) {
    resample(samples, frames, channels, from, rate, final, out_frames);
    free(samples);
  }
  clip->samples = final;
  clip->frames = (uint32_t)out_frames;
  clip->channels = channels;
  clip->bytes = bytes;
  uint32_t quarter = clip->frames / 4;
  uint32_t fade = (uint32_t)(PLAYER_LOOP_CROSSFADE * rate);
  uint32_t ramp = (uint32_t)(PLAYER_RAMP * rate);
  clip->fade = fade < quarter ? fade : quarter;
  clip->end_ramp = ramp < quarter ? ramp : quarter;
  clip->decide = clip->frames - (clip->fade > clip->end_ramp ? clip->fade : clip->end_ramp);
  return clip;
}

static void free_clip(Clip *clip) {
  if (!clip) return;
  free(clip->samples);
  free(clip);
}

static const char *format_word(int format) {
  switch (format & SF_FORMAT_TYPEMASK) {
  case SF_FORMAT_WAV:
  case SF_FORMAT_WAVEX:
  case SF_FORMAT_RF64:
  case SF_FORMAT_W64:
    return "wav";
  case SF_FORMAT_FLAC:
    return "flac";
  case SF_FORMAT_OGG:
    return (format & SF_FORMAT_SUBMASK) == SF_FORMAT_OPUS ? "opus" : "ogg";
  case SF_FORMAT_MPEG:
    return "mp3";
  case SF_FORMAT_AIFF:
    return "aiff";
  default:
    return "other";
  }
}

// One line, `probe ok FORMAT CHANNELS RATE FRAMES` or `probe error MESSAGE`,
// with FRAMES at the clip's own RATE. The whole file is decoded through a
// fixed buffer, so a file that probes well loads, and nothing is kept.
static bool probe(const char *path, FILE *to) {
  char error[256];
  Source s;
  uint64_t frames = 0;
  if (!open_source(path, &s, error, sizeof(error))) {
    fprintf(to, "probe error %s\n", error);
    return false;
  }
  bool ok = read_source(&s, NULL, &frames, error, sizeof(error));
  if (ok)
    fprintf(to, "probe ok %s %d %d %llu\n", format_word(s.info.format), s.info.channels,
            s.info.samplerate, (unsigned long long)frames);
  else
    fprintf(to, "probe error %s\n", error);
  close_source(&s);
  return ok;
}

static bool push_event(Player *p, Event e) {
  uint32_t head = atomic_load_explicit(&p->event_head, memory_order_relaxed);
  uint32_t tail = atomic_load_explicit(&p->event_tail, memory_order_acquire);
  if (head - tail >= EVENT_CAPACITY) return false;
  p->events[head % EVENT_CAPACITY] = e;
  atomic_store_explicit(&p->event_head, head + 1, memory_order_release);
  return true;
}

static void forget(Player *p, Clip *clip) {
  for (unsigned i = 0; i < p->live_count; i++)
    if (p->live[i] == clip) {
      p->live[i] = p->live[--p->live_count];
      break;
    }
  free_clip(clip);
}

// Free what the audio thread gave back.
static void collect(Player *p) {
  uint32_t head = atomic_load_explicit(&p->return_head, memory_order_acquire);
  uint32_t tail = atomic_load_explicit(&p->return_tail, memory_order_relaxed);
  for (; tail != head; tail++) forget(p, p->returned[tail % MAX_LIVE]);
  atomic_store_explicit(&p->return_tail, tail, memory_order_release);
}

// Empty a slot. Its voices fade out and the clip comes back once they have.
static bool empty_slot(Player *p, unsigned slot) {
  if (!push_event(p, (Event){.type = EVENT_SWAP, .slot = (uint8_t)slot})) return false;
  p->assigned[slot] = 0;
  return true;
}

// Every clip in memory, in a slot, still read by a fading voice or on its
// way back: the budget holds them all, not only the ones in slots.
static size_t held_bytes(Player *p) {
  size_t total = 0;
  for (unsigned i = 0; i < p->live_count; i++) total += p->live[i]->bytes;
  return total;
}

static void *worker_main(void *data) {
  Player *p = data;
  pthread_mutex_lock(&p->lock);
  for (;;) {
    while (!p->quit && !p->ready) pthread_cond_wait(&p->wake, &p->lock);
    if (p->quit) break;
    p->ready = false;
    Job *job = &p->job;
    pthread_mutex_unlock(&p->lock);
    Clip *clip = decode_clip(job->path, p->rate, job->limit, job->error, sizeof(job->error));
    pthread_mutex_lock(&p->lock);
    job->clip = clip;
    p->done = true;
  }
  pthread_mutex_unlock(&p->lock);
  return NULL;
}

// Hand the file to the worker. The answer comes from finish_load, on a later
// tick; meanwhile every other line is taken as it arrives.
static void load(Player *p, unsigned slot, unsigned long token, const char *path) {
  collect(p);
  const char *refusal = NULL;
  if (p->busy)
    refusal = "the player is still loading another clip; try again";
  else if (p->live_count == MAX_LIVE)
    refusal = "the player is still releasing earlier clips; try again";
  else if (strlen(path) >= PATH_MAX)
    refusal = "the path is too long";
  if (refusal) {
    printf("clip %u %lu error %s\n", slot, token, refusal);
    return;
  }
  size_t held = held_bytes(p);
  p->job = (Job){.slot = slot, .token = token, .limit = p->budget > held ? p->budget - held : 0};
  memcpy(p->job.path, path, strlen(path) + 1);
  p->busy = true;
  pthread_mutex_lock(&p->lock);
  p->done = false;
  p->ready = true;
  pthread_cond_signal(&p->wake);
  pthread_mutex_unlock(&p->lock);
}

// On the main thread's tick: take the worker's clip into its slot, or say why not.
static void finish_load(Player *p) {
  if (!p->busy) return;
  pthread_mutex_lock(&p->lock);
  bool done = p->done;
  p->done = false;
  pthread_mutex_unlock(&p->lock);
  if (!done) return;
  p->busy = false;
  Job *job = &p->job;
  Clip *clip = job->clip;
  unsigned slot = job->slot;
  if (job->cancelled) {
    free_clip(clip);
    printf("clip %u %lu error the load was cancelled\n", slot, job->token);
    return;
  }
  collect(p);
  if (clip && p->live_count == MAX_LIVE) {
    free_clip(clip);
    clip = NULL;
    snprintf(job->error, sizeof(job->error), "the player is still releasing earlier clips; try again");
  }
  if (clip) {
    p->live[p->live_count++] = clip;
    if (push_event(p, (Event){.type = EVENT_SWAP, .slot = (uint8_t)slot, .clip = clip})) {
      p->assigned[slot] = clip->bytes;
      // The slot's meters are printed again at the next tick, whatever they
      // were, so a reader never keeps a value from the clip it held before.
      p->playing[slot]->reported = NAN;
      p->position[slot]->reported = NAN;
      p->ended[slot]->reported = NAN;
      printf("clip %u %lu ok %u %u\n", slot, job->token, clip->frames, clip->channels);
      return;
    }
    forget(p, clip);
    snprintf(job->error, sizeof(job->error), "the player is busy; try again");
  }
  // A failed load leaves the slot empty, so it never plays an older clip.
  if (p->assigned[slot]) empty_slot(p, slot);
  printf("clip %u %lu error %s\n", slot, job->token, job->error);
}

// A slot number, 0 to PLAYER_SLOTS - 1, then a space or the end.
static bool parse_slot(const char *text, unsigned *slot, const char **rest) {
  unsigned value = 0, digits = 0;
  while (text[digits] >= '0' && text[digits] <= '9') {
    if (digits == 2) return false;
    value = value * 10 + (unsigned)(text[digits++] - '0');
  }
  if (!digits || value >= PLAYER_SLOTS || (text[digits] && text[digits] != ' ')) return false;
  *slot = value;
  *rest = text + digits + (text[digits] == ' ');
  return true;
}

// A load's token, 1 to 10 digits that the answer repeats, then a space.
static bool parse_token(const char *text, unsigned long *token, const char **rest) {
  unsigned long value = 0;
  unsigned digits = 0;
  while (text[digits] >= '0' && text[digits] <= '9') {
    if (digits == 10) return false;
    value = value * 10 + (unsigned long)(text[digits++] - '0');
  }
  if (!digits || text[digits] != ' ' || value > 4294967295ul) return false;
  *token = value;
  *rest = text + digits + 1;
  return true;
}

// A press's number, 1 to PLAYER_MAX_PRESS, and nothing after it.
static bool parse_press(const char *text, uint32_t *press) {
  unsigned long value = 0;
  unsigned digits = 0;
  while (text[digits] >= '0' && text[digits] <= '9') {
    if (digits == 8) return false;
    value = value * 10 + (unsigned long)(text[digits++] - '0');
  }
  if (!digits || text[digits] || value == 0 || value > PLAYER_MAX_PRESS) return false;
  *press = (uint32_t)value;
  return true;
}

static const char *after(const char *line, const char *word) {
  size_t n = strlen(word);
  return strncmp(line, word, n) == 0 && line[n] == ' ' ? line + n + 1 : NULL;
}

static void invalid(const char *line) {
  fprintf(stderr, "note: invalid player command: %.80s\n", line);
}

static bool player_command(Host *h, char *line) {
  Player *p = h->impl;
  const char *rest;
  unsigned slot;
  if ((rest = after(line, "probe"))) {
    probe(rest, stdout);
  } else if ((rest = after(line, "load"))) {
    const char *word, *path;
    unsigned long token;
    if (!parse_slot(rest, &slot, &word) || !parse_token(word, &token, &path) || !*path) invalid(line);
    else load(p, slot, token, path);
  } else if ((rest = after(line, "unload"))) {
    const char *end;
    if (!parse_slot(rest, &slot, &end) || *end) invalid(line);
    else if (!empty_slot(p, slot)) fprintf(stderr, "note: the player is busy; slot %u stays\n", slot);
    else {
      // A load still decoding for this slot is dropped when it comes back.
      if (p->busy && p->job.slot == slot) p->job.cancelled = true;
      printf("clip %u unloaded\n", slot);
    }
  } else if ((rest = after(line, "play"))) {
    const char *word;
    int mode = -1;
    uint32_t press = 0;
    if (parse_slot(rest, &slot, &word)) {
      if (!*word) mode = PLAYER_DEFAULT_MODE;
      for (int m = 0; m < 4 && mode < 0; m++) {
        size_t n = strlen(mode_words[m]);
        if (strncmp(word, mode_words[m], n)) continue;
        if (!word[n] || (word[n] == ' ' && parse_press(word + n + 1, &press))) mode = m;
      }
    }
    if (mode < 0) invalid(line);
    else if (!p->assigned[slot]) fprintf(stderr, "note: slot %u holds no clip\n", slot);
    else if (!push_event(p, (Event){.type = EVENT_PLAY, .slot = (uint8_t)slot, .mode = (uint8_t)mode,
                                    .press = press, .at = monotonic_ns()}))
      fprintf(stderr, "note: the player is busy; a press was dropped\n");
  } else if ((rest = after(line, "release")) || (rest = after(line, "stop"))) {
    bool stop = line[0] == 's';
    const char *end;
    Event e = {.type = stop ? EVENT_STOP : EVENT_RELEASE};
    if (stop && !strcmp(rest, "all")) e.type = EVENT_STOP_ALL;
    else if (!parse_slot(rest, &slot, &end) || *end) {
      invalid(line);
      return true;
    } else e.slot = (uint8_t)slot;
    if (!push_event(p, e)) fprintf(stderr, "note: the player is busy; a stop was dropped\n");
  } else {
    return false;
  }
  return true;
}

// --- the backend ------------------------------------------------------------

static bool player_load(Host *h, char **arguments) {
  if (h->channels != 2) return false;
  Player *p = calloc(1, sizeof(*p));
  if (!p) return false;
  h->impl = p;
  p->rate = h->rate;
  p->budget = PLAYER_MAX_DECODED_BYTES;
  p->ramp_step = (float)(1.0 / (PLAYER_RAMP * h->rate));
  if (pthread_mutex_init(&p->lock, NULL) != 0) return false;
  if (pthread_cond_init(&p->wake, NULL) != 0) return false;
  if (pthread_create(&p->worker, NULL, worker_main, p) != 0) return false;
  p->worker_started = true;
  static const char *const prefixes[] = {"gain_", "loop_", "playing_", "pos_", "ended_"};
  for (unsigned s = 0; s < PLAYER_SLOTS; s++) {
    for (unsigned k = 0; k < 5; k++)
      snprintf(p->names[s][k], sizeof(p->names[s][k]), "%s%u", prefixes[k], s);
    p->gain[s] = host_add_control(h, p->names[s][0], 0, 1, 1, false, NULL);
    p->loop[s] = host_add_control(h, p->names[s][1], 0, 1, 0, false, NULL);
    p->playing[s] = host_add_control(h, p->names[s][2], 0, PLAYER_VOICES, 0, true, NULL);
    p->position[s] = host_add_control(h, p->names[s][3], 0, 1, 0, true, NULL);
    p->ended[s] = host_add_control(h, p->names[s][4], 0, PLAYER_MAX_PRESS, 0, true, NULL);
    if (!p->gain[s] || !p->loop[s] || !p->playing[s] || !p->position[s] || !p->ended[s]) return false;
  }
  return true;
}

static bool player_activate(Host *h) { return true; }
static void player_deactivate(Host *h) {}
static void player_tick(Host *h) {
  Player *p = h->impl;
  finish_load(p);
  collect(p);
}

static void player_unload(Host *h) {
  Player *p = h->impl;
  if (!p) return;
  if (p->worker_started) {
    pthread_mutex_lock(&p->lock);
    p->quit = true;
    pthread_cond_signal(&p->wake);
    bool decoding = p->busy && !p->done;
    pthread_mutex_unlock(&p->lock);
    // A worker still inside a decode may never come back; the process is
    // ending, so it is left with the player it reads rather than waited for.
    if (decoding) {
      pthread_detach(p->worker);
      h->impl = NULL;
      return;
    }
    pthread_join(p->worker, NULL);
    if (p->busy) free_clip(p->job.clip);
  }
  // Audio has stopped: every clip still alive is in this list.
  for (unsigned i = 0; i < p->live_count; i++) free_clip(p->live[i]);
  free(p);
  h->impl = NULL;
}

const Backend player_backend = {
  .name = "Player", .argument_count = 0,
  .load = player_load, .activate = player_activate,
  .deactivate = player_deactivate, .process = player_process,
  .main_thread = player_tick, .unload = player_unload,
  .command = player_command,
};

// --- outside a running player -------------------------------------------------

static bool has_format(int format, int subtype) {
  int count = 0;
  sf_command(NULL, SFC_GET_FORMAT_MAJOR_COUNT, &count, sizeof(count));
  bool major = false;
  for (int i = 0; i < count && !major; i++) {
    SF_FORMAT_INFO info = {.format = i};
    sf_command(NULL, SFC_GET_FORMAT_MAJOR, &info, sizeof(info));
    major = (info.format & SF_FORMAT_TYPEMASK) == format;
  }
  if (!major || !subtype) return major;
  sf_command(NULL, SFC_GET_FORMAT_SUBTYPE_COUNT, &count, sizeof(count));
  for (int i = 0; i < count; i++) {
    SF_FORMAT_INFO info = {.format = i};
    sf_command(NULL, SFC_GET_FORMAT_SUBTYPE, &info, sizeof(info));
    if ((info.format & SF_FORMAT_SUBMASK) == subtype) return true;
  }
  return false;
}

// `openxlr-lv2-host formats`: the advertised formats this libsndfile decodes,
// as one line of JSON.
int player_print_formats(void) {
  const struct { const char *word; int format, subtype; } formats[] = {
      {"wav", SF_FORMAT_WAV, 0},
      {"flac", SF_FORMAT_FLAC, 0},
      {"ogg", SF_FORMAT_OGG, SF_FORMAT_VORBIS},
      {"opus", SF_FORMAT_OGG, SF_FORMAT_OPUS},
      {"mp3", SF_FORMAT_MPEG, 0},
  };
  const char *separator = "";
  printf("{\"formats\":[");
  for (size_t i = 0; i < sizeof(formats) / sizeof(formats[0]); i++)
    if (has_format(formats[i].format, formats[i].subtype)) {
      printf("%s\"%s\"", separator, formats[i].word);
      separator = ",";
    }
  printf("]}\n");
  return fflush(stdout) == 0 ? 0 : 1;
}

// `openxlr-lv2-host probe PATH`: the probe line from a process of its own,
// so a file that crashes the decoder costs nothing that plays.
int player_probe_main(const char *path) {
  bool ok = probe(path, stdout);
  return fflush(stdout) == 0 && ok ? 0 : 1;
}
