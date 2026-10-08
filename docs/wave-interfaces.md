# Multiple Wave interfaces

The primary interface retains the existing device picker, hardware input strips
and model-scoped profiles. Options, AUDIO, Wave interfaces can enable up to
four additional attached units for simultaneous hardware control and audio
input. Each additional manager reuses the primary manager's USB isolation,
reconnect backoff, gain lock, phantom settling and remembered settings policy.

An exact USB bus/address selects the physical unit. A helper that cannot select
an address refuses the open; it never falls back to the first matching product.
A serial-based instance ID remains stable across a replug or physical port
change. Devices without a unique serial use the physical port, so moving one
changes its ID. Duplicate serials are treated as absent serials. A unit with
repeated hung transfers is set aside independently of peers of the same model.
A changed USB address also identifies a replug when it happened between discovery
ticks, allowing that unit's hung-transfer budget to restart.

The window lists the primary and additional roles separately. Disabling an
additional role releases its control handle but does not remove existing mixer
routing. Selecting it as primary releases its additional handle before the
primary opens it. Each model's existing controls remain capability-gated.
The API exposes all existing hardware controls per instance; the window exposes
microphone gain, mute, hardware low cut, ClipGuard and phantom where supported.
Built-in software low cut and limiting remain on the primary XLR input.

After enabling a unit, select its PipeWire capture source and microphone, then
add an input channel. A unique source is selected automatically. Ambiguous
sources require an explicit selection. Two indistinguishable Pro audio cards
cannot be automatically switched to pro-audio and enabling the additional
role is refused. An ambiguous primary capture hint stays silent and reports a
warning. This avoids controlling or capturing a different microphone.

A new input starts muted in every mix. It is a normal external capture channel
with stereo inserts, levels, meters, preset copying, A/B and latency
compensation. One selected mono input port feeds both sides of the channel.
The second Pro microphone selects the next input pair's first port. A missing
port or disconnected source stays silent and reconnects by its exact node
name; another microphone never substitutes for it. Sound Check records that
port and loops it before the live channel chain. Sound Check can run on one
microphone at a time and keeps its existing ten-second sample and ten-minute
session limits.

Remembered offline units remain visible so their additional role can be disabled
and its enable slot freed without reconnecting the unit.

Additional enabled IDs are stored atomically in `wave-interfaces.json`.
Unreadable or malformed preferences are reported and kept rather than replaced.
Additional remembered hardware states use their instance IDs under `devices/`.
Saved profiles optionally snapshot the enabled, connected secondary units;
recall applies to those exact units if still enabled and connected. It never
enables a device, changes the primary selection or opens an absent interface.
Capture bindings, application routing and enabled-device choices stay global,
as existing profiles represent sound settings rather than machine wiring.

The code and simulated hotplug/control tests cover multiple units. Acceptance
with two physical interfaces is still required; one connected XLR Dock does
not establish that a second model's port map, firmware and PipeWire card profile
have been verified together. See [hardware support](hardware-support.md).

Profile recall reads current hardware state before comparing values, so a dial
change between poll ticks cannot make an otherwise identical profile skip a
required hardware write. This uses one extra read per recall, not per poll.
