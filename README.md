# OpenXLR

Native Linux control suite for Elgato XLR interfaces: full hardware
control over reverse-engineered USB protocols, a Wave Link style
PipeWire submixer with per-application channels, virtual microphones,
LV2 plugin inserts, multi-output monitoring, a dedicated mix for a
second computer on the USB Aux port, and an OpenDeck plugin for Stream
Deck control.

![OpenXLR mixer](docs/screenshot-mixer.png)

Elgato ships no Linux software. These devices enumerate as
class-compliant USB audio interfaces, so audio flows out of the box.
Gain, DSP, phantom power, output routing and the hardware mixer only
answer to vendor protocols, which this project reverse engineered from
USB captures of Wave Link and reimplemented from scratch.

Not affiliated with or endorsed by Elgato. Built by protocol analysis on
the author's own hardware.

## Supported devices

| Device | USB id | Status |
|---|---|---|
| Wave XLR Pro | 0fd9:00b4 | full support, verified on hardware |
| XLR Dock (Stream Deck+ module) | 0fd9:00a6 | gain, mute, headphone volume, 48V phantom power, low impedance; verified on hardware |
| Wave XLR | 0fd9:007d | gain, mute, headphone volume, low impedance, 48V phantom power; verified on hardware by community testers |
| Wave XLR MK.2 | 0fd9:00b6 | gain, mute, phantom power, DSP, ClipGuard, compressor, headphone volume, crossfade; verified on hardware by a community tester |
| XLR Dock MK.2 (Stream Deck+ module) | 0fd9:00c7 | registered on the MK.2 backend (its USB descriptor matches the MK.2's); needs a tester |

The UI shows only the controls the connected device has, and a picker
in the header switches between several attached interfaces. The
per-control state of every device is in
[docs/hardware-support.md](docs/hardware-support.md). Own an untested
device? Open an issue with a diagnostics archive (Options, SUPPORT,
Collect diagnostics).

## Features

- **Hardware control** over the vendor USB protocol. On the Pro: gain,
  mute, low cut, expander, voice tune and phantom power per input,
  ClipGuard, compressor, aux input level and lock, two headphone
  volumes with low-impedance mode, the mic/PC crossfade, and the
  physical output routing (HP1, HP2, Line Out, USB Aux). The other
  devices expose the subset their protocol has; see the table above.
  Devices without onboard DSP get a software low cut, ClipGuard and
  gain lock in the PipeWire layer instead.
- **Submixer** built from PipeWire nodes (null sinks, remap sources,
  filter chains), no kernel modules. Hardware inputs plus user-managed
  application channels that can be added, renamed, removed, or reordered; Monitor and Aux plus any number of user-managed
  output mixes published as virtual microphones; per-send levels and
  mutes, level meters, reordering, several monitor devices at once, and
  one-click listening to any output mix through those devices.
- **Inserts**: independent LV2 chains on every hardware/application channel
  and output mix. Open the native LSP X11/XWayland editor on the actual DSP
  instance for live EQ spectra and compression meters; edits persist with
  the mixer. Generated unit-aware controls and bypass remain available.
  See the [native host contract](native/README.md) for supported LV2 features.
- **Recovery**: systemd restarts unexpected exits and enforces a 60-second
  watchdog deadline; the UI reconnects without blocking or replaying edits.
  Individual LV2 hosts are also isolated and supervised for crashes/hangs.
  The header and Options always offer **Restart daemon**. Diagnostics include
  service restart/watchdog state, recent audio-stack logs and UI-session events.
- **Update notices**: an asynchronous, optional GitHub check at UI startup
  shows a newer release's notes or newer development commits. About and the
  update window identify the repository, build revision and release/snapshot
  status. Nothing is installed or uploaded automatically.
- **Application routing**: audio clients are detected from their
  PipeWire registration and routed to a channel by name rules, with the
  assignment remembered per app. Electron apps are identified by their
  process binary rather than the "Chromium" name they report. Routing can
  also be changed directly on the application nodes in the Flow window.
- **Profiles**: named scenes holding the hardware settings and the
  whole submix (levels, mutes, outputs, insert chains), saved per device
  and recalled from the UI, the API or a Stream Deck key.
- **OpenDeck plugin**: live mixer keys, visual level keys, and encoder
  actions for every switch, route, mute, mix, level, monitor destination,
  profile, and insert. Editable channels and mixes are discovered from the
  daemon rather than frozen into the plugin. Faders, meters, values, coloured
  states, and mute overlays reflect changes made in the UI or on hardware.
- **Daemon, UI and integration API**: the daemon owns the device and the graph, keeps
  running with the window closed, re-asserts the chosen default sink
  and source once a second, and serves a loopback-only, versioned HTTP and
  WebSocket API on 127.0.0.1:37890. State, live meters, effects, routing and
  every edit command are available to third-party local software, with
  correlated acknowledgements and a bundled OpenAPI 3.1 schema. The UI has a
  routing graph view, a tray icon and a diagnostics archive exporter.

The full feature list, area by area: [docs/features.md](docs/features.md).

### Development snapshots and remaining work

The editable layouts, native LSP integration and recovery/update additions on
`CarinaSchoppe/openxlr:main` are fork development work, not an upstream release.
Installing the upstream AUR package or an older upstream release does not
automatically include those changes. CI package artifacts belong to their
displayed commit; check the build identity in About.

The workflow aims at Wave Link-style routing, not complete Elgato Wave 3/Wave
Link feature parity. Hardware inputs, Monitor/Aux and the last application
channel are protected. Application fan-outs no longer advertise duplicate
playback devices. Internal PipeWire buses are labelled by role and excluded
from OpenXLR's device pickers; low-level graph tools and some desktop mixers
still expose the underlying routing stages. They are not duplicate user channels.
The concrete Wave Link comparison and roadmap are listed near the end of this
README. Planned items are not claimed as implemented.

Verification includes 147 automated .NET tests, 12 offline acceptance-driver
tests, 7 OpenDeck plugin tests, real LSP editor/audio tests, installed-package runtime checks on
Ubuntu/Fedora/Arch, and a CachyOS live service-recovery test. Reproduction
commands, measured results and tested limitations are recorded in
[docs/verification.md](docs/verification.md).

### Stream Deck / OpenDeck plugin

Dials get a touch panel with a coloured knob, live meter, value, mute overlay,
and stack position; one dial can hold several reorderable targets, cycled by
tap or press. Key-only decks get a Visual Level action with a real fader and
meter; a press can toggle mute, set a percentage, or apply a positive/negative
percentage step.

![Dial panels](docs/plugin-dials.png)

Mixer keys show an icon and a status LED (red for a mute, coloured for an
engaged feature, route, listened mix, or active monitor output). Every current
channel/mix pair is loaded from live state, so renamed and newly created
entries appear automatically. Separate output keys add/remove one device
without discarding other monitor outputs.

![Keys](docs/plugin-keys.png)

## Install

**Arch Linux** (AUR):

```sh
yay -S openxlr        # or: paru -S openxlr
systemctl --user enable --now openxlr-daemon
openxlr               # the mixer UI, also in your application menu
```

To package this checkout on **Arch/CachyOS** instead of installing an older AUR
release, install the dependencies listed in `packaging/arch/PKGBUILD`, then run
`bash tools/build-arch-package.sh` as a normal user and install the generated
`dist/openxlr-*.pkg.tar.zst` with `sudo pacman -U`. The script packages **committed
HEAD**, checksums the source archive, and keeps its temporary build directory
for inspection. It does not install or enable a service itself.

**Ubuntu** 24.04 or newer: download the `.deb` from the
[latest release](https://github.com/emaspa/openxlr/releases/latest), then

```sh
sudo apt install ./openxlr_*_amd64.deb
systemctl --user enable --now openxlr-daemon
openxlr
```

**Fedora** 44 or newer: download the `.rpm` from the
[latest release](https://github.com/emaspa/openxlr/releases/latest), then

```sh
sudo dnf install ./openxlr-*.x86_64.rpm
systemctl --user enable --now openxlr-daemon
openxlr
```

**NixOS**: the repo is a flake with a package and a module. The module
enables the daemon itself; after a rebuild, `openxlr` is in the
application menu.

```nix
{
  inputs.openxlr.url = "github:emaspa/openxlr";
  # in your NixOS configuration:
  imports = [ openxlr.nixosModules.default ];
  services.openxlr.enable = true;
}
```

On every distribution, replug the interface once after installing so
the udev rule applies. For the Stream Deck, install
`com.emaspa.openxlr.sdPlugin.zip` from the release with OpenDeck's
install-from-file, or copy the folder the package puts in
`/usr/share/openxlr/` into `~/.config/opendeck/plugins/`. Inserts show
whatever LV2 plugins are installed (`lsp-plugins-lv2` is the set used
during development); the software ClipGuard for the XLR Dock needs
`swh-plugins`. The NixOS module wires both up itself.

### Build from source

Needs the .NET 10 SDK, PipeWire with its CLI tools, libusb, and lilv
(package names per distribution in
[docs/install-from-source.md](docs/install-from-source.md)).

```sh
git clone https://github.com/emaspa/openxlr.git
cd openxlr/src
dotnet build -c Release
OPENXLR_BUILD_MIXER=1 ./OpenXLR.Daemon/bin/Release/net10.0/OpenXLR.Daemon   # terminal 1
./OpenXLR.UI/bin/Release/net10.0/OpenXLR.UI                                 # terminal 2
```

Fork builders can pass `-p:OpenXLRUpdateRepository=CarinaSchoppe/openxlr` to
`dotnet build`/`publish`. Otherwise local source builds check upstream; GitHub
Actions uses `GITHUB_REPOSITORY`. Source archive builders set
`OPENXLR_BUILD_REVISION` to the originating 40-character commit. Development
commits are announced separately from stable releases, never as a fake version
bump. Disable startup network checks in Options, UPDATES.

Device access needs the udev rule from `packaging/70-openxlr.rules`
installed under `/etc/udev/rules.d/` and a replug. The XLR Dock also
needs the WirePlumber rule from `packaging/`. Running the daemon as a
user service, the sysctl port reservation, updating and uninstalling:
[docs/install-from-source.md](docs/install-from-source.md).

## Documentation

- [Manual](docs/manual.md): first run, the concepts behind the mixer,
  step-by-step tasks, the Stream Deck plugin, troubleshooting
- [Features](docs/features.md): every control, the submixer, inserts,
  routing, profiles and the OpenDeck plugin in detail
- [Installing from source](docs/install-from-source.md): prerequisites
  by distribution, device access, the user service, updating,
  uninstall, environment variables
- [Local integration API](docs/api.md): versioned HTTP/WebSocket resources,
  OpenAPI schema, examples, the command set, and files under `~/.config/openxlr`
- [Architecture](docs/architecture.md): daemon, UI and plugin, the
  PipeWire graph, the device protocols, repository layout
- [Hardware support](docs/hardware-support.md): per-control status of
  every device
- [Wave XLR Pro protocol](docs/wave-xlr-pro-protocol.md): the vendor
  protocol as reverse engineered, with offsets
- [USB capture guide](docs/usb-capture.md): how to capture Wave Link
  traffic for an untested device

## Reporting problems

Open Options, then SUPPORT, then Collect diagnostics. It writes
`~/openxlr-diagnostics-<timestamp>.tar.gz` with the app and device
state, a raw vendor-block dump, the PipeWire graph, daemon logs and
configs. Nothing gets uploaded; attach the archive to an issue yourself.

## Status

Developed and used daily by the author with a Wave XLR Pro, an XLR
Dock and a Stream Deck + XL. The Wave XLR MK.2 backend is written from
USB captures and has not been run on hardware; see the device table
for how to help.

The majority of the code was produced by the author, with AI tooling
(Anthropic's Claude) assisting with protocol capture analysis, UI design
and parts of the coding. Every hardware finding was verified live on a
real device.

## Wave Link 3 comparison and roadmap

The comparison baseline is Elgato's current
[Wave Link app page](https://www.elgato.com/us/en/s/wave-link-app) and
[Wave Link 3.0 overview](https://www.elgato.com/us/en/explorer/products/wave/wave-link-3-0-software-overview/),
not the older Wave:3 microphone name. Wave Link 3 advertises up to five output
mixes, eight software channels, four hardware inputs, effects on inputs,
multiple apps per channel and unlimited monitor destinations. OpenXLR already
covers the Linux equivalents with editable application channels, any number of
virtual output mixes, per-send submixes, multi-output monitoring, LV2 inserts,
profiles, Flow routing, OpenDeck control, and listening to any mix. Linux uses
LV2 rather than Wave Link's VST3/AU plugin formats.

Highest-value remaining work, in priority order:

1. **Flexible external inputs** — add arbitrary microphones, capture cards and
   other PipeWire sources as managed hardware channels instead of limiting
   structural inputs to the active Wave interface.
2. **Sound-check recorder** — capture a short microphone sample and loop it
   through the active insert chain while tuning EQ, dynamics and noise control.
3. **Faster routing workflows** — a compositor-neutral foreground-application
   shortcut and generated OpenDeck layouts for frequently changed routes.
4. **Fuller plugin hosting** — LV2 worker/state/atom support, more native UI
   toolkits, file-backed plugin state and reusable insert-chain presets.
5. **First-run and appearance polish** — guided setup, explicit light/dark
   themes, localization and per-channel hide/show without deleting a channel.
6. **Smaller native PipeWire graph** — replace the remaining Pulse-compatible
   routing buses while preserving the currently tested public device names and
   recovery behaviour.

These are roadmap items, not promises in the current build. Elgato-specific
cloud effects and the Windows/macOS VST3/AU ecosystem are not presented as
Linux features until a technically maintainable equivalent exists. The
complete, checkbox-based comparison—including device setup, Sound Check,
output matrices, effects, updates, and every current Stream Deck action—is in
[TODO.md](TODO.md).

## License

[GPL-3.0](LICENSE). If you find OpenXLR useful, consider
[buying me a coffee](https://buymeacoffee.com/emaspa).
