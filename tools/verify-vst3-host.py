#!/usr/bin/env python3
"""Measure the isolated VST3 host on a private PipeWire graph."""
import argparse
import array
import math
import os
from pathlib import Path
import queue
import selectors
import subprocess
import tempfile
import threading
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("plugin", type=Path, help="VST3 bundle containing Steinberg AGain Sample Accurate")
    parser.add_argument("--host", type=Path)
    options = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    processes = []

    with tempfile.TemporaryDirectory(prefix="openxlr-vst3-test-") as runtime:
        env = dict(os.environ, XDG_RUNTIME_DIR=runtime, PIPEWIRE_RUNTIME_DIR=runtime,
                   XDG_CONFIG_HOME=runtime, PULSE_SERVER=f"unix:{runtime}/pulse/native")

        def start(*args, **kwargs):
            process = subprocess.Popen(args, env=env, **kwargs)
            processes.append(process)
            return process

        def run(*args):
            return subprocess.run(args, env=env, check=True, capture_output=True,
                                  text=True, timeout=15).stdout

        def wait_for(check, description):
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline:
                try:
                    if check():
                        return
                except subprocess.CalledProcessError:
                    pass
                time.sleep(0.05)
            raise AssertionError(f"{description} did not become ready")

        def pulse_endpoint_exists(listing, name):
            return any(len(fields) > 1 and fields[1] == name
                       for line in listing.splitlines() if (fields := line.split()))

        def pulse_process_exists(listing, process_id):
            return f'application.process.id = "{process_id}"' in listing

        def wait_for_stream(process, kind, description):
            def ready():
                if process.poll() is not None:
                    error = process.stderr.read().decode(errors="replace").strip()
                    raise AssertionError(f"{description} exited early: {error}")
                return pulse_process_exists(run("pactl", "list", kind), process.pid)
            wait_for(ready, description)

        try:
            bus = start("dbus-daemon", "--session", "--nofork", "--print-address=1",
                        stdout=subprocess.PIPE, text=True)
            env["DBUS_SESSION_BUS_ADDRESS"] = bus.stdout.readline().strip()
            policy = Path(runtime, "wireplumber/wireplumber.conf.d")
            policy.mkdir(parents=True)
            (policy / "99-no-hardware.conf").write_text(
                "wireplumber.profiles = { main = { monitor.alsa = disabled "
                "monitor.bluez = disabled monitor.alsa-midi = disabled "
                "monitor.bluez-midi = disabled monitor.v4l2 = disabled "
                "monitor.libcamera = disabled } }\n")
            for directory, monitors in (("main.lua.d", ("alsa", "v4l2", "libcamera")),
                                        ("bluetooth.lua.d", ("bluez", "bluez_midi"))):
                legacy = Path(runtime, "wireplumber", directory)
                legacy.mkdir(parents=True)
                (legacy / "89-no-hardware.lua").write_text("\n".join(
                    f"if {name}_monitor then {name}_monitor.enabled = false end"
                    for name in monitors))
            start("pipewire", stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            wait_for(lambda: Path(runtime, "pipewire-0").exists(), "PipeWire")
            start("pipewire-pulse", stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            start("wireplumber", stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            wait_for(lambda: "Server Name:" in run("pactl", "info"), "Pulse compatibility")

            for name in ("qa_in", "qa_out"):
                run("pw-cli", "create-node", "adapter",
                    "{ factory.name = support.null-audio-sink node.name = " + name +
                    " media.class = Audio/Sink audio.position = [ FL FR ] object.linger = true "
                    "adapter.auto-port-config = { mode = dsp monitor = true position = preserve } }")
            wait_for(lambda: pulse_endpoint_exists(run("pactl", "list", "short", "sinks"),
                                                    "qa_in"), "input sink")
            wait_for(lambda: pulse_endpoint_exists(run("pactl", "list", "short", "sources"),
                                                    "qa_out.monitor"), "output monitor")

            host = start(str(options.host or repo / "native/openxlr-vst3-host"),
                         str(options.plugin), "C18D3C1E719E4E29924D3ECAA5E4DA18",
                         "qa_vst3", "2", "48000", "1=0.25",
                         stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.PIPE, text=True)
            lines = queue.Queue(maxsize=4096)

            def read_host():
                for line in host.stdout:
                    lines.put(line.strip(), timeout=5)

            reader = threading.Thread(target=read_host, daemon=True)
            reader.start()

            def next_matching(prefix, timeout=10):
                deadline = time.monotonic() + timeout
                while True:
                    line = lines.get(timeout=max(0.01, deadline - time.monotonic()))
                    if line.startswith(prefix):
                        return line

            next_matching("ready")
            wait_for(lambda: "qa_vst3:playback_0" in run("pw-link", "-i"), "VST3 ports")
            for index, side in enumerate(("FL", "FR")):
                run("pw-link", f"qa_in:monitor_{side}", f"qa_vst3:playback_{index}")
                run("pw-link", f"qa_vst3:capture_{index}", f"qa_out:playback_{side}")

            def capture_peak():
                payload = array.array("f", (0.2 * math.sin(i * 2 * math.pi * 440 / 48000)
                                             for i in range(48000 * 3)
                                             for _ in range(2))).tobytes()
                capture = start("parec", "-d", "qa_out.monitor", "--format=float32le",
                                "--channels=2", "--rate=48000",
                                "--property=node.name=qa_capture", stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE)
                wait_for_stream(capture, "source-outputs", "capture stream")
                playback = start("pacat", "-d", "qa_in", "--format=float32le",
                                 "--channels=2", "--rate=48000",
                                 "--property=node.name=qa_playback", stdin=subprocess.PIPE,
                                 stderr=subprocess.PIPE)
                writer = threading.Thread(target=lambda: playback.communicate(payload), daemon=True)
                writer.start()
                wait_for_stream(playback, "sink-inputs", "playback stream")
                received = bytearray()
                with selectors.DefaultSelector() as poll:
                    poll.register(capture.stdout, selectors.EVENT_READ)
                    until = time.monotonic() + 2.5
                    while time.monotonic() < until:
                        if poll.select(0.1):
                            received.extend(os.read(capture.stdout.fileno(), 65536))
                for process in (playback, capture):
                    process.terminate()
                    process.wait(timeout=3)
                    processes.remove(process)
                writer.join(timeout=3)
                samples = array.array("f")
                samples.frombytes(received[:len(received) // 4 * 4])
                assert len(samples) > 48000, len(samples)
                return max(abs(value) for value in samples[-48000:])

            quarter = capture_peak()
            assert 0.045 < quarter < 0.055, quarter
            host.stdin.write("set 1 0.5\ngetstate\n")
            host.stdin.flush()
            saved = next_matching("state ")
            half = capture_peak()
            assert 0.095 < half < 0.105, half
            host.stdin.write(f"set 1 0.1\nloadstate {saved[6:]}\n")
            host.stdin.flush()
            next_matching("state-loaded")
            restored = capture_peak()
            assert abs(restored - half) < 0.006, (half, restored)
            host.stdin.write("set 0 1\n")
            host.stdin.flush()
            bypassed = capture_peak()
            assert 0.195 < bypassed < 0.205, bypassed
            print(f"PASS VST3 audio gain={quarter:.4f}/{half:.4f}, "
                  f"state={restored:.4f}, bypass={bypassed:.4f}")

            host.stdin.write("quit\n")
            host.stdin.flush()
            assert host.wait(timeout=5) == 0
            processes.remove(host)
            reader.join(timeout=2)
            wait_for(lambda: "qa_vst3:playback_0" not in run("pw-link", "-i"),
                     "VST3 cleanup")
            print("PASS VST3 graceful cleanup")
        finally:
            for process in reversed(processes):
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=3)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=3)


if __name__ == "__main__":
    main()
