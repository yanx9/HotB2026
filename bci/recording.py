"""Zapis sesji: surowe EEG, EEG po filtrach, znaczniki i wyjście detektora na jednej osi próbek.

    Unicorn ──► źródło ──────────────┐  raw_*      surowe EEG [µV], przed filtrami
           filtry ───────────────────┤  flt_*      dokładnie to, co widzi klasyfikator
           gp.Marker ◄── mark() ─────┤  marker     kod zdarzenia na próbce, w której nadeszło
           SignalClassifier ─────────┘  out_*      sygnały wysyłane do gry (opcjonalnie)
                                  │
                                  ▼
                  HDF5Writer -> data/raw/<sesja>/eeg_<czas>.h5

Obok: events.csv (każde zdarzenie z wartością, czasem gry i odbioru) i session.json
(kody znaczników, kolumny, profil, filtry). Próbka N w każdej kolumnie to ta sama chwila,
więc łączenie EEG ze zdarzeniami gry nie wymaga żadnego dopasowywania zegarów.
Odczyt: load_recording() niżej.
"""

import csv
import json
import threading
import time
from datetime import datetime
from pathlib import Path

import gpype as gp
import numpy as np

from processing import UNICORN_LABELS

ROOT = Path(__file__).resolve().parents[1]
RAW_DIR = ROOT / "data" / "raw"

# Stałe kody, żeby nagrania z różnych dni dało się porównywać. Nieznane nazwy dostają
# kolejne kody od 100; pełna tabela zawsze trafia do session.json.
MARKER_CODES = {
    "session_start": 1, "eyes_closed": 2, "eyes_open": 3, "scan": 4, "alert": 5, "loot": 6,
    "caught": 7, "win": 8, "mode_keyboard": 9, "mode_eeg": 10,
    "truth_closed": 20, "truth_open": 21,                        # spacja w Mroku: prawdziwy stan oczu
    "pipe_eyes_closed": 11, "pipe_eyes_open": 12,                # potok WYSŁAŁ zmianę do gry (pomiar opóźnień)
}
# fazy kalibratora: cal_<sygnał>_<faza>, np. cal_eyes_closed_active = 32; wartość = numer próby
CAL_PHASES = ("prepare", "rest", "active", "complete", "cancel")
for _i, _signal in enumerate(("eyes_closed", "right_hand", "left_hand", "jaw")):
    for _j, _phase in enumerate(CAL_PHASES):
        MARKER_CODES[f"cal_{_signal}_{_phase}"] = 30 + 10 * _i + _j
SPACING_S = 0.008   # dwa znaczniki w tej samej próbce nadpisałyby się; rozsuwamy o 2 próbki


class Recorder:
    """Podłącza zapis do potoku g.Pype. Wywołaj attach() przed pipeline.start()."""

    def __init__(self, session, info=None, folder=None):
        stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        self.folder = Path(folder) if folder else RAW_DIR / f"{session}_{stamp}"
        self.folder.mkdir(parents=True, exist_ok=True)
        self.info = dict(info or {})
        self.codes = dict(MARKER_CODES)
        self.marker = gp.Marker(name="markers")
        self.writer = None
        self.columns = []
        self.count = 0
        self._lock = threading.Lock()
        self._last_stamp = 0.0
        self._events = (self.folder / "events.csv").open("w", newline="", encoding="utf-8")
        self._csv = csv.writer(self._events)
        self._csv.writerow(["seq", "name", "code", "value", "t_game_unix", "t_received_unix", "stamp"])
        self.started = datetime.now().isoformat(timespec="seconds")

    def attach(self, pipeline, source, filtered, outputs=None, output_names=()):
        """source: surowe EEG (pierwsze 8 kanałów), filtered: po filtrach, outputs: klasyfikator."""
        eeg = list(range(len(UNICORN_LABELS)))
        inputs = [eeg, eeg, [0]] + ([gp.Router.ALL] if outputs is not None else [])
        router = gp.Router(input_channels=inputs)
        pipeline.connect(source, router["in1"])
        pipeline.connect(filtered, router["in2"])
        pipeline.connect(self.marker, router["in3"])
        if outputs is not None:
            pipeline.connect(outputs, router["in4"])
        self.columns = ([f"raw_{c}" for c in UNICORN_LABELS] + [f"flt_{c}" for c in UNICORN_LABELS]
                        + ["marker"] + [f"out_{n}" for n in output_names])
        labeler = gp.ChannelLabeler(labels=self.columns)
        pipeline.connect(router, labeler)
        self.writer = gp.HDF5Writer(file_name=str(self.folder / "eeg.h5"))
        pipeline.connect(labeler, self.writer)
        self._write_meta()

    def mark(self, name, value=0.0, t_game=None):
        """Znacznik na próbce EEG z chwili wywołania. Bezpieczne z dowolnego wątku."""
        with self._lock:
            code = self.codes.get(name)
            if code is None:
                code = self.codes[name] = max(100, max(self.codes.values()) + 1)
            stamp = max(gp.stamp_clock(), self._last_stamp + SPACING_S)
            while gp.stamp_clock() < stamp:          # nie wolno znakować przyszłości
                time.sleep(0.001)
            try:
                self.marker.emit(code, observed_at=stamp)
            except (RuntimeError, ValueError):
                return                                # potok jeszcze nie działa albo już stoi
            self._last_stamp = stamp
            self.count += 1
            self._csv.writerow([self.count, name, code, value, t_game, time.time(), stamp])
            self._events.flush()
            if code >= 100:
                self._write_meta()

    @property
    def file(self):
        return getattr(self.writer, "_file_path", None)

    def _write_meta(self):
        meta = {"started": self.started, "file": Path(self.file).name if self.file else None,
                "sampling_rate": 250.0, "units": "uV", "columns": self.columns,
                "marker_codes": self.codes, "events": "events.csv", **self.info}
        (self.folder / "session.json").write_text(json.dumps(meta, ensure_ascii=False, indent=2),
                                                  encoding="utf-8")

    def close(self):
        with self._lock:
            self._write_meta()
            self._events.close()


def load_recording(folder):
    """-> (data: dict kolumna -> np.ndarray, events: lista słowników, meta). Zdarzenia mają
    pole "sample": próbka, w której znacznik leży w pliku (dopasowane po kolejności kodów)."""
    import h5py
    folder = Path(folder)
    meta = json.loads((folder / "session.json").read_text(encoding="utf-8"))
    files = sorted(folder.glob("eeg*.h5"))
    if not files:
        raise FileNotFoundError(f"Brak pliku eeg*.h5 w {folder}")
    with h5py.File(files[-1], "r") as h5:
        block = h5["data"][()]
    data = {name: block[i + 1] for i, name in enumerate(meta["columns"])}   # wiersz 0 to czas
    with (folder / "events.csv").open(encoding="utf-8") as f:
        events = list(csv.DictReader(f))
    marker = data["marker"]
    onsets = np.flatnonzero(marker != 0)
    queue = {}
    for sample in onsets:
        queue.setdefault(int(marker[sample]), []).append(int(sample))
    for event in events:
        samples = queue.get(int(event["code"]), [])
        event["sample"] = samples.pop(0) if samples else None   # None: przyszło po zamknięciu zapisu
        event["value"] = float(event["value"] or 0)
    return data, events, meta
