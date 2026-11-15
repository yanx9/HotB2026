"""Ocena detektorów na nagraniu: decyzje potoku kontra prawda. Oczy i szczęka tym samym kodem.

    python bci/evaluate.py data/raw/mrok_s01_20261115_120000                 # oczy i szczęka
    python bci/evaluate.py data/raw/mrok_s01_... --signal jaw
    python bci/evaluate.py data/raw/mrok_s01_... --profile data/calibration/s01.json   # odtworzenie z innym profilem
    python bci/evaluate.py data/raw/kalibracja_s01_...    # prawdą są fazy kalibracji

                 oczy (eyes)                       szczęka (jaw)
    prawda       truth_closed / truth_open         truth_jaw_on / truth_jaw_off      klawisz w Mroku: spacja / F
                 cal_eyes_closed_*                 cal_jaw_*                         albo fazy kalibratora
    decyzje      out_eyes_closed_active            out_jaw_active                    to, co potok wysłał na żywo
    potok -> gra pipe_eyes_closed / _open          pipe_jaw_on / _off                chwila wysłania zmiany
    reakcja gry  eyes_closed / eyes_open           sprint_on / sprint_off

Z --profile detektor jest liczony od nowa z kolumn flt_* (EEG po tych samych filtrach) dokładnie
tym kodem, którego używa potok, co 0,1 s. Szczęka bez gotowej kalibracji w profilu to ta sama
heurystyka JawButton co w potoku.

Wynik dla każdego sygnału: tabela w konsoli, eval_<sygnał>.json i eval_<sygnał>.png w folderze
nagrania. Klucze raportu są wspólne (closures = zdarzenia "włączone", close / open = włączenie /
wyłączenie), żeby wyniki oczu i szczęki dało się porównywać wprost.
"""

import argparse
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))

from fast import FastDetectors  # noqa: E402
from processing import JawButton, UNICORN_LABELS, Window  # noqa: E402
from profiles import ProfileEngine, load_profile  # noqa: E402
from recording import load_recording  # noqa: E402

FS = 250
STEP = 25                 # zwykłe detektory: decyzja co 0,1 s, jak w SignalClassifier
FRAME = 4                 # ramka potoku: tak często zmieniają się flagi szybkich detektorów


class Signal:
    """Co odróżnia sygnały: nazwy znaczników i kolumn, słowa w raporcie i skala czasu zdarzeń."""

    def __init__(self, key, column, truth, game, pipe, on, off, events, key_name, min_s, grace_s, transition_s,
                 extra_game=(), ignore_s=0.0):
        self.key, self.column = key, column            # eyes / jaw; nazwa sygnału potoku i kalibracji
        self.truth, self.game, self.pipe = truth, game, pipe   # pary (włączenie, wyłączenie)
        self.on, self.off, self.events, self.key_name = on, off, events, key_name
        self.min_s = min_s                # krótsze zdarzenia to mrugnięcia / przypadkowe wciśnięcia
        self.grace_s = grace_s            # wykryte, jeśli detektor zareaguje do tylu sekund po końcu
        self.transition_s = transition_s  # przy liczeniu zgodności pomijamy tyle po każdej zmianie prawdy
        self.extra_game = extra_game
        self.ignore_s = ignore_s          # krótszych włączeń gra nie zauważa (mrugnięcia), więc nie liczą się


# Zamknięcie oczu trwa sekundy, a alfa narasta powoli; zaciśnięcie szczęki jest krótkie i natychmiastowe.
SIGNALS = {
    "eyes": Signal("eyes", "eyes_closed", ("truth_closed", "truth_open"), ("eyes_closed", "eyes_open"),
                   ("pipe_eyes_closed", "pipe_eyes_open"), "zamknięte", "otwarte", "zamknięcia", "spacja",
                   min_s=1.0, grace_s=1.0, transition_s=3.0, extra_game=("scan",), ignore_s=0.5),
    "jaw": Signal("jaw", "jaw", ("truth_jaw_on", "truth_jaw_off"), ("sprint_on", "sprint_off"),
                  ("pipe_jaw_on", "pipe_jaw_off"), "zaciśnięta", "luźna", "zaciśnięcia", "F",
                  min_s=0.5, grace_s=0.5, transition_s=1.0),
}


