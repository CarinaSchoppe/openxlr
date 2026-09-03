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
python3 -m unittest discover -s tools -p 'test_*.py'
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
It also exports the restart/update header, change-log window and Options.
The restart control is driven with an injected asynchronous service result,
including disabled/busy state and retry; no unit test restarts a real service.
Update tests use offline HTTP responses for numeric version ordering,
draft/prerelease exclusion, ancestry-aware commit notices, invalid repositories,
response-size limits, failures, cancellation and duplicate checks.
Diagnostic tests cover privacy redaction and real duplicate node names versus
intentional multi-stage routing.

## Distribution packages and runtime

`Package and runtime matrix` builds on Ubuntu 24.04, Fedora 44 and Arch rolling,
runs the .NET suite, installs the resulting `.deb`, `.rpm` or `.pkg.tar.zst`, and
starts the **installed** daemon and Avalonia UI. Runtime tests run as an
unprivileged user with isolated PipeWire, Pulse, WirePlumber, D-Bus and Xvfb.
They exercise API create/rename/send/delete, persisted cleanup, unique node
names, an actual X11 window, and daemon shutdown while UI clients remain open.
The installed native helper is separately tested with real LSP DSP and a native
editor gesture. This catches missing shared libraries and helper permissions,
not just whether an archive was produced.

These jobs deliberately do not publish a release or enable services globally.
Artifacts are snapshots of the workflow's commit. Containers do not provide a
real user systemd boot or physical USB hardware; the live CachyOS acceptance
test covers systemd recovery, while other devices/desktops still need hardware
acceptance. Check the actual workflow outcome before claiming distro success.

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

## Recovery/update follow-up (2026-09-04)

The extended suite passes 113 .NET tests, including the actual restart/update UI
controls, and two offline Python acceptance-driver tests. The .NET suite also
passes with an empty fontconfig configuration (no installed system fonts),
using the application's embedded Inter font as its explicit default.
The full live signal test also passes after restarting a stalled
PipeWire user session: both the unchanged `ae1bbbc` baseline and the new build
initially produced no capture samples, while private servers passed. Restarting
the audio session restored the live graph; this is not evidence that a daemon
watchdog can repair every PipeWire/kernel failure.

The latest full run again measured 0.2000 dry / 0.0500 processed / 0.2000 bypass,
independent channels 0.1000 / 0.2000, and EQ 0.0500 at 440 Hz versus 0.1980 at
6 kHz. Plugin SIGKILL/SIGSTOP and daemon SIGKILL/watchdog recovery passed, as did
persisted deletion and shutdown with a client connected (0.56 seconds).
Descriptions containing quotes, apostrophes and backslashes survive real
module parsing. A subsequent full run also passed with application fan-outs
classified as internal `Audio/Filter` nodes (not selectable Pulse sinks),
including native compressor/EQ editors, a real gesture, host and daemon
recovery, deletion and a 0.62-second shutdown. Their DSP ports are configured
explicitly because WirePlumber does not configure sink ports for that class.
Hardware metering and mix/capture taps still require Pulse sink/monitor
semantics, carry distinct role labels/filter metadata, and remain visible in
low-level graph tools. Distribution package jobs are an additional independent
gate, not covered by these local measurements.

### Completed package and installed-application checks

For implementation commit `eda5967`, both [general CI](https://github.com/CarinaSchoppe/openxlr/actions/runs/33778085812)
and the [package/runtime matrix](https://github.com/CarinaSchoppe/openxlr/actions/runs/33778085911)
completed successfully. Each of Ubuntu 24.04, Fedora 44 and Arch rolling passed
all 113 .NET tests with zero skips, the two Python driver tests, package build
and installation, the installed UI/daemon acceptance, and the installed native
helper's LSP editor gesture and measured DSP regression. Ubuntu uses its older
LSP package rather than substituting a newer development build.

The generated Arch archive was also extracted locally and its packaged
executables passed the private-server UI/API and native-editor/audio checks on
CachyOS. The installed user build then passed the real manual restart path:
the asynchronous request returned in 0.01 seconds, systemd replaced the daemon,
and the connected client recovered a fresh state automatically. The desktop UI
remained running; the service reported `WatchdogUSec=1min` and continuing
watchdog timestamps.

The packaged update checker queried GitHub successfully and correctly identified
`CarinaSchoppe/openxlr`, revision `eda5967`, as a development snapshot with no
newer build at that time. The real diagnostics export contained 21 entries,
including service health, audio journals and UI-session events. Its graph/state
JSON parsed, the graph had no duplicate OpenXLR node names, the home path was
redacted and the archive permissions were owner-only (`0600`). Nothing was
uploaded. Empty session logs remain valid archive entries.

This adds real package/runtime evidence for all three distributions, not a
claim of physical-device or systemd-boot testing inside the CI containers.
NixOS packaging and every possible LV2/device combination remain outside this
acceptance run.
