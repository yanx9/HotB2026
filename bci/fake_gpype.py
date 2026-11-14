"""Synthetic EEG in the exact float64 wire layout of g.Pype UDPSender.

    python bci/fake_gpype.py --follow-triggers
    python bci/calibrate.py --synthetic-input --profile data/calibration/fake_gpype.json

This is a socket emulator, not g.Pype or a hardware test. Never use its profiles
as evidence of a person's EEG performance. Requires only the base dependencies.
"""

import argparse
import socket
import time

import numpy as np

from calibration_markers import marker_code
from calibration_session import DemoSource
from processing import SIGNALS


PLAN = [("rest", 4), ("eyes_closed", 4), ("rest", 2), ("right_hand", 4),
        ("rest", 2), ("left_hand", 4), ("rest", 2), ("jaw", 3), ("rest", 2)]


def cycle_state(elapsed):
    elapsed %= sum(duration for _, duration in PLAN)
    for name, duration in PLAN:
        if elapsed < duration:
            return name
        elapsed -= duration
    return "rest"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1", help="adres kalibratora / mostu")
    parser.add_argument("--port", type=int, default=56000)
    parser.add_argument("--frame-samples", type=int, default=4)
    parser.add_argument("--byte-order", choices=("little", "big"), default="little")
    parser.add_argument("--units", choices=("uv", "v"), default="uv")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--follow-triggers", action="store_true", help="podążaj za wskazówkami kalibratora")
    mode.add_argument("--state", choices=("rest", *SIGNALS), help="stałe zadanie zamiast cyklu demonstracyjnego")
    parser.add_argument("--trigger-host", default="127.0.0.1")
    parser.add_argument("--trigger-port", type=int, default=56001)
    args = parser.parse_args()
    if not 1 <= args.frame_samples <= 62:
        parser.error("--frame-samples: od 1 do 62 próbek przy 250 Hz")
    source = DemoSource()
    dtype = np.dtype("<f8" if args.byte_order == "little" else ">f8")
    state = args.state or "rest"
    sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    triggers = None
    try:
        if args.follow_triggers:
            triggers = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            triggers.bind((args.trigger_host, args.trigger_port))
            triggers.setblocking(False)
        print(f"SYNTETYCZNE EEG -> {args.host}:{args.port}: 250 Hz, 8 kanałów, float64, "
              f"{args.frame_samples} próbki/pakiet. Odbiornik wymaga --synthetic-input.", flush=True)
        print("Tryb: " + ("wskazówki kalibratora" if triggers else args.state or "cykl czterech zadań"), flush=True)
        start = time.monotonic()
        sent = 0
        while True:
            if triggers:
                while True:
                    try:
                        code = int(triggers.recvfrom(128)[0].decode("ascii"))
                    except BlockingIOError:
                        break
                    except (ValueError, UnicodeError):
                        continue
                    if 100 <= code <= 134 and code % 10 <= 4:
                        state = next((name for name in SIGNALS if marker_code(name, "active") == code), "rest")
            elapsed = time.monotonic() - start
            due = (sent + args.frame_samples) / 250
            if elapsed < due:
                time.sleep(min(.005, due - elapsed))
                continue
            # After a paused process, resume current time instead of dumping a backlog.
            if elapsed - due > .1:
                sent = int(elapsed * 250) - args.frame_samples
            if not triggers and args.state is None:
                state = cycle_state(elapsed)
            t = (sent + np.arange(args.frame_samples)) / 250
            frame = source.samples(t, state)
            if args.units == "v":
                frame *= 1e-6
            sender.sendto(frame.astype(dtype).tobytes(order="C"), (args.host, args.port))
            sent += args.frame_samples
    except KeyboardInterrupt:
        pass
    finally:
        sender.close()
        if triggers:
            triggers.close()


if __name__ == "__main__":
    main()
