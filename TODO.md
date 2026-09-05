# OpenXLR work list

This list tracks current gaps against **Elgato Wave Link 3.0 and its current
Stream Deck plugin**, as documented by Elgato on 4 September 2026. It is a
work list, not a claim that proprietary Elgato services or Windows/macOS plugin
formats belong in a Linux application.

Official comparison sources:

- [Wave Link 3.0 product and technical specifications](https://www.elgato.com/us/en/s/wave-link-app)
- [Wave Link 3.0 software overview](https://www.elgato.com/us/en/explorer/products/wave/wave-link-3-0-software-overview/)
- [Wave Link plugin for Stream Deck](https://www.elgato.com/ca/en/explorer/products/wave/wave-link-plugin-for-stream-deck/)

OpenXLR-specific integration work already completed beyond that comparison:

- [x] Publish a loopback-only v1 HTTP/WebSocket API for third-party software,
  with discovery, health, state, LV2 catalog, live meters, every control
  command, correlated results, bounded clients, OpenAPI 3.1 and examples.
- [ ] Add optional authenticated LAN access only after a threat model and an
  explicit opt-in UI exist; the default must remain loopback-only.

## Mixer and routing

The [checkpoint status](docs/parity-status.md) tracks the newer high-priority
implementation. Routing matrix, VST3, presets, scanner/manager, sidechain and
latency code is now present. Related boxes below remain open until the complete
audio, installed-package and CI acceptance criteria have passed.

- [ ] Add arbitrary PipeWire microphones, headsets, audio interfaces, capture
  cards, and other capture sources as managed input channels. OpenXLR currently
  builds hardware channels only for the active supported Wave interface.
- [ ] Mix inputs from several attached Wave interfaces simultaneously. The
  current device picker controls several devices but one interface supplies the
  active hardware-input layout.
- [ ] Finish acceptance of the implemented persistent many-to-many mix-to-output
  matrix, including shared hardware buses, hotplug, rollback and convergence.
- [ ] Add user-selectable icons for output mixes. Names, ordering, creation,
  deletion, and stable references are already implemented.
- [ ] Add a compact mix-matrix mode that keeps one selected channel visible.
  Main-window device sections can already collapse, but the submixer itself
  does not have Wave Link's dedicated compact layout.
- [ ] Add mono/stereo source policy controls and expose negotiated sample
  format/rate in the UI. Keep the current tested 48 kHz stereo graph as the
  compatibility default.

## Microphone and device experience

- [ ] Add a first-run device and routing tour with mic placement, headphone
  check, gain target, and an option to run it again.
- [ ] Add an Auto Gain wizard where the connected hardware exposes safe gain
  control.
- [ ] Add Sound Check: record a short mic sample, loop it through the current
  effect chain, and make before/after comparison explicit.
- [ ] Show firmware version and serial number in the device view, with privacy
  redaction retained in exported diagnostics.
- [ ] Add user-facing device aliases. Mixer channel and output names are
  already editable; physical device names are not.
- [ ] Implement the remaining device-specific LED controls when their USB
  registers are known: LED direction, state colours, and ClipGuard reduction
  indication. Do not guess vendor bytes without captures and hardware tests.
- [ ] Research a maintainable Linux equivalent to Voice Focus noise and echo
  reduction. Do not label a generic gate or denoiser as Elgato Voice Focus.

## Effects and presets

- [ ] Complete the LV2 host contract: worker, state, atom/event ports, file
  properties, more native UI toolkits, and safe plugin-requested resize.
- [ ] Add reusable effect-chain presets, per-plugin presets, copy/paste between
  channels, and A/B comparison. Preset storage/UI/API and VST3 state are present;
  complete LV2 asset state and A/B remain open.
- [ ] Add configurable LV2 search paths, rescan, quarantine, and a plugin
  manager that reports newly found or rejected plugins.
- [ ] Add a Linux effect browser/installer with a trustworthy package source.
  Elgato Marketplace and its VST3/AU downloads are proprietary ecosystem
  services and are not presented as an implemented Linux feature.
- [ ] Add optional auto-scaling for native plugin editor windows.
- [ ] Complete native Linux VST3 editor, state, sidechain and crash acceptance
  across multiple vendors; validate yabridge with actual Windows effects.
- [ ] Verify Wave FX USB send/return endpoints on compatible hardware before
  enabling the hardware insert capability. The XLR Dock has no verified pair.
- [ ] Implement explicit raw/hardware/plugin/full processing taps and per-send
  FX selection with persistence, profiles and measured isolation.
- [ ] Complete latency compensation at every convergence, including sidechains,
  channel paths, quantum/rate changes, and unsupported latency reporting.
- [ ] Replace remaining gapful effect-chain rebuilds with measured click-free
  transitions, preserving plugin state and graph cleanup.

## Stream Deck and OpenDeck

- [x] Discover editable channels, output mixes, monitor devices, profiles, and
  LV2 inserts from live daemon state instead of hard-coded default names.
- [x] Keep keys valid across channel/mix renames by storing stable ids; show a
  deleted or unavailable target explicitly instead of silently controlling a
  different channel.
- [x] Provide Wave Link-style live controls: routing/mute/effect/profile keys,
  a visual level key with fader and meter, and encoder controls with value,
  meter, mute overlay, colour, and reorderable stacks.
- [x] Add `Listen to mix` keys and independently toggled monitor-output keys
  that preserve other selected output devices.
- [x] Support mute, fixed percentage, and relative percentage modes on visual
  level keys; keep dial rotation smooth and state-driven.
- [ ] Add `route focused application to channel`. Reliable support needs a
  desktop-portal strategy for Wayland plus an X11 fallback; shelling out to one
  compositor-specific focus command is not acceptable production behaviour.
- [ ] Add generic PipeWire output-device volume and mute, rather than only Wave
  headphone registers and the currently monitored output's volume.
- [ ] Add a main-system-output action and two-device toggle integrated with
  OpenXLR's enforced-default sink setting.
- [ ] Once the many-to-many output matrix exists, let a key add/remove an
  output from a specific mix or toggle one output between two mixes.
- [ ] Add optional smooth fades for set/adjust level keys and actions that lock
  an effect explicitly on/off instead of only toggling it.
- [ ] Generate starter layouts from the current channel/mix registry. OpenDeck
  does not import proprietary Elgato profiles, so generation must use its own
  supported profile format.
- [ ] Validate on additional OpenDeck-supported key-only decks and mobile
  clients; current hardware validation focuses on Stream Deck + XL.

## Application polish, updates, and help

- [ ] Add System/Light/Dark appearance selection. The current UI stays readable
  on light desktops but intentionally uses one dark mixer presentation.
- [ ] Add localization infrastructure and translated UI strings.
- [ ] Add per-channel hide/show without deleting routing and effects.
- [ ] Add stable/beta update channels and opt-in automatic package updates.
  The current startup check is asynchronous and notification-only by design.
- [ ] Add in-app community/help links, a log viewer, and a third-party licence
  viewer. Diagnostics export and the repository licence are already available.
- [ ] Continue reducing Pulse-compatible internal buses while preserving public
  device names, failure recovery, and the no-duplicate-channel contract.

## Completion gate for every item

An item is only checked after its state migration and failure policy are
documented, automated tests cover success and rejection paths, the installed
package is exercised on a supported distribution, relevant real audio/hardware
behaviour is measured, user documentation is updated, and CI passes. A UI
mock-up or an unverified command path is not completion.
