# Architecture

```
  OpenXLR.UI (Avalonia)   ──┐
  OpenDeck plugin (Node)  ──┼── WebSocket, JSON, 127.0.0.1:37890 ──►  OpenXLR.Daemon (ASP.NET Core)
  scripts, tools          ──┘                                          hosts OpenXLR.Core
                                                                          │
              ┌───────────────────────────────────────────────────────────┼──────────────────┐
              │                                                           │                  │
   libusb control transfers                                   amixer (ALSA controls)     lilv (in-process)
   Wave XLR Pro, Wave XLR MK.2,                               XLR Dock: gain, mute,     LV2 plugin catalog
   XLR Dock MK.2, Wave XLR (MK.1),                            headphone volume
   XLR Dock: phantom, low impedance
                                                                          │
                                       pactl (modules), pw-link, pw-dump, pw-cli, wpctl, parec
                                                                          ▼
                                                                   PipeWire graph

  ~/.config/openxlr: mixer.json, profiles/, gainlock.json (daemon); daemon.json, ui.json (UI)
```

- `OpenXLR.Daemon` owns the device and the graph: it opens the
  interface, polls its state every 100 ms, builds and maintains the
  PipeWire graph, routes application streams, and serves the WebSocket
  API. Every state change is broadcast to all clients, whichever client
  (or the hardware) caused it. It is a systemd user service.
- `OpenXLR.UI` is a view over that API with no dependency on
  `OpenXLR.Core`: it parses the state JSON and sends commands. Outside
  the API it only runs `systemctl --user` for the daemon's unit and
  writes `daemon.json` (the submixer on/off preference the daemon reads
  at start). It can be closed at any time; the daemon keeps mixing.
- The OpenDeck plugin is an OpenAction plugin running in OpenDeck's
  Node runtime, another client of the same API.
- `OpenXLR.Core` holds the device backends, the mixer engine, the
  PipeWire adapter and the profile store, shared by the daemon and the
  probe tool.

## The PipeWire graph

Everything is built with standard PipeWire modules and tools, no kernel
modules or custom drivers:

- One null sink per mix (`pactl load-module module-null-sink`), 4 in
  all.
- One combine sink per channel (`module-combine-sink`) whose internal
  streams, one per mix, are the send faders: setting a send is setting
  that stream's volume. Applications play into these sinks. 9 channels
  make 9 combine sinks, so the 9 by 4 matrix is 13 sinks and no
  loopback processes.
- For the Stream and Chat mixes, a post sink fed from the mix (directly
  or through the mix's insert chain) and a remap source
  (`module-remap-source`) reading its monitor: the virtual microphone an
  application records from. The indirection means adding inserts later
  never recreates the device the application is recording.
- Filter chains (the software low cut and ClipGuard, and the LV2
  inserts on inputs and mixes) are `filter-chain` nodes, each held by a
  long-lived `pw-cli -m` process for the life of the chain; their
  controls are set with `pw-cli set-param`.
- Direct port links (`pw-link`) wire hardware inputs, chains, mixes and
  outputs, so the output device clocks the chain. Hardware inputs are
  wired by capture-channel pair (XLR 1 = pair 0, XLR 2 = pair 1, Line
  In/USB Aux = pair 2); the Aux mix feeds the device's aux return pair
  so the hardware forwards it to the USB Aux port.
- `pw-dump` reads the graph, `wpctl` sets card profiles (parking the
  Pro on pro-audio) and node volumes, and `parec` on the sinks'
  monitors feeds the level meters.

## The device protocols

The five devices speak three dialects, all reached without detaching
the kernel's audio driver:

- Wave XLR Pro, Wave XLR MK.2 and XLR Dock MK.2: a vendor block bank
  on the unclaimed interface (`bmRequestType 0x41/0xC1`, `bRequest 1`,
  `wIndex 0x0103` on the Pro, `0x0203` on the MK.2 family). Fixed-size
  blocks hold gain, packed flag bits, and on the Pro the hardware mix
  matrix; a write reads the block, modifies it, writes it back, and on
  the Pro follows with a commit block. Offsets and how they were found:
  [wave-xlr-pro-protocol.md](wave-xlr-pro-protocol.md)
- Wave XLR (MK.1) and XLR Dock: a class-request protocol
  (`bRequest 0x85/0x05`, `wIndex 0x3303`) with one config block, as
  documented by the openwave project. The dock answers it too, which is
  how it gained phantom power (config byte 6) and low impedance (byte
  33); its everyday controls (gain, mute, headphone volume) go through
  the kernel's standard ALSA controls with `amixer`, and its DSP is
  provided host-side by the submixer

Every USB control transfer runs under a watchdog (the libusb timeout
plus 3 s); one that never returns is reported, the device dropped and
reconnected, and the daemon keeps serving.

## Repository layout

```
src/            .NET solution: Core (device + mixer), Daemon, UI, Probe, Tests
plugin/         the OpenDeck (Stream Deck) plugin
docs/           this documentation, protocol write-up, capture guides
tools/          proprobe.py, a standalone Python probe for the vendor protocol
packaging/      systemd unit, udev rule, WirePlumber rules, sysctl drop-in,
                UCM profile, rpm and nix packaging, OpenDeck patches
debian/         Debian/Ubuntu packaging
```
