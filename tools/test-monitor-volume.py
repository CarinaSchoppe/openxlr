#!/usr/bin/env python3
"""Run monitor controls on private PipeWire sockets with policy but no hardware."""
import os
from pathlib import Path
import subprocess
import tempfile
import time


def main():
    with tempfile.TemporaryDirectory(prefix="openxlr-monitor-test-") as runtime:
        env = dict(os.environ, XDG_RUNTIME_DIR=runtime, PIPEWIRE_RUNTIME_DIR=runtime,
                   XDG_CONFIG_HOME=runtime + "/config", XDG_STATE_HOME=runtime + "/state",
                   PULSE_SERVER="unix:" + runtime + "/pulse/native",
                   PIPEWIRE_REMOTE="pipewire-0", OPENXLR_TEST_MONITOR_VOLUME="1")
        processes = []
        with open(runtime + "/servers.log", "w+") as log:
            try:
                bus = subprocess.Popen(["dbus-daemon", "--session", "--nofork", "--print-address"],
                                       stdout=subprocess.PIPE, stderr=log, text=True)
                processes.append(bus)
                env["DBUS_SESSION_BUS_ADDRESS"] = bus.stdout.readline().strip()
                # The policy-only session manager never discovers ALSA devices.
                for command, socket in [("pipewire", "pipewire-0"), ("pipewire-pulse", "pulse/native")]:
                    process = subprocess.Popen([command], env=env, stdout=log, stderr=log)
                    processes.append(process)
                    deadline = time.monotonic() + 10
                    while not Path(runtime, socket).exists():
                        if process.poll() is not None or time.monotonic() > deadline:
                            raise RuntimeError(command + " did not create its socket")
                        time.sleep(0.05)
                help_text = subprocess.check_output(["wireplumber", "--help"], text=True)
                if "--profile" in help_text:
                    policies = [["--profile", "policy"]]
                else:
                    # WirePlumber 0.4 keeps default-node management in main,
                    # separate from routing policy. Disable its hardware
                    # monitors before loading that configuration as well.
                    fragments = Path(env["XDG_CONFIG_HOME"], "wireplumber", "main.lua.d")
                    fragments.mkdir(parents=True)
                    (fragments / "51-no-hardware.lua").write_text(
                        "alsa_monitor.enabled = false\nv4l2_monitor.enabled = false\n"
                        "libcamera_monitor.enabled = false\n")
                    policies = [["--config-file", "main.conf"], ["--config-file", "policy.conf"]]
                for policy in policies:
                    processes.append(subprocess.Popen(["wireplumber", *policy], env=env, stdout=log, stderr=log))
                subprocess.run(["dotnet", "test", "src/OpenXLR.Tests/OpenXLR.Tests.csproj",
                                "-c", "Release", "--no-build", "--filter",
                                "FullyQualifiedName~MonitorVolumeIntegrationTests"],
                               env=env, check=True, timeout=90,
                               cwd=Path(__file__).resolve().parent.parent)
            except Exception:
                log.flush()
                log.seek(0)
                print(log.read())
                raise
            finally:
                for process in reversed(processes):
                    if process.poll() is None:
                        process.terminate()
                        try:
                            process.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            process.kill()
                            process.wait()


if __name__ == "__main__":
    main()
