"""Most między Unicornem a grą: EEG z LSL -> sygnały przez UDP, znaczniki z gry -> LSL.

    python lsl_bridge.py --list                    # strumienie LSL widoczne w sieci
    python lsl_bridge.py                           # pierwszy strumień typu EEG, gra na tym komputerze
    python lsl_bridge.py --name UN-2023.xx.xx      # konkretny strumień
    python lsl_bridge.py --game-host 192.168.1.20  # gra na innym komputerze

Most nie wie nic o grach: wysyła nazwane sygnały, a każda gra sama wybiera, których słucha.

    focus       0..1   zaangażowanie beta / (alpha + theta) (Pope i in., 1995), okno 2 s,
                       względem ostatniej minuty: 0.5 to przeciętny poziom tej osoby
    laterality  -1..1  różnica mocy 8-30 Hz między C4 a C3, okno 1 s, względem ostatniej minuty.
                       Ruch (lub wyobrażenie ruchu) prawej ręki tłumi rytm nad C3, więc wartość
                       rośnie; lewej ręki - maleje
    jaw         0..1   zaciśnięcie szczęki: moc powyżej 55 Hz (mięśnie) ze wszystkich kanałów,
                       okno 0.25 s, względem spoczynku

To heurystyki do sterowania i neurofeedbacku, nie zwalidowane miary. Progi i wzmocnienia
trzeba dobrać na prawdziwym sygnale.

Znaczniki z gry (UDP 5006) trafiają do strumienia LSL "OgrodMarkers", więc LabRecorder
nagra je razem z EEG. Nie uruchamiaj jednocześnie marker_logger.py (ten sam port).
Format wiadomości: docs/protocol.md.
"""

import argparse
import json
import math
import socket
import sys
import threading
import time
from collections import deque

import numpy as np
from pylsl import IRREGULAR_RATE, StreamInfo, StreamInlet, StreamOutlet, local_clock, resolve_byprop, resolve_streams
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
        if len(chunk) >= self.n:
            self.buffer = chunk[-self.n:].copy()
        else:
            self.buffer = np.roll(self.buffer, -len(chunk), axis=0)
            self.buffer[-len(chunk):] = chunk
        self.filled = min(self.n, self.filled + len(chunk))

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


def channel_labels(info):
    labels = []
    channel = info.desc().child("channels").child("channel")
    while not channel.empty():
        labels.append(channel.child_value("label"))
        channel = channel.next_sibling()
    if len(labels) != info.channel_count():
        labels = [f"ch{i}" for i in range(info.channel_count())]
    return labels


def list_streams():
    streams = resolve_streams(2.0)
    if not streams:
        print("Nie widzę żadnych strumieni LSL. Czy Unicorn Suite (LSL) działa i komputery są w tej samej sieci?")
    for s in streams:
        print(f"{s.name()!r:24} typ={s.type()!r:10} kanały={s.channel_count():<3} "
              f"{s.nominal_srate():g} Hz  host={s.hostname()}  id={s.source_id()!r}")


def find_stream(name, stream_type, timeout):
    prop, value = ("name", name) if name else ("type", stream_type)
    print(f"Szukam strumienia LSL ({prop}={value!r})...")
    found = resolve_byprop(prop, value, minimum=1, timeout=timeout)
    if not found:
        print("Nie znalazłem. Widoczne strumienie:")
        list_streams()
        sys.exit(1)
    if len(found) > 1:
        print(f"Pasuje {len(found)} strumieni, biorę pierwszy. Wskaż konkretny przez --name.")
    return found[0]


def send(sock, addr, name, value):
    msg = {"type": "metric", "name": name, "value": round(value, 3), "t": time.time()}
    sock.sendto(json.dumps(msg).encode("utf-8"), addr)


