"""Ocena detektora oczu na nagraniu: decyzje eyes_closed kontra prawda.

    python bci/evaluate_eyes.py data/raw/mrok_s01_20261115_120000
    python bci/evaluate_eyes.py data/raw/mrok_s01_... --profile data/calibration/s01.json   # odtworzenie z innym profilem
    python bci/evaluate_eyes.py data/raw/kalibracja_s01_...    # prawdą są fazy kalibracji oczu

Prawda: znaczniki truth_closed / truth_open (spacja w Mroku, w obu trybach) albo fazy
cal_eyes_closed_* z kalibratora. Decyzje: bez --profile te, które potok wysłał na żywo
(kolumny out_eyes_closed_*); z --profile detektor liczony od nowa z kolumn flt_* (EEG po
tych samych filtrach) dokładnie tym kodem, którego używa potok, co 0,1 s.

Wynik: tabela w konsoli, eval_eyes.json i eval_eyes.png w folderze nagrania.
"""

import argparse
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))

from processing import UNICORN_LABELS, Window  # noqa: E402
from profiles import ProfileEngine, load_profile  # noqa: E402
from recording import load_recording  # noqa: E402

FS = 250
STEP = 25                 # decyzja co 0,1 s, jak w SignalClassifier
MIN_CLOSURE_S = 1.0       # krótsze zamknięcia to mrugnięcia / przypadkowe wciśnięcia
DETECT_GRACE_S = 1.0      # zamknięcie wykryte, jeśli detektor zareaguje do 1 s po otwarciu
TRANSITION_S = 3.0        # przy liczeniu zgodności pomijamy 3 s po każdej zmianie prawdy


def truth_intervals(events, n_samples):
    """-> lista (start, koniec) w próbkach i opis źródła prawdy."""
    for start_name, end_names, source in (
            ("truth_closed", ("truth_open",), "spacja (truth_*)"),
            ("cal_eyes_closed_active", ("cal_eyes_closed_rest", "cal_eyes_closed_complete",
                                        "cal_eyes_closed_cancel"), "fazy kalibracji")):
        intervals, start = [], None
        for event in events:
            if event["sample"] is None:
                continue
            if event["name"] == start_name and start is None:
                start = event["sample"]
            elif event["name"] in end_names and start is not None:
                intervals.append((start, event["sample"]))
                start = None
        if start is not None:
            intervals.append((start, n_samples))
        if intervals:
            return intervals, source
    return [], None   # sesja bez spacji: tylko opis zachowania detektora (describe)


def live_decisions(data):
    if "out_eyes_closed_active" not in data:
        raise SystemExit("Nagranie nie ma wyjścia detektora oczu (potok bez profilu?). Podaj --profile.")
    idx = np.arange(STEP - 1, len(data["marker"]), STEP)
    return idx, data["out_eyes_closed_active"][idx] >= 0.5, data["out_eyes_closed_valid"][idx] >= 0.5


def replay_decisions(data, profile):
    engine = ProfileEngine(profile)
    if "eyes_closed" not in engine.detectors:
        raise SystemExit("Profil nie ma gotowej kalibracji oczu (status inny niż ready).")
    x = np.column_stack([data[f"flt_{c}"] for c in UNICORN_LABELS])
    window = Window(FS, x.shape[1])
    idx, active, valid = [], [], []
    for end in range(STEP, len(x) + 1, STEP):
        window.push(x[end - STEP:end])
        result = engine.update(window, end / FS)["eyes_closed"]
        idx.append(end - 1)
        active.append(result["active"])
        valid.append(result["valid"])
    return np.array(idx), np.array(active), np.array(valid)


def evaluate(idx, active, valid, intervals, start_sample=0):
    keep = idx >= start_sample
    idx, active, valid = idx[keep], active[keep], valid[keep]
    truth = np.zeros(len(idx), bool)
    for a, b in intervals:
        truth |= (idx >= a) & (idx < b)

    closures = []
    for a, b in intervals:
        if (b - a) / FS < MIN_CLOSURE_S or b <= idx[0]:
            continue
        window = (idx >= a) & (idx < b + DETECT_GRACE_S * FS)
        hits = idx[window & active]
        after = (idx >= b) & (idx < b + 6 * FS)
        released = idx[after & ~active]
        closures.append({"start_s": round(a / FS, 1), "length_s": round((b - a) / FS, 1),
                         "detected": bool(len(hits)),
                         "close_latency_s": round((hits[0] - a) / FS, 2) if len(hits) else None,
                         "open_latency_s": round((released[0] - b) / FS, 2) if len(hits) and len(released) else None})

    # fałszywe zamknięcie: włączenie detektora poza [początek, koniec + 1 s] prawdziwego zamknięcia
    onsets = idx[np.flatnonzero(np.diff(np.r_[False, active].astype(int)) == 1)]
    allowed = np.zeros(len(idx), bool)
    for a, b in intervals:
        allowed |= (idx >= a) & (idx < b + DETECT_GRACE_S * FS)
    false_onsets = [int(s) for s in onsets if not allowed[np.searchsorted(idx, s)]]
    open_minutes = max((~truth).sum() * STEP / FS / 60, 1e-9)

    steady = np.ones(len(idx), bool)            # zgodność bez okresów przejściowych
    for a, b in intervals:
        for edge in (a, b):
            steady &= ~((idx >= edge) & (idx < edge + TRANSITION_S * FS))
    detected = [c for c in closures if c["detected"]]
    lat = lambda key: [c[key] for c in detected if c[key] is not None]  # noqa: E731
    return {
        "minutes": round(len(idx) * STEP / FS / 60, 1),
        "closures": len(closures), "detected": len(detected),
        "missed": [c["start_s"] for c in closures if not c["detected"]],
        "median_close_latency_s": round(float(np.median(lat("close_latency_s"))), 2) if lat("close_latency_s") else None,
        "median_open_latency_s": round(float(np.median(lat("open_latency_s"))), 2) if lat("open_latency_s") else None,
        "false_closures_per_open_min": round(len(false_onsets) / open_minutes, 2),
        "false_closures_at_s": [round(s / FS, 1) for s in false_onsets],
        "agreement_steady": round(float((active == truth)[steady].mean()), 3) if steady.any() else None,
        "valid_fraction": round(float(valid.mean()), 3),
        "per_closure": closures,
    }, (idx, active, valid, truth)


