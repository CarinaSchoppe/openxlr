# OpenXLR source tree

The canonical user, installation, hardware-support, architecture, and API
documentation lives in the repository [README](../README.md). Protocol status
is tracked in [hardware-support.md](../docs/hardware-support.md); the chronological
capture notebook is [wave-xlr-pro-protocol.md](../docs/wave-xlr-pro-protocol.md).

This file is intentionally limited to developer orientation so it cannot drift
into a second, contradictory product manual.

## Projects

- `OpenXLR.Core`: device backends, capability model, PipeWire graph, profiles,
  application matching, meters, and plugin chains.
- `OpenXLR.Daemon`: owns hardware and mixer state and exposes the localhost
  WebSocket API.
- `OpenXLR.UI`: Avalonia client; it never owns hardware or audio state.
- `OpenXLR.Probe`: diagnostics and protocol-development console tool.
- `OpenXLR.Tests`: regression tests for routing, device capabilities, profiles,
  diagnostics, and optional DSP dependencies.
- `../plugin/com.emaspa.openxlr.sdPlugin`: production OpenDeck/Stream Deck
  client of the same API.

## Current audio-graph invariants

- One combine sink per channel fans audio into the mixes; its internal streams
  are the per-mix faders. The matrix does not use one `pw-loopback` process per
  cell.
- Physical outputs use direct PipeWire port links so the hardware sink clocks
  the graph.
- Hardware input channels follow the actively selected interface. There is no
  `setMicInput` command or `OPENXLR_MIC_INPUT` override.
- Low cut, software ClipGuard, and LV2 inserts are optional filter chains in
  front of the affected channel. A replacement chain must be complete before
  it replaces the audible route.
- Device controls are capability-gated. A backend must not advertise a control
  until its implementation and hardware mapping are usable.

## Build and test

From the repository root:

```sh
dotnet restore src/OpenXLR.slnx
dotnet build src/OpenXLR.slnx -c Release --no-restore
dotnet test src/OpenXLR.Tests/OpenXLR.Tests.csproj -c Release --no-build
```

Run the daemon with the mixer enabled:

```sh
OPENXLR_BUILD_MIXER=1 dotnet run --project src/OpenXLR.Daemon
dotnet run --project src/OpenXLR.UI
```

The WebSocket command table and configuration paths are maintained only in the
root [README](../README.md#websocket-api).
