# WebSocket API

The daemon serves `ws://127.0.0.1:37890/ws` (the packages reserve that
port from the kernel's ephemeral range; if it is ever busy at startup
the daemon waits for it rather than touching PipeWire). On connect (and on every
change) it pushes a full `{"type":"state", …}` message carrying device
state, capabilities, mixer state, the device list and the app registry;
meters arrive as small `{"type":"meters"}` frames at 15 Hz. Commands
are single JSON objects:

| Command | Fields | Purpose |
|---|---|---|
| `getState` | none | request a state push |
| `set` | `control`, `value` | hardware control (`gain`, `mute`, `lowCut`, `expander`, `voiceTune`, `voiceTuneStrength`, `phantom`, `clipGuard`, `compressor`, `…2` variants, `hpVolumeDb`, `hp2VolumeDb`, `lowImpedance`, `crossfade`, `auxLevelDb`, `auxLevelLock`, `outHp1/2`, `outUsbAux`, `outLineOut`) and the software `gainLock` |
| `setLowCutHz` | `value` | software low cut: 0, 80, or 120 |
| `setSoftClipGuard` | `value` | software ClipGuard (hard limiter at -3 dB) |
| `setLevel` | `channel`, `mix`, `value` | one send fader |
| `setChannelMuted` | `channel`, `mix`, `value` | one send mute |
| `setMixVolume` / `setMixMuted` | `mix`, `value` | mix masters |
| `setMonitorOutputs` | `devices[]` | every sink the monitor mix feeds |
| `setAuxPortEnabled` | `value` | send the Aux mix to the USB Aux port |
| `setOutputVolume` | `value` | volume of the selected monitor devices |
| `listPlugins` | none | the installed LV2 plugins, answered with a `{"type":"plugins"}` message |
| `setInserts` | `channel`, `inserts[]` | replace a chain; `channel` is `xlr1`, `xlr2` or `mix:<id>`, each insert is `{id, kind:"lv2", plugin:<uri>, label?, bypass?, params?}` |
| `setInsertBypass` | `channel`, `insertId`, `value` | bypass one insert |
| `setInsertParam` | `channel`, `insertId`, `symbol`, `value` | one plugin control, by its LV2 port symbol |
| `assignApp` | `identity`, `channel`, `label?` | route an app (creates a registry entry if unseen) |
| `forgetApp` | `identity` | drop an app and its remembered channel |
| `setEnforcedDefaults` | `sink`, `source` | system defaults to hold |
| `setActiveDevice` | `device` | switch to another attached interface (`vvvv:pppp`) |
| `saveProfile` / `loadProfile` / `deleteProfile` | `name` | named scenes, scoped to the active device |
| `getDiagnostics` | none | vendor block dump for bug reports |

The OpenDeck plugin in `plugin/` is a client of this API; anything it
does, a script can do too.

## Configuration files

- `~/.config/openxlr/mixer.json` holds every mixer decision: levels, mutes,
  device choices, the app registry, enforced defaults, the software low
  cut, the insert chains (the daemon writes it)
- `~/.config/openxlr/profiles/<vid-pid>/<name>.json` holds the named
  scenes, one file each
- `~/.config/openxlr/gainlock.json` holds which devices have the gain lock set
- `~/.config/openxlr/ui.json` holds window preferences (tray, autostart)
