# Features

What OpenXLR does, area by area, and how. The README has the summary.

## Hardware control

Controls are reached over each device's USB protocol; the per-control
verification state is in [hardware-support.md](hardware-support.md).

Wave XLR Pro:
- Per XLR input: gain (0 to 80 dB), mute, low cut, expander, voice tune
  with strength, phantom power, ClipGuard, compressor
- USB Aux input stage: level (0 to -60 dB) and level lock
- Both headphone outputs: independent volumes, low-impedance mode
- Mic/PC direct-monitor crossfade (inside the device, no host latency)
- Physical output routing: Headphones 1, Headphones 2, Line Out and USB
  Aux are each switched in the device's hardware mixer

Wave XLR MK.2 (from captures, not run on hardware): gain, mute, low cut,
expander, voice tune with strength, headphone volume, low impedance,
crossfade.

Wave XLR: gain, mute, headphone volume, low impedance, phantom power.

XLR Dock: gain, mute and headphone volume through the kernel's standard
ALSA controls, plus phantom power and headphone low impedance over the
original Wave XLR's protocol dialect, which the dock also answers. The
phantom byte was identified by the
[openwave](https://github.com/rikkichy/openwave) project on the Wave XLR
([openwave PR #8](https://github.com/rikkichy/openwave/pull/8)) and
confirmed on the dock with a condenser microphone. Wave Link does not
write it for the dock. The dock has no onboard voice-processing DSP;
Wave Link runs those effects host-side, and on Linux the submixer
provides them (below).

## Software controls

For devices without the hardware version, the PipeWire layer provides:
- Low cut: a high-pass at 80 or 120 Hz (the two values Wave Link
  offers), a filter-chain node inserted between the mic and its channel,
  cycled from a button on the XLR 1 strip. Its response was measured
  with test tones as a second-order high-pass. The node is re-created
  if it disappears from the graph.
- ClipGuard: a post-ADC hard limiter at -3 dB in the same filter chain.
  It protects the downstream PipeWire mixes from overload, but cannot
  repair clipping that has already happened in the analogue preamp or
  ADC; microphone gain still needs headroom. It needs the `swh-plugins`
  LADSPA package. If that plugin is unavailable, enabling ClipGuard is
  rejected, the control stays disabled, and the existing microphone
  route remains live.
- Gain lock: the daemon rejects every gain change while the lock is set,
  from any client, and stores the lock per device in `gainlock.json`.
  Shown only for devices without a physical gain dial, which would
  bypass it.

These controls appear only when the active device lacks the hardware
version, so a signal is never filtered twice.

Two behaviours apply on multi-device switching: the mixer's hardware
input channels follow the active device, and after a switch the
hardware channels' monitor sends come up muted, so the newly patched mic
does not reach the speakers until unmuted.

## Submixer

Built from PipeWire nodes, no kernel modules or custom drivers:
- Structural channels for the hardware inputs (XLR 1, XLR 2, Aux In) and
  user-managed application channels. Game, Music, Browser, System, Voice
  Chat, and SFX are the initial layout; they can be added, renamed, removed,
  or reordered without changing their stable PipeWire ids.
- Structural Monitor (what you hear) and Aux (what a second computer on
  the USB Aux port receives) mixes, plus user-managed output mixes. Stream
  and Chat are the initial outputs; every added output is published as an
  `OpenXLR <name>` virtual microphone for OBS, Discord, or another app. The
  user-created output mixes can also be reordered.
- Per-channel, per-mix send levels and mutes; per-mix masters
- Any mix can be listened to after its master and inserts on several monitor
  outputs at once, hardware outputs included. The structural Monitor mix is
  the backwards-compatible default.
- Level meters throughout, dB-scaled, pushed at 15 Hz

Each channel has an internal fan-out with one stream per mix; that
stream's volume is the send fader. Application fan-outs do not advertise extra
playback devices; technical mix/monitor taps remain visible in low-level graph
tools and carry distinct internal-role labels. The Channels & outputs dialog changes
the persistent layout with stable internal ids. Add, rename and delete rebuild
the owned PipeWire nodes, including removing deleted devices from WirePlumber;
reordering changes presentation only and does not interrupt audio. Details in
[architecture.md](architecture.md).

## Inserts

LV2 and native Linux VST3 plugins in the signal path. Each XLR input carries a mono chain;
Aux In, every application channel and each mix carry a stereo chain. An Inserts row
under the channel or mix lists what is loaded: a green or red LED for
active or bypassed, a bypass button, and a gear that opens the plugin's
controls in their own window. The picker shows every installed LV2
plugin that fits the slot (mono for inputs, stereo for mixes), grouped
by category. The controls window is generated from the plugin's port
descriptions, with rotary controls, named enumeration menus, readable
units, EQ-band gain bars and dynamics threshold bars. These are parameter
overviews, not measured frequency/transfer responses or FFT spectra.
It is grouped by parameter family and has a Defaults button.
Chains are saved with the mixer and recalled by profiles.

Each LV2 plugin runs in its own daemon-owned native host with direct
PipeWire ports. Safety DSP (low cut and ClipGuard) remains in PipeWire's
filter-chain. Plugins are found in the standard LV2 directories
(`/usr/lib/lv2`, `~/.lv2`, or wherever `LV2_PATH` points); the daemon
reads them through lilv. `lsp-plugins-lv2` is the set used during
development. **Native plugin UI…** opens the installed X11 UI on the
same instance that processes the channel. LSP's native EQ spectrum,
compression history and gain-reduction meters therefore show real audio.
Native UI parameter changes return to OpenXLR and are saved with the mixer.
Closing the editor leaves DSP running; a crashed host is rebuilt by the
daemon's sweep. Native UI hosting needs X11 or XWayland and a session display
available to the user service. Unsupported required LV2 features are reported;
other UI toolkits and non-control-port plugin state are not hosted yet.
The fork additionally hosts Linux VST3 effects in `openxlr-vst3-host`, with
normalized controls, component/controller state, native X11 editors, reported
latency and compatible auxiliary input buses. CLAP remains unsupported.
The header's Plugin Manager shows scan errors and offers rescan, retry and
unquarantine. Chain and plugin presets are reusable across compatible slots.
See [parity-status.md](parity-status.md) for the remaining acceptance gaps.

The submixer can be switched off in Options. The daemon then controls
the hardware only, restarts itself, and leaves the sound card in its
stock PipeWire layout; mixes, virtual microphones and inserts go away
with it. For the Wave XLR Pro there is an experimental ALSA UCM profile
in `packaging/ucm/` that splits the raw 17/18-channel card into named
PipeWire devices (Monitor, Line 1 to 3, XLR 1, XLR 2) for that mode, or
for running without OpenXLR. It is a manual root install with a
matching revert script and is not shipped by any package. While the
submixer runs, the daemon parks the card on its pro-audio profile and
restores the split profile when it stops.

## Application routing

- Audio clients are detected from their PipeWire client registration
  and assigned to a channel by name rules; each assignment is stored in
  the app registry and can be edited while the app is silent
- Electron apps report "Chromium" as their application name; they are
  identified by their process binary instead, so Discord appears as
  Discord
- A Manage dialog shows the full registry, and an installed-application
  picker pre-assigns channels from `.desktop` entries
- The Flow graph puts a channel picker on every running application node,
  so an app can be assigned while its signal path is visible

## Profiles

Named scenes: every hardware setting plus the whole submix (send
levels, mutes, masters, monitor outputs, the mix being listened to, aux state, insert chains with
their parameters). Saved per device and recalled from the header, over
the API, or from a Stream Deck key. App routing and the enforced system defaults are global and
not part of a profile, so recalling one does not rewire the desktop.

## OpenDeck plugin

`plugin/com.emaspa.openxlr.sdPlugin` is an
[OpenDeck](https://github.com/nekename/OpenDeck) plugin with three
actions: Mixer Key, Visual Level (key), and Mixer Dial. All are clients of the
daemon's WebSocket API, so they reflect changes made in the UI or on hardware.
Property inspectors load channels, mixes, output devices, profiles, and
inserts from live state. They therefore follow editable layouts instead of
offering only the original default ids.

Dials render a touch panel: a colour-coded knob with a needle, live level
meter, value readout, mute overlay, and stack position. Every send, mix master,
gain, headphone volume, and the crossfade is a dial target. One dial can hold
several targets, reorder them in the inspector, and cycle by tap or press.

Visual Level gives key-only decks the same live information as a dial: a
fader, input/mix meter, exact value, and mute state. A press can toggle mute,
set a fixed percentage, or adjust by a signed percentage. Percentages are
mapped to the target's real scale (including gain and headphone dB ranges).

![Dial panels](plugin-dials.png)

Mixer Keys render a button with an icon and a status LED: red for a mute,
and a stable per-target colour for an engaged feature, included route,
listened mix, or active monitor output. Every hardware switch and mute is a
key target, plus the software low cut
(its frequency shown on the LED, cycling Off, 80, 120), ClipGuard, gain
lock, selecting the listened mix, and independently adding/removing a monitor
output. Each key
can pick its icon, and a typed title replaces the built-in label.

![Keys](plugin-keys.png)

Profiles: a key can recall one of the active device's saved profiles,
listed live in the property inspector; it lights while that profile is
the last one recalled or saved.

Inserts: the property inspector lists every loaded plugin from live
state. A key toggles one insert's bypass (LED green in the path, red
bypassed) or a whole chain; a dial takes any control of any insert,
stepping along the control's own scale (log, integer, enumeration,
toggle), with its name and value on the panel and the insert's bypass
on the press. A key or dial follows its insert by id, and falls back to
the same plugin in the same chain when a profile recall rebuilds the
chain.

Install: download `com.emaspa.openxlr.sdPlugin.zip` from the
[latest release](https://github.com/emaspa/openxlr/releases/latest) and
use OpenDeck's install-from-file, or copy the plugin folder into
`~/.config/opendeck/plugins/` (a symlink breaks OpenDeck's asset
serving; the packages ship the folder in `/usr/share/openxlr/`). Touch
taps on the Stream Deck + XL need OpenDeck newer than 2.14.0
([nekename/OpenDeck#437](https://github.com/nekename/OpenDeck/pull/437)).

## Other

- Versioned, loopback-only integration API: HTTP discovery, health, state,
  LV2 catalog and correlated commands; a WebSocket event stream for state and
  15 Hz meters; an OpenAPI 3.1 schema and client examples. The legacy `/ws`
  socket remains compatible.
- Audio Flow window: an interactive graph of the current routing, sources through
  outputs, with the filter chains (built-in low cut and ClipGuard, LV2
  inserts) drawn where they sit in the path and each stage marked active,
  bypassed or broken
- Enforced defaults: the daemon re-asserts the chosen system default
  sink and source on its one-second sweep, undoing WirePlumber's
  auto-switch to newly created nodes
- Tray icon, start-minimized option, daemon and window autostart from
  Options
- Diagnostics archive: one action collects app and device state, a
  vendor block dump, the PipeWire graph, daemon logs and configs into a
  tarball for bug reports
