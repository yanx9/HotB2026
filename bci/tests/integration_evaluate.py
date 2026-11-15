"""Collection and scoring of eyes and jaw, end to end, without hardware (about a minute):

    synthetic Unicorn -> pipeline with recording -> a stand-in game that sends the truth keys
    (space / F) and its own reactions as markers -> evaluate.py scores both signals

    python bci/tests/integration_evaluate.py

Checks that both signals are recorded and scored the same way. Synthetic data only.
"""

import json
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time

BCI = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(BCI))
sys.path.insert(0, str(BCI / "tests"))

import evaluate
from gpype_nodes import PLAN, stream_info
from processing import FeatureConfig
from profiles import new_profile, save_profile
from recording import load_recording
from test_calibration import record_simulated

CYCLES = 2
TRUTH = {"eyes_closed": ("truth_closed", "truth_open"), "jaw": ("truth_jaw_on", "truth_jaw_off")}
REACTION = {"eyes_closed_active": ("eyes_closed", "eyes_open"), "jaw_active": ("sprint_on", "sprint_off")}


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def main():
    with tempfile.TemporaryDirectory() as tmp:
        # eyes calibrated, jaw not: the pipeline falls back to the uncalibrated clench button
        profile = new_profile("integration", FeatureConfig(), stream_info(fake=True), True)
        profile["calibrations"]["eyes_closed"] = record_simulated("eyes_closed")
        profile_path = Path(tmp) / "profile.json"
        save_profile(profile_path, profile)

        game = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        game.bind(("127.0.0.1", 0))
        game.settimeout(.2)
        marker_port = free_port()
        markers = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

        def mark(name, value=0.0):
            markers.sendto(json.dumps({"type": "marker", "name": name, "value": value, "t": time.time()}).encode(),
                           ("127.0.0.1", marker_port))

        process = subprocess.Popen(
            [sys.executable, "-u", str(BCI / "bci_pipeline.py"), "--fake", "--allow-simulated", "--no-gui",
             "--profile", str(profile_path), "--session", "test_evaluate", "--port", str(game.getsockname()[1]),
             "--marker-port", str(marker_port), "--marker-file", str(Path(tmp) / "markers.jsonl")],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        folder, started = None, None
        for line in process.stdout:
            if "Ctrl+C" in line:
                started = time.monotonic()   # the synthetic task cycle starts with the pipeline
            if line.startswith("Nagrywanie:"):
                folder = Path(line.split(":", 1)[1].strip())
                break
        assert folder and started, "potok nie wystartował z nagrywaniem"
        threading.Thread(target=lambda: [None for _ in process.stdout], daemon=True).start()

        try:
            # the stand-in game: reacts to the pipeline's flags and reports its reactions as markers
            def play():
                state = {}
                while process.poll() is None:
                    try:
                        message = json.loads(game.recvfrom(4096)[0])
                    except (socket.timeout, OSError):
                        continue
                    names = REACTION.get(message["name"])
                    on = message["value"] >= 0.5
                    if names and on != state.get(message["name"], False):
                        state[message["name"]] = on
                        mark(names[0] if on else names[1])
            threading.Thread(target=play, daemon=True).start()
            time.sleep(.5)
            mark("session_start")
            mark("mode_eeg")

            # the player's truth keys follow the synthetic task plan
            schedule, t = [], 0.0
            for _ in range(CYCLES):
                for state, seconds in PLAN:
                    if state in TRUTH:
                        schedule += [(t, TRUTH[state][0]), (t + seconds, TRUTH[state][1])]
                    t += seconds
            for at, name in schedule:
                time.sleep(max(0.0, started + at - time.monotonic()))
                mark(name)
            time.sleep(3)
        finally:
            process.send_signal(signal.SIGINT)
            process.wait(15)
            game.close()

        try:
            data, events, _ = load_recording(folder)
            names = {e["name"] for e in events if e["sample"] is not None}
            for expected in ("truth_closed", "truth_open", "truth_jaw_on", "truth_jaw_off", "eyes_closed", "eyes_open",
                             "sprint_on", "sprint_off", "pipe_eyes_closed", "pipe_eyes_open", "pipe_jaw_on", "pipe_jaw_off"):
                assert expected in names, f"brak znacznika {expected} w nagraniu: {sorted(names)}"
            assert all(int(e["code"]) < 100 for e in events), "każdy znacznik powinien mieć stały kod"

            reports = {}
            for key, spec in evaluate.SIGNALS.items():
                assert evaluate.run(folder, data, events, spec), key
                reports[key] = json.loads((folder / f"eval_{key}.json").read_text(encoding="utf-8"))
                assert (folder / f"eval_{key}.png").stat().st_size > 5000
            eyes, jaw = reports["eyes"], reports["jaw"]
            assert set(eyes) == set(jaw), "raporty oczu i szczęki mają mieć te same pola"
            for key, report in reports.items():
                assert report["closures"] == CYCLES and report["detected"] == CYCLES, (key, report)
                assert report["false_closures_per_open_min"] == 0, (key, report)
                assert report["agreement_steady"] > .9, (key, report)
                latency = evaluate.system_latency(data, events, evaluate.SIGNALS[key])
                assert {"pipeline_s", "game_s", "total_s"} <= set(latency), (key, latency)
                assert latency["total_s"]["median"] < .5, (key, latency)
            # the truth keys are timed by the wall clock, so half a second of slack on top of the detector
            assert 0 <= eyes["median_close_latency_s"] < 2.5 and 0 <= jaw["median_close_latency_s"] < 1.0, reports

            # replaying from the filtered EEG gives the same answer as the live decisions
            for key, spec in evaluate.SIGNALS.items():
                assert evaluate.run(folder, data, events, spec, profile_path), key
                replay = json.loads((folder / f"eval_{key}.json").read_text(encoding="utf-8"))
                assert replay["detected"] == CYCLES and replay["false_closures_per_open_min"] == 0, (key, replay)
            print("OK")
        finally:
            shutil.rmtree(folder, ignore_errors=True)


if __name__ == "__main__":
    main()
