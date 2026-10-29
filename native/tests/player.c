// SPDX-License-Identifier: GPL-3.0-only
// The soundboard player driven directly at 48 kHz in 256-frame blocks, with
// no audio server. Clips are written with libsndfile into a private
// directory, loaded through the command lines and heard in the output
// samples. Every process call counts the allocations the whole program
// makes while it runs, and there must be none.
#define main host_program_main
#include "../host.c"
#undef main
#include "../player.c"
#include <assert.h>
#include <dlfcn.h>
#include <sys/stat.h>
#include <sys/wait.h>
#ifndef RTLD_NEXT  // glibc's value; its header shows it only to _GNU_SOURCE
#define RTLD_NEXT ((void *)-1l)
#endif

enum { RATE = 48000, BLOCK = 256, RECORDING = 8 * RATE };
#define PI_D 3.14159265358979323846

// --- allocation counting ------------------------------------------------------

static _Atomic bool counting;
static _Atomic unsigned long allocations;

// The program's allocator, wrapped: calls made while `counting` is set are
// counted. dlsym may allocate before the real functions are known, so those
// first requests come from a static buffer. AddressSanitizer brings its own
// allocator, and under it nothing is counted.
#ifndef __SANITIZE_ADDRESS__
#define COUNTING_ALLOCATIONS 1
static void *(*real_malloc)(size_t);
static void *(*real_calloc)(size_t, size_t);
static void *(*real_realloc)(void *, size_t);
static void (*real_free)(void *);
static _Alignas(16) char bootstrap[1 << 16];
static size_t bootstrap_used;

static void *from_bootstrap(size_t n) {
  n = (n + 15) & ~(size_t)15;
  if (bootstrap_used + n > sizeof(bootstrap)) abort();
  void *p = bootstrap + bootstrap_used;
  bootstrap_used += n;
  return p;
}

static bool in_bootstrap(const void *p) {
  return (const char *)p >= bootstrap && (const char *)p < bootstrap + sizeof(bootstrap);
}

static void resolve(void) {
  static bool resolving;
  if (real_malloc || resolving) return;
  resolving = true;
  *(void **)&real_calloc = dlsym(RTLD_NEXT, "calloc");
  *(void **)&real_realloc = dlsym(RTLD_NEXT, "realloc");
  *(void **)&real_free = dlsym(RTLD_NEXT, "free");
  *(void **)&real_malloc = dlsym(RTLD_NEXT, "malloc");
  resolving = false;
}

void *malloc(size_t n) {
  if (counting) allocations++;
  resolve();
  return real_malloc ? real_malloc(n) : from_bootstrap(n);
}

void *calloc(size_t count, size_t n) {
  if (counting) allocations++;
  resolve();
  return real_calloc ? real_calloc(count, n) : from_bootstrap(count * n);
}

void *realloc(void *p, size_t n) {
  if (counting) allocations++;
  resolve();
  if (in_bootstrap(p)) abort();
  return real_realloc(p, n);
}

void free(void *p) {
  if (counting && p) allocations++;
  if (!p || in_bootstrap(p)) return;
  resolve();
  real_free(p);
}
#endif

// --- the player ---------------------------------------------------------------

static char directory[256];
static float left[RECORDING], right[RECORDING];
static size_t recorded;

static Host *fresh(void) {
  Host *h = calloc(1, sizeof(*h));
  assert(h);
  h->backend = &player_backend;
  h->rate = RATE, h->channels = 2;
  assert(h->backend->load(h, NULL));
  assert(h->backend->activate(h));
  return h;
}

static void release(Host *h) {
  h->backend->deactivate(h);
  h->backend->unload(h);
  assert(h->impl == NULL);
  free(h);
}

// One command line through the host's own dispatch, its stdout returned.
static void send(Host *h, const char *line, char *reply, size_t size) {
  static char copy[16384];
  snprintf(copy, sizeof(copy), "%s", line);
  fflush(stdout);
  int saved = dup(STDOUT_FILENO);
  FILE *capture = tmpfile();
  assert(saved >= 0 && capture);
  dup2(fileno(capture), STDOUT_FILENO);
  command(h, copy);
  fflush(stdout);
  dup2(saved, STDOUT_FILENO);
  close(saved);
  rewind(capture);
  size_t n = fread(reply, 1, size - 1, capture);
  reply[n] = 0;
  fclose(capture);
}

static void expect(Host *h, const char *line, const char *reply) {
  char got[1024];
  send(h, line, got, sizeof(got));
  if (strcmp(got, reply)) {
    fprintf(stderr, "%s: expected \"%s\", got \"%s\"\n", line, reply, got);
    assert(!"unexpected reply");
  }
}

// A refused load: one `clip SLOT error` line holding WORDS.
static void refused(Host *h, unsigned slot, const char *path, const char *words) {
  char line[8192], got[1024], start[32];
  snprintf(line, sizeof(line), "load %u %s", slot, path);
  send(h, line, got, sizeof(got));
  snprintf(start, sizeof(start), "clip %u error ", slot);
  if (strncmp(got, start, strlen(start)) || !strstr(got, words) || strchr(got, '\n') != got + strlen(got) - 1) {
    fprintf(stderr, "%s: expected an error with \"%s\", got \"%s\"\n", line, words, got);
    assert(!"unexpected reply");
  }
}

