#!/usr/bin/env python3
"""Trim a `cargo vendor` tree for the Ubuntu source package.

Three kinds of file go:

- The Windows import libraries in the crates that exist only to carry them
  (winapi-*-pc-windows-gnu, windows_<arch>_<abi>). Cargo never compiles
  those crates for a Linux target, and they are most of the tree's size.
- Files that dpkg-source leaves out of a native source tarball by default
  (.gitmodules in prost, wasi and minimal-lexical, object files, VCS
  directories).
- Files that dh_clean deletes from any source tree before a build
  (Cargo.toml.orig in every crate, editor backups, rejected patches).

Left in, the last two would be missing on the builder, and cargo refuses a
crate whose checksum list names a file that is not there.

Every removed file is taken out of its crate's .cargo-checksum.json as well,
so cargo still accepts the crate. The package checksum, which Cargo.lock
holds, is the .crate archive's and does not change.

Usage: prune-cargo-vendor.py <vendor dir>
"""
import argparse
import fnmatch
import json
import re
import sys
from pathlib import Path

IMPORT_LIBRARY_CRATES = re.compile(
    r'^(winapi-(i686|x86_64)-pc-windows-gnu|windows_(aarch64|i686|x86_64)_(gnu|gnullvm|msvc))$')

# dpkg-source's default tar ignore patterns (Dpkg::Source::Package, dpkg
# 1.22 and 1.23), matched against every component of a path as tar's
# --exclude does.
DPKG_SOURCE_IGNORED = ('*.a', '*.la', '*.o', '*.so', '.*.sw?', '*~', ',,*', '.[#~]*',
                       '.arch-ids', '.arch-inventory', '.be', '.bzr', '.bzr.backup',
                       '.bzr.tags', '.bzrignore', '.cvsignore', '.deps', '.git',
                       '.gitattributes', '.gitignore', '.gitmodules', '.gitreview', '.hg',
                       '.hgignore', '.hgsigs', '.hgtags', '.mailmap', '.mtn-ignore', '.shelf',
                       '.svn', 'CVS', 'DEADJOE', 'RCS', '_MTN', '_darcs', '{arch}')
# dh_clean's list (debhelper 13): files it deletes anywhere in the tree.
DH_CLEAN_FILES = ('#*#', '.*~', '*~', 'DEADJOE', '*.orig', '*.rej', '*.bak',
                  '.*.orig', '.*.rej', '.SUMS', 'TAGS')
DH_CLEAN_DIRS = ('autom4te.cache', '__pycache__')


def crate_name(crate: Path) -> str:
    """The [package] name from the vendored manifest, which cargo vendor
    may have suffixed with the version in the directory name."""
    in_package = False
    for line in (crate / 'Cargo.toml').read_text(encoding='utf-8').splitlines():
        line = line.strip()
        if line.startswith('['):
            in_package = line == '[package]'
        elif in_package:
            match = re.match(r'name\s*=\s*"([^"]+)"', line)
            if match:
                return match.group(1)
    raise ValueError(f'{crate}: no package name in Cargo.toml')


def removable(name: str, path: str) -> bool:
    parts = path.split('/')
    if IMPORT_LIBRARY_CRATES.match(name) and parts[0] == 'lib':
        return True
    if any(fnmatch.fnmatchcase(part, pattern) for part in parts for pattern in DPKG_SOURCE_IGNORED):
        return True
    if any(part in DH_CLEAN_DIRS for part in parts[:-1]):
        return True
    return any(fnmatch.fnmatchcase(parts[-1], pattern) for pattern in DH_CLEAN_FILES)


def prune(vendor: Path) -> tuple[int, int]:
    files_removed = bytes_removed = 0
    for crate in sorted(p for p in vendor.iterdir() if p.is_dir()):
        checksum_path = crate / '.cargo-checksum.json'
        checksums = json.loads(checksum_path.read_text(encoding='utf-8'))
        name = crate_name(crate)
        listed = checksums['files']
        on_disk = {str(p.relative_to(crate)) for p in crate.rglob('*')
                   if p.is_file() and p.name != '.cargo-checksum.json'}
        for path in sorted(on_disk):
            if not removable(name, path):
                continue
            target = crate / path
            bytes_removed += target.stat().st_size
            files_removed += 1
            target.unlink()
            listed.pop(path, None)
        # Directories left empty go too, deepest first.
        for directory in sorted((p for p in crate.rglob('*') if p.is_dir()),
                                key=lambda p: len(p.parts), reverse=True):
            if not any(directory.iterdir()):
                directory.rmdir()
        missing = [path for path in listed if not (crate / path).is_file()]
        if missing:
            raise SystemExit(f'{crate.name}: listed in .cargo-checksum.json but missing: {missing[:5]}')
        checksum_path.write_text(json.dumps(checksums, separators=(',', ':'), sort_keys=True),
                                 encoding='utf-8')
    return files_removed, bytes_removed


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    parser.add_argument('vendor', type=Path)
    args = parser.parse_args()
    if not (args.vendor / '.').is_dir():
        sys.exit(f'{args.vendor}: not a directory')
    files, size = prune(args.vendor)
    print(f'pruned {files} files, {size / 1e6:.1f} MB, from {args.vendor}')


if __name__ == '__main__':
    main()
