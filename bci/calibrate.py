"""Four standalone calibration views for Unicorn Hybrid Black, shared by all games.

    python bci/calibrate.py
    python bci/calibrate.py --fake --profile data/calibration/fake.json

The calibrator runs its own g.Pype pipeline in this process:
HybridBlack -> Highpass -> Bandstop -> SampleTap. These are the same source and
filters as in bci_pipeline.py, so the profile fits the signal the game pipeline sees.
The headset can be held by one process at a time: disconnect here before starting
bci_pipeline.py.

The UI runs on the main thread; acquisition, fitting and atomic
profile writes run on one worker thread. No game or Unity installation needed.
"""

import argparse
from dataclasses import asdict, replace
from pathlib import Path
import sys
import tempfile
import wave

import numpy as np

try:
    from PySide6.QtCore import QObject, QThread, QTimer, QUrl, Signal, Slot, Qt
    from PySide6.QtMultimedia import QSoundEffect
    from PySide6.QtWidgets import (QApplication, QCheckBox, QComboBox, QDoubleSpinBox, QFileDialog,
                                   QFormLayout, QHBoxLayout, QLabel, QLineEdit, QMainWindow,
                                   QPlainTextEdit, QProgressBar, QPushButton, QSpinBox,
                                   QStackedWidget, QVBoxLayout, QWidget)
except ImportError as exc:
    raise SystemExit(f"Nie można załadować Qt: {exc}\nZainstaluj interfejs: pip install -r bci/requirements-calibration.txt")

from time import monotonic as local_clock

from calibration_session import CalibrationSession, Protocol
from processing import SIGNALS, Window
from profiles import ProfileEngine, assert_profile_source, fit_calibration, load_profile, new_profile, save_profile


VIEWS = {
    "eyes_closed": ("Zamykanie oczu", "Alfa względna · PO7 / PO8",
                    "W spoczynku patrz na nieruchomy punkt. Na wysoki ton zamknij oczy, "
                    "na niski otwórz. Pozostań nieruchomo. To pomiar reaktywności alfa, nie poziomu relaksu.",
                    "ZAMKNIJ OCZY", "OTWÓRZ OCZY · SPOCZYNEK"),
    "right_hand": ("Prawa ręka", "Wyobrażanie ruchu · C3 / C4",
                   "Dłonie oprzyj wygodnie. Wyobrażaj sobie odczucie powolnego otwierania i zamykania "
                   "PRAWEJ dłoni. Nie wykonuj ruchu, nie napinaj mięśni ani szczęki. W spoczynku przerwij wyobrażanie.",
                   "WYOBRAŹ RUCH PRAWEJ DŁONI", "SPOCZYNEK · BEZ WYOBRAŻANIA"),
    "left_hand": ("Lewa ręka", "Wyobrażanie ruchu · C4 / C3",
                  "Dłonie oprzyj wygodnie. Wyobrażaj sobie odczucie powolnego otwierania i zamykania "
                  "LEWEJ dłoni. Nie wykonuj ruchu, nie napinaj mięśni ani szczęki. W spoczynku przerwij wyobrażanie.",
                  "WYOBRAŹ RUCH LEWEJ DŁONI", "SPOCZYNEK · BEZ WYOBRAŻANIA"),
    "jaw": ("Zaciśnięcie szczęki", "Sygnał mięśniowy EMG · 55–95 Hz domyślnie",
            "Na wysoki ton lekko i krótko zaciśnij szczękę, na niski całkowicie rozluźnij. "
            "Nie zaciskaj maksymalnie; przerwij przy dyskomforcie. Ten sygnał pochodzi z mięśni, nie z wyobrażania ruchu.",
            "LEKKO ZACIŚNIJ SZCZĘKĘ", "ROZLUŹNIJ SZCZĘKĘ"),
}


