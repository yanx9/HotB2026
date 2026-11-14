"""Optional calibration event outputs; g.Pype mode never imports pylsl."""

import socket
import time

from processing import SIGNALS

PHASE_CODES = {"prepare": 0, "rest": 1, "active": 2, "complete": 3, "cancelled": 4}


def marker_code(signal, phase):
    return 100 + 10 * SIGNALS.index(signal) + PHASE_CODES[phase]


class GPypeCalibrationMarkers:
    """One numeric ASCII trigger per datagram, accepted by gp.UDPReceiver."""

    def __init__(self, host="127.0.0.1", port=56001):
        self.address = (host, port)
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    def emit(self, signal, phase, trial, timestamp):
        self.socket.sendto(str(marker_code(signal, phase)).encode("ascii"), self.address)

    def close(self):
        self.socket.close()


class LSLCalibrationMarkers:
    def __init__(self):
        from pylsl import IRREGULAR_RATE, StreamInfo, StreamOutlet
        self.outlet = StreamOutlet(StreamInfo("BciCalibrationMarkers", "Markers", 1,
                                              IRREGULAR_RATE, "string", "bci-calibration-markers"))

    def emit(self, signal, phase, trial, timestamp):
        from pylsl import local_clock
        self.outlet.push_sample([f"calibration/{signal}/{phase}:{trial}"],
                               timestamp + local_clock() - time.monotonic())

    def close(self):
        self.outlet = None
