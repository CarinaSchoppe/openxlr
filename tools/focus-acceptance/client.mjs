import Gtk from 'gi://Gtk?version=3.0';
import GLib from 'gi://GLib';

const application = new Gtk.Application({application_id: `org.openxlr.FocusFixture.${GLib.getenv('FIXTURE_ID')}`});
application.connect('activate', () => {
    const window = new Gtk.ApplicationWindow({application, title: 'Focus acceptance', default_width: 300, default_height: 200});
    window.show_all();
    window.present();
});
application.run([]);
