"""Qt smoke + controller tests; skipped when optional Qt dependencies are absent."""

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


if __name__ == "__main__":
    unittest.main()
