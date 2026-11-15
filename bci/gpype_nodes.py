"""Węzły g.Pype wspólne dla kalibratora i toru gry.

    Unicorn (HybridBlack) -> Highpass -> Bandstop -> SignalClassifier -> UdpJsonSender -> Unity
                                                  +-> SampleTap -> kalibrator

Jeden łańcuch filtrów (build_front) obsługuje kalibrację i grę, więc profil powstaje na
dokładnie takim sygnale, na jakim później działa klasyfikator. Żaden węzeł nie zna gier.
"""

import json
import math
import socket
import threading
import time
from collections import deque

import gpype as gp
import numpy as np
from gpype.common._private import channels as channel_info

from calibration_session import DemoSource
from processing import (FeatureConfig, FocusMeter, JawMeter, LateralityMeter, RATE_HZ, SIGNALS,
                        UNICORN_LABELS, Window)
from profiles import ProfileEngine

PORT_IN = gp.Constants.Defaults.PORT_IN
PORT_OUT = gp.Constants.Defaults.PORT_OUT
FRAME_SIZE = 4
HIGHPASS_HZ = 1.0
GAP_S = 0.5   # dłuższa przerwa między ramkami = utrata danych; okna analizy zaczynają od nowa

# cykl zadań źródła syntetycznego: (stan, sekundy)
PLAN = [("rest", 4), ("eyes_closed", 4), ("rest", 2), ("right_hand", 4),
        ("rest", 2), ("left_hand", 4), ("rest", 2), ("jaw", 3), ("rest", 2)]


def filter_settings(mains_hz=50):
    """Zapisywane w profilu: kalibracja i gra muszą używać tych samych filtrów."""
    return {"highpass_hz": HIGHPASS_HZ, "notch_hz": [mains_hz - 2, mains_hz + 2]}


def stream_info(fake=False, serial=None, mains_hz=50):
    return {"name": "SYNTETYCZNY Unicorn (g.Pype)" if fake else "Unicorn Hybrid Black (g.Pype)",
            "transport": "gpype", "source_id": "fake-gpype" if fake else (serial or "unicorn"),
            "filters": filter_settings(mains_hz)}


class SyntheticUnicorn(gp.Generator):
    """Sztuczne EEG w układzie Unicorna: 8 kanałów, 250 Hz, µV. Zastępuje HybridBlack w testach.

    state = None: cykl PLAN; inaczej "rest" albo nazwa zadania (ustawia ją kalibrator).
    Dodaje przydźwięk sieci i powolny dryf, żeby filtry miały co usuwać.
    """

    def __init__(self, state=None, mains_hz=50, **kwargs):
        kwargs.setdefault("sampling_rate", 250)
        kwargs.setdefault("channel_count", 8)
        kwargs.setdefault("frame_size", FRAME_SIZE)
        super().__init__(**kwargs)
        self.state = state
        self._mains_hz = mains_hz
        self._demo = DemoSource()
        self._sent = 0

    def current_state(self):
        if self.state is not None:
            return self.state
        elapsed = (self._sent / 250) % sum(seconds for _, seconds in PLAN)
        for name, seconds in PLAN:
            if elapsed < seconds:
                return name
            elapsed -= seconds
        return "rest"

    def step(self, data):
        out = super().step(data)
        if out is None:
            return None
        frame = out[PORT_OUT]
        t = (self._sent + np.arange(len(frame))) / 250
        x = self._demo.samples(t, self.current_state())
        x += (8 * np.sin(2 * np.pi * self._mains_hz * t) + 25 * np.sin(2 * np.pi * 0.1 * t))[:, None]
        frame[:, :8] = x   # ostatnia kolumna to znacznik czasu g.Pype, zostaje
        self._sent += len(frame)
        return out


def build_front(pipeline, fake=False, serial=None, mains_hz=50, state=None):
    """Źródło i filtrowanie. Zwraca (źródło, ostatni węzeł filtrów)."""
    if fake:
        source = SyntheticUnicorn(state=state, mains_hz=mains_hz)
    else:
        source = gp.HybridBlack(serial=serial, frame_size=FRAME_SIZE)
    highpass = gp.Highpass(f_c=HIGHPASS_HZ)
    notch = gp.Bandstop(f_lo=mains_hz - 2, f_hi=mains_hz + 2)
    pipeline.connect(source, highpass)
    pipeline.connect(highpass, notch)
    return source, notch


