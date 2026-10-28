import Gio from 'gi://Gio';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import {focusedProcess, interfaceXml} from './focus.mjs';

export default class OpenXlrFocus extends Extension {
  enable() {
    this._owner = Gio.bus_own_name(Gio.BusType.SESSION, 'org.openxlr.Focus',
      Gio.BusNameOwnerFlags.DO_NOT_QUEUE, null, connection => {
        this._object = Gio.DBusExportedObject.wrapJSObject(interfaceXml, this);
        this._object.export(connection, '/org/openxlr/Focus');
      }, () => this._unexport());
  }

  GetFocusedProcess() {
    return focusedProcess(global.display, Main.sessionMode, Main.overview);
  }

  _unexport() {
    this._object?.unexport();
    this._object = null;
  }

  disable() {
    this._unexport();
    if (this._owner) Gio.bus_unown_name(this._owner);
    this._owner = 0;
  }
}