def run_markers(port, outlet):
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("0.0.0.0", port))
    warned = False
    while True:
        data, _ = sock.recvfrom(4096)
        try:
            msg = json.loads(data)
            name, value, t = msg["name"], float(msg.get("value", 0)), float(msg["t"])
        except (ValueError, KeyError, TypeError):
            continue
        # czas zdarzenia w grze przeliczony na zegar LSL; gdy gra działa na innym komputerze,
        # zegary się różnią i zostaje czas odbioru (dokładność rzędu opóźnienia sieci)
        age = time.time() - t
        if abs(age) > 1.0:
            if not warned:
                print(f"Zegar gry różni się o {age:.1f} s, znaczniki dostają czas odbioru.")
                warned = True
            age = 0.0
        outlet.push_sample([f"{name}:{value:g}"], local_clock() - age)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--list", action="store_true", help="pokaż strumienie LSL i zakończ")
    parser.add_argument("--name", help="nazwa strumienia EEG (domyślnie pierwszy o typie --type)")
    parser.add_argument("--type", default="EEG", help="typ strumienia EEG (domyślnie EEG)")
    parser.add_argument("--channels", default="0,1,2,3,4",
                        help="indeksy kanałów do focus (domyślnie 0-4: Fz, C3, Cz, C4, Pz w montażu Unicorna)")
    parser.add_argument("--c3", type=int, default=1, help="indeks kanału C3 do laterality (domyślnie 1)")
    parser.add_argument("--c4", type=int, default=3, help="indeks kanału C4 do laterality (domyślnie 3)")
    parser.add_argument("--jaw-channels", default="0,1,2,3,4,5,6,7", help="indeksy kanałów EEG do jaw (domyślnie 0-7)")
    parser.add_argument("--jaw-band", default="55,95",
                        help="pasmo mięśniowe w Hz (domyślnie 55,95; jeśli strumień jest filtrowany do 50 Hz, np. 30,45)")
    parser.add_argument("--artifact-uv", type=float, default=200.0,
                        help="próg międzyszczytowy w µV, powyżej okno jest pomijane")
    parser.add_argument("--game-host", default="127.0.0.1")
    parser.add_argument("--game-port", type=int, default=5005)
    parser.add_argument("--marker-port", type=int, default=5006)
    parser.add_argument("--no-markers", action="store_true", help="nie publikuj znaczników z gry w LSL")
    args = parser.parse_args()

    if args.list:
        list_streams()
        return

    info = find_stream(args.name, args.type, timeout=10)
    inlet = StreamInlet(info, max_buflen=10)
    full = inlet.info()
    srate = full.nominal_srate()
    if srate <= 0:
        sys.exit(f"Strumień {full.name()!r} nie ma stałej częstotliwości próbkowania, to nie wygląda na EEG.")
    channels = [int(c) for c in args.channels.split(",")]
    jaw_channels = [int(c) for c in args.jaw_channels.split(",")]
    jaw_band = [float(f) for f in args.jaw_band.split(",")]
    highest = max(channels + jaw_channels + [args.c3, args.c4])
    if highest >= full.channel_count():
        sys.exit(f"Strumień ma {full.channel_count()} kanałów, a wskazane indeksy sięgają do {highest}.")
    if jaw_band[1] >= srate / 2:
        sys.exit(f"Pasmo --jaw-band sięga {jaw_band[1]:g} Hz, a przy {srate:g} Hz próbkowania widać najwyżej {srate / 2:g} Hz.")
    labels = channel_labels(full)
    print(f"EEG: {full.name()!r} z {full.hostname()}, {full.channel_count()} kanałów, {srate:g} Hz")
    print(f"focus z kanałów: {', '.join(labels[c] for c in channels)}")
    print(f"laterality: {labels[args.c4]} minus {labels[args.c3]}")
    print(f"jaw: {jaw_band[0]:g}-{jaw_band[1]:g} Hz z kanałów: {', '.join(labels[c] for c in jaw_channels)}")

    if not args.no_markers:
        marker_info = StreamInfo("OgrodMarkers", "Markers", 1, IRREGULAR_RATE, "string", "ogrod-markers")
        outlet = StreamOutlet(marker_info)
        threading.Thread(target=run_markers, args=(args.marker_port, outlet), daemon=True).start()
        print(f"Znaczniki: UDP {args.marker_port} -> LSL 'OgrodMarkers'")

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    game = (args.game_host, args.game_port)
    print(f"Sygnały -> {args.game_host}:{args.game_port} co {1000 // RATE_HZ} ms (Ctrl+C kończy)")

    window = Window(srate, full.channel_count())
    focus = FocusMeter(channels, args.artifact_uv)
    meters = [focus, LateralityMeter(args.c3, args.c4, args.artifact_uv), JawMeter(jaw_channels, jaw_band)]
    latest = {}
    next_tick = next_report = time.monotonic()
    last_data = time.monotonic()
    try:
        while True:
            chunk, _ = inlet.pull_chunk(timeout=0.0, max_samples=4096)
            now = time.monotonic()
            if chunk:
                window.push(np.asarray(chunk, dtype=float))
                last_data = now
            if now >= next_tick:
                next_tick = max(next_tick + 1 / RATE_HZ, now)
                if now - last_data < 0.5:   # bez świeżych danych nie wysyłamy starych wartości
                    for meter in meters:
                        value = meter.compute(window)
                        if value is not None:
                            latest[meter.name] = value
                            send(sock, game, meter.name, value)
            if now >= next_report:
                next_report = now + 1.0
                if now - last_data > 2.0:
                    print(f"Brak danych EEG od {now - last_data:.0f} s, czekam...")
                elif latest:
                    print("  ".join(f"{name} {value:+.2f}" for name, value in latest.items())
                          + f"  artefakty {focus.artifacts}")
            time.sleep(0.01)
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
