using NAudio.CoreAudioApi;
using NAudio.Wave;
using PairUp.App.Audio;

namespace PairUp.App.Services;

public sealed record CalibrationTarget(string DeviceId, string Name);
public sealed record AcousticCalibrationReport(IReadOnlyList<AcousticMeasurement> Measurements, AcousticCalibrationPlan Plan);

/// <summary>Measure actual source-to-speaker routing against a timestamped loopback reference.</summary>
public sealed class AcousticCalibrator(AudioEngine engine)
{
    public async Task<AcousticCalibrationReport> MeasureAsync(string microphoneId, IReadOnlyList<CalibrationTarget> targets,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var sourceId = engine.SourceDeviceId ?? throw new InvalidOperationException("音源尚未启动，请先打开一个输出设备");
        if (targets.Count < 2 || targets.All(t => t.DeviceId != sourceId)) throw new InvalidOperationException("请选择音源和至少一台输出音箱");
        using var routing = engine.BeginAcousticCalibration();
        using var mutedMusic = MusicMuteScope.Create(sourceId);
        using var microphone = new TimestampedAudioCapture(microphoneId, false);
        using var reference = new TimestampedAudioCapture(sourceId, true);
        await microphone.StartAsync(cancellationToken);
        await reference.StartAsync(cancellationToken);
        await Task.Delay(400, cancellationToken);
        var measurements = new List<AcousticMeasurement>();
        var separationBaseMs = 2000.0;
        foreach (var target in targets.OrderBy(t => t.DeviceId == sourceId ? 0 : 1))
        {
            var isSource = target.DeviceId == sourceId;
            var arrivals = new List<double>();
            var confidence = 1.0;
            for (var repeat = 0; repeat < 3; repeat++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"正在测量 {target.Name}，第 {repeat + 1}/3 次。" +
                    (isSource ? "请保持安静，稍后会听到短测试音。" : "先响音源，再响这台音箱；两声之间的间隔用于准确识别。"));
                // Keep the source audible: some drivers stop loopback and all forwarding when muted.
                // Vary the known separation so a fixed late source echo cannot pass all three trials.
                var separation = engine.SelectCalibrationOutput(isSource ? null : target.DeviceId,
                    isSource ? 0 : separationBaseMs + repeat * 100);
                await Task.Delay(250, cancellationToken);
                var result = await MeasureProbeAsync(sourceId, microphone, reference, separation, cancellationToken);
                arrivals.Add(result.ArrivalMs);
                confidence = Math.Min(confidence, result.Confidence);
            }
            arrivals.Sort();
            var spread = arrivals[^1] - arrivals[0];
            if (spread > 20) throw new InvalidOperationException($"“{target.Name}”三次测量相差 {spread:0} 毫秒，连接或环境不稳定，原延迟已保留。请重测。");
            measurements.Add(new(target.DeviceId, target.Name, arrivals[1], confidence, spread));
            if (isSource)
            {
                separationBaseMs = Math.Max(2000, Math.Ceiling(arrivals[1] + 800));
                if (separationBaseMs > 2800)
                    throw new InvalidOperationException("音源自身延迟超过当前可可靠区分的测量范围，原延迟已保留。请先检查蓝牙连接后重测。");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var plan = AcousticAlignment.Plan(sourceId, measurements);
        return new(measurements, plan);
    }

    private static async Task<(double ArrivalMs, double Confidence)> MeasureProbeAsync(string sourceId,
        TimestampedAudioCapture microphone, TimestampedAudioCapture reference, double separationMs, CancellationToken cancellationToken)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var source = enumerator.GetDevice(sourceId);
        var format = source.AudioClient.MixFormat;
        using var output = new WasapiOut(source, AudioClientShareMode.Shared, true, 10);
        var signal = new ProbeSampleProvider(format.SampleRate, format.Channels);
        output.Init(signal);
        var durationMs = (int)Math.Ceiling(4900 + separationMs);
        var begin = TimestampedAudioCapture.NowQpc;
        output.Play();
        try { await Task.Delay(durationMs, cancellationToken); }
        finally { output.Stop(); }
        await Task.Delay(100, cancellationToken);
        var end = begin + durationMs * 10000L;
        var refAudio = reference.Snapshot(begin, end);
        var micAudio = microphone.Snapshot(begin, end);
        return await Task.Run(() => AcousticProbeAnalysis.Analyze(refAudio, micAudio, separationMs), cancellationToken);
    }

    private sealed class ProbeSampleProvider : ISampleProvider
    {
        private readonly float[] _probe;
        private readonly int _leadFrames;
        private readonly int _totalFrames;
        private int _position;
        public WaveFormat WaveFormat { get; }
        public ProbeSampleProvider(int sampleRate, int channels)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _probe = AcousticDelayEstimator.CreateProbe(sampleRate);
            _leadFrames = (int)(sampleRate * 0.65);
            _totalFrames = sampleRate * 10;
        }
        public int Read(float[] buffer, int offset, int count)
        {
            var frames = Math.Min(count / WaveFormat.Channels, _totalFrames - _position);
            for (var i = 0; i < frames; i++)
            {
                var index = _position++ - _leadFrames;
                var value = index >= 0 && index < _probe.Length ? _probe[index] : 0;
                for (var channel = 0; channel < WaveFormat.Channels; channel++) buffer[offset + i * WaveFormat.Channels + channel] = value;
            }
            return frames * WaveFormat.Channels;
        }
    }

    private sealed class MusicMuteScope : IDisposable
    {
        private readonly List<(AudioSessionControl Session, bool Muted)> _sessions = new();
        public static MusicMuteScope Create(string sourceId)
        {
            var result = new MusicMuteScope();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var source = enumerator.GetDevice(sourceId);
                var sessions = source.AudioSessionManager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (session.GetProcessID == Environment.ProcessId) { session.Dispose(); continue; }
                    var oldMute = session.SimpleAudioVolume.Mute;
                    result._sessions.Add((session, oldMute));
                    session.SimpleAudioVolume.Mute = true;
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        public void Dispose()
        {
            foreach (var (session, muted) in _sessions)
            {
                try { session.SimpleAudioVolume.Mute = muted; }
                catch { /* Expired sessions belong to applications which stopped during calibration. */ }
                finally { session.Dispose(); }
            }
            _sessions.Clear();
        }
    }
}
