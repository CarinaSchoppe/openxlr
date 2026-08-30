# Hardware support

The current, honest state of every device OpenXLR supports. The last
two rows need owners; the section at the bottom explains how to help.

| Device | USB id | Status |
|---|---|---|
| Wave XLR Pro | `0fd9:00b4` | 🟢 full support, every control verified on hardware |
| XLR Dock | `0fd9:00a6` | 🟢 supported and verified within what its hardware can do |
| Wave XLR | `0fd9:007d` | 🟢 core controls verified on hardware by a community tester |
| Wave XLR MK.2 | `0fd9:00b6` | 🟡 decoded from captures, needs a tester |
| XLR Dock MK.2 | unknown | ⚪ same Wave FX platform as the MK.2; an owner's `lsusb` output is the missing piece |

## Wave XLR Pro (0fd9:00b4)

The daily driver behind this project. Vendor block protocol fully decoded
and documented in [wave-xlr-pro-protocol.md](wave-xlr-pro-protocol.md):
config blocks for both XLR inputs, headphone block, crossfade and output
selectors, and the commit block every selector write needs.

| Control | State | Notes |
|---|---|---|
| Gain 0 to 80 dB, mute (per XLR input) | verified | both inputs, independent structures |
| Low cut, expander, voice tune + strength | verified | per input |
| Phantom 48V, ClipGuard, compressor | verified | ClipGuard is an inverted byte in the protocol; handled. The firmware mutes the input for ~13 s around every 48V change (anti-thump) and unmutes it itself; the UI counts the hold down on the mute button |
| Headphone volumes x2, low impedance | verified | independent jacks |
| Mic and PC crossfade | verified | zero-latency direct monitor inside the device |
| Physical output routing | verified | HP1, HP2, Line Out, USB Aux; ear-verified on both jacks |
| USB Aux input level + lock, aux return | verified | return routing latches at stream open; the daemon bounces it |

## XLR Dock (0fd9:00a6)

The Stream Deck+ module. A software-defined device with no onboard memory
or DSP: Wave Link is its brain on Windows, so on Linux OpenXLR drives
gain, mute, and headphone volume through the kernel's standard ALSA
controls and provides the DSP host-side in the submixer. The exceptions
are phantom power and headphone low impedance, which live in firmware
registers the kernel does not expose, reached over the original Wave
XLR's protocol dialect.

| Control | State | Notes |
|---|---|---|
| Gain 0 to 75 dB | verified | real analog preamp, confirmed by level measurement |
| Mute, headphone volume | verified | standard ALSA controls |
| Low cut 80 / 120 Hz | software | PipeWire high-pass in the mic path; tone-measured, textbook second-order response |
| ClipGuard | software | hard limiter at -3 dB, tone-measured exact; needs the `swh-plugins` package |
| Gain lock | software | the daemon rejects all gain changes while set; the dock has no physical dial to bypass it |
| Phantom power | verified | byte 6 of the dock's config block, spoken over the original Wave XLR's protocol dialect. Identified by [openwave PR #8](https://github.com/rikkichy/openwave/pull/8) on the MK.1 against its 48V LED; confirmed here with a condenser mic on the dock's XLR. Wave Link never writes it for the dock, so on Linux OpenXLR is the only way to switch it |
| Low impedance | verified | byte 33 of the same config block, ear-verified on the dock's headphone jack |
| Hardware sidetone | not present | no control path exists; a byte sweep came back negative |

Linux quirk, solved: the kernel starves the dock's capture when playback
to it starts first, and the mic records silence. OpenXLR ships a
WirePlumber rule
([packaging/50-xlr-dock-capture-hold.conf](../packaging/50-xlr-dock-capture-hold.conf))
that keeps the capture source always active, fixing it system-wide.

## Wave XLR (0fd9:007d)

The original MK.1. Its class protocol comes proven from the
[openwave](https://github.com/rikkichy/openwave) project's users, and a
community tester has since run OpenXLR against real hardware.

| Control | State | Notes |
|---|---|---|
| Gain, mute | verified | community tester; scale is 256 raw units per dB ([openwave PR #8](https://github.com/rikkichy/openwave/pull/8) measured it on the shared protocol) |
| Headphone volume, low impedance | verified | community tester |
| Phantom 48V | coded | config byte 6, found by [openwave PR #8](https://github.com/rikkichy/openwave/pull/8) against the MK.1's own 48V LED; the same byte is verified live on the XLR Dock. Added after the tester's run, so one LED check still closes it |
| Low cut, voice DSP, crossfade | unmapped | the hardware has them; their offsets are unknown. A [USB capture](usb-capture.md) from an owner would map them |

## Wave XLR MK.2 (0fd9:00b6), needs a tester

Decoded from USB captures of Wave Link, using the Pro's protocol family
at its own address. Never run against real hardware.

A sibling exists: the XLR Dock MK.2 for the Stream Deck+, built on the
same Wave FX platform (80 dB gain, phantom, ClipGuard 2.0, onboard
expander, voice tune, compressor, EQ). Its USB id is not yet known to
this project; today's MK.1 pair turned out to share one protocol across
two ids, and the MK.2 pair very likely does the same. Own one? Open an
issue with your `lsusb` output and support is probably a small step
away.

| Control | State | Notes |
|---|---|---|
| Gain, mute, low cut, expander, voice tune + strength | coded | from capture analysis |
| Headphone volume, low impedance, crossfade | coded | from capture analysis |

## Every device gets

- Capability-driven UI: controls, channels, and mixes the device does not
  have simply do not appear
- Per-device profiles: named scenes of hardware state plus the whole
  submix, recallable from the UI, the API, or a Stream Deck key
- Multi-device switching: a header picker chooses which interface OpenXLR
  drives; the mixer's input channels follow it
- Safety on switch: monitor sends come up muted when the input device
  changes, so a hot mic can never howl through the speakers
- OpenDeck plugin: every switch, mute, and level on a Stream Deck, with
  live state and Wave Link style touch panels

## Own an MK.2? Help confirm it

The Wave XLR MK.2 (0fd9:00b6) is fully coded and waiting for its first
real device, and the XLR Dock MK.2 just needs an owner to report its
USB id (`lsusb`, look for `0fd9:`) before it can join. Testing takes a
few minutes and risks nothing that a replug does not fix:

1. Build and run OpenXLR from the [README](../README.md) install steps,
   including the udev rule.
2. Try gain, mute, and headphone volume against what the device itself
   shows.
3. In the app: Options, then SUPPORT, then Collect diagnostics.
4. Open an [issue](https://github.com/emaspa/openxlr/issues) with the
   archive and what you observed.

MK.1 owners who can record a Wave Link USB capture on Windows unlock the
rest of their device: low cut, the voice DSP, and the crossfade are
present in the hardware and just need their registers mapped (phantom is
already coded, credit to the openwave project). The
[USB capture guide](usb-capture.md) walks through it in about 15
minutes, no programming needed.