static void quiet(Host *h, const char *line) {
  char got[256];
  send(h, line, got, sizeof(got));
  assert(got[0] == 0);
}

// Run COUNT frames in blocks, appended to the recording.
static void run(Host *h, size_t count) {
  static float silence[BLOCK];
  if (recorded + count > RECORDING) recorded = 0;
  for (size_t done = 0; done < count; done += BLOCK) {
    uint32_t frames = (uint32_t)(count - done < BLOCK ? count - done : BLOCK);
    float *in[] = {silence, silence};
    float *out[] = {left + recorded, right + recorded};
    counting = true;
    h->backend->process(h, frames, in, out);
    counting = false;
    for (uint32_t i = 0; i < frames; i++) assert(isfinite(out[0][i]) && isfinite(out[1][i]));
    recorded += frames;
  }
  assert(allocations == 0);
}

static float *last(size_t count) { return left + recorded - count; }

static float largest_step(const float *x, size_t count, float before) {
  float step = 0;
  for (size_t i = 0; i < count; i++) {
    step = fmaxf(step, fabsf(x[i] - before));
    before = x[i];
  }
  return step;
}

static float meter(Host *h, const char *symbol) {
  for (uint32_t i = 0; i < h->control_count; i++)
    if (!strcmp(h->controls[i].symbol, symbol)) return atomic_load(&h->controls[i].observed);
  assert(!"no such meter");
  return 0;
}

// --- clip files ---------------------------------------------------------------

typedef double (*Signal)(size_t n, unsigned channel, unsigned rate);

static void write_clip(const char *path, int format, unsigned rate, unsigned channels,
                       size_t frames, Signal signal) {
  SF_INFO info = {.samplerate = (int)rate, .channels = (int)channels, .format = format};
  SNDFILE *file = sf_open(path, SFM_WRITE, &info);
  if (!file) {
    fprintf(stderr, "%s: %s\n", path, sf_strerror(NULL));
    abort();
  }
  float frame[8];
  for (size_t n = 0; n < frames; n++) {
    for (unsigned c = 0; c < channels; c++) frame[c] = (float)signal(n, c, rate);
    assert(sf_writef_float(file, frame, 1) == 1);
  }
  assert(sf_close(file) == 0);
}

static const char *named(const char *name) {
  static char paths[8][512];
  static unsigned next;
  char *path = paths[next++ % 8];
  snprintf(path, sizeof(paths[0]), "%s/%s", directory, name);
  return path;
}

static double dc(size_t n, unsigned channel, unsigned rate) { return 0.1; }
static double tone(size_t n, unsigned channel, unsigned rate) {
  return channel ? 0 : 0.5 * sin(2 * PI_D * 1000 * (double)n / rate);
}
static double stereo_ramp(size_t n, unsigned channel, unsigned rate) {
  return channel ? -0.25 : 0.5 * (double)(n % 1000) / 1000;
}
static double saw(size_t n, unsigned channel, unsigned rate) { return 0.5 * (double)n / 4800; }
static double silence(size_t n, unsigned channel, unsigned rate) { return 0; }
static double nonfinite(size_t n, unsigned channel, unsigned rate) {
  const double values[] = {NAN, INFINITY, -INFINITY, 2.0, -3.0, 0.5};
  return values[n % 6];
}

static void write_text(const char *path, const char *text) {
  FILE *f = fopen(path, "w");
  assert(f && fputs(text, f) >= 0 && fclose(f) == 0);
}

// --- measuring a tone ---------------------------------------------------------

typedef struct { double level_db, frequency; } Tone;

// The RMS level in dB of a sine's peak and its frequency from the first and
// last upward zero crossings, interpolated.
static Tone measure(const float *x, size_t count, size_t stride, unsigned rate) {
  double sum = 0, first = -1, latest = -1;
  unsigned crossings = 0;
  for (size_t i = 0; i < count; i++) {
    double a = x[i * stride];
    sum += a * a;
    if (i && x[(i - 1) * stride] < 0 && a >= 0) {
      double b = x[(i - 1) * stride];
      double at = (double)(i - 1) + b / (b - a);
      if (first < 0) first = at;
      latest = at;
      crossings++;
    }
  }
  assert(crossings > 2);
  double rms = sqrt(sum / count);
  return (Tone){20 * log10(rms * sqrt(2) / 0.5), (crossings - 1) / ((latest - first) / rate)};
}

// --- checks -------------------------------------------------------------------

