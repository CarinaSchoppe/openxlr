// SPDX-License-Identifier: GPL-3.0-only
//! DeepFilterNet 3 for the native helper's noise removal (native/denoise.c),
//! behind a small C interface of its own. The model is built into the
//! library, so nothing is looked up on disk at run time. No entry point lets
//! a panic unwind into C: each one catches it and reports a failure, after
//! which the caller stops using that instance and frees it.
//!
//! The engine is libDF from DeepFilterNet 0.5.6 (MIT or Apache-2.0), kept
//! under libdf/ with its licences.

use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;

use df::tract::{DfParams, DfTract, RuntimeParams};
use ndarray::{ArrayView2, ArrayViewMut2};

/// DeepFilterNet3_onnx.tar.gz from the 0.5.6 release; the Makefile fetches
/// it and checks its SHA-256 before this crate is built.
static MODEL: &[u8] = include_bytes!("../model/DeepFilterNet3_onnx.tar.gz");

/// The smallest limit passed on. Below 0.01 dB DeepFilterNet hands the input
/// straight back without its delay, so a strength moving through zero would
/// jump in time; at 0.01 dB it still runs its model and its delay, and plays
/// the input all but untouched.
const LEAST_LIMIT_DB: f32 = 0.01;

/// One mono DeepFilterNet instance.
pub struct OpenxlrDf {
    model: DfTract,
}

/// A new instance with every buffer it needs, or null when the model could
/// not be loaded. `atten_lim_db` is the most the noise is turned down, in dB;
/// 100 and above means no limit.
#[no_mangle]
pub extern "C" fn openxlr_df_create(atten_lim_db: f32) -> *mut OpenxlrDf {
    let made = catch_unwind(|| {
        let params = DfParams::from_bytes(MODEL).ok()?;
        let limit = atten_lim_db.max(LEAST_LIMIT_DB);
        let runtime = RuntimeParams::default_with_ch(1).with_atten_lim(limit);
        let model = DfTract::new(params, &runtime).ok()?;
        Some(Box::into_raw(Box::new(OpenxlrDf { model })))
    });
    made.ok().flatten().unwrap_or(ptr::null_mut())
}

/// Samples taken and given by one call of `openxlr_df_process`.
///
/// # Safety
/// `st` comes from `openxlr_df_create` and has not been freed.
#[no_mangle]
pub unsafe extern "C" fn openxlr_df_hop(st: *const OpenxlrDf) -> usize {
    st.as_ref().map_or(0, |df| df.model.hop_size)
}

/// The sample rate the model runs at.
///
/// # Safety
/// As for `openxlr_df_hop`.
#[no_mangle]
pub unsafe extern "C" fn openxlr_df_rate(st: *const OpenxlrDf) -> usize {
    st.as_ref().map_or(0, |df| df.model.sr)
}

/// How many samples late a sample leaves `openxlr_df_process`: the overlap
/// of the analysis window plus the model's lookahead, in hops.
///
/// # Safety
/// As for `openxlr_df_hop`.
#[no_mangle]
pub unsafe extern "C" fn openxlr_df_delay(st: *const OpenxlrDf) -> usize {
    st.as_ref().map_or(0, |df| {
        df.model.fft_size - df.model.hop_size + df.model.lookahead * df.model.hop_size
    })
}

/// Change the attenuation limit; takes effect from the next hop.
///
/// # Safety
/// As for `openxlr_df_hop`, and no other call on `st` runs at the same time.
#[no_mangle]
pub unsafe extern "C" fn openxlr_df_set_atten_lim(st: *mut OpenxlrDf, atten_lim_db: f32) {
    if let Some(df) = st.as_mut() {
        let limit = atten_lim_db.max(LEAST_LIMIT_DB);
        let _ = catch_unwind(AssertUnwindSafe(|| df.model.set_atten_lim(limit)));
    }
}

/// Denoise one hop from `input` into `output`, each `openxlr_df_hop` samples.
/// 0 on success, -1 when the model failed; `output` is then undefined.
///
/// # Safety
/// As for `openxlr_df_set_atten_lim`; both buffers hold a hop of samples.
#[no_mangle]
pub unsafe extern "C" fn openxlr_df_process(st: *mut OpenxlrDf, input: *const f32, output: *mut f32) -> i32 {
    let Some(df) = st.as_mut() else { return -1 };
    if input.is_null() || output.is_null() {
        return -1;
    }
    let hop = df.model.hop_size;
    let done = catch_unwind(AssertUnwindSafe(|| {
        let noisy = ArrayView2::from_shape_ptr((1, hop), input);
        let enhanced = ArrayViewMut2::from_shape_ptr((1, hop), output);
        df.model.process(noisy, enhanced).is_ok()
    }));
    if matches!(done, Ok(true)) {
        0
    } else {
        -1
    }
}

/// Free an instance from `openxlr_df_create`; null is ignored.
///
/// # Safety
/// `st` is not used again.
#[no_mangle]
pub unsafe extern "C" fn openxlr_df_free(st: *mut OpenxlrDf) {
    if !st.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| drop(Box::from_raw(st))));
    }
}
