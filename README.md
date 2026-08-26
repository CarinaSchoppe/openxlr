# OpenXLR

Native Linux control suite for the Elgato Wave XLR Pro: full hardware
control over a reverse-engineered USB protocol, plus a Wave Link style
PipeWire submixer with per-application channels, virtual microphones,
multi-output monitoring, and a dedicated mix for a second computer on the
device's USB Aux port.

![OpenXLR mixer](docs/screenshot-mixer.png)

Elgato ships no Linux software. The Wave XLR Pro enumerates as a
class-compliant USB Audio 2.0 interface, so audio flows out of the box.
Gain DSP, phantom power, output routing and the hardware mixer only answer
to a vendor protocol, which this project reverse engineered from USB
captures of Wave Link and reimplemented from scratch. The protocol is
documented in [docs/wave-xlr-pro-protocol.md](docs/wave-xlr-pro-protocol.md).

Not affiliated with or endorsed by Elgato. Built by protocol analysis on
the author's own hardware.

## Features

### Hardware control

Everything Wave Link exposes for the device:
- Per-XLR input: gain (0–80 dB), mute, low cut, expander, voice tune with
  strength, phantom power, ClipGuard, compressor
- USB Aux input stage: level (0 to −60 dB) and level lock
- Both headphone outputs: independent volumes, low-impedance mode
- Mic ↔ PC zero-latency direct-monitor crossfade
- Physical output routing: each output (Headphones 1/2, Line Out, USB Aux)
  is switched in the device's own hardware mixer

### Submixer

Pure PipeWire, no custom drivers or kernel modules:
- Channels for the hardware inputs (XLR 1, XLR 2, Aux In) and for
  application groups (Game, Music, Browser, System, Voice Chat, SFX)
- Four mixes: Monitor (what you hear), Stream and Chat (published as
  virtual microphones selectable in OBS/Discord), and Aux (what a second
  computer on the USB Aux port receives)
- Per-channel, per-mix send levels and mutes; per-mix masters
- Monitor mix playable on several outputs at once, hardware outputs
  included
- Live level meters throughout, dB-scaled

### Application routing
- OpenXLR detects every running audio-capable app through its PipeWire
  client registration and routes it to a channel by rules; it remembers
  each assignment, editable even while the app is silent
- Truthful names for Electron apps (Discord is "Discord", not "Chromium")
- Manage dialog with the full app registry and an installed-application
  picker to pre-assign channels from `.desktop` entries

### Quality of life
- Live Audio Flow graph of the whole routing, sources through outputs
- The daemon holds your chosen system-default devices and re-asserts them
  every second, the way Wave Link does
- Tray icon, start-minimized option, autostart integration
- One-click diagnostics archive for bug reports

## Architecture

```
                    WebSocket (127.0.0.1:37890, JSON)
   OpenXLR.UI  ────────────────┐
   (Avalonia)                  ▼
                        OpenXLR.Daemon  ── libusb ──►  Wave XLR Pro
   future clients ────► (ASP.NET Core)                 (vendor protocol)
   (OpenDeck plugin,           │
    scripts, …)                └── pactl / pw-cli / pw-link ──► PipeWire graph
```

- `OpenXLR.Daemon` owns everything: it connects the device, polls its
  state, builds and maintains the PipeWire graph, routes application
  streams, and serves a WebSocket API. It broadcasts every state change
  to all clients, whichever client (or the hardware) caused it.
- `OpenXLR.UI` is a stateless view over that API and can be closed at any
  time; the daemon keeps mixing.
- `OpenXLR.Core` holds the device backend and the mixer engine; the
  daemon and any future tooling share it.

### The PipeWire graph

Applications play into per-channel combine sinks whose internal streams
(one per mix) are the faders. The whole 9x4 matrix costs 13 sinks and zero
loopback processes, and direct port links clock everything through the
output device. Hardware inputs are wired by capture-channel pair
(XLR 1 = pair 0, XLR 2 = pair 1, Line In/USB Aux = pair 2). Stream and Chat
mixes are published as `OpenXLR Stream` / `OpenXLR Chat` capture devices;
the Aux mix feeds the device's aux return pair so the hardware forwards it
to the USB Aux port.

### The device protocol, in one paragraph

All control rides on vendor control transfers to the device's unclaimed
interface 3 (`bmRequestType 0x41/0xC1`, `bRequest 1`, `wIndex 0x0103`,
`wValue` = block number): a paged property bank of fixed-size blocks holding
byte fields. Those fields are gain in dB, quarter-dB attenuators, packed
flag bits, and the hardware mix matrix (per-mix level cells, membership
bits, and per-output mix assignments). A write reads the whole block,
modifies it, writes it back, and follows with a commit block. One quirk matters: the device latches its aux-return
routing when the host playback stream starts, so enabling the aux port
bounces the stream once. Details, offsets and the discovery story:
[docs/wave-xlr-pro-protocol.md](docs/wave-xlr-pro-protocol.md).

