"""LSL acquisition and explicit Unicorn channel/unit mapping, shared by all apps."""

import time

import numpy as np
from pylsl import StreamInlet, local_clock, proc_clocksync, proc_monotonize, resolve_byprop, resolve_streams

from processing import FeatureConfig, UNICORN_LABELS


def channel_metadata(info):
    labels, units = [], []
    channel = info.desc().child("channels").child("channel")
    while not channel.empty():
        labels.append(channel.child_value("label"))
        units.append(channel.child_value("unit"))
        channel = channel.next_sibling()
    count = info.channel_count()
    return ((labels + [""] * count)[:count], (units + [""] * count)[:count])


def channel_labels(info):
    labels, _ = channel_metadata(info)
    return [label or f"ch{i}" for i, label in enumerate(labels)]


def discover_streams():
    return [{"name": s.name(), "type": s.type(), "channels": s.channel_count(),
             "sampling_rate": s.nominal_srate(), "source_id": s.source_id()} for s in resolve_streams(1.0)
            if s.nominal_srate() > 0]


def find_stream(name=None, stream_type="EEG", timeout=5):
    prop, value = ("name", name) if name else ("type", stream_type)
    found = resolve_byprop(prop, value, minimum=1, timeout=timeout)
    if not found:
        raise ValueError("Nie znaleziono strumienia LSL. Uruchom Unicorn LSL i wybierz jego nazwę.")
    if len(found) > 1:
        raise ValueError("Znaleziono kilka strumieni. Wskaż jednoznaczną nazwę przez --name.")
    return found[0]


def build_config(info, units="auto", assume_unicorn_order=False, artifact_uv=200, jaw_band=(55, 95)):
    labels, metadata_units = channel_metadata(info)
    normalized = [label.strip().lower() for label in labels]
    expected = [label.lower() for label in UNICORN_LABELS]
    if all(normalized.count(label) == 1 for label in expected):
        mapping = [normalized.index(label) for label in expected]
    elif assume_unicorn_order and info.channel_count() >= 8:
        mapping = list(range(8))
        labels[:8] = UNICORN_LABELS
    else:
        raise ValueError("Brak jednoznacznych etykiet Fz,C3,Cz,C4,Pz,PO7,Oz,PO8. "
                         "Sprawdź montaż. Dla standardowej kolejności Unicorna zaznacz jej potwierdzenie "
                         "lub użyj --assume-unicorn-order.")
    if units == "auto":
        known = {metadata_units[i].strip().lower().replace("µ", "u").replace("μ", "u") for i in mapping}
        if known <= {"uv", "microvolts", "microvolt"}:
            scale = 1.0
        elif known <= {"v", "volt", "volts"}:
            scale = 1e6
        else:
            raise ValueError("Brak spójnych jednostek LSL. Wybierz jawnie µV albo V po sprawdzeniu eksportera.")
    else:
        if units not in ("uv", "v"):
            raise ValueError("Jednostki: auto, uv lub v.")
        scale = 1e6 if units == "v" else 1.0
    return FeatureConfig(sampling_rate=info.nominal_srate(), channel_count=info.channel_count(),
                         eeg_channels=mapping, posterior_channels=mapping[4:8], c3=mapping[1], c4=mapping[3],
                         artifact_uv=artifact_uv, jaw_band=list(jaw_band), input_scale_uv=scale,
                         channel_labels=[label or f"ch{i}" for i, label in enumerate(labels)]).validate()


class LSLSource:
    def __init__(self, name=None, stream_type="EEG", units="auto", assume_unicorn_order=False,
                 artifact_uv=200, jaw_band=(55, 95)):
        info = find_stream(name, stream_type)
        self.inlet = StreamInlet(info, max_buflen=5, processing_flags=proc_clocksync | proc_monotonize)
        try:
            full = self.inlet.info(timeout=5)
            self.config = build_config(full, units, assume_unicorn_order, artifact_uv, jaw_band)
            self.stream = {"name": full.name(), "source_id": full.source_id(), "hostname": full.hostname(),
                           "units_option": units, "assumed_unicorn_order": assume_unicorn_order,
                           "transport": "lsl", "timing": "lsl_corrected_to_monotonic"}
            self.inlet.open_stream(timeout=5)
        except Exception:
            self.inlet.close_stream()
            raise
        self.last_timestamp = None
        self.last_received = None
        self.gap = False
        self.simulated = self.stream["source_id"].startswith("fake-")

    def read(self):
        samples, stamps = self.inlet.pull_chunk(timeout=0.0, max_samples=4096)
        self.gap = False
        if not stamps:
            return np.empty((0, self.config.channel_count)), np.array([])
        x, t = np.asarray(samples, dtype=float), np.asarray(stamps, dtype=float)
        # The calibration controller uses the host monotonic clock, independently of transport.
        t += time.monotonic() - local_clock()
        if x.shape != (len(t), self.config.channel_count) or not np.isfinite(t).all():
            raise ValueError("Niepoprawny kształt lub znaczniki czasu LSL.")
        # No windows may span a lost chunk. If there is an internal gap retain only its suffix.
        tolerance = 3 / self.config.sampling_rate
        diffs = np.diff(t)
        gaps = np.where((diffs <= 0) | (diffs > tolerance))[0]
        if len(gaps):
            cut = gaps[-1] + 1
            x, t = x[cut:], t[cut:]
            self.gap = True
        if self.last_timestamp is not None and not 0 < t[0] - self.last_timestamp <= tolerance:
            self.gap = True
        self.last_timestamp = float(t[-1])
        self.last_received = time.monotonic()
        x[:, self.config.eeg_channels] *= self.config.input_scale_uv
        return x, t

    def fresh(self):
        return (self.last_received is not None and time.monotonic() - self.last_received < 0.5
                and self.last_timestamp is not None and -0.1 <= time.monotonic() - self.last_timestamp < 0.5)

    def close(self):
        self.inlet.close_stream()


# Backwards-compatible import for existing integrations.
from profiles import assert_profile_source
