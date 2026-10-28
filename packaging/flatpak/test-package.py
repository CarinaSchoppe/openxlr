#!/usr/bin/env python3
"""Check the package's dependency inventory, exports and sandbox permissions."""
import importlib.util
import json
import re
from pathlib import Path
import base64
import os
import shlex
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


generator = load('nuget_sources', 'nuget-sources.py')
cargo_generator = load('cargo_sources', 'cargo-sources.py')


def makefile_value(name):
    """A plain `NAME = value` from native/Makefile, with $(VAR) expanded."""
    text = (ROOT / 'native' / 'Makefile').read_text()
    value = re.search(rf'^{name}\s*=\s*(.+)$', text, re.M).group(1).strip()
    return re.sub(r'\$\((\w+)\)', lambda m: makefile_value(m.group(1)), value)


class PackageTests(unittest.TestCase):
    def test_locked_packages_and_downloads_agree(self):
        expected = generator.sources()
        actual = json.loads((HERE / 'nuget-sources.json').read_text())
        self.assertEqual(expected, [{k: v for k, v in item.items() if k != 'sha512'} for item in actual])
        for item in actual:
            self.assertRegex(item['sha512'], r'^[a-f0-9]{128}$')
            self.assertTrue(item['url'].startswith('https://api.nuget.org/v3-flatcontainer/'))
            self.assertNotIn('/', item['dest-filename'])

    def test_conflicting_normalized_package_identities_are_refused(self):
        with tempfile.TemporaryDirectory() as root:
            for project in ('OpenXLR.Core', 'OpenXLR.Daemon', 'OpenXLR.UI', 'OpenXLR.Tui'):
                folder = Path(root, 'src', project)
                folder.mkdir(parents=True)
                digest = base64.b64encode(bytes([0 if project == 'OpenXLR.Core' else 1]) * 64).decode()
                (folder / 'packages.lock.json').write_text(json.dumps({'dependencies': {'net10.0': {
                    'Example': {'type': 'Direct', 'resolved': '1.0', 'contentHash': digest}}}}))
            with self.assertRaisesRegex(ValueError, "Conflicting package hash") :
                generator.sources(Path(root))

    def test_permissions_are_specific_and_cannot_spawn_host_programs(self):
        manifest = json.loads((HERE / 'io.github.emaspa.openxlr.json').read_text())
        permissions = manifest['finish-args']
        self.assertIn('--device=usb', permissions)
        self.assertIn('--filesystem=xdg-run/pipewire-0:ro', permissions)
        self.assertIn('--filesystem=xdg-config/wireplumber/wireplumber.conf.d:create', permissions)
        self.assertNotIn('--filesystem=xdg-config/wireplumber:create', permissions)
        for permission in permissions:
            self.assertNotIn(permission, ('--device=all', '--filesystem=host', '--filesystem=home', '--socket=session-bus', '--socket=system-bus', '--talk-name=org.kde.KWin'))
            self.assertNotIn('org.freedesktop.Flatpak', permission)
        extension = manifest['add-extensions']['org.freedesktop.LinuxAudio.Plugins']
        self.assertEqual('lv2', extension['merge-dirs'])
        self.assertEqual(manifest['runtime-version'], extension['version'])

    def test_exports_share_the_application_id(self):
        manifest = json.loads((HERE / 'io.github.emaspa.openxlr.json').read_text())
        app = manifest['app-id']
        metadata = ET.parse(HERE / (app + '.metainfo.xml')).getroot()
        self.assertEqual(app, metadata.findtext('id'))
        self.assertEqual(app + '.desktop', metadata.findtext('launchable'))

    def test_service_script_is_packaged(self):
        build = (HERE / 'build.sh').read_text()
        self.assertIn('install -Dm755 packaging/flatpak/openxlr-flatpak-service /app/share/openxlr/openxlr-flatpak-service', build)
        script = (HERE / 'openxlr-flatpak-service').read_text()
        self.assertIn('--command=/app/lib/openxlr/daemon/OpenXLR.Daemon', script)
        self.assertIn('Restart=on-failure', script)

    @unittest.skipUnless(shutil.which('systemd-analyze'), 'needs systemd unit parsing')
    def test_service_paths_survive_systemd_parsing(self):
        for suffix in ('with spaces', 'with "quotes" and back\\slash', 'with 100%q'):
            with self.subTest(path=suffix), tempfile.TemporaryDirectory(prefix='openxlr-service-') as root:
                base = Path(root)
                binary = base / 'bin with spaces 100%q'
                binary.mkdir(parents=True)
                for name, body in (
                    ('flatpak', 'exit 0'),
                    ('systemctl', 'if [ "$2" = is-enabled ]; then exit 1; fi\nexit 0'),
                ):
                    program = binary / name
                    program.write_text('#!/bin/sh\n' + body + '\n')
                    program.chmod(0o755)
                config = base / 'config'
                data = base / suffix
                environment = {**os.environ, 'PATH': str(binary) + os.pathsep + os.environ['PATH'],
                               'XDG_CONFIG_HOME': str(config), 'XDG_DATA_HOME': str(data)}
                subprocess.run(['sh', str(HERE / 'openxlr-flatpak-service')], env=environment,
                               check=True, capture_output=True, text=True, timeout=10)
                unit = config / 'systemd/user/openxlr-flatpak.service'
                parsed = subprocess.run(['systemd-analyze', '--user', '--generators=no', 'verify', str(unit)],
                                        env={**environment, 'SYSTEMD_LOG_LEVEL': 'debug',
                                             'SYSTEMD_ENVIRONMENT_GENERATORS_PATH': str(base / 'no-generators')},
                                        capture_output=True, text=True, timeout=15)
                self.assertEqual(0, parsed.returncode, parsed.stderr)
                stop = parsed.stdout.split('ExecStop:', 1)[1].split('Command Line:', 1)[1].splitlines()[0]
                arguments = shlex.split(stop)
                self.assertEqual(['/bin/sh', str(data / 'openxlr/openxlr-flatpak-service'), 'stop'], arguments[:3])
                start = parsed.stdout.split('ExecStart:', 1)[1].split('Command Line:', 1)[1].splitlines()[0]
                self.assertEqual([str(binary / 'flatpak'), 'run', '--command=/app/lib/openxlr/daemon/OpenXLR.Daemon',
                                  'io.github.emaspa.openxlr'], shlex.split(start))

    def test_imports_are_private_and_do_not_grant_host_plugin_folders(self):
        permissions = json.loads((HERE / 'io.github.emaspa.openxlr.json').read_text())['finish-args']
        for folder in ('.lv2', '.clap', '.vst3'):
            self.assertIn('--persist=' + folder, permissions)
            self.assertFalse(any(item.startswith('--filesystem=') and folder in item for item in permissions))

    def test_cargo_crates_match_the_lock_file(self):
        expected = cargo_generator.sources()
        actual = json.loads((HERE / 'cargo-sources.json').read_text())
        self.assertEqual(expected, actual)
        lock = (ROOT / 'native' / 'deepfilter' / 'Cargo.lock').read_text()
        registry = lock.count('source = "registry+https://github.com/rust-lang/crates.io-index"')
        self.assertEqual(registry * 2, len(actual))
        for item in actual:
            self.assertTrue(item['dest'].startswith('native/deepfilter/vendor/'))
            if item['type'] == 'archive':
                self.assertRegex(item['sha256'], r'^[a-f0-9]{64}$')
                self.assertTrue(item['url'].startswith('https://static.crates.io/crates/'))
            else:
                self.assertEqual('.cargo-checksum.json', item['dest-filename'])

    def test_crates_from_elsewhere_are_refused(self):
        with tempfile.TemporaryDirectory() as root:
            lock = Path(root, 'Cargo.lock')
            lock.write_text('version = 3\n\n[[package]]\nname = "x"\nversion = "1.0.0"\n'
                            'source = "git+https://example.com/x"\n')
            with self.assertRaisesRegex(ValueError, 'only crates.io'):
                cargo_generator.sources(lock)

    def test_deepfilter_model_is_the_one_native_makefile_checks(self):
        manifest = json.loads((HERE / 'io.github.emaspa.openxlr.json').read_text())
        module = next(m for m in manifest['modules'] if m['name'] == 'openxlr')
        models = [s for s in module['sources']
                  if isinstance(s, dict) and s.get('dest-filename') == 'DeepFilterNet3_onnx.tar.gz']
        self.assertEqual(1, len(models))
        model = models[0]
        self.assertEqual(makefile_value('DF_MODEL_URL'), model['url'])
        self.assertEqual(makefile_value('DF_MODEL_SHA256'), model['sha256'])
        self.assertEqual('native/' + makefile_value('DF_MODEL'),
                         model['dest'] + '/' + model['dest-filename'])

    def test_deepfilter_is_built_in(self):
        manifest = json.loads((HERE / 'io.github.emaspa.openxlr.json').read_text())
        self.assertIn('org.freedesktop.Sdk.Extension.rust-stable', manifest['sdk-extensions'])
        self.assertEqual('/usr/lib/sdk/rust-stable/bin', manifest['build-options']['append-path'])
        module = next(m for m in manifest['modules'] if m['name'] == 'openxlr')
        self.assertIn('cargo-sources.json', module['sources'])
        tree = module['sources'][0]
        self.assertEqual('dir', tree['type'])
        for local in ('native/deepfilter/target', 'native/deepfilter/vendor', 'native/deepfilter/model'):
            self.assertIn(local, tree['skip'])
        build = (HERE / 'build.sh').read_text()
        self.assertIn('export DEEPFILTER=1\n', build)
        # Packaged notices of the code the native helper links.
        for notice in ('native/rnnoise/COPYING', 'native/deepfilter/libdf/LICENSE-MIT',
                       'native/deepfilter/libdf/LICENSE-APACHE'):
            self.assertIn(f'install -Dm644 {notice} ', build)
            self.assertTrue((ROOT / notice).is_file(), notice)

    def test_archives_are_pinned_and_no_git_source_floats(self):
        manifest = json.loads((HERE / 'io.github.emaspa.openxlr.json').read_text())
        for module in manifest['modules']:
            for source in module.get('sources', []):
                if not isinstance(source, dict):
                    continue
                if source['type'] == 'archive':
                    self.assertRegex(source['sha256'], r'^[a-f0-9]{64}$')
                    self.assertTrue(source['url'].startswith('https://'))
                if source['type'] == 'git':
                    self.assertRegex(source.get('commit', ''), r'^[a-f0-9]{40}$')


if __name__ == '__main__':
    unittest.main()
