import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import {focusedProcess} from './focus.mjs';
import {FocusService} from './service.mjs';

export default class OpenXlrFocus extends Extension {
  enable() {
    this._focus = new FocusService(() => focusedProcess(global.display, Main.sessionMode, Main.overview));
    this._focus.enable();
  }

  disable() {
    this._focus?.disable();
    this._focus = null;
  }
}
