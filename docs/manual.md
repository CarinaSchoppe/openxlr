# OpenXLR manual

How to use OpenXLR day to day: what it changes on your system, the
concepts behind the mixer window, step-by-step tasks, and what to do
when something does not work. For installing, see the README; for the
full list of controls per device, [hardware-support.md](hardware-support.md);
for scripting, [api.md](api.md).

## 1. First run

OpenXLR is two programs. The daemon (`openxlr-daemon`, a systemd user
service) talks to the interface over USB, builds the PipeWire mixer and
keeps running whether or not a window is open. The mixer window
(`openxlr`) shows and changes what the daemon holds; closing it changes
nothing.

When the daemon starts with the submixer on (the default), these things
happen on your system:

- New audio devices appear in your desktop's sound settings, all named
  `OpenXLR …`: one output per hardware or application channel and one
  virtual microphone per output mix. The initial layout includes the six
  application channels plus `OpenXLR Stream` and `OpenXLR Chat`; it can be
  changed from Channels & outputs.
- Applications that play audio are moved onto a channel output by name
  (section 2). They keep playing; only the device they play into
  changes.
- Your system default output and input are left as they were. The
  daemon remembers them at start and puts them back if the session
  manager switches to one of the new devices in the following seconds.
  If you set defaults in Options (section 3.7), those are held instead.
- On the Wave XLR Pro the daemon parks the card on its pro-audio
  profile while it runs, so the raw multichannel device is available to
  the mixer, and restores the previous profile when it stops.

The window's header shows the connected interface with a green dot.
"No device" means the daemon cannot open the interface: replug it once
after installing so the udev rule applies (section 5.1).

If you only want hardware control and no mixer, turn the submixer off
in Options (section 3.8). The daemon restarts in hardware-control mode
and the `OpenXLR …` devices disappear.

## 2. Concepts

**Channels** are where audio enters the mixer. Three structural channels carry the
interface's inputs (XLR 1, XLR 2 where the device has one, Aux In for
the Pro's Line In and USB Aux input) and six carry application groups:
Game, Music, Browser, System, Voice Chat, SFX initially. Application
channels can be added, renamed, or deleted. Each channel is a PipeWire output device
an application can play into.

**Mixes** are where audio leaves. Monitor and Aux are structural. Stream
and Chat are the initial user-managed virtual outputs:

| Mix | What it is | Where it goes |
|---|---|---|
| Monitor | what you hear | the outputs ticked in the MONITOR card |
| Stream | what your audience hears | the `OpenXLR Stream` virtual microphone, for OBS or any recorder |
| Chat | what your call partners hear | the `OpenXLR Chat` virtual microphone, for Discord, Zoom and the like |
| Aux | what a second computer receives | the interface's USB Aux port (Wave XLR Pro only) |

Every channel has a **send** into every mix: a level and a mute. The
SUBMIXER card shows them as a grid, channels down, mixes across. Each
mix also has a **master** level and mute. A typical setup keeps Music
loud in Monitor and lower in Stream, and out of Chat entirely.

**Application routing**: when an app starts playing, the daemon reads
its name from PipeWire and picks a channel by rules: browsers to
Browser; Spotify, YouTube Music and media players to Music; Discord,
Zoom, Slack and other chat apps to Voice Chat; Steam, Lutris, Heroic
and games to Game; anything else to System. Electron apps report
"Chromium", so the process name is used instead, which is how Discord
lands in Voice Chat. An app you move to another channel is remembered
(section 3.3).

**Profiles** are named scenes: the interface's hardware settings plus
the whole submixer (sends, masters, monitor outputs, aux state, insert
chains). They are saved per interface. Application routing and the
system default devices are not part of a profile, so recalling one
does not rewire the desktop.

**Inserts** are LV2 plugins placed in the signal path: a mono chain on
each XLR input, a stereo chain on each mix. The XLR Dock and the
original Wave XLR have no onboard DSP, so on those OpenXLR also offers
a software low cut and ClipGuard on XLR 1.

## 3. Tasks

### 3.1 Send your microphone to a call or a recording

1. In the SUBMIXER card, make sure XLR 1 is unmuted in the Stream and
   Chat columns.
2. In the other application, choose the microphone: `OpenXLR Chat` for
   Discord, Zoom, Teams; `OpenXLR Stream` for OBS or a recorder.
3. Everything you add to those mixes (a game in Stream, music at a low
   level) reaches the same virtual microphone. What you hear yourself
   comes from the Monitor mix, which is separate.

