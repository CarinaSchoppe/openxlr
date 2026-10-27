#!/usr/bin/env python3
"""Check prune-cargo-vendor.py on a small vendored tree."""
import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('prune', HERE / 'prune-cargo-vendor.py')
prune = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prune)


def crate(vendor, directory, name, files):
    root = vendor / directory
    root.mkdir(parents=True)
    listed = {}
    for path, data in files.items():
        target = root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        listed[path] = hashlib.sha256(data).hexdigest()
    manifest = f'[package]\nname = "{name}"\nversion = "0.1.0"\n'.encode()
    (root / 'Cargo.toml').write_bytes(manifest)
    listed['Cargo.toml'] = hashlib.sha256(manifest).hexdigest()
    (root / '.cargo-checksum.json').write_text(json.dumps({'files': listed, 'package': 'ab' * 32}))
    return root


class PruneTests(unittest.TestCase):
    def test_import_libraries_and_dh_clean_files_go_with_their_checksums(self):
        with tempfile.TemporaryDirectory() as tmp:
            vendor = Path(tmp)
            windows = crate(vendor, 'windows_x86_64_gnu', 'windows_x86_64_gnu', {
                'lib/libwindows.0.48.5.a': b'x' * 1000, 'src/lib.rs': b'', 'build.rs': b'fn main() {}',
                'Cargo.toml.orig': b'[package]'})
            winapi = crate(vendor, 'winapi-x86_64-pc-windows-gnu', 'winapi-x86_64-pc-windows-gnu', {
                'lib/libwinapi_user32.a': b'y' * 10, 'src/lib.rs': b''})
            # A crate named like a Windows one but not one of them keeps its lib/.
            ordinary = crate(vendor, 'tract-linalg-0.19.16', 'tract-linalg', {
                'lib/data.bin': b'z', 'src/lib.rs': b'', 'Cargo.toml.orig': b'[package]',
                'src/old.rs~': b'', 'src/__pycache__/x.pyc': b'',
                '.gitmodules': b'[submodule]', 'tests/.git/HEAD': b'ref', 'tests/fixture.so': b'elf'})

            files, size = prune.prune(vendor)

            # Two import libraries, two Cargo.toml.orig, a backup, a .pyc,
            # and three files dpkg-source would leave out.
            self.assertEqual(9, files)
            self.assertEqual(1000 + 10 + 2 * len(b'[package]') + len(b'[submodule]') + 3 + 3, size)
            self.assertFalse((ordinary / 'tests').exists())
            self.assertFalse((windows / 'lib').exists())
            self.assertFalse((winapi / 'lib').exists())
            self.assertTrue((ordinary / 'lib/data.bin').is_file())
            for root in (windows, winapi, ordinary):
                checksums = json.loads((root / '.cargo-checksum.json').read_text())
                self.assertEqual('ab' * 32, checksums['package'])
                on_disk = {str(p.relative_to(root)) for p in root.rglob('*')
                           if p.is_file() and p.name != '.cargo-checksum.json'}
                self.assertEqual(on_disk, set(checksums['files']))
                self.assertFalse(any(name.endswith('.orig') for name in on_disk))
            self.assertEqual({'Cargo.toml', 'build.rs', 'src/lib.rs'},
                             set(json.loads((windows / '.cargo-checksum.json').read_text())['files']))

    def test_the_crate_name_comes_from_the_manifest(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = crate(Path(tmp), 'windows_i686_msvc-0.48.5', 'windows_i686_msvc', {})
            self.assertEqual('windows_i686_msvc', prune.crate_name(root))


if __name__ == '__main__':
    unittest.main()
