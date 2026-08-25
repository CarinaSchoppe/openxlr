# Wave XLR + Stream Deck on Linux: research knowledge base

Snapshot date: 2026-08-24, addendum 2026-08-25 (section 8, which supersedes several claims below — read it first). All repo stats and README claims were verified on these dates and will drift. Purpose: hand this to another Claude instance (or future session) as the full state of research into replacing the Elgato Wave Link + Stream Deck stack for a Windows-to-Linux workstation migration. Read section 7 before asserting anything about what does or does not exist in this ecosystem.

## 1. Context and goal

- User: Emanuele (GitHub: emaspa). Senior IT background, 30 years experience, active OSS developer. Has .NET 8 / Avalonia experience (Linux port of Thermal Grizzly WireView Pro II) and an extensive homelab.
- Migrating his workstation from Windows to Linux. Already solved: RGB via frugalrgb, hardware dashboards via infopanel-linux. Remaining gap: the Elgato audio/streaming stack.
- Hardware: Elgato Wave XLR (revision unconfirmed, see section 6) and an Elgato Stream Deck.
- Current Windows state: Wave Link 3.2.6 (pinned; 3.2.8.3928 crashes with 0xc0000005 in coreclr.dll).
- User's stated position: considers the original OpenWave buggy and unmaintained; was leaning toward building his own control layer. Research below narrowed that to two small build items (section 5).

## 2. What Wave Link actually does (the replacement target)

Not just a hardware control panel. The core value is the virtual submixer: per-application channels (Game, Chat, Music, Browser, System, SFX), two-plus independent mixes (monitor mix = what the user hears; stream mix = what OBS/the audience gets), per-channel volume and mute per mix, mic channel routing, and virtual devices other apps can select. On Windows this requires a virtual audio driver. On Linux, PipeWire is natively a routing graph, so the equivalent is null sinks + pw-loopback + WirePlumber policy, no driver work.

## 3. Ecosystem survey (verified by fetching each repo)

### 3.1 rikkichy/openwave (upstream)

- https://github.com/rikkichy/openwave. MIT. Python 3.10+, GTK4/libadwaita, raw libusb via ctypes.
- Stats at snapshot: 17 stars, 41 commits, 3 contributors, 2 open issues, 1 open PR, 2 forks, single release v1.0.0 (2026-05-25).
- Scope: device controls only. Mic gain, mute (bidirectional sync with hardware button), headphone volume (synced with knob), low impedance mode, 10 Hz polling for hardware sync, system tray (StatusNotifierItem over D-Bus), first-run udev/polkit setup, and an "audio capture fix" background daemon (systemd or runit) that holds the mic stream open to dodge a firmware race that otherwise drops capture to silence.
- USB protocol (the crown-jewel knowledge, reverse-engineered via Frida against the macOS Wave Link binary): the original Wave XLR is USB Audio Class 1, VID:PID 0fd9:007d, configured via vendor control transfers on endpoint 0. snd-usb-audio blocks wIndex=0x3300 (routes through interface 0, owned by the audio driver). The trick: send wIndex=0x3303 instead. Firmware only validates the 0x33 prefix; the kernel sees interface 3 (unclaimed) and permits the transfer. No driver detach, audio uninterrupted.
- Architecture: device.py (USB backend), app.py (GTK4 UI + polling), tray.py, audio.py (capture keepalive), daemon.py (service entry), setup.py.

### 3.2 CryoByte33/openwave (fork) - THE KEY FIND, current best option

- https://github.com/CryoByte33/openwave. Fork of the above, 59 commits (18 ahead of upstream at snapshot). Stats: 1 star, 0 forks, no releases, has a tests/ directory and CI workflows.
- Adds a full Wave-Link-style submixer on top of upstream's device controls:
  - Three mixes: Personal (what you hear), Chat, Record. Per-source fader for each mix plus a master fader that scales all three (GoXLR channel model).
  - Virtual mics: Chat and Record mixes are published as capture devices named "OpenWave Chat" and "OpenWave Record", selectable by Discord/OBS like a normal microphone.
  - Source groups (e.g. a "Games" group sharing one set of levels), channel strips with live meters, mute, drag-to-reorder.
  - MK.2 support: the Wave XLR MK.2 is 0fd9:00b6, USB Audio Class 2, different control scheme (standard class requests, wIndex=0x0203), implemented as a separate backend with automatic detection of which device is plugged in.
