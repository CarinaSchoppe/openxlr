# GNOME focused application routing

The optional OpenXLR Focus extension supplies GNOME's current application
PID when a routing key is pressed. It is not a focus monitor: it keeps no
history, reads no titles or application names and emits no focus signals.
The method returns no application on the lock screen, login screen,
overview or when the focused window is minimized or gone.

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
manager. The AUR recipe is external: its maintainer must add these files
when this extension lands. No Flatpak host bridge or new sandbox permission
is part of this feature; the OpenXLR Flatpak still cannot query the compositor.

## Access and compatibility

Only the session-bus connection owning `org.openxlr.Desktop` may call the
extension. Other callers receive AccessDenied before focus is read. When
the owner exits or changes, the previous owner loses access. The UI queries
on its existing desktop connection. A stalled extension retains at most
one unanswered request, preserves desktop shortcuts and retries after the
reply arrives or the UI's desktop connection is recreated. A late reply
is never reused as a fresh focus result.

The metadata currently names only Shell 46. A private headless Shell 46
run does not count as owner desktop acceptance. Before merge, test focus
changes between native and Flatpak applications, the overview and lock
screen, extension disable/re-enable and an OpenXLR restart on a normal
GNOME desktop. Record the actual Shell version, and add other versions to
metadata only after testing them. The roadmap item remains open.

The Node checks cover the focus filter. The GJS test exercises ownership
and lifecycle on a private bus without a GNOME desktop:

```sh
node --test packaging/gnome/tests/*.test.mjs
dbus-run-session -- gjs -m packaging/gnome/tests/bus.mjs
```