def result_text(result):
    if not result:
        return "Brak kalibracji. Parametry pojawią się po ukończeniu wszystkich prób."
    lines = [result["reason"], "", f"Pełne pary prób: {result['usable_trials']} / {result['total_trials']}"]
    for key, label in (("rest_center", "Środek spoczynku"), ("active_center", "Środek zadania"),
                       ("threshold_raw", "Próg aktywacji (cecha)"), ("release_raw", "Próg zwolnienia (cecha)"),
                       ("threshold", "Próg aktywacji (0–1)"), ("release_threshold", "Próg zwolnienia (0–1)"),
                       ("dwell_s", "Czas potwierdzenia [s]"), ("smoothing_s", "Wygładzanie [s]")):
        if key in result:
            lines.append(f"{label}: {result[key]:.4f}")
    validation = result.get("validation")
    if validation:
        lines += ["", f"Trafność zbalansowana (osobne próby): {validation['balanced_accuracy']:.0%}",
                  f"Czułość: {validation['sensitivity']:.0%} · swoistość: {validation['specificity']:.0%}",
                  f"Trafność okien kontrolnych: {validation['window_balanced_accuracy']:.0%}",
                  "", validation["note"]]
    return "\n".join(lines)


class Worker(QObject):
    state = Signal(dict)
    connected = Signal(dict)
    result = Signal(str, dict)
    error = Signal(str)
    cue = Signal(str)

    def __init__(self):
        super().__init__()
        self.source = self.session = self.profile = self.engine = None
        self.timer = None
        self.last_report = 0
        self.last_data = 0
        self.preview = {}
        self.recording = False

    @Slot(dict)
    def connect_source(self, options):
        self.disconnect_source()
        try:
            self.path = Path(options["path"]).expanduser().resolve()
            if not options["subject"].strip():
                raise ValueError("Podaj identyfikator osoby / sesji (bez danych osobowych).")
            from gpype_nodes import PipelineSource
            self.source = PipelineSource(fake=options["fake"], serial=options["serial"] or None,
                                         mains_hz=options["mains_hz"], artifact_uv=options["artifact_uv"],
                                         jaw_band=options["jaw_band"],
                                         record_session=None if options.get("no_record")
                                         else f"kalibracja_{options['subject'].strip()}")
            if self.source.recorder:
                self.source.recorder.info["profile"] = str(self.path)
                print(f"Nagrywanie kalibracji: {self.source.recorder.folder}", flush=True)
            if self.path.exists():
                self.profile = load_profile(self.path)
                if self.profile["subject"] != options["subject"]:
                    raise ValueError("Ten plik należy do innego identyfikatora. Wybierz nową ścieżkę lub zgodny identyfikator.")
                assert_profile_source(self.profile, self.source.stream, self.source.simulated, self.source.config,
                                      allow_simulated=self.source.simulated)
            else:
                self.profile = new_profile(options["subject"], self.source.config, self.source.stream,
                                           self.source.simulated)
            self.engine = ProfileEngine(self.profile)
            self.window = Window(self.source.config.sampling_rate, self.source.config.channel_count)
            self.last_data = local_clock()
            self.last_report = 0
            if self.timer is None:
                self.timer = QTimer(self)
                self.timer.setInterval(10)
                self.timer.timeout.connect(self.tick)
            self.timer.start()
            self.connected.emit({"stream": self.source.stream, "config": self.source.config.to_dict(),
                                 "path": str(self.path), "simulated": self.source.simulated})
            for name, result in self.profile["calibrations"].items():
                self.result.emit(name, result)
        except Exception as exc:
            self.disconnect_source()
            self.error.emit(str(exc))

    @Slot()
    def disconnect_source(self):
        if self.timer:
            self.timer.stop()
        if self.source:
            try:
                self.source.close()
            except Exception as exc:
                self.error.emit(f"Zamykanie potoku g.Pype: {exc}")
        self.source = self.profile = self.engine = None
        self.session = None
        self.recording = False
        self.connected.emit({})

    @Slot(str, int)
    def start_calibration(self, name, trials):
        if self.source is None or self.recording:
            return
        if not self.source.fresh():
            self.error.emit("Brak świeżych próbek. Nie rozpoczynam kalibracji.")
            return
        try:
            now = local_clock()
            protocol = replace(Protocol.for_signal(name), trials=trials)
            self.session = CalibrationSession(name, self.source.config, now, protocol)
            self.recording = True
            self.source.mark(f"cal_{name}_prepare")
            self.state.emit({"session": self.session.snapshot(now), "fresh": True, "preview": self.preview})
        except Exception as exc:
            self.error.emit(str(exc))

    @Slot()
    def cancel(self):
        if self.session and self.recording:
            self.session.cancelled = True
            self.session.phase = "cancelled"
            self.session.phase_end = local_clock()
            self.recording = False
            if self.source:
                self.source.mark(f"cal_{self.session.name}_cancel", self.session.trial)
            self.cue.emit("rest")
            self.state.emit({"session": self.session.snapshot(local_clock()), "fresh": True, "preview": self.preview})

    @Slot()
    def tick(self):
        if self.source is None:
            return
        try:
            now = local_clock()
            # źródło syntetyczne udaje zadanie, o które prosi właśnie kalibrator
            self.source.set_state(self.session.name if self.recording and self.session.phase == "active" else "rest")
            samples, stamps = self.source.read()
            fresh = self.source.fresh()
            if self.source.gap:
                self.window.clear()
                for detector in self.engine.detectors.values():
                    detector.reset()
                if self.recording:
                    self.session.window.clear()
                    self.session.last_sample = None
            if len(stamps):
                self.window.push(samples)
                self.last_data = now if fresh else self.last_data
                if self.recording:
                    self.session.ingest(samples, stamps)
            if self.recording and now - self.last_data > 2:
                self.cancel()
                self.error.emit("Utrata danych EEG. Próba przerwana; poprzednie parametry nie zostały nadpisane.")
            if self.recording and self.session.advance(now):
                self.source.mark(f"cal_{self.session.name}_{self.session.phase}", self.session.trial)
                self.cue.emit(self.session.phase)
                if self.session.completed:
                    self.recording = False
                    result = fit_calibration(self.session.name, self.session.trials, asdict(self.session.protocol))
                    result["events"] = self.session.events
                    self.profile["calibrations"][self.session.name] = result
                    save_profile(self.path, self.profile)
                    self.engine = ProfileEngine(self.profile)
                    self.result.emit(self.session.name, result)
            if now - self.last_report >= 0.1:
                self.last_report = now
                self.preview = self.engine.update(self.window, now, fresh=fresh)
                self.state.emit({"session": self.session.snapshot(now) if self.session else None,
                                 "fresh": fresh, "preview": self.preview, "recording": self.recording,
                                 "diagnostics": self.source.diagnostics()})
        except Exception as exc:
            self.cancel()
            if self.timer:
                self.timer.stop()
            self.error.emit(f"Błąd akwizycji lub zapisu: {exc}. Połącz źródło ponownie.")


