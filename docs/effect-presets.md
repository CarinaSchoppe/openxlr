# Effect presets

The existing Effects window saves whole chains, copies them between channels
and stores A/B snapshots for comparison. The generated controls additionally
open Plugin presets for the selected effect. Single-effect presets are filtered
by exact plugin ID and format. Applying one keeps the target slot ID and every
other effect, using the same validated whole-chain replacement as the existing
workflow. The daemon still verifies catalogue availability, parameters and the
target channel width before changing the live chain.

Both windows import and export portable `.openxlr-effects.json` files:

```json
{
  "name": "Speech",
  "chain": {
    "version": 1,
    "channels": 2,
    "inserts": [{
      "id": "gate",
      "kind": "lv2",
      "plugin": "urn:example:gate",
      "label": "Speech gate",
      "bypass": false,
      "nativeHost": true,
      "params": {"threshold": -30}
    }]
  }
}
```

These are parameter snapshots, not plugin-private binary states. They do not
include samples, native editor geometry or software installations. An import
only saves a reusable preset; it never installs a plugin or applies a chain
automatically. An effect preset must contain exactly one matching plugin when
imported through Plugin presets. The chain window can import either shape.

The shared store holds at most 64 names, compared without case. Duplicate names
are refused rather than overwritten. Names have 1 to 80 printable characters;
chains have at most 16 effects and 256 finite parameters per effect. Files and
the combined store are limited to 8 MiB. Import reads incrementally with that
bound. Unknown versions, malformed values, duplicate IDs and invalid formats
are refused before saving. A corrupt existing store is preserved.

Local exports use atomic replacement. Other desktop storage providers use
streamed writes and their own replacement guarantees; those writes cannot be
promised atomic by OpenXLR. Streaming import/export has a thirty-second timeout
and closing the window cancels the operation. An outstanding chooser cannot
import into a removed effect. Native chooser and network-open timeouts are
owned by the desktop storage provider.
An individual effect's preset window survives chain reordering or removal of
another effect. Removing its own effect or replacing that slot with a different
plugin closes the window and cancels any outstanding import.

Choose a preset name before exporting the live chain, or use Current chain /
Current effect. Selecting a saved preset exports that snapshot. Importing does
not start Sound Check or overwrite A/B slots; a recorded dry sample keeps
looping through the current chain while presets or comparison slots are applied.
Latency reporting and optional compensation continue through the existing
insert host and routing implementation, rather than a separate preset path.
