using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PairUp.App.Audio;

public sealed record CapturedAudio(float[] Samples, byte[] Coverage, int SampleRate, long StartQpc)
{
    // Numeric timing metadata only. It neither changes coverage nor stores audio outside memory.
    internal IReadOnlyList<CapturePacketTiming>? PacketTimings { get; init; }
    internal int UnlocatedPackets { get; init; }
    internal CaptureBufferInfo? BufferInfo { get; init; }

    public bool HasContinuousProbe(double offsetMs)
    {
        var first = Math.Max(0, (int)Math.Round(offsetMs * SampleRate / 1000));
        var count = (int)Math.Round(AcousticDelayEstimator.ProbeDurationSeconds * SampleRate);
        if (first + count > Coverage.Length) return false;
        var valid = 0;
        for (var i = first; i < first + count; i++) if (Coverage[i] == 1) valid++;
        return valid >= count * 0.995;
    }
    public double AbsoluteMilliseconds(double offsetMs) => StartQpc / 10000.0 + offsetMs;

    internal string DescribeProbeCoverage(double offsetMs) => DescribeCoverage(
        Math.Max(0, (int)Math.Round(offsetMs * SampleRate / 1000)),
        (int)Math.Round(AcousticDelayEstimator.ProbeDurationSeconds * SampleRate));

    internal string DescribeCoverage(int first, int count)
    {
        if (SampleRate <= 0 || first < 0 || count <= 0) return "时间记录统计不可用。";
        var end = Math.Min((long)Coverage.Length, (long)first + count);
        var valid = 0;
        var missing = 0;
        var invalid = 0;
        var missingRun = 0;
        var longestMissingRun = 0;
        for (var i = first; i < end; i++)
        {
            if (Coverage[i] == 1) valid++;
            else if (Coverage[i] == 0) missing++;
            else invalid++;
            missingRun = Coverage[i] == 0 ? missingRun + 1 : 0;
            longestMissingRun = Math.Max(longestMissingRun, missingRun);
        }
        var outside = count - valid - missing - invalid;
        var result = FormattableString.Invariant(
            $"时间统计：{count * 1000.0 / SampleRate:F2} 毫秒内有效 {valid * 100.0 / count:F3}%（{valid}/{count} 点）；未覆盖 {missing * 1000.0 / SampleRate:F3} 毫秒（{missing} 点），无效标记 {invalid * 1000.0 / SampleRate:F3} 毫秒（{invalid} 点），最长未覆盖 {longestMissingRun * 1000.0 / SampleRate:F3} 毫秒，超出录音范围 {outside} 点。标准仍为至少 99.5% 有效且完整落在录音范围内。");
        if (PacketTimings is null) return result + " 无录音包统计。";

        var timestampErrors = 0;
        var discontinuities = 0;
        var comparedBoundaries = 0;
        var deviceForwardJumps = 0;
        var deviceBackwardJumps = 0;
        var continuousDeviceWithTimelineGap = 0;
        var maximumReceiveIntervalMs = 0.0;
        long mappingGapFrames = 0;
        long mappingOverlapFrames = 0;
        foreach (var packet in PacketTimings)
        {
            var packetEnd = (long)packet.FirstSample + packet.FrameCount;
            if (packet.FirstSample < end && packetEnd > first)
            {
                if (packet.TimestampError) timestampErrors++;
                if (packet.DataDiscontinuity) discontinuities++;
                maximumReceiveIntervalMs = Math.Max(maximumReceiveIntervalMs, packet.ReceiveIntervalMs ?? 0);
            }
            if (packet.TimelineFrameGap is not { } timelineGap || packet.DeviceFrameGap is not { } deviceGap) continue;
            // Count only the part of a boundary gap/overlap inside this diagnostic interval.
            var previousEnd = (long)packet.FirstSample - timelineGap;
            var affectedStart = Math.Min(previousEnd, packet.FirstSample);
            var affectedEnd = Math.Max(previousEnd, packet.FirstSample);
            var affected = Math.Max(0, Math.Min(end, affectedEnd) - Math.Max(first, affectedStart));
            if (timelineGap > 0) mappingGapFrames += affected;
            else if (timelineGap < 0) mappingOverlapFrames += affected;
            if ((packet.FirstSample >= first && packet.FirstSample < end) || affected > 0)
            {
                comparedBoundaries++;
                if (deviceGap > 0) deviceForwardJumps++;
                else if (deviceGap < 0) deviceBackwardJumps++;
                if (deviceGap == 0 && timelineGap > 0 && affected > 0) continuousDeviceWithTimelineGap++;
            }
        }
        return result + FormattableString.Invariant(
            $" 包统计：时间戳异常 {timestampErrors} 包、不连续标记 {discontinuities} 包；可比较的包边界 {comparedBoundaries} 个，设备帧序号前跳 {deviceForwardJumps} 次、回退 {deviceBackwardJumps} 次；时间映射空隙 {mappingGapFrames * 1000.0 / SampleRate:F3} 毫秒、重叠 {mappingOverlapFrames * 1000.0 / SampleRate:F3} 毫秒，其中帧序号连续但映射有空隙 {continuousDeviceWithTimelineGap} 处；相邻包接收间隔最大 {maximumReceiveIntervalMs:F3} 毫秒。整次录音窗口另有 {UnlocatedPackets} 包时间戳非正、无法定位（按接收时间计入）。这些统计不等同于设备断开。") +
            (BufferInfo?.Describe(SampleRate) ?? " 无缓冲区统计。");
    }
}