def cue_wave(freq, pulses=1, pulse_s=0.35, gap_s=0.12, rate=44100, level=0.85):
    """Głośny, wyraźny sygnał: ton z harmonicznymi (1-5) i płaską obwiednią z 10 ms narastania.

    Czysta sinusoida 330 Hz z obwiednią Hanninga była na laptopie prawie niesłyszalna: małe
    głośniki słabo oddają niskie tony, a okno Hanninga daje pełną głośność tylko przez ułamek
    czasu. Harmoniczne przenoszą energię w pasmo 1-3 kHz, które każdy głośnik odtwarza.
    """
    t = np.arange(int(rate * pulse_s)) / rate
    tone = sum(np.sin(2 * np.pi * k * freq * t) / k for k in range(1, 6))
    ramp = np.minimum(1, np.minimum(t, t[-1] - t) / 0.010)
    pulse = tone / np.abs(tone).max() * ramp
    gap = np.zeros(int(rate * gap_s))
    signal = np.concatenate([np.concatenate([pulse, gap]) for _ in range(pulses)])[:-len(gap)]
    return level * signal


# zadanie: jeden długi wysoki ton; spoczynek: dwa krótkie niskie (rozpoznawalne też po rytmie)
CUES = {"active": dict(freq=880, pulses=1, pulse_s=0.45),
        "rest": dict(freq=440, pulses=2, pulse_s=0.16),
        "complete": dict(freq=1175, pulses=3, pulse_s=0.12, gap_s=0.08)}


