#!/usr/bin/env python3
"""Exercise the optional bridge in an installed Flatpak without changing persistent overrides."""
import os
from pathlib import Path
import subprocess

APP = 'io.github.emaspa.openxlr'
location = Path(subprocess.check_output(['flatpak', 'info', '--show-location', APP], text=True).strip())
runtime = location / 'files/lib/dotnet/dotnet'
assembly = location / 'files/lib/openxlr/ui/OpenXLR.UI.dll'


def run(options, candidates):
    return subprocess.run(['flatpak', 'run', '--command=/app/lib/dotnet/dotnet', *options,
                           APP, '/app/lib/openxlr/ui/OpenXLR.UI.dll', 'focus-host', candidates],
                          capture_output=True, text=True, timeout=15)


candidates = ','.join(sorted((name for name in os.listdir('/proc') if name.isdigit()), key=int))
if candidates.count(',') >= 1024:
    raise SystemExit('Acceptance needs at most 1024 host processes; use a quieter test session.')
refused = run(['--env=OPENXLR_FOCUS_HOST=0', '--no-talk-name=org.freedesktop.Flatpak'], candidates)
assert refused.returncode == 2 and 'optional host bridge' in refused.stderr, refused
refused = run(['--env=OPENXLR_FOCUS_HOST=1', '--no-talk-name=org.freedesktop.Flatpak'], candidates)
assert refused.returncode == 2 and 'host bridge failed' in refused.stderr, refused
for attempt in range(3):
    before = subprocess.run([str(runtime), str(assembly), 'focus-host', candidates], capture_output=True, text=True, timeout=10)
    sandbox = run(['--env=OPENXLR_FOCUS_HOST=1', '--talk-name=org.freedesktop.Flatpak'], candidates)
    after = subprocess.run([str(runtime), str(assembly), 'focus-host', candidates], capture_output=True, text=True, timeout=10)
    assert before.returncode == after.returncode == sandbox.returncode == 0, (before, sandbox, after)
    if before.stdout.strip() == sandbox.stdout.strip() == after.stdout.strip():
        print('Flatpak: opt-in, denied permission and host process identity passed')
        break
else:
    raise SystemExit('The focused process changed during every comparison; repeat with a stable focused window.')
