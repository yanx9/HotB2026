"""Strojenie szybkich detektorów (fast.py) na nagraniu gry z klawiszami prawdy.

    python bci/tune_fast.py data/raw/mrok_s01_<czas>                                    # sam raport
    python bci/tune_fast.py data/raw/mrok_s01_<czas> --profile data/calibration/s01.json --write

Nagranie: gra w trybie KEY, gracz trzyma spację przy zamkniętych oczach i F przy zaciśniętej
szczęce (docs/recording.md). Narzędzie:

 1. zestawia klawisze z fizjologią w EEG i ODRZUCA rozbieżności: klawisz bez zaciśnięcia,
    zaciśnięcie bez klawisza, zaciśnięcie krótsze niż klawisz, spację bez zamknięcia oczu,
    zdarzenia za krótkie. Odrzucone chwile nie liczą się ani do strojenia, ani do wyniku;
 2. mierzy opóźnienie detektorów, które działały w czasie nagrania;
 3. dobiera progi szybkich detektorów tak, by opóźnienie było najmniejsze przy nie większej
    liczbie pomyłek (przedwczesne otwarcia, przerwy, fałszywe włączenia), na pierwszych 60%
    nagrania, i sprawdza je na pozostałych 40%;
 4. z --write zapisuje progi dobrane na całym nagraniu do profilu (klucz "fast"); potok
    uruchomiony z takim profilem używa szybkich detektorów.

Wynik: tabela w konsoli i tune_fast.json w folderze nagrania.
"""

import argparse
import itertools
import json
import sys
from pathlib import Path

import numpy as np
from scipy.signal import butter, sosfiltfilt

sys.path.insert(0, str(Path(__file__).resolve().parent))

from fast import EYES_DEFAULT, FS, JAW_DEFAULT, EyesLogic, FastFeatures, JawLogic  # noqa: E402
from processing import UNICORN_LABELS  # noqa: E402
from profiles import load_profile, save_profile  # noqa: E402
from recording import load_recording  # noqa: E402

FRAME = 4                      # ramka potoku: decyzja co 16 ms
FS = int(FS)
SPLIT = 0.6
BURST_DB, BURST_PEAK_DB = 8.0, 10.0   # zaciśnięcie w EMG: ponad 8 dB od spoczynku, szczyt co najmniej 10 dB
EDGE_S = 0.5                   # o tyle może się rozminąć klawisz z fizjologią
MIN_EYES_S, MIN_JAW_S = 1.0, 0.5
GAME_MIN_CLOSED_S = 0.5        # gra pomija krótsze zamknięcia oczu (mrugnięcia)
LATE_S, AHEAD_S = 1.5, 0.7     # wyłączenie później / włączenie wcześniej niż tyle względem klawisza to pomyłka


def intervals(events, on, off, n):
    out, start = [], None
    for e in events:
        if e["sample"] is None:
            continue
        if e["name"] == on and start is None:
            start = e["sample"]
        elif e["name"] == off and start is not None:
            out.append((start, e["sample"]))
            start = None
    if start is not None:
        out.append((start, n))
    return out


def runs(mask, min_s, gap_s):
    edges = np.flatnonzero(np.diff(np.r_[0, mask.astype(int), 0]))
    out = []
    for a, b in zip(edges[::2], edges[1::2]):
        if out and a - out[-1][1] < gap_s * FS:
            out[-1][1] = b
        else:
            out.append([a, b])
    return [(a, b) for a, b in out if b - a >= min_s * FS]


def smooth(x, seconds):
    k = int(seconds * FS)
    c = np.cumsum(np.r_[0.0, x])
    out = np.full(len(x), np.nan)
    out[k - 1:] = (c[k:] - c[:-k]) / k
    return out


def physiology(data, posterior):
    """Nieprzyczynowy obraz tego, co się działo: EMG szczęki, alfa i skoki Fz. Tylko do
    sprawdzania klawiszy; detektory tego nie widzą."""
    band = lambda x, lo, hi: sosfiltfilt(butter(4, [lo, hi], btype="band", fs=FS, output="sos"), x)  # noqa: E731
    power = np.column_stack([smooth(band(data[f"flt_{c}"], 55, 95) ** 2, 0.1) for c in UNICORN_LABELS])
    power[np.isnan(power)] = np.nanmedian(power)      # pierwsze 0,1 s okna
    emg = 10 * np.log10(np.median(power, axis=1) + 1e-12)
    emg -= np.nanmedian(emg)
    alpha = np.mean([10 * np.log10(smooth(band(data[f"flt_{UNICORN_LABELS[i]}"], 8, 13) ** 2, 0.5) + 1e-12)
                     for i in posterior], axis=0)
    fz = sosfiltfilt(butter(2, [0.3, 6], btype="band", fs=FS, output="sos"), data["raw_Fz"])
    return np.nan_to_num(emg, nan=0.0), alpha, fz