class Sounds(QObject):
    def __init__(self, parent):
        super().__init__(parent)
        self.directory = tempfile.TemporaryDirectory(prefix="bci-cues-")
        self.effects = {}
        rate = 44100
        for name, params in CUES.items():
            signal = cue_wave(rate=rate, **params)
            path = Path(self.directory.name) / f"{name}.wav"
            with wave.open(str(path), "wb") as wav:
                wav.setparams((1, 2, rate, 0, "NONE", "not compressed"))
                wav.writeframes((signal * 32767).astype("<i2").tobytes())
            effect = QSoundEffect(parent)
            effect.setSource(QUrl.fromLocalFile(str(path)))
            effect.setVolume(1.0)
            self.effects[name] = effect

    @Slot(str)
    def play(self, phase):
        if phase in self.effects:
            for effect in self.effects.values():
                effect.stop()
            self.effects[phase].play()


class CalibrationView(QWidget):
    start = Signal(str, int)
    stop = Signal()

    def __init__(self, name):
        super().__init__()
        self.name = name
        title, subtitle, instructions, _, _ = VIEWS[name]
        layout = QVBoxLayout(self)
        heading = QLabel(title)
        heading.setObjectName("heading")
        layout.addWidget(heading)
        layout.addWidget(QLabel(subtitle))
        intro = QLabel(instructions)
        intro.setWordWrap(True)
        layout.addWidget(intro)
        self.prompt = QLabel("Gotowy do kalibracji")
        self.prompt.setObjectName("prompt")
        self.prompt.setWordWrap(True)
        self.prompt.setMinimumHeight(80)
        self.prompt.setAlignment(Qt.AlignmentFlag.AlignCenter)
        layout.addWidget(self.prompt)
        self.countdown = QLabel("Wysoki, długi ton: zadanie · dwa niskie, krótkie: spoczynek")
        layout.addWidget(self.countdown)
        self.progress = QProgressBar()
        self.progress.setRange(0, 100)
        layout.addWidget(self.progress)
        row = QHBoxLayout()
        self.trials = QSpinBox()
        self.trials.setRange(6, 40)
        self.trials.setValue(Protocol.for_signal(name).trials)
        row.addWidget(QLabel("Pary prób:"))
        row.addWidget(self.trials)
        self.start_button = QPushButton("Rozpocznij kalibrację")
        self.start_button.clicked.connect(lambda: self.start.emit(self.name, self.trials.value()))
        self.stop_button = QPushButton("Przerwij")
        self.stop_button.clicked.connect(self.stop.emit)
        row.addWidget(self.start_button)
        row.addWidget(self.stop_button)
        layout.addLayout(row)
        self.quality = QLabel("Brak danych")
        self.quality.setWordWrap(True)
        layout.addWidget(self.quality)
        self.live = QLabel("Podgląd po kalibracji: —")
        layout.addWidget(self.live)
        self.result_box = QPlainTextEdit(result_text(None))
        self.result_box.setReadOnly(True)
        layout.addWidget(self.result_box, 1)

    def update_session(self, session):
        phase = session["phase"]
        prompts = {"prepare": "PRZYGOTUJ SIĘ", "rest": VIEWS[self.name][4],
                   "active": VIEWS[self.name][3], "complete": "KALIBRACJA ZAKOŃCZONA",
                   "cancelled": "PRZERWANO · ODPOCZNIJ"}
        self.prompt.setText(prompts[phase])
        self.countdown.setText(f"Para {session['trial']} / {session['total_trials']} · {session['remaining_s']:.1f} s")
        done = max(0, session["trial"] - 1) * 2 + (1 if phase == "active" else 0)
        self.progress.setValue(100 if phase == "complete" else int(100 * done / (2 * session["total_trials"])))
        self.quality.setText(f"Okna przyjęte: {session['accepted_windows']} · odrzucone: {session['rejected_windows']} "
                             f"· stan: {session['quality']}")


