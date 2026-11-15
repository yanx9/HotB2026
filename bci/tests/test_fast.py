"""Fast eye and jaw detectors (fast.py) and the key/physiology check of tune_fast.py."""

from pathlib import Path
import sys
import unittest

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from fast import EYES_DEFAULT, FS, EyesLogic, FastFeatures, JawLogic
from tune_fast import check_keys

DT = 4 / FS


def row(alpha=0.0, dz_fast=0.0, dz_slow=0.0, emg=0.0, fz=0.0, warm=True):
    """(fast alpha, slow alpha, baseline, fast Fz step, slow Fz step, EMG over rest, warm, Fz level)"""
    return alpha, alpha, 0.0, dz_fast, dz_slow, emg, warm, fz


def feed(logic, rows):
    return [logic.step(r, DT) for r in rows]


def seconds(s, **kw):
    return [row(**kw)] * round(s / DT)


class EyesLogicTests(unittest.TestCase):
    def test_lid_step_closes_at_once_and_lid_drop_opens(self):
        eyes = EyesLogic()
        states = feed(eyes, seconds(1, fz=0) + [row(dz_fast=60, fz=60)] + seconds(3, alpha=7, fz=60)
                      + [row(alpha=7, dz_slow=-60, fz=0)] + seconds(1))
        closed_at = states.index(True)
        self.assertEqual(closed_at, round(1 / DT))                 # the very frame of the step
        self.assertFalse(states[closed_at + round(3 / DT) + 1])    # the very frame of the drop
        self.assertTrue(all(states[closed_at:closed_at + round(3 / DT)]))

    def test_blink_and_glance_do_not_last(self):
        eyes = EyesLogic()
        blink = feed(eyes, seconds(1) + [row(dz_fast=60, fz=60)] + seconds(0.2, fz=60) + [row(dz_fast=-60)] + seconds(1))
        self.assertLess(sum(blink) * DT, 0.5)                      # shorter than the game's minimum closure
        eyes = EyesLogic()   # a look upwards: the step is there, but Fz comes back and alpha never rises
        glance = feed(eyes, seconds(1) + [row(dz_fast=60, fz=60)] + seconds(0.5, fz=60) + seconds(4, fz=0))
        self.assertLess(sum(glance) * DT, EYES_DEFAULT["t_check"] + 0.2)

    def test_alpha_alone_closes_slowly_and_its_loss_opens(self):
        eyes = EyesLogic()
        states = feed(eyes, seconds(1) + seconds(4, alpha=8) + seconds(2))
        on = states.index(True) * DT - 1
        self.assertTrue(0.5 < on < 0.8, on)
        off = (len(states) - 1 - states[::-1].index(True)) * DT - 5
        self.assertTrue(0.2 < off < 0.8, off)

    def test_clench_freezes_the_state_and_warmup_blocks_it(self):
        eyes = EyesLogic()
        states = feed(eyes, seconds(1) + [row(dz_fast=60, fz=60)] + seconds(1.5, alpha=7, fz=60)
                      + seconds(1, alpha=7, emg=15, dz_slow=-80) + seconds(0.2, alpha=7, fz=60))
        self.assertTrue(states[-1])    # the drop during the clench did not open the eyes
        self.assertFalse(any(feed(EyesLogic(), [row(dz_fast=60, warm=False)] * 20)))


class JawLogicTests(unittest.TestCase):
    def test_needs_the_dwell_and_releases_below_the_lower_threshold(self):
        jaw = JawLogic({"on_db": 10, "off_db": 4, "t_on": 0.1, "t_off": 0.1})
        self.assertFalse(any(feed(jaw, seconds(0.06, emg=20) + seconds(1))))    # a click shorter than the dwell
        states = feed(jaw, seconds(0.5, emg=20) + seconds(0.5, emg=6) + seconds(0.5, emg=0))
        self.assertTrue(0.08 < states.index(True) * DT < 0.15)
        self.assertTrue(states[round(0.9 / DT)])       # 6 dB is between the thresholds: stays on
        self.assertFalse(states[-1])


