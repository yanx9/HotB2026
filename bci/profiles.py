"""Versioned calibration profiles, conservative fitting and shared inference.

No game actions belong here. A score is distance from the calibrated baseline,
not a classifier probability. Validation holds out entire paired trials.
"""

import json
import math
import os
import tempfile
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

from processing import FeatureConfig, SIGNALS, extract_feature, feature_window

SCHEMA_VERSION = 1
FEATURE_VERSION = "unicorn-spectral-v1"


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def new_profile(subject, config, stream, simulated=False):
    return {"schema_version": SCHEMA_VERSION, "feature_version": FEATURE_VERSION,
            "subject": subject, "created_at": utc_now(), "updated_at": utc_now(),
            "simulated": bool(simulated), "stream": stream,
            "feature_config": config.to_dict(), "calibrations": {}}


def fit_calibration(name, trials, protocol):
    """Each trial has non-overlapping rest/active windows; never random-split windows."""
    if name not in SIGNALS:
        raise ValueError("Nieznana kalibracja.")
    usable = [t for t in trials if len(t["rest"]) >= 2 and len(t["active"]) >= 2]
    result = {"signal": name, "calibrated_at": utc_now(), "protocol": protocol,
              "status": "insufficient_data", "reason": "Potrzeba co najmniej 6 pełnych par spoczynek–zadanie i 60% poprawnych par.",
              "usable_trials": len(usable), "total_trials": len(trials),
              "window_s": feature_window(name), "trials": trials}
    if len(usable) < max(6, math.ceil(len(trials) * 0.6)):
        return result
    n_test = max(2, math.ceil(len(usable) * 0.25))
    train, test = usable[:-n_test], usable[-n_test:]
    # Trial medians give equal weight to trials even when some windows were rejected.
    rest = np.array([np.median(t["rest"]) for t in train])
    active = np.array([np.median(t["active"]) for t in train])
    if not np.isfinite(np.concatenate([rest, active])).all():
        raise ValueError("Niefinitywne wartości cech.")
    rest_center, active_center = float(np.median(rest)), float(np.median(active))
    delta = active_center - rest_center
    result.update(rest_center=rest_center, active_center=active_center,
                  training_trials=[t["index"] for t in train],
                  validation_trials=[t["index"] for t in test])
    if delta <= 0.05:
        result.update(status="not_separable", reason="Brak oczekiwanej zmiany cechy względem spoczynku. Powtórz kalibrację.")
        return result
    # Freeze parameters fitted on training data; held-out trials are not used to tune them.
    enter = max(rest_center + 0.55 * delta, float(np.percentile(rest, 95)))
    release = rest_center + 0.3 * delta
    rest_test = np.array([np.median(t["rest"]) for t in test])
    active_test = np.array([np.median(t["active"]) for t in test])
    specificity = float(np.mean(rest_test < enter))
    sensitivity = float(np.mean(active_test >= enter))
    accuracy = (specificity + sensitivity) / 2
    # Also evaluate every held-out window (not just trial means).
    window_specificity = float(np.mean([v < enter for t in test for v in t["rest"]]))
    window_sensitivity = float(np.mean([v >= enter for t in test for v in t["active"]]))
    window_accuracy = (window_specificity + window_sensitivity) / 2
    train_accuracy = float((np.mean(rest < enter) + np.mean(active >= enter)) / 2)
    separation = delta / max(float(np.std(rest)), float(np.std(active)), 0.05)
    accepted = (accuracy >= 0.75 and window_accuracy >= 0.70 and specificity >= 0.75
                and sensitivity >= 0.5 and train_accuracy >= 0.75 and separation >= 1.0 and enter < active_center)
    result.update(
        status="ready" if accepted else "not_separable",
        reason="Parametry gotowe do testu sterowania; to nie walidacja kliniczna." if accepted else
               "Rozdzielenie zadania od spoczynku jest zbyt słabe w próbach kontrolnych. Nie używamy tego detektora.",
        threshold_raw=enter, release_raw=release, scale=delta,
        threshold=float(np.clip((enter - rest_center) / delta, 0, 1)),
        release_threshold=0.3, dwell_s=0.2 if name == "jaw" else 0.35,
        smoothing_s=0.08 if name == "jaw" else 0.2,
        validation={"method": "chronological_holdout_paired_trials", "balanced_accuracy": accuracy,
                    "sensitivity": sensitivity, "specificity": specificity,
                    "window_balanced_accuracy": window_accuracy,
                    "training_balanced_accuracy": train_accuracy, "separation": separation,
                    "note": "Mały test wewnątrz sesji; osobne kalibracje rąk nie walidują rozróżniania lewa/prawa."})
    return result