def check_keys(data, events, posterior):
    """-> dobre i odrzucone zdarzenia obu sygnałów oraz przedziały wyłączone z oceny."""
    n = len(data["marker"])
    emg, alpha, fz = physiology(data, posterior)
    eyes = intervals(events, "truth_closed", "truth_open", n)
    jaw = intervals(events, "truth_jaw_on", "truth_jaw_off", n)
    bursts = [(a, b) for a, b in runs(emg > BURST_DB, 0.25, 0.3) if a > 3 * FS and emg[a:b].max() >= BURST_PEAK_DB]
    open_mask = np.ones(n, bool)
    for a, b in eyes:
        open_mask[max(0, a - FS):min(n, b + 2 * FS)] = False
    alpha = alpha - np.nanmedian(alpha[open_mask])

    good = {"eyes": [], "jaw": []}
    rejected = []
    excluded = [(0, 5 * FS)]                       # rozruch filtrów i poziomów odniesienia
    reject = lambda signal, a, b, why: (rejected.append({"signal": signal, "start_s": round(a / FS, 1),  # noqa: E731
                                                         "length_s": round((b - a) / FS, 1), "reason": why}),
                                        excluded.append((a - FS, b + 2 * FS)))

    for a, b in jaw:
        hits = [x for x in bursts if x[0] < b + EDGE_S * FS and x[1] > a - EDGE_S * FS]
        if (b - a) < MIN_JAW_S * FS:
            reject("jaw", a, b, "klawisz krótszy niż 0,5 s")
        elif not hits:
            reject("jaw", a, b, "klawisz bez zaciśnięcia w EMG")
        else:
            covered = sum(min(x[1], b) - max(x[0], a) for x in hits if min(x[1], b) > max(x[0], a)) / (b - a)
            if abs(hits[0][0] - a) > EDGE_S * FS or abs(hits[-1][1] - b) > EDGE_S * FS or covered < 0.7:
                reject("jaw", a, b, f"zaciśnięcie nie pokrywa się z klawiszem (EMG przez {covered:.0%} czasu, "
                                    f"początek {(hits[0][0] - a) / FS:+.1f} s, koniec {(hits[-1][1] - b) / FS:+.1f} s)")
            else:
                good["jaw"].append((a, b, hits[0][0]))    # trzeci element: początek zaciśnięcia w EMG
    for a, b in bursts:
        if not any(a < kb + EDGE_S * FS and b > ka - EDGE_S * FS for ka, kb in jaw):
            reject("jaw", a, b, f"zaciśnięcie bez klawisza (EMG {emg[a:b].max():.0f} dB)")

    step = lambda i: (fz[i + int(.15 * FS):i + int(.45 * FS)].mean() - fz[i - int(.3 * FS):i - int(.05 * FS)].mean()  # noqa: E731
                      if FS < i < n - FS else 0.0)
    for a, b in eyes:
        inside = float(np.nanmean(alpha[min(a + FS, b - 1):b])) if b - a > FS else float("nan")
        unkeyed = [x for x in bursts if x[0] < b and x[1] > a and not any(x[0] < kb and x[1] > ka for ka, kb in jaw)]
        if (b - a) < MIN_EYES_S * FS:
            reject("eyes", a, b, "spacja krótsza niż 1 s" + (" w trakcie zaciśnięcia bez klawisza: pomylony klawisz?" if unkeyed else ""))
        elif step(a) < 20 and not inside > 3:
            reject("eyes", a, b, f"spacja bez zamknięcia oczu (skok Fz {step(a):+.0f} µV, alfa {inside:+.1f} dB)")
        else:
            good["eyes"].append((a, b, inside))          # trzeci element: alfa w trakcie, dB ponad otwarte
    for a, b in runs((alpha > 4) & open_mask & (emg < 6), 2.0, 0.3):
        if step(a - FS // 2) > 30:
            reject("eyes", a, b, f"zamknięcie oczu bez spacji (alfa {np.nanmean(alpha[a:b]):+.1f} dB, skok Fz {step(a - FS // 2):+.0f} µV)")
    return good, rejected, excluded


def feature_rows(data, posterior):
    features = FastFeatures(posterior)
    eeg = np.column_stack([data[f"flt_{c}"] for c in UNICORN_LABELS])
    raw_fz = data["raw_Fz"]
    rows = [features.push(eeg[i:i + FRAME], raw_fz[i:i + FRAME]) for i in range(0, len(eeg) - FRAME + 1, FRAME)]
    return rows, np.arange(FRAME - 1, FRAME * len(rows), FRAME)


def run_logic(logic, rows):
    dt = FRAME / FS
    return np.fromiter((logic.step(row, dt) for row in rows), bool, len(rows))


def score(state, t, good, excluded, lo, hi, min_s, early_s):
    """Opóźnienia i pomyłki na próbkach [lo, hi). Zdarzenie detektora: odcinek "włączone" >= min_s."""
    edges = np.diff(np.r_[0, state.astype(int), 0])
    on, off = np.flatnonzero(edges == 1), np.flatnonzero(edges == -1)
    episodes = [(t[a], t[min(b, len(t) - 1)]) for a, b in zip(on, off)]
    episodes = [e for e in episodes if e[1] - e[0] >= min_s * FS]
    inside = [g for g in good if lo <= g[0] and g[1] <= hi]
    start, end, missed, early, broken, late, ahead = [], [], 0, 0, 0, 0, 0
    for a, b, *_ in inside:
        hits = [e for e in episodes if e[0] < b and e[1] > a]
        if not hits:
            missed += 1
            continue
        start.append((hits[0][0] - a) / FS)
        end.append((hits[-1][1] - b) / FS)
        early += hits[-1][1] < b - early_s * FS        # wyłączył się przed puszczeniem klawisza
        broken += len(hits) > 1                        # przerwa w środku
        late += hits[-1][1] > b + LATE_S * FS          # zawiesił się po puszczeniu
        ahead += hits[0][0] < a - AHEAD_S * FS         # był włączony na długo przed klawiszem
    false = sum(1 for e in episodes if lo <= e[0] < hi
                and not any(e[0] < b + FS and e[1] > a - FS for a, b, *_ in good)
                and not any(e[0] < b and e[1] > a for a, b in excluded))
    busy = sum(min(b, hi) - max(a, lo) for a, b in [g[:2] for g in inside] + excluded if min(b, hi) > max(a, lo))
    minutes = max((hi - lo - busy) / FS / 60, 1e-9)
    q = lambda v: [round(float(x), 2) for x in np.percentile(v, [50, 90])] if v else None  # noqa: E731
    truth = np.zeros(len(t), bool)
    for a, b, *_ in good:
        truth |= (t >= a - FS // 2) & (t < b + FS)
    for a, b in excluded:
        truth |= (t >= a) & (t < b)
    span = (t >= lo) & (t < hi)
    return {"events": len(inside), "missed": missed, "on_latency_s": q(start), "off_latency_s": q(end),
            "early_off": int(early), "broken": int(broken), "late_off": int(late), "early_on": int(ahead),
            "false_per_min": round(false / minutes, 2),
            "on_outside_truth": round(float((state & ~truth & span).sum() / max((~truth & span).sum(), 1)), 3)}


def cost(m):
    """Mniejsze = lepsze. Pomyłki kosztują dużo więcej niż dziesiąte części sekundy."""
    if m["on_latency_s"] is None:
        return 1e9
    errors = (m["missed"] + m["early_off"] + m["late_off"] + m["early_on"] + 0.5 * m["broken"]) / max(m["events"], 1)
    return (m["off_latency_s"][0] + 0.3 * max(m["on_latency_s"][0], 0) + 15 * errors
            + 2 * m["false_per_min"] + 10 * m["on_outside_truth"])


def search(make, grid, rows, t, good, excluded, lo, hi, min_s, early_s):
    best = None
    for values in itertools.product(*grid.values()):
        params = dict(zip(grid, values))
        m = score(run_logic(make(params), rows), t, good, excluded, lo, hi, min_s, early_s)
        c = cost(m)
        if best is None or c < best[0]:
            best = (c, params, m)
    return best[1], best[2]


EYES_GRID = {"c_dn": (40.0, 45.0, 50.0), "c_soft": (30.0, 36.0, 999.0), "d_soft": (2.0, 4.0), "t_arm": (0.7, 1.0),
             "d": (4.0, 5.0, 6.0), "t_d": (0.3, 0.5), "lift_min": (10.0, 20.0)}
JAW_GRID = {"on_db": (6.0, 8.0, 10.0, 12.0), "off_db": (3.0, 5.0), "t_on": (0.03, 0.06, 0.1), "t_off": (0.06, 0.12, 0.2)}


def main():
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8", errors="replace")
    cli = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    cli.add_argument("folder", type=Path)
    cli.add_argument("--profile", type=Path, help="profil, do którego trafią progi (domyślnie tylko raport)")
    cli.add_argument("--write", action="store_true", help="zapisz progi w profilu")
    args = cli.parse_args()
    if args.write and not args.profile:
        cli.error("--write wymaga --profile")

    data, events, meta = load_recording(args.folder)
    n = len(data["marker"])
    posterior = meta.get("feature_config", {}).get("posterior_channels", [4, 5, 6, 7])
    good, rejected, excluded = check_keys(data, events, posterior)
    print(f"{args.folder.name}: {n / FS / 60:.1f} min, kanały alfy: {', '.join(UNICORN_LABELS[i] for i in posterior)}")
    for signal, name in (("eyes", "oczy (spacja)"), ("jaw", "szczęka (F)")):
        bad = [r for r in rejected if r["signal"] == signal]
        print(f"\n{name}: dobre zdarzenia {len(good[signal])}, odrzucone {len(bad)}")
        for r in bad:
            print(f"   {r['start_s']:7.1f} s  {r['length_s']:4.1f} s  {r['reason']}")
    weak = [g for g in good["eyes"] if not g[2] > 3]
    if weak:
        print(f"   (zostawione, choć alfa prawie nie wzrosła: {len(weak)} zamknięć; powieki się zamknęły, "
              f"więc to prawdziwe zamknięcia, których sama alfa nie widzi: {[round(g[0] / FS, 1) for g in weak]})")

    rows, t = feature_rows(data, posterior)
    split = int(n * SPLIT)
    kinds = {"eyes": (EyesLogic, EYES_GRID, EYES_DEFAULT, GAME_MIN_CLOSED_S, 0.4, "out_eyes_closed_active"),
             "jaw": (JawLogic, JAW_GRID, JAW_DEFAULT, 0.05, 0.3, "out_jaw_active")}
    report = {"recording": str(args.folder), "minutes": round(n / FS / 60, 1), "rejected": rejected, "signals": {}}
    fast = {}
    for signal, (logic, grid, default, min_s, early_s, column) in kinds.items():
        if len(good[signal]) < 8:
            print(f"\n{signal}: za mało dobrych zdarzeń ({len(good[signal])}), pomijam.")
            continue
        args_ = (rows, t, good[signal], excluded)
        live = None
        if column in data:
            live_state = data[column][t] >= 0.5
            live = {"all": score(live_state, t, good[signal], excluded, 0, n, min_s, early_s),
                    "test": score(live_state, t, good[signal], excluded, split, n, min_s, early_s)}
        make = lambda params, logic=logic: logic(params)  # noqa: E731
        trained, on_train = search(make, grid, *args_, 0, split, min_s, early_s)
        on_test = score(run_logic(make(trained), rows), t, good[signal], excluded, split, n, min_s, early_s)
        final, on_all = search(make, grid, *args_, 0, n, min_s, early_s)
        fast[signal] = {**default, **final}
        report["signals"][signal] = {"live": live, "trained_on_first_60pct": trained, "train": on_train,
                                     "test": on_test, "final_params": fast[signal], "final_on_all": on_all}
        line = lambda m: (f"włączenie {m['on_latency_s'][0]:.2f} s (90%: {m['on_latency_s'][1]:.2f}), "  # noqa: E731
                          f"wyłączenie {m['off_latency_s'][0]:.2f} s (90%: {m['off_latency_s'][1]:.2f}), "
                          f"pominięte {m['missed']}/{m['events']}, wyłączone za wcześnie {m['early_off']}, "
                          f"zawieszone {m['late_off']}, włączone przed czasem {m['early_on']}, przerwane {m['broken']}, "
                          f"fałszywe {m['false_per_min']}/min, włączony poza prawdą {m['on_outside_truth']:.1%} czasu") if m["on_latency_s"] else "brak wykryć"
        print(f"\n{signal}: opóźnienie względem klawisza (mediana)")
        if live:
            print(f"   na żywo, całość:        {line(live['all'])}")
            print(f"   na żywo, ostatnie 40%:  {line(live['test'])}")
        print(f"   szybki,  ostatnie 40%:  {line(on_test)}   <- progi z pierwszych 60%: {trained}")
        print(f"   szybki,  całość:        {line(on_all)}   <- progi do profilu: {final}")
    (args.folder / "tune_fast.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n-> {args.folder / 'tune_fast.json'}")

    if args.write and fast:
        profile = load_profile(args.profile)
        profile["fast"] = {**fast, "tuned_on": args.folder.name, "posterior_channels": posterior}
        save_profile(args.profile, profile)
        print(f"Zapisano szybkie detektory ({', '.join(fast)}) w {args.profile}. Uruchom potok z tym profilem.")


if __name__ == "__main__":
    main()
