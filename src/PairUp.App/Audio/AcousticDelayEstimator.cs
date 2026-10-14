using System.Numerics;

namespace PairUp.App.Audio;

public enum ProbeFailureKind
{
    None,
    InvalidRecording,
    InsufficientRecording,
    WeakSignal,
    UnrecognizedWeakSignal,
    UnrecognizedSignal,
    AmbiguousSignal,
    Clipped
}

public sealed record ProbeMatch(double OffsetMs, double Confidence, bool IsReliable, string Reason,
    ProbeFailureKind FailureKind = ProbeFailureKind.None)
{
    public bool CanRetryAtHigherVolume => FailureKind is ProbeFailureKind.WeakSignal or ProbeFailureKind.UnrecognizedWeakSignal;
}

/// <summary>Original normalized FFT correlation implementation; no external analysis code is copied.</summary>
public static class AcousticDelayEstimator
{
    public const int AnalysisRate = 16000;
    public const double ProbeDurationSeconds = 0.22;

    public static float[] CreateProbe(int sampleRate, float gain = 0.10f)
    {
        var count = (int)Math.Round(sampleRate * ProbeDurationSeconds);
        var samples = new float[count];
        const double fromHz = 650;
        const double toHz = 3600;
        var slope = (toHz - fromHz) / ProbeDurationSeconds;
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)sampleRate;
            var window = Math.Pow(Math.Sin(Math.PI * i / (count - 1)), 2);
            samples[i] = gain * (float)(window * Math.Sin(2 * Math.PI * (fromHz * t + slope * t * t / 2)));
        }
        return samples;
    }

    public static float[] Resample(float[] samples, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate) return samples;
        var result = new float[(int)Math.Floor(samples.Length * (double)targetRate / sourceRate)];
        for (var i = 0; i < result.Length; i++)
        {
            var position = i * (double)sourceRate / targetRate;
            var left = (int)position;
            var right = Math.Min(left + 1, samples.Length - 1);
            result[i] = (float)(samples[left] + (samples[right] - samples[left]) * (position - left));
        }
        return result;
    }

    public static ProbeMatch Find(float[] recording, int sampleRate, double minimumMs = 0, double maximumMs = 5000)
    {
        if (sampleRate < 8000 || recording.Length == 0 || recording.Any(x => !float.IsFinite(x)))
            return new(0, 0, false, "录音数据无效", ProbeFailureKind.InvalidRecording);
        var rawInput = Resample(recording, sampleRate, AnalysisRate);
        var input = RemoveOutOfBandNoise(rawInput);
        var probe = CreateProbe(AnalysisRate);
        var first = Math.Max(0, (int)(minimumMs * AnalysisRate / 1000));
        var last = Math.Min(input.Length - probe.Length, (int)(maximumMs * AnalysisRate / 1000));
        if (last < first) return new(0, 0, false, "录音时间不足", ProbeFailureKind.InsufficientRecording);

        // Check original-rate samples before any weak-signal return. Otherwise clipped noise
        // outside the sweep band could look quiet after filtering and incorrectly raise volume.
        var originalFirst = (int)Math.Round(first * (double)sampleRate / AnalysisRate);
        var originalEnd = Math.Min(recording.Length, (int)Math.Ceiling((last + probe.Length) * (double)sampleRate / AnalysisRate));
        if (HasClipping(recording, originalFirst, originalEnd, sampleRate))
            return new(0, 0, false, "录音过响失真，请降低音量或调整麦克风位置后再测", ProbeFailureKind.Clipped);

        var length = 1;
        while (length < input.Length + probe.Length - 1) length <<= 1;
        var signalSpectrum = new Complex[length];
        var probeSpectrum = new Complex[length];
        var energies = new double[input.Length + 1];
        var mean = input.Average(x => (double)x);
        for (var i = 0; i < input.Length; i++)
        {
            var value = input[i] - mean;
            signalSpectrum[i] = value;
            energies[i + 1] = energies[i] + value * value;
        }
        var probeEnergy = 0.0;
        for (var i = 0; i < probe.Length; i++)
        {
            probeSpectrum[i] = probe[probe.Length - 1 - i];
            probeEnergy += probe[i] * probe[i];
        }
        Transform(signalSpectrum, false);
        Transform(probeSpectrum, false);
        for (var i = 0; i < length; i++) signalSpectrum[i] *= probeSpectrum[i];
        Transform(signalSpectrum, true);

        var scores = new double[last - first + 1];
        var best = 0.0;
        var bestIndex = first;
        for (var i = first; i <= last; i++)
        {
            var energy = energies[i + probe.Length] - energies[i];
            var score = energy > 1e-9 ? Math.Min(1, Math.Abs(signalSpectrum[i + probe.Length - 1].Real) / Math.Sqrt(probeEnergy * energy)) : 0;
            scores[i - first] = score;
            if (score > best) { best = score; bestIndex = i; }
        }
        if (best < 0.24)
        {
            // Only quiet, valid captures qualify for automatic gain escalation. Strong noise
            // with no identifiable sweep needs human/environment checks, not louder playback.
            var searchEnergy = 0.0;
            var searchPeak = 0.0;
            var searchEnd = last + probe.Length;
            for (var i = first; i < searchEnd; i++)
            {
                var value = input[i];
                searchEnergy += value * value;
                searchPeak = Math.Max(searchPeak, Math.Abs(value));
            }
            var quiet = searchPeak < 0.01 && Math.Sqrt(searchEnergy / (searchEnd - first)) < 0.003;
            return new(0, best, false, quiet ? "收到的声音很弱，尚未识别到清楚的测试音" : "有明显声音，但未识别到清楚的测试音，请检查噪声、音箱和麦克风位置",
                quiet ? ProbeFailureKind.UnrecognizedWeakSignal : ProbeFailureKind.UnrecognizedSignal);
        }
        var second = 0.0;
        for (var i = first; i <= last; i++)
            // Only exclude the sweep's narrow autocorrelation main lobe, not room-scale echoes.
            if (Math.Abs(i - bestIndex) > AnalysisRate * 0.002) second = Math.Max(second, scores[i - first]);
        if (second >= best * 0.88) return new(0, best, false, "收到多个难以区分的测试音或较强回声，请让环境安静后重测", ProbeFailureKind.AmbiguousSignal);
        var peak = 0.0;
        for (var i = bestIndex; i < bestIndex + probe.Length; i++)
            peak = Math.Max(peak, Math.Abs(input[i]));
        // Separate matched-probe strength from background noise in the same window.
        var matchedAmplitude = Math.Abs(signalSpectrum[bestIndex + probe.Length - 1].Real) / probeEnergy * 0.10;
        if (peak < 0.0003 || matchedAmplitude < 0.0003)
            return new(0, best, false, "测试音过小，无法可靠校准", ProbeFailureKind.WeakSignal);

        var fraction = 0.0;
        var index = bestIndex - first;
        if (index > 0 && index + 1 < scores.Length)
        {
            var a = scores[index - 1]; var b = scores[index]; var c = scores[index + 1];
            var denominator = a - 2 * b + c;
            if (Math.Abs(denominator) > 1e-12) fraction = Math.Clamp(0.5 * (a - c) / denominator, -0.5, 0.5);
        }
        return new((bestIndex + fraction) * 1000 / AnalysisRate, best, true, "测试音清楚");
    }

    internal static bool HasClipping(float[] recording, int first, int end, int sampleRate)
    {
        var window = Math.Max(1, (int)Math.Round(ProbeDurationSeconds * sampleRate));
        var clipped = 0;
        for (var i = first; i < end; i++)
        {
            if (Math.Abs(recording[i]) >= 0.995) clipped++;
            if (i - first >= window && Math.Abs(recording[i - window]) >= 0.995) clipped--;
            if (clipped > window * 0.02) return true;
        }
        return false;
    }

    private static float[] RemoveOutOfBandNoise(float[] input)
    {
        var length = 1;
        while (length < input.Length) length <<= 1;
        var spectrum = new Complex[length];
        for (var i = 0; i < input.Length; i++) spectrum[i] = input[i];
        Transform(spectrum, false);
        for (var i = 0; i < length; i++)
        {
            var frequency = Math.Min(i, length - i) * (double)AnalysisRate / length;
            // Symmetric real weights retain timing and polarity; keep the complete sweep band.
            var weight = frequency < 500 || frequency > 3900 ? 0
                : frequency < 650 ? (frequency - 500) / 150
                : frequency > 3600 ? (3900 - frequency) / 300 : 1;
            spectrum[i] *= weight;
        }
        Transform(spectrum, true);
        var result = new float[input.Length];
        for (var i = 0; i < input.Length; i++) result[i] = (float)spectrum[i].Real;
        return result;
    }

    private static void Transform(Complex[] values, bool inverse)
    {
        var length = values.Length;
        for (int i = 1, j = 0; i < length; i++)
        {
            var bit = length >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (var size = 2; size <= length; size <<= 1)
        {
            var angle = (inverse ? 2 : -2) * Math.PI / size;
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < length; start += size)
            {
                var factor = Complex.One;
                for (var j = 0; j < size / 2; j++)
                {
                    var even = values[start + j]; var odd = values[start + j + size / 2] * factor;
                    values[start + j] = even + odd;
                    values[start + j + size / 2] = even - odd;
                    factor *= step;
                }
            }
        }
        if (inverse) for (var i = 0; i < length; i++) values[i] /= length;
    }
}

