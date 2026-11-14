"""Most EEG -> generyczne sygnały UDP dla Unity.

Nowe wejście: bci_bridge.py (g.Pype UDPSender). lsl_bridge.py zachowuje domyślny LSL.
Obie nazwy obsługują --source gpype / lsl i ten sam profil / kod przetwarzania.

    python lsl_bridge.py --list                    # strumienie LSL widoczne w sieci
    python lsl_bridge.py                           # pierwszy strumień typu EEG, gra na tym komputerze
    python lsl_bridge.py --name UN-2023.xx.xx      # konkretny strumień
    python lsl_bridge.py --game-host 192.168.1.20  # gra na innym komputerze
    python lsl_bridge.py --profile ../data/calibration/participant.json

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

Z --profile: eyes_closed, right_hand, left_hand, jaw, laterality i flagi *_valid / *_active.
Brak gotowej kalibracji nie uruchamia zastępczej heurystyki. focus pozostaje heurystyką.

Znaczniki z gry (UDP 5006) trafiają do JSONL dla g.Pype lub do LSL "BciMarkers"
dla wejścia LSL. Nie uruchamiaj jednocześnie marker_logger.py (ten sam port).
Format wiadomości: docs/protocol.md.
"""

import argparse
import json
import math
import socket
import sys
import threading
import time
from pathlib import Path

import numpy as np
from processing import FocusMeter, JawMeter, LateralityMeter, Window, RATE_HZ
from profiles import ProfileEngine, assert_profile_source, load_profile
from gpype_source import GPypeUDPSource, add_udp_arguments


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
    from pylsl import resolve_streams
    streams = resolve_streams(2.0)
    if not streams:
        print("Nie widzę żadnych strumieni LSL. Czy Unicorn Suite (LSL) działa i komputery są w tej samej sieci?")
    for s in streams:
        print(f"{s.name()!r:24} typ={s.type()!r:10} kanały={s.channel_count():<3} "
              f"{s.nominal_srate():g} Hz  host={s.hostname()}  id={s.source_id()!r}")


def find_stream(name, stream_type, timeout):
    from pylsl import resolve_byprop
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


def send(sock, addr, name, value, kind="metric"):
    if not math.isfinite(value):
        return
    msg = {"type": kind, "name": name, "value": round(value, 3), "t": time.time()}
    sock.sendto(json.dumps(msg, allow_nan=False).encode("utf-8"), addr)


def run_markers(sock, outlet, clock=time.monotonic):
    warned = False
    while True:
        try:
            data, _ = sock.recvfrom(4096)
        except OSError:
            if hasattr(outlet, "close"):
                outlet.close()
            return
        try:
            msg = json.loads(data)
            name, value, t = msg["name"], float(msg.get("value", 0)), float(msg["t"])
            if msg.get("type") != "marker" or not isinstance(name, str) or not all(map(math.isfinite, (value, t))):
                continue
        except (ValueError, KeyError, TypeError):
            continue
        # czas zdarzenia w grze przeliczony na zegar wyjścia; gdy gra działa na innym komputerze,
        # zegary się różnią i zostaje czas odbioru (dokładność rzędu opóźnienia sieci)
        age = time.time() - t
        if abs(age) > 1.0:
            if not warned:
                print(f"Zegar gry różni się o {age:.1f} s, znaczniki dostają czas odbioru.")
                warned = True
            age = 0.0
        outlet.push_sample([f"{name}:{value:g}"], clock() - age)


class MarkerFileOutlet:
    def __init__(self, path):
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        self.file = path.open("a", encoding="utf-8")

    def push_sample(self, sample, timestamp):
        self.file.write(json.dumps({"marker": sample[0], "t_monotonic_estimate": timestamp,
                                    "t_received_unix": time.time()}, ensure_ascii=False) + "\n")
        self.file.flush()

    def close(self):
        self.file.close()


