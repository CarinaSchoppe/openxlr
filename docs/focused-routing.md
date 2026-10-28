# Focused application routing

Enable Desktop keys in Options and leave the UI running, including in the
tray. A PC shortcut needs the GlobalShortcuts portal. An OpenDeck routing
key only needs the UI's focus service and works without that portal. The
daemon needs `gdbus` from GLib's command-line tools.

The source follows the desktop session. An explicitly named desktop takes
precedence over leftover IPC variables from another session. Unknown
Wayland compositors report an error instead of using Xwayland windows or
matching titles.

| Session | Source | Setup |
| --- | --- | --- |
| KDE Plasma | KWin scripting, active window's PID | KWin's scripting service |
| GNOME Wayland | OpenXLR Focus Shell extension | Enable the extension below |
| Sway | `swaymsg -r -t get_tree` | Session's `SWAYSOCK`, `swaymsg` installed |
| Hyprland | `hyprctl -j activewindow` | Session's `HYPRLAND_INSTANCE_SIGNATURE`, `hyprctl` installed |
| Niri | `niri msg --json focused-window` | Session's `NIRI_SOCKET`, `niri` installed |
| X11, including Xfce, Cinnamon, MATE and GNOME Xorg | EWMH `_NET_ACTIVE_WINDOW`, `_NET_WM_PID`, `WM_CLIENT_MACHINE` | `xprop` installed; window manager supports EWMH |

GNOME, Sway, Niri and Openbox X11 have been exercised with real focused GTK
windows in isolated software-rendered sessions. Hyprland has previously
been exercised in a live session. This does not replace acceptance in an
owner's normal desktop session or in SteamOS Game Mode.

Only the process ID is used. The X11 source rejects remote clients and a
window that changes between property reads. The exact process takes
precedence; otherwise a unique audio identity among descendants within
16 parent links is required. No client, several audio identities, a
missing helper, malformed reply or a timeout leaves routing unchanged.
Compositor replies are bounded in size and time, and only one query runs
at once.

## GNOME Shell extension

OpenXLR Focus supports GNOME Shell 45 through 51. It exposes only
`org.openxlr.Focus.GetFocusedProcess` on the session bus. It does not read
window titles, keep focus history or register shortcuts. Locked sessions,
the overview, the greeter and missing or minimized focused windows return
no process. No Shell Eval permission is required.

Native Debian, RPM and Nix packages include the extension under their
share directory. Enable it after logging out and back in:

```sh
gnome-extensions enable openxlr-focus@emaspa.github.com
```

For a source checkout, install just the extension as your user, then log
out and back in and enable it:

```sh
mkdir -p "${XDG_DATA_HOME:-$HOME/.local/share}/gnome-shell/extensions"
cp -r packaging/gnome/openxlr-focus@emaspa.github.com "${XDG_DATA_HOME:-$HOME/.local/share}/gnome-shell/extensions/"
```

For Flatpak, copy it from the installed application on the host:

```sh
mkdir -p "${XDG_DATA_HOME:-$HOME/.local/share}/gnome-shell/extensions"
cp -r "$(flatpak info --show-location io.github.emaspa.openxlr)/files/share/gnome-shell/extensions/openxlr-focus@emaspa.github.com" "${XDG_DATA_HOME:-$HOME/.local/share}/gnome-shell/extensions/"
```

The extension can be disabled independently. The next routing request
then reports that it needs enabling; no previous focus is reused. Update
a user-installed copy when updating OpenXLR.

## Optional Flatpak host bridge

The default sandbox cannot query the host compositor and its `/proc`
contains a different process namespace. The optional bridge runs a
bounded query on the host using the installed Flatpak's bundled .NET
runtime and UI assembly. No separately installed host daemon or .NET SDK
is needed. The host resolves candidate audio processes and their parents;
the sandbox daemon rechecks the resulting IDs against its current audio
clients and refuses multiple identities. PipeWire portal instance IDs keep
Flatpak audio clients separate from native clients with the same numeric
PID. On the host, `flatpak ps` identifies their PID namespaces and `/proc`
maps namespace PIDs to host PIDs. An unavailable instance is refused. X11
clients must report a usable host PID; namespace-local X11 PIDs cannot be
inferred from window titles or application names.

On the host, explicitly enable host execution and the bridge, then
restart the Flatpak UI:

```sh
flatpak override --user --talk-name=org.freedesktop.Flatpak --env=OPENXLR_FOCUS_HOST=1 io.github.emaspa.openxlr
```

This permission allows host command execution for the application. It is
not granted by the package. Disable the bridge and remove that permission
without resetting other overrides:

```sh
flatpak override --user --no-talk-name=org.freedesktop.Flatpak --unset-env=OPENXLR_FOCUS_HOST io.github.emaspa.openxlr
```

The bridge forwards the UI session's compositor variables listed above;
the host must provide the corresponding tools. GNOME still needs the Shell extension. Game Mode's compositor
needs its own focus source; enabling the bridge does not add one.

## Reproduce desktop acceptance

Build the Release test assembly first. The runner uses Docker, separate
session and system buses, virtual displays and software rendering. It
mounts the checkout read-only and does not connect to the host desktop or
audio server:

```sh
dotnet build src/OpenXLR.slnx -c Release
python3 tools/test-compositor-focus.py
```

Choose `--source gnome`, `sway`, `niri` or `x11` to run one source, and
`--no-build` to reuse its container image. Each session changes between
two real GTK processes, closes one and verifies the remaining focus
through both the source and the UI's D-Bus service. GNOME additionally
disables and re-enables its extension.

Protocol references: [Mutter Window](https://mutter.gnome.org/meta/class.Window.html),
[Niri IPC](https://github.com/niri-wm/niri/wiki/IPC),
[EWMH root properties](https://specifications.freedesktop.org/wm/latest/ar01s03.html),
[EWMH application properties](https://specifications.freedesktop.org/wm/latest/ar01s05.html),
and [Flatpak command reference](https://docs.flatpak.org/en/latest/flatpak-command-reference.html).

With the Flatpak installed and a stable focused application, run
`python3 tools/test-flatpak-focus.py` on the host. It checks refusal without
opt-in and without permission, then compares the sandbox result with host
queries before and after it. Permissions are passed only to those test
launches; persistent overrides stay unchanged.

To include a real sandboxed PulseAudio stream in the private audio test,
set `FLATPAK_USER_DIR` to the user's existing Flatpak installation and run
`OPENXLR_TEST_FLATPAK_AUDIO=1 OPENXLR_TEST_FILTER=FullyQualifiedName~FlatpakAudioProcessIsMatchedThroughItsInstanceAndRoutesWithoutPidCollision python3 tools/test-monitor-volume.py`.
This opt-in test uses an installed OpenXLR Flatpak and sends silence to a
private PipeWire sink. It checks namespace PID resolution and the actual
channel move without connecting to host audio.