Options has a tip for step 2: enforcing `OpenXLR Chat` as the system
default input (section 3.7) makes every voice app pick it up without
configuration.

### 3.2 Choose what you hear and how loud

1. In the MONITOR card, tick every device the Monitor mix should play
   on: your speakers, a headset, or several at once. On the Wave XLR
   Pro its own outputs (Headphones 1, Headphones 2, Line Out) appear
   here too; ticking one switches the hardware's output routing.
2. The Volume slider sets the level of the selected devices.
3. The HEADPHONES card holds the interface's own headphone volume,
   low-impedance mode, and on the Pro the Mic ↔ PC crossfade, which is
   the zero-latency direct monitor inside the device: left is only your
   microphone, right is only computer audio.

### 3.3 Put an application on a different channel

The APPLICATIONS card lists every app that is currently registered with
PipeWire as an audio client; a green light means it is playing.

1. Change the channel in the dropdown next to the app. The move happens
   immediately and is remembered for that app.
2. To pre-assign an app that has not played yet, open Manage…, pick it
   from the installed-application list, choose a channel and press Add.
   The identity is guessed from its launcher; if the app reports a
   different name on first play it shows up as a new entry.
3. Forget, in the same window, drops an app and its remembered channel.

The same dropdown is available on every application node in Flow. To add
or add, rename, or remove application channels and output mixes, use Channels & outputs in
the SUBMIXER card. A new output immediately gets a master, a send on every
channel, an insert chain, and a selectable `OpenXLR <name>` virtual
microphone. Deleting one removes its PipeWire devices; deleting a channel
moves its assigned apps to the first remaining application channel. The
owned graph is rebuilt briefly for either structural change.

An app that is missing from the card is not registered with PipeWire
as a client. That happens with some applications until they start
playing.

### 3.4 Feed a second computer over USB Aux (Wave XLR Pro)

1. Connect the second computer to the Pro's USB Aux port. It sees the
   Pro as a plain USB audio device.
2. In the SUBMIXER card, set the sends into the Aux mix: typically your
   microphone and the game, without the second computer's own chat.
3. Tick "To USB Aux port" on the Aux mix. The interface's audio stream
   restarts once, which interrupts playback for a moment; the device
   only picks up the new routing at stream start.

The USB Aux *input* (what the second computer sends back) is the Aux In
channel, with its level and lock in the INPUTS card.

### 3.5 Add a plugin to the signal path

1. Under XLR 1, XLR 2 or a mix, press "Add plugin…". For a program channel,
   open its card (also possible from Flow), then **Inserts… → Add plugin…**.
   The picker lists
   the installed LV2 plugins that fit the slot (mono for an input,
   stereo for a mix), searchable by name or category. `lsp-plugins-lv2`
   is the set used during development. Unsupported plugin requirements are reported.
2. Add. The plugin appears in the Inserts row with a green light while
   active.
3. Controls opens a window generated from the plugin's parameters. EQs
   and dynamics get band-gain/threshold overviews; continuous values use rotary
   controls with readable units, enumerations show their named choices,
   and large plugins are grouped. Defaults restores the declared values.
   Knobs support vertical dragging, the mouse wheel, arrow keys, Home and End.
   The bars reflect control settings, not a live FFT or measured response.
   Bypass takes it out of the path
   (red light); the arrows reorder the chain; the cross removes it.
4. Chains are saved with the mixer and with profiles.

For LSP, use **Native plugin UI…** in the controls window. This opens the
manufacturer's editor with live EQ/compressor analysis on the same DSP
instance that processes your audio. Native control changes are saved;
closing its window does not remove the effect. The button requires an
active insert, an X11 UI in the plugin metadata and a matching daemon.
XWayland is supported; GTK/Qt-only vendor UIs are not. If the user service
has no display, the opening error is shown without stopping the audio.
File-based plugin state (for example loaded impulse responses) is not
persisted by this control-port host. VST and CLAP plugins cannot be loaded.

### 3.6 Save and recall a scene

1. Set everything the way you want it: hardware controls, sends,
   masters, monitor outputs, inserts.
2. Header, Profiles: type a name and press Save.
3. To recall: Profiles, then the name. To remove: the cross next to it.

Profiles belong to the interface they were saved with; another device
shows its own list. With the OpenDeck plugin a key can recall a
profile (section 4).

### 3.7 Hold the system default devices

