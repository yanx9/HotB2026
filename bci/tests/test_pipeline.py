"""g.Pype nodes of the game pipeline; skipped when g.Pype is not installed.

Node logic is driven through step() directly; one short test runs a real pipeline
with the synthetic source. The full path is covered by tests/integration_pipeline.py.
"""

import json
from pathlib import Path
import socket
import sys
import time
import unittest

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

try:
    from gpype_nodes import (PORT_IN, PORT_OUT, PipelineSource, SignalClassifier, SyntheticUnicorn,
                             UdpJsonSender, stream_info)
    GPYPE_AVAILABLE = True
except ImportError:
    GPYPE_AVAILABLE = False

from calibration_session import DemoSource
from processing import FeatureConfig, SIGNALS
from profiles import new_profile
from test_calibration import record_simulated


def context(channels=8, rate=250, units="uV"):
    return {PORT_IN: {"sampling_rate": rate, "channel_count": channels, "frame_size": 4, "timing": "Sync",
                      "type": "Any", "channel_units": [units] * channels}}


def feed(node, state, seconds, demo, start):
    """Push synthetic EEG through a node frame by frame; returns the last output row by name."""
    out = None
    for i in range(int(seconds * 250 / 4)):
        t = start + (i * 4 + np.arange(4)) / 250
        out = node.step({PORT_IN: demo.samples(t, state).astype(np.float32)})[PORT_OUT]
    return dict(zip(node.names, out[-1])), start + seconds


