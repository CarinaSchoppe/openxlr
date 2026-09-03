"""Disposable PipeWire/Pulse/WirePlumber session for audio acceptance tests.

No hardware monitors, no user config changes, bounded commands and guaranteed
child cleanup. Both WirePlumber 0.4 (Ubuntu) and 0.5 use their own policy format.
"""
from pipewire_snapshot import parse_dump
import os
from pathlib import Path
import subprocess
import tempfile
import time


class PrivateAudio:
    def __init__(self):
        self.processes = []
        self.logs = []
        self.directory = tempfile.TemporaryDirectory(prefix="openxlr-private-audio-")
        self.path = Path(self.directory.name)
        self.env = dict(os.environ, XDG_RUNTIME_DIR=str(self.path), PIPEWIRE_RUNTIME_DIR=str(self.path),
                        XDG_CONFIG_HOME=str(self.path), PULSE_SERVER=f"unix:{self.path}/pulse/native")

    def start(self, *args, **kwargs):
        process = subprocess.Popen(args, env=self.env, **kwargs)
        self.processes.append(process)
        return process

    def logged(self, *args):
        path = self.path / f"{Path(args[0]).name}-{len(self.logs)}.log"
        self.logs.append(path)
        with path.open("w") as output:
            return self.start(*args, stdout=output, stderr=subprocess.STDOUT)

    def run(self, *args):
        return subprocess.run(args, env=self.env, check=True, capture_output=True, text=True, timeout=15).stdout

    @staticmethod
    def wait_for(check, seconds=15):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            try:
                if check():
                    return
            except subprocess.CalledProcessError:
                pass
            time.sleep(0.05)
        raise AssertionError("private audio server/ports did not become ready")

    def __enter__(self):
        try:
            bus = self.start("dbus-daemon", "--session", "--nofork", "--print-address=1", stdout=subprocess.PIPE, text=True)
            self.env["DBUS_SESSION_BUS_ADDRESS"] = bus.stdout.readline().strip()
            policy = self.path / "wireplumber/wireplumber.conf.d"
            policy.mkdir(parents=True)
            (policy / "99-no-hardware.conf").write_text(
                "wireplumber.profiles = { main = { monitor.alsa = disabled monitor.bluez = disabled "
                "monitor.alsa-midi = disabled monitor.bluez-midi = disabled "
                "monitor.v4l2 = disabled monitor.libcamera = disabled } }\n")
            for directory, monitors in (("main.lua.d", ("alsa", "v4l2", "libcamera")),
                                        ("bluetooth.lua.d", ("bluez", "bluez_midi"))):
                legacy = self.path / "wireplumber" / directory
                legacy.mkdir(parents=True)
                (legacy / "89-no-hardware.lua").write_text("\n".join(
                    f"if {name}_monitor then {name}_monitor.enabled = false end" for name in monitors))
            self.logged("pipewire")
            self.wait_for(lambda: (self.path / "pipewire-0").exists())
            self.logged("pipewire-pulse")
            self.logged("wireplumber")
            self.wait_for(lambda: "Server Name:" in self.run("pactl", "info"))
            hardware = [n.get("info", {}).get("props", {}) for n in parse_dump(self.run("pw-dump"))
                        if n.get("info", {}).get("props", {}).get("device.api")]
            assert not hardware, f"private graph must not own hardware: {hardware}"
            return self
        except BaseException:
            self.__exit__(True, None, None)
            raise

    def __exit__(self, error_type, error, traceback):
        try:
            for process in reversed(self.processes):
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=3)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=3)
            if error_type:
                for path in self.logs:
                    print(f"--- {path.name} ---\n{path.read_text(errors='replace')[-12000:]}", flush=True)
        finally:
            self.directory.cleanup()
