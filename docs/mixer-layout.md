# Saved mixer layout

The daemon reads the layout from `mixer.json` before building its graph.
Stop the daemon before editing this file manually: while running, its normal
settings saves overwrite the file with the live configuration.

`userChannels` is an ordered list of application channels and `userMixes` an
ordered list of virtual microphones. Each entry has a stable `id` and a display
`name`. For example:

```json
{
  "userChannels": [{"id": "podcast", "name": "Interview"}],
  "userMixes": [{"id": "recording", "name": "Recording"}]
}
```

These are fields in the existing settings object; retain its other fields when
editing. Missing or null lists keep the legacy defaults. An empty mix list
removes the editable virtual microphones. An empty application list falls back
to System so incoming applications have a destination.

Hardware inputs, Monitor A (`monitor`), Monitor B (`monitor2`) and Aux
(`auxout`) remain structural. The `ignore` application target is reserved.
Invalid or duplicate entries are ignored. IDs contain at most 36 lowercase
ASCII letters, digits, underscores or hyphens, beginning with a letter. Names
contain 1 to 60 printable characters. At most 32 application channels and 16
virtual microphones are restored.

Node names derive from IDs, not labels. The list order survives a settings
save and restart. Removed application destinations fall back to the first
application channel, never a hardware input; ignored applications stay ignored.
Existing monitor-feed settings and profile semantics are unchanged.

This format does not provide live layout commands or a desktop layout editor.
Manual changes, including external PipeWire descriptions, take effect at startup.
