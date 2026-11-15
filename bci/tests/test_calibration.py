"""Hardware-free regression tests for the actual calibration / inference path.

    python -m unittest discover -s bci/tests -v
"""

import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from calibration_session import CalibrationSession, DemoSource, Protocol
from processing import FeatureConfig, SIGNALS, Window, extract_feature
from profiles import (Detector, ProfileEngine, assert_profile_source, fit_calibration, load_profile,
                      new_profile, save_profile)


def record_simulated(name, no_effect=False):
    """Real EEG features + real session state machine, virtual clock, non-overlapping windows."""
    source = DemoSource()
    session = CalibrationSession(name, source.config, 0, Protocol.for_signal(name))
    now = 0.0
    while not session.completed:
        times = now + np.arange(10) / 250
        state = name if session.phase == "active" and not no_effect else "rest"
        session.ingest(source.samples(times, state), times)
        now += 10 / 250
        session.advance(now)
        if now > 600:
            raise AssertionError("Session did not complete")
    return fit_calibration(name, session.trials, session.snapshot(now)["protocol"])


class CalibrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.models = {name: record_simulated(name) for name in SIGNALS}
        cls.profile = new_profile("test", FeatureConfig(), {"name": "fake", "source_id": "fake-test"}, True)
        cls.profile["calibrations"] = cls.models

    def test_each_condition_has_its_own_validated_model(self):
        for name, result in self.models.items():
            with self.subTest(name=name):
                self.assertEqual(result["status"], "ready", result)
                self.assertEqual(result["usable_trials"], Protocol.for_signal(name).trials)
                self.assertGreaterEqual(result["validation"]["balanced_accuracy"], .75)
                self.assertFalse(set(result["training_trials"]) & set(result["validation_trials"]))

    def test_no_effect_is_not_accepted(self):
        self.assertNotEqual(record_simulated("right_hand", no_effect=True)["status"], "ready")

    def test_good_training_but_failed_holdout_is_not_accepted(self):
        trials = [{"index": i, "rest": [0, .01], "active": [2, 2.01] if i < 7 else [0, .01]}
                  for i in range(10)]
        result = fit_calibration("right_hand", trials, {})
        self.assertEqual(result["status"], "not_separable")
        self.assertEqual(result["validation"]["sensitivity"], 0)

    def test_excessively_rejected_trials_are_not_accepted(self):
        trials = [{"index": i, "rest": [0, .01], "active": [2, 2.01]} for i in range(6)]
        trials += [{"index": i, "rest": [], "active": []} for i in range(6, 20)]
        self.assertEqual(fit_calibration("jaw", trials, {})["status"], "insufficient_data")

    def test_profile_roundtrip_preserves_all_independent_calibrations(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "profile.json"
            p = copy.deepcopy(self.profile)
            save_profile(path, p)
            loaded = load_profile(path)
            self.assertEqual(loaded, p)
            loaded["calibrations"]["jaw"] = record_simulated("jaw")
            save_profile(path, loaded)
            self.assertEqual(load_profile(path)["calibrations"]["left_hand"], p["calibrations"]["left_hand"])
            self.assertEqual(list(Path(tmp).glob("*.tmp")), [])

    def test_corrupt_or_incompatible_profile_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "bad.json"
            for key, value in (("scale", 0), ("threshold_raw", float("nan")), ("window_s", 10)):
                p = copy.deepcopy(self.profile)
                p["calibrations"]["jaw"][key] = value
                path.write_text(json.dumps(p))
                with self.assertRaises(ValueError):
                    load_profile(path)
            p = copy.deepcopy(self.profile)
            p["schema_version"] = 999
            path.write_text(json.dumps(p))
            with self.assertRaises(ValueError):
                load_profile(path)

    def test_live_states_laterality_and_jaw_suppression(self):
        for task in SIGNALS:
            with self.subTest(task=task):
                engine = ProfileEngine(self.profile)
                source = DemoSource()
                window = Window(250, 8)
                t = np.arange(500) / 250
                window.push(source.samples(t, task))
                for now in np.arange(0, 2, .1):
                    result = engine.update(window, now)
                self.assertTrue(result[task]["active"], result)
                if task == "right_hand":
                    self.assertGreater(result["laterality"]["value"], .5)
                    self.assertFalse(result["left_hand"]["active"])
                if task == "left_hand":
                    self.assertLess(result["laterality"]["value"], -.5)
                    self.assertFalse(result["right_hand"]["active"])
                if task == "jaw":
                    self.assertFalse(result["right_hand"]["valid"])
                    self.assertFalse(result["eyes_closed"]["valid"])
                result = engine.update(window, 3, fresh=False)
                self.assertTrue(all(r["value"] == 0 and not r["valid"] and not r["active"] for r in result.values()))

    def test_missing_models_never_fall_back_to_heuristics(self):
        p = copy.deepcopy(self.profile)
        p["calibrations"] = {"jaw": self.models["jaw"]}
        engine = ProfileEngine(p)
        window = Window(250, 8)
        window.push(DemoSource().samples(np.arange(500) / 250, "right_hand"))
        result = engine.update(window, 1)
        self.assertEqual(result["right_hand"]["value"], 0)
        self.assertFalse(result["laterality"]["valid"])

    def test_invalid_and_flat_data_are_rejected(self):
        window = Window(250, 8)
        for data, reason in ((np.zeros((500, 8)), "flat_channel"), (np.full((500, 8), np.nan), "non_finite")):
            window.push(data)
            for name in SIGNALS:
                self.assertEqual(extract_feature(window, name, FeatureConfig()), (None, reason))
        window.push(DemoSource().samples(np.arange(500) / 250))
        window.buffer[-1, 1] = 500
        self.assertEqual(extract_feature(window, "right_hand", FeatureConfig())[1], "amplitude_artifact")

    def test_initial_transition_and_gaps_do_not_pollute_windows(self):
        cfg = FeatureConfig()
        session = CalibrationSession("right_hand", cfg, 0)
        session.advance(5)
        # Old queued samples and transition samples must be ignored.
        t = np.arange(5.5, 6, .004)
        session.ingest(DemoSource().samples(t), t)
        self.assertEqual(session.trials[-1]["rest"], [])
        # A large hole between chunks cannot make a full feature window.
        for start in (6, 8):
            t = start + np.arange(100) / 250
            session.ingest(DemoSource().samples(t), t)
        self.assertEqual(session.trials[-1]["rest"], [])

    def test_delayed_gui_tick_never_skips_instructions(self):
        session = CalibrationSession("jaw", FeatureConfig(), 0)
        session.advance(100)
        self.assertEqual(session.phase, "rest")
        self.assertEqual(session.trial, 1)
        self.assertEqual(session.phase_started, 100)

    def test_cancelled_session_does_not_collect(self):
        session = CalibrationSession("jaw", FeatureConfig(), 0)
        session.advance(5)
        session.cancelled = True
        session.advance(100)
        t = np.arange(5, 9, .004)
        session.ingest(DemoSource().samples(t), t)
        self.assertEqual(session.trials[0]["rest"], [])
        self.assertFalse(session.completed)

    def test_eyes_feature_survives_one_bad_channel_and_electrode_gain(self):
        cfg = FeatureConfig()
        x = DemoSource().samples(np.arange(500) / 250, "eyes_closed")
        window = Window(250, 8)
        window.push(x)
        clean, reason = extract_feature(window, "eyes_closed", cfg)
        self.assertIsNone(reason)
        window.push(x * 3)   # worse contact = larger amplitude; a ratio of powers does not move
        self.assertAlmostEqual(extract_feature(window, "eyes_closed", cfg)[0], clean, places=6)
        loose = x.copy()
        loose[:, cfg.posterior_channels[0]] += 300 * np.sign(np.sin(2 * np.pi * .7 * np.arange(500) / 250))
        window.push(loose)
        value, reason = extract_feature(window, "eyes_closed", cfg)
        self.assertIsNone(reason)          # the other posterior channel carries the window
        self.assertGreater(value, record_simulated("eyes_closed")["rest_center"])

    def test_eyes_detector_holds_its_decision_through_a_short_artifact(self):
        detector = Detector(self.models["eyes_closed"])
        m = detector.model
        for now in np.arange(0, 1.5, .1):
            detector.update(m["active_center"], now)
        self.assertTrue(detector.active)
        # a blink / head movement: windows rejected for less than hold_s -> still closed, still valid
        self.assertEqual(detector.update(None, 1.4 + m["hold_s"] - .1)[1:], (True, True))
        # rejected for longer -> reset, invalid
        self.assertEqual(detector.update(None, 1.4 + m["hold_s"] + .2)[1:], (False, False))

    def test_profile_from_previous_feature_version_is_rejected(self):
        p = copy.deepcopy(self.profile)
        p["feature_version"] = "unicorn-spectral-v1"
        with self.assertRaises(ValueError):
            ProfileEngine(p)

    def test_hysteresis_and_dwell(self):
        detector = Detector(self.models["jaw"])
        m = detector.model
        self.assertFalse(detector.update(m["active_center"], 0)[1])
        for now in np.arange(.1, .6, .1):
            detector.update(m["active_center"], now)
        self.assertTrue(detector.active)
        self.assertFalse(detector.update(None, .7)[1])
        self.assertEqual(detector.value, 0)


class ProfileSourceTests(unittest.TestCase):
    STREAM = {"name": "test", "transport": "gpype", "source_id": "fake-gpype",
              "filters": {"highpass_hz": 1.0, "notch_hz": [48, 52]}}

    def test_synthetic_profile_cannot_be_used_on_real_source(self):
        p = new_profile("demo", FeatureConfig(), self.STREAM, True)
        with self.assertRaises(ValueError):
            assert_profile_source(p, self.STREAM, True)
        assert_profile_source(p, self.STREAM, True, allow_simulated=True)
        with self.assertRaises(ValueError):
            assert_profile_source(p, self.STREAM, False, allow_simulated=True)

    def test_real_profile_requires_same_pipeline(self):
        p = new_profile("person", FeatureConfig(), self.STREAM, False)
        assert_profile_source(p, self.STREAM, False, FeatureConfig())
        with self.assertRaises(ValueError):
            assert_profile_source(p, self.STREAM, True, allow_simulated=True)
        with self.assertRaises(ValueError):   # inny filtr sieciowy
            assert_profile_source(p, {**self.STREAM, "filters": {"highpass_hz": 1.0, "notch_hz": [58, 62]}}, False)
        with self.assertRaises(ValueError):   # inne ustawienia cech
            assert_profile_source(p, self.STREAM, False, FeatureConfig(artifact_uv=500))
        for transport in ("lsl", "gpype_udp"):   # profile sprzed potoku g.Pype
            old = copy.deepcopy(p)
            old["stream"]["transport"] = transport
            with self.assertRaises(ValueError):
                assert_profile_source(old, self.STREAM, False)


if __name__ == "__main__":
    unittest.main()