## Requirements

- Linux with PipeWire 1.4 or newer (developed on 1.6), `pipewire-pulse`
  and WirePlumber; `pactl`, `pw-cli`, `pw-link`, `pw-dump`, `parec` on PATH
- .NET 10 SDK to build (runtime to run)
- libusb 1.0
- An Elgato Wave XLR Pro (`0fd9:00b4`). Other Wave revisions speak
  different protocols and are not supported (see
  [rikkichy/openwave](https://github.com/rikkichy/openwave) for the
  original Wave XLR)

## Install

```sh
git clone https://github.com/emaspa/openxlr.git
cd openxlr/src
dotnet build -c Release
```

Device access (udev rule, then replug the device):

```sh
sudo tee /etc/udev/rules.d/70-wavexlr-pro.rules << 'EOF'
SUBSYSTEM=="usb", ATTRS{idVendor}=="0fd9", ATTRS{idProduct}=="00b4", MODE="0660", TAG+="uaccess"
EOF
sudo udevadm control --reload
```

Run the daemon (the mixer graph is opt-in so a bare run never surprises
your audio setup):

```sh
OPENXLR_BUILD_MIXER=1 ./OpenXLR.Daemon/bin/Release/net10.0/OpenXLR.Daemon
```

Run the UI:

```sh
./OpenXLR.UI/bin/Release/net10.0/OpenXLR.UI
```

For permanent use, the Options window (the gear button) installs a systemd
user unit for the daemon and an autostart entry for the UI; a reference unit is in
[packaging/openxlr-daemon.service](packaging/openxlr-daemon.service).

### Environment variables

| Variable | Effect |
|---|---|
| `OPENXLR_BUILD_MIXER=1` | build the PipeWire submix graph (otherwise device-control only) |
| `OPENXLR_MONITOR_OUTPUT=<sink>` | initial monitor output (overrides saved choice) |

## WebSocket API

The daemon serves `ws://127.0.0.1:37890/ws`. On connect (and on every
change) it pushes a full `{"type":"state", …}` message carrying device
state, capabilities, mixer state, the device list and the app registry;
meters arrive as small `{"type":"meters"}` frames at 15 Hz. Commands
are single JSON objects:

| Command | Fields | Purpose |
|---|---|---|
| `getState` | none | request a state push |
| `set` | `control`, `value` | hardware control (`gain`, `mute`, `lowCut`, `expander`, `voiceTune`, `voiceTuneStrength`, `phantom`, `clipGuard`, `compressor`, `…2` variants, `hpVolumeDb`, `hp2VolumeDb`, `lowImpedance`, `crossfade`, `auxLevelDb`, `auxLevelLock`, `outHp1/2`, `outUsbAux`, `outLineOut`) |
| `setLevel` | `channel`, `mix`, `value` | one send fader |
| `setChannelMuted` | `channel`, `mix`, `value` | one send mute |
| `setMixVolume` / `setMixMuted` | `mix`, `value` | mix masters |
| `setMonitorOutputs` | `devices[]` | every sink the monitor mix feeds |
| `setAuxPortEnabled` | `value` | send the Aux mix to the USB Aux port |
| `setOutputVolume` | `value` | volume of the selected monitor devices |
| `assignApp` | `identity`, `channel`, `label?` | route an app (creates a registry entry if unseen) |
| `forgetApp` | `identity` | drop an app and its remembered channel |
| `setEnforcedDefaults` | `sink`, `source` | system defaults to hold |
| `getDiagnostics` | none | vendor block dump for bug reports |

This is the same API a future OpenDeck/Stream Deck plugin will use.

## Configuration

- `~/.config/openxlr/mixer.json` holds every mixer decision: levels, mutes,
  device choices, the app registry, enforced defaults (the daemon writes it)
- `~/.config/openxlr/ui.json` holds window preferences (tray, autostart)

## Reporting problems

Open Options, then SUPPORT, then Collect diagnostics. It writes
`~/openxlr-diagnostics-<timestamp>.tar.gz` with the app and device state, a
raw vendor-block dump, the PipeWire graph, daemon logs and configs. Nothing
gets uploaded; attach the archive to an issue yourself.

## Repository layout

```
src/            .NET solution: Core (device + mixer), Daemon, UI, Probe
docs/           protocol documentation, research log, capture methodology
tools/          proprobe.py, a standalone python probe for the vendor protocol
packaging/      systemd user unit
```

## Status

Daily-driven by the author. The next planned piece is an OpenDeck plugin
that puts the mixes and device controls on Stream Deck keys and dials over
the same WebSocket API.

## License

[GPL-3.0](LICENSE). If you find OpenXLR useful, consider
[buying me a coffee](https://buymeacoffee.com/emaspa).
