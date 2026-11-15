"""Szybkie detektory oczu i szczęki: decyzja co ramkę (16 ms), nie co 0,1 s z okna 2 s.

Skąd bierze się szybkość:

    oczy   zamknięcie i otwarcie powiek to duży, powolny skok potencjału na Fz (gałki oczne),
           widoczny w SUROWYM sygnale w ciągu 0,1-0,3 s. Filtr 1 Hz potoku go usuwa, dlatego
           detektor dostaje surowe Fz osobnym kanałem. Alfa nad potylicą narasta dopiero po
           0,5-1 s, więc tu tylko potwierdza zamknięcie i jest zapasową drogą otwarcia.
    szczęka  moc 55-95 Hz z okna 60 ms, mediana po kanałach (jedna zła elektroda jej nie rusza).

Progi dobiera tune_fast.py na nagraniu gry z klawiszami prawdy (spacja / F) i zapisuje w profilu
pod kluczem "fast". Bez tego klucza potok używa zwykłych detektorów z kalibracji.

Cechy (FastFeatures) i automaty decyzji (EyesLogic, JawLogic) są rozdzielone, żeby strojenie
liczyło cechy raz i przebiegało po nich wiele zestawów progów tym samym kodem co potok.
"""

from collections import deque

import numpy as np
from scipy.signal import butter, lfilter, sosfilt

FS = 250.0
WARMUP_S = 3.0          # rozruch filtrów i poziomów odniesienia: w tym czasie nic nie włączamy
FAST_LAG_S, SLOW_LAG_S = 0.15, 0.45

EYES_DEFAULT = {
    "c_up": 40.0,       # µV: skok Fz w górę w 0,15 s = powieki się zamknęły
    "a_on": 6.0,        # dB ponad spoczynek: zapasowe wejście samą alfą...
    "t_on": 0.6,        # ...utrzymaną tyle sekund
    "t_blink": 0.45,    # tyle po zamknięciu szybki skok w dół oznacza mrugnięcie, nie zamknięcie
    "c_blink": 30.0,
    "t_arm": 0.9,       # od tej chwili spadek Fz liczy się jako otwarcie oczu
    "c_dn": 45.0,       # µV: spadek Fz w 0,45 s = powieki się otworzyły
    "c_soft": 36.0,     # mniejszy spadek wystarcza, jeśli jednocześnie...
    "d_soft": 3.0,      # ...alfa spadła o tyle dB poniżej poziomu tego zamknięcia
    "a_conf": 3.0,      # dB: alfa tego zamknięcia uznana za widoczną
    "d": 5.0,           # dB: zapasowe otwarcie, gdy alfa spadnie o tyle poniżej swojego poziomu...
    "t_d": 0.4,         # ...na tyle sekund
    "lift_min": 15.0,   # µV: bez alfy zamknięcie trwa tylko, dopóki Fz zostaje tyle ponad poziomem sprzed skoku
    "t_check": 0.8,     # od kiedy i...
    "t_lift": 2.5,      # ...do kiedy to sprawdzamy (potem surowy sygnał dryfuje)
    "t_max": 8.0,       # zamknięcie bez widocznej alfy kończy się najpóźniej po tylu sekundach
}
JAW_DEFAULT = {"on_db": 8.0, "off_db": 4.0, "t_on": 0.05, "t_off": 0.1}
HOLD_DB, HOLD_S = 8.0, 0.3   # zaciśnięta szczęka zalewa pasmo alfa: stan oczu wtedy zamrażamy


def _ema(tau):
    alpha = 1.0 - np.exp(-1.0 / (tau * FS))
    return [alpha], [1.0, alpha - 1.0]


class _Quantile:
    """Bieżący kwantyl sygnału w dB: poziom spoczynkowy, którego krótkie zdarzenia nie ruszają."""

    def __init__(self, q, rate_db_s):
        self.q, self.rate, self.value = q, rate_db_s, None

    def update(self, x, dt):
        if self.value is None:
            self.value = x
        self.value += self.rate * dt * (self.q if x > self.value else self.q - 1.0)
        return self.value


