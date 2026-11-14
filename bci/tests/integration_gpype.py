"""~32 s test: UDPSender-compatible bytes -> real bridge -> two Unity-format sockets.

Also tests all four states, commands, marker JSONL, exclusive input port,
neutralisation after stopped EEG and recovery. No g.Pype, LSL, Qt or headset.
"""

import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import threading
import time

import numpy as np

BCI = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(BCI))
from calibration_session import DemoSource
from fake_gpype import PLAN, cycle_state
from gpype_source import GPypeUDPSource
from processing import SIGNALS
from profiles import new_profile, save_profile
from test_calibration import record_simulated


def receiver():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", 0))
    sock.setblocking(False)
    return sock


def main():
    sample_source = DemoSource()
    input_reservation = GPypeUDPSource(port=0, simulated=True)
    input_port = input_reservation.sock.getsockname()[1]
    profile = new_profile("udp-integration", input_reservation.config, input_reservation.stream, True)
    profile["calibrations"] = {name: record_simulated(name) for name in SIGNALS}
    input_reservation.close()
    sinks = [receiver(), receiver()]
    marker_reservation = receiver()
    marker_port = marker_reservation.getsockname()[1]
    marker_reservation.close()
    stop = threading.Event()
    paused = threading.Event()
    resume_jaw = threading.Event()
    errors = []

    def publish():
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            start, sent = time.monotonic(), 0
            while not stop.is_set():
                elapsed = time.monotonic() - start
                if (sent + 4) / 250 <= elapsed:
                    if elapsed - (sent + 4) / 250 > .1:
                        sent = int(elapsed * 250) - 4
                    t = (sent + np.arange(4)) / 250
                    if not paused.is_set():
                        state = "jaw" if resume_jaw.is_set() else cycle_state(float(t[0]))
                        sock.sendto(sample_source.samples(t, state).astype("<f8").tobytes(),
                                    ("127.0.0.1", input_port))
                    sent += 4
                time.sleep(.002)
        except Exception as exc:
            errors.append(exc)
        finally:
            sock.close()

    with tempfile.TemporaryDirectory() as tmp:
        path, log_path = Path(tmp) / "profile.json", Path(tmp) / "bridge.log"
        marker_path = Path(tmp) / "markers.jsonl"
        save_profile(path, profile)
        # Make any accidental runtime LSL import fail, even in an environment
        # where the old optional integration dependencies are installed.
        (Path(tmp) / "pylsl.py").write_text("raise ImportError('LSL must not be used by the g.Pype path')\n")
        env = dict(os.environ, PYTHONPATH=tmp)
        command = [sys.executable, "-u", str(BCI / "bci_bridge.py"), "--profile", str(path),
                   "--allow-simulated", "--synthetic-input", "--port", str(sinks[0].getsockname()[1]),
                   "--target", f"127.0.0.1:{sinks[1].getsockname()[1]}",
                   "--marker-port", str(marker_port), "--marker-file", str(marker_path)]
        observed = [set(), set()]
        latest = [{}, {}]
        commands, directions = set(), set()

        def collect(duration, process):
            until = time.monotonic() + duration
            while time.monotonic() < until:
                assert process.poll() is None, log_path.read_text()
                for i, sink in enumerate(sinks):
                    while True:
                        try:
                            raw, _ = sink.recvfrom(8192)
                        except BlockingIOError:
                            break
                        message = json.loads(raw)
                        assert set(message) == {"type", "name", "value", "t"}, message
                        assert np.isfinite(message["value"]), message
                        name, value = message["name"], message["value"]
                        if message["type"] == "command":
                            commands.add(name)
                        else:
                            latest[i][name] = value
                            if name.endswith("_active") and value == 1:
                                observed[i].add(name)
                            if name == "laterality" and abs(value) > .5:
                                directions.add("right" if value > 0 else "left")
                time.sleep(.005)

        with log_path.open("w") as log:
            process = subprocess.Popen(command, stdout=log, stderr=log, env=env)
            thread = threading.Thread(target=publish, daemon=True)
            try:
                until = time.monotonic() + 5
                while "EEG:" not in log_path.read_text():
                    assert process.poll() is None and time.monotonic() < until, log_path.read_text()
                    time.sleep(.02)
                try:
                    duplicate = GPypeUDPSource(port=input_port)
                except OSError:
                    pass
                else:
                    duplicate.close()
                    raise AssertionError("Two listeners were allowed to split one EEG input")
                thread.start()
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as marker_sender:
                    for bad in (b"garbage", b"null", b"[]", b'{"name":"bad","t":NaN}'):
                        marker_sender.sendto(bad, ("127.0.0.1", marker_port))
                    marker_sender.sendto(json.dumps({"type": "marker", "name": "integration", "value": 7,
                                                     "t": time.time()}).encode(), ("127.0.0.1", marker_port))
                collect(sum(duration for _, duration in PLAN), process)
                expected = {name + "_active" for name in SIGNALS}
                for received in observed:
                    assert expected <= received, (received, log_path.read_text())
                assert {"left", "right"} <= commands and directions == {"left", "right"}, (commands, directions)
                markers = [json.loads(line) for line in marker_path.read_text().splitlines()]
                assert len(markers) == 1 and markers[0]["marker"] == "integration:7", markers
                paused.set()
                collect(1, process)
                for received in latest:
                    for name in (*SIGNALS, "laterality", "focus"):
                        assert received[name] == received[name + "_valid"] == 0, received
                resume_jaw.set()
                paused.clear()
                collect(2, process)
                assert all(received["jaw_active"] == 1 for received in latest), latest
                assert not errors, errors
                print("PASS: raw g.Pype UDP, four calibrated states, two consumers, commands, JSONL markers, "
                      "exclusive port, stale zeros, recovery; no LSL import")
            except BaseException:
                print(log_path.read_text())
                raise
            finally:
                stop.set()
                if thread.is_alive():
                    thread.join(2)
                process.terminate()
                process.wait(timeout=5)
                for sink in sinks:
                    sink.close()


if __name__ == "__main__":
    main()
