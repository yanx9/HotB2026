"""Tor gry: Unicorn -> g.Pype -> filtrowanie / klasyfikacja -> UDP JSON -> Unity.

    python bci/bci_pipeline.py --profile data/calibration/participant.json
    python bci/bci_pipeline.py                       # bez profilu: nieskalibrowane heurystyki
    python bci/bci_pipeline.py --fake --no-gui       # bez opaski: syntetyczne EEG

Wszystko działa w jednym procesie g.Pype: HybridBlack -> Highpass -> Bandstop ->
SignalClassifier -> UdpJsonSender. Potok nie wie nic o grach: wysyła nazwane sygnały,
a każda gra sama wybiera, których słucha (docs/protocol.md).

Znaczniki z gier (UDP 5006) trafiają do data/raw/bci_markers_*.jsonl, a przy nagrywaniu
(domyślnie) także na kanał znaczników w data/raw/<sesja>_<czas>/eeg_*.h5 (recording.py).
Profil powstaje w kalibratorze: python bci/calibrate.py (docs/calibration.md).
"""

import argparse
import json
import math
import socket
import sys
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def parse_target(text):
    host, _, port = text.rpartition(":")
    if not host or not port.isdigit() or not 1 <= int(port) <= 65535:
        raise argparse.ArgumentTypeError("odbiorca w postaci HOST:PORT")
    return host, int(port)


