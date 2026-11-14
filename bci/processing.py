"""Game-independent EEG features shared by calibration and the LSL bridge.

Input is always samples x channels, in microvolts. These are experimental
control features, not clinical measures or probabilities of mental states.
"""

import math
from collections import deque
from dataclasses import asdict, dataclass, field

import numpy as np
from scipy.signal import detrend, welch

BANDS = {"theta": (4, 8), "alpha": (8, 13), "beta": (13, 30)}
WINDOW_S = 2.0
RATE_HZ = 10
BASELINE_S = 60
WARMUP_S = 3


class Window:
    """Ostatnie sekundy sygnału ze wszystkich kanałów."""

    def __init__(self, srate, n_channels, seconds=WINDOW_S):
        self.srate = srate
        self.n = int(seconds * srate)
        self.buffer = np.zeros((self.n, n_channels))
        self.filled = 0

    def push(self, chunk):
        if not len(chunk):
            return
        if len(chunk) >= self.n:
            self.buffer = chunk[-self.n:].copy()
        else:
            self.buffer = np.roll(self.buffer, -len(chunk), axis=0)
            self.buffer[-len(chunk):] = chunk
        self.filled = min(self.n, self.filled + len(chunk))

    def clear(self):
        self.filled = 0
        self.buffer.fill(0)

    # None, dopóki nie zebrało się tyle danych
    def last(self, seconds, channels):
        n = int(seconds * self.srate)
        if self.filled < n:
            return None
        return detrend(self.buffer[-n:, channels], axis=0)


class FocusMeter:
    name = "focus"

    def __init__(self, channels, artifact_uv):
        self.channels = channels
        self.artifact_uv = artifact_uv
        self.history = deque(maxlen=BASELINE_S * RATE_HZ)
        self.value = 0.5
        self.powers = None
        self.artifacts = 0

    # None = brak nowej wartości (za mało danych albo artefakt); gra trzyma wtedy poprzednią
    def compute(self, window):
        x = window.last(WINDOW_S, self.channels)
        if x is None:
            return None
        if np.ptp(x, axis=0).max() > self.artifact_uv:   # mrugnięcie, ruch, odklejona elektroda
            self.artifacts += 1
            return None

        freqs, psd = welch(x, fs=window.srate, nperseg=int(window.srate), axis=0)
        self.powers = {name: float(psd[(freqs >= lo) & (freqs < hi)].mean()) for name, (lo, hi) in BANDS.items()}
        index = self.powers["beta"] / (self.powers["alpha"] + self.powers["theta"] + 1e-12)
        self.history.append(math.log(index + 1e-12))
        if len(self.history) < WARMUP_S * RATE_HZ:
            return self.value

        spread = float(np.std(self.history)) or 1e-6
        z = (self.history[-1] - float(np.mean(self.history))) / spread
        self.value += 0.3 * (1 / (1 + math.exp(-z)) - self.value)   # wygładzanie, bez migotania
        return self.value


class LateralityMeter:
    name = "laterality"

    def __init__(self, left, right, artifact_uv):
        self.channels = [left, right]
        self.artifact_uv = artifact_uv
        self.history = deque(maxlen=BASELINE_S * RATE_HZ)
        self.value = 0.0

    def compute(self, window):
        x = window.last(1.0, self.channels)
        if x is None or np.ptp(x, axis=0).max() > self.artifact_uv:
            return None
        freqs, psd = welch(x, fs=window.srate, nperseg=int(window.srate / 2), axis=0)
        power = psd[(freqs >= 8) & (freqs <= 30)].mean(axis=0)
        self.history.append(math.log(power[1] + 1e-12) - math.log(power[0] + 1e-12))
        if len(self.history) < WARMUP_S * RATE_HZ:
            return 0.0

        # odchylenie od własnej średniej: 2 odchylenia standardowe = pełne wychylenie
        spread = float(np.std(self.history)) or 1e-6
        z = (self.history[-1] - float(np.mean(self.history))) / spread
        self.value += 0.3 * (min(1.0, max(-1.0, z / 2)) - self.value)
        return self.value


class JawMeter:
    name = "jaw"

    def __init__(self, channels, band):
        self.channels = channels
        self.band = band
        self.history = deque(maxlen=BASELINE_S * RATE_HZ)
        self.value = 0.0

    # bez odrzucania artefaktów: zaciśnięcie szczęki samo jest artefaktem
    def compute(self, window):
        x = window.last(0.25, self.channels)
        if x is None:
            return None
        spectrum = np.abs(np.fft.rfft(x * np.hanning(len(x))[:, None], axis=0)) ** 2
        freqs = np.fft.rfftfreq(len(x), 1 / window.srate)
        level = math.log10(float(spectrum[(freqs >= self.band[0]) & (freqs <= self.band[1])].mean()) + 1e-12)
        self.history.append(level)
        if len(self.history) < WARMUP_S * RATE_HZ:
            return 0.0

        # spoczynek = dolne 20% ostatniej minuty; 3x więcej mocy to początek skali, 30x to pełna
        rest = float(np.percentile(self.history, 20))
        self.value = min(1.0, max(0.0, level - rest - 0.5))
        return self.value


