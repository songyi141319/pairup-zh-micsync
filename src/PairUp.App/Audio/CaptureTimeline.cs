namespace PairUp.App.Audio;

internal sealed record CapturePacket(long Start, float[] Samples, bool Invalid, long DevicePosition,
    NAudio.CoreAudioApi.AudioClientBufferFlags Flags, long ReceivedAtQpc);

internal sealed record RawSampleSpan(float[] Samples, double FirstSample, double Step, int Run);

internal sealed record RawCaptureEvidence(IReadOnlyList<RawSampleSpan> Spans)
{
    public bool HasClipping(int first, int end, int rate)
    {
        var window = Math.Max(1, (int)Math.Round(AcousticDelayEstimator.ProbeDurationSeconds * rate));
        var clipped = new Queue<long>();
        var run = -1;
        long position = 0;
        foreach (var span in Spans)
        {
            if (span.Run != run) { clipped.Clear(); position = 0; run = span.Run; }
            for (var i = 0; i < span.Samples.Length; i++, position++)
            {
                var mapped = span.FirstSample + span.Step * i;
                if (mapped < first || mapped >= end) { clipped.Clear(); continue; }
                // Preserve the original 220ms / 2% raw-sample clipping criterion.
                while (clipped.TryPeek(out var old) && position - old >= window) clipped.Dequeue();
                if (!float.IsFinite(span.Samples[i])) return true;
                if (Math.Abs(span.Samples[i]) >= 0.995) clipped.Enqueue(position);
                if (clipped.Count > window * 0.02) return true;
            }
        }
        return false;
    }
}

internal sealed record TimelineRepairInfo(int RebuiltSegments, int RebuiltPackets, int RejectedSegments,
    double MaximumResidualMs, double MaximumClockPpm, int Collisions, string Rejections = "")
{
    public string Describe() => FormattableString.Invariant(
        $" 时间排列：按连续设备帧重建 {RebuiltSegments} 段/{RebuiltPackets} 包；未满足重建条件 {RejectedSegments} 段；已重建段最大时间残差 {MaximumResidualMs:F3} 毫秒、时钟偏差 {MaximumClockPpm:F1} ppm；冲突样点 {Collisions} 个。覆盖率标准未降低。{Rejections}");
}

/// <summary>
/// Device positions establish sample order; independent QPC anchors retain absolute timing.
/// Fit only continuous, trustworthy runs, never fill gaps or interpolate across a broken run.
/// </summary>
internal static class CaptureTimeline
{
    private const int MinimumPackets = 8;
    private const double MinimumSpanMs = 200;
    private const double MaximumResidualMs = 0.75;
    private const double MaximumClockPpm = 2000;