static void check_backend(void) {
#ifdef COUNTING_ALLOCATIONS
  // The counter sees an allocation when there is one.
  counting = true;
  void *volatile block = malloc(16);
  counting = false;
  assert(block && allocations == 1);
  free(block);
  allocations = 0;
#else
  puts("skip: allocations are not counted under AddressSanitizer");
#endif
  assert(backend_named("player") == &player_backend);
  assert(player_backend.command == player_command && player_backend.tap_outputs == 0 &&
         player_backend.key_inputs == 0);
  // The limits the plan names, each one constant.
  assert(PLAYER_SLOTS == 64 && PLAYER_VOICES == 8 && PLAYER_MAX_SECONDS == 30);
  assert(PLAYER_MAX_FILE_BYTES == 64 * 1024 * 1024);
  assert(PLAYER_MAX_DECODED_BYTES == 96 * 1024 * 1024);
  assert((int)PLAYER_DEFAULT_MODE == (int)MODE_OVERLAP);
  Host *mono = calloc(1, sizeof(*mono));
  mono->rate = RATE, mono->channels = 1;
  assert(!player_load(mono, NULL) && mono->impl == NULL);
  free(mono);
  Host *h = fresh();
  assert(h->control_count == 4 * PLAYER_SLOTS);
  const struct { const char *name; float minimum, maximum, initial; bool output; } controls[] = {
      {"gain_0", 0, 1, 1, false}, {"loop_0", 0, 1, 0, false}, {"playing_0", 0, 8, 0, true},
      {"pos_0", 0, 1, 0, true},   {"gain_63", 0, 1, 1, false}, {"pos_63", 0, 1, 0, true},
  };
  for (size_t i = 0; i < sizeof(controls) / sizeof(controls[0]); i++) {
    const Control *found = NULL;
    for (uint32_t c = 0; c < h->control_count; c++)
      if (!strcmp(h->controls[c].symbol, controls[i].name)) found = &h->controls[c];
    assert(found && found->minimum == controls[i].minimum && found->maximum == controls[i].maximum);
    assert(atomic_load(&found->desired) == controls[i].initial && found->output == controls[i].output);
  }
  assert(set_control(h, "gain_7", 2) && atomic_load(&((Player *)h->impl)->gain[7]->desired) == 1);
  assert(!set_control(h, "playing_7", 1) && !set_control(h, "gain_64", 1));
  // Lines the player does not know stay unknown, and `set` still reaches it.
  quiet(h, "set loop_3 1");
  assert(atomic_load(&((Player *)h->impl)->loop[3]->desired) == 1);
  quiet(h, "louder 3");
  release(h);
  puts("PASS: the player backend, its controls and its limits");
}

static void check_resampler(void) {
  static float in[2 * 96000], out[2 * 96000];
  // A 1 kHz sine from 44.1 kHz to 48 kHz and back down, and from 96 kHz,
  // keeps its level within 0.1 dB and its frequency within 1 Hz.
  const uint32_t pairs[][2] = {{44100, 48000}, {48000, 44100}, {96000, 48000}, {22050, 48000}};
  for (size_t p = 0; p < sizeof(pairs) / sizeof(pairs[0]); p++) {
    uint32_t from = pairs[p][0], to = pairs[p][1];
    for (uint32_t n = 0; n < from; n++) {
      in[2 * n] = (float)tone(n, 0, from);
      in[2 * n + 1] = 0;
    }
    uint64_t frames = resampled_frames(from, from, to);
    assert(frames == to);
    resample(in, from, 2, from, to, out, frames);
    Tone t = measure(out + 2 * 1000, frames - 2000, 2, to);
    if (fabs(t.level_db) > 0.1 || fabs(t.frequency - 1000) > 1) {
      fprintf(stderr, "%u to %u: %.4f dB, %.4f Hz\n", from, to, t.level_db, t.frequency);
      assert(!"the resampler moved the tone");
    }
    for (uint64_t n = 0; n < frames; n++) assert(out[2 * n + 1] == 0);
  }
  // A 40 kHz tone at 96 kHz would fold to 8 kHz at 48 kHz; it is filtered
  // out instead, more than 60 dB down.
  for (uint32_t n = 0; n < 96000; n++) in[n] = (float)(0.5 * sin(2 * PI_D * 40000 * n / 96000.0));
  resample(in, 96000, 1, 96000, 48000, out, 48000);
  double peak = 0;
  for (uint32_t n = 1000; n < 47000; n++) peak = fmax(peak, fabs(out[n]));
  assert(20 * log10(peak / 0.5) < -60);
  puts("PASS: the resampler keeps a 1 kHz tone's level and frequency and filters what would alias");
}

