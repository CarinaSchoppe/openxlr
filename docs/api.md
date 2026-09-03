# WebSocket API

The daemon serves `ws://127.0.0.1:37890/ws`. The packages reserve that
port from the kernel's ephemeral range; if it is busy at startup the
daemon waits for it instead of touching PipeWire.

Messages from the daemon, each a JSON object with a `type` field:

| Type | When | Content |
|---|---|---|
| `state` | on connect and on every change | `daemonVersion`, device state, capabilities, mixer state, the device list, the app registry, profile names, `activeProfile` (the profile last recalled or saved for the active device; not cleared by later manual changes) |
| `meters` | 15 Hz while the mixer is built | live stereo levels per channel and mix |
| `plugins` | in answer to `listPlugins` | the installed LV2 plugins |
| `error` | when a command is rejected | `message` |
| `commandResult` | after a mixer command containing `requestId` | the same `requestId`, optional `error` (absent/null means success) |

The first message on a connection is always `state`, before meter frames.
The `state.features` array advertises `editableLayout`, `commandResults`,
`channelInserts` and `nativePluginUi`;
clients must check features rather than comparing release version strings.
For example, send `{"cmd":"createMix","name":"Podcast","requestId":"unique-id"}`.
An authoritative `state` precedes the matching `commandResult`. Layout
changes are persisted before success is reported. A lost connection or
timeout leaves the result unknown: inspect the restored state before retrying,
since re-sending an Add command could create a second item. Legacy commands
without `requestId` remain supported.

Commands are single JSON objects with a `cmd` field:

| Command | Fields | Purpose |
|---|---|---|
| `getState` | none | request a state push |
| `set` | `control`, `value` | hardware control (`gain`, `mute`, `lowCut`, `expander`, `voiceTune`, `voiceTuneStrength`, `phantom`, `clipGuard`, `compressor`, their `…2` variants for XLR 2, `hpVolumeDb`, `hp2VolumeDb`, `lowImpedance`, `crossfade`, `auxLevelDb`, `auxLevelLock`, `outHp1`, `outHp2`, `outUsbAux`, `outLineOut`) and the software `gainLock` |
| `setLowCutHz` | `value` | software low cut: 0, 80, or 120 |
| `setSoftClipGuard` | `value` | software ClipGuard (post-ADC limiter at -3 dB); enabling is rejected if `swh-plugins` is unavailable, without replacing or disconnecting the live microphone route |
| `setLevel` | `channel`, `mix`, `value` | one send fader |
| `setChannelMuted` | `channel`, `mix`, `value` | one send mute |
| `setMixVolume` / `setMixMuted` | `mix`, `value` | mix masters |
| `createChannel` | `name` | add an application channel and rebuild the owned PipeWire graph |
| `renameChannel` | `channel`, `name` | change its display/device name while keeping its stable id and references |
| `deleteChannel` | `channel` | delete an application channel; assigned apps move to the first remaining application channel |
| `createMix` | `name` | add a virtual output mix and publish its `OpenXLR <name>` recording device |
| `renameMix` | `mix`, `name` | change an output's display/device name while keeping its stable id and sends |
| `deleteMix` | `mix` | delete a user-created virtual output, its sends, inserts and PipeWire devices |
| `setMonitorOutputs` | `devices[]` | every sink the monitor mix feeds |
| `setMonitorOutput` | `device` | a single monitor sink; `null` disconnects the route |
| `setAuxPortEnabled` | `value` | send the Aux mix to the USB Aux port |
| `setOutputVolume` | `value` | volume of the selected monitor devices |
| `listPlugins` | none | the installed LV2 plugins, answered with a `plugins` message |
| `setInserts` | `channel`, `inserts[]` | replace a chain; `channel` is any channel ID or `mix:<id>`, each insert is `{id, kind:"lv2", plugin:<uri>, label?, bypass?, params?}` |
| `setInsertBypass` | `channel`, `insertId`, `value` | bypass one insert |
| `setInsertParam` | `channel`, `insertId`, `symbol`, `value` | one plugin control, by its LV2 port symbol |
| `showInsertUi` | `channel`, `insertId`, `requestId` | open/raise the active instance's X11 vendor UI; reports missing display, unavailable UI or inactive host as an error |
| `assignApp` | `identity`, `channel`, `label?` | route an app (creates a registry entry if unseen) |
| `assignStream` | `streamId`, `channel` | route one live stream by its PipeWire id; also remembered for the app |
| `forgetApp` | `identity` | drop an app and its remembered channel |
| `setEnforcedDefaults` | `sink`, `source` | system defaults to hold |
| `setActiveDevice` | `device` | switch to another attached interface (`vvvv:pppp`) |
| `saveProfile` / `loadProfile` / `deleteProfile` | `name` | named scenes, scoped to the active device |
| `getDiagnostics` | none | vendor block dump for bug reports |

The OpenDeck plugin in `plugin/` is a client of this API; the command
handler is `WebSocketHub.cs` and the message shapes are in
`Protocol.cs`, both under `src/OpenXLR.Daemon/`.

## Configuration files

All under `~/.config/openxlr/` (or `$XDG_CONFIG_HOME/openxlr/`):

- `mixer.json`: every mixer decision: the user-managed channel/output
  layout, levels, mutes, device choices, the app registry, enforced
  defaults, the software low cut, and insert chains. Written by the daemon.
- `profiles/<vid-pid>/<name>.json`: the named scenes, one file each
- `gainlock.json`: which devices have the gain lock set
- `daemon.json`: the daemon's own preferences, read once at start.
  `submixer` (true/false/absent) turns the submixer on or off; absent
  means the unit's environment decides (`OPENXLR_BUILD_MIXER`). Written
  by the UI's Options window.
- `ui.json`: window preferences (tray, start minimized, autostart
  toggles)
