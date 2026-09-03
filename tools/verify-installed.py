#!/usr/bin/env python3
"""Run an installed package's daemon, UI and DSP on private audio servers.

Requires Xvfb/display and python-websocket-client. Refuses an occupied API port;
never stops a user's daemon. CI supplies a disposable user and no USB hardware.
"""
import argparse
import json
from pipewire_snapshot import parse_dump
import os
from pathlib import Path
import socket
import subprocess
import time
import uuid

import websocket
from private_audio import PrivateAudio


def command(connection, cmd, **fields):
    request = uuid.uuid4().hex
    connection.send(json.dumps(dict(cmd=cmd, requestId=request, **fields)))
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        message = json.loads(connection.recv())
        if message.get("type") == "commandResult" and message.get("requestId") == request:
            assert not message.get("error"), message
            return
    raise AssertionError(f"No response to {cmd}")


def wait_for_ui_window(audio, desktop, seconds=30):
    """Wait until the installed UI maps its real main window on X11."""
    deadline = time.monotonic() + seconds
    windows = "xwininfo did not return a window tree"
    while time.monotonic() < deadline:
        assert desktop.poll() is None, f"installed UI exited {desktop.returncode}"
        try:
            windows = audio.run("xwininfo", "-root", "-tree")
        except subprocess.CalledProcessError as error:
            windows = error.stdout or error.stderr or str(error)
        if '"OpenXLR"' in windows:
            return
        time.sleep(0.1)
    raise AssertionError(f"installed UI did not map its main window within {seconds}s\n{windows}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prefix", type=Path, default=Path("/usr"))
    options = parser.parse_args()
    install = options.prefix / "lib/openxlr"
    daemon = install / "daemon/OpenXLR.Daemon"
    ui = install / "ui/OpenXLR.UI"
    helper = install / "daemon/openxlr-lv2-host"
    for executable in (daemon, ui, helper):
        assert executable.is_file() and os.access(executable, os.X_OK), executable
    linked = subprocess.run(["ldd", str(helper)], capture_output=True, text=True, check=True).stdout
    assert "not found" not in linked, linked
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 37890))  # refuse to share an API with a live install
    with PrivateAudio() as audio:
        config = audio.path / "openxlr"
        config.mkdir()
        (config / "ui.json").write_text(json.dumps(dict(checkForUpdates=False, minimizeToTray=False)))
        (config / "mixer.json").write_text(json.dumps(dict(userChannels=[dict(id="system", name="System")], userMixes=[])))
        audio.env["OPENXLR_BUILD_MIXER"] = "1"
        server = audio.logged(str(daemon))
        connection = None
        deadline = time.monotonic() + 40
        while time.monotonic() < deadline:
            assert server.poll() is None, f"installed daemon exited {server.returncode}"
            try:
                connection = websocket.create_connection("ws://127.0.0.1:37890/ws", timeout=30)
                state = json.loads(connection.recv())
                assert state.get("mixer"), state
                break
            except (ConnectionError, websocket.WebSocketException):
                time.sleep(0.1)
        assert connection is not None, "installed daemon API did not start"
        try:
            desktop = audio.logged(str(ui))
            wait_for_ui_window(audio, desktop)
            command(connection, "createChannel", name="Package QA")
            command(connection, "createMix", name="Package Output")
            command(connection, "renameChannel", channel="package-qa", name='QA "renamed"')
            command(connection, "renameMix", mix="package-output", name="QA Output")
            command(connection, "setLevel", channel="package-qa", mix="package-output", value=0.4)
            graph = parse_dump(audio.run("pw-dump"))
            names = [n.get("info", {}).get("props", {}).get("node.name") for n in graph]
            assert names.count("OpenXLR_ch_package-qa") == names.count("OpenXLR_package-output") == 1
            command(connection, "deleteChannel", channel="package-qa")
            command(connection, "deleteMix", mix="package-output")
            names = [n.get("info", {}).get("props", {}).get("node.name", "") for n in parse_dump(audio.run("pw-dump"))]
            assert not any("package-qa" in n or "package-output" in n for n in names)
            saved = json.loads((config / "mixer.json").read_text())
            assert not saved["userMixes"] and len(saved["userChannels"]) == 1
            assert desktop.poll() is None, f"installed UI exited {desktop.returncode}"
            server.terminate()  # deliberately keep the UI/WebSocket connected
            assert server.wait(timeout=15) == 0
            assert desktop.poll() is None, "UI failed when daemon disconnected"
            print("PASS installed daemon/API, actual UI window, editable layout, saved deletion and clean shutdown", flush=True)
        finally:
            connection.close(timeout=0)


if __name__ == "__main__":
    main()
