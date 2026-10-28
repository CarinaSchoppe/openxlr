// Test input for GNOME 46's virtual desktop. The production extension sends no input.
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

const service = 'org.gnome.Mutter.RemoteDesktop';
function call(path, iface, method, args = null) {
    return Gio.DBus.session.call_sync(service, path, iface, method, args, null, Gio.DBusCallFlags.NONE, 2000, null);
}
const path = call('/org/gnome/Mutter/RemoteDesktop', service, 'CreateSession').deepUnpack()[0];
call(path, `${service}.Session`, 'Start');
call(path, `${service}.Session`, 'NotifyKeyboardKeysym', new GLib.Variant('(ub)', [0xff1b, true]));
call(path, `${service}.Session`, 'NotifyKeyboardKeysym', new GLib.Variant('(ub)', [0xff1b, false]));
GLib.usleep(300000);
call(path, `${service}.Session`, 'Stop');
