#!/usr/bin/env python3
"""Measure the native compensation helper in the real PipeWire graph."""

from __future__ import annotations

import argparse
import array
import pathlib
import subprocess
import tempfile
import time

from private_audio import PrivateAudio


ROOT = pathlib.Path(__file__).resolve().parents[1]
RATE = 48_000
DELAY = 1_537


def wait_for_port(audio: PrivateAudio, pattern: str, output: bool) -> str:
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        ports = audio.run("pw-link", "-o" if output else "-i").splitlines()
        match = next((port.strip() for port in ports if pattern in port), None)
        if match:
            return match
        time.sleep(0.05)
    raise RuntimeError(f"PipeWire port did not appear: {pattern}")


def stop(process: subprocess.Popen[bytes]) -> None:
    if process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=2)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=2)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=pathlib.Path,
                        default=ROOT / "native" / "openxlr-delay-host")
    options = parser.parse_args()
    host_path = options.host.resolve()
    if not host_path.is_file():
        raise SystemExit(f"latency helper not found: {host_path}")
    token = f"{int(time.time_ns()):x}"
    reference_node = f"OpenXLR_delay_reference_{token}"
    delay_node = f"OpenXLR_delay_acceptance_{token}"
    player_node = f"OpenXLR_delay_player_{token}"
    recorder_node = f"OpenXLR_delay_recorder_{token}"
    with PrivateAudio() as audio, tempfile.TemporaryDirectory(prefix="openxlr-delay-") as temporary:
        source = pathlib.Path(temporary) / "source.f32"
        capture = pathlib.Path(temporary) / "capture.f32"
        samples = array.array("f", [0.0] * 12_000)
        samples[4_096] = 0.75
        source.write_bytes(samples.tobytes())

        reference_host = audio.start(
            str(host_path), reference_node, "1", "0", str(RATE),
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        delayed_host = audio.start(
            str(host_path), delay_node, "1", str(DELAY), str(RATE),
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        source_stream = source.open("rb")
        capture_stream = capture.open("wb")
        recorder: subprocess.Popen[bytes] | None = None
        player: subprocess.Popen[bytes] | None = None
        try:
            for host in (reference_host, delayed_host):
                assert host.stdout is not None
                if host.stdout.readline().strip() != b"ready":
                    raise RuntimeError(host.stderr.read().decode(errors="replace"))
            recorder = audio.start(
                "pw-record", "--target", "0", "--rate", str(RATE),
                "--channels", "2", "--channel-map", "FL,FR", "--format", "f32",
                "-P", f'{{ node.name = "{recorder_node}" node.autoconnect = false }}',
                "-",
                stdout=capture_stream,
                stderr=subprocess.PIPE,
            )
            player = audio.start(
                "pw-play", "--target", "0", "--rate", str(RATE),
                "--channels", "1", "--channel-map", "MONO", "--format", "f32",
                "-P",
                f'{{ node.name = "{player_node}" node.autoconnect = false }}',
                "-",
                stdin=source_stream,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
            )
            player_out = wait_for_port(audio, player_node, output=True)
            reference_in = wait_for_port(audio, f"{reference_node}:playback_0", output=False)
            reference_out = wait_for_port(audio, f"{reference_node}:capture_0", output=True)
            delay_in = wait_for_port(audio, f"{delay_node}:playback_0", output=False)
            delay_out = wait_for_port(audio, f"{delay_node}:capture_0", output=True)
            record_inputs = audio.run("pw-link", "-i").splitlines()
            left = next(port.strip() for port in record_inputs if recorder_node in port and "input_FL" in port)
            right = next(port.strip() for port in record_inputs if recorder_node in port and "input_FR" in port)
            for output, input_ in (
                (player_out, reference_in), (reference_out, left),
                (player_out, delay_in), (delay_out, right),
            ):
                audio.run("pw-link", "-w", output, input_)

            player.wait(timeout=5)
            time.sleep(0.15)
            stop(recorder)
            capture_stream.close()
            interleaved = array.array("f")
            interleaved.frombytes(capture.read_bytes())
            dry = interleaved[0::2]
            delayed = interleaved[1::2]
            dry_peak = max(range(len(dry)), key=lambda index: abs(dry[index]))
            delayed_peak = max(range(len(delayed)), key=lambda index: abs(delayed[index]))
            measured = delayed_peak - dry_peak
            if measured != DELAY:
                raise RuntimeError(
                    f"expected {DELAY} delayed samples, measured {measured} "
                    f"(dry={dry_peak}, delayed={delayed_peak})"
                )
            if abs(dry[dry_peak] - 0.75) > 0.001 or abs(delayed[delayed_peak] - 0.75) > 0.001:
                raise RuntimeError("the compensation helper changed the impulse amplitude")
            print(f"PASS PipeWire latency compensation: {measured} samples, amplitude preserved")
        finally:
            if player is not None:
                stop(player)
            if recorder is not None:
                stop(recorder)
            source_stream.close()
            capture_stream.close()
            for host in (reference_host, delayed_host):
                if host.stdin is not None and host.poll() is None:
                    try:
                        host.stdin.write(b"quit\n")
                        host.stdin.flush()
                        host.wait(timeout=2)
                    except (BrokenPipeError, subprocess.TimeoutExpired):
                        stop(host)


if __name__ == "__main__":
    main()