- Mixing mechanics: each app's stream is moved onto its own PipeWire null sink; a pw-loopback per mix carries that sink's monitor into the mix at the fader's volume (pulling a fader removes the source from that mix only). Chat/Record are themselves null sinks with monitors published as capture devices. Mic is read straight from hardware; Personal feeds the headphones.
- Architecture (notably better separated than upstream): device.py (both USB backends), devicecontroller.py (connect/poll/reconnect off the UI thread), mixmatrix.py (channel-strip widget), mixer.py (submix engine), routing.py (pure function: sources + levels in, a routing plan out, which the mixer diffs and applies), sources.py (channels, groups, stream-to-source matching), pipewire.py (single adapter over pw-loopback/pw-cli/wpctl), audio.py + daemon.py (keepalive), setup.py + service.py, tray.py.
- CRITICAL GOTCHA: the fork's README still points both the curl install one-liner and the git clone instructions at rikkichy/openwave. Following its own install docs installs upstream, without any of the mixer. Must clone the fork and run ./install.sh from the checkout.
- Unknown whether upstream's 1 open PR is this work. If never merged, the superior version lives indefinitely in a 1-star fork.

### 3.3 PipeWeaver (evaluated, then superseded for this use case)

- https://github.com/pipeweaver/pipeweaver. PipeWire-based audio management for streaming/broadcasting: virtual sources, physical source attachment, matrix mixing, complex mute arrangements, routing to physical/virtual outputs. Web UI plus an API for external control. Requires PipeWire >= 1.4.0 (earlier versions had latency problems with UCM devices in the routing tree). Explicitly pre-release: settings may reset between code changes.
- Companion: DeckWeaver (github.com/designgears) - a Linux Stream Deck plugin for PipeWeaver: volume with configurable step, mute toggle, device selection across sources/targets, hardware output reassignment, real-time visual feedback on keys.
- DECISION: superseded by the CryoByte33 fork, whose Personal/Chat/Record model maps to Wave Link more directly than PipeWeaver's matrix. DO NOT run both. Two processes creating null sinks and loopbacks against the same PipeWire graph will fight. DeckWeaver remains valuable as a reference implementation for plugin shape only.

### 3.4 Stream Deck hosts

- OpenDeck (nekename): the pick. Cross-platform (Linux/Windows/macOS), Tauri-based, runs the majority of original Elgato Stream Deck plugins including Windows-only ones via Wine. Plugin API: OpenAction.
- Alternatives: StreamController (GTK4, own plugin ecosystem, polished), deckmaster (muesli; TOML config, buttons run commands, no GUI), streamdeck-ui (older Python), Boatswain (GNOME).
- Fallback integration path requiring zero new code: deckmaster-style buttons shelling out to wpctl/pactl.

### 3.5 Adjacent projects (genre references, not part of the stack)

- goxlr-utility (GoXLR-on-Linux): the architectural model for this entire niche. Headless daemon owning the hardware, stable API, interchangeable frontends. Credited as inspiration in OpenWave's README.
- alsa-scarlett-gui + kernel drivers (Geoffrey Bennett): gold standard. GTK4 control panel for Focusrite Scarlett/Clarett/Vocaster with the drivers (scarlett2, FCP for 4th gen) upstreamed into the kernel. Companion CLI: fcp-support/fcp-tool.
- Wave Reborn (Lukuoris): another Wave Link alternative, Python/FastAPI web UI, appears PulseAudio-API based. Not investigated deeply.
- pulsemeeter: Voicemeeter replication for Linux.
- Genre peers with useful protocol-capture documentation (usbmon/Wireshark workflow): OpenRazer, Solaar, HeadsetControl, rivalcfg, OpenRGB, ckb-next.
- Elgato Key Lights (HTTP over LAN, mDNS _elg._tcp, no USB reverse engineering): keylight-control (Electron), elgato-keylight (Rust CLI).

