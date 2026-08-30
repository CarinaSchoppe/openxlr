# Plugin inserts: design and plan

Goal: Wave Link parity for plugin inserts. A channel gets an ordered
list of audio plugins processed host-side, the way Wave Link runs VSTs
on its channels. Nobody in the Linux streaming-mixer niche ships this
today: goxlr-utility has an open feature request
([#200](https://github.com/GoXLR-on-Linux/goxlr-utility/issues/200))
judged out of scope there, and PipeWeaver lists LV2 as a future plan.

## Research findings (2026-08-30)

- PipeWire's `filter-chain` module hosts **builtin, LADSPA and LV2**
  filters natively: plugin by URI, initial control values, and
  key/value state for plugins implementing `LV2_STATE__interface`.
  Control values live as node props and are changeable at runtime. It
  does **not** host VST2/VST3/CLAP, and nothing upstream suggests that
  is coming.
- **Carla** (2.5.10, in the Arch repos) is the workhorse for the rest:
  `carla-single` runs ONE plugin per process (formats: lv2, vst2,
  vst3, clap, ladspa, dssi, au, sf2, sfz) and, run under
  `pw-jack`, appears in the PipeWire graph as an ordinary client with
  input/output ports we can link like any node. `carla-discovery-native`
  is its scanner binary and reports plugin metadata. Windows plugins
  load through Carla's own bridges
  (`CARLA_BRIDGE_PLUGIN_BINARY_TYPE=win64`) or through yabridge.
- **yabridge** 5.1.1 remains the way to run Windows VST2/VST3/CLAP
  under Wine (currently pinned to wine-staging 9.21; a wine 10 branch
  exists). It exposes Windows plugins as native Linux VST3s, which the
  Carla path then hosts with no extra work from us. Documentation
  territory, not integration territory.

## Architecture: insert slots

A new per-channel concept in the mixer: `inserts`, an ordered list of

    { id, kind: ladspa | lv2 | vst2 | vst3 | clap,
      ident,            // LV2 URI or plugin file path + index
      label, bypass,
      params: { symbol: value },   // stage 1 (filter-chain kinds)
      stateFile }                  // stage 2 (Carla kinds)

- Channels: hardware inputs first (XLR 1, XLR 2, Aux In), where the
  mic-path filter-chain already exists. App channels later if wanted;
  the graph work is identical.
- Graph placement: in the channel path BEFORE the fan-out to mixes, so
  every mix hears the same processed signal, exactly like the existing
  software low cut and ClipGuard (inserts chain after them). Every
  insert adds at least one quantum of latency to the monitor path, so
  a later "dry monitor" toggle (monitor tap before the inserts) is on
  the roadmap; bypass per insert ships from day one.
- Self-healing: the daemon already re-links the mic filter node when it
  dies; inserts join the same watch.

## Stage 1: LV2 and LADSPA inserts (native filter-chain)

No new dependencies beyond the user's plugins; lands quickly.

1. Generalise the mic filter-chain builder in `PipeWireAdapter`: today
   it composes fixed nodes (bq_highpass, hard_limiter); teach it to
   append arbitrary `{ type = lv2 plugin = <uri> ... }` nodes with
   serial links and initial control values from `params`. Edits tear
   down and rebuild the chain (brief dropout, same as toggling low cut
   today).
2. Plugin discovery: shell out to `lv2ls` / `lv2info` (package:
   `lilv`, optdepends) to enumerate plugins and their control ports
   (symbol, range, default, logarithmic hint). Cache the scan; expose
   over the WS protocol (`listPlugins`).
3. Parameter control: control-port values are node props on the
   filter-chain node, so live changes go through the same pw-cli
   set-param path used elsewhere; the UI auto-generates sliders from
   the port metadata.
4. Constraint: the mic path is mono; stage 1 offers only plugins whose
   audio I/O is mono-capable (port-count check at scan time), which
   covers the useful voice set (LSP, Calf, x42 mostly ship mono
   variants). Stereo channels (Aux In, app channels) can host stereo
   plugins when they gain inserts.
5. Protocol: `listPlugins`, `setInserts` (whole list per channel),
   `setInsertParam`, `setInsertBypass`. Persist in mixer settings;
   profiles store the insert list with everything else.
6. UI: an INSERTS row per hardware channel: add (searchable picker),
   reorder, bypass, remove; expanding an insert shows its generated
   parameter sliders.

## Stage 2: VST2 / VST3 / CLAP via Carla

One `carla-single` process per insert, spawned and owned by the daemon.

1. Spawn: `pw-jack carla-single native vst3 <path>` (kind mapped per
   format). The process appears in the graph as a client whose ports
   the daemon discovers by node-name watch (same machinery as device
   sinks) and links into the channel chain.
2. Discovery: run `carla-discovery-native` over the standard paths
   (`~/.vst3`, `/usr/lib/vst3`, `~/.vst`, `/usr/lib/vst`, `~/.clap`,
   `/usr/lib/clap`), cache results, merge into `listPlugins` with the
   LV2 set.
3. State: the open research question, gated by the M0 prototype below.
   Candidate mechanisms, in preference order: (a) Carla's OSC control
   surface on carla-single to trigger save/load of plugin state to our
   `stateFile`; (b) a managed per-insert `.carxp` project with a
   headless carla-rack instead of carla-single; (c) accept
   editor-configured state living only for the process lifetime in the
   MVP, with a documented save button. Do not guess; prototype.
4. Editor GUI: VST parameters are not auto-generated; an "Edit" button
   asks the host to show the plugin's own editor window. carla-single
   shows it by default; whether it can start hidden and show on demand
   is an M0 question (worst case: the editor opens on insert creation
   and the user closes it).
5. Crash isolation: a VST crash kills its own process, never the
   daemon. Watchdog restarts it with saved state; after three crashes
   in a minute the insert auto-bypasses and the UI says so.
6. Windows plugins: no integration work. Document yabridge (their
   VST3s appear native and stage 2 hosts them) and Carla's own win64
   bridge env as the unsupported-but-known path.

## Milestones

- **M0, prototype (one evening, gates stage 2)**: by hand, host one
  native Linux VST3 (for example LSP or Dragonfly, both packaged) in
  `carla-single` under pw-jack on this machine; verify port names and
  linking, measure added latency, answer the state-persistence and
  hidden-GUI questions, try one yabridge plugin if available.
- **M1**: stage 1 complete (LV2/LADSPA slots, scanner, params, UI,
  persistence, profiles) and shipped; it is useful on its own.
- **M2**: stage 2 complete (Carla processes, VST scanner, editor
  button, crash policy, state per the M0 answer).
- **M3**: polish: dry-monitor toggle, per-insert latency readout,
  OpenDeck key target for insert bypass, docs and README.

## Packaging

- Arch: optdepends `lilv` (LV2 scan), `carla` (VST hosting).
- deb/rpm: Suggests `lilv-utils` / `lilv`, `carla`.
- Nix: module options `services.openxlr.inserts.lv2` (adds lilv) and
  `.carla` (adds carla to the daemon's PATH wrapper).

## Risks

- carla-single state persistence may be awkward (hence the M0 gate and
  the carla-rack fallback).
- Heavy plugins at small quantum cause xruns; the per-insert latency
  and CPU readout plus bypass keep it debuggable.
- Carla 2.5 vs the 2.6/main line differ in CLAP coverage; pin
  behaviour to what the distro ships.
- Wine/yabridge issues are explicitly out of scope: documented, not
  supported.