static void check_decoding(const char *helper) {
  Host *h = fresh();
  Player *p = h->impl;
  char line[1024], got[1024];
  // A float WAV at the helper's rate arrives sample for sample.
  const char *wav = named("stereo.wav");
  write_clip(wav, SF_FORMAT_WAV | SF_FORMAT_FLOAT, RATE, 2, 4800, stereo_ramp);
  snprintf(line, sizeof(line), "load 0 %s", wav);
  expect(h, line, "clip 0 ok 4800 2\n");
  run(h, BLOCK);
  const Clip *c = p->current[0];
  assert(c && c->channels == 2 && c->frames == 4800);
  for (size_t n = 0; n < 4800; n++)
    assert(c->samples[2 * n] == (float)stereo_ramp(n, 0, RATE) && c->samples[2 * n + 1] == -0.25f);
  // A 16 bit mono WAV, and a path with a space in it.
  const char *spaced = named("a mono clip.wav");
  write_clip(spaced, SF_FORMAT_WAV | SF_FORMAT_PCM_16, RATE, 1, 1000, dc);
  snprintf(line, sizeof(line), "load 1 %s", spaced);
  expect(h, line, "clip 1 ok 1000 1\n");
  // A FLAC at 44.1 kHz comes in at the helper's rate with its tone intact.
  const char *flac = named("tone.flac");
  write_clip(flac, SF_FORMAT_FLAC | SF_FORMAT_PCM_24, 44100, 1, 44100, tone);
  snprintf(line, sizeof(line), "load 2 %s", flac);
  expect(h, line, "clip 2 ok 48000 1\n");
  run(h, BLOCK);
  Tone t = measure(p->current[2]->samples + 1000, 46000, 1, RATE);
  assert(fabs(t.level_db) < 0.1 && fabs(t.frequency - 1000) < 1);
  // Non-finite samples become silence and the rest is held to full scale.
  const char *wild = named("nonfinite.wav");
  write_clip(wild, SF_FORMAT_WAV | SF_FORMAT_FLOAT, RATE, 1, 6, nonfinite);
  snprintf(line, sizeof(line), "load 3 %s", wild);
  expect(h, line, "clip 3 ok 6 1\n");
  run(h, BLOCK);
  const float sane[] = {0, 0, 0, 1, -1, 0.5f};
  assert(memcmp(p->current[3]->samples, sane, sizeof(sane)) == 0);
  // probe, through the pipe and from a process of its own.
  snprintf(line, sizeof(line), "probe %s", flac);
  expect(h, line, "probe ok flac 1 44100 44100\n");
  snprintf(line, sizeof(line), "probe %s", wav);
  expect(h, line, "probe ok wav 2 48000 4800\n");
  assert(p->live_count == 4);  // probing kept nothing
  const char *text = named("notes.txt");
  write_text(text, "not a sound\n");
  snprintf(line, sizeof(line), "'%s' probe '%s'", helper, flac);
  FILE *pipe = popen(line, "r");
  assert(pipe && fgets(got, sizeof(got), pipe) && pclose(pipe) == 0);
  assert(!strcmp(got, "probe ok flac 1 44100 44100\n"));
  snprintf(line, sizeof(line), "'%s' probe '%s'", helper, text);
  pipe = popen(line, "r");
  assert(pipe && fgets(got, sizeof(got), pipe) && WEXITSTATUS(pclose(pipe)) == 1);
  assert(!strcmp(got, "probe error the file is not audio this build can decode\n"));
  // formats: what this libsndfile decodes, wav and flac always.
  snprintf(line, sizeof(line), "'%s' formats", helper);
  pipe = popen(line, "r");
  assert(pipe && fgets(got, sizeof(got), pipe) && pclose(pipe) == 0);
  printf("formats: %s", got);
  assert(!strncmp(got, "{\"formats\":[\"wav\",\"flac\"", 24));
  // The compressed formats this libsndfile also writes load and probe too.
  const struct { const char *word, *file; int format; } compressed[] = {
      {"ogg", "clip.ogg", SF_FORMAT_OGG | SF_FORMAT_VORBIS},
      {"opus", "clip.opus", SF_FORMAT_OGG | SF_FORMAT_OPUS},
      {"mp3", "clip.mp3", SF_FORMAT_MPEG | SF_FORMAT_MPEG_LAYER_III},
  };
  for (size_t i = 0; i < sizeof(compressed) / sizeof(compressed[0]); i++) {
    char quoted[16];
    snprintf(quoted, sizeof(quoted), "\"%s\"", compressed[i].word);
    bool listed = strstr(got, quoted) != NULL;
    SF_INFO info = {.samplerate = RATE, .channels = 1, .format = compressed[i].format};
    if (!sf_format_check(&info)) {
      assert(!listed || !strcmp(compressed[i].word, "mp3"));  // MP3 can decode without an encoder
      printf("skip: this libsndfile writes no %s\n", compressed[i].word);
      continue;
    }
    assert(listed);
    const char *path = named(compressed[i].file);
    write_clip(path, compressed[i].format, RATE, 1, RATE, tone);
    char reply[256], expected[64];
    snprintf(line, sizeof(line), "probe %s", path);
    send(h, line, reply, sizeof(reply));
    snprintf(expected, sizeof(expected), "probe ok %s 1 48000 ", compressed[i].word);
    assert(!strncmp(reply, expected, strlen(expected)));
    snprintf(line, sizeof(line), "load 4 %s", path);
    send(h, line, reply, sizeof(reply));
    unsigned frames = 0, channels = 0;
    assert(sscanf(reply, "clip 4 ok %u %u", &frames, &channels) == 2 && channels == 1);
    // Encoders pad; the length is near a second.
    assert(frames >= RATE * 95 / 100 && frames <= RATE * 11 / 10);
    printf("%s: %u frames\n", compressed[i].word, frames);
  }
  release(h);
  puts("PASS: WAV and FLAC decode, resample and sanitize; probe and formats answer");
}