def truth_intervals(events, n_samples, signal):
    """-> lista (start, koniec) w próbkach i opis źródła prawdy."""
    cal = f"cal_{signal.column}_"
    for start_name, end_names, source in (
            (signal.truth[0], (signal.truth[1],), f"klawisz {signal.key_name} ({signal.truth[0]} / {signal.truth[1]})"),
            (cal + "active", (cal + "rest", cal + "complete", cal + "cancel"), "fazy kalibracji")):
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
    return [], None   # sesja bez klawisza prawdy: tylko opis zachowania detektora (describe)


def live_decisions(data, signal):
    active, valid = f"out_{signal.column}_active", f"out_{signal.column}_valid"
    if active not in data:
        raise SystemExit(f"Nagranie nie ma wyjścia detektora {signal.column} (potok bez profilu?). Podaj --profile.")
    idx = np.arange(FRAME - 1, len(data["marker"]), FRAME)
    return idx, data[active][idx] >= 0.5, data[valid][idx] >= 0.5


def replay_decisions(data, profile, signal):
    tuned = (profile.get("fast") or {})
    if signal.key in tuned:   # szybki detektor z profilu: ten sam kod co w potoku, ramka po ramce
        fast = FastDetectors(profile["feature_config"]["posterior_channels"], {signal.key: tuned[signal.key]})
        x, raw_fz = np.column_stack([data[f"flt_{c}"] for c in UNICORN_LABELS]), data["raw_Fz"]
        idx, active, valid = [], [], []
        for end in range(FRAME, len(x) + 1, FRAME):
            eyes, jaw, warm = fast.push(x[end - FRAME:end], raw_fz[end - FRAME:end])
            idx.append(end - 1)
            active.append(eyes if signal.key == "eyes" else jaw)
            valid.append(warm)
        return np.array(idx), np.array(active), np.array(valid)
    engine = ProfileEngine(profile)
    button = None
    if signal.column not in engine.detectors:
        if signal.key != "jaw":
            raise SystemExit(f"Profil nie ma gotowej kalibracji {signal.column} (status inny niż ready).")
        button = JawButton(engine.config.eeg_channels, engine.config.jaw_band)   # jak w potoku bez kalibracji szczęki
    x = np.column_stack([data[f"flt_{c}"] for c in UNICORN_LABELS])
    window = Window(FS, x.shape[1])
    idx, active, valid = [], [], []
    for end in range(STEP, len(x) + 1, STEP):
        window.push(x[end - STEP:end])
        if button:
            _, is_active, is_valid = button.update(window)
        else:
            result = engine.update(window, end / FS)[signal.column]
            is_active, is_valid = result["active"], result["valid"]
        idx.append(end - 1)
        active.append(is_active)
        valid.append(is_valid)
    return np.array(idx), np.array(active), np.array(valid)


def drop_short(idx, active, seconds):
    """Włączenia krótsze niż seconds znikają: tak widzi je gra."""
    if seconds <= 0 or not len(idx):
        return active
    active = active.copy()
    edges = np.flatnonzero(np.diff(np.r_[0, active.astype(int), 0]))
    for a, b in zip(edges[::2], edges[1::2]):
        if (idx[min(b, len(idx) - 1)] - idx[a]) < seconds * FS:
            active[a:b] = False
    return active


def evaluate(idx, active, valid, intervals, signal, start_sample=0):
    keep = idx >= start_sample
    idx, active, valid = idx[keep], active[keep], valid[keep]
    STEP = int(np.median(np.diff(idx)))   # odstęp decyzji w próbkach
    truth = np.zeros(len(idx), bool)
    for a, b in intervals:
        truth |= (idx >= a) & (idx < b)

    closures = []
    for a, b in intervals:
        if (b - a) / FS < signal.min_s or b <= idx[0]:
            continue
        window = (idx >= a) & (idx < b + signal.grace_s * FS)
        hits = idx[window & active]
        after = (idx >= b) & (idx < b + 6 * FS)
        released = idx[after & ~active]
        closures.append({"start_s": round(a / FS, 1), "length_s": round((b - a) / FS, 1),
                         "detected": bool(len(hits)),
                         "close_latency_s": round((hits[0] - a) / FS, 2) if len(hits) else None,
                         "open_latency_s": round((released[0] - b) / FS, 2) if len(hits) and len(released) else None})

    # fałszywe włączenie: detektor włącza się poza [początek, koniec + grace_s] prawdziwego zdarzenia
    onsets = idx[np.flatnonzero(np.diff(np.r_[False, active].astype(int)) == 1)]
    allowed = np.zeros(len(idx), bool)
    for a, b in intervals:   # pół sekundy przed klawiszem też: detektor bywa szybszy niż palec
        allowed |= (idx >= a - 0.5 * FS) & (idx < b + signal.grace_s * FS)
    false_onsets = [int(s) for s in onsets if not allowed[np.searchsorted(idx, s)]]
    open_minutes = max((~truth).sum() * STEP / FS / 60, 1e-9)

    steady = np.ones(len(idx), bool)            # zgodność bez okresów przejściowych
    for a, b in intervals:
        for edge in (a, b):
            steady &= ~((idx >= edge) & (idx < edge + signal.transition_s * FS))
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