def describe(idx, active, valid, events, start_sample=0):
    """Sesja bez prawdy (bez spacji): co detektor robił i jak zareagowała gra."""
    keep = idx >= start_sample
    idx, active, valid = idx[keep], active[keep], valid[keep]
    edges = np.diff(np.r_[False, active, False].astype(int))
    starts, ends = idx[np.flatnonzero(edges[:-1] == 1)], np.flatnonzero(edges == -1)
    ends = [idx[min(e, len(idx) - 1)] for e in ends]
    lengths = [float(b - a) / FS for a, b in zip(starts, ends)]
    minutes = len(idx) * STEP / FS / 60
    game = {name: sum(1 for e in events if e["name"] == name and e["sample"] and e["sample"] >= start_sample)
            for name in ("eyes_closed", "scan", "caught", "win", "loot")}
    scans = [e["value"] for e in events if e["name"] == "scan" and e["sample"] and e["sample"] >= start_sample]
    return {"minutes": round(minutes, 1), "closures": len(lengths),
            "closures_per_min": round(len(lengths) / max(minutes, 1e-9), 1),
            "closure_length_s": {"median": round(float(np.median(lengths)), 1) if lengths else None,
                                 "min": round(min(lengths), 1) if lengths else None,
                                 "max": round(max(lengths), 1) if lengths else None,
                                 "under_1s": int(sum(l < 1 for l in lengths))},
            "time_closed_fraction": round(float(active.mean()), 3),
            "valid_fraction": round(float(valid.mean()), 3),
            "game_events": game,
            "scan_radius_m_median": round(float(np.median(scans)), 1) if scans else None}, \
        (idx, active, valid, np.zeros(len(idx), bool))


def system_latency(data, events):
    """Opóźnienie od decyzji detektora do reakcji gry, rozbite na potok i Unity (tylko tryb EEG).

    potok: próbka EEG, na której zmieniła się decyzja -> chwila wysłania do gry (znacznik pipe_*)
    gra:   wysłanie (zegar potoku) -> gra zmienia stan oczu (zegar gry; ten sam komputer)
    razem: próbka zmiany decyzji -> próbka, na którą trafił znacznik reakcji gry
    Bez znaczników pipe_* (nagrania sprzed tej zmiany) liczy tylko "razem".
    """
    if "out_eyes_closed_active" not in data:
        return None
    act = data["out_eyes_closed_active"] >= 0.5
    edges = {True: np.flatnonzero(np.diff(act.astype(int)) == 1) + 1,
             False: np.flatnonzero(np.diff(act.astype(int)) == -1) + 1}
    mode, pipe, out = "eeg", {}, {"pipeline_s": [], "game_s": [], "total_s": []}
    for e in events:
        if e["sample"] is None:
            continue
        name = e["name"]
        if name.startswith("mode_"):
            mode = "eeg" if name == "mode_eeg" else "keyboard"
        closed = name.endswith("closed")
        if name in ("pipe_eyes_closed", "pipe_eyes_open"):
            before = edges[closed][edges[closed] <= e["sample"]]
            if len(before) and (e["sample"] - before[-1]) / FS < 3:
                out["pipeline_s"].append((e["sample"] - before[-1]) / FS)
            pipe[closed] = e
        elif name in ("eyes_closed", "eyes_open") and mode == "eeg":
            before = edges[closed][edges[closed] <= e["sample"]]
            if len(before) and (e["sample"] - before[-1]) / FS < 3:
                out["total_s"].append((e["sample"] - before[-1]) / FS)
            sent = pipe.get(closed)
            if sent and e["t_game_unix"] and 0 <= e["sample"] - sent["sample"] < 3 * FS:
                out["game_s"].append(float(e["t_game_unix"]) - float(sent["t_received_unix"]))
    return {key: {"median": round(float(np.median(v)), 3), "max": round(float(np.max(v)), 3), "n": len(v)}
            for key, v in out.items() if v}


