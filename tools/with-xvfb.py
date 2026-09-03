#!/usr/bin/env python3
"""Run one test with an isolated Xvfb display (also on Arch without xvfb-run)."""
import os
import selectors
import subprocess
import sys

if len(sys.argv) < 2:
    raise SystemExit("usage: with-xvfb.py COMMAND [ARGUMENT ...]")
server = subprocess.Popen(["Xvfb", "-displayfd", "1", "-screen", "0", "1280x1024x24", "-nolisten", "tcp"],
                          stdout=subprocess.PIPE, text=True)
try:
    with selectors.DefaultSelector() as ready:
        ready.register(server.stdout, selectors.EVENT_READ)
        assert ready.select(10), "Xvfb did not start"
    display = server.stdout.readline().strip()
    assert display.isdecimal(), f"invalid Xvfb display: {display}"
    result = subprocess.run(sys.argv[1:], env=dict(os.environ, DISPLAY=":" + display), timeout=300)
    raise SystemExit(result.returncode)
finally:
    server.terminate()
    try:
        server.wait(timeout=5)
    except subprocess.TimeoutExpired:
        server.kill()
        server.wait(timeout=5)