Session managers like to switch the system default output to a newly
appeared device, and some applications follow that default. In Options,
SYSTEM DEFAULT DEVICES, choose the output and input OpenXLR should
hold; it re-asserts them once a second and reverts any outside change.
"(don't enforce)" leaves the system alone.

### 3.8 Hardware control only

Options, Submixer: off. The daemon restarts in hardware-control mode:
the sound card keeps its stock PipeWire layout (on the Pro, its UCM
profile where one is installed), and the `OpenXLR …` devices, mixes,
virtual microphones and inserts go away. The INPUTS and HEADPHONES
cards keep working. Turn it on again the same way.

### 3.9 Start at login, tray

Options, STARTUP:

- "Start the daemon at login" enables the daemon's systemd user
  service. On a packaged install this is the package's own unit.
- "Start the mixer UI at login" adds an autostart entry for the window.
- With "Close button minimizes to tray", the window hides instead of
  quitting; the tray icon's menu shows it again or quits. "Start
  minimized to tray" opens the window hidden.

The window also remembers which of its sections (INPUTS, HEADPHONES,
MONITOR, APPLICATIONS, SUBMIXER) you collapsed with the chevron in
their header, across restarts.

### 3.10 Upgrade

Packages do not restart a running daemon. After an upgrade the window
shows a banner naming the daemon's version and its own, with a Restart
daemon button; press it, or run

```sh
systemctl --user restart openxlr-daemon
```

Until then the window offers only the controls the old daemon reports.
Toggling the submixer in Options also restarts the daemon.

### 3.11 Automatic daemon recovery and editable cards

The shipped systemd user unit restarts unexpected exits after three seconds.
Its watchdog deadline is 60 seconds without a healthy heartbeat. Every 20
seconds the daemon checks that its device and mixer loops have progressed
within the last 30 seconds. A frozen process is restarted automatically; a
blocked worker can take approximately 60–90 seconds to trigger recovery.
Unplugging a device does not count as a hang. An explicit `systemctl --user
stop openxlr-daemon` stays stopped. No UI window is required for monitoring.
Source builds launched directly in a terminal do not have a service manager
and therefore cannot restart themselves.

Restart requests from the UI are asynchronous. During an outage the UI
reconnects automatically; unconfirmed edits are reported, never silently
replayed. Install/reload the updated service unit as well as the daemon binary
to enable the watchdog (`systemctl --user daemon-reload`, then restart).

Open **Channels & outputs…** to add, rename and delete application channels
and virtual output mixes. Hardware inputs, Monitor, Aux and the last app
channel are protected. Wait for “Layout saved.”; structural edits briefly
rebuild the graph and are saved before confirmation. Deleted-channel apps
move to the first remaining application channel. Deleted sends and inserts
are removed; an enforced default pointing at a deleted output is cleared.

**Edit channel** on a card, or clicking a channel in **Flow**, opens its own
send editor. Each fader/mute affects only that channel in that mix. In Flow,
an application's picker assigns it to a channel and stays open during routine
state updates. Deleted/disabled channels cannot be edited in stale windows.
If “matching daemon build” is shown, the installed daemon lacks these API
features: install the matching daemon, not just a newer UI.

Developer verification and the opt-in audio-interruption test are documented
in [verification.md](verification.md).

## 4. Stream Deck (OpenDeck)

The plugin has two actions. Both are clients of the daemon and show
its live state, so what a key displays is what the mixer window shows.

**Toggle** (a key) switches one thing: a hardware control (mute,
phantom, low cut, expander, voice tune, ClipGuard, compressor, low
impedance, the Pro's output selectors, the aux level lock, the gain
lock), the software low cut (cycling Off, 80, 120), a mix or send mute,
the monitor output (switching the Monitor mix to one specific device),
the bypass of one insert or of a whole chain, or a profile to recall.
The key's LED is green for an engaged feature, red for a mute, and grey
when the daemon is offline or the target does not exist on the
connected interface. A key's icon can be chosen in its settings, and a
title typed there replaces the built-in label.

**Dial** (an encoder) changes a level: the monitor output volume, a
gain, a headphone volume, the aux level, the crossfade, a mix master, a
channel's send into one mix or into all mixes, or one control of an
insert. The touch strip shows a knob, a level meter, the value and a
mute overlay; pressing the dial mutes (or, for a gain, mutes the input;
for the crossfade, recentres). A dial can hold several targets, cycled
by tap or press as chosen in its settings.

