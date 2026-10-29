import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {interfaceXml} from './focus.mjs';

// Only the connection owning the UI's desktop service may read focus.
export class FocusService {
  constructor(readFocus) {
    this._readFocus = readFocus;
    this._generation = 0;
    this._watchGeneration = 0;
  }

  enable() {
    if (this._owner) return;
    const generation = ++this._generation;
    this._owner = Gio.bus_own_name(Gio.BusType.SESSION, 'org.openxlr.Focus',
      Gio.BusNameOwnerFlags.DO_NOT_QUEUE, null, connection => {
        if (generation !== this._generation) return;
        this._unexport();
        const watchGeneration = this._watchGeneration;
        this._watch = Gio.bus_watch_name_on_connection(connection, 'org.openxlr.Desktop',
          Gio.BusNameWatcherFlags.NONE,
          (_connection, _name, owner) => {
            if (watchGeneration === this._watchGeneration) this._desktopOwner = owner;
          }, () => {
            if (watchGeneration === this._watchGeneration) this._desktopOwner = null;
          });
        this._object = Gio.DBusExportedObject.wrapJSObject(interfaceXml, this);
        this._object.export(connection, '/org/openxlr/Focus');
      }, () => {
        if (generation === this._generation) this._unexport();
      });
  }

  GetFocusedProcessAsync(_parameters, invocation) {
    if (!this._desktopOwner || invocation.get_sender() !== this._desktopOwner) {
      invocation.return_dbus_error('org.freedesktop.DBus.Error.AccessDenied',
        'Only the running OpenXLR desktop service may read focus.');
      return;
    }
    invocation.return_value(new GLib.Variant('(u)', [this._readFocus()]));
  }

  _unexport() {
    ++this._watchGeneration;
    this._desktopOwner = null;
    if (this._watch) Gio.bus_unwatch_name(this._watch);
    this._watch = 0;
    this._object?.unexport();
    this._object = null;
  }

  disable() {
    ++this._generation;
    this._unexport();
    if (this._owner) Gio.bus_unown_name(this._owner);
    this._owner = 0;
  }
}