def describe(idx, active, valid, events, signal, start_sample=0):
    """Sesja bez prawdy (bez klawisza): co detektor robił i jak zareagowała gra."""
    keep = idx >= start_sample
    idx, active, valid = idx[keep], active[keep], valid[keep]
    STEP = int(np.median(np.diff(idx)))
    edges = np.diff(np.r_[False, active, False].astype(int))
    starts, ends = idx[np.flatnonzero(edges[:-1] == 1)], np.flatnonzero(edges == -1)
    ends = [idx[min(e, len(idx) - 1)] for e in ends]
    lengths = [float(b - a) / FS for a, b in zip(starts, ends)]
    minutes = len(idx) * STEP / FS / 60
    game = {name: sum(1 for e in events if e["name"] == name and e["sample"] and e["sample"] >= start_sample)
            for name in (signal.game[0],) + signal.extra_game + ("caught", "win", "loot")}
    scans = [e["value"] for e in events if e["name"] == "scan" and e["sample"] and e["sample"] >= start_sample]
    return {"minutes": round(minutes, 1), "closures": len(lengths),
            "closures_per_min": round(len(lengths) / max(minutes, 1e-9), 1),
            "closure_length_s": {"median": round(float(np.median(lengths)), 1) if lengths else None,
                                 "min": round(min(lengths), 1) if lengths else None,
                                 "max": round(max(lengths), 1) if lengths else None,
                                 "under_min": int(sum(l < signal.min_s for l in lengths))},
            "time_closed_fraction": round(float(active.mean()), 3),
            "valid_fraction": round(float(valid.mean()), 3),
            "game_events": game,
            "scan_radius_m_median": round(float(np.median(scans)), 1) if scans else None}, \
        (idx, active, valid, np.zeros(len(idx), bool))


def system_latency(data, events, signal):
    """Opóźnienie od decyzji detektora do reakcji gry, rozbite na potok i Unity (tylko tryb EEG).

    potok: próbka EEG, na której zmieniła się decyzja -> chwila wysłania do gry (znacznik pipe_*)
    gra:   wysłanie (zegar potoku) -> gra zmienia stan (zegar gry; ten sam komputer)
    razem: próbka zmiany decyzji -> próbka, na którą trafił znacznik reakcji gry
    Bez znaczników pipe_* (nagrania sprzed tej zmiany) liczy tylko "razem".
    """
    column = f"out_{signal.column}_active"
    if column not in data:
        return None
    act = data[column] >= 0.5
    edges = {True: np.flatnonzero(np.diff(act.astype(int)) == 1) + 1,
             False: np.flatnonzero(np.diff(act.astype(int)) == -1) + 1}
    mode, pipe, out = "eeg", {}, {"pipeline_s": [], "game_s": [], "total_s": []}
    for e in events:
        if e["sample"] is None:
            continue
        name = e["name"]
        if name.startswith("mode_"):
            mode = "eeg" if name == "mode_eeg" else "keyboard"
        if name in signal.pipe:
            on = name == signal.pipe[0]
            before = edges[on][edges[on] <= e["sample"]]
            if len(before) and (e["sample"] - before[-1]) / FS < 3:
                out["pipeline_s"].append((e["sample"] - before[-1]) / FS)
            pipe[on] = e
        elif name in signal.game and mode == "eeg":
            on = name == signal.game[0]
            before = edges[on][edges[on] <= e["sample"]]
            if len(before) and (e["sample"] - before[-1]) / FS < 3:
                out["total_s"].append((e["sample"] - before[-1]) / FS)
            sent = pipe.get(on)
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