def validate_profile(profile):
    if profile.get("schema_version") != SCHEMA_VERSION or profile.get("feature_version") != FEATURE_VERSION:
        raise ValueError("Nieobsługiwana wersja profilu / ekstrakcji cech.")
    config = FeatureConfig(**profile["feature_config"]).validate()
    if type(profile.get("simulated")) is not bool or not isinstance(profile.get("subject"), str):
        raise ValueError("Profil nie zawiera poprawnego identyfikatora / trybu symulacji.")
    if not isinstance(profile.get("calibrations"), dict):
        raise ValueError("Brak tabeli kalibracji.")
    for name, model in profile["calibrations"].items():
        if name not in SIGNALS or model.get("signal") != name:
            raise ValueError("Nieznana lub niespójna nazwa sygnału w profilu.")
        if model.get("status") not in ("ready", "not_separable", "insufficient_data"):
            raise ValueError("Nieznany status kalibracji.")
        if model["status"] != "ready":
            continue
        for key in ("rest_center", "active_center", "scale", "threshold_raw", "release_raw",
                    "threshold", "release_threshold", "dwell_s", "smoothing_s", "window_s"):
            if not isinstance(model.get(key), (int, float)) or not math.isfinite(model[key]):
                raise ValueError(f"Niepoprawny parametr {name}.{key}.")
        if (model["scale"] <= 0 or model["active_center"] <= model["rest_center"]
                or not model["rest_center"] < model["release_raw"] < model["threshold_raw"]
                or not 0 <= model["release_threshold"] < model["threshold"] <= 1
                or model["dwell_s"] < 0 or model["smoothing_s"] < 0
                or model["window_s"] != feature_window(name)):
            raise ValueError(f"Niespójne progi lub okno {name}.")
    return config


def load_profile(path):
    with open(path, encoding="utf-8") as f:
        profile = json.load(f)
    validate_profile(profile)
    return profile


def save_profile(path, profile):
    validate_profile(profile)
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    profile["updated_at"] = utc_now()
    fd, temporary = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(profile, f, ensure_ascii=False, indent=2, allow_nan=False)
            f.write("\n")
            f.flush()
            os.fsync(f.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


class Detector:
    def __init__(self, model):
        self.model = model
        self.reset()

    def reset(self):
        self.value = 0.0
        self.active = False
        self.since = None
        self.last_time = None

    def update(self, raw, now):
        if raw is None:
            self.reset()
            return 0.0, False
        m = self.model
        target = float(np.clip((raw - m["rest_center"]) / m["scale"], 0, 1))
        dt = 0.1 if self.last_time is None else max(0, now - self.last_time)
        self.last_time = now
        blend = 1 if m["smoothing_s"] == 0 else 1 - math.exp(-dt / m["smoothing_s"])
        self.value += blend * (target - self.value)
        if self.active:
            if self.value < m["release_threshold"]:
                self.active, self.since = False, None
        elif self.value >= m["threshold"]:
            if self.since is None:
                self.since = now
            if now - self.since >= m["dwell_s"]:
                self.active = True
        else:
            self.since = None
        return self.value, self.active


class ProfileEngine:
    """Named metrics and states; game bindings stay in Unity. Invalid data is neutral."""

    def __init__(self, profile):
        self.config = validate_profile(profile)
        self.detectors = {name: Detector(model) for name, model in profile["calibrations"].items()
                          if model["status"] == "ready"}

    def update(self, window, now, fresh=True):
        results = {}
        # A calibrated jaw event gates motor/eye inference so EMG isn't rewarded as MI.
        for name in ("jaw", "eyes_closed", "right_hand", "left_hand"):
            detector = self.detectors.get(name)
            raw, reason = (extract_feature(window, name, self.config) if fresh and detector else
                           (None, "stale" if not fresh else "uncalibrated"))
            if name != "jaw" and results["jaw"]["active"]:
                raw, reason = None, "jaw_artifact"
            value, active = detector.update(raw, now) if detector else (0.0, False)
            results[name] = {"value": value, "active": active, "valid": raw is not None, "reason": reason}
        right, left = results["right_hand"], results["left_hand"]
        # One binary model is not enough to infer a signed left/right axis.
        lateral_valid = right["valid"] and left["valid"] and not (right["active"] and left["active"])
        lateral = (right["value"] - left["value"]) if lateral_valid else 0.0
        results["laterality"] = {"value": lateral, "active": False, "valid": lateral_valid,
                                  "reason": None if lateral_valid else "missing_or_ambiguous_hand_models"}
        if right["active"] and left["active"]:
            right["active"] = left["active"] = False
        return results

def assert_profile_source(profile, stream, simulated, config=None, allow_simulated=False):
    """Profil pasuje tylko do toru, na którym powstał: to samo źródło, filtry i ustawienia cech."""
    saved = validate_profile(profile)
    if profile["simulated"] and not (allow_simulated and simulated):
        raise ValueError("Profil syntetyczny można uruchamiać tylko ze źródłem syntetycznym (--fake --allow-simulated).")
    if simulated and not profile["simulated"]:
        raise ValueError("Profil rzeczywisty nie pasuje do źródła syntetycznego.")
    if config is not None and saved.to_dict() != config.to_dict():
        raise ValueError("Ustawienia cech (próg artefaktów, pasmo szczęki) różnią się od profilu. "
                         "Użyj tych samych ustawień albo wykonaj ponowną kalibrację.")
    if profile["stream"].get("transport") != stream["transport"]:
        raise ValueError("Profil pochodzi z innego toru (LSL albo surowe UDP). Wykonaj kalibrację przez g.Pype.")
    if profile["stream"].get("filters") != stream["filters"]:
        raise ValueError("Filtry potoku różnią się od zapisanych w profilu. Wykonaj ponowną kalibrację.")
