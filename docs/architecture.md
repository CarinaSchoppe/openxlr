# Architecture

```
                     WebSocket (127.0.0.1:37890, JSON)
   OpenXLR.UI   ────────────────┐
   (Avalonia)                   ▼
                         OpenXLR.Daemon  ── libusb ──►  Elgato interface
   OpenDeck plugin ────► (ASP.NET Core)                 (vendor protocol)
   scripts, tools               │
                                └── pactl / pw-cli / pw-link ──► PipeWire graph
```

- `OpenXLR.Daemon` owns everything: it connects the device, polls its
  state, builds and maintains the PipeWire graph, routes application
  streams, and serves a WebSocket API. It broadcasts every state change
  to all clients, whichever client (or the hardware) caused it.
- `OpenXLR.UI` is a stateless view over that API and can be closed at any
  time; the daemon keeps mixing.
- `OpenXLR.Core` holds the device backend and the mixer engine; the
  daemon and any future tooling share it.

## The PipeWire graph

Applications play into per-channel combine sinks whose internal streams
(one per mix) are the faders. The whole 9x4 matrix costs 13 sinks and zero
loopback processes, and direct port links clock everything through the
output device. Hardware inputs are wired by capture-channel pair
(XLR 1 = pair 0, XLR 2 = pair 1, Line In/USB Aux = pair 2). Stream and Chat
mixes are published as `OpenXLR Stream` / `OpenXLR Chat` capture devices;
the Aux mix feeds the device's aux return pair so the hardware forwards it
to the USB Aux port.

## The device protocols

The four devices speak three different dialects:

- Wave XLR Pro and MK.2: a vendor block bank on the unclaimed interface
  (`bmRequestType 0x41/0xC1`, `bRequest 1`, `wIndex 0x0103` on the Pro,
  `0x0203` on the MK.2). Fixed-size blocks hold gain, packed flag bits,
  and the hardware mix matrix; a write reads the block, modifies it,
  writes it back, and follows with a commit block. Full offsets and the
  discovery story: [wave-xlr-pro-protocol.md](wave-xlr-pro-protocol.md)
- Wave XLR (MK.1) and XLR Dock: a small class-request protocol
  (`bRequest 0x85/0x05`, `wIndex 0x3303`) with one config block, proven
  by the openwave project. The dock turned out to speak it too, which is
  how it gained phantom power (config byte 6) and low impedance (byte
  33); its everyday controls (gain, mute, headphone volume) ride the
  kernel's standard ALSA path, and its DSP is provided host-side by the
  submixer

## Repository layout

```
src/            .NET solution: Core (device + mixer), Daemon, UI, Probe
plugin/         the OpenDeck (Stream Deck) plugin
docs/           protocol documentation, research log, capture methodology
tools/          proprobe.py, a standalone python probe for the vendor protocol
packaging/      systemd unit, udev rule, WirePlumber rules, rpm and nix packaging
```