class FastFeatures:
    """Przyczynowe cechy liczone próbka po próbce; push() zwraca wiersz dla ostatniej próbki ramki.

    Wiersz: (alfa szybka, alfa wolna, poziom spoczynkowy alfy, skok Fz 0,15 s, skok Fz 0,45 s,
             EMG ponad spoczynek, rozgrzany, poziom Fz) w dB / µV.
    """

    def __init__(self, posterior, channels=8):
        self.posterior = list(posterior)
        self._alpha = butter(2, [8, 13], btype="band", fs=FS, output="sos")
        self._emg = butter(4, [55, 95], btype="band", fs=FS, output="sos")
        self._lp6 = butter(2, 6, fs=FS, output="sos")
        self._lp3 = butter(2, 3, fs=FS, output="sos")
        self._channels = channels
        self.reset()

    def reset(self):
        zeros = lambda sos, n: np.zeros((len(sos), 2, n))  # noqa: E731
        self._z_alpha = zeros(self._alpha, len(self.posterior))
        self._z_emg = zeros(self._emg, self._channels)
        self._z_lp6, self._z_lp3 = zeros(self._lp6, 1), zeros(self._lp3, 1)
        self._e_fast = self._e_slow = None
        self._e_emg = None
        self._fz0 = None
        self._buf6 = deque(maxlen=int(FAST_LAG_S * FS) + 1)
        self._buf3 = deque(maxlen=int(SLOW_LAG_S * FS) + 1)
        self._alpha_base, self._emg_base = _Quantile(0.15, 1.5), _Quantile(0.2, 1.0)
        self._seen = 0

    def push(self, eeg, raw_fz):
        """eeg: ramka po filtrach potoku (próbki x kanały, µV); raw_fz: surowe Fz tej samej ramki."""
        eeg = np.asarray(eeg, float)
        n = len(eeg)
        dt = n / FS
        self._seen += n

        alpha, self._z_alpha = sosfilt(self._alpha, eeg[:, self.posterior], axis=0, zi=self._z_alpha)
        power = np.mean(alpha ** 2, axis=1)
        if self._e_fast is None:
            self._e_fast, self._e_slow = np.array([power[0]]), np.array([power[0]])
        fast, self._e_fast = lfilter(*_ema(0.2), power, zi=self._e_fast * (1 - _ema(0.2)[0][0]))
        slow, self._e_slow = lfilter(*_ema(0.6), power, zi=self._e_slow * (1 - _ema(0.6)[0][0]))
        self._e_fast, self._e_slow = np.array([fast[-1]]), np.array([slow[-1]])
        a_fast, a_slow = 10 * np.log10(fast[-1] + 1e-9), 10 * np.log10(slow[-1] + 1e-9)

        emg, self._z_emg = sosfilt(self._emg, eeg[:, :self._channels], axis=0, zi=self._z_emg)
        if self._e_emg is None:
            self._e_emg = emg[0] ** 2
        k = _ema(0.06)[0][0]
        for row in emg ** 2:
            self._e_emg = self._e_emg + k * (row - self._e_emg)
        emg_db = 10 * np.log10(np.median(self._e_emg) + 1e-9)

        raw_fz = np.asarray(raw_fz, float).reshape(-1, 1)
        if self._fz0 is None:
            self._fz0 = raw_fz[0, 0]          # surowy sygnał ma duże przesunięcie stałe
        z6, self._z_lp6 = sosfilt(self._lp6, raw_fz - self._fz0, axis=0, zi=self._z_lp6)
        z3, self._z_lp3 = sosfilt(self._lp3, raw_fz - self._fz0, axis=0, zi=self._z_lp3)
        self._buf6.extend(z6[:, 0])
        self._buf3.extend(z3[:, 0])
        dz_fast, dz_slow = self._buf6[-1] - self._buf6[0], self._buf3[-1] - self._buf3[0]

        warm = self._seen >= WARMUP_S * FS
        if self._seen >= FS:                  # poziomy odniesienia ruszają po rozbiegu filtrów
            base = self._alpha_base.update(a_slow, dt)
            emg_rel = emg_db - self._emg_base.update(emg_db, dt)
        else:
            base, emg_rel = a_slow, 0.0
        return a_fast, a_slow, base, dz_fast, dz_slow, emg_rel, warm, self._buf6[-1]