internal sealed record CapturePacketTiming(int FirstSample, int FrameCount, bool TimestampError,
    bool DataDiscontinuity, long? DeviceFrameGap, long? TimelineFrameGap, double? ReceiveIntervalMs);

internal sealed record CaptureBufferInfo(long RequestedDuration, int PollIntervalMs, int? BufferFrames,
    long? DefaultPeriod, long? MinimumPeriod)
{
    internal string Describe(int sampleRate)
    {
        var size = BufferFrames is { } frames
            ? FormattableString.Invariant($"{frames} 帧（{frames * 1000.0 / sampleRate:F3} 毫秒）") : "未取得";
        var defaultPeriod = DefaultPeriod is { } period
            ? FormattableString.Invariant($"{period / 10000.0:F3} 毫秒") : "未取得";
        var minimumPeriod = MinimumPeriod is { } minimum
            ? FormattableString.Invariant($"{minimum / 10000.0:F3} 毫秒") : "未取得";
        return FormattableString.Invariant(
            $" 采集设置：{sampleRate} Hz，请求缓冲 {RequestedDuration / 10000.0:F1} 毫秒，实际容量 {size}，轮询等待 {PollIntervalMs} 毫秒，设备默认周期 {defaultPeriod}，最小周期 {minimumPeriod}。");
    }
}

/// <summary>WASAPI packet timestamps put microphone and source on the same monotonic clock.</summary>
public sealed class TimestampedAudioCapture : IDisposable
{
    private const long RequestedBufferDuration = 200000;
    private const int PollIntervalMs = 3;
    private sealed record Packet(long Start, float[] Samples, bool Invalid, long DevicePosition,
        AudioClientBufferFlags Flags, long ReceivedAtQpc);
    private readonly string _deviceId;
    private readonly bool _loopback;
    private readonly object _gate = new();
    private readonly List<Packet> _packets = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _worker;
    private Exception? _failure;
    private CaptureBufferInfo? _bufferInfo;
    public int SampleRate { get; private set; }