Installing: the plugin zip from the release through OpenDeck's
install-from-file, or the folder the package ships in
`/usr/share/openxlr/` copied into `~/.config/opendeck/plugins/`
(copied, not linked; OpenDeck does not serve assets through a symlink).
Restart OpenDeck after installing or updating the plugin.

## 5. Troubleshooting

### 5.1 "No device" in the header

- The interface must be replugged once after installing so the udev
  rule (`/usr/lib/udev/rules.d/70-openxlr.rules`) applies to it.
- `lsusb` should list an `0fd9:` device. If it does but the header
  still says no device, look at the daemon's log:
  `journalctl --user -u openxlr-daemon -n 50`. "present but could not
  be opened" is the udev rule not applied yet.
- With more than one supported interface attached, the header shows a
  picker; the mixer's input channels follow the chosen one.

### 5.2 Microphone silent on the XLR Dock

The kernel starves the dock's capture when playback to it starts before
capture, and the microphone records silence. The package installs a
WirePlumber rule that keeps the dock's capture source always active
(`50-xlr-dock-capture-hold.conf`). On a source install copy it from
`packaging/` into `~/.config/wireplumber/wireplumber.conf.d/` and
restart WirePlumber.

### 5.3 Daemon does not start after an upgrade, or after a reboot

- `systemctl --user status openxlr-daemon` shows the state. "203/EXEC"
  in a restart loop means a stale unit in
  `~/.config/systemd/user/openxlr-daemon.service` written by a version
  before 0.1.9; opening the mixer window once repairs it, or remove the
  file, `systemctl --user daemon-reload`, then
  `systemctl --user enable --now openxlr-daemon`.
- "port 37890 busy": another program holds the daemon's API port. The
  packages reserve it from the kernel's ephemeral range; the daemon
  waits for it rather than failing.

### 5.4 ClipGuard greyed out, empty plugin picker

- The software ClipGuard needs the SWH LADSPA plugins (`swh-plugins`).
  Without them the control is disabled and its tooltip says so; the
  rest keeps working.
- The insert picker lists what lilv finds in the standard LV2
  directories (`/usr/lib/lv2`, `~/.lv2`, or `LV2_PATH`). An empty
  picker means no LV2 plugins are installed, or lilv is missing.

### 5.5 Sound comes out of the wrong device

The session manager switched the system default when a new device
appeared. Set the defaults in Options (section 3.7), or pick the device
you want in your desktop's sound settings once; the daemon defends the
defaults it saw at start only for the first seconds.

### 5.6 A control changes in the window but not on the device

The daemon writes to the interface and reads the state back; if the
device ignores the write, the control snaps back. On the Wave XLR Pro
the mute button shows a countdown after every 48V change: the firmware
holds that input muted for about 13 seconds and unmutes it itself. On
other devices this would be new information: collect diagnostics
(section 5.8) and open an issue.

### 5.7 The daemon froze, or a control hung the window

Since 0.1.11 a USB transfer that never returns fails after a few
seconds instead of stalling the daemon; the device is dropped and
reconnected after 10 seconds, and the fault is recorded. Collect
diagnostics afterwards (section 5.8): the archive contains the exact
transfer, and that is what makes the report actionable.

### 5.8 Reporting a problem

Options, SUPPORT, Collect diagnostics. It writes
`~/openxlr-diagnostics-<timestamp>.tar.gz` with the daemon's state and
capabilities, a dump of the interface's vendor blocks, the PipeWire
graph and device listings, the recent daemon journal, the
configuration files and version information. The home path, host name
and the serial numbers of attached USB devices are redacted; review the
archive anyway before attaching it to a public issue. Nothing is
uploaded automatically.

## 6. Files and services

| Path | What it is |
|---|---|
| `~/.config/openxlr/mixer.json` | every mixer decision, written by the daemon |
| `~/.config/openxlr/profiles/<vid-pid>/<name>.json` | saved profiles, one file each |
| `~/.config/openxlr/daemon.json` | the submixer on/off preference |
| `~/.config/openxlr/gainlock.json` | which devices have the gain lock set |
| `~/.config/openxlr/ui.json` | window preferences |
| `openxlr-daemon.service` (systemd user unit) | the daemon; `journalctl --user -u openxlr-daemon` for its log |
| `ws://127.0.0.1:37890/ws` | the daemon's API, documented in [api.md](api.md) |

Uninstalling a package leaves `~/.config/openxlr` in place; remove it
by hand if you want a clean slate.