## 4. Architecture decisions already made

- No orchestrator. Independent components talking over standard interfaces; each survives the others' churn. Rejected: any central daemon owning mixer + deck + device together.
- Chosen stack: CryoByte33 OpenWave fork (device control + submixer) + OpenDeck (plugin host) + PipeWire native audio path. The Wave XLR appears in the graph twice conceptually: as a plain USB audio device (mic in, headphone out; needs no custom code) and as a USB control target (needs the fork).
- Control plane vs audio plane kept separate: the control software must never sit in the audio path.
- Scene switching (one button sets mixer + mic + lights) lives in Stream Deck profile config, not a new daemon. Build last, if at all.

## 5. Remaining build items (only two, both small)

1. A D-Bus control interface on the OpenWave fork. Mixer and device state currently live inside the GTK process; tray.py already establishes D-Bus plumbing (StatusNotifierItem), so adding a control interface in-process is the pragmatic path. Expose get/set for device controls and per-source per-mix faders, plus a change signal. Design the API before any UI work; both consumers below are clients of it. Plausible as an upstream PR to the fork rather than a fork-of-a-fork.
2. An OpenAction plugin for OpenDeck mapping keys/dials to that API, with state feedback on keys (gain readout, mute state). DeckWeaver is the shape reference. Estimated a few hundred lines.

Explicitly no longer needed (earlier plan, obsoleted by the fork): a new wavexlr daemon, a new panel UI, any PipeWeaver integration.

## 6. Open items and unknowns

- Which Wave XLR revision the user owns: MK.1 (0fd9:007d) or MK.2 (0fd9:00b6). Check with lsusb. Determines which backend matters and which protocol any further captures target.
- Hardware direct-monitor blend (the zero-latency mic monitoring mix): not confirmed as exposed in either repo's documented feature set. May require further protocol capture. Losing it means mic monitoring goes through PipeWire with real latency.
- Interrupt endpoint: unknown whether the XLR exposes one for button/knob events. If yes, the 10 Hz polling loop can be replaced with event-driven sync. Uninvestigated.
- Whether the fork's submixer is solid in daily use. 1 star, no releases, untested by the user.
- Whether the fork merges upstream.
- Robustness of sources.py stream-to-source matching under Proton/Wine, where all games tend to report as the wine binary. This is the classic weak point of per-app routing on Linux. Untested.
- TIME-SENSITIVE: protocol capture against Wave Link 3.2.6 on the user's existing Windows install (Frida and/or USBPcap: monitor blend, init sequence, anything neither repo covers). The reference implementation becomes inconvenient to access once the machine migrates. Do this before wiping Windows.

## 7. Epistemic warnings for the next instance

- During this research, "X does not exist" was asserted three times and was wrong within one turn each time: (1) a Wave Link-style Linux mixer supposedly did not exist, then PipeWeaver was found; (2) OpenWave was characterized as a control panel only with a structurally wrong keepalive, then the CryoByte33 fork was found with a full submixer and a clean reconciler design; (3) the entire build plan (daemon + panel + plugin) was scoped before checking the fork network. Lesson: fetch the repo, its fork list, and its commit count before characterizing it. Search results and cached READMEs lag, and this niche moved fast through 2026.
- All stats and feature lists here come from README fetches, not from running the code. Nothing in this document is validated on hardware yet.
- Do not recommend running the fork's curl install one-liner (installs upstream, see 3.2).

## 8. Session 2 addendum (2026-08-25) hardware identified, Windows config exfiltrated, host decided

