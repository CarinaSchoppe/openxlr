# Mixer and recovery verification

The verified story has complementary layers: Avalonia controls → a simulated
WebSocket daemon; actual daemon/PipeWire → persisted settings; and native
LSP gestures → host controls → daemon settings → measured audio. The opt-in
live test exercises the real audio graph, not the simulated UI server.

## Safe checks (no audio interruption)

```sh
dotnet build src/OpenXLR.slnx -c Release
dotnet test src/OpenXLR.Tests/OpenXLR.Tests.csproj -c Release
dotnet format style src/OpenXLR.slnx --verify-no-changes --no-restore
dotnet format analyzers src/OpenXLR.slnx --severity warn --verify-no-changes --no-restore
git diff --check
python3 tools/verify-native-host.py
```

`MixerUiTests` runs the actual XAML/controls on Avalonia's headless dispatcher
with a real local WebSocket server. It checks Add, routing pickers surviving
state updates, per-card sends, deletion, stale-window disabling, older-daemon
feature detection, and knob two-way binding after keyboard input.
The per-channel Inserts button opens that channel's chain, and the native
editor button is tested through a real WebSocket, including server errors
and disabling the feature when connected to an older daemon.
`DaemonClientTests` checks concurrent catalog requests, correlated failures,
reconnection, no automatic replay, and cancellation/disposal.
`ServiceWatchdogTests` checks monotonic progress expiry and systemd settings.
`ServiceCommandTests` checks non-blocking timeouts,
large stdout/stderr and failed exits. Native host contract tests cover sample
rates; optional installed-LSP tests are explicitly skipped if the bundle is
absent (CI installs it before the test run).
The private-server audio test measures actual LSP output gain changes and
compression without using the user's server or hardware. It is part of CI.
Its optional `--native-ui` mode requires an X11/XWayland display (or Xvfb)
and bubblewrap. The test overlays the child host's legacy `.config` directory:
LSP 1.2.14 ignores `XDG_CONFIG_HOME`, so that variable alone neither isolates
user settings nor reliably exercises a fresh first-run profile.
CI explicitly uses `--disposable-ci-profile` on GitHub-hosted runners, where
the test checks that no existing LSP profile is present. That option rejects
local/self-hosted runs. It avoids changing Ubuntu's namespace restrictions;
local runs still use bubblewrap and leave the user's settings untouched.
Set `OPENXLR_SCREENSHOT_DIR` to a directory when running the tests to export
rendered PNGs of the layout editor, individual card and plugin controls.

## Real audio/service test (explicitly disruptive)

Do not run during recording or calls. Requires a systemd user session,
PipeWire/Pulse compatibility, `pw-dump`, `pacat`, `parec`, LSP LV2 plugins,
Python 3 and `websocket-client` (`python-websocket-client` on Arch).
Install the PipeWire Pulse descriptor-limit override from
[source installation](install-from-source.md#6-make-it-permanent) first.

```sh
python3 tools/verify-live-mixer.py --allow-audio-interruption
# Includes actual X11/XWayland vendor windows and a real knob gesture:
python3 tools/verify-live-mixer.py --allow-audio-interruption --native-ui
```

The script temporarily stops `openxlr-daemon.service`, starts the Release
build in a transient user unit with disposable settings, and restores the
previous service in `finally`. It verifies node creation/removal, simultaneous
layout edits from two clients, application fallback, a generated signal
through an LSP stereo compressor and bypass, persistence, forced SIGKILL
recovery, a SIGSTOP hang triggering the 60-second systemd watchdog, and a
graceful stop with a client still connected. It prints measured results and
fails on unmet assertions. Allow several minutes for graph rebuilds and the
intentional watchdog timeout. Do not run multiple copies concurrently.

The extended test also compares processed/unprocessed application channels,
kills and freezes/rebuilds an individual plugin host, and measures an EQ at two frequencies.
Application tests inspect the real Pulse sink-input target: a test player
starts in another muted channel, is moved by its saved assignment, and is
reassigned while still playing. Audio captures also start in the wrong sink
and require automatic routing before measuring the settled output signal.
`--native-ui` checks the native compressor/EQ editors, wheels the compressor's
Output control, observes the updated daemon parameter and measures its audio
effect. The synthetic X11 gesture targets only the identified test instance's
canvas and does not move the pointer. It closes only the test instance's
owned transient greeting with `WM_DELETE_WINDOW` before editing a control;
older LSP releases do not implement Escape there. CI runs the private-server version
under Xvfb. The gesture recognizes the default compressor layouts of LSP
1.2.14 (Ubuntu) and 1.2.35; unknown layouts fail instead of silently passing.
CI retains the editor screenshot on failure. `--screenshots DIR` additionally captures the live vendor
windows using ImageMagick. The original daemon and default devices are restored
in `finally`; never use this script during an important recording/call.

The headless suite does not replace a physical desktop/hardware acceptance
test. It does not verify every LV2 plugin or every supported USB device.
Native LSP FFT/history/gain-reduction runs in the vendor window; OpenXLR's
generated graphs remain labelled parameter overviews. Tests do not claim
support for arbitrary plugin state/files, GTK/Qt-only UIs or all hardware.

## Recorded acceptance run (2026-09-03)

On the CachyOS/PipeWire development session, the Release build passed 73
automated tests (zero failures/skips), .NET style/analyzer checks, strict C
compilation and GCC's static analyzer. Daemon/UI publish output includes an
executable native helper whose shared-library dependencies resolve.

The private-server test passed native UI editing and audio measurements
with LSP 1.2.35 and the extracted Ubuntu 1.2.14 package on fresh isolated profiles;
it additionally asserts that no hardware nodes are owned by its graph.
The disruptive full live test passed all assertions, including:

| Check | Measured result |
| --- | --- |
| Compressor output gain and bypass | dry 0.2000, processed 0.0500, bypass 0.2000 |
| Independent application channels | processed 0.1000, other channel 0.2000 |
| Program assignment and live channel switching | actual Pulse sink-input destinations matched the requested channels |
| Native Output knob | change returned to daemon, saved to disk, and changed measured audio |
| EQ response | 440 Hz: 0.0500; 6 kHz: 0.1980 |
| Plugin host failure | SIGKILL and SIGSTOP both reconstructed processing with saved controls |
| Daemon failure | SIGKILL and 60-second watchdog/SIGSTOP restored layout without duplicate nodes |
| Delete | nodes, inserts and sends removed; application assignment moved to fallback |
| Shutdown with connected WebSocket | 0.69 seconds |

This is evidence for these scenarios, not a guarantee for every plugin,
device or operating system. Complete Debian/RPM/Nix package builds are not
part of this local result; their build inputs and helper permissions were
updated, and release workflows check the packaged executable.
