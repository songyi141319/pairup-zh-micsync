"""Sanity checks for the research harness; no hardware or actual recordings."""
import unittest

import numpy as np

import compare


class LabChecks(unittest.TestCase):
    def test_upstream_lag_sign_and_sample_units(self):
        rng = np.random.default_rng(20261014)
        probe = rng.normal(size=2000)
        audio = np.pad(probe, (3217, 1000))
        for phat in [False, True]:
            full = compare.load_sync().correlate(audio, probe, phat=phat)
            delay = np.argmax(np.abs(full)) - (len(probe) - 1)
            self.assertEqual(delay, 3217)

    def test_zero_curve_does_not_pass(self):
        result = compare.decide(np.zeros(40000), np.zeros(48000))
        self.assertFalse(result["accepted"])

    def test_equal_peaks_do_not_pass(self):
        curve = np.zeros(40000)
        curve[5000] = curve[7000] = 1
        self.assertFalse(compare.decide(curve, np.zeros(48000))["accepted"])

    def test_clip_guard(self):
        audio = np.zeros(48000)
        audio[1000:3000] = 1
        curve = np.zeros(40000); curve[6000] = 1
        self.assertEqual(compare.decide(curve, audio)["reason"], "clipping_or_invalid")

    def test_fixture_reproducible(self):
        probe = np.arange(8000) / 80000
        a, first = compare.recording(probe, "far_noisy", 7)
        b, second = compare.recording(probe, "far_noisy", 7)
        np.testing.assert_array_equal(a, b)
        self.assertEqual(first, second)

    def test_missing_and_duplicate_results_are_rejected(self):
        expected = [f"{name}-{seed}" for name in compare.SCENARIOS for seed in range(compare.REPETITIONS)]
        baseline = [dict(Id=key) for key in expected]
        rows = [dict(id=key, scenario=key.rsplit("-", 1)[0], seed=int(key.rsplit("-", 1)[1]), method=method)
                for key in expected for method in ["gcc_phat_short", "matched_short_control", "matched_long", "pyfar_ess"]]
        compare.validate_results(rows, baseline)
        for bad in [baseline[:-1], baseline + baseline[:1]]:
            with self.assertRaises(ValueError):
                compare.validate_results(rows, bad)
        for bad in [rows[:-1], rows + rows[:1]]:
            with self.assertRaises(ValueError):
                compare.validate_results(bad, baseline)


if __name__ == "__main__":
    unittest.main()