This section supersedes sections 3.4 (host choice rationale), 4, 5, and 6 where they conflict. Everything here was verified on the live Linux machine (CachyOS, PipeWire 1.6.8), on the mounted Windows partition (`/dev/nvme1n1p3`, mounted ro at `/mnt/windows`), or by two research agents that cloned and grepped the actual repos on 2026-08-25.

### 8.1 The hardware is NOT what section 6 assumed

`lsusb` on the live machine:

- **Elgato Wave XLR Pro, 0fd9:00b4**, a THIRD revision, neither MK.1 (007d) nor MK.2 (00b6). USB Audio Class 2, `Rev=04.10`, serial AAY3H5481241GS. Interfaces: If0 AudioControl **with a 6-byte interrupt endpoint (1ms)**, but the Pro has no physical controls (see below), so this is not user-input events; if used at all it likely carries metering/clip or state-echo notifications; If1/If2 audio streaming; **If3 vendor-specific (class 0xFF), zero endpoints, no kernel driver**, the control plane, targetable by control transfers without detaching audio (same structural trick as MK.1/MK.2).
- **Stream Deck + XL, 0fd9:00c6**, one physical device: 36 LCD keys (9x4), 6 encoders, touchscreen strip, single plain HID interface. Sits on a "USB Dock for Stream Deck +" (0fd9:00ac) which is a USB billboard device only, no audio function.
- Also present: Facecam Pro (0079), Sound BlasterX Katana (USB speakers, the Windows monitor output).

Working out of the box on Linux with zero custom code: Wave XLR Pro audio (multichannel source + sink in PipeWire), and, big find, **mic gain as a standard UAC2 ALSA control**: `amixer -c Pro` exposes a 0–80 dB capture volume (currently 52 dB, exactly matching the Windows Wave Link gain setting of 0.6933×75) plus capture mute switches. Hardware gain/mute need no reverse engineering; only phantom power, low-cut, ClipGuard, and monitor blend live behind the vendor interface. Per Emanuele (2026-08-25): the Wave XLR Pro has NO LED/cosmetic controls and NO physical controls at all (no knob, no mute button), it is operated entirely from software / a Stream Deck. The LED and dial-function settings in the Windows MixerConfiguration.json (XLR General Color, Mute Color, brightness, flipped, Microphone Dial Function) belong to the old Elgato XLR Dock, not the Pro. Do not look for LED or knob/button commands in the 00b4 protocol. Consequences: (a) the interrupt endpoint on the AC interface is not for user-input events, if it carries anything, it's likely metering/clip/gain-reduction or state-echo notifications; (b) upstream OpenWave's hardware-sync features (button/knob polling) are irrelevant for the Pro; (c) on Linux the D-Bus API + OpenDeck plugin are the SOLE control surface, not a convenience, build items 2 and 3 in section 8.5 are hard requirements for daily use, and control state lives wholly in our software (no device-initiated changes to reconcile, multi-client sync happens over D-Bus signals rather than device polls).

### 8.2 No Linux software supports 00b4 hardware control (verified absent, not just unfound)