def start_markers(args):
    if args.no_markers:
        return None
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock.bind(("0.0.0.0", args.marker_port))
        mode = args.marker_output if args.marker_output != "auto" else ("jsonl" if args.source == "gpype" else "lsl")
        clock = time.monotonic
        if mode == "lsl":
            from pylsl import IRREGULAR_RATE, StreamInfo, StreamOutlet, local_clock
            info = StreamInfo(args.marker_name, "Markers", 1, IRREGULAR_RATE, "string", "bci-markers")
            outlet = StreamOutlet(info)
            clock = local_clock
            destination = f"LSL {args.marker_name!r}"
        else:
            path = args.marker_file or (Path(__file__).resolve().parents[1] / "data/raw" /
                                        time.strftime("bci_markers_%Y%m%d_%H%M%S.jsonl"))
            outlet = MarkerFileOutlet(path)
            destination = str(path)
    except Exception:
        sock.close()
        raise
    threading.Thread(target=run_markers, args=(sock, outlet, clock), daemon=True).start()
    print(f"Znaczniki: UDP {args.marker_port} -> {destination}")
    return sock


def run_calibrated(args):
    profile = load_profile(args.profile)
    engine = ProfileEngine(profile)
    config = engine.config
    if args.source == "gpype":
        saved = profile["stream"].get("input", {})
        options = {"host": "127.0.0.1", "port": 56000, "sampling_rate": config.sampling_rate,
                   "labels": config.channel_labels, "units": "uv" if config.input_scale_uv == 1 else "v",
                   "byte_order": "little", "frame_samples": 4}
        options.update(saved)
        for key, value in (("host", args.udp_host), ("port", args.udp_port), ("sampling_rate", args.sampling_rate),
                           ("labels", args.channel_labels), ("units", args.units),
                           ("byte_order", args.byte_order), ("frame_samples", args.frame_samples)):
            if value is not None:
                options[key] = value
        source = GPypeUDPSource(**options, artifact_uv=config.artifact_uv, jaw_band=config.jaw_band,
                               simulated=args.synthetic_input)
    else:
        from lsl_source import LSLSource
        source = LSLSource(args.name, args.type,
                           units=args.units or profile["stream"].get("units_option", "auto"),
                           assume_unicorn_order=args.assume_unicorn_order or profile["stream"].get("assumed_unicorn_order", False),
                           artifact_uv=config.artifact_uv, jaw_band=config.jaw_band)
    marker_socket = None
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        assert_profile_source(profile, source, allow_simulated=args.allow_simulated)
        marker_socket = start_markers(args)
        targets = [(args.game_host, args.game_port)]
        for destination in args.target:
            host, port = destination.rsplit(":", 1)
            target = (host, int(port))
            if not 1 <= target[1] <= 65535:
                raise ValueError("Port odbiorcy poza zakresem.")
            if target not in targets:
                targets.append(target)
        print(f"Profil: {profile['subject']} · {args.profile}")
        print(f"Gotowe detektory: {', '.join(engine.detectors) or 'BRAK — wykonaj kalibrację'}")
        print(f"EEG: {source.stream['name']} · {config.sampling_rate:g} Hz -> {targets}")
        print("focus = nieskalibrowana heurystyka. Wyniki 0–1 nie są prawdopodobieństwami.")
        if args.source == "gpype":
            print("g.Pype UDP: czas próbek jest szacowany po stronie odbiornika; pakiety nie zawierają czasu akwizycji.")
        window = Window(config.sampling_rate, config.channel_count)
        focus = FocusMeter(config.eeg_channels[:5], config.artifact_uv)
        next_tick = next_report = time.monotonic()
        commands_at = {"right_hand": 0.0, "left_hand": 0.0}
        while True:
            samples, timestamps = source.read()
            now = time.monotonic()
            if source.gap:
                window.clear()
                for detector in engine.detectors.values():
                    detector.reset()
            if len(timestamps):
                window.push(samples)
            fresh = source.fresh()
            if not fresh:
                window.clear()
            if now >= next_tick:
                next_tick = now + 1 / RATE_HZ
                results = engine.update(window, now, fresh=fresh)
                for name, item in results.items():
                    for target in targets:
                        send(sock, target, name, item["value"])
                        send(sock, target, name + "_valid", float(item["valid"]))
                        if name != "laterality":
                            send(sock, target, name + "_active", float(item["active"]))
                x = window.last(2.0, config.eeg_channels[:5])
                focus_valid = (fresh and x is not None and np.isfinite(x).all()
                               and np.all(np.ptp(x, axis=0) > 0.01) and not results["jaw"]["active"])
                value = focus.compute(window) if focus_valid else None
                for target in targets:
                    send(sock, target, "focus", value if value is not None else 0.0)
                    send(sock, target, "focus_valid", float(value is not None))
                # Legacy semantic direction commands keep existing Unity receivers compatible.
                # No mapping to select/cut/steer or other game actions lives in the bridge.
                if not args.no_commands:
                    for name, command in (("right_hand", "right"), ("left_hand", "left")):
                        if results[name]["active"] and now >= commands_at[name]:
                            for target in targets:
                                send(sock, target, command, results[name]["value"], "command")
                            commands_at[name] = now + 0.5
            if now >= next_report:
                next_report = now + 1
                print("  ".join(f"{name}={item['value']:.2f}{'*' if item['active'] else ''}"
                                f"{' (brak)' if not item['valid'] else ''}" for name, item in results.items()))
                if hasattr(source, "diagnostics"):
                    print(f"UDP: {source.diagnostics()}")
            time.sleep(0.005)
    except KeyboardInterrupt:
        pass
    finally:
        source.close()
        sock.close()
        if marker_socket:
            marker_socket.close()


