"""Udaje Unicorn Hybrid Black w LSL: 8 kanałów EEG, 250 Hz, w mikrowoltach.

Do testowania lsl_bridge.py bez headsetu. Moc alfa i beta zmienia się powoli w przeciwnych
fazach, więc focus w moście powinien falować z okresem --period sekund. Rytm nad C3 i C4
słabnie na przemian (laterality faluje co 20 s), a co 12 s przez sekundę pojawia się szum
mięśniowy na wszystkich kanałach (jaw).

    python fake_unicorn.py
"""

import argparse
import time

import numpy as np
from pylsl import StreamInfo, StreamOutlet, local_clock

CHANNELS = ["Fz", "C3", "Cz", "C4", "Pz", "PO7", "Oz", "PO8"]
SRATE = 250
CHUNK = 10


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--name", default="FakeUnicorn")
    parser.add_argument("--period", type=float, default=30.0, help="okres zmian skupienia w sekundach")
    args = parser.parse_args()

    info = StreamInfo(args.name, "EEG", len(CHANNELS), SRATE, "float32", "fake-unicorn")
    channels = info.desc().append_child("channels")
    for label in CHANNELS:
        channel = channels.append_child("channel")
        channel.append_child_value("label", label)
        channel.append_child_value("unit", "microvolts")
        channel.append_child_value("type", "EEG")
    outlet = StreamOutlet(info, chunk_size=CHUNK)

    rng = np.random.default_rng()
    start, sent = local_clock(), 0
    print(f"Nadaję {args.name!r}: {len(CHANNELS)} kanałów, {SRATE} Hz (Ctrl+C kończy)")
    try:
        while True:
            # nadrabiamy tyle próbek, ile powinno upłynąć od startu
            while sent + CHUNK <= (local_clock() - start) * SRATE:
                t = (sent + np.arange(CHUNK)) / SRATE
                focus = 0.5 + 0.5 * np.sin(2 * np.pi * t / args.period)
                wave = (6 * np.sin(2 * np.pi * 6 * t)                          # theta, stała
                        + (1.5 - focus) * 10 * np.sin(2 * np.pi * 10 * t)      # alfa słabnie przy skupieniu
                        + (0.5 + focus) * 4 * np.sin(2 * np.pi * 20 * t))      # beta rośnie
                samples = wave[:, None] + rng.normal(0, 5, (CHUNK, len(CHANNELS)))
                side = np.sin(2 * np.pi * t / 20)                              # który "ruch ręki" trwa
                mu = 8 * np.sin(2 * np.pi * 11 * t)
                samples[:, CHANNELS.index("C3")] += (1 - side) * mu            # prawa ręka tłumi rytm nad C3
                samples[:, CHANNELS.index("C4")] += (1 + side) * mu
                if t[0] % 12 > 11:                                             # zaciśnięta szczęka
                    samples += rng.normal(0, 40, samples.shape)
                outlet.push_chunk(samples.tolist(), start + (sent + CHUNK - 1) / SRATE)
                sent += CHUNK
            time.sleep(0.005)
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