class CalibrationApp(QMainWindow):
    connect_requested = Signal(dict)
    disconnect_requested = Signal()
    start_requested = Signal(str, int)
    cancel_requested = Signal()

    def __init__(self, args):
        super().__init__()
        self.setWindowTitle("BCI · Kalibracja Unicorn Hybrid Black")
        self.resize(1080, 960)
        self.connected_flag = self.recording = self.connecting = False
        self.saved_results = {}
        self.thread = QThread(self)
        self.worker = Worker()
        self.worker.moveToThread(self.thread)
        self.thread.finished.connect(self.worker.deleteLater)
        self.connect_requested.connect(self.worker.connect_source)
        self.disconnect_requested.connect(self.worker.disconnect_source)
        self.start_requested.connect(self.worker.start_calibration)
        self.cancel_requested.connect(self.worker.cancel)
        self.worker.state.connect(self.on_state)
        self.worker.connected.connect(self.on_connected)
        self.worker.error.connect(self.on_error)
        self.worker.result.connect(self.on_result)
        self.thread.start()

        root = QWidget()
        self.setCentralWidget(root)
        layout = QVBoxLayout(root)
        title = QLabel("BCI / KALIBRACJA")
        title.setObjectName("heading")
        layout.addWidget(title)
        self.status = QLabel("Wybierz profil i połącz opaskę. Każdą z czterech kalibracji można wykonać osobno.")
        self.status.setWordWrap(True)
        layout.addWidget(self.status)

        form = QFormLayout()
        self.fake = QCheckBox("Syntetyczne EEG (bez opaski)")
        self.fake.setChecked(args.fake)
        self.no_record = getattr(args, "no_record", False)
        self.serial = QLineEdit(args.serial or "")
        self.serial.setPlaceholderText("numer seryjny Unicorna (puste = ostatnio używany / jedyny)")
        self.mains = QComboBox()
        for hz in (50, 60):
            self.mains.addItem(f"sieć {hz} Hz", hz)
        self.mains.setCurrentIndex(self.mains.findData(args.mains))
        self.connect_button = QPushButton("Połącz")
        self.connect_button.clicked.connect(self.toggle_connection)
        row = QHBoxLayout()
        row.addWidget(self.serial, 2)
        for widget in (self.mains, self.fake, self.connect_button):
            row.addWidget(widget)
        form.addRow("Unicorn przez g.Pype", row)

        self.subject = QLineEdit(args.subject)
        self.profile_path = QLineEdit(str(args.profile))
        choose = QPushButton("Wybierz plik…")
        choose.clicked.connect(self.choose_path)
        row = QHBoxLayout()
        row.addWidget(self.subject)
        row.addWidget(self.profile_path, 2)
        row.addWidget(choose)
        form.addRow("ID / profil JSON", row)

        self.artifact = QDoubleSpinBox()
        self.artifact.setRange(20, 2000)
        self.artifact.setValue(args.artifact_uv)
        self.artifact.setSuffix(" µV p-p")
        self.jaw_band = QLineEdit(args.jaw_band)
        row = QHBoxLayout()
        row.addWidget(self.artifact)
        row.addWidget(QLabel("Pasmo szczęki [Hz]:"))
        row.addWidget(self.jaw_band)
        form.addRow("Cechy", row)
        layout.addLayout(form)
        self.source_widgets = [self.fake, self.serial, self.mains, self.subject, self.profile_path,
                               choose, self.artifact, self.jaw_band]

        row = QHBoxLayout()
        self.audio_confirm = QCheckBox("Słyszę oba sygnały i znam ich znaczenie")
        self.audio_confirm.toggled.connect(self.update_buttons)
        self.sounds = Sounds(self)
        self.worker.cue.connect(self.sounds.play)
        self.test_audio = QPushButton("Sprawdź tony: zadanie / spoczynek")
        self.test_audio.clicked.connect(self.test_sound)
        row.addWidget(self.test_audio)
        row.addWidget(self.audio_confirm)
        layout.addLayout(row)

        body = QHBoxLayout()
        nav = QVBoxLayout()
        self.stack = QStackedWidget()
        self.views, self.nav_buttons = {}, []
        for index, name in enumerate(SIGNALS):
            button = QPushButton(VIEWS[name][0])
            button.setCheckable(True)
            button.clicked.connect(lambda checked=False, i=index: self.select_view(i))
            nav.addWidget(button)
            self.nav_buttons.append(button)
            view = CalibrationView(name)
            view.start.connect(self.begin)
            view.stop.connect(self.cancel_requested.emit)
            self.views[name] = view
            self.stack.addWidget(view)
        nav.addStretch()
        body.addLayout(nav)
        body.addWidget(self.stack, 1)
        layout.addLayout(body, 1)
        self.footer = QLabel("Profil nie zawiera mapowania na gry. Potok bci_pipeline.py wysyła te same sygnały do dowolnej gry.")
        self.footer.setWordWrap(True)
        self.footer.setTextInteractionFlags(Qt.TextInteractionFlag.TextSelectableByMouse)
        layout.addWidget(self.footer)
        self.select_view(0)
        self.update_buttons()
        self.setStyleSheet("""
            QWidget { background: #12212a; color: #e0ecef; font-size: 13px; }
            QLabel#heading { font-size: 25px; font-weight: 600; padding: 8px 0; }
            QLabel#prompt { background: #1e3940; color: #b9eed8; font-size: 22px; border-radius: 8px; padding: 12px; }
            QPushButton { background: #284650; border: 1px solid #42616a; border-radius: 5px; padding: 9px; }
            QPushButton:checked { background: #366b61; }
            QPushButton:disabled { color: #74838a; background: #1b2b33; }
            QLineEdit, QComboBox, QSpinBox, QDoubleSpinBox, QPlainTextEdit { background: #192f39; padding: 5px; }
            QProgressBar { border: 1px solid #42616a; text-align: center; min-height: 18px; }
            QProgressBar::chunk { background: #387965; }
        """)

    def test_sound(self):
        self.sounds.play("active")
        QTimer.singleShot(1000, lambda: self.sounds.play("rest"))

    def select_view(self, index):
        self.stack.setCurrentIndex(index)
        for i, button in enumerate(self.nav_buttons):
            button.setChecked(i == index)

    def choose_path(self):
        path, _ = QFileDialog.getSaveFileName(self, "Profil kalibracji (nowy lub istniejący)",
                                             self.profile_path.text(), "JSON (*.json)")
        if path:
            self.profile_path.setText(path)

    def toggle_connection(self):
        if self.connected_flag:
            self.disconnect_requested.emit()
            return
        try:
            band = [float(v) for v in self.jaw_band.text().split(",")]
            if len(band) != 2:
                raise ValueError("Pasmo szczęki wpisz jako dwie liczby: 55,95")
            self.connecting = True
            self.status.setText("Uruchamiam potok g.Pype i łączę z opaską…")
            self.update_buttons()
            self.connect_requested.emit({"fake": self.fake.isChecked(), "serial": self.serial.text().strip(),
                "mains_hz": self.mains.currentData(), "subject": self.subject.text().strip(),
                "path": self.profile_path.text(), "artifact_uv": self.artifact.value(), "jaw_band": band,
                "no_record": self.no_record})
        except ValueError as exc:
            self.on_error(str(exc))

    def on_connected(self, info):
        self.connected_flag = bool(info)
        self.recording = False
        if info:
            self.connecting = False
            cfg = info["config"]
            self.status.setText(f"{info['stream']['name']} · "
                                f"{cfg['sampling_rate']:g} Hz · EEG: {cfg['eeg_channels']} · "
                                f"C3={cfg['c3']}, C4={cfg['c4']} · tylne={cfg['posterior_channels']}")
            self.footer.setText(f"Wynik zapisuje się po każdej pełnej kalibracji: {info['path']}")
            self.saved_results.clear()
            for view in self.views.values():
                view.result_box.setPlainText(result_text(None))
                view.prompt.setText("Gotowy do kalibracji")
                view.progress.setValue(0)
        elif not self.connecting:
            self.status.setText("Rozłączono.")
        self.update_buttons()

    def begin(self, name, trials):
        self.recording = True
        self.views[name].result_box.setPlainText("Trwa zbieranie danych. Przerwanie nie nadpisze poprzednich parametrów.")
        self.update_buttons()
        self.start_requested.emit(name, trials)

    def on_state(self, state):
        session = state.get("session")
        self.recording = bool(session and session["phase"] not in ("complete", "cancelled"))
        if session:
            self.views[session["name"]].update_session(session)
            if session["phase"] == "cancelled":
                self.views[session["name"]].result_box.setPlainText(
                    "Przerwano. Nie zapisano nowych parametrów.\n\n" + result_text(self.saved_results.get(session["name"])))
        for name, view in self.views.items():
            result = state.get("preview", {}).get(name)
            if result:
                view.live.setText(f"Podgląd: {result['value']:.2f} · "
                                  f"{'AKTYWNY' if result['active'] else 'nieaktywny'} · "
                                  f"{result['reason'] or 'sygnał poprawny'}")
        if not state["fresh"] and session:
            self.views[session["name"]].quality.setText("Brak świeżych danych EEG — sprawdź połączenie.")
        diagnostics = state.get("diagnostics")
        if diagnostics:
            self.status.setText(f"g.Pype · ramki: {diagnostics['frames']} · "
                                f"resynchronizacje czasu: {diagnostics['timing_resets']} · "
                                f"{'dane aktualne' if state['fresh'] else 'BRAK DANYCH — sprawdź opaskę'}")
        self.update_buttons()

    def on_result(self, name, result):
        self.saved_results[name] = result
        self.views[name].result_box.setPlainText(result_text(result))

    def on_error(self, message):
        self.connecting = False
        self.recording = False
        self.status.setText("Błąd: " + message)
        self.update_buttons()

    def update_buttons(self):
        if not hasattr(self, "views"):
            return
        for view in self.views.values():
            view.start_button.setEnabled(self.connected_flag and not self.recording and self.audio_confirm.isChecked())
            view.stop_button.setEnabled(self.recording)
            view.trials.setEnabled(not self.recording)
        for button in self.nav_buttons:
            button.setEnabled(not self.recording)
        for widget in self.source_widgets:
            widget.setEnabled(not self.connected_flag and not self.connecting)
        self.connect_button.setText("Rozłącz" if self.connected_flag else "Połącz")
        self.connect_button.setEnabled(not self.recording and not self.connecting)
        self.test_audio.setEnabled(not self.recording)
        self.audio_confirm.setEnabled(not self.recording)

    def closeEvent(self, event):
        # Stop polling before quitting the worker event loop; never save a partial trial.
        from PySide6.QtCore import QMetaObject
        QMetaObject.invokeMethod(self.worker, "disconnect_source", Qt.ConnectionType.BlockingQueuedConnection)
        self.thread.quit()
        self.thread.wait()
        event.accept()


def parser():
    cli = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    cli.add_argument("--subject", default="participant", help="identyfikator osoby / sesji, bez danych osobowych")
    cli.add_argument("--profile", type=Path, default=None, help="domyślnie data/calibration/participant.json")
    cli.add_argument("--fake", "--demo", action="store_true", help="syntetyczne EEG zamiast opaski")
    cli.add_argument("--serial", help="numer seryjny Unicorna")
    cli.add_argument("--mains", type=int, choices=(50, 60), default=50, help="częstotliwość sieci do filtru")
    cli.add_argument("--artifact-uv", type=float, default=200)
    cli.add_argument("--jaw-band", default="55,95")
    cli.add_argument("--no-record", action="store_true",
                     help="nie zapisuj surowego EEG (domyślnie: data/raw/kalibracja_<osoba>_<czas>/)")
    return cli


def main():
    args = parser().parse_args()
    if args.profile is None:
        args.profile = Path(__file__).resolve().parents[1] / "data/calibration" / (
            "fake.json" if args.fake else "participant.json")
    app = QApplication(sys.argv[:1])
    window = CalibrationApp(args)
    window.show()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
