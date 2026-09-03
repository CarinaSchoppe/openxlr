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
import signal
import subprocess
import tempfile
import threading
import time
import uuid

import websocket
from native_ui_smoke import wheel_compressor_output


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
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        sock.settimeout(max(0.1, deadline - time.monotonic()))
        result = json.loads(sock.recv())
        if result["type"] == "state":
            state = result
        if result["type"] == "commandResult" and result["requestId"] == request:
            assert not result.get("error"), result
            assert state is not None
            return state["mixer"]
    raise AssertionError(f"no acknowledgement for {cmd}")


def nodes():
    return [n.get("info", {}).get("props", {}).get("node.name")
            for n in json.loads(run("pw-dump")) if n["type"] == "PipeWire:Interface:Node"]


def assert_application_sink(identity, channel):
    """Inspect Pulse's actual stream target, not just the daemon's settings."""
    sinks = json.loads(run("pactl", "-f", "json", "list", "sinks"))
    expected = next(s["index"] for s in sinks if s["name"] == f"OpenXLR_ch_{channel}")
    streams = [s for s in json.loads(run("pactl", "-f", "json", "list", "sink-inputs"))
               if s.get("properties", {}).get("application.id") == identity]
    targets = [(s["index"], s["sink"]) for s in streams]
    assert len(streams) == 1 and streams[0]["sink"] == expected, (identity, channel, expected, targets)


def verify_application_routing(sock):
    """Exercise Flow's assignApp command on a live, silent application stream."""
    with open(os.devnull, "wb") as discard, open("/dev/zero", "rb") as silence:
        playback = subprocess.Popen(["pacat", "--playback", "-d", "OpenXLR_ch_parallel-b",
            "--format=float32le", "--channels=2", "--rate=48000", "--latency-msec=30",
            "--property=application.name=QA Player", "--property=node.name=qa-playback",
            "--property=application.id=openxlr-live-qa", "--property=application.process.binary=openxlr-live-qa"],
            stdin=silence, stdout=discard, stderr=subprocess.PIPE)
        try:
            # A saved assignment must move a newly started program, even when
            # it initially connects to another sink. Then switch it while live.
            # A fresh large graph can still be reconciling its feeds. Allow
            # a bounded settling period before testing immediate live edits.
            deadline = time.monotonic() + 30
            while True:
                try:
                    assert_application_sink("openxlr-live-qa", "qa-channel")
                    break
                except AssertionError:
                    if time.monotonic() >= deadline:
                        state = command(sock, "setMixVolume", mix="qa-output", value=1)
                        print("Routing diagnostic:", [a for a in state.get("streams", [])
                              if a.get("identity") == "openxlr-live-qa"], flush=True)
                        for node in json.loads(run("pw-dump")):
                            props = node.get("info", {}).get("props", {})
                            if props.get("node.name") == "qa-playback":
                                print("Stream properties:", {key: props.get(key) for key in
                                      ("node.name", "node.link-group", "media.class", "application.process.binary")}, flush=True)
                        raise
                    time.sleep(0.1)
            command(sock, "assignApp", identity="openxlr-live-qa", channel="parallel-a")
            assert_application_sink("openxlr-live-qa", "parallel-a")
            command(sock, "assignApp", identity="openxlr-live-qa", channel="qa-channel")
            assert_application_sink("openxlr-live-qa", "qa-channel")
            print("PASS saved application routing and live Flow reassignment change the actual stream sink", flush=True)
        finally:
            playback.terminate()
            try:
                playback.wait(timeout=3)
            except subprocess.TimeoutExpired:
                playback.kill()
                playback.wait(timeout=3)
            playback.stderr.close()