- rikkichy/openwave (HEAD 7602875, 2026-08-18): PID table has only 0x007D and Wave:3 0x0070 (added via PR #3, 2026-08-05). No MK.2, no Pro. Issue #5 requests "mk2/pro" DSP support, no PID, no takers. Issue #6 gives the XLR Dock MK.2 PID: 0fd9:00c7 (not our dock; ours is the audio-less 00ac billboard).
- CryoByte33/openwave (HEAD a9cd7ed, 2026-06-29): `openwave/device.py` has PID_MK1=0x007D, PID_MK2=0x00B6 only. **The MK.2 PR to upstream (rikkichy #4) was closed UNMERGED by CryoByte33 himself on 2026-06-25** ("I'll open another later"). No releases, 1 star, still the best code.
- GitHub code search for "0fd9:00b4" and "Wave XLR Pro": zero hits anywhere. No kernel quirks for any Wave device. Other Wave tools (jacobtread/waver, titaniumtraveler/tidal-wave, DuskyProjects/WaveLinux, a Rust Wave-Link clone, last push 2026-08-21) all target 007d only.
- Protocol intel: **LukasParke/wave3-research** (Wave Link teardown + native Wave:3 daemon) proves the Pro exists in Wave Link as `LWT::WaveXLRProDevice`, LWT = Lewitt, Elgato's audio OEM (the Pro's DSP resembles a Lewitt CONNECT 6, per oddbear). Wave Link ships three vendor backend strategies (LegacyUAC1, LegacyUAC2, MK2VendorUSBBackendStrategy); which one the Pro uses is unknown, no Pro-specific strategy symbol appears, plausibly MK.2-style standard class requests, unverified. Nobody has published a byte-level capture of the Pro. It is a fresh reverse-engineering target, and the Windows install (still intact, dual-boot) is the reference implementation, the section 6 TIME-SENSITIVE capture item is now the critical path, with USBPcap/Frida against Wave Link targeting 00b4.
- The CryoByte33 submixer runs fine without a supported device: `Mixer().start()` runs before device detection, connect failure just greys the device pane and polls for hotplug. Mic/headphone pickup is by ALSA node-name substring "Wave_XLR" (`openwave/pwnames.py:40`), which the Pro's node name should match (unverified caveat: `mixer.refresh_device` fires only on recognized-PID connect, so a Pro's mic/HP loopbacks are found at app start only, not on replug).

### 8.3 Stream Deck host: OpenDeck, decisively (supersedes any StreamController leaning)

