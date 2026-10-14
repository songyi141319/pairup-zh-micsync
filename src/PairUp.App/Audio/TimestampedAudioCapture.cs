using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PairUp.App.Audio;

public sealed record CapturedAudio(float[] Samples, byte[] Coverage, int SampleRate, long StartQpc)
{
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
}

/// <summary>WASAPI packet timestamps put microphone and source on the same monotonic clock.</summary>
public sealed class TimestampedAudioCapture : IDisposable
{
    private sealed record Packet(long Start, float[] Samples, bool Invalid);
    private readonly string _deviceId;
    private readonly bool _loopback;
    private readonly object _gate = new();
    private readonly List<Packet> _packets = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _worker;
    private Exception? _failure;
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
            client.Initialize(AudioClientShareMode.Shared, flags, 200000, 0, format, Guid.Empty);
            var reader = client.AudioCaptureClient;
            client.Start();
            _ready.TrySetResult();
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    while (reader.GetNextPacketSize() > 0)
                    {
                        var pointer = reader.GetBuffer(out var frames, out var packetFlags, out _, out var timestamp);
                        try
                        {
                            if (frames == 0) continue;
                            var raw = new byte[frames * format.BlockAlign];
                            if (!packetFlags.HasFlag(AudioClientBufferFlags.Silent)) Marshal.Copy(pointer, raw, 0, raw.Length);
                            var samples = DecodeMono(raw, format);
                            var invalid = timestamp <= 0 || packetFlags.HasFlag(AudioClientBufferFlags.TimestampError)
                                || packetFlags.HasFlag(AudioClientBufferFlags.DataDiscontinuity);
                            lock (_gate)
                            {
                                _packets.Add(new(timestamp, samples, invalid));
                                var earliest = timestamp - 150000000;
                                _packets.RemoveAll(p => p.Start + (long)(p.Samples.Length * 10000000.0 / SampleRate) < earliest);
                            }
                        }
                        finally { reader.ReleaseBuffer(frames); }
                    }
                    _stop.Token.WaitHandle.WaitOne(3);
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
        lock (_gate)
        {
            if (_failure is not null) throw new InvalidOperationException((_loopback ? "电脑内部的参考声音读取中断：" : "麦克风收音中断：") + _failure.Message, _failure);
            var count = (int)Math.Ceiling((endQpc - startQpc) * SampleRate / 10000000.0);
            if (count <= 0 || count > SampleRate * 12) throw new ArgumentOutOfRangeException(nameof(endQpc));
            var samples = new float[count];
            var coverage = new byte[count];
            foreach (var packet in _packets)
            {
                var destination = (int)Math.Round((packet.Start - startQpc) * SampleRate / 10000000.0);
                var source = Math.Max(0, -destination);
                var start = Math.Max(0, destination);
                var copy = Math.Min(packet.Samples.Length - source, count - start);
                if (copy <= 0) continue;
                Array.Copy(packet.Samples, source, samples, start, copy);
                Array.Fill(coverage, packet.Invalid ? (byte)2 : (byte)1, start, copy);
            }
            return new(samples, coverage, SampleRate, startQpc);
        }
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
