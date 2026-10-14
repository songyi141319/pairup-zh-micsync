using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PairUp.App.Services;

/// <summary>A finite, gently enveloped chime sent directly to one Windows endpoint.</summary>
internal static class DevicePreview
{
    public static async Task PlayAsync(string deviceId, CancellationToken cancellationToken)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(deviceId);
        if (device.State != DeviceState.Active)
            throw new InvalidOperationException("这台设备未连接，请连接后再试听。");
        if (device.AudioEndpointVolume.Mute || device.AudioEndpointVolume.MasterVolumeLevelScalar <= 0)
            throw new InvalidOperationException("这台设备已静音或音量为零，请先调整音量再试听。");
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 30);
        var format = device.AudioClient.MixFormat;
        output.Init(new Chime(format.SampleRate, format.Channels).ToWaveProvider());
        var sessions = device.AudioSessionManager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            if (session.GetProcessID == Environment.ProcessId &&
                (session.SimpleAudioVolume.Mute || session.SimpleAudioVolume.Volume <= 0))
                throw new InvalidOperationException("声屿的应用音量已静音或为零，请先在 Windows 音量混合器中恢复后试听。");
        }
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) => stopped.TrySetResult(args.Exception);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            output.Play();
            var timeout = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            var finished = await Task.WhenAny(stopped.Task, timeout);
            cancellationToken.ThrowIfCancellationRequested();
            if (finished != stopped.Task) throw new InvalidOperationException("设备未及时结束试听，请检查连接。");
            if (await stopped.Task is { } error) throw new InvalidOperationException("试听播放失败：" + error.Message, error);
        }
        finally { output.Stop(); }
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
