"""Offline candidate screening, NOT a calibration engine or hardware acceptance.

Actual upstream pyroomacoustics correlation and pyfar sweep/deconvolution are
used with an explicitly experimental common peak gate. The production C#
estimator is called separately, unchanged. Known fixture truth is used only
for scoring, never passed to a detector. No audio/clipboard/device APIs.
"""
from __future__ import annotations

import argparse
import csv
import importlib.util
import json
import platform
from collections import Counter
from pathlib import Path

import numpy as np
import pyfar as pf
from scipy import signal

RATE = 16000
MINIMUM = 100
MAXIMUM = 1600
REPETITIONS = 12
SCENARIOS = {
    "clean": dict(gain=1, noise=.0005),
    "quiet_clean": dict(gain=.0015, noise=.000001),
    "far_noisy": dict(gain=.005, noise=.0015),
    "very_weak_noisy": dict(gain=.0008, noise=.001),
    "muffled": dict(gain=.03, noise=.001, lowpass=True),
    "moderate_echo": dict(gain=.03, noise=.001, echo=.7),
    "stronger_echo": dict(gain=.01, noise=.001, echo=1.5),
    "clock_drift_800ppm": dict(gain=.03, noise=.001, ppm=800),
    "noise_only": dict(gain=0, noise=.001, absent=True),
    "silence": dict(gain=0, noise=0, absent=True),
    "hum_only": dict(gain=0, noise=.00001, hum=.01, absent=True),
    "wrong_probe": dict(gain=.2, noise=.0001, wrong=True, absent=True),
    # Same waveform from a different speaker: an honest blind-spot test. A
    # single unencoded probe cannot establish speaker identity from delay alone.
    "silent_target_source_echo": dict(gain=.1, noise=.0001, absent=True),
    "clipping": dict(gain=40, noise=.0001, invalid=True),
}


