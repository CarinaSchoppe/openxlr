# Focused application routing

Enable Desktop keys in Options and leave OpenXLR running, including in the
tray. A PC shortcut or an OpenDeck key can route the focused application to
an application channel. PC shortcuts use the desktop portal; Deck routing
works without its GlobalShortcuts interface.

## Focus sources

KDE Plasma uses a temporary KWin script, Sway uses `swaymsg get_tree`,
Hyprland uses `hyprctl activewindow`, Niri uses `niri msg --json focused-window`,
and X11 uses `xprop` with EWMH properties. Launch OpenXLR from the session
so it inherits the correct compositor socket or display. A named desktop
takes precedence over stale environment hints from another compositor.
Niri is invoked with the same socket value that passed validation.
X11 checks the client machine against both the local short name and full
kernel hostname and refuses remote clients. It rechecks the active window
after reading its PID. Xwayland cannot identify a focused Wayland window.

The native daemon asks the UI through `gdbus`, with a bounded list of live
PipeWire process keys. The UI first matches the exact host process, then
its descendants, with a bounded parent walk. Namespace and parent walks
check the request deadline before returning matches. The daemon accepts
only keys it requested and rechecks the current audio identity before saving an
assignment. Missing or ambiguous identities never guess from window titles.

## Flatpak applications

A PipeWire audio client with a portal instance id reports a PID inside its
sandbox. Native OpenXLR resolves that instance through `flatpak ps` and the
host's `/proc` namespace and `NSpid` records. Instances sharing a namespace
remain separate candidates. An invalid instance id is skipped; it is never
converted to a native PID. If Flatpak is missing, times out or refuses the
query, native candidates can still match. A sandbox candidate without a
mapping remains unavailable. No Flatpak host execution permission is added.
OpenXLR itself running inside Flatpak still has no compositor access.

## Errors and acceptance

Keep the UI running with Desktop keys enabled and install `gdbus` from the
GLib tools. If the daemon was updated while an older UI remains running,
restart the UI too. The compositor reports no focused application when the
desktop or its overview has no application focus. The request changes no
routing if the compositor, process mapping or unique audio identity is
unavailable. A disappearing audio client is rechecked at the daemon.

Hyprland has owner live-session acceptance. Sway, Niri and this X11 source
still need owner desktop acceptance; private D-Bus, process, PipeWire and
Xvfb tests do not replace it. The roadmap item remains open. GNOME needs a
separate focus source and is not included here.