def capture_peak(channel="qa-channel", identity="openxlr-live-qa", frequency=440, screenshot=None):
    """Measure a synthetic tone through the added virtual output.

    Only the isolated output mix receives it; its monitor send is muted first.
    Concurrent read/write avoids audio-pipe backpressure deadlocks.
    """
    samples = array.array("f", (0.2 * math.sin(i * 2 * math.pi * frequency / 48000)
                               for i in range(48000) for _ in range(2))).tobytes()
    capture = subprocess.Popen(["parec", "-d", "OpenXLR_qa-output", "--format=float32le",
                                "--channels=2", "--rate=48000", "--latency-msec=30"], stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE)
    # Start in a muted non-target channel. The daemon must honor the saved
    # program assignment before the settled audio can reach our capture.
    playback = subprocess.Popen(["pacat", "--playback", "-d", "OpenXLR_ch_parallel-b",
                                 "--format=float32le", "--channels=2", "--rate=48000", "--latency-msec=30",
                                 "--property=application.name=QA Player", "--property=node.name=qa-playback",
                                 f"--property=application.process.binary={identity}",
                                 f"--property=application.id={identity}"],
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
                if screenshot and time.monotonic() > deadline - 1:
                    run("import", "-window", "OpenXLR - Native LV2 controls", str(screenshot))
                    screenshot = None
        data = array.array("f")
        data.frombytes(received[:len(received) // 4 * 4])
        if not data:
            # Capture the broken boundary before stopping its streams; after
            # teardown an idle graph hides whether policy linked the recorder.
            print("No-audio QA links:\n" + "\n".join(line for line in run("pw-link", "-l").splitlines()
                                                     if "qa-" in line), flush=True)
            print("No-audio capture streams:", [(s["index"], s["source"], s.get("properties", {}).get("target.object"))
                for s in json.loads(run("pactl", "-f", "json", "list", "source-outputs"))], flush=True)
            capture.terminate()
            playback.terminate()
            capture.wait(timeout=3)
            playback.wait(timeout=3)
            raise AssertionError(f"no samples: capture={capture.stderr.read()!r}, playback={playback.stderr.read()!r}; "
                                 + run("pactl", "list", "sources", "short"))
        assert len(data) >= 48000, "insufficient settled audio"
        assert_application_sink(identity, channel)
        return max(abs(x) for x in data[-48000:])
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
    parser.add_argument("--native-ui", action="store_true", help="Also open the actual LSP editors (requires an X11/XWayland display)")
    parser.add_argument("--screenshots", type=Path, help="Capture the live native editor windows here (requires ImageMagick)")
    options = parser.parse_args()
    if options.screenshots:
        options.screenshots.mkdir(parents=True, exist_ok=True)
    repo = Path(__file__).resolve().parents[1]
    daemon = repo / "src/OpenXLR.Daemon/bin/Release/net10.0/OpenXLR.Daemon"
    assert daemon.is_file(), "build Release first"
    normal = "openxlr-daemon.service"
    was_active = subprocess.run(["systemctl", "--user", "is-active", "--quiet", normal]).returncode == 0
    unit = f"openxlr-validation-{os.getpid()}.service"
    sock = None
    defaults = (run("pactl", "get-default-sink"), run("pactl", "get-default-source"))
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
            channel_label = 'QA "Renamed" / music\'s \\ path'
            command(sock, "renameChannel", channel="qa-channel", name=channel_label)
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
            graph = [n.get("info", {}).get("props", {}) for n in json.loads(run("pw-dump"))
                     if n["type"] == "PipeWire:Interface:Node"]
            fanout = next(p for p in graph if p.get("node.name") == "OpenXLR_fanout_qa-channel")
            assert fanout["openxlr.internal"] is True, fanout
            assert fanout["media.class"] == "Audio/Filter", fanout
            public_input = next(p for p in graph if p.get("node.name") == "OpenXLR_ch_qa-channel")
            assert public_input["node.description"] == "OpenXLR " + channel_label, public_input
            assert fanout["node.description"] != public_input["node.description"], fanout
            sinks = json.loads(run("pactl", "-f", "json", "list", "sinks"))
            assert not any(s["name"] == "OpenXLR_fanout_qa-channel" for s in sinks), sinks
            print("PASS public channel label and distinct internal distribution label survive punctuation", flush=True)
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
            command(sock, "setLevel", channel="parallel-b", mix="monitor", value=0)
            command(sock, "setLevel", channel="parallel-a", mix="monitor", value=0)
            verify_application_routing(sock)

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

            # A program channel's effects precede fan-out and must not affect another channel.
            command(sock, "setInserts", channel="qa-channel", inserts=[dict(
                id="qa-channel-compressor", kind="lv2", plugin=plugin, label="QA Channel Compressor", bypass=False,
                params=dict(g_in=1, g_out=0.5, al=1, cr=1))])
            processed = capture_peak()
            command(sock, "assignApp", identity="openxlr-other-qa", channel="parallel-a")
            command(sock, "setLevel", channel="parallel-a", mix="qa-output", value=1)
            other = capture_peak("parallel-a", "openxlr-other-qa")
            assert 0.095 < processed < 0.105 and 0.19 < other < 0.21, (processed, other)
            print(f"PASS independent channel DSP: processed={processed:.4f}, other={other:.4f}", flush=True)
            if options.native_ui:
                command(sock, "showInsertUi", channel="qa-channel", insertId="qa-channel-compressor")
                print("PASS actual native compressor UI opens on the processing instance", flush=True)
                capture_peak(screenshot=options.screenshots / "native-compressor.png" if options.screenshots else None)
                wheel_compressor_output()
                time.sleep(1.5)  # native edits are coalesced by the daemon's one-second sweep
                state = command(sock, "setMixVolume", mix="qa-output", value=1)
                changed_gain = state["inserts"]["qa-channel"][0]["insert"]["params"]["g_out"]
                assert changed_gain > 0.5, changed_gain
                assert abs(capture_peak() - 0.2 * changed_gain) < 0.006
                saved = json.loads((Path(config) / "openxlr/mixer.json").read_text())
                assert saved["inserts"]["qa-channel"][0]["params"]["g_out"] == changed_gain
                command(sock, "setInsertParam", channel="qa-channel", insertId="qa-channel-compressor", symbol="g_out", value=0.5)
                print("PASS native UI gesture → persisted daemon control → measured audio", flush=True)

            # The native host, not only the daemon, is supervised and reconstructed.
            def native_pid():
                for node in json.loads(run("pw-dump")):
                    props = node.get("info", {}).get("props", {})
                    if props.get("node.name") == "OpenXLR_ins_ch_qa-channel_in_lv2_0":
                        return int(props["application.process.id"])
                return None

            for fault in (signal.SIGKILL, signal.SIGSTOP):
                previous_pid = native_pid()
                assert previous_pid
                os.kill(previous_pid, fault)
                deadline = time.monotonic() + 30
                while time.monotonic() < deadline:
                    if native_pid() not in (None, previous_pid):
                        break
                    time.sleep(0.5)
                else:
                    raise AssertionError(f"native host did not recover from {fault.name}")
                recovered = capture_peak()
                assert 0.095 < recovered < 0.105, recovered
                print(f"PASS native host {fault.name} recovery preserves channel processing", flush=True)

            eq = dict(id="qa-eq", kind="lv2", plugin="http://lsp-plug.in/plugins/lv2/para_equalizer_x8_stereo",
                      label="QA EQ", bypass=False, params=dict(ft_0=1, f_0=440, g_0=0.25, q_0=2))
            command(sock, "setInserts", channel="qa-channel", inserts=[eq])
            at_band, away = capture_peak(), capture_peak(frequency=6000)
            assert 0.04 < at_band < 0.065 and away > at_band * 2, (at_band, away)
            print(f"PASS real EQ frequency response: 440Hz={at_band:.4f}, 6kHz={away:.4f}", flush=True)
            if options.native_ui:
                command(sock, "showInsertUi", channel="qa-channel", insertId="qa-eq")
                capture_peak(screenshot=options.screenshots / "native-eq.png" if options.screenshots else None)
                print("PASS actual native EQ UI opens on the processing instance", flush=True)

            # The rename acknowledgement forces a durable save of current settings.
            command(sock, "renameMix", mix="qa-output", name="QA Persisted")
            saved = json.loads((Path(config) / "openxlr/mixer.json").read_text())
            assert any(m["id"] == "qa-output" for m in saved["userMixes"])
            for signal_name in ("SIGKILL", "SIGSTOP"):
                previous = run("systemctl", "--user", "show", unit, "--property=MainPID", "--value")
                run("systemctl", "--user", "kill", "--kill-whom=main", f"--signal={signal_name}", unit)
                sock.close(timeout=0)
                sock = None
                deadline = time.monotonic() + 90
                while time.monotonic() < deadline:
                    current = run("systemctl", "--user", "show", unit, "--property=MainPID", "--value")
                    if current not in ("0", previous):
                        break
                    time.sleep(0.5)
                else:
                    raise AssertionError(f"service did not recover from {signal_name}")
                sock = connect()
                mixer = command(sock, "setMixVolume", mix="qa-output", value=1)
                assert next(m for m in mixer["mixes"] if m["id"] == "qa-output")["name"] == "QA Persisted"
                assert nodes().count("OpenXLR_qa-output") == 1
                print(f"PASS automatic {signal_name} recovery + persisted layout + unique nodes", flush=True)
            command(sock, "deleteChannel", channel="qa-channel")
            mixer = command(sock, "deleteMix", mix="qa-output")
            assert "mix:qa-output" not in mixer.get("inserts", {})
            assert "qa-channel" not in mixer.get("inserts", {})
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
            for kind, device in zip(("sink", "source"), defaults):
                run("pactl", f"set-default-{kind}", device)


if __name__ == "__main__":
    main()
