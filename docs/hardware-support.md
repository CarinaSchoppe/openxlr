# Hardware support

The state of every device OpenXLR supports, control by control. The
last two rows need owners; the section at the bottom explains how to
help.

| Device | USB id | Status |
|---|---|---|
| Wave XLR Pro | `0fd9:00b4` | every control verified on hardware |
| XLR Dock | `0fd9:00a6` | every control the hardware has, verified on hardware |
| Wave XLR | `0fd9:007d` | core controls verified on hardware by a community tester |
| Wave XLR MK.2 | `0fd9:00b6` | decoded from captures, not run on hardware |
| XLR Dock MK.2 | unknown | not supported; expected to share the MK.2's protocol, USB id needed |

## Wave XLR Pro (0fd9:00b4)

Vendor block protocol decoded and documented in
[wave-xlr-pro-protocol.md](wave-xlr-pro-protocol.md): config blocks for
both XLR inputs, headphone block, crossfade and output selectors, and
the commit block every selector write needs.

| Control | State | Notes |
|---|---|---|
| Gain 0 to 80 dB, mute (per XLR input) | verified | both inputs, independent structures |
| Low cut, expander, voice tune + strength | verified | per input |
| Phantom 48V, ClipGuard, compressor | verified | ClipGuard is an inverted byte in the protocol. The firmware mutes the input for about 13 s around every 48V change (anti-thump) and unmutes it itself; the UI counts the hold down on the mute button |
| Headphone volumes x2, low impedance | verified | independent jacks |
| Mic and PC crossfade | verified | direct monitor inside the device |
| Physical output routing | verified | HP1, HP2, Line Out, USB Aux; verified by listening on both jacks |
| USB Aux input level + lock, aux return | verified | return routing latches at stream open; the daemon bounces the stream |

## XLR Dock (0fd9:00a6)

The Stream Deck+ module. It has no onboard memory or DSP: Wave Link
runs its processing host-side on Windows. On Linux OpenXLR drives gain,
mute and headphone volume through the kernel's standard ALSA controls
and provides the DSP host-side in the submixer. Phantom power and
headphone low impedance live in firmware registers the kernel does not
expose, reached over the original Wave XLR's protocol dialect.

| Control | State | Notes |
|---|---|---|
| Gain 0 to 75 dB | verified | analog preamp; confirmed by level measurement |
| Mute, headphone volume | verified | standard ALSA controls |
| Low cut 80 / 120 Hz | software | PipeWire high-pass in the mic path; response measured with test tones as second-order |
| ClipGuard | software | hard limiter at -3 dB, measured with test tones; needs the `swh-plugins` package |
| Gain lock | software | the daemon rejects all gain changes while set; the dock has no physical dial to bypass it |
| Phantom power | verified | byte 6 of the dock's config block over the original Wave XLR's protocol dialect. Identified by [openwave PR #8](https://github.com/rikkichy/openwave/pull/8) on the MK.1 against its 48V LED; confirmed here with a condenser microphone on the dock's XLR. Wave Link does not write it for the dock |
| Low impedance | verified | byte 33 of the same config block, verified by listening on the dock's headphone jack |
| Hardware sidetone | not present | no control path found; a byte sweep came back negative |

Kernel behaviour: the kernel starves the dock's capture endpoint when
playback to it starts first, and the mic records silence. OpenXLR
ships a WirePlumber rule
([packaging/50-xlr-dock-capture-hold.conf](../packaging/50-xlr-dock-capture-hold.conf))
that keeps the capture source always active, so playback can never
start first.

## Wave XLR (0fd9:007d)

The original MK.1. Its class protocol was documented by the
[openwave](https://github.com/rikkichy/openwave) project, and a
community tester has run OpenXLR against real hardware.

| Control | State | Notes |
|---|---|---|
| Gain, mute | verified | community tester; scale is 256 raw units per dB ([openwave PR #8](https://github.com/rikkichy/openwave/pull/8) measured it on the shared protocol) |
| Headphone volume, low impedance | verified | community tester |
| Phantom 48V | coded | config byte 6, found by [openwave PR #8](https://github.com/rikkichy/openwave/pull/8) against the MK.1's own 48V LED; the same byte is verified on the XLR Dock. Added after the tester's run, so an LED check on a MK.1 is still open |
| Low cut, voice DSP, crossfade | unmapped | the hardware has them; their offsets are unknown. A [USB capture](usb-capture.md) from an owner would map them |

## Wave XLR MK.2 (0fd9:00b6), needs a tester

Decoded from USB captures of Wave Link, using the Pro's protocol family
at its own address. Not run on hardware.

The XLR Dock MK.2 for the Stream Deck+ is built on the same Wave FX
platform (80 dB gain, phantom, ClipGuard 2.0, onboard expander, voice
tune, compressor, EQ). Its USB id is not known to this project. The
MK.1 Wave XLR and the first XLR Dock share one protocol across two ids;
if the MK.2 pair does the same, support is a registry entry away. Open
an issue with your `lsusb` output.

| Control | State | Notes |
|---|---|---|
| Gain, mute, low cut, expander, voice tune + strength | coded | from capture analysis |
| Headphone volume, low impedance, crossfade | coded | from capture analysis |

## Every device gets

- Capability-driven UI: controls, channels, and mixes the device does
  not have are not shown
- Per-device profiles: named scenes of hardware state plus the whole
  submix, recalled from the UI or the API
- Multi-device switching: a header picker chooses which interface
  OpenXLR drives; the mixer's input channels follow it
- On switch, the hardware channels' monitor sends come up muted, so the
  newly patched mic does not reach the speakers until unmuted
- OpenDeck plugin: every switch, mute, and level on a Stream Deck, with
  live state

## Own an MK.2? Help confirm it

The Wave XLR MK.2 (0fd9:00b6) backend is complete and waiting for a
first run on real hardware; the XLR Dock MK.2 needs an owner to report
its USB id (`lsusb`, look for `0fd9:`). Testing takes a few minutes:

1. Install OpenXLR per the [README](../README.md), including the udev
   rule.
2. Try gain, mute, and headphone volume against what the device itself
   shows.
3. In the app: Options, then SUPPORT, then Collect diagnostics.
4. Open an [issue](https://github.com/emaspa/openxlr/issues) with the
   archive and what you observed.

MK.1 owners who can record a Wave Link USB capture on Windows can map
the rest of their device: low cut, the voice DSP, and the crossfade
exist in the hardware and need their registers found (phantom is
already coded, from the openwave project). The
[USB capture guide](usb-capture.md) walks through it in about 15
minutes, no programming needed.