class JawLogic:
    def __init__(self, params=None):
        self.p = {**JAW_DEFAULT, **(params or {})}
        self.active, self._for = False, 0.0

    def step(self, row, dt):
        emg_rel, warm = row[5], row[6]
        if not warm:
            self.active, self._for = False, 0.0
            return False
        want = emg_rel < self.p["off_db"] if self.active else emg_rel > self.p["on_db"]
        self._for = self._for + dt if want else 0.0
        if self._for >= (self.p["t_off"] if self.active else self.p["t_on"]):
            self.active, self._for = not self.active, 0.0
        return self.active


class EyesLogic:
    """Automat: OTWARTE -> ZAMKNIĘTE na skok Fz w górę (albo trwałą alfę), z powrotem na spadek
    Fz (albo utratę alfy). Mrugnięcie zamyka na ułamek sekundy; gra i tak pomija zamknięcia < 0,5 s.

    Zamknięcie zaczęte skokiem musi się potwierdzić: alfą albo tym, że Fz zostaje podniesione.
    Inaczej było to spojrzenie w górę albo mrugnięcie i stan wraca do OTWARTE.
    """

    def __init__(self, params=None):
        self.p = {**EYES_DEFAULT, **(params or {})}
        self.closed = False
        self._age = self._alpha_for = self._drop_for = self._hold = 0.0
        self._level, self._confirmed, self._before = 0.0, False, None

    def step(self, row, dt):
        a_fast, a_slow, base, dz_fast, dz_slow, emg_rel, warm, fz = row
        p = self.p
        if not warm:
            self.closed = False
            return False
        self._hold = HOLD_S if emg_rel > HOLD_DB else max(0.0, self._hold - dt)
        if self._hold > 0:
            return self.closed                # szczęka: bez zmiany stanu
        alpha = a_slow - base
        if not self.closed:
            self._alpha_for = self._alpha_for + dt if alpha > p["a_on"] else 0.0
            by_lid = dz_fast > p["c_up"]
            if by_lid or self._alpha_for >= p["t_on"]:
                self.closed, self._age, self._alpha_for, self._drop_for = True, 0.0, 0.0, 0.0
                self._level, self._confirmed = alpha, not by_lid
                self._before = fz - dz_fast if by_lid else None   # poziom Fz sprzed skoku
            return self.closed

        self._age += dt
        if self._age < p["t_blink"]:
            if dz_fast < -p["c_blink"]:
                self.closed = False
            return self.closed
        if self._age > 0.8:                   # poziom alfy tego zamknięcia: szybko w górę, bardzo wolno w dół
            self._level += (alpha - self._level) * 2.0 * dt if alpha > self._level else -0.3 * dt
            self._confirmed = self._confirmed or self._level > p["a_conf"]
        fallen = a_fast - base < self._level - p["d_soft"]
        if self._age >= p["t_arm"] and (dz_slow < -p["c_dn"] or (self._confirmed and fallen and dz_slow < -p["c_soft"])):
            self.closed = False
        elif self._confirmed:
            self._drop_for = self._drop_for + dt if alpha < self._level - p["d"] else 0.0
            if self._drop_for >= p["t_d"]:
                self.closed = False
        elif self._age > p["t_max"]:
            self.closed = False
        elif self._before is not None and p["t_check"] <= self._age <= p["t_lift"] and fz - self._before < p["lift_min"]:
            self.closed = False               # powieki nie zostały zamknięte
        return self.closed


class FastDetectors:
    """Cechy i oba automaty razem, tak jak używa ich potok. params: zawartość profile["fast"]."""

    def __init__(self, posterior, params):
        self.features = FastFeatures(posterior)
        self.eyes = EyesLogic(params["eyes"]) if "eyes" in params else None
        self.jaw = JawLogic(params["jaw"]) if "jaw" in params else None
        self.row = None

    def reset(self):
        self.features.reset()
        for logic in (self.eyes, self.jaw):
            if logic:
                logic.__init__(logic.p)

    def push(self, eeg, raw_fz):
        """-> (oczy zamknięte albo None, szczęka zaciśnięta albo None, rozgrzany)."""
        self.row = self.features.push(eeg, raw_fz)
        dt = len(eeg) / FS
        return (self.eyes.step(self.row, dt) if self.eyes else None,
                self.jaw.step(self.row, dt) if self.jaw else None, bool(self.row[6]))
