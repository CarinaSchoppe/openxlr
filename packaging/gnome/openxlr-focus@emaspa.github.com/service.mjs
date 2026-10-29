import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {interfaceXml} from './focus.mjs';

// Restrict the extension endpoint to the UI's desktop connection.
export class FocusService {
  constructor(readFocus) {
    this._readFocus = readFocus;
    this._generation = 0;
  }

  enable(connection = Gio.DBus.session) {
    if (this._object) return;
    const generation = ++this._generation;
    try {
      this._watch = Gio.bus_watch_name_on_connection(connection, 'org.openxlr.Desktop',
        Gio.BusNameWatcherFlags.NONE,
        (_connection, _name, owner) => {
          if (generation === this._generation) this._desktopOwner = owner;
        }, () => {
          if (generation === this._generation) this._desktopOwner = null;
        });
      this._object = Gio.DBusExportedObject.wrapJSObject(interfaceXml, this);
      // Gio.DBus.session is also the connection exporting org.gnome.Shell.
      // Do not acquire a separate, independently replaceable service name.
      this._object.export(connection, '/org/openxlr/Focus');
    } catch (error) {
      this.disable();
      throw error;
    }
  }

  GetFocusedProcessAsync(_parameters, invocation) {
    if (!this._desktopOwner || invocation.get_sender() !== this._desktopOwner) {
      invocation.return_dbus_error('org.freedesktop.DBus.Error.AccessDenied',
        'Only the running OpenXLR desktop service may read focus.');
      return;
    }
    try {
      invocation.return_value(new GLib.Variant('(u)', [this._readFocus()]));
    } catch (_error) {
      invocation.return_dbus_error('org.openxlr.Focus.Unavailable',
        'GNOME Shell could not read the focused application.');
    }
  }

  disable() {
    ++this._generation;
    this._desktopOwner = null;
    if (this._watch) Gio.bus_unwatch_name(this._watch);
    this._watch = 0;
    this._object?.unexport();
    this._object = null;
  }
}