static void check_refusals(void) {
  Host *h = fresh();
  Player *p = h->impl;
  char path[8192];
  refused(h, 0, named("missing.wav"), "does not exist");
  refused(h, 0, directory, "not a regular file");
  const char *fifo = named("fifo.wav");
  assert(mkfifo(fifo, 0600) == 0);
  refused(h, 0, fifo, "not a regular file");
  const char *empty = named("empty.wav");
  write_text(empty, "");
  refused(h, 0, empty, "the file is empty");
  const char *text = named("text.wav");
  write_text(text, "RIFF but not really a wave file at all\n");
  refused(h, 0, text, "not audio this build can decode");
  // One byte over the file limit is refused before a byte is decoded.
  const char *large = named("large.wav");
  int fd = open(large, O_WRONLY | O_CREAT | O_TRUNC, 0600);
  assert(fd >= 0 && ftruncate(fd, PLAYER_MAX_FILE_BYTES + 1) == 0 && close(fd) == 0);
  refused(h, 0, large, "the file is 64.1 MiB; the limit is 64 MiB");
  // Thirty seconds play, one more frame does not.
  const char *thirty = named("thirty.wav");
  write_clip(thirty, SF_FORMAT_WAV | SF_FORMAT_PCM_16, 8000, 1, 30 * 8000, silence);
  snprintf(path, sizeof(path), "load 5 %s", thirty);
  expect(h, path, "clip 5 ok 1440000 1\n");
  const char *longer = named("longer.wav");
  write_clip(longer, SF_FORMAT_WAV | SF_FORMAT_PCM_16, 8000, 1, 30 * 8000 + 1, silence);
  refused(h, 0, longer, "the clip is 30.1 s long; the limit is 30 s");
  // A length no header gives is found by decoding one frame past the limit.
  Source s;
  char error[256];
  float *samples = NULL;
  uint64_t frames = 0;
  assert(open_source(thirty, &s, error, sizeof(error)));
  s.limit = 1000;
  assert(!read_source(&s, &samples, &frames, error, sizeof(error)) && samples == NULL);
  assert(!strcmp(error, "the clip is longer than 30 s"));
  close_source(&s);
  const char *surround = named("surround.wav");
  write_clip(surround, SF_FORMAT_WAV | SF_FORMAT_PCM_16, RATE, 3, 100, silence);
  refused(h, 0, surround, "the clip has 3 channels; clips are mono or stereo");
  const char *slow = named("slow.wav");
  write_clip(slow, SF_FORMAT_WAV | SF_FORMAT_PCM_16, 4000, 1, 100, silence);
  refused(h, 0, slow, "sample rate of 4000 Hz is outside 8000 to 384000 Hz");
  memset(path, 'a', 5000);
  path[0] = '/', path[5000] = 0;
  refused(h, 0, path, "the path is too long");
  // The memory budget counts every slot but the one being loaded.
  const char *two = named("two.wav");  // 2 s of stereo: 750 KiB of samples
  write_clip(two, SF_FORMAT_WAV | SF_FORMAT_PCM_16, RATE, 2, 2 * RATE, dc);
  expect(h, "unload 5", "clip 5 unloaded\n");
  p->budget = 1 << 20;
  snprintf(path, sizeof(path), "load 6 %s", two);
  expect(h, path, "clip 6 ok 96000 2\n");
  refused(h, 7, two, "would take more than 96 MiB of memory");
  expect(h, path, "clip 6 ok 96000 2\n");  // replacing it fits
  p->budget = PLAYER_MAX_DECODED_BYTES;
  // A failed load empties its slot, so it cannot play the clip it had.
  refused(h, 6, text, "not audio");
  quiet(h, "play 6 overlap");
  run(h, BLOCK);
  assert(p->current[6] == NULL);
  player_tick(h);
  assert(p->live_count == 0);
  // Lines that are not commands of the player reply nothing on stdout.
  const char *bad[] = {"load 64 /x", "load -1 /x", "load 1", "load 1 ", "load 007 /x",
                       "play 3 sideways", "play abc", "play", "stop 999", "stop",
                       "stop every", "unload 3 extra", "release", "release x", "probe"};
  for (size_t i = 0; i < sizeof(bad) / sizeof(bad[0]); i++) quiet(h, bad[i]);
  release(h);
  puts("PASS: missing, special, empty, foreign, oversize, overlong, surround and slow files, "
       "the memory budget and malformed lines are refused in a sentence");
}

