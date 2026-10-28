// SPDX-License-Identifier: GPL-3.0-only
// Worker ownership with a fake model. Schedule an audio handoff between
// the main thread's ownership reads, then take the returned worker while
// another model loads. Every created model must be freed at unload.
#define main host_program_main
#include "../host.c"
#undef main
#include <assert.h>

// Read the actual value first, then let the audio thread return its worker.
// This makes the failing interleaving deterministic without production hooks.
// Set watched before starting a worker and leave it alone until after join.
static void *watched;
static void (*after_load)(void);
#undef atomic_load
#define atomic_load(p) __extension__ ({ \
  __auto_type value = atomic_load_explicit((p), memory_order_seq_cst); \
  if ((void *)(p) == watched && after_load) { \
    void (*run)(void) = after_load; after_load = NULL; run(); \
  } \
  value; \
})
#include "../denoise.c"
#undef atomic_load
#define atomic_load(p) atomic_load_explicit((p), memory_order_seq_cst)

#if OPENXLR_DEEPFILTER
struct OpenxlrDf { int unused; };
static unsigned made, freed;
static bool fail_load;
static void (*during_load)(void);

OpenxlrDf *openxlr_df_create(float strength) {
  if (fail_load) return NULL;
  OpenxlrDf *model = calloc(1, sizeof(*model));
  assert(model);
  made++;
  if (during_load) { void (*run)(void) = during_load; during_load = NULL; run(); }
  return model;
}
size_t openxlr_df_hop(const OpenxlrDf *st) { return 480; }
size_t openxlr_df_rate(const OpenxlrDf *st) { return 48000; }
size_t openxlr_df_delay(const OpenxlrDf *st) { return 960; }
void openxlr_df_set_atten_lim(OpenxlrDf *st, float strength) {}
int32_t openxlr_df_process(OpenxlrDf *st, const float *in, float *out) {
  memcpy(out, in, 480 * sizeof(float));
  return 0;
}
void openxlr_df_free(OpenxlrDf *st) { freed++; free(st); }

static Denoise *subject;
static void take_again(void) { assert(df_take(subject)); }
static void return_worker(void) { df_give_back(subject); during_load = take_again; }

static Denoise *start(Host *h) {
  Denoise *d = denoise_load(h);
  assert(d);
  watched = &d->df_spare;
  atomic_store(&d->enabled->desired, 1);
  atomic_store(&d->engine->desired, DENOISE_DEEPFILTER);
  assert(denoise_activate(d));
  return d;
}

static void check_return_during_tick(void) {
  Host h = { .rate = 48000, .channels = 1 };
  subject = start(&h);
  unsigned before = made;
  after_load = return_worker;
  denoise_main_thread(subject);
  after_load = during_load = NULL;
  df_give_back(subject);
  denoise_unload(subject);
  assert(made == before && made == freed);
  puts("handoff: a returned worker cannot be replaced and leaked during the tick");
}

static void check_switch_while_held(void) {
  Host h = { .rate = 48000, .channels = 1 };
  Denoise *d = start(&h);
  unsigned before = made;
  DfWorker *worker = d->df;
  for (unsigned i = 0; i < 100; i++) {
    atomic_store(&d->enabled->desired, 0);
    denoise_main_thread(d);
    assert(freed + 1 == made);  // the audio thread still holds it
    atomic_store(&d->enabled->desired, 1);
    denoise_main_thread(d);
    df_give_back(d);
    denoise_main_thread(d);
    assert(df_take(d) && d->df == worker);
    assert(made == before);
  }
  atomic_store(&d->enabled->desired, 0);
  denoise_main_thread(d);
  df_give_back(d);
  denoise_main_thread(d);
  assert(made == freed);
  atomic_store(&d->enabled->desired, 1);
  denoise_main_thread(d);
  assert(df_take(d));
  assert(made == before + 1);
  denoise_unload(d);
  assert(made == freed);
  puts("handoff: switches reuse a held worker and free it only after its return");
}

static void check_failed_load_retry(void) {
  Host h = { .rate = 48000, .channels = 1 };
  fail_load = true;
  Denoise *d = start(&h);
  assert(atomic_load(&d->df_load_failed));
  fail_load = false;
  atomic_store(&d->enabled->desired, 0);
  denoise_main_thread(d);
  atomic_store(&d->enabled->desired, 1);
  denoise_main_thread(d);
  assert(!atomic_load(&d->df_load_failed) && df_take(d));
  denoise_unload(d);
  assert(made == freed);
  puts("handoff: a failed load leaves room for a later retry");
}
#endif

int main(void) {
#if OPENXLR_DEEPFILTER
  check_return_during_tick();
  check_switch_while_held();
  check_failed_load_retry();
  puts("denoise handoff: all checks passed");
#else
  puts("denoise handoff: DeepFilterNet is not built");
#endif
  return 0;
}