def print_latency(lat):
    if not lat:
        return
    part = lambda key, text: (f"{text} {lat[key]['median']:.2f} s (maks. {lat[key]['max']:.2f}, n={lat[key]['n']})"  # noqa: E731
                              if key in lat else None)
    parts = [x for x in (part("pipeline_s", "potok"), part("game_s", "gra"), part("total_s", "razem")) if x]
    print("  opóźnienie decyzja -> gra (mediana): " + " · ".join(parts))
    if "pipeline_s" not in lat:
        print("    (nagranie bez znaczników pipe_*: podział na potok i grę od następnej sesji)")


def plot(path, title, series, intervals, false_at):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    idx, active, valid, truth = series
    t = idx / FS
    fig, ax = plt.subplots(figsize=(16, 3.2))
    for a, b in intervals:
        ax.axvspan(a / FS, b / FS, color="tab:blue", alpha=0.18, lw=0)
    ax.step(t, active * 1.0, where="post", color="tab:orange", lw=1.2, label="detektor: zamknięte")
    ax.step(t, ~valid * -0.2, where="post", color="tab:red", lw=0.8, label="sygnał nieważny")
    for s in false_at:
        ax.axvline(s, color="red", lw=0.8, ls=":")
    ax.set_ylim(-0.4, 1.3)
    ax.set_yticks([0, 1], ["otwarte", "zamknięte"])
    ax.set_xlabel("s od początku nagrania")
    ax.set_title(f"{title}  (niebieskie tło = prawda, czerwone kropki = fałszywe zamknięcia)", fontsize=10)
    ax.legend(loc="upper right", fontsize=8)
    fig.tight_layout()
    fig.savefig(path, dpi=90)
    plt.close(fig)


def main():
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8", errors="replace")
    cli = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    cli.add_argument("folder", type=Path)
    cli.add_argument("--profile", type=Path, help="odtwórz detektor z tym profilem zamiast decyzji z nagrania")
    args = cli.parse_args()

    data, events, meta = load_recording(args.folder)
    intervals, source = truth_intervals(events, len(data["marker"]))
    if args.profile:
        idx, active, valid = replay_decisions(data, load_profile(args.profile))
        mode = f"odtworzenie z profilem {args.profile}"
    else:
        idx, active, valid = live_decisions(data)
        mode = "decyzje z nagrania (na żywo)"
    starts = [e["sample"] for e in events if e["name"] == "session_start" and e["sample"] is not None]
    if source is None:   # bez spacji: nie ma czym sprawdzić trafności, tylko opis
        report, series = describe(idx, active, valid, events, starts[0] if starts else 0)
        report.update(recording=str(args.folder), truth=None, decisions=mode)
        (args.folder / "eval_eyes.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        plot(args.folder / "eval_eyes.png", args.folder.name + " (bez prawdy)", series, [], [])
        r = report
        print(f"{args.folder.name}: {r['minutes']} min · BEZ PRAWDY (brak spacji) · {mode}")
        print(f"  zamknięcia wg detektora: {r['closures']} ({r['closures_per_min']}/min), długość mediana "
              f"{r['closure_length_s']['median']} s, krótszych niż 1 s: {r['closure_length_s']['under_1s']}")
        print(f"  oczy zamknięte {r['time_closed_fraction']:.0%} czasu · sygnał ważny {r['valid_fraction']:.0%}")
        print(f"  gra: {r['game_events']} · zasięg skanu (mediana) {r['scan_radius_m_median']} m")
        print("  Trafności nie da się policzyć bez spacji; do oceny używaj sesji ze spacją.")
        print_latency(system_latency(data, events))
        return
    report, series = evaluate(idx, active, valid, intervals, starts[0] if starts else 0)
    report.update(recording=str(args.folder), truth=source, decisions=mode)

    (args.folder / "eval_eyes.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    plot(args.folder / "eval_eyes.png", args.folder.name, series, intervals,
         report["false_closures_at_s"])
    r = report
    print(f"{args.folder.name}: {r['minutes']} min · prawda: {source} · {mode}")
    print(f"  zamknięcia ≥{MIN_CLOSURE_S:g} s wykryte: {r['detected']}/{r['closures']}"
          + (f"  (pominięte w s: {r['missed']})" if r["missed"] else ""))
    print(f"  opóźnienie: zamknięcie {r['median_close_latency_s']} s, otwarcie {r['median_open_latency_s']} s (mediana)")
    print(f"  fałszywe zamknięcia: {r['false_closures_per_open_min']} na minutę z otwartymi oczami")
    print(f"  zgodność poza przejściami: {r['agreement_steady']} · sygnał ważny: {r['valid_fraction']:.0%}")
    print_latency(system_latency(data, events))
    print(f"  -> {args.folder / 'eval_eyes.json'}, {args.folder / 'eval_eyes.png'}")


if __name__ == "__main__":
    main()
