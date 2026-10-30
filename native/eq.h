// SPDX-License-Identifier: GPL-3.0-only
// The microphone EQ's filter shapes and their coefficients (eq.c), shared with
// the voice changer (voice.c), which builds its own filters from them.
#pragma once

enum { LOW_CUT, LOW_SHELF, PEAK, NOTCH, HIGH_SHELF, HIGH_CUT };

typedef struct {
  double b0, b1, b2, a1, a2;
} EqCoefficients;

// One biquad from the Audio EQ Cookbook, normalised so a0 is 1.
void eq_coefficients(int type, double rate, double frequency, double gain_db,
                     double q, EqCoefficients *out);