def log_markers(sock, path, recorder=None):
    """Znaczniki gier -> JSONL i (z recorder) na próbkę EEG z chwili odbioru.

    Czas gry zapisujemy obok, ale o pozycji w nagraniu decyduje chwila odbioru: zegar gry
    może być na innym komputerze. Na jednym komputerze UDP dokłada < 1 ms."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as file:
        while True:
            try:
                data, _ = sock.recvfrom(4096)
            except OSError:
                return
            try:
                message = json.loads(data)
                name, value, t = message["name"], float(message.get("value", 0)), float(message["t"])
                if message.get("type") != "marker" or not isinstance(name, str) or not all(map(math.isfinite, (value, t))):
                    continue
            except (ValueError, KeyError, TypeError):
                continue
            if recorder:
                recorder.mark(name, value, t)
            file.write(json.dumps({"marker": name, "value": value, "t_game_unix": t,
                                   "t_received_unix": time.time()}, ensure_ascii=False) + "\n")
            file.flush()


def start_markers(port, path, recorder=None):
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock.bind(("0.0.0.0", port))
    except OSError as exc:
        sock.close()
        raise OSError(f"Port znaczników {port} jest zajęty ({exc}). Zamknij drugi potok / marker_logger.py "
                      "albo dodaj --no-markers.") from exc
    threading.Thread(target=log_markers, args=(sock, path, recorder), daemon=True).start()
    print(f"Znaczniki gier: UDP {port} -> {path}")
    return sock


def report(classifier, sender, every=2.0):
    """Co kilka sekund stan klasyfikatora w konsoli, także przy otwartym oknie g.Pype."""
    count, last = classifier._count, time.monotonic()
    best = None   # najmniejsze (zegar - próbki/fs): chwila, w której potok był najbardziej na bieżąco
    while True:
        time.sleep(every)
        now = time.monotonic()
        rate = (classifier._count - count) / (now - last)
        count, last = classifier._count, now
        # Zaległość: o ile sekund przetwarzanie jest dalej za opaską niż w najlepszej chwili.
        # Rośnie, gdy dane z Bluetooth / potok nie nadążają; wtedy gra reaguje z opóźnieniem.
        offset = now - count / 250
        best = offset if best is None else min(best, offset)
        values = dict(zip(classifier.names, classifier._values))
        shown = "  ".join(f"{name}={value:.2f}" for name, value in values.items()
                          if not name.endswith(("_valid", "_active")))
        print(f"{shown}  · wysłano {sender.sent}\n  EEG {rate:.0f} Hz · zaległość {offset - best:.2f} s · "
              f"{classifier.diagnose()}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--profile", help="profil JSON z calibrate.py; bez niego działają nieskalibrowane heurystyki")
    parser.add_argument("--serial", help="numer seryjny Unicorna (domyślnie ostatnio używany albo jedyny widoczny)")
    parser.add_argument("--fake", action="store_true", help="syntetyczne EEG zamiast opaski")
    parser.add_argument("--allow-simulated", action="store_true", help="pozwól użyć profilu syntetycznego (tylko z --fake)")
    parser.add_argument("--mains", type=int, choices=(50, 60), help="częstotliwość sieci do filtru (domyślnie z profilu albo 50)")
    parser.add_argument("--host", "--game-host", default="127.0.0.1", help="komputer z grą")
    parser.add_argument("--port", "--game-port", type=int, default=5005)
    parser.add_argument("--target", action="append", type=parse_target, default=[], help="dodatkowy odbiorca HOST:PORT")
    parser.add_argument("--no-commands", action="store_true", help="tylko metryki; bez komend left / right")
    parser.add_argument("--marker-port", type=int, default=5006)
    parser.add_argument("--marker-file", type=Path, help="domyślnie data/raw/bci_markers_<data>.jsonl")
    parser.add_argument("--no-markers", action="store_true", help="nie zajmuj portu znaczników")
    parser.add_argument("--no-gui", action="store_true", help="bez okna podglądu g.Pype")
    parser.add_argument("--session", default="gra", help="nazwa sesji w nazwie folderu nagrania, np. mrok_s01")
    parser.add_argument("--no-record", action="store_true", help="nie zapisuj EEG (domyślnie data/raw/<sesja>_<czas>/)")
    args = parser.parse_args()

    try:
        import gpype as gp
        from gpype_nodes import SignalClassifier, UdpJsonSender, build_front, stream_info
        from profiles import assert_profile_source, load_profile
    except ImportError as exc:
        sys.exit(f"Brak zależności: {exc}\nZainstaluj: pip install -r bci/requirements.txt")

    profile = None
    mains = args.mains or 50
    try:
        if args.profile:
            profile = load_profile(args.profile)
            mains = args.mains or round(sum(profile["stream"].get("filters", {}).get("notch_hz", [48, 52])) / 2)
            assert_profile_source(profile, stream_info(args.fake, args.serial, mains), args.fake,
                                  allow_simulated=args.allow_simulated)
        classifier = SignalClassifier(profile)
    except (OSError, ValueError, KeyError, TypeError) as exc:
        sys.exit(f"Nie można użyć profilu: {exc}")

    targets = list(dict.fromkeys([(args.host, args.port)] + args.target))
    commands = {}
    if profile and not args.no_commands:
        commands = {"right_hand_active": ("right", "right_hand"), "left_hand_active": ("left", "left_hand")}
    sender = UdpJsonSender(classifier.names, targets, commands)

    pipeline = gp.Pipeline()
    source, filtered = build_front(pipeline, args.fake, args.serial, mains)
    pipeline.connect(filtered, classifier)
    pipeline.connect(classifier, sender)

    recorder = None
    if not args.no_record:
        from recording import Recorder
        recorder = Recorder(args.session, info={
            "kind": "game", "profile": str(Path(args.profile).resolve()) if args.profile else None,
            "subject": profile["subject"] if profile else None, "simulated": bool(args.fake),
            "stream": stream_info(args.fake, args.serial, mains),
            "feature_config": classifier.feature_config.to_dict()})
        recorder.attach(pipeline, source, filtered, classifier, classifier.names)
        if "eyes_closed_active" in classifier.names:
            # chwila wysłania decyzji do gry; z reakcją gry rozdziela opóźnienie potoku i Unity
            sender.on_change["eyes_closed_active"] = lambda closed: recorder.mark(
                "pipe_eyes_closed" if closed else "pipe_eyes_open")
        if args.no_markers:
            print("Uwaga: --no-markers - nagranie nie będzie miało znaczników z gry.")

    app = None
    if not args.no_gui:
        try:
            app = gp.MainApp(caption="BCI · sygnały do gry")
            eeg = gp.TimeSeriesScope(name="EEG po filtrach [µV]", amplitude_limit=50, time_window=10)
            signals = gp.TimeSeriesScope(name="Sygnały do gry: " + ", ".join(classifier.names),
                                         amplitude_limit=1, time_window=10)
            pipeline.connect(filtered, eeg)
            pipeline.connect(classifier, signals)
            app.add_widget(eeg)
            app.add_widget(signals)
        except ImportError as exc:
            sys.exit(f"Okno podglądu wymaga gpype[gui]: {exc}\nUruchom z --no-gui albo doinstaluj zależności.")
    elif not args.fake and sys.platform == "darwin":
        print("Uwaga: na macOS sterownik Unicorna potrzebuje pętli zdarzeń okna; bez --no-gui działa pewniej.")

    marker_socket = None
    try:
        if not args.no_markers:
            marker_socket = start_markers(args.marker_port, args.marker_file or
                                          ROOT / "data/raw" / time.strftime("bci_markers_%Y%m%d_%H%M%S.jsonl"),
                                          recorder)
        if profile:
            ready = ", ".join(classifier.engine.detectors) or "BRAK — wykonaj kalibrację"
            print(f"Profil: {profile['subject']} · {args.profile} · gotowe detektory: {ready}")
        else:
            print("Bez profilu: nieskalibrowane heurystyki focus, laterality, jaw. Kalibracja: python bci/calibrate.py")
        print(f"{'SYNTETYCZNE EEG' if args.fake else 'Unicorn Hybrid Black'} -> filtry 1 Hz / {mains} Hz -> "
              f"sygnały {', '.join(f'{host}:{port}' for host, port in targets)} (Ctrl+C kończy)")
        pipeline.start()
        if recorder:
            print(f"Nagrywanie: {recorder.folder}")
        if app:
            threading.Thread(target=report, args=(classifier, sender), daemon=True).start()
            app.run()
        else:
            report(classifier, sender)
    except KeyboardInterrupt:
        pass
    except Exception as exc:
        sys.exit(f"Potok zatrzymany: {exc}")
    finally:
        try:
            pipeline.stop()
        finally:
            pipeline.close()
            if marker_socket:
                marker_socket.close()
            if recorder:
                recorder.close()
                print(f"Zapisano nagranie: {recorder.folder} (znaczników: {recorder.count})")


if __name__ == "__main__":
    main()