- **OpenDeck v2.14.0 (2026-07-29) supports the Stream Deck + XL natively**: release notes name it; dependency elgato-streamdeck 0.13.1 (maintained by OpenDeck's own author under OpenActionAPI org) has `PID_STREAMDECK_PLUS_XL = 0x00c6`, `Kind::PlusXl` = 36 keys 9x4, 6 encoders, 100x1200 LCD strip, 120x120 JPEG keys. Encoder events (dialRotate/dialDown/dialUp), touchTap, and per-encoder LCD segment rendering all implemented and actively iterated (v2.13.x–2.14.0). 2,074 stars, monthly release cadence through 2026.
- StreamController: + XL support landed ~2026-08-11..16 only in 1.5.0-beta.16 via their forked python lib; dial/touch support has open bugs (#447 dials not shown on first load, #169 touchscreen swipes dead) and the maintainer owns no dial hardware (#294). Wrong choice for a dial-heavy + XL workflow.
- OpenDeck runs original Elgato SDK plugins: JS ones natively (requires Node >= 20), Windows-only ones via Wine per-plugin (unreliable; OS-integration plugins like BarRaider WinTools/Audio Switcher are structurally useless on Linux, issue #309 closed not-planned). **No .sdProfile/ProfilesV3 import exists**, profiles must be rebuilt by hand, which is why the export below matters.

### 8.4 Windows config exfiltrated (the replication spec)

Exported to `~/wavexlr-opendeck/windows-export/` (Windows partition can be unmounted): `WaveLink/MixerConfiguration.json` + `AppRoutingInfo.json`, all 26 Stream Deck profiles (ProfilesV3, with key images), BackgroundPacks, icon-pack list (539MB of Marketplace packs not copied, re-downloadable), and a generated `profile-inventory.md` for the two profiles that target the current deck (device model 20GBX9901 = Stream Deck + XL; 20GBD9901 = old Stream Deck +, 20GBA9901 = old 15-key deck, their profiles are historical duplicates).

Wave Link mixer state (the audio replication target): channels Mic ("Elgato XLR Dock", Windows-side name; gain 0.6933≈52dB, low-cut on, ClipGuard on, phantom off, mic muted in local mix), Browser (Chrome), Game (Steam/Hearthstone/KCD2/Expedition 33/Bloodlines 2/Discord voice, local+stream at 0.5), Music (Spotify, 1.0), Voice Chat, System (0.6), Aux1/Aux2/SFX. Three mixes: local monitor → Katana speakers at 0.55, stream → "Stream Output" virtual device, microphoneFX. Blacklisted apps list in AppRoutingInfo.json.

Stream Deck + XL daily-driver functionality to replicate (from profile-inventory.md; ignore the imported Mac-authored marketplace pages with /Users/data_science2 soundboard paths and Voicemod actions): Wave Link control on keys AND dials (wavecontrol, channellevel, mixlevel, mainoutputdevice, audioeffect, addtochannel), volume-controller input/output device dials, Discord suite (mute/deafen/voice-channel/soundboard/stream/video toggles), HWiNFO sensor tiles (infopanel-linux territory), world clocks, Spotify + YouTube Music (ytmdc) transport, Key Lights (Control Center) + Govee on/off, screenshot keys, Google Meet suite (Chrome), window-mover layouts, weather forecast dial.

### 8.5 Revised build plan (supersedes section 5)

0. **FIRST, before wiping Windows: USB protocol capture of Wave Link driving the Wave XLR Pro** (USBPcap and/or Frida, targeting vendor transfers on 00b4 interface 3): init sequence, gain/mute/phantom/low-cut/ClipGuard writes (no LEDs, the Pro has none), monitor-blend, and the interrupt endpoint traffic. Windows partition intact at /dev/nvme1n1p3.
1. Add a 00b4 backend to the CryoByte33 fork's device.py (start from the MK.2 backend; teardown evidence hints the Pro is MK.2-style-or-newer). Meanwhile the fork's submixer + plain UAC2 audio + ALSA gain control already cover most of Wave Link's value.
2. D-Bus control interface on the fork (unchanged from section 5).
3. OpenAction plugin for OpenDeck against that API (unchanged), now knowing OpenDeck's encoder/LCD events are first-class for the + XL's 6 dials.

Resolved from section 6: revision = Wave XLR Pro 00b4 (new third option); interrupt endpoint exists (AC interface, 6 bytes/1ms); PipeWire 1.6.8 ≥ any requirement; fork did not merge upstream (PR closed unmerged). Still open: whether the Pro's iProduct-based node name matches the fork's "Wave_XLR" pickup (near-certain, untested); monitor blend location; fork's daily-use solidity; Proton stream-matching robustness.

## 9. Session 3 addendum (2026-08-25, same day) the 00b4 protocol is DECODED

Emanuele ran a single 11-minute USBPcap capture on Windows (`wavexlrpro.pcapng`, 7.4 GB, on the Windows desktop; some actions driven from the Stream Deck, irrelevant, they route through Wave Link to identical USB transfers). Decoded on Linux with tshark. Full write-up: `~/wavexlr-opendeck/wave-xlr-pro-protocol.md`; extracted transfers in `~/wavexlr-opendeck/capture-analysis/`.

Headline: the Wave XLR Pro speaks a THIRD protocol family, a paged property bank. All control is vendor transfers to interface 3: bmRequestType 0x41 (write) / 0xc1 (read), bRequest=1, **wIndex=0x0103**, wValue = block number. Blocks: 0x01 (108B config, R/W), 0x02 (150B telemetry, RO), 0x03 (12B, WO), 0x04 (80B config: 0–100 fader @off10, packed-flags byte @off1, 3-state enum @off2, R/W), 0x05 (8B: TWO one-byte level faders @off0 and @off2, range 0x00–0xf0, R/W), 0x06 (29B RO), 0x08 (96B RO). Wave Link polls all read blocks at ~17 Hz. Writes are whole-block read-modify-write. Not the MK.1 0x3303 trick, not the MK.2 0x0203 scheme, but the same unclaimed-interface-3 routing, so no driver detach.

Remaining gap: semantic labels for the toggle bits (block 4 off1 flags, block 1 offsets 12/13/24/30/39/48/90/91), the capture had no action log, so timeline windows exist but names are inferred. Fix: either Emanuele maps the recalled action order onto the event timeline, or a 2-minute one-control-at-a-time re-capture of just the toggles. The faders are unambiguous. This unblocks build item 1 (the 00b4 backend for the fork), it becomes a paged-register driver, simpler than either existing backend.

**Update (same session): control map largely resolved.** Aligning the event timeline to the capture-plan step order (Emanuele followed it ~99%, plus some unremembered mic tweaks): MIC GAIN = block 0x0004 off0, value = dB (0x00–0x50 = 0–80; gradual/dial-driven; also mirrored in the standard ALSA capture-volume, so use ALSA on Linux). MIC MUTE = block 0x0004 off1 bit0. Other mic toggles (phantom/low-cut/ClipGuard) = off1 bits1/4 + off2 enum (low-cut type). Output/monitor toggles (impedance/polarity/output-mute) = off1 bits 3/5/6/7. HEADPHONE VOLUME = block 0x0005 off0, MONITOR BLEND = block 0x0005 off2 (both 0x00–0xf0). MIC OUTPUT VOLUME = block 0x0004 off10 (0–100%). Block 0x0001 = extra mic DSP config (unremembered tweaks live here). The exact bit-within-group labels for the toggles are the only soft part; finalize by testing live against the device on Linux or a 2-min targeted re-capture. Full map: `~/wavexlr-opendeck/wave-xlr-pro-protocol.md` §4.

## 10. Direction change (2026-08-25) standalone product, not a fork

Emanuele decided NOT to ship this as a fork of CryoByte33/openwave. Rationale: with the Wave XLR Pro protocol now fully reverse-engineered and hardware-verified (§9, §0 of protocol.md), he is no longer dependent on the fork's incomplete Pro support. The goal is a cohesive, owned product: full Pro hardware control + a functional UI + an OpenDeck plugin with dial/touch support. The `WaveXLRPro` backend written into the fork (`~/wavexlr-opendeck/fork/`, diff in capture-analysis/) is retained as a PROVEN PROTOTYPE / protocol reference, not the shipping artifact.

Architecture (supersedes §4's "no orchestrator" stance, a headless daemon is now wanted, matching the goxlr-utility model §3.5): a headless daemon owns the Pro (vendor block protocol) + the PipeWire submixer + a stable control API; the UI and the OpenDeck plugin are independent clients of that API. Keeps control software out of the audio path; dial/touch support falls out because the plugin is just another API client. Openwave (MIT) and its MK.2 backend remain a permissively-licensed reference; the Pro protocol knowledge is Emanuele's own (independently captured). OPEN DECISION: implementation stack for daemon+UI (candidates: .NET8/Avalonia, his WireView Pro II Linux stack, one language across daemon+UI; Rust, genre gold standard for USB/PipeWire; Python/GTK, fastest, reuses fork knowledge). OpenDeck plugin is a separate JS/OpenAction sub-project regardless.

**Update: named OpenXLR, .NET 10 chosen, Core built + hardware-verified.** Product name LOCKED as **OpenXLR** (vendor-neutral on purpose, "XLR" is the generic connector, not Elgato's trademarked "Wave", so other interface brands can be added later behind the same daemon/UI/plugin). Stack: **.NET 10 + Avalonia** (Emanuele's WireView stack; .NET 10 is current LTS, .NET 8 EOL Nov 2026). `~/wavexlr-opendeck/src/` now holds `OpenXLR.Core` (device abstraction `IAudioDevice`/`DeviceCapabilities`/`DeviceRegistry` + `WaveXlrProDevice` over a libusb P/Invoke, read-modify-write blocks) and `OpenXLR.Probe`. Builds clean on .NET 10; VERIFIED ON HARDWARE, the C# layer detects the device via sysfs, reads full state, and round-trips gain/mute against ALSA. The openwave fork port (§9) is retained only as a reference prototype. Next: OpenXLR.Daemon (device + PipeWire submixer + control API) → Avalonia UI → OpenDeck JS plugin.
