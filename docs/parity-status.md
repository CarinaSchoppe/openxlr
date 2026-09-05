# High-priority software-audio checkpoint

This document tracks the fork implementation against the production-grade
Wave Link/software-audio requirements. It is a development checkpoint, not a
completed parity release. Maintained as part of Carina Schoppe's fork work.

## Present in this checkpoint

| Area | Implementation | Remaining acceptance/limits |
| --- | --- | --- |
| Routing matrix | Persisted stable destination IDs, many-to-many output routes, disconnected intent, rollback and hotplug repair; header Routing window | Shared Wave XLR Pro output-bus semantics and all rollback cases need wider audio acceptance |
| Native Linux VST3 | Separate native DSP/editor/scanner process; parameters, component/controller state, X11 editor, bus discovery, latency reports | Broad vendor/editor compatibility, yabridge and dynamic graph-rate changes remain unverified |
| Plugin Manager | Ready/failed/rejected/quarantined results, bounded rescan, explicit retry/unquarantine | LV2 scan is isolated as one catalog scan; per-bundle LV2 quarantine is not yet implemented |
| Presets | Ordered chain and individual plugin presets; rename/duplicate/delete/import/export API; save/load UI | LV2 arbitrary state/worker/assets and A/B remain open; VST3 state is bounded to the host's limit |
| Recovery | Host health checks, dry fallback, exponential retries, quarantine after repeated failures, explicit recovery controls | Runtime quarantine currently resets with the daemon; persistent scanner quarantine is separate |
| Sidechains | Real VST3 auxiliary input ports, stable channel/mix source IDs, width/cycle validation, UI bus pickers | Measured compressor ducking and complete cross-chain lifecycle/latency acceptance remain open |
| Latency | Bounded native sample-delay helper, per-destination convergence planning, source/compensation samples in API | Initial mix-output implementation only; not complete channel/sidechain PDC or click-free switching |
| Wave FX hardware insert | Explicit false device capability | No verified hardware USB send/return pair; no speculative hardware writes |

The raw/hardware/plugin/full enum names reserve protocol vocabulary; only
`MixProcessed` is currently advertised for matrix routes. Unsupported values
are rejected instead of mapping different labels onto the same tap.

## Ownership and state

The daemon owns the graph. `PluginCatalogService` publishes an immutable
`PluginRegistry` snapshot and caches VST3 metadata by bundle fingerprint.
Scanner output, runtime, filesystem traversal, native state and preset imports
are bounded. Cache and quarantine files live in the user's OpenXLR XDG
directories; explicit failed scans invalidate prior ready cache entries.

Native plugins run in `openxlr-lv2-host` or `openxlr-vst3-host`. Audio uses
PipeWire ports, not managed IPC. Controls/state use a separate line protocol.
The sample-delay helper allocates its buffers before processing and performs
no allocation, locking or file I/O in its audio callback.

`mixer.json` schema 2 and profiles retain output routes and insert definitions.
Preset import validates names, formats, finite controls, sizes and identities;
it does not accept arbitrary executable paths. Stored VST3 module paths come
from scanning, not from an API insert request.

## Checks and reproduction

The local checkpoint has passed the managed build/test suite, Python acceptance
driver tests, Node plugin tests and an actual private-PipeWire impulse test.
The impulse test measured 1,537 samples of differential delay while preserving
0.75 peak amplitude. Both branches include a helper so their graph scheduling
cost is equal. This verifies the helper, not every possible route topology.

```sh
dotnet build src/OpenXLR.slnx -c Release -warnaserror
dotnet test src/OpenXLR.slnx -c Release --no-build
dotnet format src/OpenXLR.slnx --verify-no-changes --no-restore
python3 -m unittest discover -s tools -p 'test_*.py'
node --test plugin/tests/*.test.mjs
python3 tools/verify-delay-host.py
python3 tools/verify-native-host.py
# Requires the official SDK's AGain Sample Accurate bundle built locally:
python3 tools/verify-vst3-host.py /absolute/path/again-sample-accurate.vst3
```

Debian, Fedora and Arch package definitions include the new helpers and their
C++/CMake build dependencies. CI checks the installed helper permissions and
delay audio. Check the current commit's workflow results before claiming all
three distributions passed. Historical validation in `verification.md` refers
only to the commits named there.

## Next implementation work

1. Finish many-to-many routing and sidechain audio acceptance and repair any
   lifecycle problems it exposes, including shared physical output buses.
2. Implement real stage taps/per-send FX selection, with reference migration.
3. Replace remaining chain rebuild gaps with measured click-free transitions.
4. Complete plugin asset state and runtime quarantine persistence.
5. Implement Sound Check recording/loop/A-B through the real engine, with
   monitor-only routing, bounded local storage, and complete restoration.
6. Run full package/UI/audio/CI acceptance, update PR #10 with measured results,
   and broaden vendor, yabridge and hardware testing.

The detailed Wave Link comparison remains in [TODO.md](../TODO.md).
