#!/usr/bin/env python3
"""Run real desktop focus acceptance on private buses and software-rendered outputs in disposable containers."""
import argparse
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source', choices=['all', 'gnome', 'sway', 'niri', 'x11'], default='all')
parser.add_argument('--no-build', action='store_true', help='Reuse already built acceptance images')
args = parser.parse_args()
sources = ['gnome', 'sway', 'x11', 'niri'] if args.source == 'all' else [args.source]
built = set()
for source in sources:
    kind = 'niri' if source == 'niri' else 'noble'
    image = f'openxlr-focus-{kind}:acceptance'
    if kind not in built and not args.no_build:
        subprocess.run(['docker', 'build', '-f', str(root / 'tools/focus-acceptance' / f'Dockerfile.{kind}'),
                        '-t', image, str(root / 'tools/focus-acceptance')], check=True)
    built.add(kind)
    # GNOME uses its dummy login manager, not a host logind connection.
    bootstrap = ('rmdir /run/systemd/seats 2>/dev/null || true; mkdir -p /run/dbus; '
                 'dbus-uuidgen --ensure; dbus-daemon --system --fork; '
                 'exec runuser -u tester -- bash /repo/tools/focus-acceptance/session.sh "$1"')
    subprocess.run(['docker', 'run', '--rm', '--network=none', '--mount', f'type=bind,src={root},dst=/repo,readonly',
                    image, 'bash', '-c', bootstrap, 'focus-acceptance', source], check=True)
