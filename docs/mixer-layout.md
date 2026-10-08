# Saved mixer layout

The daemon reads the layout from `mixer.json` before building its graph.
Stop the daemon before editing this file manually: while running, its normal
settings saves overwrite the file with the live configuration.

`userChannels` is an ordered list of application and capture channels and
`userMixes` an ordered list of user mixes. A mix may set `kind` to
`monitor` or `virtualMic`; absent `kind` means `virtualMic` for old files.
Other kinds are ignored. Each entry has a stable
`id` and a display `name`. For example:

```json
{
  "userChannels": [{"id": "podcast", "name": "Interview"}],
  "userMixes": [{"id": "recording", "name": "Recording"},
                {"id": "headphones", "name": "Headphones", "kind": "monitor"}]
}
```

These are fields in the existing settings object; retain its other fields when
editing. Missing or null lists keep the legacy defaults. A single invalid entry
is dropped and logged with its path, and the rest of the file still applies; a
file that cannot be parsed at all is copied to `mixer.json.corrupt` before the
next save replaces it. An empty mix list removes all user mixes. An empty application list falls back to System so incoming
applications have a destination.

Hardware inputs, Monitor A (`monitor`), Monitor B (`monitor2`) and Aux
(`auxout`) remain structural. The `ignore` application target is reserved.
Invalid or duplicate entries are ignored; ids compare without regard to case,
and an entry that repeats a structural id is dropped. IDs contain at most 36
lowercase ASCII letters, digits, underscores or hyphens, beginning with a
letter. Names contain 1 to 60 printable characters and are trimmed. At most
32 editable channels and 16 user mixes in total are restored.

Node names derive from IDs, not labels. The list order survives a settings
save and restart. Removed application destinations fall back to the first
application channel, never a hardware input; ignored applications stay ignored.
Existing monitor-feed settings and profile semantics are unchanged.

## Other fields

The rest of the settings object is the live state the daemon writes back on
every save. Every field is optional on read; an absent one is empty or off.

