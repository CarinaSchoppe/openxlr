# Effect feature integration checks

This branch combines the independent development pull requests for review and
reproduction. It is not a release, and none of these pull requests has been
merged into development by this work. The shared base is `7adb041`.

| Pull request | Feature | Tested head |
| --- | --- | --- |
| #156 | Channel and mix appearance | `17148bb` |
| #157 | Plugin manager | `19cdb53` |
| #158 | Plugin latency and optional mix compensation | `a6e854c` |
| #159 | Sound Check | `a89d3c7` |
| #160 | Inserts on every channel | `24e5f98` |
| #161 | Copy, presets, rename and A/B | `b755b8b` |
| #162 | Momentary effect keys | `87c76db` |

## Combined behavior

The integration keeps one command action field, both connection-reset paths,
shared XLR insert view models, and the existing shared effect editor for every
channel. Catalog reloads include software and capture channels. Sound Check,
momentary lease expiry, software-chain repair and mix-delay repair all remain
active in the normal sweep. Internal bus, delay and Sound Check nodes stay out
of device pickers.

The native LV2 fallback belongs around the insert filter launcher, not inside
the shared launcher also used for builtin mix delays. The combined adapter
honors explicit host choice, latency measurement and remembered LV2 fallback.
The integration commits retain this resolution and the shared UI/protocol
resolutions; blindly choosing either side of a merge conflict loses behavior.

The combined channel audio test additionally measures overlapping held keys
on an actual software-channel gain plugin, restoration after the final release,
and enabling latency measurement for an already running software effect. The
saved native-editor choice remains unchanged. This test extends the independent
feature tests because it needs both APIs in one build.

## Validation on 19 September 2026

- Locked restore and native-enabled Release build: zero warnings and errors.
- General suite: 1,114 passed, 26 environment-gated cases skipped.
- Private PipeWire suite: 48 passed. Its separately gated ClipGuard case was
  then explicitly enabled and passed with the installed SWH limiter and LSP
  native LV2 gate, both with and without low cut.
- Native audio bounds, CLAP, VST3, scanner, Sound Check buffer and Xvfb editor
  suites passed.
- Deck JavaScript: 15 tests passed, syntax and manifest checks passed.
- Four separate Xvfb checks passed: window layout, skins, tray and tooltip
  input. Minimum-size Sound Check, effect workflow and plugin manager renders
  were inspected.
- Shell lint, version consistency, locked packaging restore, OpenAPI structure
  and RPM file coverage checks passed.
- NuGet advisory check reported no known vulnerable direct or transitive
  packages for the five projects at the time of this check.

The all-channel LV2 fallback was also reproduced and tested with Ubuntu's
PipeWire 1.0.5, whose installed module set lacked the optional LV2 loader.
The catalog connection regression fails without its generation guard and
passes with it. Delayed stereo ports and partial-link cleanup have deterministic
helper tests in addition to the private audio test. The idle-graph allocation
test retains its zero-byte requirement on a dedicated warmed thread and passed
the complete suite and three focused repetitions.

Reproduce with the commands in CONTRIBUTING.md. Build the two fixtures before
the private audio suite:

```sh
make -C native tests/gain.lv2/gain.so tests/latency.lv2/latency.so
LV2_PATH="$PWD/native/tests" python3 tools/test-monitor-volume.py
OPENXLR_TEST_DSP=1 OPENXLR_TEST_FILTER=FullyQualifiedName~DspAudioIntegrationTests LV2_PATH=/usr/lib/lv2 python3 tools/test-monitor-volume.py
```

## Follow-up review of every feature head

The review reproduced and fixed four faults before repeating the combined build,
general suite, private PipeWire suite, ClipGuard audio check, window layout test,
Deck tests, metadata checks and package advisory scan:

- Invalid or duplicate preset names and malformed preset files escaped the UI's
  error handling. A view-model regression now covers save, read, delete and apply.
- Replies from an earlier Sound Check connection could overwrite current errors
  or interfere with a pending command. Four delayed-response cases exercise old
  successes and failures during both normal commands and window close.
- A closing Sound Check window accepted further actions while waiting for stop.
  A real X11 window and delayed WebSocket reply now verify disabled controls
  until the window closes.
- A transient PipeWire delay-control failure left mix compensation disabled.
  The private audio test injects the failure, verifies bounded recovery and
  checks that the healthy plugin keeps its node identity. Delay-only repair now
  reuses the routing helper without restarting healthy plugin instances or
  losing their private state.

Each new regression failed before its corresponding correction. The updated
feature commits are signed. The other four feature heads were unchanged in that first follow-up.
No additional exploitable security issue was confirmed by this review; this is
not a claim that arbitrary plugin code or the entire application is bug-free.

## Control lifecycle and maintainability review

The next pass inspected all seven diffs against development, concentrating on:

| PR | Reviewed boundaries |
| --- | --- |
| #156 | Presentation validation, SVG values, display order, persistence rollback and hidden-channel routing |
| #157 | Search-path bounds, file preservation, host environment, catalogue generations and open parameter controls |
| #158 | Latency units and invalid reports, bounded delay recovery, retained plugin state and catalogue lookup cost |
| #159 | Recording bounds, lost input paths, session expiry, stale replies and pending window closure |
| #160 | Stable public sinks, both stereo links, failed creation/deletion rollback, native fallback and resource accounting |
| #161 | Snapshot ownership, preset bounds, corrupt-file preservation, effect identity, window ownership and queued edits |
| #162 | Overlapping holds, lease expiry, manual overrides, baseline persistence, disconnects and bounded held actions |

A pending catalogue used to initialize controls on the first unrelated plugin
entry. Open controls could also retain old metadata after rescanning. They now
refresh after the complete catalogue is available, retaining parameter values
and removing stale controls. The per-insert collection subscriptions and their
unused catalogue-ready property were removed. A delayed catalogue test covers
repeated opening, a target after an unrelated entry, changed metadata and a
missing target after rescan.

Effect-control windows were indexed only by insert ID, although those IDs are
unique within a chain. Two channels with the same ID opened one window. Windows
now follow the actual instance, and removing or replacing it closes its window.
Parameter throttling likewise includes the channel; queued edits are discarded
on removal, replacement and disconnect. An X11 test checks independent windows,
and a real WebSocket test checks both channels' commands and the absence of
stale queued edits. The original catalogue and window-identity tests failed
before their fixes.

The standalone workflow suite also exposed the old allocation probe's runner
interference. It now uses the same dedicated-thread, warmed probe already in
#158, retaining the exact zero-byte assertion. The feature overview and channel
architecture description now match the new routing and documented limitations.

The combined validation listed above was repeated after these code changes,
including all four Xvfb suites and the advisory scan. The standalone manager
suite passed 1,036 cases with 20 environment-gated skips; the workflow suite
passed 1,031 with 20 skips. Final documentation merges do not alter the tested
source, native code or Deck code.

## Remaining acceptance

Physical interfaces, real listening, interactive third-party editor controls,
moving/resizing/reopening editors on the user's desktop, and Windows bridge
acceptance were not run. Xvfb and synthetic audio do not replace those checks.
Other environment-gated tests, such as live compositor focus routing, are not
claimed as covered by this work. Passing tests and advisory scans do not prove
that arbitrary plugins or every configuration are free from defects.

Latency compensation is initially off and aligns reported mix-insert delay,
not distinct input paths or device latency. Effect snapshots contain exposed
parameters, not plugin-private binary state or external sample files. Effect
replacement may briefly interrupt audio, and Sound Check loop boundaries may
have a transient. These limits are also documented in the individual features.
