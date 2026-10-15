namespace PairUp.App.Audio;

/// <summary>Only a quiet signal in a continuous valid recording permits a louder retry.</summary>
public sealed class AcousticWeakSignalException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
}

public static class AcousticProbeAnalysis
{
    public static (double ArrivalMs, double Confidence) Analyze(CapturedAudio reference, CapturedAudio microphone,
        double separationMs = 0)
    {
        if (!double.IsFinite(separationMs) || separationMs < 0 || separationMs > FixedDelaySampleProvider.MaximumDelayMs)
            throw new ArgumentOutOfRangeException(nameof(separationMs));
        EnsureRawSignalSafe(reference, 200, 1500, "电脑内部参考声音");
        var refMatch = AcousticDelayEstimator.Find(reference.Samples, reference.SampleRate, 200, 1500);
        if (!refMatch.IsReliable)
        {
            if (refMatch.CanRetryAtHigherVolume)
                EnsureContinuousForRetry(microphone, 200,
                    microphone.Samples.Length * 1000.0 / Math.Max(1, microphone.SampleRate) - AcousticDelayEstimator.ProbeDurationSeconds * 1000,
                    "麦克风录音");
            ThrowUnreliable(refMatch, reference, 200, 1500, "电脑内部参考声音",
                "电脑内部未取得清楚的参考测试音（尚不能判断音箱是否出声）：" + refMatch.Reason + "。");
        }
        if (!reference.HasContinuousProbe(refMatch.OffsetMs))
            throw new InvalidOperationException("电脑内部参考测试音已识别，但时间记录不够连续，无法可靠测量。原延迟已保留。\n" +
                reference.DescribeProbeCoverage(refMatch.OffsetMs));
        var referenceTime = reference.AbsoluteMilliseconds(refMatch.OffsetMs);
        var relativeReference = referenceTime - microphone.StartQpc / 10000.0;
        var minimumMicMs = relativeReference + separationMs - 100;
        var maximumMicMs = relativeReference + separationMs + 3900;
        EnsureRawSignalSafe(microphone, minimumMicMs, maximumMicMs, "麦克风录音");
        var micMatch = AcousticDelayEstimator.Find(microphone.Samples, microphone.SampleRate, minimumMicMs, maximumMicMs);
        if (!micMatch.IsReliable)
            ThrowUnreliable(micMatch, microphone, minimumMicMs, maximumMicMs, "麦克风录音",
                "电脑内部的参考测试音正常，但麦克风未清楚识别到待测音箱：" + micMatch.Reason + "。");
        if (!microphone.HasContinuousProbe(micMatch.OffsetMs))
            throw new InvalidOperationException("麦克风已识别到测试音，但该段录音的时间记录不够连续，无法可靠校准。尚不能据此判断麦克风断开，原延迟已保留。\n" +
                microphone.DescribeProbeCoverage(micMatch.OffsetMs));
        var arrival = microphone.AbsoluteMilliseconds(micMatch.OffsetMs) - referenceTime - separationMs;
        if (arrival < -100 || arrival > 3900)
            throw new InvalidOperationException("测试音到达时间超出可验证范围，原延迟已保留");
        return (arrival, Math.Min(refMatch.Confidence, micMatch.Confidence));
    }

    private static void ThrowUnreliable(ProbeMatch match, CapturedAudio audio, double minimumMs, double maximumMs,
        string captureName, string message)
    {
        if (!match.CanRetryAtHigherVolume) throw new InvalidOperationException(message + "原延迟已保留，请人工检查后重测。");
        EnsureContinuousForRetry(audio, minimumMs, maximumMs, captureName);
        throw new AcousticWeakSignalException(message);
    }

    private static void EnsureContinuousForRetry(CapturedAudio audio, double minimumMs, double maximumMs, string captureName)
    {
        EnsureRawSignalSafe(audio, minimumMs, maximumMs, captureName);
        // Missing/invalid capture packets are filled with zeros. Those zeros must never be
        // mistaken for a genuinely quiet room and cause a volume increase.
        var first = Math.Max(0, (int)Math.Round(minimumMs * audio.SampleRate / 1000));
        var end = Math.Min(audio.Samples.Length, (int)Math.Ceiling((maximumMs / 1000 + AcousticDelayEstimator.ProbeDurationSeconds) * audio.SampleRate));
        if (audio.SampleRate < 8000 || audio.Coverage.Length != audio.Samples.Length || first >= end ||
            audio.Samples.Any(sample => !float.IsFinite(sample)) || audio.HasUnsafeTiming(first, end - first))
            throw new InvalidOperationException(captureName + "的时间记录不完整，不能通过提高音量重试。原延迟已保留。");
        if (AcousticDelayEstimator.HasClipping(audio.Samples, first, end, audio.SampleRate))
            throw new InvalidOperationException(captureName + "过响失真，不能通过提高音量重试。请调整音量或麦克风位置后重测。");
        var valid = 0;
        for (var i = first; i < end; i++) if (audio.Coverage[i] == 1) valid++;
        if (valid < (end - first) * 0.995)
            throw new InvalidOperationException(captureName + "的时间记录不够连续，不能通过提高音量重试。尚不能据此判断设备断开，原延迟已保留。\n" +
                audio.DescribeCoverage(first, end - first));
    }

    private static void EnsureRawSignalSafe(CapturedAudio audio, double minimumMs, double maximumMs, string name)
    {
        var first = (int)Math.Clamp(Math.Floor(minimumMs * audio.SampleRate / 1000), 0, audio.Samples.Length);
        var end = (int)Math.Clamp(Math.Ceiling((maximumMs / 1000 + AcousticDelayEstimator.ProbeDurationSeconds) * audio.SampleRate), 0, audio.Samples.Length);
        if (audio.RawEvidence?.HasClipping(first, end, audio.SampleRate) == true)
            throw new InvalidOperationException(name + "的原始录音含过响失真或无效样点，不能通过提高音量重试。原延迟已保留。");
    }
}
