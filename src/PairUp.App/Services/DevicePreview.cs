using NAudio.CoreAudioApi;
using NAudio.Wave;
using PairUp.App.Audio;

namespace PairUp.App.Services;

/// <summary>A finite, gently enveloped chime sent directly to one Windows endpoint.</summary>
internal sealed class DevicePreview
{
    public string? RestorationWarning { get; private set; }
    public bool ResumeBlocked { get; private set; }
    public async Task PlayAsync(string deviceId, int percent, CancellationToken cancellationToken)
    {
        if (percent is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        cancellationToken.ThrowIfCancellationRequested();
        var volumes = new CalibrationVolumeScope();
        var music = new List<(AudioSessionControl Session, bool Muted)>();
        var warnings = new List<string>();
        var deviceName = "所选音箱";
        var stage = "读取设备";
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(deviceId);
            deviceName = device.FriendlyName;
            if (device.State != DeviceState.Active)
                throw new InvalidOperationException("这台设备未连接，请连接后再试听。");
            volumes.Capture([new CalibrationTarget(deviceId, device.FriendlyName)]);
            // Prevent another application's music on this endpoint from becoming louder when
            // applying the user's explicitly chosen audition level. Do not alter its scalar.
            stage = "读取应用音量";
            var existing = device.AudioSessionManager.Sessions;
            for (var i = 0; i < existing.Count; i++)
            {
                var session = existing[i];
                if (session.GetProcessID == Environment.ProcessId) { session.Dispose(); continue; }
                try
                {
                    music.Add((session, session.SimpleAudioVolume.Mute));
                    session.SimpleAudioVolume.Mute = true;
                }
                catch { if (!music.Any(item => ReferenceEquals(item.Session, session))) session.Dispose(); throw; }
            }
            stage = "设置试听音量";
            volumes.ApplyLevel(percent, manual: true);
            stage = "读取播放格式";
            WaveFormat format;
            using (var client = device.AudioClient) format = client.MixFormat;
            stage = "准备试听播放";
            using var output = SharedAudioOutput.Open(device,
                new Chime(format.SampleRate, format.Channels).ToWaveProvider(), 30, out var compatibilityMode, cancellationToken);
            volumes.PrepareOwnSessions();
            volumes.EnsureReady(compatibilityMode ? "准备试听后（兼容播放，100毫秒缓冲）" : "准备试听后");
            var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, args) => stopped.TrySetResult(args.Exception);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                stage = "开始试听";
                output.Play();
                volumes.EnsureReady("开始试听后");
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                while (!stopped.Task.IsCompleted)
                {
                    await Task.WhenAny(stopped.Task, Task.Delay(250, cancellationToken));
                    cancellationToken.ThrowIfCancellationRequested();
                    volumes.EnsureReady("试听播放中", freshRead: false);
                    if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
                        throw new InvalidOperationException("设备未及时结束试听，请检查连接。");
                }
                if (await stopped.Task is { } error) throw new InvalidOperationException("试听播放失败：" + error.Message, error);
            }
            finally { output.Stop(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException(AudioFailure.Describe(ex, deviceName, stage) + "\n" + volumes.DescribeDiagnostics(), ex);
        }
        finally
        {
            volumes.Dispose();
            warnings.AddRange(volumes.RestoreWarnings);
            ResumeBlocked = volumes.RestoreWarnings.Count > 0;
            if (ResumeBlocked)
                warnings.Add("原音量未能完整恢复，已保留音乐静音；请检查设备音量后手动恢复播放。");
            foreach (var (session, muted) in music)
            {
                try { session.SimpleAudioVolume.Mute = ResumeBlocked || muted; }
                catch { warnings.Add("部分音乐应用的静音状态未能恢复，请检查 Windows 音量混合器。"); }
                finally
                {
                    try { session.Dispose(); }
                    catch { warnings.Add("部分试听音量控制未能完全释放。"); }
                }
            }
            RestorationWarning = warnings.Count == 0 ? null : string.Join("；", warnings.Distinct());
        }
    }

    private sealed class Chime(int sampleRate, int channels) : ISampleProvider
    {
        private int _frame;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        public int Read(float[] buffer, int offset, int count)
        {
            var frames = Math.Min(count / channels, (int)(sampleRate * 1.8) - _frame);
            for (var i = 0; i < frames; i++, _frame++)
            {
                var t = (double)_frame / sampleRate;
                var noteTime = t < 0.8 ? t - 0.15 : t - 0.8;
                var frequency = t < 0.8 ? 523.25 : 659.25;
                var envelope = noteTime is >= 0 and < 0.65
                    ? Math.Min(1, noteTime / 0.035) * Math.Pow(1 - noteTime / 0.65, 2) : 0;
                var value = (float)(0.12 * envelope * Math.Sin(2 * Math.PI * frequency * noteTime));
                for (var channel = 0; channel < channels; channel++) buffer[offset + i * channels + channel] = value;
            }
            return frames * channels;
        }
    }
}
