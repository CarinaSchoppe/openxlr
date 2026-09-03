#!/usr/bin/env python3
"""Opt-in integration test on a real PipeWire/systemd user session.

Requires a Release build, python-websocket-client, LSP LV2, pacat/parec and
pw-dump. Stops the normal daemon temporarily, uses disposable settings, and
restores the original service in finally. Never run this during a recording.
Run: python3 tools/verify-live-mixer.py --allow-audio-interruption
"""

import argparse
import array
import concurrent.futures
import json
import math
import os
from pathlib import Path
import selectors
import subprocess
import tempfile
import threading
import time
import uuid

import websocket


def run(*args, **kwargs):
    return subprocess.run(args, check=True, text=True, capture_output=True, timeout=130, **kwargs).stdout.strip()


def connect():
    deadline = time.monotonic() + 100
    while time.monotonic() < deadline:
        try:
            sock = websocket.create_connection("ws://127.0.0.1:37890/ws", timeout=45)
            state = json.loads(sock.recv())
            assert state.get("mixer"), state
            return sock
        except (OSError, websocket.WebSocketException):
            time.sleep(0.25)
    raise AssertionError("daemon did not rebuild its mixer")


def command(sock, cmd, **fields):
    request = uuid.uuid4().hex
    sock.send(json.dumps(dict(cmd=cmd, requestId=request, **fields)))
    state = None
    while True:
        result = json.loads(sock.recv())
        if result["type"] == "state":
            state = result
        if result["type"] == "commandResult" and result["requestId"] == request:
            assert not result.get("error"), result
            assert state is not None
            return state["mixer"]


def nodes():
    return [n.get("info", {}).get("props", {}).get("node.name")
            for n in json.loads(run("pw-dump")) if n["type"] == "PipeWire:Interface:Node"]