def main(default_source="lsl"):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--list", action="store_true", help="pokaż strumienie LSL i zakończ")
    parser.add_argument("--source", choices=("gpype", "lsl"), default=default_source)
    add_udp_arguments(parser, defaults=False)
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
    parser.add_argument("--game-host", "--host", default="127.0.0.1")
    parser.add_argument("--game-port", "--port", type=int, default=5005)
    parser.add_argument("--profile", help="profil JSON z calibrate.py; montaż i cechy są wczytywane z profilu")
    parser.add_argument("--units", choices=("auto", "uv", "v"), help="jednostki EEG; UDP wymaga uv albo v")
    parser.add_argument("--assume-unicorn-order", action="store_true", help="potwierdź standardowy montaż przy braku etykiet LSL")
    parser.add_argument("--allow-simulated", action="store_true", help="profil DEMO: dla UDP wymaga też --synthetic-input")
    parser.add_argument("--target", action="append", default=[], help="dodatkowy odbiorca HOST:PORT (z --profile)")
    parser.add_argument("--no-commands", action="store_true", help="tylko metryki; bez komend left/right z profilu")
    parser.add_argument("--marker-port", type=int, default=5006)
    parser.add_argument("--no-markers", action="store_true", help="wyłącz odbiornik znaczników gry (UDP 5006)")
    parser.add_argument("--marker-name", default="BciMarkers", help="nazwa strumienia znaczników, np. OgrodMarkers dla starych nagrań")
    parser.add_argument("--marker-output", choices=("auto", "jsonl", "lsl"), default="auto",
                        help="auto: JSONL dla g.Pype, LSL dla starego wejścia LSL")
    parser.add_argument("--marker-file", help="plik znaczników JSONL (domyślnie data/raw/bci_markers_*.jsonl)")
    args = parser.parse_args()

    if args.list:
        list_streams()
        return

    if args.profile:
        try:
            run_calibrated(args)
        except (ValueError, KeyError, TypeError, OSError, RuntimeError, ImportError) as exc:
            sys.exit(f"Nie można uruchomić profilu: {exc}")
        return
    if args.source == "gpype":
        parser.error("Wejście g.Pype wymaga --profile. Najpierw wykonaj kalibrację: python bci/calibrate.py")
    if args.target or args.units or args.assume_unicorn_order or args.allow_simulated:
        parser.error("--target, --units, --assume-unicorn-order i --allow-simulated wymagają --profile")
    print("Tryb bez profilu: nieskalibrowane heurystyki. Kalibracja: python bci/calibrate.py")

    from pylsl import StreamInlet
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

    marker_socket = start_markers(args)

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
    finally:
        inlet.close_stream()
        sock.close()
        if marker_socket:
            marker_socket.close()


if __name__ == "__main__":
    main()