// Damaged copies of real clips: every load answers with one line and leaves
// the player sound.
static void check_damaged(void) {
  Host *h = fresh();
  Player *p = h->impl;
  const struct { const char *name; int format; } sources[] = {
      {"source.wav", SF_FORMAT_WAV | SF_FORMAT_PCM_16},
      {"source.flac", SF_FORMAT_FLAC | SF_FORMAT_PCM_16},
      {"source.ogg", SF_FORMAT_OGG | SF_FORMAT_VORBIS},
      {"source.mp3", SF_FORMAT_MPEG | SF_FORMAT_MPEG_LAYER_III},
  };
  static unsigned char original[1 << 20], copy[1 << 20];
  unsigned seed = 12345, loaded = 0, refusals = 0;
  for (size_t f = 0; f < sizeof(sources) / sizeof(sources[0]); f++) {
    SF_INFO info = {.samplerate = 44100, .channels = 2, .format = sources[f].format};
    if (!sf_format_check(&info)) continue;
    const char *path = named(sources[f].name);
    write_clip(path, sources[f].format, 44100, 2, 22050, tone);
    FILE *in = fopen(path, "rb");
    size_t size = fread(original, 1, sizeof(original), in);
    fclose(in);
    assert(size > 64 && size < sizeof(original));
    const char *damaged = named("damaged");
    for (unsigned round = 0; round < 150; round++) {
      memcpy(copy, original, size);
      size_t length = size;
      seed = seed * 1103515245 + 12345;
      if (round % 3 == 0) length = 1 + (seed >> 8) % size;  // cut short
      unsigned flips = 1 + round % 16;
      for (unsigned k = 0; k < flips; k++) {
        seed = seed * 1103515245 + 12345;
        // Mostly in the header, where lengths and counts live.
        size_t at = (seed >> 8) % (k % 2 ? length : (length < 256 ? length : 256));
        copy[at] ^= (unsigned char)(1 + (seed >> 20) % 255);
      }
      FILE *out = fopen(damaged, "wb");
      assert(fwrite(copy, 1, length, out) == length && fclose(out) == 0);
      char line[1024], got[1024];
      unsigned slot = round % 4;
      snprintf(line, sizeof(line), "load %u %s", slot, damaged);
      send(h, line, got, sizeof(got));
      char ok[32], failed[32];
      snprintf(ok, sizeof(ok), "clip %u ok ", slot);
      snprintf(failed, sizeof(failed), "clip %u error ", slot);
      assert(strchr(got, '\n') == got + strlen(got) - 1);
      if (!strncmp(got, ok, strlen(ok))) loaded++;
      else assert(!strncmp(got, failed, strlen(failed))), refusals++;
      snprintf(line, sizeof(line), "play %u", slot);
      send(h, line, got, sizeof(got));
      run(h, 4 * BLOCK);
      player_tick(h);
    }
  }
  quiet(h, "stop all");
  run(h, 4 * BLOCK);
  player_tick(h);
  assert(p->live_count <= 4);
  printf("damaged clips: %u loaded, %u refused\n", loaded, refusals);
  assert(loaded > 0 && refusals > 0);
  release(h);
  puts("PASS: damaged and truncated files load or are refused, and the output stays finite");
}

// A clip of constant 0.1 on both channels, a second long, in slot 0.
static Host *dc_board(const char *name, size_t frames) {
  Host *h = fresh();
  const char *path = named(name);
  write_clip(path, SF_FORMAT_WAV | SF_FORMAT_FLOAT, RATE, 2, frames, dc);
  char line[1024], expected[64];
  snprintf(line, sizeof(line), "load 0 %s", path);
  snprintf(expected, sizeof(expected), "clip 0 ok %zu 2\n", frames);
  expect(h, line, expected);
  return h;
}

static void check_presses(void) {
  Host *h = dc_board("dc.wav", RATE);
  const unsigned ramp = (unsigned)(PLAYER_RAMP * RATE);  // 240 frames
  const float bound = 0.1f / ramp * 1.01f;
  // A start ramps in over 5 ms, from the next block.
  quiet(h, "play 0 overlap");
  run(h, BLOCK);
  float *x = last(BLOCK);
  assert(x[0] > 0 && x[0] < 0.001f && largest_step(x, BLOCK, 0) <= bound);
  assert(fabsf(x[ramp] - 0.1f) < 1e-6f && fabsf(right[recorded - 1] - 0.1f) < 1e-6f);
  assert(meter(h, "playing_0") == 1);
  // Overlap adds a voice.
  quiet(h, "play 0");
  run(h, 2 * BLOCK);
  x = last(2 * BLOCK);
  assert(largest_step(x, 2 * BLOCK, 0.1f) <= bound && fabsf(x[2 * BLOCK - 1] - 0.2f) < 1e-6f);
  assert(meter(h, "playing_0") == 2);
  // Stop fades both out within the ramp.
  quiet(h, "stop 0");
  run(h, 2 * BLOCK);
  x = last(2 * BLOCK);
  assert(largest_step(x, 2 * BLOCK, 0.2f) <= 2 * bound && x[ramp] == 0 && x[2 * BLOCK - 1] == 0);
  assert(meter(h, "playing_0") == 0);
  // Restart: the old voice fades out under the new one fading in, so the
  // sum holds, and one voice is left.
  quiet(h, "play 0 overlap");
  run(h, 4 * BLOCK);
  quiet(h, "play 0 restart");
  run(h, 4 * BLOCK);
  x = last(4 * BLOCK);
  for (unsigned i = 0; i < 4 * BLOCK; i++) assert(fabsf(x[i] - 0.1f) < 1e-5f);
  assert(meter(h, "playing_0") == 1);
  // Toggle stops what plays, and starts again on the next press.
  quiet(h, "play 0 toggle");
  run(h, 2 * BLOCK);
  x = last(2 * BLOCK);
  assert(largest_step(x, 2 * BLOCK, 0.1f) <= bound && x[2 * BLOCK - 1] == 0);
  assert(meter(h, "playing_0") == 0);
  quiet(h, "play 0 toggle");
  run(h, 2 * BLOCK);
  assert(fabsf(left[recorded - 1] - 0.1f) < 1e-6f && meter(h, "playing_0") == 1);
  // Hold ends on release; an overlapping voice of the same clip plays on.
  quiet(h, "play 0 hold");
  run(h, 2 * BLOCK);
  assert(fabsf(left[recorded - 1] - 0.2f) < 1e-6f);
  quiet(h, "release 0");
  run(h, 2 * BLOCK);
  x = last(2 * BLOCK);
  assert(largest_step(x, 2 * BLOCK, 0.2f) <= bound && fabsf(x[2 * BLOCK - 1] - 0.1f) < 1e-6f);
  assert(meter(h, "playing_0") == 1);
  // A release that comes before its own press in the same block still ends it.
  quiet(h, "stop all");
  run(h, 2 * BLOCK);
  quiet(h, "play 0 hold");
  quiet(h, "release 0");
  run(h, 2 * BLOCK);
  x = last(2 * BLOCK);
  for (unsigned i = 0; i < 2 * BLOCK; i++) assert(x[i] == 0);
  // Gain follows at the ramp's pace on a running voice and applies to new ones.
  quiet(h, "play 0");
  run(h, 2 * BLOCK);
  quiet(h, "set gain_0 0.5");
  run(h, 2 * BLOCK);
  x = last(2 * BLOCK);
  assert(largest_step(x, 2 * BLOCK, 0.1f) <= bound && fabsf(x[2 * BLOCK - 1] - 0.05f) < 1e-6f);
  quiet(h, "play 0");
  run(h, 2 * BLOCK);
  assert(fabsf(left[recorded - 1] - 0.1f) < 1e-6f);
  quiet(h, "set gain_0 1");
  // Position: the newest voice, from 0 to 1.
  quiet(h, "stop all");
  run(h, 2 * BLOCK);
  quiet(h, "play 0");
  run(h, RATE / 4);
  assert(fabsf(meter(h, "pos_0") - 0.25f) < 0.01f);
  quiet(h, "play 0");
  run(h, BLOCK);
  assert(meter(h, "pos_0") < 0.01f && meter(h, "playing_0") == 2);
  quiet(h, "stop all");
  run(h, 2 * BLOCK);
  assert(meter(h, "pos_0") == 0);
  release(h);
  puts("PASS: overlap, restart, toggle and hold, release, stop, gain and position ramp without a jump");
}

