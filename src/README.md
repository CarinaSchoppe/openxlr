# OpenXLR

Open-source Linux control suite for USB audio interfaces, starting with the
Elgato Wave XLR Pro, designed to grow to other brands. Replaces the Windows-only
Elgato Wave Link + Stream Deck stack with a native, owned stack.

The name is deliberately vendor-neutral (`XLR`, the generic connector standard,
not Elgato's "Wave" branding), so additional interfaces (further Elgato
revisions, GoXLR, Focusrite, …) can be added behind the same daemon/UI/plugin
surface.

## Architecture (target)

A headless **daemon** owns the hardware and the PipeWire submixer and exposes a
stable control API; the **UI** (Avalonia) and the **OpenDeck plugin** (dial +
touch) are independent clients of that API. The control plane never sits in the
audio path.

```
OpenXLR.Core     device protocol + abstraction (IAudioDevice, DeviceRegistry) + submixer
OpenXLR.Daemon   headless service: owns device + submixer + WebSocket control API
OpenXLR.UI       Avalonia client of the API                                          [done]
opendeck-plugin  JS/OpenAction plugin, another API client (dials/touch)              [next]
```

## Control API (WebSocket + JSON)

The daemon serves `ws://127.0.0.1:37890/ws`. On connect a client receives a
`state` message and another on every change (including changes made by other
clients, so UI + plugin + CLI stay in sync).

One connection carries both the hardware controls and the submixer.

Client -> daemon (device):
- `{"cmd":"getState"}`
- `{"cmd":"set","control":"gain","value":52}` where controls are `gain` (dB),
  `mute`/`lowCut`/`expander`/`voiceTune`/`lowImpedance`/`phantom`/`clipGuard`/
  `polarity` (bool), `voiceTuneStrength` (0..100), `hpVolumeDb` (dB),
  `crossfade` (0..200).

Client -> daemon (mixer):
- `{"cmd":"setLevel","channel":"game","mix":"monitor","value":0.3}`
- `{"cmd":"setChannelMuted","channel":"music","mix":"stream","value":true}`
- `{"cmd":"setMixVolume","mix":"stream","value":0.4}`
- `{"cmd":"setMixMuted","mix":"chat","value":true}`
- `{"cmd":"assignStream","channel":"game","streamId":123}`
- `{"cmd":"setMonitorOutput","device":"<node.name>"}` (null disconnects)
- `{"cmd":"setMicInput","device":"<node.name>"}`
- `{"cmd":"assignStream","streamId":123,"channel":"music"}` (remembered per app)

Daemon -> client:
- `{"type":"meters","levels":{"ch:music":0.37,"mix:monitor":0.62}}` at 15 Hz
- `{"type":"state","connected":true,"device":{…},"capabilities":{…},"state":{…},"mixer":{"mixes":[…],"channels":[…],"monitorOutput":"…","micInput":"…"},"devices":[{"name":"…","description":"…","kind":0,"isOwn":false}]}`
- `{"type":"error","message":"…"}`

Run: `dotnet run --project OpenXLR.Daemon` (device only), or with the submixer:
`OPENXLR_BUILD_MIXER=1 dotnet run --project OpenXLR.Daemon`. Set
`OPENXLR_MONITOR_OUTPUT=<sink node.name>` to route the monitor mix to speakers
and `OPENXLR_MIC_INPUT=<source node.name>` to feed the mic channel. Both are also
selectable at runtime from the UI or the API.
The graph is built on start and torn down on shutdown.

## Status

- **OpenXLR.Core** holds the Wave XLR Pro vendor-block protocol, ported from the
  reverse-engineered spec and **verified on hardware** (gain/mute round-trip
  against ALSA). Multi-brand seam in place (`IAudioDevice`, `DeviceCapabilities`,
  `DeviceRegistry`). Device control uses a thin P/Invoke over `libusb-1.0`.
- **OpenXLR.Probe** is a console tool that detects the device, dumps full state, and
  cross-checks against ALSA. `dotnet run --project OpenXLR.Probe`.
- **OpenXLR.Daemon** is the headless service exposing the WebSocket control API above.
  **Verified on hardware**: connects the device, applies `set` commands live,
  and broadcasts state changes to all clients (multi-client sync confirmed).
- **Submixer** (`OpenXLR.Core/Mixing/`) is the Wave Link equivalent, **verified on
  live PipeWire**: a sink per channel, a sink per mix, a fader (pw-loopback) for
  every channel-mix pair with live volume, per-mix mute, mix master scaling,
  and non-monitor mixes published as virtual capture devices for OBS/Discord.
  Try it: `dotnet run --project OpenXLR.Probe -- mixer` (builds the graph,
  exercises faders, tears down clean). It is wired into the daemon and driven
  over the API above.

- **OpenXLR.UI** is the Avalonia desktop client, **verified running against live
  hardware**: device identity + connection dot, gain and headphone sliders, the
  DSP toggles, output and input pickers, and the full channel-strip grid (a fader
  + mute per channel per mix) plus mix masters. Reconnects on its own if the
  daemon isn't up yet. `dotnet run --project OpenXLR.UI`.
- **Per-app routing** matches each application stream to a channel by process
  binary, then app name, then media name, and moves it automatically. Wine and
  Proton games share a binary, so their identity folds in the media name, which
  keeps separate games apart and lets a manual choice pin one without affecting
  the others. **Verified live** against real streams (chrome to Browser, spotify
  to Music, steam and a wine binary to Game, Discord to Voice Chat, unknown apps
  to System). A manual change is remembered for the next launch.
- **Level meters** read each channel and mix monitor at 8 kHz mono and push
  decaying peaks at 15 Hz, shown as bars in the UI.
- **Persistence** stores levels, mutes, device choices, and per-app assignments
  in `~/.config/openxlr/mixer.json` (XDG aware, written atomically, debounced).
  **Verified**: restarting with no environment variables restores everything.
- **Device selection** lets the monitor mix feed any sink and the mic channel
  come from any source, real or virtual, changeable at runtime without
  rebuilding the graph. **Verified live**: switched output between physical
  sinks, to a virtual node, disconnected, and restored; same for the input.

Requires .NET 10 SDK and a udev rule granting access to the device
(`/etc/udev/rules.d/70-wavexlr-pro.rules`, MODE 0660 / uaccess for 0fd9:00b4).

Protocol reference: `../wave-xlr-pro-protocol.md`.
