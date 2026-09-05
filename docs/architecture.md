# Architecture

```
  OpenXLR.UI (Avalonia)   ──┐
  OpenDeck plugin (Node)  ──┼── HTTP/WebSocket JSON, 127.0.0.1:37890 ──►  OpenXLR.Daemon (ASP.NET Core)
  scripts, tools          ──┘                                          hosts OpenXLR.Core
                                                                          │
              ┌───────────────────────────────────────────────────────────┼──────────────────┐
              │                                                           │                  │
   libusb control transfers                                   amixer (ALSA controls)     isolated scanner
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
  PipeWire graph, routes application streams, and serves the versioned local
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

The fork's newer scanner, VST3, preset, sidechain, routing-matrix and delay
components are described in [parity-status.md](parity-status.md) and the
[native host contract](../native/README.md). The LV2 catalog is now read in a
child daemon process; third-party scan code is not loaded in the serving daemon.

Everything is built with standard PipeWire modules and tools, no kernel
modules or custom drivers:

- One null sink per mix (`pactl load-module module-null-sink`). Monitor
  and Aux are structural; every user-created output adds another one.
- One combine fan-out per channel (`module-combine-sink`) whose internal
  streams, one per mix, are the send faders: setting a send is setting
  that stream's volume. Hardware feeds these sinks directly. Applications
  play into stable public null sinks; each is linked through its stereo
  insert chain (or a direct bypass) into its internal fan-out. Application fan-outs
  are `Audio/Filter` nodes with explicitly configured DSP input ports: they do
  not advertise a second Pulse playback device. Hardware fan-outs retain sink
  classification because their monitor taps supply the hardware meters. The graph has
  one combine per hardware or user-created application channel and uses no
  loopback processes.
- For every user-created virtual output mix, a post sink fed from the mix (directly
  or through the mix's insert chain) and a remap source
  (`module-remap-source`) reading its monitor: the virtual microphone an
  application records from. The indirection means adding inserts later
  never recreates the device the application is recording.
- Adding, renaming, or deleting an application channel or virtual output rebuilds
  this owned module graph under the daemon lock. The old modules are
  unloaded, so WirePlumber immediately loses the deleted devices; live
  application streams are then moved to their remembered or fallback
  channel in the new graph. If the rebuild fails, the previous layout is
  restored.
- Reordering channels or output mixes changes only the persisted model and
  client presentation order. Stable ids and the live graph stay untouched, so
  audio is not interrupted.
- Renames keep the internal id stable, so application assignments, profile
  cells, insert keys, and controller references continue to resolve.
- Safety DSP (the software low cut and ClipGuard) uses `filter-chain` nodes, each held by a
  long-lived `pw-cli -m` process for the life of the chain; their
  controls are set with `pw-cli set-param`.
- Every LV2 insert uses an isolated `openxlr-lv2-host` process containing
  one lilv DSP instance and, when requested, that instance's native X11 UI.
  The host has direct PipeWire DSP ports; audio never crosses a managed
  process pipe. Float controls and output meter values use a bounded-size
  line protocol. Native UI edits are coalesced and saved by the daemon's
  normal sweep. See [native host contract](../native/README.md).
- Direct port links (`pw-link`) wire hardware inputs, chains, mixes and
  outputs, so the output device clocks the chain. Hardware inputs are
  wired by capture-channel pair (XLR 1 = pair 0, XLR 2 = pair 1, Line
  In/USB Aux = pair 2); the Aux mix feeds the device's aux return pair
  so the hardware forwards it to the USB Aux port.
- The selected monitor devices read the post-insert tap of whichever mix has
  its Listen button active. Hotplug healing and plugin-chain rebuilds resolve
  that saved mix id again; deleting it falls back to the structural Monitor mix.
- `pw-dump` reads the graph, `wpctl` sets card profiles (parking the
  Pro on pro-audio) and node volumes, and `parec` on the sinks'
  monitors feeds the level meters.

## The device protocols

### Service and UI lifecycle

`ServiceWatchdog` sends systemd `READY=1` after application startup and
`WATCHDOG=1` only while lock-free progress markers from the device/mixer
loops remain recent. It honours `WATCHDOG_USEC` and `WATCHDOG_PID`, including
abstract Unix notification sockets. Notification I/O has a two-second bound.
Units use `Type=notify`, `WatchdogSec=60`, `Restart=always`, `RestartSec=3`
and no start-rate cutoff; explicit stops remain stopped. Failed mixer startup
exits nonzero so a transient PipeWire failure can be retried. systemd kills
the daemon's child processes on failure; startup removes stale OpenXLR modules.

`MixerService` serializes commands, layout rebuild/rollback, snapshots and
saves. State broadcasts are coalesced through a bounded asynchronous channel:
no callback under the device lock synchronously acquires the mixer lock.
Sweep/meter callbacks cannot pile up; shutdown cancels delayed default-device
defence and prevents late timer saves. Dispose is idempotent because the
hosted service is also registered as a singleton.

`DaemonClient` has one reconnect loop, bounded connection/send attempts and
WebSocket ping/pong timeouts. Layout requests use correlated acknowledgements,
are not replayed after reconnect, and leave controls disabled until completion
or a reported failure. Flow defers rebuilding open routing pickers, and card
editors reuse the stable channel/send view models.

All UI-side systemctl operations drain stdout/stderr asynchronously, have
a five-second deadline and kill timed-out child processes. Unit changes
are serialized. Autostart preferences are persisted only after success.

### Hardware communication

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
