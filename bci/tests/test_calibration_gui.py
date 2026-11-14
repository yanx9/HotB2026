"""Qt smoke + controller tests; skipped when optional Qt dependencies are absent."""

import os
from pathlib import Path
import select
import socket
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

try:
    from PySide6.QtWidgets import QApplication
    from PySide6.QtTest import QTest
    from calibrate import CalibrationApp, Worker, parser
    QT_AVAILABLE = True
except (ImportError, SystemExit):
    QT_AVAILABLE = False

from processing import SIGNALS
from profiles import load_profile
from calibration_markers import marker_code
from calibration_session import DemoSource
import numpy as np


@unittest.skipUnless(QT_AVAILABLE, "Optional compatible Qt installation required")
class CalibrationGuiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])

    def wait_until(self, predicate):
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            self.app.processEvents()
            if predicate():
                return
            # Release the GIL so the Python acquisition thread can execute.
            time.sleep(.01)
        self.fail("Timed out waiting for GUI / acquisition thread")

    def test_four_separate_views_connect_and_cancel_without_saving(self):
        with tempfile.TemporaryDirectory() as tmp:
            args = parser().parse_args(["--demo", "--profile", str(Path(tmp) / "demo.json")])
            window = CalibrationApp(args)
            window.show()
            try:
                self.assertEqual(window.stack.count(), 4)
                self.assertEqual(window.source_kind.currentData(), "gpype")
                self.assertFalse(window.udp_panel.isHidden())
                self.assertTrue(window.scan_button.isHidden())
                self.assertEqual(window.units.currentData(), "uv")
                for i, button in enumerate(window.nav_buttons):
                    button.click()
                    self.assertEqual(window.stack.currentIndex(), i)
                window.select_view(0)
                window.connect_button.click()
                self.wait_until(lambda: window.connected_flag)
                self.assertTrue(window.connected_flag, window.status.text())
                self.assertFalse(window.views["eyes_closed"].start_button.isEnabled())
                window.audio_confirm.setChecked(True)
                self.assertTrue(window.views["eyes_closed"].start_button.isEnabled())
                window.views["eyes_closed"].start_button.click()
                self.wait_until(lambda: window.views["eyes_closed"].prompt.text() == "PRZYGOTUJ SIĘ")
                self.assertTrue(window.recording)
                self.assertTrue(all(not b.isEnabled() for b in window.nav_buttons))
                window.views["eyes_closed"].stop_button.click()
                self.wait_until(lambda: not window.recording)
                self.assertFalse(window.recording)
                self.assertFalse(args.profile.exists())
                window.grab().save(str(Path(tempfile.gettempdir()) / "hotb_calibration_ui.png"))
            finally:
                window.close()
                QTest.qWait(50)

    def test_worker_saves_four_results_and_reopens_profile(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "profile.json"
            worker = Worker()
            errors, results = [], []
            worker.error.connect(errors.append)
            worker.result.connect(lambda name, model: results.append((name, model)))
            options = {"demo": True, "name": "", "subject": "test", "path": str(path),
                       "units": "auto", "assume_order": False, "artifact_uv": 200, "jaw_band": [55, 95]}
            clock = [0.0]
            with patch("calibrate.local_clock", side_effect=lambda: clock[0]):
                try:
                    worker.connect_source(options)
                    for name in SIGNALS:
                        worker.start_calibration(name, 6)
                        for _ in range(10000):
                            clock[0] += 0.04
                            worker.tick()
                            if not worker.recording:
                                break
                        self.assertFalse(worker.recording)
                        self.assertEqual(errors, [])
                        self.assertTrue(path.exists())
                        self.assertEqual(load_profile(path)["calibrations"][name]["status"], "ready")
                    self.assertEqual(set(load_profile(path)["calibrations"]), set(SIGNALS))
                    worker.disconnect_source()
                    worker.connect_source(options)
                    self.assertEqual(set(worker.engine.detectors), set(SIGNALS))
                    self.assertEqual(errors, [])
                finally:
                    worker.disconnect_source()

    def test_standard_udp_calibrates_four_views_and_emits_numeric_triggers(self):
        """Real sockets / wire / controller / fitting; virtual time avoids a 6-minute test."""
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "udp.json"
            sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            triggers = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            triggers.bind(("127.0.0.1", 0))
            triggers.setblocking(False)
            worker = Worker()
            errors, codes = [], set()
            worker.error.connect(errors.append)
            clock = [100.0]
            samples = DemoSource()
            options = {"demo": False, "source": "gpype", "subject": "udp-test", "path": str(path),
                       "units": "uv", "artifact_uv": 200, "jaw_band": [55, 95],
                       "udp": {"port": 0, "clock": lambda: clock[0]}, "synthetic_input": True,
                       "trigger_port": triggers.getsockname()[1]}

            def tick():
                clock[0] += .016
                state = worker.session.name if worker.recording and worker.session.phase == "active" else "rest"
                frame = samples.samples(clock[0] - np.arange(3, -1, -1) / 250, state)
                sender.sendto(frame.astype("<f8").tobytes(), worker.source.sock.getsockname())
                self.assertTrue(select.select([worker.source.sock], [], [], 1)[0])
                worker.tick()
                if worker.session and worker.session.completed:
                    select.select([triggers], [], [], .1)
                while True:
                    try:
                        codes.add(int(triggers.recvfrom(128)[0].decode("ascii")))
                    except BlockingIOError:
                        break

            with patch("calibrate.local_clock", side_effect=lambda: clock[0]):
                try:
                    worker.connect_source(options)
                    self.assertEqual(errors, [])
                    self.assertFalse(worker.source.fresh())
                    tick()
                    for name in SIGNALS:
                        worker.start_calibration(name, 6)
                        self.assertTrue(worker.recording, (name, errors, worker.source.diagnostics()))
                        for _ in range(10000):
                            tick()
                            if not worker.recording:
                                break
                        self.assertFalse(worker.recording)
                        self.assertEqual(errors, [])
                        self.assertEqual(load_profile(path)["calibrations"][name]["status"], "ready")
                        self.assertTrue({marker_code(name, phase) for phase in ("prepare", "rest", "active", "complete")} <= codes)
                    profile = load_profile(path)
                    self.assertEqual(set(profile["calibrations"]), set(SIGNALS))
                    self.assertEqual(profile["stream"]["transport"], "gpype_udp")
                    self.assertTrue(profile["simulated"])
                    # A broken stream cancels a new run without altering the saved results.
                    before = path.read_bytes()
                    worker.start_calibration("jaw", 6)
                    clock[0] += 2.1
                    worker.tick()
                    self.assertFalse(worker.recording)
                    self.assertIn("Utrata danych EEG", errors[-1])
                    self.assertEqual(path.read_bytes(), before)
                finally:
                    worker.disconnect_source()
                    sender.close()
                    triggers.close()


if __name__ == "__main__":
    unittest.main()
