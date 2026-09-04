# Local integration API v1

The daemon exposes a versioned HTTP and WebSocket API on
`127.0.0.1:37890`. It is intended for desktop utilities, automation,
accessibility tools, controllers, and broadcast software that need to inspect
or control OpenXLR. The older `ws://127.0.0.1:37890/ws` address remains a
compatible alias for the v1 event socket.

The server deliberately binds to loopback only. It does not enable CORS and it
does not offer unauthenticated LAN access. A web page from an unrelated origin
therefore cannot silently change the local audio setup. Native local clients
do not need credentials. Integrations should discover `apiVersion`, check the
`state.features` flags they use, preserve stable channel/mix IDs, and display
errors rather than blindly retrying mutations.

The default port is reserved by the Linux packages from the kernel's ephemeral
range. For an isolated development or test instance only,
`OPENXLR_API_PORT=<1024..65535>` selects another loopback port.

## HTTP resources

| Method and path | Result |
|---|---|
| `GET /healthz` | cheap daemon liveness response; does not mean audio hardware is connected |
| `GET /api/v1` | discovery document with version and resource URLs |
| `GET /api/v1/state` | authoritative hardware, mixer, routing, app, profile, and capability state |
| `GET /api/v1/plugins` | installed LV2 catalog; the first scan can take longer |
| `POST /api/v1/commands` | execute one command from the command table below |
| `GET /api/v1/openapi.json` | machine-readable OpenAPI 3.1 contract, including schemas and examples |
| `WS /api/v1/events` | state, live meter and command event stream |

Quick read-only checks:

```sh
curl --fail http://127.0.0.1:37890/healthz
curl --fail http://127.0.0.1:37890/api/v1/state | jq .
```

Every HTTP command response is a `commandResult` with `apiVersion`, a caller
supplied or server-generated `requestId`, `ok`, and an optional `error`. A
mutation also contains the authoritative post-command `state`, which is the
safe basis for the next edit. Successful commands return HTTP 200, malformed
JSON or an invalid correlation ID returns 400, requests over 64 KiB return 413, and a well-formed command
rejected by validation/hardware/audio returns 422.

```sh
curl --fail-with-body http://127.0.0.1:37890/api/v1/commands \
  -H 'Content-Type: application/json' \
  -d '{"cmd":"setLevel","channel":"music","mix":"stream","value":0.72,"requestId":"obs-panel-42"}'
```

Layout mutations (`create…`, `rename…`, `delete…`, `reorder…`) use stable IDs.
Read current state again after a lost response before retrying a create, because
the command might already have succeeded.

## WebSocket events

Connect to `ws://127.0.0.1:37890/api/v1/events` (or the legacy `/ws`).
The first frame is always `state`, before meter frames. Commands use the same
JSON objects as HTTP. When `requestId` is present every command gets a
correlated `commandResult`; mutations receive authoritative `state` first.

Messages from the daemon, each a JSON object with a `type` field:

| Type | When | Content |
|---|---|---|
| `state` | on connect and on every change | `daemonVersion`, device state, capabilities, mixer state, the device list, the app registry, profile names, `activeProfile` (the profile last recalled or saved for the active device; not cleared by later manual changes) |
| `meters` | 15 Hz while the mixer is built | live stereo levels per channel and mix |
| `plugins` | in answer to `listPlugins` | the installed LV2 plugins |
| `error` | when a command is rejected | `message` |
| `commandResult` | after any command containing `requestId` | `apiVersion`, the same `requestId`, `ok`, and optional `error`/query `result` |

The `state.features` array advertises `editableLayout`, `commandResults`,
`channelInserts`, `nativePluginUi`, `layoutOrder` and `monitorMixSelection`;
`httpApiV1` advertises the HTTP surface. Clients must check features rather
than comparing release version strings.
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
| `reorderChannels` | `order[]` | set every editable application channel id in presentation order; no graph rebuild |
| `createMix` | `name` | add a virtual output mix and publish its `OpenXLR <name>` recording device |
| `renameMix` | `mix`, `name` | change an output's display/device name while keeping its stable id and sends |
| `deleteMix` | `mix` | delete a user-created virtual output, its sends, inserts and PipeWire devices |
| `reorderMixes` | `order[]` | set every user-created output mix id in presentation order; no graph rebuild |
| `setMonitoredMix` | `mix` | listen to this mix's post-insert signal on the selected monitor devices |
| `setMonitorOutputs` | `devices[]` | every sink the currently listened mix feeds |
| `setMonitorOutput` | `device` | a single sink for the listened mix; `null` disconnects the route |
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

## Client examples

Python 3, using only its standard library:

```python
import json
from urllib.request import Request, urlopen

base = "http://127.0.0.1:37890"
with urlopen(f"{base}/api/v1/state", timeout=2) as response:
    state = json.load(response)

music = next(channel for channel in state["mixer"]["channels"]
             if channel["name"] == "Music")
stream = next(mix for mix in state["mixer"]["mixes"]
              if mix["name"] == "Stream")
body = json.dumps({"cmd": "setLevel", "channel": music["id"],
                   "mix": stream["id"], "value": 0.72,
                   "requestId": "example-1"}).encode()
request = Request(f"{base}/api/v1/commands", data=body,
                  headers={"Content-Type": "application/json"})
with urlopen(request, timeout=3) as response:
    result = json.load(response)
assert result["ok"], result.get("error")
```

Browser-like WebSocket clients should be shipped as part of a trusted local
application, not hosted on an arbitrary site:

```js
const socket = new WebSocket("ws://127.0.0.1:37890/api/v1/events");
socket.addEventListener("message", ({ data }) => {
  const event = JSON.parse(data);
  if (event.type === "state") renderMixer(event.mixer);
  if (event.type === "meters") renderMeters(event.levels);
  if (event.type === "commandResult" && !event.ok) showError(event.error);
});
socket.addEventListener("open", () => socket.send(JSON.stringify({
  cmd: "getState", requestId: crypto.randomUUID(),
})));
```

Do not derive IDs by lower-casing display names. Names can change while IDs and
references remain stable. On reconnect, replace cached state with the initial
`state` frame. Meter events are transient and may be dropped for a slow client;
state is level-triggered and authoritative. The server limits each WebSocket
client to a bounded send queue so an abandoned integration cannot grow daemon
memory without limit.

## Configuration files

All under `~/.config/openxlr/` (or `$XDG_CONFIG_HOME/openxlr/`):

- `mixer.json`: every mixer decision: the user-managed channel/output
  layout and order, levels, mutes, monitor devices/listened mix, the app registry, enforced
  defaults, the software low cut, and insert chains. Written by the daemon.
- `profiles/<vid-pid>/<name>.json`: the named scenes, one file each
- `gainlock.json`: which devices have the gain lock set
- `daemon.json`: the daemon's own preferences, read once at start.
  `submixer` (true/false/absent) turns the submixer on or off; absent
  means the unit's environment decides (`OPENXLR_BUILD_MIXER`). Written
  by the UI's Options window.
- `ui.json`: window preferences (tray, start minimized, autostart
  toggles)
