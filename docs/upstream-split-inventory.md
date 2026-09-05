# Upstream split inventory

This is an archive-only tracking document, not proposed upstream documentation.
The checkpoint 16f0e81 preserves all 28 commits from the old fork main through
0da63a4, plus the previously uncommitted parity implementation. Nothing has been
reset or removed from the original development checkout.

Porting a mixed commit is not the same as cherry-picking it. Some commits span
several review topics; some contain changes the maintainer explicitly declined.
The table records both ported work and work still awaiting a separate port.
It does not claim that every archived feature is already in an open PR.

## Open upstream PRs

| PR | Scope | State at creation |
|---|---|---|
| [12](https://github.com/emaspa/openxlr/pull/12) | Stream-move verification | Ready; CI passed |
| [13](https://github.com/emaspa/openxlr/pull/13) | pw-dump multi-batch parsing | Ready; CI passed |
| [14](https://github.com/emaspa/openxlr/pull/14) | DaemonClient hardening | Ready; CI passed |
| [15](https://github.com/emaspa/openxlr/pull/15) | Hub coalescing | Ready; CI passed |
| [16](https://github.com/emaspa/openxlr/pull/16) | Restart button | Ready; CI passed |
| [17](https://github.com/emaspa/openxlr/pull/17) | api.md command field | Ready; CI passed |
| [18](https://github.com/emaspa/openxlr/pull/18) | Progress-gated watchdog | Draft; daemon/hardware acceptance pending |

All target upstream main based on 721802e, not the old fork main. The six small
fixes also merge together cleanly and pass the combined 80-test suite.

## Every original commit

| Commit | Original subject | Disposition |
|---|---|---|
| 0ed2d7e | Merge pull request #2 from emaspa/main | Historical merge retained in archive; fresh upstream supplies the current base |
| 5f486a0 | Merge pull request #4 from emaspa/main | Historical merge retained in archive |
| bcae078 | Add dynamic mixer layouts and visual LV2 controls | Editable-layout port pending; knob window excluded by review |
| a6bff4a | Merge pull request #5 from CarinaSchoppe/feature/dynamic-mixer-ui | Historical merge retained; feature contents tracked separately |
| 7a9d853 | Merge remote-tracking branch upstream/main | Historical merge retained; current upstream hardware fixes must win |
| 72d6001 | Harden daemon recovery and validate dynamic mixer UI end to end | Client/hub portions in PRs 14/15; watchdog in draft 18; layout tests pending port |
| 043969c | Host native LSP editors and isolate per-channel audio processing | Separate optional LV2 host PR pending; no C dependency in ordinary .NET build |
| c4b2cad | Verify native editor gestures across Ubuntu and current LSP layouts | LV2 acceptance material retained; Python/CI additions not proposed |
| f384f4a | Isolate LSP test profiles and close legacy first-run dialogs | Archived test tooling; evaluate relevant native-host tests separately |
| b21e7a7 | Use fresh disposable hosted-runner profiles without namespace overrides | Archived CI/test tooling, excluded at maintainer request |
| ae1bbbc | Verify real application routing and live channel reassignment | Routing mechanism in PR 12; Python acceptance retained in archive |
| 20a0e96 | Add service recovery controls, update provenance and distro package acceptance | Restart UI in PR 16, watchdog in draft 18; opt-in update notice port pending; package matrix excluded |
| eda5967 | Fix minimal distro runtime dependencies and hide application fan-out devices | Layout fan-out port pending Pro verification; dependencies follow respective optional host, not a general package rewrite |
| 41948ad | Record successful distro packages and installed recovery acceptance | Historical verification retained, not claimed as acceptance of new split branches |
| 19c6dd5 | Synchronize catalog coalescing test independently of runner scheduling | Deterministic real-WebSocket coverage in PR 14 |
| 24c4cc1 | Fold PipeWire snapshot change batches consistently in routing and diagnostics | Backend parser in PR 13; raw upstream diagnostics retained; Python tools excluded |
| be7bb16 | Wait for observable PipeWire routing after command acknowledgement | Production verification in PR 12; old Python tests retained only in archive |
| 13e9b30 | Retry bounded native captures during private graph startup | Archived native acceptance tooling |
| 19fe85a | Confirm stream routing and release CLI prerequisites | PR 12 covers routing; release/CLI tooling retained only in archive |
| 10f4ae0 | Add selectable listening mixes and persistent layout order | Layout ordering port pending; listening-mix feature follows layout per roadmap |
| 5d6b8a1 | Stabilize headless mixer layout test | Layout acceptance material retained for the layout port |
| c64fa4d | Wait for installed UI readiness in package tests | Archived packaging/CI acceptance, excluded from current PRs |
| 3f446c8 | Move GitHub Actions to Node 24 releases | Excluded CI change; retained in archive |
| 85453d4 | Add integration API and dynamic OpenDeck controls | Dynamic lists belong to layout; API port pending authentication, Origin and Content-Type checks; doc field in PR 17 |
| 402a96c | Credit fork feature development | Excluded by maintainer; historical credit commit retained in archive |
| cac054c | Version fork feature build as 0.1.16 | Excluded by maintainer; no version changes in split PRs |
| 859a525 | Synchronize native audio CI startup | Archived CI/test tooling |
| 0da63a4 | Verify GitHub CLI across package matrix | Archived CLI/package matrix, excluded at maintainer request |

## Previously uncommitted features

All are preserved in 16f0e81. These are not yet ready-to-merge upstream ports:

- Many-to-many routing matrix and its persisted route schema/UI.
- Native VST3 host and vendored SDK subset.
- Plugin catalog scan, cache, rescan and quarantine manager.
- Plugin and chain preset storage, import/export and native state.
- Plugin crash recovery, retries and fail-open routing.
- Sidechain routing and controls.
- Delay helper and initial latency compensation.
- Corresponding API/UI integration, tests, packaging support and parity docs.

Keep VST3/CLAP/yabridge direction, scanner, presets and PDC out of the LV2 decision
PR. Per-send FX stages, Sound Check and seamless chain switching remain incomplete,
as documented in parity-status.md; their names must not be presented as delivered.

## Required next ports

1. Editable channels/mixes, setup, channel editor, Flow editing, dynamic OpenDeck
   lists. Rename descriptions only; add nodes incrementally; preserve running
   streams and virtual mics. Hardware inputs, Monitor and Aux stay structural.
2. Complete draft watchdog acceptance: prolonged build, no audio server, server
   loss, hung workers, graceful graph cleanup, package/Nix validation.
3. Native LV2 host alone: build opt-out, filter-chain for plugins without editors,
   parent-thread/PDEATHSIG lifetime fix, separate UI/audio liveness.
4. HTTP API with authentication required by the newer roadmap, plus Origin and
   Content-Type checks and existing limits. Update notices default off or one-time
   opt-in and throttled, preferably a separate notice PR.

Do not put rejected versions/changelogs, README credits/fork promotion, forced
dark theme, knob controls or the package CI matrix back into these PRs.
