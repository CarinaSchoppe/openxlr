#!/usr/bin/env python3
"""Generate Flatpak downloads for DeepFilterNet's Rust crates from native/deepfilter/Cargo.lock.

Each crate is unpacked under native/deepfilter/vendor/<name>-<version> with
the .cargo-checksum.json a vendored crate needs; native/Makefile builds from
that directory offline when it exists. The lock file holds every crate's
SHA-256, so generating and checking need no network.
"""
import argparse
import json
import re
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LOCK = ROOT / 'native' / 'deepfilter' / 'Cargo.lock'
OUTPUT = Path(__file__).with_name('cargo-sources.json')
CRATES_IO = 'registry+https://github.com/rust-lang/crates.io-index'
VENDOR = 'native/deepfilter/vendor'


def sources(lock=LOCK):
    packages = tomllib.loads(Path(lock).read_text(encoding='utf-8'))['package']
    result = []
    for package in sorted(packages, key=lambda p: (p['name'], p['version'])):
        source = package.get('source')
        if source is None:
            continue  # the wrapper and libDF, which are in the tree
        if source != CRATES_IO:
            raise ValueError(f"{package['name']} {package['version']}: only crates.io is supported, not {source}")
        name, version, checksum = package['name'], package['version'], package['checksum']
        if not re.fullmatch(r'[a-f0-9]{64}', checksum):
            raise ValueError(f'Invalid SHA-256 for {name} {version}')
        dest = f'{VENDOR}/{name}-{version}'
        result.append({'type': 'archive', 'archive-type': 'tar-gzip',
                       'url': f'https://static.crates.io/crates/{name}/{name}-{version}.crate',
                       'sha256': checksum, 'dest': dest})
        result.append({'type': 'inline', 'contents': json.dumps({'package': checksum, 'files': {}}),
                       'dest': dest, 'dest-filename': '.cargo-checksum.json'})
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    text = json.dumps(sources(), indent=2) + '\n'
    if args.check:
        if not OUTPUT.exists() or OUTPUT.read_text(encoding='utf-8') != text:
            raise SystemExit('Flatpak cargo sources are stale; run packaging/flatpak/cargo-sources.py')
    else:
        OUTPUT.write_text(text, encoding='utf-8')