def load_sync():
    path = Path(__file__).parent / "vendor" / "pyroomacoustics_sync.py"
    spec = importlib.util.spec_from_file_location("upstream_sync", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def probes(root):
    short = np.fromfile(root / "legacy-probe.bin", dtype="<f4").astype(float)
    t = np.arange(RATE) / RATE
    long_linear = .1 * np.sin(np.pi * np.arange(RATE) / (RATE - 1)) ** 2
    long_linear *= np.sin(2 * np.pi * (650 * t + (3600 - 650) * t * t / 2))
    exponential = pf.signals.exponential_sweep_time(
        RATE, [650, 3600], n_fade_out=160, amplitude=.1, sampling_rate=RATE
    ).time[0]
    # Keep the same peak amplitude; the one-second methods spend more time and
    # energy, deliberately. Results must not be described as equal-energy gains.
    return {"short": short, "long": long_linear, "ess": exponential}


def recording(probe, scenario, seed):
    cfg = SCENARIOS[scenario]
    rng = np.random.default_rng(seed)
    arrival_ms = 187.3 + (seed % 5) * 47.1
    n = RATE * 3
    output = rng.normal(0, cfg["noise"], n)
    waveform = probe[::-1] if cfg.get("wrong") else probe
    if cfg.get("lowpass"):
        waveform = signal.sosfilt(signal.butter(2, 1000, fs=RATE, output="sos"), waveform)
    dilation = 1 + cfg.get("ppm", 0) / 1e6

    def add(delay_ms, gain):
        positions = (np.arange(n) - delay_ms * RATE / 1000) / dilation
        output[:] += gain * np.interp(positions, np.arange(len(waveform)), waveform, left=0, right=0)

    add(arrival_ms, cfg["gain"])
    if cfg.get("echo"):
        add(arrival_ms + 60, cfg["gain"] * cfg["echo"])
    if cfg.get("hum"):
        output += cfg["hum"] * np.sin(2 * np.pi * 100 * np.arange(n) / RATE)
    return np.clip(output, -1, 1), arrival_ms


def decide(curve, recording):
    """Common candidate gate; no truth, gain, or scenario name is available."""
    if not np.all(np.isfinite(recording)) or np.count_nonzero(np.abs(recording) >= .995) > RATE * .22 * .02:
        return dict(accepted=False, delay_ms=None, reason="clipping_or_invalid")
    first = int(MINIMUM * RATE / 1000)
    last = int(MAXIMUM * RATE / 1000)
    values = np.abs(curve[first:last + 1])
    if len(values) == 0 or not np.all(np.isfinite(values)):
        return dict(accepted=False, delay_ms=None, reason="invalid_curve")
    best = int(np.argmax(values))
    peak = values[best]
    outside = np.abs(np.arange(len(values)) - best) > int(.002 * RATE)
    background = values[outside]
    median = np.median(background)
    mad = np.median(np.abs(background - median)) * 1.4826
    prominence = (peak - median) / max(mad, 1e-30)
    ratio = np.max(background) / max(peak, 1e-30)
    accepted = bool(peak > 1e-20 and prominence >= 10 and ratio < .88)
    offset = first + best
    if 0 < best < len(values) - 1:
        a, b, c = values[best - 1:best + 2]
        denominator = a - 2 * b + c
        if abs(denominator) > 1e-30:
            offset += np.clip(.5 * (a - c) / denominator, -.5, .5)
    return dict(accepted=accepted, delay_ms=float(offset * 1000 / RATE),
                reason="accepted" if accepted else "weak_or_ambiguous",
                prominence=float(prominence), competitor_ratio=float(ratio))


def candidate(name, probe, audio, sync):
    if name == "gcc_phat_short":
        if not np.any(audio):
            return dict(accepted=False, delay_ms=None, reason="silence")
        full = sync.correlate(audio, probe, phat=True)
        curve = full[len(probe) - 1:]
    elif name in {"matched_short_control", "matched_long"}:
        full = sync.correlate(audio, probe, phat=False)
        curve = full[len(probe) - 1:]
    elif name == "pyfar_ess":
        ir = pf.dsp.deconvolve(pf.Signal(audio, RATE), pf.Signal(probe, RATE),
                               fft_length=len(audio) + len(probe) - 1,
                               frequency_range=[650, 3600])
        curve = ir.time[0]
    else:
        raise ValueError(f"Unknown candidate: {name}")
    return decide(curve, audio)


def prepare(root):
    root.mkdir(parents=True, exist_ok=True)
    waves = probes(root)
    sync = load_sync()
    cases, rows = [], []
    for scenario in SCENARIOS:
        for seed in range(REPETITIONS):
            identifier = f"{scenario}-{seed}"
            old, truth = recording(waves["short"], scenario, seed)
            filename = f"{identifier}.bin"
            old.astype("<f4").tofile(root / filename)
            cases.append(dict(Id=identifier, File=filename))
            for method, wave in (("gcc_phat_short", "short"), ("matched_short_control", "short"), ("matched_long", "long"), ("pyfar_ess", "ess")):
                audio, _ = recording(waves[wave], scenario, seed)
                result = candidate(method, waves[wave], audio, sync)
                rows.append(dict(id=identifier, scenario=scenario, seed=seed, method=method, truth_ms=truth, **result))
    (root / "cases.json").write_text(json.dumps(cases), encoding="utf-8")
    (root / "candidates.json").write_text(json.dumps(rows, indent=2, allow_nan=False), encoding="utf-8")
    (root / "environment.json").write_text(json.dumps(dict(python=platform.python_version(), numpy=np.__version__,
        pyfar=pf.__version__, repetitions=REPETITIONS, sample_rate=RATE, audio_devices_opened=False), indent=2), encoding="utf-8")
    print(f"Prepared {len(cases)} synthetic cases, {len(rows)} candidate measurements; no audio devices.")


def summarize(root):
    rows = json.loads((root / "candidates.json").read_text(encoding="utf-8"))
    baseline = json.loads((root / "baseline.json").read_text(encoding="utf-8"))
    validate_results(rows, baseline)
    for value in baseline:
        scenario, seed = value["Id"].rsplit("-", 1)
        rows.append(dict(id=value["Id"], scenario=scenario, seed=int(seed), method="current_csharp",
                         truth_ms=187.3 + (int(seed) % 5) * 47.1, accepted=value["IsReliable"],
                         delay_ms=value["OffsetMs"], reason=value["Reason"]))
    summaries = []
    for row in rows:
        cfg = SCENARIOS[row["scenario"]]
        should_reject = cfg.get("absent", False) or cfg.get("invalid", False)
        error = None if row["delay_ms"] is None else abs(row["delay_ms"] - row["truth_ms"])
        row["error_ms"] = error
        row["verdict"] = ("false_accept" if should_reject else "wrong_delay" if error > 2 else "correct") if row["accepted"] else ("correct_reject" if should_reject else "rejected")
    for scenario in SCENARIOS:
        for method in ["current_csharp", "gcc_phat_short", "matched_short_control", "matched_long", "pyfar_ess"]:
            group = [r for r in rows if r["scenario"] == scenario and r["method"] == method]
            counts = {v: sum(r["verdict"] == v for r in group) for v in ["correct", "rejected", "wrong_delay", "correct_reject", "false_accept"]}
            summaries.append(dict(scenario=scenario, method=method, **counts))
    (root / "results.json").write_text(json.dumps(rows, indent=2, ensure_ascii=False, allow_nan=False), encoding="utf-8")
    with (root / "summary.csv").open("w", newline="", encoding="utf-8-sig") as file:
        writer = csv.DictWriter(file, fieldnames=summaries[0].keys())
        writer.writeheader(); writer.writerows(summaries)
    print(json.dumps(summaries, ensure_ascii=False, indent=2))


def validate_results(rows, baseline):
    expected = {f"{scenario}-{seed}" for scenario in SCENARIOS for seed in range(REPETITIONS)}
    if Counter(row["Id"] for row in baseline) != Counter({key: 1 for key in expected}):
        raise ValueError("Missing, duplicate, or unexpected baseline result")
    methods = ["gcc_phat_short", "matched_short_control", "matched_long", "pyfar_ess"]
    wanted = Counter({(key, method): 1 for key in expected for method in methods})
    if Counter((row["id"], row["method"]) for row in rows) != wanted:
        raise ValueError("Missing, duplicate, or unexpected candidate result")
    for row in rows:
        scenario, seed = row["id"].rsplit("-", 1)
        if row["scenario"] != scenario or row["seed"] != int(seed):
            raise ValueError("Candidate scenario metadata mismatch")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["prepare", "summarize"])
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    (prepare if args.mode == "prepare" else summarize)(args.root)