static void check_voice_limit(void) {
  Host *h = dc_board("dc.wav", RATE);
  Player *p = h->impl;
  const float bound = 0.1f / (PLAYER_RAMP * RATE) * 1.01f;
  for (unsigned i = 0; i < PLAYER_VOICES; i++) quiet(h, "play 0 overlap");
  run(h, 2 * BLOCK);
  assert(fabsf(left[recorded - 1] - 0.8f) < 1e-5f && meter(h, "playing_0") == 8);
  // The ninth press steals the oldest voice, which fades under the new one.
  quiet(h, "play 0 overlap");
  run(h, 2 * BLOCK);
  float *x = last(2 * BLOCK);
  for (unsigned i = 0; i < 2 * BLOCK; i++) assert(fabsf(x[i] - 0.8f) < 1e-5f);
  assert(meter(h, "playing_0") == 8);
  // Stealing crosses slots: the oldest voice goes, whichever clip it plays.
  char line[1024];
  snprintf(line, sizeof(line), "load 1 %s", named("dc.wav"));
  expect(h, line, "clip 1 ok 48000 2\n");
  quiet(h, "play 1");
  run(h, 2 * BLOCK);
  assert(meter(h, "playing_0") == 7 && meter(h, "playing_1") == 1);
  // Twenty presses in one block: never more than eight voices heard.
  for (unsigned i = 0; i < 20; i++) quiet(h, "play 0");
  run(h, 4 * BLOCK);
  x = last(4 * BLOCK);
  for (unsigned i = 0; i < 4 * BLOCK; i++) assert(x[i] <= 0.8f + 1e-5f);
  assert(meter(h, "playing_0") + meter(h, "playing_1") == 8);
  unsigned busy = 0;
  for (unsigned i = 0; i < PHYSICAL_VOICES; i++) busy += p->voices[i].clip != NULL;
  assert(busy == 8);
  // Stop all fades every voice of every slot.
  quiet(h, "stop all");
  run(h, 2 * BLOCK);
  x = last(2 * BLOCK);
  assert(largest_step(x, 2 * BLOCK, 0.8f) <= 8 * bound && x[2 * BLOCK - 1] == 0);
  assert(meter(h, "playing_0") == 0 && meter(h, "playing_1") == 0);
  release(h);
  puts("PASS: eight voices at once, the ninth steals the oldest with its fade, stop all");
}