public sealed record AcousticMeasurement(string DeviceId, string Name, double ArrivalMs, double Confidence, double SpreadMs);
public sealed record AcousticCalibrationPlan(bool CanApply, Dictionary<string, double> DelaysMs, string Explanation);

public static class AcousticAlignment
{
    public static AcousticCalibrationPlan Plan(string sourceId, IReadOnlyList<AcousticMeasurement> measurements)
    {
        if (measurements.Count < 2 || measurements.Select(x => x.DeviceId).Distinct().Count() != measurements.Count)
            return new(false, new(), "缺少音源或输出设备的测量，原延迟已保留");
        var source = measurements.SingleOrDefault(x => x.DeviceId == sourceId);
        if (source is null)
            return new(false, new(), "缺少音源或输出设备的测量，原延迟已保留");
        var result = new Dictionary<string, double>();
        foreach (var measurement in measurements)
        {
            if (!double.IsFinite(measurement.ArrivalMs) || !double.IsFinite(measurement.Confidence) || !double.IsFinite(measurement.SpreadMs) ||
                measurement.Confidence < 0.24 || measurement.Confidence > 1 || measurement.SpreadMs < 0 || measurement.SpreadMs > 20)
                return new(false, new(), $"“{measurement.Name}”的测量不稳定，原延迟已保留，请重新测量");
            if (measurement.DeviceId == sourceId) continue;
            var compensation = source.ArrivalMs - measurement.ArrivalMs;
            if (compensation < 0)
                return new(false, new(), $"“{measurement.Name}”比音源慢 {-compensation:0} 毫秒。当前音源直接播放，无法给它增加延迟；请改用不发声的输出作为音源，再让 PairUp 转发所有音箱后校准。原延迟已保留。");
            if (compensation > FixedDelaySampleProvider.MaximumDelayMs)
                return new(false, new(), $"“{measurement.Name}”需要补偿 {compensation:0} 毫秒，超过 3000 毫秒范围，原延迟已保留");
            result[measurement.DeviceId] = Math.Max(0, Math.Round(compensation, 1, MidpointRounding.AwayFromZero));
        }
        return new(true, result, "已按实际听音位置计算延迟");
    }
}
