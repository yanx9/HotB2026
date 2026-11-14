"""Odbiera znaczniki zdarzeń z gry (UDP 5006) i zapisuje je do CSV.

Znaczniki (początek bodźca, odpowiedź gracza) są potrzebne do pocięcia nagrania EEG
na epoki przy kalibracji P300. Format wiadomości: docs/protocol.md.

    python marker_logger.py                # zapis do data/raw/markers_<data>.csv
"""

import argparse
import csv
import json
import socket
import time
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--port", type=int, default=5006)
    parser.add_argument("--out", default=None, help="plik CSV (domyślnie data/raw/markers_<data>.csv)")
    args = parser.parse_args()

    out = Path(args.out) if args.out else (
        Path(__file__).resolve().parent.parent / "data" / "raw" / time.strftime("markers_%Y%m%d_%H%M%S.csv"))
    out.parent.mkdir(parents=True, exist_ok=True)

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", args.port))
    print(f"Słucham na porcie {args.port}, zapis do {out} (Ctrl+C kończy)")
    with open(out, "w", newline="") as f:
        writer = csv.writer(f)
        writer.writerow(["t_game", "t_received", "name", "value"])
        try:
            while True:
                data, _ = sock.recvfrom(4096)
                msg = json.loads(data)
                writer.writerow([msg["t"], time.time(), msg["name"], msg["value"]])
                f.flush()
                print(msg["name"], msg["value"])
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