| Field | Holds |
|---|---|
| `mixVolumes` | mix id to master level |
| `mixMuted` | mix ids whose master is muted |
| `levels` | `channel\|mix` to send level |
| `channelMuted` | `channel\|mix` cells whose send is muted |
| `monitorOutputs` | the selected outputs, in order; the first one is what `@monitor` resolves to |
| `monitorOutput` | the older single selection, read only when `monitorOutputs` is empty |
| `monitorFeeds` | output to feed, see [Output feeds](#output-feeds) |
| `auxPortEnabled` | whether the Aux mix reaches the USB Aux port; when absent, a saved `#usbaux` monitor selection turns it on once and is dropped from the selection |
| `enforcedDefaultSink` | the output held as the system default, or `@monitor`; `setEnforcedDefaults` and `setMainOutput` write it |
| `enforcedDefaultSource` | the source held as the system default |
| `appOverrides` | application identity to remembered channel |
| `knownApps` | `{identity, label, channelId}` for every application seen |
| `lowCutHz` | the software low cut, 0, 80 or 120 |
| `softClipGuard` | the software ClipGuard |
| `inserts` | insert chains by `xlr1`, `xlr2` or `mix:<id>` |

A null field or a null entry inside one is dropped and logged; so is a
non-finite number in `mixVolumes`, `levels` or an insert's `params`. A
`monitorFeeds` entry for an output that is not selected is removed the next
time the selection is written.

## Editing while running

The window's Edit layout button, and the commands below over the API,
edit the running graph. Adding or reordering preserves existing routes;
renaming an application channel can cause a short gap on that channel,
and deleting a virtual microphone disconnects its recorders. A command
whose ids, names or lists fail validation is refused before anything
changes. Each one is then saved before it is acknowledged, with the
whole settings object, so a fader move pending in the debounced save is
on disk with it and an earlier save error is cleared; a failed save
restores the previous layout and reports an error. A rename to the current
name is acknowledged without a save. Ordinary fader saves keep their
debounced, retried behaviour.

- `createChannel {name}` adds an application channel. Only its sink is
  loaded; it starts muted in every mix. The sink's sends into the mixes
  must appear within three seconds, or the sink is unloaded again and
  the command fails: a send that showed up later would carry PipeWire's
  defaults, full level and unmuted, instead of the stored fader.
- `renameChannel {channel, name}` saves the name and reloads that channel's
  playback device under it, so desktop applets show the new name at once.
  PipeWire parks the streams that were playing into it on the default
  output for the moment the sink is away, and the daemon puts them back.
  A capture channel's sink is hidden, so its name changes without a reload.
- `deleteChannel {channel}` removes an application or capture channel. Apps
  routed to it, remembered assignments included, and whatever is playing
  into its sink at that moment move to the first remaining application
  channel; then its sink is unloaded, and a capture channel's link to its
  source with it. The last application channel stays.
- `createMix {name, kind?}` adds a virtual microphone by default. With
  `kind: "monitor"` it adds an output mix without a post sink or virtual
  microphone. Its master uses the existing desktop volume and mute controls,
  including the 150% ceiling. The channel sinks feed the mix sinks by name pattern, so every channel grows a send into the new mix
  by itself, muted before the optional capture device is published. If a
  channel's send has not appeared within three seconds the mix is removed again and
  the command fails, for the same reason.
- `renameMix {mix, name}` changes the name in OpenXLR only. The mix or
  capture device stays in place so clients using it are not interrupted;
  its PipeWire description keeps the old name until the daemon restarts; the state carries `renamedSinceStart` and the window shows a
  restart hint.
- `deleteMix {mix}` removes a user mix and its sends and inserts. For a
  virtual microphone it also removes the capture device; its recorders lose
  that device.
  Outputs listening to that mix keep the other mixes in their feed, or return
  to the first monitor mix if none remain. An enforced default source
  that was this microphone is cleared. An enforced default sink that was
  the deleted mix is cleared too, leaving the desktop to choose a default. All of that is part
  of the saved deletion and rolls back with it when saving fails.
- `setLayoutOrder {channels, mixes}` reorders the editable ids. Supply every
  application and capture channel id and every user-mix id exactly
  once; hardware inputs, Monitor A/B and Aux keep their positions. No node
  changes. Open windows apply the published order to channel tiles, mix
  controls and send rows while retaining the existing controls and their
  values.

Every added channel or mix costs pipewire-pulse a few dozen open files;
the daemon refuses an addition the server has no room for, and the packages
raise the server's limit ([manual: open-file limit](manual.md#open-files)).

Ids are generated from names: lowercase letters and digits with hyphens,
starting with a letter, at most 28 characters, made unique against the
existing ids and the reserved ones with a numeric suffix (`-2`, `-3`). A
name that leaves nothing usable becomes `channel` or `mix`, and one that
starts with a digit gets that word in front. An id never changes
afterwards, so node names, profiles and Stream Deck keys survive a rename.

Other manual changes, including external PipeWire descriptions, take effect
at startup.

## Capture inputs

`createCaptureChannel {name, source, capturePair, captureMonoChannel}` adds a channel from an
external PipeWire capture source. `source` is its exact `node.name`, at most
256 printable characters. `capturePair` is a zero-based stereo pair from 0 to
31 and defaults to 0. Optional `captureMonoChannel` selects one zero-based
port from 0 to 63 instead, requires `capturePair: 0`, and duplicates that mono
input to both sides. An unavailable port stays silent. Mono sources feed both sides. A missing pair stays
silent. The source must be present when creating the channel. OpenXLR's own
devices and sink monitor sources are not capture inputs, to avoid direct
feedback: a name starting with `OpenXLR` or ending in `.monitor` is refused.

Capture channels share the editable-channel limit of 32. They start muted in
all mixes. Their hidden combine sink supports the existing sends, faders,
meters and scene recall. They are excluded from application-routing choices.
At least one application channel must remain; restoring a layout containing
only capture inputs reserves a slot for a System application channel. Its
application identity is retained even if a discarded capture entry had the
same id and display name.

Bindings are stored in `userChannels`, for example
`{"id":"second-mic","name":"Second microphone","captureSource":"alsa_input.usb-headset","capturePair":0}`.
An entry without `captureSource` is an application channel, as in older files;
one that carries a `capturePair` other than 0 or a non-null `captureMonoChannel`
without a source is dropped.
Invalid capture bindings are discarded, not converted into application channels.
The binding belongs to the layout, not a profile. Rename, reorder and delete
use the existing channel commands. Renaming leaves the capture graph running.
To use a different source or pair, create a new capture channel and remove the
old one.

Disconnected sources retain their exact binding and faders. They reconnect
when that node and pair return, without falling back to another microphone.
Both sides of the channel must connect before the input reports connected.
If only one link succeeds, it is removed and the next sweep retries the
whole connection, including the two links used for a mono source.
Multiple interfaces can therefore supply audio simultaneously, independently
of the interface selected for hardware controls. Additional hardware controls are available in [Wave interfaces](wave-interfaces.md).
The built-in software low cut and ClipGuard remain on the primary XLR path.
Capture channels use the existing stereo insert hosting and mix latency
compensation. Sound Check records their selected mono port, or the left port
of their stereo pair, and duplicates the loop through the live stereo chain.

## Output feeds

`monitorFeeds` records the mixes included in each selected output, as one mix
id or several joined with `+`. Every mix in a feed reaches the output at
unity, as a direct port link from the mix; a blend at other levels is a mix
of its own, with the sends set there. An output without an entry, or with
an empty or unknown one, hears the first monitor mix. Deleting a mix drops
it from every feed. The jacks of one interface share a return bus, so they
are written with one feed.

`outputRoutes`, the per-route level list that 0.1.40 and 0.1.41 wrote, is
ignored on read, in this file and in profiles.

If a channel or mix deletion cannot be saved, its previous routing settings
and any pending volume or mute writes are restored together. The normal
reconciliation keeps retrying those writes when PipeWire becomes available.

## Mixer presentation

Display metadata lives in the `appearance` map in `mixer.json`, keyed by
`channel:<id>` or `mix:<id>`, with `icon`, `colour`, `hidden` and optional `order`.
Missing entries retain the existing appearance and layout order. Equal or
missing positions use the original layout order. Deleted items lose their
metadata. This is saved mixer presentation and is also included in profiles.
The entry limit covers the full layout, including all editable channels,
both kinds of user mix and structural items. User monitor mixes retain
their editable state alongside their icons, colours and display order.
Saving a failed edit restores the previous presentation. No PipeWire nodes
are rebuilt by these edits. `setLayoutOrder` changes routing order for editable items; the window calls it
when **Use displayed order for routing** is chosen. `setDisplayOrder` overrides its visual order for all items.

The live state names the default output feed in `primaryMonitorMix`. This is
the first monitor mix in routing order, even if display ordering puts another
mix first. It is derived state, not an additional saved routing preference.

Profiles include this map as `mixer.appearance`. A missing or null map keeps
current presentation; `{}` clears it. Entries for deleted channels or mixes
are dropped when recalling. Malformed entries reject the whole profile before
hardware or mixer settings change. Presentation ordering never changes the
routing order of channels and mixes.

Control sizing is a window preference, not routing or layout metadata. Profiles
can store it as `presentation.touchControls`: true selects Touch, false selects
Standard, and an absent or null field keeps the local preference.

Window-saved profiles also carry the separate window presentation, including
skin, System/Light/Dark appearance mode, collapsed sections and compact view.
Those preferences are stored locally in `ui.json`, not as mixer layout values;
changing them never rebuilds audio routes. An older profile without a mode
preserves the current mode. See [profile presentation](api.md).
The window's Arrange handles use `setDisplayOrder` for channels and mixes,
with both complete ID lists from the latest state. Hidden channels remain in
the lists. The drag inserts the source before or after its destination; it
does not exchange the two items or reorder intervening items. No optimistic
order is applied before the daemon's state and acknowledgement arrive.
The five window sections use a separate `sectionOrder` preference in
`ui.json` and in the profile's `presentation.sectionOrder`; it is independent
of routing and skin resources.

## Exclusive channel groups

The layout editor's **Exclusive groups** button creates or edits a named
group of channels. Choose at least two members and save. An existing group
can be selected to rename it, replace its members or delete it. A channel
can belong to only one group. Failed saves leave membership and mutes
unchanged; the dialog keeps the error visible for correction.

The settings file stores `exclusiveGroups`, for example:

```json
"exclusiveGroups": [
  {"id":"microphones","name":"Microphones","channels":["xlr1","headset"]}
]
```

Membership and each mix's selection survive restarts and are included in
new profiles. Legacy profiles preserve membership. Channel deletion removes
that member; a group with fewer than two channels dissolves without changing
remaining mutes. Malformed settings entries and overlapping groups are
ignored on load. The limits are 16 groups and 2 to 35 members per group.

Use the existing send mute button to open a member in a particular mix.
The other members close in that mix, keeping their levels and their sends
in every other mix. All members may be muted. When creating a group or
recalling a profile with multiple open members in a mix, that whole group
starts silent in the affected mix. Select the wanted member explicitly.

The commands are `setExclusiveGroup {group?, name, channels}`,
`deleteExclusiveGroup {group}` and `cycleExclusiveGroup {group, mix}`.
The first two save before replying. Cycling uses the saved member order
and resolves from current daemon state, including repeated key presses.
No nodes or additional gain stages are created for groups.


### Channel insert paths

All channel ids are valid insert keys. Software and external-capture channels
use the public `OpenXLR_ch_<id>` sink. While a chain exists, a hidden
`OpenXLR_bus_<id>` combine distributes the processed signal to the mixes. Display
order does not alter effect order or sends. Adding the first or removing the
last insert recreates only that channel sink with the same name. Empty chains
use the original combine sink and no hidden bus. Deleting a user channel removes its
insert definition in the same saved settings change as its routing.

Compact mix and mini-view selection are window presentation, stored in ui.json and profiles, not audio layout. They retain stable mix IDs and never change send levels or routing priority.
