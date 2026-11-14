"""Qt smoke + controller tests; skipped when Qt (gpype[gui]) is not installed."""

import os
from pathlib import Path
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
from calibration_session import DemoSource
import numpy as np


class VirtualSource:
    """Stands in for gpype_nodes.PipelineSource: same interface, samples on the test's clock."""

    clock = None

    def __init__(self, fake=False, serial=None, mains_hz=50, artifact_uv=200, jaw_band=(55, 95)):
        from processing import FeatureConfig
        self.config = FeatureConfig(artifact_uv=artifact_uv, jaw_band=list(jaw_band)).validate()
        self.stream = {"name": "virtual", "transport": "gpype", "source_id": "fake-gpype",
                       "filters": {"highpass_hz": 1.0, "notch_hz": [mains_hz - 2, mains_hz + 2]}}
        self.simulated, self.gap, self.stalled = True, False, False
        self.demo, self.state, self.sent, self.last = DemoSource(), "rest", self.clock(), None

    def set_state(self, state):
        self.state = state

    def read(self):
        if self.stalled:
            return np.empty((0, 8)), np.array([])
        now = self.clock()
        count = int(round((now - self.sent) * 250))
        stamps = self.sent + (1 + np.arange(count)) / 250
        self.sent += count / 250
        self.last = now
        return self.demo.samples(stamps, self.state), stamps

    def fresh(self):
        return self.last is not None and self.clock() - self.last < .5

    def diagnostics(self):
        return {"frames": 0, "timing_resets": 0}

    def close(self):
        pass


@unittest.skipUnless(QT_AVAILABLE, "Qt required: pip install -r bci/requirements.txt")
class CalibrationGuiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])

    def wait_until(self, predicate):
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            self.app.processEvents()
            if predicate():
                return
            # Release the GIL so the Python acquisition thread can execute.
            time.sleep(.01)
        self.fail("Timed out waiting for GUI / acquisition thread")

    def test_four_separate_views_connect_and_cancel_without_saving(self):
        with tempfile.TemporaryDirectory() as tmp:
            args = parser().parse_args(["--fake", "--profile", str(Path(tmp) / "fake.json")])
            window = CalibrationApp(args)
            window.show()
            try:
                self.assertEqual(window.stack.count(), 4)
                self.assertTrue(window.fake.isChecked())
                self.assertEqual(window.mains.currentData(), 50)
                for i, button in enumerate(window.nav_buttons):
                    button.click()
                    self.assertEqual(window.stack.currentIndex(), i)
                window.select_view(0)
                window.connect_button.click()
                self.wait_until(lambda: window.connected_flag)
                self.assertTrue(window.connected_flag, window.status.text())
                self.assertFalse(window.views["eyes_closed"].start_button.isEnabled())
                # the real g.Pype pipeline with the synthetic source must deliver samples
                self.wait_until(lambda: "dane aktualne" in window.status.text())
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
        """Controller, session, fitting and profile writes on a virtual clock (a real run takes minutes)."""
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "profile.json"
            worker = Worker()
            errors = []
            worker.error.connect(errors.append)
            options = {"fake": True, "serial": "", "mains_hz": 50, "subject": "test", "path": str(path),
                       "artifact_uv": 200, "jaw_band": [55, 95]}
            clock = [100.0]
            VirtualSource.clock = staticmethod(lambda: clock[0])

            def tick():
                clock[0] += 0.04
                worker.tick()

            with patch("calibrate.local_clock", side_effect=lambda: clock[0]), \
                    patch("gpype_nodes.PipelineSource", VirtualSource):
                try:
                    worker.connect_source(options)
                    self.assertEqual(errors, [])
                    worker.start_calibration("jaw", 6)
                    self.assertFalse(worker.recording)   # no samples yet: refuse to start
                    self.assertIn("Brak świeżych próbek", errors.pop())
                    tick()
                    for name in SIGNALS:
                        worker.start_calibration(name, 6)
                        self.assertTrue(worker.recording, errors)
                        for _ in range(10000):
                            tick()
                            if not worker.recording:
                                break
                        self.assertFalse(worker.recording)
                        self.assertEqual(errors, [])
                        self.assertEqual(load_profile(path)["calibrations"][name]["status"], "ready")
                    profile = load_profile(path)
                    self.assertEqual(set(profile["calibrations"]), set(SIGNALS))
                    self.assertEqual(profile["stream"]["transport"], "gpype")
                    self.assertTrue(profile["simulated"])
                    worker.disconnect_source()
                    worker.connect_source(options)
                    self.assertEqual(set(worker.engine.detectors), set(SIGNALS))
                    self.assertEqual(errors, [])
                    # A broken stream cancels a new run without altering the saved results.
                    tick()
                    before = path.read_bytes()
                    worker.start_calibration("jaw", 6)
                    self.assertTrue(worker.recording)
                    worker.source.stalled = True
                    clock[0] += 2.1
                    worker.tick()
                    self.assertFalse(worker.recording)
                    self.assertIn("Utrata danych EEG", errors[-1])
                    self.assertEqual(path.read_bytes(), before)
                finally:
                    worker.disconnect_source()


if __name__ == "__main__":
    unittest.main()