@unittest.skipUnless(GPYPE_AVAILABLE, "g.Pype required: pip install -r bci/requirements.txt")
class PipelineTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.profile = new_profile("test", FeatureConfig(), stream_info(fake=True), True)
        cls.profile["calibrations"] = {name: record_simulated(name) for name in SIGNALS}

    def test_classifier_with_profile_follows_each_task(self):
        node = SignalClassifier(self.profile)
        out = node.setup({}, context())[PORT_OUT]
        self.assertEqual(out["channel_count"], len(node.names))
        self.assertEqual(out["channel_labels"], node.names)
        demo, t = DemoSource(), 0.0
        rest, t = feed(node, "rest", 4, demo, t)
        for name in SIGNALS:
            self.assertEqual(rest[name + "_valid"], 1, name)
            self.assertEqual(rest[name + "_active"], 0, name)
        self.assertEqual(rest["laterality_valid"], 1)
        for name, check in (("eyes_closed", lambda v: v["eyes_closed_active"] == 1),
                            ("right_hand", lambda v: v["right_hand_active"] == 1 and v["laterality"] > .5),
                            ("left_hand", lambda v: v["left_hand_active"] == 1 and v["laterality"] < -.5)):
            active, t = feed(node, name, 4, demo, t)
            self.assertTrue(check(active), (name, active))
            self.assertEqual(active["jaw_active"], 0)
            _, t = feed(node, "rest", 3, demo, t)
        clench, t = feed(node, "jaw", 1.0, demo, t)
        self.assertEqual(clench["jaw_active"], 1)
        # a short clench does not flip the eyes decision: it is held (eyes hold_s = 1.5 s)
        self.assertEqual((clench["eyes_closed_valid"], clench["eyes_closed_active"]), (1, 0))
        clench, t = feed(node, "jaw", 2.0, demo, t)
        # a clench gates the EEG-based signals instead of rewarding muscle activity
        for name in ("eyes_closed", "right_hand", "left_hand", "laterality", "focus"):
            self.assertEqual(clench[name + "_valid"], 0, name)

    def test_classifier_without_profile_sends_heuristics(self):
        node = SignalClassifier()
        node.setup({}, context())
        self.assertEqual(node.names, ["laterality", "laterality_valid", "jaw", "jaw_active", "jaw_valid", "focus", "focus_valid"])
        demo = DemoSource()
        rest, t = feed(node, "rest", 6, demo, 0.0)
        self.assertEqual((rest["jaw_valid"], rest["laterality_valid"], rest["focus_valid"]), (1, 1, 1))
        self.assertLess(rest["jaw"], .2)
        clench, t = feed(node, "jaw", 1, demo, t)
        self.assertGreater(clench["jaw"], .8)
        self.assertEqual((rest["jaw_active"], clench["jaw_active"]), (0, 1))

    def test_profile_without_jaw_calibration_still_reports_clench(self):
        profile = dict(self.profile, calibrations={k: v for k, v in self.profile["calibrations"].items() if k != "jaw"})
        node = SignalClassifier(profile)
        node.setup({}, context())
        demo = DemoSource()
        rest, t = feed(node, "rest", 6, demo, 0.0)
        self.assertEqual((rest["jaw_active"], rest["jaw_valid"], rest["eyes_closed_valid"]), (0, 1, 1))
        clench, t = feed(node, "jaw", 1, demo, t)
        self.assertEqual((clench["jaw_active"], clench["jaw_valid"]), (1, 1))
        # the uncalibrated clench is a button for games; it does not gate the calibrated eye detector
        self.assertEqual(clench["eyes_closed_valid"], 1)
        rest, t = feed(node, "rest", 2, demo, t)
        self.assertEqual(rest["jaw_active"], 0)

    def test_classifier_refuses_wrong_layout_and_converts_volts(self):
        with self.assertRaises(ValueError):
            SignalClassifier().setup({}, context(rate=500))
        with self.assertRaises(ValueError):
            SignalClassifier().setup({}, context(channels=4))
        node = SignalClassifier(self.profile)
        node.setup({}, context(units="V"))
        demo, out = DemoSource(), None
        for i in range(500):
            t = (i * 4 + np.arange(4)) / 250
            out = node.step({PORT_IN: demo.samples(t, "eyes_closed") * 1e-6})[PORT_OUT]
        self.assertEqual(dict(zip(node.names, out[-1]))["eyes_closed_active"], 1)

    def test_data_gap_restarts_analysis_windows(self):
        node = SignalClassifier(self.profile)
        node.setup({}, context())
        values, t = feed(node, "rest", 3, DemoSource(), 0.0)
        self.assertEqual(values["eyes_closed_valid"], 1)
        node._last_frame -= 1.0   # as if no frame had arrived for a second
        values, _ = feed(node, "rest", 0.5, DemoSource(), t)
        self.assertEqual(values["eyes_closed_valid"], 0)

    def test_diagnosis_names_the_channel_that_breaks_the_signals(self):
        node = SignalClassifier(self.profile)
        node.setup({}, context())
        demo = DemoSource()

        def run(loose, start):
            out = None
            for i in range(250):   # 4 s, loose electrodes swinging by 600 µV
                t = start + (i * 4 + np.arange(4)) / 250
                x = demo.samples(t, "rest")
                x[:, loose] += 300 * np.sign(np.sin(2 * np.pi * 0.7 * t))[:, None]
                out = node.step({PORT_IN: x.astype(np.float32)})[PORT_OUT]
            return dict(zip(node.names, out[-1]))

        # one loose posterior electrode: eyes carry on from the other one
        values = run([5], 0.0)
        self.assertEqual(values["eyes_closed_valid"], 1)
        text = node.diagnose()
        self.assertRegex(text, r"PO7 \d+!")
        self.assertNotRegex(text, r"Fz \d+!")
        self.assertIn("okno 2.0/2 s", text)
        # both loose: no usable channel, and after hold_s the signal is reported invalid
        values = run([5, 7], 4.0)
        self.assertEqual(values["eyes_closed_valid"], 0)
        self.assertIn("eyes_closed: artefakt amplitudy", node.diagnose())

    def test_sender_emits_protocol_messages_and_commands(self):
        receiver = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        receiver.bind(("127.0.0.1", 0))
        receiver.settimeout(1)
        names = ["right_hand", "right_hand_active", "jaw"]
        sender = UdpJsonSender(names, [receiver.getsockname()], {"right_hand_active": ("right", "right_hand")})
        sender._every = 25
        sender._socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            for _ in range(12):   # 48 samples at 250 Hz: updates at samples 0 and 25
                sender.step({PORT_IN: np.tile(np.float32([0.8, 1, float("nan")]), (4, 1))})
            messages = [json.loads(receiver.recvfrom(4096)[0]) for _ in range(sender.sent)]
        finally:
            sender._socket.close()
            receiver.close()
        self.assertEqual([(m["type"], m["name"]) for m in messages],
                         [("metric", "right_hand"), ("metric", "right_hand_active"), ("command", "right"),
                          ("metric", "right_hand"), ("metric", "right_hand_active")])   # NaN is never sent
        self.assertEqual(messages[0]["value"], 0.8)
        self.assertEqual(set(messages[0]), {"type", "name", "value", "t"})
        self.assertLess(abs(messages[0]["t"] - time.time()), 5)

    def test_real_pipeline_delivers_filtered_samples_on_a_uniform_grid(self):
        source = PipelineSource(fake=True)
        chunks, stamps = [], []
        try:
            deadline = time.monotonic() + 4
            while time.monotonic() < deadline:
                x, t = source.read()
                chunks.append(x)
                stamps.append(t)
                time.sleep(.01)
            self.assertTrue(source.fresh())
        finally:
            source.close()
        x, t = np.concatenate(chunks), np.concatenate(stamps)
        self.assertEqual(x.shape[1], 8)
        self.assertTrue(900 <= len(x) <= 1040, len(x))   # 250 Hz for 4 s
        self.assertTrue(np.all(np.diff(t) > 0))
        self.assertLessEqual(source.resets, 2)
        # the synthetic source adds 8 µV of mains hum and a slow drift; the filters remove both
        tail = x[-500:, 0]
        spectrum = np.abs(np.fft.rfft(tail * np.hanning(500))) / 125
        freqs = np.fft.rfftfreq(500, 1 / 250)
        self.assertLess(spectrum[freqs == 50][0], 2)
        self.assertGreater(spectrum[freqs == 10][0], 3)   # the EEG rhythm itself is kept
        self.assertLess(abs(tail.mean()), 3)

    def test_recording_puts_markers_on_eeg_samples(self):
        import gpype as gp
        import tempfile
        from gpype_nodes import build_front
        from recording import Recorder, load_recording
        with tempfile.TemporaryDirectory() as tmp:
            pipeline = gp.Pipeline()
            source, filtered = build_front(pipeline, fake=True)
            node = SignalClassifier(self.profile)
            pipeline.connect(filtered, node)
            recorder = Recorder("test", folder=tmp)
            recorder.attach(pipeline, source, filtered, node, node.names)
            pipeline.start()
            try:
                time.sleep(1.5)
                recorder.mark("truth_closed")
                recorder.mark("eyes_closed")          # the same instant: must not overwrite
                time.sleep(1.0)
                recorder.mark("truth_open", 1.0)
                recorder.mark("nowy_znacznik", 7)      # unknown name gets its own code
                time.sleep(0.5)
            finally:
                pipeline.stop()
                pipeline.close()
                recorder.close()
            data, events, meta = load_recording(tmp)
            del pipeline, source, filtered, node   # release the HDF5 file before the folder goes
        self.assertEqual(meta["columns"][:3], ["raw_Fz", "raw_C3", "raw_Cz"])
        self.assertIn("out_eyes_closed_active", data)
        self.assertEqual([e["name"] for e in events], ["truth_closed", "eyes_closed", "truth_open", "nowy_znacznik"])
        samples = [e["sample"] for e in events]
        self.assertTrue(all(s is not None for s in samples), samples)
        self.assertGreaterEqual(samples[1] - samples[0], 1)                 # pushed apart, both kept
        self.assertAlmostEqual((samples[2] - samples[0]) / 250, 1.0, delta=0.05)
        self.assertGreaterEqual(meta["marker_codes"]["nowy_znacznik"], 100)
        self.assertEqual(events[3]["value"], 7)
        # raw keeps the synthetic drift and mains hum, flt is what the classifier saw
        self.assertGreater(np.ptp(data["raw_Fz"]), np.ptp(data["flt_Fz"][250:]))

    def test_synthetic_source_cycles_through_tasks(self):
        node = SyntheticUnicorn()
        seen = []
        for second in range(27):
            node._sent = second * 250
            seen.append(node.current_state())
        self.assertEqual(set(seen), {"rest", *SIGNALS})
        node.state = "jaw"
        self.assertEqual(node.current_state(), "jaw")


if __name__ == "__main__":
    unittest.main()
