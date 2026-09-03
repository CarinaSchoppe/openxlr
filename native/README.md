# Isolated LV2 host

`openxlr-lv2-host URI NODE CHANNELS RATE [SYMBOL=VALUE ...]` owns exactly
one plugin. Build with `make -C native`; the Core build copies the executable
alongside the daemon/probe/test assemblies and into publish output.
Dependencies: C11 compiler, pkg-config, PipeWire development files, lilv,
LV2 headers and Xlib. It does not depend on JACK or a second audio server.

## Ownership and threading

- PipeWire's realtime callback copies preallocated audio buffers, applies
  atomic control values and runs LV2. Each PipeWire buffer is dequeued **once**
  per cycle. No logging, UI calls, IPC, locks or allocation occur in host RT code.
- The main loop handles stdin, X11 events and LV2 UI idle calls at 30 Hz.
  The UI receives instance-access to the **same DSP**, which LSP uses for
  its FFT meshes and history. A URI table publishes stable IDs atomically;
  unknown URIs requested on the RT thread return zero rather than allocate.
- The daemon drains stdout/stderr continuously, coalesces parameter changes,
  and consumes them under the mixer lock. A host cannot call into that lock.
- The host exits on stdin EOF or parent death. Closing the native window
  only destroys the UI. Plugin libraries remain resident until process exit,
  as toolkits can own process-global resources. The daemon re-creates a dead
  host with its persisted parameters and reconnects its ports. A one-second
  heartbeat covers both the main/UI loop and active audio callback. Ten seconds
  without progress marks the host unhealthy; the next mixer sweep replaces it.

## Line protocol

Input: `set SYMBOL FLOAT`, `show`, `hide`, `quit`. Arguments are never
evaluated by a shell. Lines are limited to 16 KiB. Non-finite/out-of-range
controls are rejected/clamped; the daemon validates the plugin metadata first.

Output: `ready`, `heartbeat`, `control SYMBOL FLOAT` (native UI edit), `meter SYMBOL FLOAT`
(plugin output port), `ui opened` or `ui unavailable: REASON`. Diagnostics
go to stderr. Output dictionaries contain one latest value per port, not an
unbounded event history. Readiness and UI opening have bounded waits.

## Supported contract

Mono XLR paths and stereo channel/mix paths, float controls, audio ports,
empty atom sequences, URID map/unmap, and X11 UIs using float ports or direct
instance access (LSP). Unsupported **required** features are rejected rather
than passing incomplete feature pointers. Native UI control values persist;
arbitrary atom messages, worker jobs, file/state extensions, MIDI and GTK/Qt
UI embedding are deliberately not advertised. Plugins requiring these need
an extension of this host contract, not silent fallbacks.

The graph sample rate is read from PipeWire metadata (forced rate takes
precedence). Maximum host quantum is 8192 frames. Unsupported graph changes
silence the cycle and terminate the host for supervised reconstruction;
they must never read/write beyond an allocated buffer.

## Verification

`python3 tools/verify-native-host.py` starts private PipeWire/Pulse/D-Bus and
WirePlumber with hardware monitors disabled, then measures output gain and
actual compression. It runs in CI without audio hardware. The opt-in
`tools/verify-live-mixer.py --allow-audio-interruption --native-ui` additionally
tests actual application routing, per-channel isolation, EQ response, native
UI interaction, host/daemon crash recovery and persisted settings. See
[`docs/verification.md`](../docs/verification.md) before running it.
