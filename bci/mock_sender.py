"""Atrapa części BCI: wysyła do gry fałszywe komendy i metryki przez UDP.

Pozwala rozwijać grę bez podłączonego Unicorna. Format wiadomości: docs/protocol.md.

    python mock_sender.py                # losowe komendy + płynnie zmieniająca się metryka
    python mock_sender.py --interactive  # komendy i wartości metryk wpisywane ręcznie
"""

import argparse
import json
import math
import random
import socket
import threading
import time

COMMANDS = ["left", "right", "select"]
METRIC = "focus"
METRIC_RATE_HZ = 10


def send(sock, addr, type_, name, value, echo=False):
    msg = {"type": type_, "name": name, "value": round(value, 3), "t": time.time()}
    sock.sendto(json.dumps(msg).encode("utf-8"), addr)
    if echo:
        print(json.dumps(msg))


def run_auto(sock, addr):
    print(f"Metryka '{METRIC}' leci z {METRIC_RATE_HZ} Hz, losowa komenda co 2–4 s")
    start = time.time()
    next_command = start + random.uniform(2, 4)
    while True:
        now = time.time()
        # wolna fala + szum, przycięta do 0..1
        value = 0.5 + 0.4 * math.sin((now - start) * 0.5) + random.gauss(0, 0.05)
        send(sock, addr, "metric", METRIC, min(max(value, 0.0), 1.0))
        if now >= next_command:
            send(sock, addr, "command", random.choice(COMMANDS), random.uniform(0.6, 1.0), echo=True)
            next_command = now + random.uniform(2, 4)
        time.sleep(1 / METRIC_RATE_HZ)


def run_interactive(sock, addr):
    metrics = {METRIC: 0.5}

    # metryki trzymają ostatnią wpisaną wartość i lecą w tle
    def stream_metrics():
        while True:
            for name, value in list(metrics.items()):
                send(sock, addr, "metric", name, value)
            time.sleep(1 / METRIC_RATE_HZ)

    threading.Thread(target=stream_metrics, daemon=True).start()
    print(f"Wpisz komendę (np. {', '.join(COMMANDS)}) albo metrykę z wartością 0–1 (np. '{METRIC} 0.8')")
    while True:
        parts = input("> ").split()
        if len(parts) == 1:
            send(sock, addr, "command", parts[0], 1.0, echo=True)
        elif len(parts) == 2:
            try:
                metrics[parts[0]] = min(max(float(parts[1]), 0.0), 1.0)
            except ValueError:
                print("Wartość metryki musi być liczbą")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5005)
    parser.add_argument("--interactive", action="store_true", help="wpisuj komendy ręcznie zamiast losowych")
    args = parser.parse_args()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    addr = (args.host, args.port)
    print(f"Wysyłam na {args.host}:{args.port} (Ctrl+C kończy)")
    try:
        if args.interactive:
            run_interactive(sock, addr)
        else:
            run_auto(sock, addr)
    except (KeyboardInterrupt, EOFError):
        pass


if __name__ == "__main__":
    main()
