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

- `OpenXLR.Daemon` owns the device and the graph: it connects the
  device, polls its state, builds and maintains the PipeWire graph,
  routes application streams, and serves the WebSocket API. Every state
  change is broadcast to all clients, whichever client (or the hardware)
  caused it.
- `OpenXLR.UI` is a view over that API and can be closed at any time;
  the daemon keeps mixing.
- `OpenXLR.Core` holds the device backends and the mixer engine, shared
  by the daemon and any other tooling.

## The PipeWire graph

Applications play into per-channel combine sinks whose internal streams
(one per mix) are the faders. The 9 by 4 matrix costs 13 sinks and no
loopback processes, and direct port links (`pw-link`) let the output
device clock the chain. Hardware inputs are wired by capture-channel
pair (XLR 1 = pair 0, XLR 2 = pair 1, Line In/USB Aux = pair 2). The
Stream and Chat mixes are published as the `OpenXLR Stream` and
`OpenXLR Chat` capture devices; the Aux mix feeds the device's aux
return pair so the hardware forwards it to the USB Aux port.

Insert chains, the software low cut and ClipGuard are PipeWire
filter-chain nodes placed in the same graph.

## The device protocols

The four devices speak three dialects:

- Wave XLR Pro and MK.2: a vendor block bank on the unclaimed interface
  (`bmRequestType 0x41/0xC1`, `bRequest 1`, `wIndex 0x0103` on the Pro,
  `0x0203` on the MK.2). Fixed-size blocks hold gain, packed flag bits,
  and the hardware mix matrix; a write reads the block, modifies it,
  writes it back, and follows with a commit block. Offsets and how they
  were found: [wave-xlr-pro-protocol.md](wave-xlr-pro-protocol.md)
- Wave XLR (MK.1) and XLR Dock: a class-request protocol
  (`bRequest 0x85/0x05`, `wIndex 0x3303`) with one config block, as
  documented by the openwave project. The dock answers it too, which is
  how it gained phantom power (config byte 6) and low impedance (byte
  33); its everyday controls (gain, mute, headphone volume) use the
  kernel's standard ALSA path, and its DSP is provided host-side by the
  submixer

## Repository layout

```
src/            .NET solution: Core (device + mixer), Daemon, UI, Probe
plugin/         the OpenDeck (Stream Deck) plugin
docs/           this documentation, protocol write-up, capture guides
tools/          proprobe.py, a standalone Python probe for the vendor protocol
packaging/      systemd unit, udev rule, WirePlumber rules, sysctl drop-in,
                UCM profile, rpm and nix packaging, OpenDeck patches
debian/         Debian/Ubuntu packaging
```
