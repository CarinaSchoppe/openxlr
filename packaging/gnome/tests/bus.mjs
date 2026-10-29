// Private GIO/D-Bus integration, not GNOME desktop acceptance.
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import System from 'system';
import {FocusService} from '../openxlr-focus@emaspa.github.com/service.mjs';

function connection() {
  return Gio.DBusConnection.new_for_address_sync(GLib.getenv('DBUS_SESSION_BUS_ADDRESS'),
    Gio.DBusConnectionFlags.AUTHENTICATION_CLIENT | Gio.DBusConnectionFlags.MESSAGE_BUS_CONNECTION, null, null);
}
function call(bus, destination, path, iface, method, parameters = null) {
  return new Promise((resolve, reject) => bus.call(destination, path, iface, method, parameters,
    null, Gio.DBusCallFlags.NONE, 2000, null, (source, result) => {
      try { resolve(source.call_finish(result).deepUnpack()); } catch (error) { reject(error); }
    }));
}
function check(value, message) { if (!value) throw new Error(message); }
async function wait(checkReady) {
  for (let i = 0; i < 200; ++i) {
    if (checkReady()) return;
    await new Promise(resolve => GLib.timeout_add(GLib.PRIORITY_DEFAULT, 10, () => { resolve(); return GLib.SOURCE_REMOVE; }));
  }
  throw new Error('The private service did not become ready.');
}
async function main() {
  let reads = 0, pid = 42, broken = false;
  const service = new FocusService(() => { ++reads; if (broken) throw new Error('Focus was destroyed.'); return pid; });
  const owner = connection(), stranger = connection(), shell = connection();
  const focus = bus => call(bus, 'org.gnome.Shell', '/org/openxlr/Focus', 'org.openxlr.Focus', 'GetFocusedProcess');
  async function refused(bus) {
    const before = reads;
    try { await focus(bus); } catch (error) {
      check(Gio.DBusError.get_remote_error(error) === 'org.freedesktop.DBus.Error.AccessDenied', String(error));
      check(reads === before, 'An unauthorized call read focus.');
      return;
    }
    throw new Error('An unauthorized caller received focus.');
  }
  async function name(bus, method) {
    return call(bus, 'org.freedesktop.DBus', '/org/freedesktop/DBus', 'org.freedesktop.DBus', method,
      new GLib.Variant(method === 'RequestName' ? '(su)' : '(s)', method === 'RequestName' ? ['org.openxlr.Desktop', 4] : ['org.openxlr.Desktop']));
  }
  try {
    await call(shell, 'org.freedesktop.DBus', '/org/freedesktop/DBus', 'org.freedesktop.DBus',
      'RequestName', new GLib.Variant('(su)', ['org.gnome.Shell', 4]));
    service.enable(shell);
    await wait(() => !!service._object);
    await refused(owner);
    await name(owner, 'RequestName');
    await wait(() => service._desktopOwner === owner.get_unique_name());
    await refused(stranger);
    check((await focus(owner))[0] === 42, 'The owner did not receive focus.');
    broken = true;
    try { await focus(owner); throw new Error('A focus exception was accepted.'); }
    catch (error) {
      check(Gio.DBusError.get_remote_error(error) === 'org.openxlr.Focus.Unavailable',
        `A focus exception did not receive a bounded D-Bus error: ${error}`);
    }
    broken = false;
    pid = 73;
    check((await focus(owner))[0] === 73, 'Focus was cached.');
    const beforeRelease = reads;
    const release = name(owner, 'ReleaseName');
    const immediatelyAfterRelease = focus(owner).then(value => ({value}), error => ({error}));
    await release;
    const immediate = await immediatelyAfterRelease;
    check(immediate.error && Gio.DBusError.get_remote_error(immediate.error) === 'org.freedesktop.DBus.Error.AccessDenied',
      'The former owner could read focus immediately after releasing the name.');
    check(reads === beforeRelease, 'The owner-loss race read focus.');
    await wait(() => !service._desktopOwner);
    await refused(owner);
    await name(stranger, 'RequestName');
    await wait(() => service._desktopOwner === stranger.get_unique_name());
    await refused(owner);
    check((await focus(stranger))[0] === 73, 'The replacement owner was refused.');
    service.disable();
    check(!service._desktopOwner && !service._watch && !service._object, 'Disabled service retained an export or owner.');
    service.enable(shell);
    await wait(() => service._desktopOwner === stranger.get_unique_name());
    await refused(owner);
    check((await focus(stranger))[0] === 73, 'Re-enabled service did not recover.');
    print('GNOME focus private D-Bus ownership, replacement and lifecycle checks passed.');
  } finally {
    service.disable();
    owner.close_sync(null);
    stranger.close_sync(null);
    shell.close_sync(null);
  }
}
const loop = new GLib.MainLoop(null, false);
let failed = false;
main().catch(error => { logError(error); failed = true; }).finally(() => loop.quit());
loop.run();
if (failed) System.exit(1);
