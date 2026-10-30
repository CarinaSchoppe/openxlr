# GNOME focused application routing

The optional OpenXLR Focus extension supplies GNOME Shell's current
application PID on Wayland when a routing key is pressed. A GNOME X11
session uses the native X11 focus source described in the manual. It is not a focus monitor: it keeps no
history, reads no titles or application names and emits no focus signals.
The extension stays enabled in the regular user session and its unlock
dialog. It returns no application while locked, in the overview or when
the focused window is minimized or gone. It does not run in the login
screen session.

## Enable and remove

The native Debian, RPM and Nix packages include the extension under
`share/gnome-shell/extensions/openxlr-focus@emaspa.github.com`. It is not
enabled automatically. In GNOME, enable it with:

```sh
gnome-extensions enable openxlr-focus@emaspa.github.com
```

Log out and back in if GNOME has not discovered newly installed files.
For a source checkout, copy the extension to your user data directory:

```sh
install -d "${XDG_DATA_HOME:-$HOME/.local/share}/gnome-shell/extensions/openxlr-focus@emaspa.github.com"
install -m644 packaging/gnome/openxlr-focus@emaspa.github.com/* \
  "${XDG_DATA_HOME:-$HOME/.local/share}/gnome-shell/extensions/openxlr-focus@emaspa.github.com/"
```

Then enable Desktop keys in OpenXLR, leave the UI running, and choose a PC
shortcut or an OpenDeck focused application key. A missing GlobalShortcuts
portal prevents PC shortcuts but leaves Deck routing available.

To stop exposing the focus source:

```sh
gnome-extensions disable openxlr-focus@emaspa.github.com
```

Remove a manual user copy by deleting only the four files you copied from
that extension directory. Package-installed files belong to the package
manager. The AUR recipe, maintained outside this repository, installs the
same four files. No Flatpak host bridge or new sandbox permission
is part of this feature; the OpenXLR Flatpak still cannot query the compositor.

## Access and compatibility

The extension exports `/org/openxlr/Focus` on GNOME Shell's own session-bus
connection. The UI calls `org.gnome.Shell`, using the `org.openxlr.Focus`
interface; the extension owns no separate service name. A process owning
`org.openxlr.Focus` cannot replace that endpoint. A session without the
Shell service reports that it has no GNOME Shell focus source.

GNOME Flashback and Budgie 10.9 and earlier also own `org.gnome.Shell` and
list GNOME in `XDG_CURRENT_DESKTOP`, but run no GNOME Shell, so the
extension cannot load there. OpenXLR recognises a `GNOME-Flashback` or
`Budgie` entry in `XDG_CURRENT_DESKTOP` or `XDG_SESSION_DESKTOP` and never
asks for the extension. In an X11 session it reads focus through the X11
source, as on other X11 desktops. In any other session focused routing
reports that the desktop has no focus source.

GNOME Shell on X11 uses the X11 source too, which supports native
applications only. The extension is the focus source for GNOME Shell on
Wayland.

The extension endpoint accepts only the session-bus connection owning
`org.openxlr.Desktop`. Other direct callers receive AccessDenied before
focus is read. This is not a session-wide focus privacy guarantee: the
UI's existing desktop interface can itself be called by other session
processes, as on the other compositors. When the owner exits or changes,
the previous owner loses access. The UI queries on its existing desktop
connection. A stalled extension retains at most one unanswered request, preserves desktop shortcuts and retries after the
reply arrives or the UI's desktop connection is recreated. A late reply
is never reused as a fresh focus result. If reading focus throws, the
extension returns a D-Bus error so the next request can run. Only the
declared unsigned PID reply is accepted; a different D-Bus signature is
refused without closing the desktop connection.

The metadata names GNOME Shell 46, 47, 48, 49 and 50. We tested each one
headless, not in a desktop session, running `gnome-shell --headless` with
a virtual monitor on a private session bus in containers with Shell 46.0
(Ubuntu 24.04), 47.10 (Fedora 41), 48.7 (Debian 13), 48.8 (Fedora 42),
49.10 (Fedora 43), 50.1 (Ubuntu 26.04) and 50.6 (Fedora 44). On each the
extension enabled with no error, and a caller without
`org.openxlr.Desktop` received AccessDenied, before and after that name
had an owner. The owner received no application while the overview was
visible, the PID of a focused GTK 4 Wayland window, and no application
again once that window closed. Disabling the extension removed the
endpoint and enabling it restored it.

A headless run does not count as owner desktop acceptance. That still has
to cover focus changes between native and Flatpak applications, the lock
screen and an OpenXLR restart on a normal GNOME desktop, with the Shell
version recorded. Add a Shell version to the metadata only after testing
it. The roadmap item remains open.

The Node checks cover the focus filter. The GJS test exercises ownership
and lifecycle on a private bus without a GNOME desktop:

```sh
node --test packaging/gnome/tests/*.test.mjs
dbus-run-session -- gjs -m packaging/gnome/tests/bus.mjs
```