def eeg_scale(context, config):
    """Sprawdza, czy wejście wygląda jak 8 kanałów Unicorna; zwraca mnożnik do µV."""
    rate = context.get(gp.Constants.Keys.SAMPLING_RATE)
    count = context.get(gp.Constants.Keys.CHANNEL_COUNT)
    if rate != config.sampling_rate or count is None or count < config.channel_count:
        raise ValueError(f"Wejście ma {count} kanałów przy {rate} Hz, a profil wymaga "
                         f"{config.channel_count} kanałów przy {config.sampling_rate:g} Hz.")
    units = (context.get(gp.Constants.Keys.CHANNEL_UNITS) or [None])[0]
    return 1e6 if units == "V" else 1.0


class SampleTap(gp.INode):
    """Oddaje przefiltrowane próbki kalibratorowi w tym samym procesie."""

    def __init__(self, seconds=5.0, **kwargs):
        super().__init__(**kwargs)
        self._frames = deque(maxlen=int(seconds * 250 / FRAME_SIZE))
        self._lock = threading.Lock()
        self._scale = 1.0
        self.frames = 0

    def setup(self, data, port_context_in):
        self._scale = eeg_scale(port_context_in[PORT_IN], FeatureConfig())
        return super().setup(data, port_context_in)

    def step(self, data):
        frame = data[PORT_IN][:, :8].astype(float) * self._scale
        with self._lock:
            self._frames.append((time.monotonic(), frame))
            self.frames += 1
        return {}

    def drain(self):
        with self._lock:
            frames = list(self._frames)
            self._frames.clear()
        return frames