def plot(path, title, series, intervals, false_at, signal):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    idx, active, valid, truth = series
    t = idx / FS
    fig, ax = plt.subplots(figsize=(16, 3.2))
    for a, b in intervals:
        ax.axvspan(a / FS, b / FS, color="tab:blue", alpha=0.18, lw=0)
    ax.step(t, active * 1.0, where="post", color="tab:orange", lw=1.2, label=f"detektor: {signal.on}")
    ax.step(t, ~valid * -0.2, where="post", color="tab:red", lw=0.8, label="sygnał nieważny")
    for s in false_at:
        ax.axvline(s, color="red", lw=0.8, ls=":")
    ax.set_ylim(-0.4, 1.3)
    ax.set_yticks([0, 1], [signal.off, signal.on])
    ax.set_xlabel("s od początku nagrania")
    ax.set_title(f"{title}  (niebieskie tło = prawda, czerwone kropki = fałszywe {signal.events})", fontsize=10)
    ax.legend(loc="upper right", fontsize=8)
    fig.tight_layout()
    fig.savefig(path, dpi=90)
    plt.close(fig)


def run(folder, data, events, signal, profile_path=None):
    """Ocena jednego sygnału na wczytanym nagraniu. False, gdy nagranie nie ma jego decyzji."""
    intervals, source = truth_intervals(events, len(data["marker"]), signal)
    try:
        if profile_path:
            idx, active, valid = replay_decisions(data, load_profile(profile_path), signal)
            mode = f"odtworzenie z profilem {profile_path}"
        else:
            idx, active, valid = live_decisions(data, signal)
            mode = "decyzje z nagrania (na żywo)"
    except SystemExit as error:
        print(f"{folder.name} · {signal.key}: pominięte. {error}")
        return False
    active = drop_short(idx, active, signal.ignore_s)
    start = 0   # całe nagranie: gra bywa włączona przed potokiem i jej session_start wtedy nie ma
    json_path, png_path = folder / f"eval_{signal.key}.json", folder / f"eval_{signal.key}.png"
    head = f"{folder.name} · {signal.key}"
    if source is None:   # bez klawisza prawdy: nie ma czym sprawdzić trafności, tylko opis
        report, series = describe(idx, active, valid, events, signal, start)
        report.update(recording=str(folder), signal=signal.key, truth=None, decisions=mode)
        json_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        plot(png_path, f"{head} (bez prawdy)", series, [], [], signal)
        r = report
        print(f"{head}: {r['minutes']} min · BEZ PRAWDY (brak klawisza {signal.key_name}) · {mode}")
        print(f"  {signal.events} wg detektora: {r['closures']} ({r['closures_per_min']}/min), długość mediana "
              f"{r['closure_length_s']['median']} s, krótszych niż {signal.min_s:g} s: {r['closure_length_s']['under_min']}")
        print(f"  {signal.on} {r['time_closed_fraction']:.0%} czasu · sygnał ważny {r['valid_fraction']:.0%}")
        print(f"  gra: {r['game_events']}" + (f" · zasięg skanu (mediana) {r['scan_radius_m_median']} m" if "scan" in signal.extra_game else ""))
        print(f"  Trafności nie da się policzyć bez klawisza {signal.key_name}; do oceny używaj sesji z klawiszem.")
        print_latency(system_latency(data, events, signal))
        return True
    report, series = evaluate(idx, active, valid, intervals, signal, start)
    report.update(recording=str(folder), signal=signal.key, truth=source, decisions=mode)
    json_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    plot(png_path, head, series, intervals, report["false_closures_at_s"], signal)
    r = report
    print(f"{head}: {r['minutes']} min · prawda: {source} · {mode}")
    print(f"  {signal.events} ≥{signal.min_s:g} s wykryte: {r['detected']}/{r['closures']}"
          + (f"  (pominięte w s: {r['missed']})" if r["missed"] else ""))
    print(f"  opóźnienie: włączenie {r['median_close_latency_s']} s, wyłączenie {r['median_open_latency_s']} s (mediana)")
    print(f"  fałszywe {signal.events}: {r['false_closures_per_open_min']} na minutę w stanie \"{signal.off}\"")
    print(f"  zgodność poza przejściami: {r['agreement_steady']} · sygnał ważny: {r['valid_fraction']:.0%}")
    print_latency(system_latency(data, events, signal))
    print(f"  -> {json_path}, {png_path}")
    return True


def main(default="all"):
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8", errors="replace")
    cli = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    cli.add_argument("folder", type=Path)
    cli.add_argument("--signal", choices=("eyes", "jaw", "all"), default=default)
    cli.add_argument("--profile", type=Path, help="odtwórz detektor z tym profilem zamiast decyzji z nagrania")
    args = cli.parse_args()

    data, events, _ = load_recording(args.folder)
    keys = list(SIGNALS) if args.signal == "all" else [args.signal]
    done = [run(args.folder, data, events, SIGNALS[key], args.profile) for key in keys]
    if not any(done):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