UNICORN_LABELS = ("Fz", "C3", "Cz", "C4", "Pz", "PO7", "Oz", "PO8")
SIGNALS = ("eyes_closed", "right_hand", "left_hand", "jaw")


@dataclass
class FeatureConfig:
    """Stored with the profile; the exact same configuration is used at runtime."""

    sampling_rate: float = 250.0
    channel_count: int = 8
    eeg_channels: list = field(default_factory=lambda: list(range(8)))
    posterior_channels: list = field(default_factory=lambda: [4, 5, 6, 7])
    c3: int = 1
    c4: int = 3
    artifact_uv: float = 200.0
    jaw_band: list = field(default_factory=lambda: [55.0, 95.0])
    input_scale_uv: float = 1.0
    channel_labels: list = field(default_factory=lambda: list(UNICORN_LABELS))

    def validate(self):
        if not math.isfinite(self.sampling_rate) or self.sampling_rate < 100:
            raise ValueError("Wymagana stała częstotliwość próbkowania co najmniej 100 Hz.")
        if not self.eeg_channels or not self.posterior_channels:
            raise ValueError("Brak kanałów EEG lub kanałów tylnych.")
        indices = self.eeg_channels + self.posterior_channels + [self.c3, self.c4]
        if any(type(i) is not int or not 0 <= i < self.channel_count for i in indices):
            raise ValueError("Indeks kanału poza zakresem strumienia.")
        if self.c3 == self.c4 or not set(self.posterior_channels + [self.c3, self.c4]) <= set(self.eeg_channels):
            raise ValueError("C3 i C4 muszą być różnymi kanałami EEG; tylne kanały muszą należeć do EEG.")
        if len(set(self.eeg_channels)) != len(self.eeg_channels):
            raise ValueError("Powtórzone indeksy EEG.")
        if len(self.channel_labels) != self.channel_count:
            raise ValueError("Liczba etykiet nie odpowiada liczbie kanałów.")
        if not math.isfinite(self.artifact_uv) or self.artifact_uv <= 0:
            raise ValueError("Próg artefaktów musi być dodatni.")
        if not math.isfinite(self.input_scale_uv) or self.input_scale_uv <= 0:
            raise ValueError("Nieprawidłowa skala jednostek wejściowych.")
        if len(self.jaw_band) != 2 or not 0 < self.jaw_band[0] < self.jaw_band[1] < self.sampling_rate / 2:
            raise ValueError("Pasmo szczęki musi mieścić się poniżej częstotliwości Nyquista.")
        return self

    def to_dict(self):
        return asdict(self)


def feature_window(name):
    return {"eyes_closed": 2.0, "right_hand": 1.0, "left_hand": 1.0, "jaw": 0.25}[name]


def extract_feature(window, name, config):
    """Returns (raw feature, rejection reason). Fixed, directional features.

    Eyes: posterior log alpha power. Right/left: opposite sensorimotor log
    power contrasts (8-30 Hz). Jaw: log HF power, an EMG artifact, NOT EEG MI.
    Rejection checks run before detrending, including flat/disconnected channels.
    """
    seconds = feature_window(name)
    count = round(seconds * window.srate)
    if window.filled < count:
        return None, "warming_up"
    channels = (config.posterior_channels if name == "eyes_closed" else
                config.eeg_channels if name == "jaw" else [config.c3, config.c4])
    raw = window.buffer[-count:, channels]
    if not np.isfinite(raw).all():
        return None, "non_finite"
    ranges = np.ptp(raw, axis=0)
    if np.any(ranges < 0.01):
        return None, "flat_channel"
    if np.max(ranges) > (5000 if name == "jaw" else config.artifact_uv):
        return None, "amplitude_artifact"
    x = detrend(raw, axis=0)
    freqs, psd = welch(x, fs=window.srate, nperseg=min(len(x), round(window.srate)), axis=0)
    band = config.jaw_band if name == "jaw" else (8, 13) if name == "eyes_closed" else (8, 30)
    mask = (freqs >= band[0]) & (freqs < band[1])
    power = psd[mask].mean(axis=0)
    if not np.isfinite(power).all() or np.any(power <= 1e-12):
        return None, "no_band_power"
    if name == "jaw":
        return float(np.log10(power.mean())), None
    if name == "eyes_closed":
        return float(np.log(power.mean())), None
    contrast = float(np.log(power[1]) - np.log(power[0]))
    return (contrast if name == "right_hand" else -contrast), None


