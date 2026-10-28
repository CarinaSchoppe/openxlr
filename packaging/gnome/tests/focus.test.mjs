import assert from 'node:assert/strict';
import test from 'node:test';
import {focusedProcess, interfaceXml} from '../openxlr-focus@emaspa.github.com/focus.mjs';

test('the GNOME source exports only an on-demand process id', () => {
  assert.deepEqual([...interfaceXml.matchAll(/method name="([^"]+)"/g)].map(m => m[1]), ['GetFocusedProcess']);
  let pid = 42;
  const window = {minimized: false, has_focus: () => true, get_pid: () => pid,
    get_title: () => { throw new Error('window titles must not be read'); }};
  const display = {focus_window: window}, session = {currentMode: 'user'}, overview = {visible: false};
  assert.equal(focusedProcess(display, session, overview), 42);
  assert.equal(focusedProcess(display, {currentMode: 'ubuntu', parentMode: 'user'}, overview), 42);
  assert.equal(focusedProcess(display, {currentMode: 'user', isLocked: true}, overview), 0);
  assert.equal(focusedProcess(display, {currentMode: 'gdm', isGreeter: true}, overview), 0);
  pid = 73;
  assert.equal(focusedProcess(display, session, overview), 73);
  for (const bad of [0, -1, null, '42', NaN, Infinity, 1.5, 2147483648]) {
    pid = bad;
    assert.equal(focusedProcess(display, session, overview), 0);
  }
});

test('no previous application is reported on the overview, lock screen or after focus disappears', () => {
  const display = {focus_window: {minimized: false, has_focus: () => true, get_pid: () => 42}};
  assert.equal(focusedProcess(display, {currentMode: 'unlock-dialog'}, {visible: false}), 0);
  assert.equal(focusedProcess(display, {currentMode: 'user'}, {visible: true}), 0);
  display.focus_window.minimized = true;
  assert.equal(focusedProcess(display, {currentMode: 'user'}, {visible: false}), 0);
  display.focus_window.minimized = false;
  display.focus_window.has_focus = () => false;
  assert.equal(focusedProcess(display, {currentMode: 'user'}, {visible: false}), 0);
  display.focus_window = null;
  assert.equal(focusedProcess(display, {currentMode: 'user'}, {visible: false}), 0);
});