class FeatureTests(unittest.TestCase):
    def test_features_follow_alpha_emg_and_the_raw_fz_step(self):
        rng = np.random.default_rng(0)
        n = int(20 * FS)
        t = np.arange(n) / FS
        eeg = rng.normal(0, 3, (n, 8))
        eeg[int(10 * FS):int(14 * FS), 5:8] += 15 * np.sin(2 * np.pi * 10 * t[int(10 * FS):int(14 * FS)])[:, None]
        eeg[int(16 * FS):int(17 * FS)] += rng.normal(0, 40, (int(FS), 8))        # broadband "clench"
        fz = 3000 + rng.normal(0, 2, n)
        fz[int(10 * FS):int(14 * FS)] += 70
        features = FastFeatures([5, 6, 7])
        rows = np.array([features.push(eeg[i:i + 4], fz[i:i + 4]) for i in range(0, n, 4)])
        at = lambda s: rows[round(s * FS / 4)]  # noqa: E731
        self.assertFalse(rows[10][6])
        self.assertTrue(at(5)[6])
        self.assertGreater(at(12)[1] - at(12)[2], 6)             # slow alpha well over its resting level
        self.assertLess(abs(at(8)[1] - at(8)[2]), 3)
        self.assertGreater(rows[round(10 * FS / 4):round(10.3 * FS / 4), 3].max(), 55)     # +70 µV step seen within 0.3 s
        self.assertLess(rows[round(14 * FS / 4):round(14.6 * FS / 4), 4].min(), -55)
        self.assertGreater(at(16.5)[5], 10)
        self.assertLess(abs(at(8)[5]), 4)


class KeyCheckTests(unittest.TestCase):
    def test_keys_that_disagree_with_the_eeg_are_rejected(self):
        rng = np.random.default_rng(1)
        n = int(60 * FS)
        data = {f"flt_{c}": rng.normal(0, 3, n) for c in ("Fz", "C3", "Cz", "C4", "Pz", "PO7", "Oz", "PO8")}
        data["raw_Fz"] = rng.normal(0, 2, n)
        data["marker"] = np.zeros(n)

        def clench(a, b):
            for c in ("Fz", "C3", "Cz", "C4", "Pz", "PO7", "Oz", "PO8"):
                data[f"flt_{c}"][int(a * FS):int(b * FS)] += rng.normal(0, 30, int(b * FS) - int(a * FS))

        def close(a, b):
            t = np.arange(int(a * FS), int(b * FS)) / FS
            for c in ("PO7", "Oz", "PO8"):
                data[f"flt_{c}"][int(a * FS) + 150:int(b * FS)] += 14 * np.sin(2 * np.pi * 10 * t[150:])
            data["raw_Fz"][int(a * FS):int(b * FS)] += 70

        clench(10, 12); clench(20, 21); clench(30, 30.6)           # with key, key held too long, no key
        close(40, 44)                                              # with key; the key at 50 has no closure
        events = [{"name": name, "sample": int(s * FS)} for s, name in (
            (10, "truth_jaw_on"), (12, "truth_jaw_off"), (20, "truth_jaw_on"), (23.5, "truth_jaw_off"),
            (25, "truth_jaw_on"), (26.5, "truth_jaw_off"),
            (40, "truth_closed"), (44, "truth_open"), (50, "truth_closed"), (53, "truth_open"), (56, "truth_closed"), (56.5, "truth_open"))]
        good, rejected, excluded = check_keys(data, events, [5, 6, 7])
        self.assertEqual([round(a / FS) for a, b, _ in good["jaw"]], [10])
        self.assertEqual([round(a / FS) for a, b, _ in good["eyes"]], [40])
        reasons = {(r["signal"], round(r["start_s"])): r["reason"] for r in rejected}
        self.assertIn("nie pokrywa się", reasons[("jaw", 20)])
        self.assertIn("bez zaciśnięcia", reasons[("jaw", 25)])
        self.assertIn("bez klawisza", reasons[("jaw", 30)])
        self.assertIn("bez zamknięcia oczu", reasons[("eyes", 50)])
        self.assertIn("krótsza", reasons[("eyes", 56)])


if __name__ == "__main__":
    unittest.main()
