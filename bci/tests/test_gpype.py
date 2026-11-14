"""UDPSender wire format and timing failures, without hardware or real sockets."""

import copy
from pathlib import Path
import struct
import sys
import unittest
from unittest.mock import patch

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from gpype_source import GPypeUDPSource
from processing import UNICORN_LABELS
from profiles import assert_profile_source, new_profile


class DatagramSocket:
    def __init__(self):
        self.queue = []

    def bind(self, address):
        self.address = address

    def getsockname(self):
        return self.address

    def setblocking(self, value):
        pass

    def recvfrom(self, size):
        if not self.queue:
            raise BlockingIOError()
        return self.queue.pop(0)

    def close(self):
        pass


class GPypeTests(unittest.TestCase):
    def setUp(self):
        self.now = 100.0
        patcher = patch("gpype_source.socket.socket", side_effect=lambda *a: DatagramSocket())
        patcher.start()
        self.addCleanup(patcher.stop)

    def source(self, **options):
        source = GPypeUDPSource(clock=lambda: self.now, **options)
        self.addCleanup(source.close)
        return source

    def enqueue(self, source, count=1, peer=("127.0.0.1", 45678)):
        # Independent struct encoder checks numpy reshape / byte order against a
        # known sample-major frame, including distinct values in every channel.
        payload = struct.pack("<32d", *range(32))
        source.sock.queue += [(payload, peer)] * count

    def test_sample_major_float64_matches_documented_udpsender(self):
        source = self.source()
        payload = struct.pack("<32d", *range(32))
        actual = source.decode(payload)
        np.testing.assert_array_equal(actual, np.arange(32).reshape(4, 8))
        self.assertEqual(len(payload), 256)
        self.assertEqual(source.stream["transport"], "gpype_udp")

    def test_reordered_channels_big_endian_volts_and_aux_are_separate(self):
        labels = ["aux", "C4", "Fz", "C3", "Cz", "Pz", "PO7", "Oz", "PO8"]
        source = self.source(labels=labels, units="v", byte_order="big")
        x = np.full((4, 9), 2e-6)
        x[:, 0] = 999
        actual = source.decode(x.astype(">f8").tobytes())
        np.testing.assert_allclose(actual[:, source.config.eeg_channels], 2)
        np.testing.assert_array_equal(actual[:, 0], 999)
        self.assertEqual((source.config.c3, source.config.c4), (3, 1))

    def test_bad_shape_float32_nonfinite_and_wrong_frame_are_rejected(self):
        source = self.source()
        for payload in (b"", b"json", bytes(255), bytes(512),
                        np.zeros((4, 8), dtype="<f4").tobytes(),
                        np.full((4, 8), np.nan, dtype="<f8").tobytes()):
            with self.subTest(length=len(payload)), self.assertRaises(ValueError):
                source.decode(payload)
        with self.assertRaises(ValueError):
            self.source(labels=["C3"] * 8)
        with self.assertRaises(ValueError):
            self.source(units="auto")

    def test_variable_frame_accepts_small_frames_but_not_large_backlog(self):
        source = self.source(frame_samples=0)
        self.assertEqual(source.decode(bytes(8 * 8)).shape, (1, 8))
        with self.assertRaises(ValueError):
            source.decode(bytes(63 * 8 * 8))

    def test_uniform_timing_tolerates_jitter_and_becomes_stale(self):
        source = self.source()
        self.enqueue(source)
        _, first = source.read()
        np.testing.assert_allclose(first, [99.988, 99.992, 99.996, 100])
        self.assertTrue(source.gap)
        self.now += .019  # three milliseconds of arrival jitter
        self.enqueue(source)
        _, second = source.read()
        np.testing.assert_allclose(np.diff(np.r_[first, second]), .004)
        self.assertFalse(source.gap)
        self.assertTrue(source.fresh())
        self.now += .51
        self.assertFalse(source.fresh())

    def test_invalid_packet_discards_preceding_data_and_resets_window(self):
        source = self.source()
        self.enqueue(source)
        source.sock.queue.append((b"invalid", ("127.0.0.1", 45678)))
        self.enqueue(source)
        data, _ = source.read()
        self.assertEqual(len(data), 4)
        self.assertTrue(source.gap)
        self.assertEqual(source.invalid_packets, 1)

    def test_reader_pause_and_queued_data_cannot_masquerade_as_fresh(self):
        source = self.source()
        self.enqueue(source)
        source.read()
        self.now += .4
        self.enqueue(source)
        data, _ = source.read()
        self.assertEqual(len(data), 0)
        self.assertTrue(source.gap)
        self.assertFalse(source.fresh())
        self.now += .016
        self.enqueue(source)
        self.assertEqual(len(source.read()[0]), 4)
        self.assertTrue(source.fresh())
        self.now += .016
        self.enqueue(source, count=16)
        self.assertEqual(len(source.read()[0]), 0)
        self.assertFalse(source.fresh())

    def test_long_arrival_gap_reanchors_and_rejects_second_publisher(self):
        source = self.source()
        self.enqueue(source)
        source.read()
        for _ in range(20):
            self.now += .01
            source.read()
        self.enqueue(source)
        _, stamps = source.read()
        self.assertTrue(source.gap)
        self.assertAlmostEqual(stamps[-1], self.now)
        self.enqueue(source, peer=("127.0.0.1", 45679))
        self.assertEqual(len(source.read()[0]), 0)
        self.assertEqual(source.invalid_packets, 1)

    def test_profile_restores_format_and_rejects_changed_acquisition(self):
        source = self.source()
        profile = new_profile("test", source.config, source.stream, False)
        assert_profile_source(profile, source)
        # Moving the endpoint to another port doesn't change the EEG model.
        assert_profile_source(profile, self.source(port=57000))
        for changes in ({"units": "v"}, {"frame_samples": 8}, {"byte_order": "big"},
                        {"labels": list(reversed(UNICORN_LABELS))}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                assert_profile_source(profile, self.source(**changes))
        old_lsl = copy.deepcopy(profile)
        old_lsl["stream"] = {"name": "Unicorn LSL"}
        with self.assertRaises(ValueError):
            assert_profile_source(old_lsl, source)


if __name__ == "__main__":
    unittest.main()
