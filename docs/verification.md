# Mixer and recovery verification

The automated story is: native Avalonia controls → real loopback WebSocket →
daemon/PipeWire → persisted settings → authoritative state → updated UI.

## Safe checks (no audio interruption)

```sh
dotnet build src/OpenXLR.slnx -c Release
dotnet test src/OpenXLR.Tests/OpenXLR.Tests.csproj -c Release
dotnet format style src/OpenXLR.slnx --verify-no-changes --no-restore
dotnet format analyzers src/OpenXLR.slnx --severity warn --verify-no-changes --no-restore
git diff --check
```

`MixerUiTests` runs the actual XAML/controls on Avalonia's headless dispatcher
with a real local WebSocket server. It checks Add, routing pickers surviving
state updates, per-card sends, deletion, stale-window disabling, older-daemon
feature detection, and knob two-way binding after keyboard input.
`DaemonClientTests` checks concurrent catalog requests, correlated failures,
reconnection, no automatic replay, and cancellation/disposal.
`ServiceWatchdogTests` checks monotonic progress expiry and systemd settings.
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

The headless suite does not replace a physical desktop/hardware acceptance
test. It does not verify every LV2 plugin or every supported USB device.
Native vendor UI hosting and LSP FFT/gain-reduction telemetry are not provided
by the current PipeWire control protocol; the OpenXLR graphs are explicitly
labelled parameter overviews.
