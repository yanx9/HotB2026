"""Receive standard g.Pype UDPSender frames, with no LSL dependency.

Wire: float64, C-order (samples x channels), no header or timestamps.
Layout, units and sample rate MUST match the sending pipeline. Timing is a
receiver-side estimate, not the acquisition clock. See docs/calibration.md.
"""

import socket
import time

import numpy as np

from processing import FeatureConfig, UNICORN_LABELS

DEFAULT_LABELS = ",".join(UNICORN_LABELS)


class GPypeUDPSource:
    def __init__(self, host="127.0.0.1", port=56000, sampling_rate=250,
                 labels=DEFAULT_LABELS, units="uv", byte_order="little", frame_samples=4,
                 artifact_uv=200, jaw_band=(55, 95), simulated=False, clock=None):
        self.clock = clock or time.monotonic
        if isinstance(labels, str):
            labels = [v.strip() for v in labels.split(",")]
        normalized = [label.strip().lower() for label in labels]
        expected = [label.lower() for label in UNICORN_LABELS]
        if not 8 <= len(labels) <= 64 or not all(normalized.count(label) == 1 for label in expected):
            raise ValueError("Podaj kolejność wszystkich kanałów UDP, w tym jednoznaczne Fz,C3,Cz,C4,Pz,PO7,Oz,PO8.")
        if units not in ("uv", "v") or byte_order not in ("little", "big"):
            raise ValueError("UDP nie zawiera metadanych: wybierz jednostki uv/v i kolejność bajtów little/big.")
        if type(frame_samples) is not int or frame_samples < 0 or frame_samples > sampling_rate * 0.25:
            raise ValueError("Pakiet musi mieścić najwyżej 250 ms danych; 0 oznacza zmienną liczbę próbek.")
        if not 0 <= port <= 65535:
            raise ValueError("Port poza zakresem.")
        mapping = [normalized.index(label) for label in expected]
        self.config = FeatureConfig(sampling_rate=float(sampling_rate), channel_count=len(labels),
                                    eeg_channels=mapping, posterior_channels=mapping[4:],
                                    c3=mapping[1], c4=mapping[3], input_scale_uv=1e6 if units == "v" else 1,
                                    channel_labels=list(labels), artifact_uv=artifact_uv,
                                    jaw_band=list(jaw_band)).validate()
        self.dtype = np.dtype("<f8" if byte_order == "little" else ">f8")
        self.frame_samples = frame_samples
        self.simulated = simulated
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            # No SO_REUSEADDR: two programs must never silently split the EEG packets.
            self.sock.bind((host, port))
            self.sock.setblocking(False)
        except OSError as exc:
            self.sock.close()
            raise OSError(f"Nie można otworzyć wejścia g.Pype {host}:{port}: {exc}. "
                          "Zamknij drugi odbiornik tego portu (kalibrator lub most).") from exc
        bound_port = self.sock.getsockname()[1]
        self.stream = {"name": f"g.Pype UDP {host}:{bound_port}", "transport": "gpype_udp",
                       "source_id": "fake-gpype" if simulated else "gpype-udp",
                       "timing": "receiver_estimate_no_acquisition_timestamps",
                       "input": {"host": host, "port": bound_port, "sampling_rate": sampling_rate,
                                 "labels": list(labels), "units": units, "byte_order": byte_order,
                                 "frame_samples": frame_samples},
                       "units_option": units}
        self.last_timestamp = self.last_received = self.last_poll = None
        self.peer = None
        self.gap = False
        self.packet_count = self.invalid_packets = self.resets = 0

    def decode(self, payload):
        stride = 8 * self.config.channel_count
        if not payload or len(payload) % stride:
            raise ValueError("Rozmiar pakietu nie pasuje do float64 × liczba kanałów.")
        rows = len(payload) // stride
        if rows > self.config.sampling_rate * .25 or (self.frame_samples and rows != self.frame_samples):
            raise ValueError("Liczba próbek w pakiecie nie pasuje do zadeklarowanej ramki.")
        x = np.frombuffer(payload, dtype=self.dtype).reshape(rows, self.config.channel_count).astype(float)
        x[:, self.config.eeg_channels] *= self.config.input_scale_uv
        if not np.isfinite(x[:, self.config.eeg_channels]).all():
            raise ValueError("Pakiet zawiera NaN / Inf w EEG.")
        return x

    def read(self):
        now = self.clock()
        self.gap = False
        # A delayed reader cannot tell fresh data from old data queued in the socket.
        stalled = self.last_poll is not None and now - self.last_poll > .25
        self.last_poll = now
        frames = []
        total = 0
        for _ in range(512):
            try:
                payload, peer = self.sock.recvfrom(65536)
            except BlockingIOError:
                break
            if self.peer is not None and peer != self.peer:
                # Only one publisher per input. Reconnect to accept a changed sender.
                self.invalid_packets += 1
                continue
            try:
                frame = self.decode(payload)
            except ValueError:
                self.invalid_packets += 1
                self.gap = True
                frames.clear()
                total = 0
                continue
            self.peer = peer
            self.packet_count += 1
            frames.append(frame)
            total += len(frame)
        else:
            stalled = True  # bounded processing; never play an unbounded backlog
        if total > self.config.sampling_rate * .25:
            stalled = True
        if stalled:
            # Discard queued samples rather than stamp historical data as current.
            self.gap = True
            frames.clear()
        if not frames:
            if self.gap:
                self.last_timestamp = self.last_received = None
                self.resets += 1
            return np.empty((0, self.config.channel_count)), np.array([])
        x = np.concatenate(frames)
        fs = self.config.sampling_rate
        # Uniform sample grid; tolerate delivery jitter, re-anchor after a gap or drift.
        predicted_end = None if self.last_timestamp is None else self.last_timestamp + len(x) / fs
        if (self.gap or predicted_end is None or self.last_received is None
                or now - self.last_received > .15 or abs(predicted_end - now) > .1):
            self.gap = True
            end = now
            self.resets += 1
        else:
            end = predicted_end
        stamps = end - np.arange(len(x) - 1, -1, -1) / fs
        self.last_timestamp, self.last_received = float(stamps[-1]), now
        return x, stamps

    def fresh(self):
        return (self.last_received is not None and self.clock() - self.last_received < .5
                and self.last_timestamp is not None and -.15 <= self.clock() - self.last_timestamp < .5)

    def diagnostics(self):
        return {"packets": self.packet_count, "invalid_packets": self.invalid_packets,
                "timing_resets": self.resets, "peer": str(self.peer) if self.peer else None}

    def close(self):
        self.sock.close()


def add_udp_arguments(parser, defaults=True):
    """Common CLI for the calibrator and generic bridge; no per-game options."""
    def default(value):
        return value if defaults else None
    parser.add_argument("--udp-host", default=default("127.0.0.1"), help="lokalny adres odbiornika EEG, nie adres gry")
    parser.add_argument("--udp-port", type=int, default=default(56000), help="port wejścia UDPSender g.Pype")
    parser.add_argument("--sampling-rate", type=float, default=default(250), help="częstotliwość próbkowania po stronie nadajnika")
    parser.add_argument("--channel-labels", default=default(DEFAULT_LABELS), help="kolejność WSZYSTKICH kanałów w pakiecie")
    parser.add_argument("--frame-samples", type=int, default=default(4), help="próbki w datagramie (0 = zmienna ramka)")
    parser.add_argument("--byte-order", choices=("little", "big"), default=default("little"))
    parser.add_argument("--synthetic-input", action="store_true", help="oznacz zewnętrzny nadajnik testowy jako syntetyczny")
