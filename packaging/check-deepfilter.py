#!/usr/bin/env python3
"""Every package builds DeepFilterNet in, with the version and model native/Makefile pins.

A source build without cargo still falls back to RNNoise alone, so a
packaging path that loses DEEPFILTER=1 would ship without DeepFilterNet and
nothing else would notice before a user did. Standard library only.
"""
import base64
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding='utf-8')


def makefile_value(name):
    value = re.search(rf'^{name}\s*=\s*(.+)$', read('native/Makefile'), re.M).group(1).strip()
    return re.sub(r'\$\((\w+)\)', lambda m: makefile_value(m.group(1)), value)


version = makefile_value('DF_VERSION')
url = makefile_value('DF_MODEL_URL')
sha256 = makefile_value('DF_MODEL_SHA256')
sri = 'sha256-' + base64.b64encode(bytes.fromhex(sha256)).decode()

REQUIRED = [
    # Ubuntu 24.04's cargo 1.75 reads lock files up to version 3; a newer
    # cargo keeps the version it finds. The crates build with rustc 1.67.
    ('native/deepfilter/Cargo.lock', '\nversion = 3\n'),
    ('debian/rules', '\nexport DEEPFILTER = 1\n'),
    ('debian/control', ' cargo, rustc'),
    ('packaging/rpm/openxlr.spec', '\nexport DEEPFILTER=1\n'),
    ('packaging/rpm/openxlr.spec', '\nBuildRequires:  cargo\n'),
    ('packaging/rpm/openxlr.spec', '\nBuildRequires:  rust\n'),
    ('packaging/rpm/openxlr.spec', f'\n%global deepfilter_version {version}\n'),
    ('packaging/rpm/openxlr.spec',
     '\nSource1:        ' + url.replace(version, '%{deepfilter_version}')
     + '#/DeepFilterNet3_onnx-%{deepfilter_version}.tar.gz\n'),
    ('packaging/ppa/make-source.sh', 'cargo vendor --locked vendor'),
    ('packaging/ppa/make-source.sh', 'packaging/ppa/prune-cargo-vendor.py'),
    ('packaging/nix/package.nix', 'DEEPFILTER = "1";'),
    ('packaging/nix/package.nix', f'deepfilterVersion = "{version}";'),
    ('packaging/nix/package.nix',
     'url = "' + url.replace(version, '${deepfilterVersion}') + '";'),
    ('packaging/nix/package.nix', f'hash = "{sri}";'),
    ('packaging/flatpak/build.sh', '\nexport DEEPFILTER=1\n'),
    ('packaging/flatpak/io.github.emaspa.openxlr.json', f'"url": "{url}"'),
    ('packaging/flatpak/io.github.emaspa.openxlr.json', f'"sha256": "{sha256}"'),
    ('.github/workflows/ci.yml', 'make -C native test-denoise'),
]

errors = [f'{path}: missing {text.strip()!r}' for path, text in REQUIRED if text not in read(path)]
for error in errors:
    print(f'deepfilter: {error}', file=sys.stderr)
print(f'deepfilter: {len(REQUIRED)} checks, ' + ('ok' if not errors else f'{len(errors)} problems'))
sys.exit(1 if errors else 0)
