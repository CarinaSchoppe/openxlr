# Features

The full feature list, by area. The README has the summary.

## Hardware control

Everything Wave Link exposes for the device. The full set, on the Wave
XLR Pro:
- Per-XLR input: gain (0 to 80 dB), mute, low cut, expander, voice tune with
  strength, phantom power, ClipGuard, compressor
- USB Aux input stage: level (0 to −60 dB) and level lock
- Both headphone outputs: independent volumes, low-impedance mode
- Mic ↔ PC zero-latency direct-monitor crossfade
- Physical output routing: each output (Headphones 1/2, Line Out, USB Aux)
  is switched in the device's own hardware mixer

On the others, what their protocols expose so far:
- Wave XLR MK.2: gain, mute, low cut, expander, voice tune with strength,
  headphone volume, low impedance, crossfade
- Wave XLR: gain, mute, headphone volume, low impedance, phantom power
- XLR Dock: gain, mute, headphone volume, driven through the kernel's
  standard ALSA controls, plus 48V phantom power and headphone low
  impedance over the original Wave XLR's protocol dialect, which the
  dock turns out to speak. The
  [openwave](https://github.com/rikkichy/openwave) project identified
  the phantom byte on the MK.1 against its 48V LED
  ([openwave PR #8](https://github.com/rikkichy/openwave/pull/8)); we
  confirmed the same register live on the dock with a condenser
  microphone. Wave Link itself never writes it for the dock, so this is
  a control the hardware has that the official software does not offer.
  The dock has no onboard DSP; Wave Link runs those effects host-side,
  so their Linux home is the submixer

## Software controls

For devices whose DSP lives host-side, OpenXLR provides the equivalents
in its PipeWire layer. They appear only when the active device lacks the
hardware version, so nothing is ever filtered twice:
- Low cut: a high-pass at 80 or 120 Hz (Wave Link's choices) inserted
  between the mic and its channel, cycled from a button on the XLR 1
  strip. Measured at the textbook second-order response and self-healing
  if its filter node ever dies
- ClipGuard: a hard limiter at -3 dB in the same filter chain, so a
  sudden shout cannot clip the recording (needs the swh-plugins LADSPA
  package)
- Gain lock: the daemon rejects every gain change while the lock is set,
  from any client, and remembers it per device across restarts. Shown
  only for devices without physical controls; a lock the hardware's own
  dial could bypass would be a lie

Two safety behaviors come with multi-device switching: the mixer's
input channels follow the active device, and a device switch brings the
hardware channels' monitor sends up muted, so a hot mic can never howl
through the speakers the moment it is patched in.

## Submixer

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

## Inserts

LV2 plugins in the signal path, the way Wave Link hosts VSTs. Each XLR
input carries a mono chain and each mix (Monitor, Stream, Chat, Aux) a
stereo one. An Inserts row under the channel or mix lists what is
loaded: a green or red LED for active or bypassed, a bypass button, and
a gear that opens the plugin's controls in their own window. The picker
shows every installed LV2 plugin that fits the slot (mono for inputs,
stereo for mixes), grouped by category. The controls window is generated
from the plugin's port descriptions, grouped by parameter family, with a
Defaults button. Chains are saved with the mixer and recalled by
profiles.

Every chain is a PipeWire filter-chain node, the same engine behind the
low cut and ClipGuard, so plugins run inside PipeWire's graph with no
extra process and add latency only while a chain has something in it.
Plugins are found in the standard LV2 directories (`/usr/lib/lv2`,
`~/.lv2`, or wherever `LV2_PATH` points); the daemon reads them through
lilv. `lsp-plugins-lv2` is a good first set (gate, compressor, EQ,
de-esser, limiter); `x42-plugins` and `calf` are also worth having.
Plugins that ship a custom GUI still work, you get the generated
controls instead of their window. VST and CLAP plugins cannot be loaded
yet; that needs a plugin host and is planned.

The submixer can be switched off in Options. The daemon then controls
the hardware only, restarts itself, and leaves the sound card in its
stock PipeWire layout; mixes, virtual microphones and inserts go away
with it. For the Wave XLR Pro there is an experimental ALSA UCM profile
in `packaging/ucm/` that splits the raw 17/18-channel card into named
PipeWire devices (Monitor, Line 1 to 3, XLR 1, XLR 2) for exactly that
mode, or for running without OpenXLR at all. It is a manual root
install with a matching revert script, not shipped by any package yet;
while the submixer runs, the daemon parks the card on its pro-audio
profile and puts the split back when it stops.

## Application routing
- OpenXLR detects every running audio-capable app through its PipeWire
  client registration and routes it to a channel by rules; it remembers
  each assignment, editable even while the app is silent
- Truthful names for Electron apps (Discord is "Discord", not "Chromium")
- Manage dialog with the full app registry and an installed-application
  picker to pre-assign channels from `.desktop` entries

## Profiles

Named scenes: every hardware setting plus the whole submix (send levels,
mutes, masters, monitor outputs, aux state, insert chains with their
parameters). Saved per device, recalled
from the header or over the API.
App routing and system defaults stay global on purpose: recalling a
scene never rewires the desktop.

## OpenDeck plugin

`plugin/com.emaspa.openxlr.sdPlugin` puts the whole rig on a Stream Deck
via [OpenDeck](https://github.com/nekename/OpenDeck), drawn like the
hardware it controls.

Dials get Wave Link style touch panels: a knob with a moving needle, a
live level meter, the value readout, and a mute overlay. Every send,
mix master, gain, headphone volume, and the crossfade is a dial target,
and one dial can hold a stack of targets cycled by tap or press.

![Dial panels](plugin-dials.png)

Keys are drawn as buttons on the same faceplate: the icon on a machined
cap, a status LED, red for a mute, green for an engaged feature or the
active monitor output. Every hardware switch and mute is a key target,
plus the software low cut (its frequency shown as LED digits, cycling
Off, 80, 120), ClipGuard, gain lock, and monitor-output switching to a
specific device. Each key can pick its icon (a headphone output shows
headphones, not a speaker), and a typed title replaces the built-in
label.

![Keys](plugin-keys.png)

Inserts are on the deck too. The property inspector lists every loaded
plugin from live state: a key toggles one insert's bypass (LED green
when in the path, red when bypassed, like the UI) or a whole chain at
once, and a dial takes any control of any insert, stepping along the
control's own scale (log, integer, enumeration, toggle), with its
name and value on the panel and the insert's bypass on the press. A
key or dial follows its insert by id, and falls back to the same
plugin in the same chain when a profile recall rebuilds the chain.

Everything reads and writes daemon state, so keys and dials reflect
changes made in the UI or on the hardware.

To install: download `com.emaspa.openxlr.sdPlugin.zip` from the
[latest release](https://github.com/emaspa/openxlr/releases/latest) and
use OpenDeck's install-from-file, or copy the plugin folder into
`~/.config/opendeck/plugins/` (a symlink breaks OpenDeck's asset
serving; the AUR package ships the folder in `/usr/share/openxlr/`).
Touch taps on the Stream Deck + XL need OpenDeck newer than 2.14.0
([nekename/OpenDeck#437](https://github.com/nekename/OpenDeck/pull/437),
merged upstream).

## Quality of life
- Live Audio Flow graph of the whole routing, sources through outputs
- The daemon holds your chosen system-default devices and re-asserts them
  every second, the way Wave Link does
- Tray icon, start-minimized option, autostart integration
- One-click diagnostics archive for bug reports