class SignalClassifier(gp.IONode):
    """EEG w µV -> nazwane sygnały. Na wyjściu jeden kanał na sygnał, odświeżany 10 razy na sekundę.

    Z profilem kalibracji: eyes_closed, right_hand, left_hand, jaw (0-1) z flagami _active
    i _valid, laterality (-1..1) i focus. Bez profilu: nieskalibrowane heurystyki focus,
    laterality i jaw. Czas liczony jest w próbkach, nie zegarem komputera.

    Szczęka działa zawsze: gdy profil nie ma gotowego detektora jaw (albo profilu nie ma),
    jaw i jaw_active liczy heurystyka względem spoczynku z ostatniej minuty. jaw_active włącza
    się już przy lekkim zaciśnięciu, bo gry używają go jako przycisku, nie jako miary siły.
    """

    JAW_ON, JAW_OFF = 0.2, 0.08   # skala JawMeter: 0 to 3x mocy spoczynkowej, 1 to 30x
    JAW_DWELL = 2                 # tyle kolejnych odczytów (0,2 s) ponad progiem, żeby pominąć pojedyncze trzaski

    def __init__(self, profile=None, **kwargs):
        super().__init__(**kwargs)
        self.engine = ProfileEngine(profile) if profile else None
        self.feature_config = self.engine.config if self.engine else FeatureConfig()
        config = self.feature_config
        self.focus = FocusMeter(config.eeg_channels[:5], config.artifact_uv)
        if self.engine:
            self.names = [name + suffix for name in SIGNALS for suffix in ("", "_active", "_valid")]
            self.names += ["laterality", "laterality_valid"]
            self.meters = []
        else:
            self.names = ["laterality", "laterality_valid", "jaw", "jaw_active", "jaw_valid"]
            self.meters = [LateralityMeter(config.c3, config.c4, config.artifact_uv)]
        # heurystyka szczęki zastępuje brakujący detektor z kalibracji
        self.jaw = None if self.engine and "jaw" in self.engine.detectors else JawMeter(config.eeg_channels, config.jaw_band)
        self._jaw_active, self._jaw_above = False, 0
        self.names += ["focus", "focus_valid"]
        self.results = {}
        self._values = np.zeros(len(self.names))
        self._window = Window(config.sampling_rate, config.channel_count)
        self._scale = 1.0
        self._count = self._next_update = 0
        self._last_frame = None
        self.resets = 0

    def setup(self, data, port_context_in):
        out = super().setup(data, port_context_in)
        self._scale = eeg_scale(port_context_in[PORT_IN], self.feature_config)
        for context in out.values():
            context[gp.Constants.Keys.CHANNEL_COUNT] = len(self.names)
            for key in channel_info.DESCRIPTION_KEYS:
                context.pop(key, None)
            context.update(channel_info.describe([gp.Constants.ChannelRoles.SIGNAL] * len(self.names),
                                                 labels=list(self.names)))
        return out

    def reset(self):
        self.resets += 1
        self._window.clear()
        if self.engine:
            for detector in self.engine.detectors.values():
                detector.reset()

    def update(self, now):
        values = {}
        if self.engine:
            self.results = self.engine.update(self._window, now)
            for name, item in self.results.items():
                values[name] = item["value"]
                values[name + "_valid"] = float(item["valid"])
                values[name + "_active"] = float(item["active"])
            clench = self.results["jaw"]["active"]
        else:
            for meter in self.meters:
                value = meter.compute(self._window)
                values[meter.name] = meter.value if value is None else value
                values[meter.name + "_valid"] = float(value is not None)
            clench = False
        if self.jaw:
            value = self.jaw.compute(self._window)
            level = 0.0 if value is None else value
            self._jaw_above = self._jaw_above + 1 if level >= self.JAW_ON else 0
            if self._jaw_active:
                self._jaw_active = level >= self.JAW_OFF
            elif self._jaw_above >= self.JAW_DWELL:
                self._jaw_active = True
            clench = self._jaw_active
            values.update(jaw=level, jaw_active=float(clench), jaw_valid=float(value is not None))
        # focus pozostaje heurystyką także z profilem; zaciśnięta szczęka go unieważnia
        value = None if clench else self.focus.compute(self._window)
        values["focus"] = self.focus.value if value is None else value
        values["focus_valid"] = float(value is not None)
        self._values = np.array([values[name] for name in self.names])

    REASONS = {"warming_up": "za mało danych", "amplitude_artifact": "artefakt amplitudy",
               "flat_channel": "płaski kanał", "non_finite": "NaN w danych", "jaw_artifact": "zaciśnięta szczęka",
               "no_band_power": "brak mocy w paśmie"}

    def diagnose(self):
        """Jedna linia dla operatora: czy okno się wypełnia, rozpiętość kanałów i czemu sygnał jest nieważny."""
        config, window = self.feature_config, self._window
        filled = window.filled / config.sampling_rate
        parts = [f"okno {filled:.1f}/{window.n / config.sampling_rate:.0f} s", f"przerwy {self.resets}"]
        n = min(window.filled, round(2 * config.sampling_rate))
        if n:
            spans = np.ptp(window.buffer[-n:], axis=0)
            labels = config.channel_labels or [str(i) for i in range(len(spans))]
            parts.append("µV p-p " + " ".join(f"{label} {span:.0f}{'!' if span > config.artifact_uv else ''}"
                                               for label, span in zip(labels, spans)))
        for name, item in self.results.items():
            if not item["valid"] and item["reason"] in self.REASONS:
                parts.append(f"{name}: {self.REASONS[item['reason']]}")
        if self.focus.artifacts:
            parts.append(f"focus: odrzucone okna {self.focus.artifacts}")
        return " · ".join(parts)

    def step(self, data):
        config = self.feature_config
        frame = data[PORT_IN][:, :config.channel_count].astype(float) * self._scale
        arrived = time.monotonic()
        if self._last_frame is not None and arrived - self._last_frame > GAP_S:
            self.reset()
        self._last_frame = arrived
        self._window.push(frame)
        self._count += len(frame)
        if self._count >= self._next_update:
            self._next_update += round(config.sampling_rate / RATE_HZ)
            self.update(self._count / config.sampling_rate)
        return {PORT_OUT: np.tile(self._values, (len(frame), 1)).astype(gp.Constants.DATA_TYPE)}