    public TimestampedAudioCapture(string deviceId, bool loopback) { _deviceId = deviceId; _loopback = loopback; }
    public static long NowQpc => (long)(Stopwatch.GetTimestamp() * (10000000.0 / Stopwatch.Frequency));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_worker is not null) throw new InvalidOperationException("录音已启动");
        _worker = Task.Run(CaptureLoop);
        await _ready.Task.WaitAsync(cancellationToken);
    }

    private void CaptureLoop()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(_deviceId);
            if (!_loopback && device.AudioEndpointVolume.Mute) throw new InvalidOperationException("所选麦克风处于静音，请取消麦克风静音后重试");
            if (!_loopback && device.AudioEndpointVolume.MasterVolumeLevelScalar <= 0)
                throw new InvalidOperationException("所选麦克风输入音量为 0%，请在 Windows 中调高麦克风输入音量后重试");
            using var client = device.AudioClient;
            var format = client.MixFormat.AsStandardWaveFormat();
            SampleRate = format.SampleRate;
            if (format.Encoding != WaveFormatEncoding.IeeeFloat && format.Encoding != WaveFormatEncoding.Pcm)
                throw new InvalidOperationException("当前麦克风录音格式不受支持");
            var flags = _loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
            client.Initialize(AudioClientShareMode.Shared, flags, RequestedBufferDuration, 0, format, Guid.Empty);
            // Optional metadata reads must not turn a working capture into an initialization failure.
            _bufferInfo = new(RequestedBufferDuration, PollIntervalMs, ReadDiagnostic(() => client.BufferSize),
                ReadDiagnostic(() => client.DefaultDevicePeriod), ReadDiagnostic(() => client.MinimumDevicePeriod));
            var reader = client.AudioCaptureClient;
            client.Start();
            _ready.TrySetResult();
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    while (reader.GetNextPacketSize() > 0)
                    {
                        var pointer = reader.GetBuffer(out var frames, out var packetFlags, out var devicePosition, out var timestamp);
                        var receivedAtQpc = NowQpc;
                        try
                        {
                            if (frames == 0) continue;
                            var raw = new byte[frames * format.BlockAlign];
                            if (!packetFlags.HasFlag(AudioClientBufferFlags.Silent)) Marshal.Copy(pointer, raw, 0, raw.Length);
                            var samples = DecodeMono(raw, format);
                            var invalid = timestamp <= 0 || packetFlags.HasFlag(AudioClientBufferFlags.TimestampError)
                                || packetFlags.HasFlag(AudioClientBufferFlags.DataDiscontinuity);
                            // Keep the existing conservative whole-packet rejection. Microsoft defines
                            // DataDiscontinuity relative to the previous device position; it does not
                            // guarantee that the remainder has trustworthy absolute probe timing.
                            // https://learn.microsoft.com/windows/win32/api/audioclient/ne-audioclient-_audclnt_bufferflags
                            lock (_gate)
                            {
                                _packets.Add(new(timestamp, samples, invalid, devicePosition, packetFlags, receivedAtQpc));
                                var earliest = timestamp - 150000000;
                                _packets.RemoveAll(p => p.Start <= 0
                                    ? p.ReceivedAtQpc < receivedAtQpc - 150000000
                                    : p.Start + (long)(p.Samples.Length * 10000000.0 / SampleRate) < earliest);
                            }
                        }
                        finally { reader.ReleaseBuffer(frames); }
                    }
                    _stop.Token.WaitHandle.WaitOne(PollIntervalMs);
                }
            }
            finally { client.Stop(); }
        }
        catch (Exception ex)
        {
            lock (_gate) _failure = ex;
            _ready.TrySetException(new InvalidOperationException((_loopback ? "无法读取电脑内部的参考声音：" : "无法启动麦克风收音：") + ex.Message, ex));
        }
    }

    public CapturedAudio Snapshot(long startQpc, long endQpc)
    {
        Packet[] packets;
        lock (_gate)
        {
            if (_failure is not null)
                throw new InvalidOperationException((_loopback ? "电脑内部参考声音采集返回错误：" : "麦克风采集返回错误：") +
                    _failure.Message + $"（错误代码 0x{_failure.HResult:X8}）。原延迟已保留。", _failure);
            // Packets and their sample arrays are immutable after insertion. Build the snapshot
            // and its diagnostics outside the gate so this work cannot block packet collection.
            packets = _packets.ToArray();
        }
        var count = (int)Math.Ceiling((endQpc - startQpc) * SampleRate / 10000000.0);
        if (count <= 0 || count > SampleRate * 12) throw new ArgumentOutOfRangeException(nameof(endQpc));
        var samples = new float[count];
        var coverage = new byte[count];
        var timings = new List<CapturePacketTiming>(packets.Length);
        var unlocatedPackets = 0;
        Packet? previous = null;
        var previousDestination = 0;
        foreach (var packet in packets)
        {
            if (packet.Start <= 0)
            {
                if (packet.ReceivedAtQpc >= startQpc && packet.ReceivedAtQpc < endQpc) unlocatedPackets++;
                previous = null;
                // An absent timestamp cannot be placed on the timeline. In particular, do not
                // cast a huge negative offset to int and accidentally copy it to sample zero.
                continue;
            }
            var destination = (int)Math.Round((packet.Start - startQpc) * SampleRate / 10000000.0);
            var timestampError = packet.Flags.HasFlag(AudioClientBufferFlags.TimestampError);
            var timingComparable = packet.Start > 0 && !timestampError && previous is not null && previous.Start > 0 &&
                !previous.Flags.HasFlag(AudioClientBufferFlags.TimestampError);
            timings.Add(new(destination, packet.Samples.Length, timestampError,
                packet.Flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity),
                timingComparable ? packet.DevicePosition - previous!.DevicePosition - previous.Samples.Length : null,
                timingComparable ? (long)destination - previousDestination - previous!.Samples.Length : null,
                previous is not null ? (packet.ReceivedAtQpc - previous.ReceivedAtQpc) / 10000.0 : null));
            previous = packet;
            previousDestination = destination;

            var source = Math.Max(0, -destination);
            var start = Math.Max(0, destination);
            var copy = Math.Min(packet.Samples.Length - source, count - start);
            if (copy > 0)
            {
                Array.Copy(packet.Samples, source, samples, start, copy);
                Array.Fill(coverage, packet.Invalid ? (byte)2 : (byte)1, start, copy);
            }
        }
        return new(samples, coverage, SampleRate, startQpc)
            { PacketTimings = timings, UnlocatedPackets = unlocatedPackets, BufferInfo = _bufferInfo };
    }

    private static T? ReadDiagnostic<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch { return null; }
    }

    public static float[] DecodeMono(byte[] raw, WaveFormat format)
    {
        var result = new float[raw.Length / format.BlockAlign];
        var bytes = format.BitsPerSample / 8;
        for (var frame = 0; frame < result.Length; frame++)
        {
            // Use the first microphone channel; summing opposite-polarity channels can erase it.
            var position = frame * format.BlockAlign;
            result[frame] = format.Encoding == WaveFormatEncoding.IeeeFloat && bytes == 4
                ? BitConverter.ToSingle(raw, position)
                : bytes switch
                {
                    2 => BitConverter.ToInt16(raw, position) / 32768f,
                    3 => ((raw[position] | raw[position + 1] << 8 | raw[position + 2] << 16) << 8 >> 8) / 8388608f,
                    4 => BitConverter.ToInt32(raw, position) / 2147483648f,
                    _ => throw new InvalidOperationException("麦克风采样位数不受支持")
                };
        }
        return result;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _worker?.GetAwaiter().GetResult();
        lock (_gate) _packets.Clear();
        _stop.Dispose();
    }
}