def capture_peak():
    """Measure a synthetic 440 Hz signal through the added virtual output.

    Only the isolated output mix receives it; its monitor send is muted first.
    Concurrent read/write avoids audio-pipe backpressure deadlocks.
    """
    samples = array.array("f", (0.2 * math.sin(i * 2 * math.pi * 440 / 48000)
                               for i in range(48000) for _ in range(2))).tobytes()
    capture = subprocess.Popen(["parec", "-d", "OpenXLR_qa-output", "--format=float32le",
                                "--channels=2", "--rate=48000", "--latency-msec=30"], stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE)
    playback = subprocess.Popen(["pacat", "--playback", "-d", "OpenXLR_ch_qa-channel",
                                 "--format=float32le", "--channels=2", "--rate=48000", "--latency-msec=30",
                                 "--property=application.name=OpenXLR Live QA",
                                 "--property=application.id=openxlr-live-qa"],
                                stdin=subprocess.PIPE, stderr=subprocess.PIPE)
    def feed():
        try:
            for _ in range(4):
                playback.stdin.write(samples)
                playback.stdin.flush()
        except (BrokenPipeError, ValueError):
            pass
    writer = threading.Thread(target=feed, daemon=True)
    writer.start()
    received = bytearray()
    try:
        with selectors.DefaultSelector() as selector:
            selector.register(capture.stdout, selectors.EVENT_READ)
            deadline = time.monotonic() + 3
            while time.monotonic() < deadline:
                if selector.select(0.1):
                    received.extend(os.read(capture.stdout.fileno(), 65536))
        data = array.array("f")
        data.frombytes(received[:len(received) // 4 * 4])
        if not data:
            capture.terminate()
            playback.terminate()
            capture.wait(timeout=3)
            playback.wait(timeout=3)
            raise AssertionError(f"no samples: capture={capture.stderr.read()!r}, playback={playback.stderr.read()!r}; "
                                 + run("pactl", "list", "sources", "short"))
        return max(abs(x) for x in data)
    finally:
        for process in (playback, capture):
            process.terminate()
        for process in (playback, capture):
            try:
                process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=3)
        writer.join(timeout=3)
        playback.stdin.close()
        capture.stdout.close()
        playback.stderr.close()
        capture.stderr.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--allow-audio-interruption", action="store_true", required=True)
    parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    daemon = repo / "src/OpenXLR.Daemon/bin/Release/net10.0/OpenXLR.Daemon"
    assert daemon.is_file(), "build Release first"
    normal = "openxlr-daemon.service"
    was_active = subprocess.run(["systemctl", "--user", "is-active", "--quiet", normal]).returncode == 0
    unit = f"openxlr-validation-{os.getpid()}.service"
    sock = None
    with tempfile.TemporaryDirectory(prefix="openxlr-validation-") as config:
        try:
            if was_active:
                run("systemctl", "--user", "stop", normal)
            run("systemd-run", "--user", f"--unit={unit}", "--collect",
                "--property=Type=notify", "--property=NotifyAccess=main", "--property=WatchdogSec=60",
                "--property=WatchdogSignal=SIGKILL", "--property=TimeoutStartSec=120",
                "--property=TimeoutStopSec=45", "--property=Restart=always", "--property=RestartSec=3",
                "--property=StartLimitIntervalSec=0", f"--setenv=XDG_CONFIG_HOME={config}",
                "--setenv=OPENXLR_BUILD_MIXER=1", str(daemon))
            sock = connect()
            command(sock, "createChannel", name="QA Channel")
            command(sock, "createMix", name="QA Output")
            command(sock, "renameChannel", channel="qa-channel", name="QA Renamed")
            command(sock, "renameMix", mix="qa-output", name="QA Recording")
            command(sock, "assignApp", identity="openxlr-live-qa", channel="qa-channel", label="QA")
            command(sock, "setLevel", channel="qa-channel", mix="monitor", value=0)
            command(sock, "setLevel", channel="qa-channel", mix="qa-output", value=1)
            # Keep other channels (including the hardware microphone) out of this capture.
            mixer = command(sock, "setMixVolume", mix="qa-output", value=1)
            for channel in mixer["channels"]:
                if channel["id"] != "qa-channel":
                    command(sock, "setLevel", channel=channel["id"], mix="qa-output", value=0)
            assert "OpenXLR_ch_qa-channel" in nodes()
            assert "OpenXLR_qa-output" in nodes()
            print("PASS create/rename/app assignment and actual PipeWire node creation", flush=True)

            # Two clients editing concurrently must not overwrite each other's layout.
            def add(name):
                peer = connect()
                try:
                    return command(peer, "createChannel", name=name)
                finally:
                    peer.close()
            with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
                list(pool.map(add, ["Parallel A", "Parallel B"]))
            mixer = command(sock, "setMixVolume", mix="qa-output", value=1)
            assert {"parallel-a", "parallel-b"} <= {c["id"] for c in mixer["channels"]}
            print("PASS concurrent layout transactions", flush=True)

            dry_peak = capture_peak()
            assert 0.15 < dry_peak < 0.25, dry_peak
            plugin = "http://lsp-plug.in/plugins/lv2/compressor_stereo"
            mixer = command(sock, "setInserts", channel="mix:qa-output", inserts=[dict(
                id="qa-compressor", kind="lv2", plugin=plugin, label="QA Compressor", bypass=False,
                params=dict(g_in=1, g_out=0.25, al=1, cr=1))])
            assert not mixer["inserts"]["mix:qa-output"][0].get("error"), mixer["inserts"]
            wet_peak = capture_peak()
            assert 0.20 < wet_peak / dry_peak < 0.30, (dry_peak, wet_peak)
            command(sock, "setInsertBypass", channel="mix:qa-output", insertId="qa-compressor", value=True)
            bypass_peak = capture_peak()
            assert 0.9 < bypass_peak / dry_peak < 1.1, (dry_peak, bypass_peak)
            print(f"PASS real LSP processing + bypass: dry={dry_peak:.4f}, wet={wet_peak:.4f}, bypass={bypass_peak:.4f}", flush=True)

            # The rename acknowledgement forces a durable save of current settings.
            command(sock, "renameMix", mix="qa-output", name="QA Persisted")
            saved = json.loads((Path(config) / "openxlr/mixer.json").read_text())
            assert any(m["id"] == "qa-output" for m in saved["userMixes"])
            for signal in ("SIGKILL", "SIGSTOP"):
                previous = run("systemctl", "--user", "show", unit, "--property=MainPID", "--value")
                run("systemctl", "--user", "kill", "--kill-whom=main", f"--signal={signal}", unit)
                sock.close(timeout=0)
                sock = None
                deadline = time.monotonic() + 90
                while time.monotonic() < deadline:
                    current = run("systemctl", "--user", "show", unit, "--property=MainPID", "--value")
                    if current not in ("0", previous):
                        break
                    time.sleep(0.5)
                else:
                    raise AssertionError(f"service did not recover from {signal}")
                sock = connect()
                mixer = command(sock, "setMixVolume", mix="qa-output", value=1)
                assert next(m for m in mixer["mixes"] if m["id"] == "qa-output")["name"] == "QA Persisted"
                assert nodes().count("OpenXLR_qa-output") == 1
                print(f"PASS automatic {signal} recovery + persisted layout + unique nodes", flush=True)
            command(sock, "deleteChannel", channel="qa-channel")
            mixer = command(sock, "deleteMix", mix="qa-output")
            assert "mix:qa-output" not in mixer.get("inserts", {})
            assert not any(n and ("qa-channel" in n or "qa-output" in n) for n in nodes())
            saved = json.loads((Path(config) / "openxlr/mixer.json").read_text())
            assert saved["appOverrides"]["openxlr-live-qa"] == "game"
            assert not any("qa-channel" in key or "qa-output" in key for key in saved["levels"])
            print("PASS deletion removes real nodes, inserts, sends and remaps applications", flush=True)

            start = time.monotonic()
            run("systemctl", "--user", "stop", unit)  # socket deliberately still open
            elapsed = time.monotonic() - start
            assert elapsed < 15, elapsed
            print(f"PASS graceful stop with connected client in {elapsed:.2f}s", flush=True)
        finally:
            if sock is not None:
                sock.close(timeout=0)
            subprocess.run(["systemctl", "--user", "stop", unit], capture_output=True, timeout=60)
            if was_active:
                run("systemctl", "--user", "start", normal)
                print("Original daemon service restored.", flush=True)


if __name__ == "__main__":
    main()