static void check_ends_and_loops(void) {
  // A clip that ends by itself fades over its last 5 ms.
  Host *h = dc_board("short.wav", 1000);
  const unsigned ramp = (unsigned)(PLAYER_RAMP * RATE);
  quiet(h, "play 0");
  run(h, 6 * BLOCK);
  float *x = last(6 * BLOCK);
  assert(largest_step(x, 6 * BLOCK, 0) <= 0.1f / ramp * 1.01f);
  assert(x[999] > 0 && x[999] < 0.001f && x[1000] == 0 && x[6 * BLOCK - 1] == 0);
  assert(meter(h, "playing_0") == 0);
  release(h);
  // A looping clip whose end is far from its start wraps without a step;
  // without the crossfade it would jump by 0.5.
  h = fresh();
  Player *p = h->impl;
  const char *path = named("saw.wav");
  write_clip(path, SF_FORMAT_WAV | SF_FORMAT_FLOAT, RATE, 1, 4800, saw);
  char line[1024];
  snprintf(line, sizeof(line), "load 0 %s", path);
  expect(h, line, "clip 0 ok 4800 1\n");
  quiet(h, "set loop_0 1");
  quiet(h, "play 0");
  run(h, 5 * 4800);
  x = last(5 * 4800);
  const Clip *c = p->current[0];
  float own = 0.5f / 4800, fade = 0.5f / c->fade, end = 0.5f / c->end_ramp;
  assert(largest_step(x, 5 * 4800, 0) <= fade + own);
  assert(x[5 * 4800 - 1] != 0 && meter(h, "playing_0") == 1);
  assert(memcmp(left + recorded - 4800, right + recorded - 4800, 4800 * sizeof(float)) == 0);
  // Loop off on the running voice: it plays out this pass and ends with its fade.
  quiet(h, "set loop_0 0");
  run(h, 2 * 4800);
  x = last(2 * 4800);
  assert(largest_step(x, 2 * 4800, x[-1]) <= end + own);
  assert(x[2 * 4800 - 1] == 0 && meter(h, "playing_0") == 0);
  // A clip of three frames loops with nothing to fade over.
  const char *tiny = named("tiny.wav");
  write_clip(tiny, SF_FORMAT_WAV | SF_FORMAT_FLOAT, RATE, 1, 3, dc);
  snprintf(line, sizeof(line), "load 1 %s", tiny);
  expect(h, line, "clip 1 ok 3 1\n");
  quiet(h, "set loop_1 1");
  quiet(h, "play 1");
  run(h, 2 * BLOCK);
  assert(fabsf(left[recorded - 1] - 0.1f) < 1e-6f);
  quiet(h, "stop 1");
  run(h, 2 * BLOCK);
  assert(left[recorded - 1] == 0);
  release(h);
  puts("PASS: a clip's end fades, a loop wraps through its crossfade, loop off plays out");
}

static void check_replacing(void) {
  Host *h = dc_board("dc.wav", RATE);
  Player *p = h->impl;
  const float bound = 0.1f / (PLAYER_RAMP * RATE) * 1.01f;
  quiet(h, "play 0");
  run(h, 2 * BLOCK);
  // A new clip in a playing slot: the old voices fade out on the old clip,
  // which comes back to the main thread and is freed once they have.
  const char *quieter = named("silence.wav");
  write_clip(quieter, SF_FORMAT_WAV | SF_FORMAT_FLOAT, RATE, 2, RATE, silence);
  char line[1024];
  snprintf(line, sizeof(line), "load 0 %s", quieter);
  expect(h, line, "clip 0 ok 48000 2\n");
  assert(p->live_count == 2);
  run(h, BLOCK / 2);
  player_tick(h);
  assert(p->live_count == 2);  // still fading
  run(h, 2 * BLOCK);
  float *x = last(2 * BLOCK + BLOCK / 2);
  assert(largest_step(x, 2 * BLOCK + BLOCK / 2, 0.1f) <= bound && x[2 * BLOCK] == 0);
  player_tick(h);
  assert(p->live_count == 1 && p->current[0]->samples[0] == 0);
  // Unloading a playing slot fades it the same way.
  expect(h, "load 0 /nonexistent/ignored", "clip 0 error the file does not exist\n");
  snprintf(line, sizeof(line), "load 0 %s", named("dc.wav"));
  expect(h, line, "clip 0 ok 48000 2\n");
  quiet(h, "play 0");
  run(h, 2 * BLOCK);
  expect(h, "unload 0", "clip 0 unloaded\n");
  run(h, 2 * BLOCK);
  x = last(2 * BLOCK);
  assert(largest_step(x, 2 * BLOCK, 0.1f) <= bound && x[2 * BLOCK - 1] == 0);
  player_tick(h);
  assert(p->live_count == 0 && p->current[0] == NULL);
  // Presses with no audio running fill the queue, and the rest are dropped.
  expect(h, line, "clip 0 ok 48000 2\n");
  for (unsigned i = 0; i < EVENT_CAPACITY + 10; i++) quiet(h, "play 0");
  run(h, 2 * BLOCK);
  assert(meter(h, "playing_0") == 8);
  // Unloading with clips still playing frees everything.
  quiet(h, "play 0 restart");
  release(h);
  puts("PASS: a replaced or unloaded clip fades out and is freed after its voices end");
}

int main(int argc, char **argv) {
  assert(argc == 2);
  snprintf(directory, sizeof(directory), "%s/openxlr-player-XXXXXX",
           getenv("TMPDIR") ? getenv("TMPDIR") : "/tmp");
  assert(mkdtemp(directory));
  check_backend();
  check_resampler();
  check_decoding(argv[1]);
  check_refusals();
  check_damaged();
  check_presses();
  check_voice_limit();
  check_ends_and_loops();
  check_replacing();
  char command_line[512];
  snprintf(command_line, sizeof(command_line), "rm -rf '%s'", directory);
  assert(system(command_line) == 0);
  return 0;
}
