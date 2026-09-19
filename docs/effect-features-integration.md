# Effect feature integration checks

This branch combines the independent development pull requests for review and
reproduction. It is not a release, and none of these pull requests has been
merged into development by this work. The shared base is `7adb041`.

| Pull request | Feature | Tested head |
| --- | --- | --- |
| #156 | Channel and mix appearance | `17148bb` |
| #157 | Plugin manager | `819b485` |
| #158 | Plugin latency and optional mix compensation | `a6e854c` |
| #159 | Sound Check | `9bd2a5d` |
| #160 | Inserts on every channel | `d3b6e9e` |
| #161 | Copy, presets, rename and A/B | `cbc8117` |
| #162 | Momentary effect keys | `91d224f` |

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
feature commits are signed. The other four feature heads remain unchanged.
No additional exploitable security issue was confirmed by this review; this is
not a claim that arbitrary plugin code or the entire application is bug-free.

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
