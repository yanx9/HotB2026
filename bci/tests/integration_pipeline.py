"""Whole path without hardware, in real time (about a minute):

    synthetic Unicorn -> g.Pype filters -> calibration of jaw -> profile
    profile -> bci_pipeline.py process -> UDP JSON (as a game receives it) + game markers -> JSONL

    python bci/tests/integration_pipeline.py

Synthetic data only: this checks the plumbing, not anyone's EEG.
"""

import json
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time

BCI = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(BCI))

from calibration_session import CalibrationSession, Protocol
from gpype_nodes import PLAN, PipelineSource
from profiles import fit_calibration, load_profile, new_profile, save_profile
from dataclasses import asdict


def calibrate_jaw(path):
    source = PipelineSource(fake=True)
    try:
        while not source.fresh():
            source.read()
            time.sleep(.01)
        protocol = Protocol(trials=6, rest_s=1.5, active_s=1.5, settle_s=0.5, prepare_s=1.0)
        session = CalibrationSession("jaw", source.config, time.monotonic(), protocol)
        while not session.completed:
            source.set_state("jaw" if session.phase == "active" else "rest")
            samples, stamps = source.read()
            if source.gap:
                session.window.clear()
                session.last_sample = None
            session.ingest(samples, stamps)
            session.advance(time.monotonic())
            time.sleep(.01)
        profile = new_profile("integration", source.config, source.stream, True)
        profile["calibrations"]["jaw"] = fit_calibration("jaw", session.trials, asdict(protocol))
        save_profile(path, profile)
    finally:
        source.close()
    return load_profile(path)["calibrations"]["jaw"]


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def main():
    with tempfile.TemporaryDirectory() as tmp:
        profile_path, marker_path = Path(tmp) / "profile.json", Path(tmp) / "markers.jsonl"
        model = calibrate_jaw(profile_path)
        assert model["status"] == "ready", model["reason"]
        print(f"kalibracja jaw przez potok g.Pype: {model['status']}, "
              f"trafność {model['validation']['balanced_accuracy']:.0%}")

        game = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        game.bind(("127.0.0.1", 0))
        game.settimeout(.5)
        marker_port = free_port()
        process = subprocess.Popen([sys.executable, "-u", str(BCI / "bci_pipeline.py"), "--fake", "--allow-simulated",
                                    "--no-gui", "--profile", str(profile_path), "--port", str(game.getsockname()[1]),
                                    "--marker-port", str(marker_port), "--marker-file", str(marker_path)])
        try:
            seen, order, bridge = {}, [], None
            deadline = time.monotonic() + sum(seconds for _, seconds in PLAN) + 6
            while time.monotonic() < deadline:
                assert process.poll() is None, "potok zakończył się przedwcześnie"
                try:
                    data, bridge = game.recvfrom(4096)
                except socket.timeout:
                    continue
                message = json.loads(data)
                assert set(message) == {"type", "name", "value", "t"}, message
                seen.setdefault((message["type"], message["name"]), []).append(message["value"])
                if message["name"] == "jaw_active" and (not order or order[-1] != message["value"]):
                    order.append(message["value"])
            # the game answers to the address the signals came from
            game.sendto(json.dumps({"type": "marker", "name": "cut", "value": 3, "t": time.time()}).encode(),
                        (bridge[0], marker_port))
            time.sleep(.5)
        finally:
            process.terminate()
            process.wait(10)
            game.close()

        assert order[:3] == [0, 1, 0], f"jaw_active powinno włączyć się raz w cyklu: {order}"
        assert max(seen[("metric", "jaw")]) > .9 and min(seen[("metric", "jaw_valid")][20:]) == 1
        rate = len(seen[("metric", "jaw")]) / (sum(seconds for _, seconds in PLAN) + 6)
        assert 8 <= rate <= 11, f"oczekiwane ~10 Hz, jest {rate:.1f}"
        # nieskalibrowane sygnały są neutralne i oznaczone jako nieważne, bez heurystyk w zastępstwie
        for name in ("eyes_closed", "right_hand", "left_hand", "laterality"):
            assert set(seen[("metric", name)]) == {0} and set(seen[("metric", name + "_valid")]) == {0}, name
        assert ("command", "right") not in seen and ("command", "left") not in seen
        assert ("metric", "focus") in seen
        marker = json.loads(marker_path.read_text(encoding="utf-8").splitlines()[-1])
        assert (marker["marker"], marker["value"]) == ("cut", 3), marker
        print(f"potok -> gra: {len(seen)} nazw sygnałów, {rate:.1f} Hz, jaw_active {order[:3]}, znacznik zapisany")
        print("OK")


if __name__ == "__main__":
    main()