    public static TimelineRepairInfo Render(IReadOnlyList<CapturePacket> packets, int rate, long startQpc,
        float[] samples, byte[] coverage, out RawCaptureEvidence rawEvidence)
    {
        var rawSpans = new List<RawSampleSpan>();
        var rebuiltSegments = 0;
        var rebuiltPackets = 0;
        var rejected = 0;
        var collisions = 0;
        var maxResidual = 0.0;
        var maxPpm = 0.0;
        var rejectedFits = new List<string>();
        var segments = new List<(int First, int End, int Run)>();
        var index = 0;
        while (index < packets.Count)
        {
            var first = index++;
            if (Trustworthy(packets[first]))
                while (index < packets.Count && Consecutive(packets[index - 1], packets[index])) index++;

            Split(first, index, first, 0);
        }
        double? previousEnd = null;
        foreach (var segment in segments)
        {
            var first = segment.First;
            index = segment.End;
            double? segmentStart = null;
            double? segmentEnd = null;
            if (TryFit(packets, first, index, rate, startQpc, out var origin, out var scale,
                out var residual, out var ppm, out _, out _))
            {
                rebuiltSegments++;
                rebuiltPackets += index - first;
                maxResidual = Math.Max(maxResidual, residual);
                maxPpm = Math.Max(maxPpm, Math.Abs(ppm));
                for (var p = first; p < index; p++)
                    rawSpans.Add(new(packets[p].Samples,
                        origin + scale * (packets[p].DevicePosition - packets[first].DevicePosition), scale, segment.Run));
                // Source coordinates remain contiguous across packet boundaries. The last
                // real sample is the endpoint: never invent a right neighbour for extrapolation.
                var frameCount = packets[index - 1].DevicePosition - packets[first].DevicePosition + packets[index - 1].Samples.Length;
                segmentStart = origin;
                segmentEnd = origin + frameCount * scale;
                var begin = (int)Math.Clamp(Math.Ceiling(origin), 0, samples.Length);
                var end = (int)Math.Clamp(Math.Floor(origin + (frameCount - 1) * scale) + 1, 0, samples.Length);
                var packetIndex = first;
                for (var destination = begin; destination < end; destination++)
                {
                    var position = (destination - origin) / scale;
                    var left = (long)Math.Floor(position);
                    if (left < 0 || left >= frameCount) continue;
                    while (packetIndex + 1 < index &&
                        packets[packetIndex + 1].DevicePosition - packets[first].DevicePosition <= left) packetIndex++;
                    var offset = (int)(left - (packets[packetIndex].DevicePosition - packets[first].DevicePosition));
                    var a = packets[packetIndex].Samples[offset];
                    var fraction = position - left;
                    float b;
                    if (fraction < 1e-9) b = a;
                    else if (offset + 1 < packets[packetIndex].Samples.Length) b = packets[packetIndex].Samples[offset + 1];
                    else if (packetIndex + 1 < index) b = packets[packetIndex + 1].Samples[0];
                    else continue;
                    Put(destination, (float)(a + (b - a) * fraction), false);
                }
            }
            else
            {
                rejected++;
                for (var p = first; p < index; p++)
                {
                    var packet = packets[p];
                    if (packet.Start <= 0) continue;
                    var position = Math.Round(((double)packet.Start - startQpc) * rate / 10000000.0);
                    segmentStart ??= position;
                    segmentEnd = position + packet.Samples.Length;
                    rawSpans.Add(new(packet.Samples, position, 1, segment.Run));
                    if (!double.IsFinite(position) || position >= samples.Length || position + packet.Samples.Length <= 0) continue;
                    var destination = (int)position;
                    var source = Math.Max(0, -destination);
                    for (; source < packet.Samples.Length && destination + source < samples.Length; source++)
                        Put(destination + source, packet.Samples[source], packet.Invalid || packet.DevicePosition < 0);
                }
            }

            // Fits on opposite sides of a rejected region are independent absolute anchors.
            // Do not smooth, shift or interpolate the seam. A probe crossing it must fail.
            if (first > 0 && segmentStart is { } right && double.IsFinite(right))
            {
                var left = previousEnd is { } value && double.IsFinite(value) ? value : right;
                var begin = (int)Math.Clamp(Math.Floor(Math.Min(left, right)), 0, coverage.Length);
                var end = (int)Math.Clamp(Math.Ceiling(Math.Max(left, right)) + 1, 0, coverage.Length);
                for (var i = begin; i < end; i++) coverage[i] = 2;
            }
            previousEnd = segmentEnd;
        }
        rawEvidence = new(rawSpans);
        return new(rebuiltSegments, rebuiltPackets, rejected, maxResidual, maxPpm, collisions,
            rejectedFits.Count == 0 ? "" : " 拒绝拟合记录（最多8条）：" + string.Join("；", rejectedFits));

        void Split(int first, int end, int run, int depth)
        {
            if (TryFit(packets, first, end, rate, startQpc, out _, out _, out var residual, out var ppm, out var reason, out var worst))
            {
                segments.Add((first, end, run));
                return;
            }
            if (rejectedFits.Count < 8)
                rejectedFits.Add(FormattableString.Invariant($"包 {first}–{end - 1}：{reason}，残差 {residual:F3} 毫秒、偏差 {ppm:F1} ppm"));
            // Prefer isolating an identifiable bad anchor over cutting otherwise good audio
            // at an arbitrary midpoint. The bad packet is retained, never interpolated over.
            // Both neighbours must independently fit the unchanged limits (or be too short
            // to fit and therefore remain raw); at least one side must be a trustworthy fit.
            if (depth < 12 && end - first >= MinimumPackets * 2 && residual > MaximumResidualMs)
            {
                var leftFits = Fits(first, worst);
                var rightFits = Fits(worst + 1, end);
                if ((leftFits || TooShort(first, worst)) && (rightFits || TooShort(worst + 1, end)) && (leftFits || rightFits))
                {
                    if (first < worst) Split(first, worst, run, depth + 1);
                    segments.Add((worst, worst + 1, run));
                    if (worst + 1 < end) Split(worst + 1, end, run, depth + 1);
                    return;
                }
            }
            if (depth < 12 && end - first >= MinimumPackets * 2 && Trustworthy(packets[first]) &&
                packets[end - 1].DevicePosition - packets[first].DevicePosition >= rate * MinimumSpanMs * 2 / 1000)
            {
                var middle = first + (end - first) / 2;
                Split(first, middle, run, depth + 1);
                Split(middle, end, run, depth + 1);
            }
            else segments.Add((first, end, run));
        }

        bool TooShort(int first, int end) => end - first < MinimumPackets ||
            packets[end - 1].DevicePosition - packets[first].DevicePosition < rate * MinimumSpanMs / 1000;
        bool Fits(int first, int end) => end > first && TryFit(packets, first, end, rate, startQpc,
            out _, out _, out _, out _, out _, out _);

        void Put(int destination, float value, bool invalid)
        {
            // Invalid and colliding data stays invalid; later good packets cannot wash it out.
            if (coverage[destination] != 0) { coverage[destination] = 2; collisions++; return; }
            samples[destination] = value;
            coverage[destination] = invalid || !float.IsFinite(value) ? (byte)2 : (byte)1;
        }
    }

