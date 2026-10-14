using NAudio.CoreAudioApi;
using NAudio.Wave;
using PairUp.App.Audio;

namespace PairUp.App.Services;

public sealed record CalibrationTarget(string DeviceId, string Name);
public sealed record AcousticCalibrationReport(IReadOnlyList<AcousticMeasurement> Measurements, AcousticCalibrationPlan Plan);

/// <summary>Measure actual source-to-speaker routing against a timestamped loopback reference.</summary>
public sealed class AcousticCalibrator(AudioEngine engine)
{
    public string? RestorationWarning { get; private set; }

    public async Task<AcousticCalibrationReport> MeasureAsync(string microphoneId, IReadOnlyList<CalibrationTarget> targets,
        IProgress<string>? progress, CancellationToken cancellationToken, CalibrationOptions? options = null)
    {
        options ??= new CalibrationOptions();
        var levels = options.GetLevels(DateTime.Now);
        RestorationWarning = null;
        var sourceId = engine.SourceDeviceId ?? throw new InvalidOperationException("音源尚未启动，请先打开一个输出设备");
        if (targets.Count < 2 || targets.All(t => !SameDevice(t.DeviceId, sourceId)))
            throw new InvalidOperationException("请选择音源和至少一台输出音箱");
        if (targets.Any(t => string.IsNullOrWhiteSpace(t.DeviceId)) ||
            targets.Select(t => t.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
            throw new InvalidOperationException("校准设备列表不完整或有重复，请重新选择。");

        // Own each scope before initialization, so any partially completed setup is rolled
        // back too. Music stays muted until volume, session state and routing are restored.
        var mutedMusic = new MusicMuteScope();
        var volumes = new CalibrationVolumeScope();
        IDisposable? routing = null;
        TimestampedAudioCapture? microphone = null;
        TimestampedAudioCapture? reference = null;
        var restoreWarnings = new List<string>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var target in targets) mutedMusic.Mute(target.DeviceId);
            routing = engine.BeginAcousticCalibration();
            volumes.Capture(targets);
            microphone = new TimestampedAudioCapture(microphoneId, false);
            reference = new TimestampedAudioCapture(sourceId, true);
            await microphone.StartAsync(cancellationToken);
            await reference.StartAsync(cancellationToken);
            await Task.Delay(400, cancellationToken);

            for (var attempt = 0; attempt < levels.Length; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var percent = levels[attempt];
                volumes.ApplyLevel(percent, options.Mode == CalibrationVolumeMode.Manual);
                progress?.Report($"主音源与选中音箱临时统一为 {percent}%。结束后恢复各自原音量和静音状态。");
                await Task.Delay(250, cancellationToken);
                try
                {
                    return await MeasureTargetsAsync(sourceId, targets, microphone, reference, volumes, percent, progress, cancellationToken);
                }
                catch (AcousticWeakSignalException ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // A disconnection, changed mixer state or stopped capture must win over
                    // a quiet snapshot; none of those conditions permits louder retries.
                    CheckForwarding();
                    volumes.EnsureReady("检查是否允许弱信号重试");
                    if (attempt + 1 == levels.Length)
                        throw new InvalidOperationException((options.Mode == CalibrationVolumeMode.Manual
                            ? $"手动模式保持 {percent}%，未自动提高音量。"
                            : $"已尝试 {string.Join("、", levels.Select(level => level + "%"))}，已达到本次自动音量上限。") +
                            "仍未取得可靠测试音。请先单台试听，再检查麦克风位置。未应用新的延迟。\n" + ex.Message, ex);
                    progress?.Report($"{percent}% 时测试音仍偏弱，将以 {levels[attempt + 1]}% 重新测量整组音箱。");
                }
            }
            throw new InvalidOperationException("校准未产生可用测量结果。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // This is deliberately before finally: normal-volume restoration must not be
            // mistaken for the temporary level that was present while a probe was running.
            throw new InvalidOperationException(ex.Message + "\n" + volumes.DescribeDiagnostics(), ex);
        }
        finally
        {
            if (routing is not null)
                Restore(engine.SilenceCalibrationOutputs, "待测音箱未能完全停止转发，请检查播放状态。", restoreWarnings);
            Restore(() => reference?.Dispose(), "内部参考声音未能正常停止，请关闭校准窗口后检查。", restoreWarnings);
            Restore(() => microphone?.Dispose(), "麦克风收音未能正常停止，请关闭校准窗口后检查。", restoreWarnings);
            Restore(volumes.Dispose, "部分原音量未能恢复，请检查 Windows 设备音量。", restoreWarnings);
            restoreWarnings.AddRange(volumes.RestoreWarnings);
            if (restoreWarnings.Count > 0 && routing is not null)
            {
                mutedMusic.KeepMuted = true;
                foreach (var target in targets)
                    if (engine.SuppressCalibrationResume(target.DeviceId) is { } pauseWarning) restoreWarnings.Add(pauseWarning);
                restoreWarnings.Add("恢复未完全成功，已暂停音乐恢复；请检查设备音量后手动恢复播放。");
            }
            Restore(() => routing?.Dispose(), "原延迟或播放路由未能完全恢复，请检查播放状态。", restoreWarnings);
            if (engine.CalibrationRestoreWarning is { } routingWarning)
            {
                mutedMusic.KeepMuted = true;
                restoreWarnings.Add(routingWarning);
            }
            Restore(mutedMusic.Dispose, "部分音乐应用的原静音状态未能恢复，请检查 Windows 音量混合器。", restoreWarnings);
            restoreWarnings.AddRange(mutedMusic.RestoreWarnings);
            RestorationWarning = restoreWarnings.Count == 0 ? null : string.Join("\n", restoreWarnings.Distinct());
        }
    }

    private async Task<AcousticCalibrationReport> MeasureTargetsAsync(string sourceId, IReadOnlyList<CalibrationTarget> targets,
        TimestampedAudioCapture microphone, TimestampedAudioCapture reference, CalibrationVolumeScope volumes, int percent,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var measurements = new List<AcousticMeasurement>();
        var separationBaseMs = 2000.0;
        foreach (var target in targets.OrderBy(t => SameDevice(t.DeviceId, sourceId) ? 0 : 1))
        {
            var isSource = SameDevice(target.DeviceId, sourceId);
            var arrivals = new List<double>();
            var confidence = 1.0;
            for (var repeat = 0; repeat < 3; repeat++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"当前 {percent}%，正在测量 {target.Name}，第 {repeat + 1}/3 次。" +
                    (isSource ? "请保持安静，稍后会听到短测试音。" : "先响音源，再响这台音箱；两声之间的间隔用于准确识别。"));
                // Keep the source audible: some drivers stop loopback and all forwarding when muted.
                // Vary the known separation so a fixed late source echo cannot pass all three trials.
                try
                {
                    CheckForwarding();
                    volumes.EnsureReady("选择待测音箱前");
                    var separation = engine.SelectCalibrationOutput(isSource ? null : target.DeviceId,
                        isSource ? 0 : separationBaseMs + repeat * 100);
                    volumes.EnsureReady("选择待测音箱并确认主音源解除静音后");
                    await Task.Delay(250, cancellationToken);
                    volumes.EnsureReady("等待播放连接稳定后");
                    var result = await MeasureProbeAsync(sourceId, microphone, reference, volumes, separation, cancellationToken);
                    CheckForwarding();
                    volumes.EnsureReady("本次测量完成后");
                    arrivals.Add(result.ArrivalMs);
                    confidence = Math.Min(confidence, result.Confidence);
                }
                catch (OperationCanceledException) { throw; }
                catch (AcousticWeakSignalException ex)
                {
                    throw new AcousticWeakSignalException($"“{target.Name}”第 {repeat + 1}/3 次：{ex.Message}", ex);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"“{target.Name}”第 {repeat + 1}/3 次测量失败：{ex.Message}\n未应用新的延迟。", ex);
                }
            }
            arrivals.Sort();
            var spread = arrivals[^1] - arrivals[0];
            if (spread > 20) throw new InvalidOperationException($"“{target.Name}”三次测量相差 {spread:0} 毫秒，连接或环境不稳定，原延迟已保留。请重测。");
            measurements.Add(new(isSource ? sourceId : target.DeviceId, target.Name, arrivals[1], confidence, spread));
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
        TimestampedAudioCapture microphone, TimestampedAudioCapture reference, CalibrationVolumeScope volumes,
        double separationMs, CancellationToken cancellationToken)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var source = enumerator.GetDevice(sourceId);
        WaveFormat format;
        using (var client = source.AudioClient) format = client.MixFormat;
        using var output = new WasapiOut(source, AudioClientShareMode.Shared, true, 10);
        volumes.EnsureReady("创建测试播放连接后");
        var signal = new ProbeSampleProvider(format.SampleRate, format.Channels);
        try { output.Init(signal); }
        catch (Exception ex) { throw new InvalidOperationException("无法准备测试音播放：" + ex.Message, ex); }
        volumes.EnsureReady("测试播放初始化后");
        // Init may create a new Windows mixer session. Capture its original state before
        // normalizing this process's session; endpoint volume remains capped at the chosen level.
        volumes.PrepareOwnSessions("测试播放初始化并调整声屿应用音量后");
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) => stopped.TrySetResult(args.Exception);
        var durationMs = (int)Math.Ceiling(4900 + separationMs);
        var begin = TimestampedAudioCapture.NowQpc;
        try
        {
            try { output.Play(); }
            catch (Exception ex) { throw new InvalidOperationException("无法开始播放测试音：" + ex.Message, ex); }
            volumes.EnsureReady("启动测试播放后");
            var duration = Task.Delay(durationMs, cancellationToken);
            while (!duration.IsCompleted)
            {
                await Task.WhenAny(duration, stopped.Task, Task.Delay(250, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
                if (stopped.Task.IsCompleted)
                {
                    var error = await stopped.Task;
                    throw new InvalidOperationException("测试音播放提前停止：" + (error?.Message ?? "请检查音源连接"), error);
                }
                volumes.EnsureReady("测试音播放中", freshRead: false);
            }
            await duration;
            volumes.EnsureReady("测试音播放结束前");
        }
        finally { output.Stop(); }
        await Task.Delay(100, cancellationToken);
        volumes.EnsureReady("测试音停止后、信号分析前");
        var end = begin + durationMs * 10000L;
        var refAudio = reference.Snapshot(begin, end);
        var micAudio = microphone.Snapshot(begin, end);
        return await Task.Run(() => AcousticProbeAnalysis.Analyze(refAudio, micAudio, separationMs), cancellationToken);
    }

    private void CheckForwarding()
    {
        if (engine.CaptureError is { } captureError)
            throw new InvalidOperationException("音源转发中断：" + captureError);
    }

    private static bool SameDevice(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static void Restore(Action restore, string warning, List<string> warnings)
    {
        try { restore(); }
        catch { warnings.Add(warning); }
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
        private readonly List<string> _restoreWarnings = new();
        public IReadOnlyList<string> RestoreWarnings => _restoreWarnings;
        public bool KeepMuted { get; set; }

        public void Mute(string sourceId)
        {
            using var enumerator = new MMDeviceEnumerator();
            using var source = enumerator.GetDevice(sourceId);
            var sessions = source.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                AudioSessionControl? session = sessions[i];
                try
                {
                    if (session.GetProcessID == Environment.ProcessId) continue;
                    var oldMute = session.SimpleAudioVolume.Mute;
                    _sessions.Add((session, oldMute));
                    var retained = session;
                    session = null;
                    // Other applications' scalar volumes are never written.
                    retained.SimpleAudioVolume.Mute = true;
                }
                finally { session?.Dispose(); }
            }
        }
        public void Dispose()
        {
            foreach (var (session, muted) in _sessions)
            {
                try { session.SimpleAudioVolume.Mute = KeepMuted || muted; }
                catch
                {
                    var expired = false;
                    try { expired = session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired; }
                    catch { }
                    if (!expired) _restoreWarnings.Add("部分音乐应用的原静音状态未能恢复，请检查 Windows 音量混合器。");
                }
                finally
                {
                    try { session.Dispose(); }
                    catch { _restoreWarnings.Add("部分音乐应用的音量控制未能完全释放。"); }
                }
            }
            _sessions.Clear();
        }
    }
}
