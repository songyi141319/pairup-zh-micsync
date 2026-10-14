namespace PairUp.App.Audio;

public static class AcousticProbeAnalysis
{
    public static (double ArrivalMs, double Confidence) Analyze(CapturedAudio reference, CapturedAudio microphone,
        double separationMs = 0)
    {
        if (!double.IsFinite(separationMs) || separationMs < 0 || separationMs > FixedDelaySampleProvider.MaximumDelayMs)
            throw new ArgumentOutOfRangeException(nameof(separationMs));
        var refMatch = AcousticDelayEstimator.Find(reference.Samples, reference.SampleRate, 200, 1500);
        if (!refMatch.IsReliable)
            throw new InvalidOperationException("电脑内部未取得清楚的参考测试音（尚不能判断音箱是否出声）：" + refMatch.Reason + "。请检查 Windows 音量混合器中 PairUp 是否过小，以及音源连接是否正常。原延迟已保留。");
        if (!reference.HasContinuousProbe(refMatch.OffsetMs))
            throw new InvalidOperationException("参考声音的时间记录不连续，无法可靠测量。原延迟已保留，请重测。");
        var referenceTime = reference.AbsoluteMilliseconds(refMatch.OffsetMs);
        var relativeReference = referenceTime - microphone.StartQpc / 10000.0;
        var micMatch = AcousticDelayEstimator.Find(microphone.Samples, microphone.SampleRate,
            relativeReference + separationMs - 100, relativeReference + separationMs + 3900);
        if (!micMatch.IsReliable) throw new InvalidOperationException("电脑内部的参考测试音正常，但麦克风未清楚识别到待测音箱：" + micMatch.Reason + "。请检查音箱是否出声、设备音量和麦克风位置。原延迟已保留。");
        if (!microphone.HasContinuousProbe(micMatch.OffsetMs))
            throw new InvalidOperationException("测试期间麦克风录音中断，原延迟已保留，请重测");
        var arrival = microphone.AbsoluteMilliseconds(micMatch.OffsetMs) - referenceTime - separationMs;
        if (arrival < -100 || arrival > 3900)
            throw new InvalidOperationException("测试音到达时间超出可验证范围，原延迟已保留");
        return (arrival, Math.Min(refMatch.Confidence, micMatch.Confidence));
    }
}