    private static bool Trustworthy(CapturePacket p) => !p.Invalid && p.Start > 0 && p.DevicePosition >= 0 && p.Samples.Length > 0;

    private static bool Consecutive(CapturePacket a, CapturePacket b) => Trustworthy(a) && Trustworthy(b)
        && b.Start > a.Start && b.DevicePosition >= a.DevicePosition
        && b.DevicePosition - a.DevicePosition == a.Samples.Length;

    private static bool TryFit(IReadOnlyList<CapturePacket> packets, int first, int end, int rate, long startQpc,
        out double origin, out double scale, out double residualMs, out double clockPpm, out string reason, out int worst)
    {
        origin = 0; scale = 1; residualMs = 0; clockPpm = 0;
        worst = first;
        reason = "包数或有效时间锚不足";
        if (end - first < MinimumPackets || !Trustworthy(packets[first])) return false;
        var anchor = packets[first];
        var span = packets[end - 1].DevicePosition - anchor.DevicePosition;
        reason = "连续采样跨度不在200毫秒至20秒范围内";
        if (span < rate * MinimumSpanMs / 1000 || span > rate * 20L) return false;
        // Regress differences, not enormous absolute counter values.
        var meanX = 0.0;
        var meanY = 0.0;
        for (var i = first; i < end; i++)
        {
            meanX += packets[i].DevicePosition - anchor.DevicePosition;
            meanY += (packets[i].Start - anchor.Start) * (rate / 10000000.0);
        }
        meanX /= end - first; meanY /= end - first;
        var xx = 0.0; var xy = 0.0;
        for (var i = first; i < end; i++)
        {
            var x = packets[i].DevicePosition - anchor.DevicePosition - meanX;
            var y = (packets[i].Start - anchor.Start) * (rate / 10000000.0) - meanY;
            xx += x * x; xy += x * y;
        }
        scale = xy / xx;
        clockPpm = (scale - 1) * 1000000;
        reason = "设备时钟斜率无效";
        if (!double.IsFinite(scale) || scale <= 0) return false;
        var intercept = meanY - scale * meanX;
        for (var i = first; i < end; i++)
        {
            var actual = (packets[i].Start - anchor.Start) * (rate / 10000000.0);
            var expected = intercept + scale * (packets[i].DevicePosition - anchor.DevicePosition);
            var errorMs = Math.Abs(actual - expected) * 1000 / rate;
            if (errorMs > residualMs) { residualMs = errorMs; worst = i; }
        }
        reason = "设备时钟偏差超过2000ppm";
        if (Math.Abs(clockPpm) > MaximumClockPpm) return false;
        reason = "时间锚残差超过0.75毫秒";
        if (residualMs > MaximumResidualMs) return false;
        reason = "绝对时间起点无效";
        origin = ((double)anchor.Start - startQpc) * rate / 10000000.0 + intercept;
        return double.IsFinite(origin);
    }
}
