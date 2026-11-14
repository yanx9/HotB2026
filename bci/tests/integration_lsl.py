"""~30 s local end-to-end test: synthetic LSL -> actual bridge subprocess -> two UDP consumers.

Also verifies return markers and stale-input neutralisation.
Requires permission to create local sockets; no headset, Unity, or Qt needed.

    python bci/tests/integration_lsl.py
"""

import json
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import threading
import time
import uuid

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from pylsl import StreamInfo, StreamInlet, StreamOutlet, local_clock, resolve_byprop

from calibration_session import DemoSource
from processing import SIGNALS, UNICORN_LABELS
from profiles import new_profile, save_profile
from test_calibration import record_simulated


def receiver():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", 0))
    sock.setblocking(False)
    return sock


def main():
    source = DemoSource()
    stream_name = "CalibrationIntegration-" + uuid.uuid4().hex[:8]
    info = StreamInfo(stream_name, "EEG", 8, 250, "float32", "fake-" + stream_name)
    channels = info.desc().append_child("channels")
    for label in UNICORN_LABELS:
        channel = channels.append_child("channel")
        channel.append_child_value("label", label)
        channel.append_child_value("unit", "microvolts")
    outlet = StreamOutlet(info, chunk_size=10)
    sinks = [receiver(), receiver()]
    reservation = receiver()
    marker_port = reservation.getsockname()[1]
    reservation.close()
    marker_name = stream_name + "-Markers"
    plan = [("rest", 4), ("eyes_closed", 4), ("rest", 2), ("right_hand", 4),
            ("rest", 2), ("left_hand", 4), ("rest", 2), ("jaw", 3), ("rest", 2)]
    stop = threading.Event()
    errors = []

    def publish():
        try:
            start = local_clock()
            sent = 0
            while not stop.is_set():
                while sent + 10 <= (local_clock() - start) * 250:
                    t = (sent + np.arange(10)) / 250
                    elapsed, state = float(t[0]), "rest"
                    for task, duration in plan:
                        if elapsed < duration:
                            state = task
                            break
                        elapsed -= duration
                    outlet.push_chunk(source.samples(t, state).tolist(), start + (sent + 9) / 250)
                    sent += 10
                time.sleep(.005)
        except Exception as exc:
            errors.append(exc)

    with tempfile.TemporaryDirectory() as tmp:
        profile = new_profile("integration", source.config, source.stream, True)
        profile["calibrations"] = {name: record_simulated(name) for name in SIGNALS}
        path = Path(tmp) / "profile.json"
        save_profile(path, profile)
        command = [sys.executable, "-u", str(Path(__file__).resolve().parents[1] / "lsl_bridge.py"),
                   "--name", stream_name, "--profile", str(path), "--allow-simulated",
                   "--port", str(sinks[0].getsockname()[1]),
                   "--target", f"127.0.0.1:{sinks[1].getsockname()[1]}",
                   "--marker-port", str(marker_port), "--marker-name", marker_name]
        log_path = Path(tmp) / "bridge.log"
        with log_path.open("w") as log:
            process = subprocess.Popen(command, stdout=log, stderr=log)
            thread = threading.Thread(target=publish, daemon=True)
            thread.start()
            try:
                marker_streams = resolve_byprop("name", marker_name, minimum=1, timeout=10)
                assert marker_streams, log_path.read_text()
                marker_inlet = StreamInlet(marker_streams[0])
                marker_inlet.open_stream(timeout=3)
                sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                # Invalid UDP packets must not terminate the marker thread.
                for bad in (b"garbage", b"null", b"[]", b'{"name":"bad","t":NaN}'):
                    sender.sendto(bad, ("127.0.0.1", marker_port))
                sender.sendto(json.dumps({"type": "marker", "name": "integration", "value": 7,
                                           "t": time.time()}).encode(), ("127.0.0.1", marker_port))
                marker, _ = marker_inlet.pull_sample(timeout=3)
                assert marker == ["integration:7"], marker
                sender.close()
                marker_inlet.close_stream()
                observed = [set(), set()]
                latest = [{}, {}]
                commands = set()
                until = time.monotonic() + 29
                while time.monotonic() < until:
                    assert process.poll() is None, log_path.read_text()
                    for i, sink in enumerate(sinks):
                        while True:
                            try:
                                packet, _ = sink.recvfrom(8192)
                            except BlockingIOError:
                                break
                            message = json.loads(packet)
                            assert set(message) == {"type", "name", "value", "t"}
                            assert np.isfinite(message["value"])
                            if message["type"] == "command":
                                commands.add(message["name"])
                            else:
                                latest[i][message["name"]] = message["value"]
                                if message["name"].endswith("_active") and message["value"] == 1:
                                    observed[i].add(message["name"])
                    time.sleep(.01)
                expected = {name + "_active" for name in SIGNALS}
                for received in observed:
                    assert expected <= received, (expected, received, log_path.read_text())
                assert {"left", "right"} <= commands, commands
                stop.set()
                thread.join(2)
                time.sleep(1)
                for i, sink in enumerate(sinks):
                    while True:
                        try:
                            packet, _ = sink.recvfrom(8192)
                        except BlockingIOError:
                            break
                        message = json.loads(packet)
                        latest[i][message["name"]] = message["value"]
                    for name in SIGNALS:
                        assert latest[i][name] == 0, latest[i]
                        assert latest[i][name + "_valid"] == 0, latest[i]
                assert not errors, errors
                print("PASS: all four calibrated states, both UDP consumers, left/right commands, markers, stale zeros")
            finally:
                stop.set()
                thread.join(2)
                process.terminate()
                process.wait(timeout=5)
                for sink in sinks:
                    sink.close()


if __name__ == "__main__":
    main()
