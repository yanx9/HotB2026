"""Trial state machine on the sample clock; no GUI, g.Pype or game dependencies."""

from dataclasses import asdict, dataclass

import numpy as np

from processing import Window, extract_feature, feature_window


@dataclass(frozen=True)
class Protocol:
    trials: int = 10
    rest_s: float = 6.0
    active_s: float = 6.0
    settle_s: float = 1.0
    prepare_s: float = 5.0

    @classmethod
    def for_signal(cls, name):
        if name == "jaw":
            return cls(rest_s=4, active_s=2, settle_s=0.5)
        if name in ("left_hand", "right_hand"):
            return cls(trials=12, rest_s=6, active_s=6)
        # Oczy: reakcja na ton (~0,4 s) + narastanie / blokowanie alfy (~0,5-1 s); z 1 s pierwsze
        # okno łapało jeszcze przejście i zaniżało różnicę między fazami.
        return cls(settle_s=1.5)

    def validate(self, name):
        if not 6 <= self.trials <= 40:
            raise ValueError("Liczba powtórzeń musi być w zakresie 6–40.")
        for value in (self.rest_s, self.active_s, self.settle_s, self.prepare_s):
            if not np.isfinite(value) or value < 0:
                raise ValueError("Niepoprawny czas fazy.")
        minimum = self.settle_s + 2 * feature_window(name)
        if min(self.rest_s, self.active_s) < minimum:
            raise ValueError("Faza musi mieścić czas ustalenia i co najmniej dwa niezależne okna.")
        if name == "jaw" and self.active_s > 3:
            raise ValueError("Próba szczęki powinna być krótka (maks. 3 s).")
        return self


class CalibrationSession:
    def __init__(self, name, config, now, protocol=None):
        self.name, self.config = name, config
        self.protocol = (protocol or Protocol.for_signal(name)).validate(name)
        self.phase = "prepare"
        self.phase_started = now
        self.phase_end = now + self.protocol.prepare_s
        self.trial = 0
        self.trials = []
        self.events = []
        self.window = Window(config.sampling_rate, config.channel_count)
        self.next_feature = now
        self.last_sample = None
        self.completed = False
        self.cancelled = False
        self.rejected = 0
        self.latest_feature = None
        self.quality = "warming_up"
        self.events.append({"phase": "prepare", "trial": 0, "t": now})

    def advance(self, now):
        """At most one transition; an app stall must never silently skip an unseen cue."""
        if self.completed or self.cancelled or now < self.phase_end:
            return False
        if self.phase == "prepare" or self.phase == "active":
            if self.trial >= self.protocol.trials:
                self.phase, self.completed = "complete", True
                self.phase_started = self.phase_end = now
            else:
                self.trial += 1
                self.trials.append({"index": self.trial, "rest": [], "active": [], "rejected": 0})
                self.phase = "rest"
                self.phase_started, self.phase_end = now, now + self.protocol.rest_s
        else:
            self.phase = "active"
            self.phase_started, self.phase_end = now, now + self.protocol.active_s
        self.window.clear()
        self.last_sample = None
        self.next_feature = now + self.protocol.settle_s + feature_window(self.name) - 1 / self.config.sampling_rate
        self.events.append({"phase": self.phase, "trial": self.trial, "t": now})
        return True

    def ingest(self, samples, timestamps):
        if self.phase not in ("rest", "active") or self.completed or self.cancelled:
            return
        # Timestamps are the source's uniform sample grid on the host monotonic clock.
        for sample, timestamp in zip(samples, timestamps):
            if not self.phase_started + self.protocol.settle_s <= timestamp <= self.phase_end:
                continue
            if self.last_sample is not None and not 0 < timestamp - self.last_sample <= 3 / self.config.sampling_rate:
                self.window.clear()
            self.last_sample = timestamp
            self.window.push(np.asarray(sample)[None, :])
            if timestamp + 1e-8 < self.next_feature:
                continue
            raw, reason = extract_feature(self.window, self.name, self.config)
            if reason == "warming_up":
                # The first post-settle sample may arrive just after the planned
                # boundary. Wait for the full window rather than losing a trial.
                continue
            self.latest_feature, self.quality = raw, reason or "ok"
            if raw is not None:
                self.trials[-1][self.phase].append(raw)
            else:
                self.rejected += 1
                self.trials[-1]["rejected"] += 1
            self.next_feature = timestamp + feature_window(self.name)

    def snapshot(self, now):
        return {"name": self.name, "phase": self.phase, "trial": self.trial,
                "total_trials": self.protocol.trials, "remaining_s": max(0, self.phase_end - now),
                "feature": self.latest_feature, "quality": self.quality,
                "accepted_windows": sum(len(t["rest"]) + len(t["active"]) for t in self.trials),
                "rejected_windows": self.rejected, "protocol": asdict(self.protocol)}


class DemoSource:
    """Explicitly synthetic data for repeatable UI/bridge tests; never a real profile."""

    def __init__(self):
        from processing import FeatureConfig
        self.config = FeatureConfig()
        self.stream = {"name": "DEMO — syntetyczny Unicorn", "source_id": "fake-calibration-demo", "transport": "demo"}
        self.simulated = True
        self.rng = np.random.default_rng(42)
        self.state = "rest"

    def samples(self, timestamps, state="rest"):
        t = np.asarray(timestamps)
        x = self.rng.normal(0, 1.5, (len(t), 8))
        x += (5 * np.sin(2 * np.pi * 10 * t) + 3 * np.sin(2 * np.pi * 20 * t))[:, None]
        if state == "eyes_closed":
            x[:, 4:] += 15 * np.sin(2 * np.pi * 10 * t)[:, None]
        if state in ("left_hand", "right_hand"):
            idx = 1 if state == "right_hand" else 3
            x[:, idx] -= 4 * np.sin(2 * np.pi * 10 * t) + 2.5 * np.sin(2 * np.pi * 20 * t)
        if state == "jaw":
            x += 35 * np.sin(2 * np.pi * 72 * t)[:, None]
        return x
