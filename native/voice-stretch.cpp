// SPDX-License-Identifier: GPL-3.0-only
// The voice changer's pitch and formant shifter: Signalsmith Stretch 1.4.0
// (signalsmith/, MIT) behind the C interface voice.c uses. Stretch allocates
// in configure() only; process(), reset() and the setters reuse what it holds,
// so after voice_shifter_create everything here is safe on the audio thread.
#include "voice.h"
#include "signalsmith-stretch.h"
#include <new>

struct VoiceShifter {
  // A fixed seed: the default constructor reads std::random_device, and the
  // output should not depend on the run.
  signalsmith::stretch::SignalsmithStretch<float> stretch{0x0de1e7eL};
};

extern "C" {

VoiceShifter *voice_shifter_create(int block, int interval) {
  if (block < 16 || interval < 1 || interval > block) return nullptr;
  VoiceShifter *s = new (std::nothrow) VoiceShifter;
  if (!s) return nullptr;
  s->stretch.configure(1, block, interval, false);
  // Let it find the fundamental for the formant analysis: a fixed guess
  // would suit a low voice or a high one, not both.
  s->stretch.setFormantBase(0);
  voice_shifter_set(s, 0, 0);
  return s;
}

void voice_shifter_free(VoiceShifter *s) { delete s; }

void voice_shifter_reset(VoiceShifter *s) { s->stretch.reset(); }

void voice_shifter_set(VoiceShifter *s, float pitch, float formant) {
  s->stretch.setTransposeSemitones(pitch);
  s->stretch.setFormantSemitones(formant, true);
}

void voice_shifter_process(VoiceShifter *s, const float *in, float *out, int frames) {
  const float *ins[1] = {in};
  float *outs[1] = {out};
  s->stretch.process(ins, frames, outs, frames);
}

int voice_shifter_latency(const VoiceShifter *s) {
  return s->stretch.inputLatency() + s->stretch.outputLatency();
}

}  // extern "C"
