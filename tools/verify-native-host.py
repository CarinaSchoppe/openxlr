#!/usr/bin/env python3
"""Test the native DSP host on private PipeWire/Pulse servers, without hardware.

WirePlumber runs with hardware monitors disabled on a private D-Bus session.
Also suitable for CI. Requires PipeWire, WirePlumber, Pulse tools, LSP LV2.
"""
import array
import argparse
import json
import math
import os
from pathlib import Path
import queue
import selectors
import shutil
import subprocess
import tempfile
import threading
import time
from native_ui_smoke import wheel_compressor_output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native-ui", action="store_true", help="Test a real native LSP control gesture; use xvfb-run in CI")
    parser.add_argument("--screenshot", type=Path, help="Capture the tested native editor before the gesture (requires ImageMagick)")
    parser.add_argument("--disposable-ci-profile", action="store_true",
                        help="Use a fresh GitHub-hosted runner's disposable LSP profile instead of bubblewrap")
    options = parser.parse_args()
    if options.disposable_ci_profile:
        if not options.native_ui or os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
            parser.error("--disposable-ci-profile requires --native-ui on a GitHub-hosted runner")
        if (Path.home() / ".config/lsp-plugins").exists():
            parser.error("the disposable runner must have a fresh LSP profile")
    repo = Path(__file__).resolve().parents[1]
    processes = []
    with tempfile.TemporaryDirectory(prefix="openxlr-native-test-") as runtime:
        env = dict(os.environ, XDG_RUNTIME_DIR=runtime, PIPEWIRE_RUNTIME_DIR=runtime, XDG_CONFIG_HOME=runtime,
                   PULSE_SERVER=f"unix:{runtime}/pulse/native")

        def start(*args, **kwargs):
            process = subprocess.Popen(args, env=env, **kwargs)
            processes.append(process)
            return process

        def run(*args):
            return subprocess.run(args, env=env, check=True, capture_output=True, text=True, timeout=15).stdout

        def wait_for(check):
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline:
                try:
                    if check():
                        return
                except subprocess.CalledProcessError:
                    pass
                time.sleep(0.05)
            raise AssertionError("private audio server/ports did not become ready")

        try:
            bus = start("dbus-daemon", "--session", "--nofork", "--print-address=1", stdout=subprocess.PIPE, text=True)
            env["DBUS_SESSION_BUS_ADDRESS"] = bus.stdout.readline().strip()
            policy = Path(runtime, "wireplumber/wireplumber.conf.d")
            policy.mkdir(parents=True)
            (policy / "99-no-hardware.conf").write_text(
                "wireplumber.profiles = { main = { monitor.alsa = disabled monitor.bluez = disabled "
                "monitor.alsa-midi = disabled monitor.bluez-midi = disabled "
                "monitor.v4l2 = disabled monitor.libcamera = disabled } }\n")
            # Ubuntu's WirePlumber 0.4 uses Lua fragments; 0.5 uses the config
            # above. Both disable hardware before their enable-all stage.
            for directory, monitors in (("main.lua.d", ("alsa", "v4l2", "libcamera")),
                                        ("bluetooth.lua.d", ("bluez", "bluez_midi"))):
                legacy = Path(runtime, "wireplumber", directory)
                legacy.mkdir(parents=True)
                (legacy / "89-no-hardware.lua").write_text("\n".join(
                    f"if {name}_monitor then {name}_monitor.enabled = false end" for name in monitors))
            start("pipewire", stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            wait_for(lambda: Path(runtime, "pipewire-0").exists())
            start("pipewire-pulse", stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            start("wireplumber", stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            wait_for(lambda: "Server Name:" in run("pactl", "info"))
            hardware = [(n["type"], n.get("info", {}).get("props", {}))
                        for n in json.loads(run("pw-dump"))
                        if n.get("info", {}).get("props", {}).get("device.api")]
            assert not hardware, f"private graph must not own hardware: {hardware}"
            for name in ("qa_in", "qa_out"):
                run("pw-cli", "create-node", "adapter",
                    "{ factory.name = support.null-audio-sink node.name = " + name +
                    " media.class = Audio/Sink audio.position = [ FL FR ] object.linger = true "
                    "adapter.auto-port-config = { mode = dsp monitor = true position = preserve } }")
            host_command = [str(repo / "native/openxlr-lv2-host")]
            if options.native_ui and not options.disposable_ci_profile:
                assert shutil.which("bwrap"), "native UI isolation requires bubblewrap (bwrap)"
                # LSP 1.2.14 ignores XDG_CONFIG_HOME. Overlay only this child
                # process's legacy config directory; never edit user settings
                # or depend on whether the user already saw LSP's greeting.
                legacy_config = Path(runtime, "legacy-config")
                legacy_config.mkdir()
                host_command = ["bwrap", "--bind", "/", "/", "--dev-bind", "/dev", "/dev",
                                "--bind", str(legacy_config), str(Path.home() / ".config"),
                                "--die-with-parent", "--", *host_command]
            host = start(*host_command,
                         "http://lsp-plug.in/plugins/lv2/compressor_stereo", "qa_plugin", "2", "48000",
                         "g_out=0.25", "al=1", "cr=1", stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
            lines = queue.Queue(maxsize=4096)

            def read():
                for line in host.stdout:
                    lines.put(line.strip(), timeout=5)

            reader = threading.Thread(target=read, daemon=True)
            reader.start()
            deadline = time.monotonic() + 10
            while lines.get(timeout=max(0.01, deadline - time.monotonic())) != "ready":
                pass
            wait_for(lambda: "qa_plugin:playback_0" in run("pw-link", "-i"))
            for index, side in enumerate(("FL", "FR")):
                run("pw-link", f"qa_in:monitor_{side}", f"qa_plugin:playback_{index}")
                run("pw-link", f"qa_plugin:capture_{index}", f"qa_out:playback_{side}")

            def peak():
                data = array.array("f", (0.2 * math.sin(i * 2 * math.pi * 440 / 48000)
                                          for i in range(48000 * 3) for _ in range(2))).tobytes()
                capture = start("parec", "-d", "qa_out.monitor", "--format=float32le", "--channels=2", "--rate=48000",
                                "--property=node.name=qa_capture",
                                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
                playback = start("pacat", "-d", "qa_in", "--format=float32le", "--channels=2", "--rate=48000",
                                 "--property=node.name=qa_playback",
                                 stdin=subprocess.PIPE, stderr=subprocess.DEVNULL)
                writer = threading.Thread(target=lambda: playback.communicate(data), daemon=True)
                writer.start()
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
                assert len(samples) > 48000, "insufficient captured audio"
                return max(abs(x) for x in samples[-48000:])

            first = peak()
            assert 0.045 < first < 0.055, first
            host.stdin.write("set g_out 0.5\n")
            host.stdin.flush()
            second = peak()
            assert 0.095 < second < 0.105, second
            if options.native_ui:
                host.stdin.write("show\n")
                host.stdin.flush()
                deadline = time.monotonic() + 10
                while lines.get(timeout=max(0.01, deadline - time.monotonic())) != "ui opened":
                    pass
                time.sleep(1.5)  # include LSP's delayed first-run greeting in the acceptance path
                wheel_compressor_output("qa_plugin", options.screenshot)
                deadline = time.monotonic() + 5
                while True:
                    try:
                        message = lines.get(timeout=max(0.01, deadline - time.monotonic()))
                    except queue.Empty as error:
                        raise AssertionError("native gesture did not emit a g_out control change; inspect the screenshot") from error
                    if message.startswith("control g_out "):
                        value = float(message.split()[2])
                        break
                assert value > 0.5 and abs(peak() - 0.2 * value) < 0.006, value
                print("PASS native LSP gesture reaches the actual DSP instance", flush=True)
            host.stdin.write("set g_out 1\nset cr 4\nset al 0.1\n")
            host.stdin.flush()
            compressed = peak()
            assert 0.08 < compressed < 0.17, compressed
            print(f"PASS isolated native DSP: gain .25={first:.4f}, gain .5={second:.4f}, compression={compressed:.4f}", flush=True)
            host.stdin.write("quit\n")
            host.stdin.flush()
            assert host.wait(timeout=5) == 0
            processes.remove(host)
            reader.join(timeout=2)
            print("PASS native host graceful shutdown", flush=True)
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