class UdpJsonSender(gp.Sink):
    """Kanały wejścia jako wiadomości {type, name, value, t} do gier (docs/protocol.md).

    names: nazwa sygnału dla każdego kanału wejścia. commands: nazwa kanału 0/1 -> (komenda,
    kanał z wartością); komenda idzie co 0.5 s, dopóki kanał jest aktywny.
    """

    COMMAND_INTERVAL_S = 0.5

    def __init__(self, names, targets=(("127.0.0.1", 5005),), commands=None, **kwargs):
        super().__init__(**kwargs)
        self.names = list(names)
        self.targets = list(targets)
        self.commands = dict(commands or {})
        self.sent = 0
        self._socket = None
        self._every = 25
        self._count = self._next_send = 0
        self._command_at = {}
        # kanał 0/1 -> funkcja(stan), wołana w chwili wysłania zmiany do gry (pomiar opóźnień)
        self.on_change = {}
        self._last_state = {}

    def setup(self, data, port_context_in):
        context = port_context_in[PORT_IN]
        if context[gp.Constants.Keys.CHANNEL_COUNT] != len(self.names):
            raise ValueError("Liczba kanałów wejścia nie zgadza się z liczbą nazw sygnałów.")
        self._every = max(1, round(context[gp.Constants.Keys.SAMPLING_RATE] / RATE_HZ))
        return super().setup(data, port_context_in)

    def start(self):
        self._socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        super().start()

    def stop(self):
        if self._socket:
            self._socket.close()
            self._socket = None
        super().stop()

    def send(self, name, value, kind="metric"):
        if not math.isfinite(value):
            return
        message = json.dumps({"type": kind, "name": name, "value": round(float(value), 3),
                              "t": time.time()}).encode("utf-8")
        for target in self.targets:
            try:
                self._socket.sendto(message, target)
            except OSError:
                pass   # odbiorca chwilowo nieosiągalny; następna wartość idzie za 100 ms
        self.sent += 1

    def step(self, data):
        frame = data[PORT_IN]
        self._count += len(frame)
        if self._socket is None or self._count < self._next_send:
            return {}
        self._next_send += self._every
        row = dict(zip(self.names, frame[-1]))
        for name, value in row.items():
            self.send(name, value)
        for flag, callback in self.on_change.items():
            state = row.get(flag, 0) >= 0.5
            if state != self._last_state.get(flag, False):
                self._last_state[flag] = state
                try:
                    callback(state)
                except Exception:   # pomiar nie może zatrzymać wysyłania do gry
                    pass
        now = time.monotonic()
        for flag, (command, value_name) in self.commands.items():
            if row.get(flag, 0) >= 0.5 and now >= self._command_at.get(flag, 0):
                self.send(command, row.get(value_name, 1.0), "command")
                self._command_at[flag] = now + self.COMMAND_INTERVAL_S
        return {}


class PipelineSource:
    """Wejście kalibratora: własny potok g.Pype (źródło -> filtry -> SampleTap) w tym procesie.

    Próbki dostają równą siatkę czasu na zegarze monotonicznym komputera. Bluetooth oddaje
    dane paczkami, więc siatka trzyma się licznika próbek i zakotwicza na nowo po przerwie.
    """

    def __init__(self, fake=False, serial=None, mains_hz=50, artifact_uv=200, jaw_band=(55, 95),
                 clock=time.monotonic, record_session=None):
        self.clock = clock
        self.simulated = bool(fake)
        self.config = FeatureConfig(artifact_uv=artifact_uv, jaw_band=list(jaw_band),
                                    channel_labels=list(UNICORN_LABELS)).validate()
        self.stream = stream_info(fake, serial, mains_hz)
        self.pipeline = gp.Pipeline()
        self.node, filtered = build_front(self.pipeline, fake, serial, mains_hz, state="rest" if fake else None)
        self.tap = SampleTap()
        self.pipeline.connect(filtered, self.tap)
        self.recorder = None
        if record_session:   # surowe + przefiltrowane EEG i fazy kalibracji do data/raw/ (recording.py)
            from recording import Recorder
            self.recorder = Recorder(record_session, info={"kind": "calibration", "stream": self.stream,
                                                           "feature_config": self.config.to_dict()})
            self.recorder.attach(self.pipeline, self.node, filtered)
        self.last_timestamp = self.last_received = None
        self.gap = False
        self.resets = 0
        try:
            self.pipeline.start()
        except Exception:
            self.close()
            raise

    def set_state(self, state):
        """Tylko źródło syntetyczne: jakie zadanie ma udawać."""
        if self.simulated:
            self.node.state = state

    def read(self):
        now = self.clock()
        self.gap = False
        frames = self.tap.drain()
        if not frames:
            return np.empty((0, self.config.channel_count)), np.array([])
        x = np.concatenate([frame for _, frame in frames])
        fs = self.config.sampling_rate
        predicted_end = None if self.last_timestamp is None else self.last_timestamp + len(x) / fs
        if (predicted_end is None or now - self.last_received > GAP_S or abs(predicted_end - now) > 0.15
                or not np.isfinite(x).all()):
            self.gap = True
            self.resets += 1
            predicted_end = now
        stamps = predicted_end - np.arange(len(x) - 1, -1, -1) / fs
        self.last_timestamp, self.last_received = float(stamps[-1]), now
        return x, stamps

    def fresh(self):
        return self.last_received is not None and self.clock() - self.last_received < GAP_S

    def diagnostics(self):
        return {"frames": self.tap.frames, "timing_resets": self.resets}

    def mark(self, name, value=0.0):
        if self.recorder:
            self.recorder.mark(name, value)

    def close(self):
        try:
            self.pipeline.stop()
        finally:
            self.pipeline.close()
            if self.recorder:
                self.recorder.close()
