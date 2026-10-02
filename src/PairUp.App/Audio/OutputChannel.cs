using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PairUp.App.Audio;

public sealed record DriverBufferInfo(double LatencyMs, double PaddingMs);

/// <summary>
/// Renders captured audio to a single output device, independent of every other
/// OutputChannel, with its own volume and a resampler to match the device's mix format.
/// </summary>
public sealed class OutputChannel : IDisposable
{
    private readonly ChannelAudioBuffer _buffer;
    private readonly DeviceProcessingSampleProvider _processingProvider;
    private readonly VolumeSampleProvider _volumeProvider;
    private readonly WasapiOut _output;
    private readonly AudioClient? _renderClient;

    public string DeviceId { get; }

    public OutputChannel(MMDevice device, WaveFormat sourceFormat)
    {
        DeviceId = device.ID;
        _buffer = new ChannelAudioBuffer(sourceFormat);
        ISampleProvider sampleChain = _buffer.Samples;

        // Match channel count before resampling so WdlResampler only has to handle sample rate.
        if (sourceFormat.Channels == 1 && device.AudioClient.MixFormat.Channels == 2)
            sampleChain = new MonoToStereoSampleProvider(sampleChain);
        else if (sourceFormat.Channels == 2 && device.AudioClient.MixFormat.Channels == 1)
            sampleChain = new StereoToMonoSampleProvider(sampleChain);

        if (sourceFormat.SampleRate != device.AudioClient.MixFormat.SampleRate)
            sampleChain = new WdlResamplingSampleProvider(sampleChain, device.AudioClient.MixFormat.SampleRate);

        _processingProvider = new DeviceProcessingSampleProvider(sampleChain);
        _volumeProvider = new VolumeSampleProvider(_processingProvider) { Volume = 0.75f };

        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, ChooseLatencyMs(device));
        _output.Init(_volumeProvider);
        // MMDevice.AudioClient activates a NEW client each time. Inspect the initialized
        // renderer's client instead; the pinned NAudio 2.2.1 has no public accessor.
        try
        {
            _renderClient = typeof(WasapiOut).GetField("audioClient",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_output) as AudioClient;
        }
        catch { _renderClient = null; }
        _output.Play();
    }

    /// <summary>
    /// Sizes the WASAPI render buffer from what the device's own driver reports it needs, rather
    /// than guessing from the device's name.
    ///
    /// This replaced a fixed 100ms buffer for everything, and then a name-based
    /// "Bluetooth gets 100ms, everything else gets 10ms" split. The name heuristic turned out
    /// to be unreliable in exactly the case it mattered: AirPods enumerate as
    /// "Headphones (AirPods Pro - ...)" with no "bluetooth" anywhere in the string, so a genuinely
    /// wireless device was already being treated as wired — while the 100ms branch only ever
    /// caught the Hands-Free telephony endpoints. Asking the driver for its own period sidesteps
    /// the guessing: a device that needs more headroom reports a longer period and gets it.
    ///
    /// Clamped to a 10ms floor because requesting below the shared-mode period just gets clamped
    /// by the audio engine anyway (measured: these endpoints report a 10ms period, with 3ms
    /// available only in exclusive mode), and to a 100ms ceiling so a pathological driver can't
    /// reintroduce the original latency problem.
    /// </summary>
    private static int ChooseLatencyMs(MMDevice device)
    {
        try
        {
            // DefaultDevicePeriod is in 100-nanosecond units.
            var periodMs = device.AudioClient.DefaultDevicePeriod / 10000.0;
            return (int)Math.Clamp(Math.Ceiling(periodMs), 10, 100);
        }
        catch
        {
            return 20; // Driver wouldn't say; pick a safe middle ground.
        }
    }

    /// <summary>
    /// Queues newly captured audio for this device. If the auto-sync balancer has scheduled a
    /// trim (see <see cref="TrimBacklog"/>), some of the newest audio is quietly dropped here
    /// instead — this is the only safe place to shrink this channel's queued backlog, since
    /// WasapiOut's own playback thread is concurrently calling Read() on the same buffer from
    /// the consumer side; dropping on the way in avoids racing that read.
    /// </summary>
    public void Feed(byte[] data, int bytesRecorded)
    {
        _buffer.Feed(data, bytesRecorded);
    }

    public float Volume
    {
        get => _volumeProvider.Volume;
        set => _volumeProvider.Volume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Low-shelf gain in dB, roughly -12..+12, for small speakers that need more bass.</summary>
    public double BassBoostDb
    {
        get => _processingProvider.BassGainDb;
        set => _processingProvider.BassGainDb = value;
    }

    /// <summary>High-shelf gain in dB, roughly -12..+12, to tame harsh/tinny earbuds.</summary>
    public double TrebleDb
    {
        get => _processingProvider.TrebleGainDb;
        set => _processingProvider.TrebleGainDb = value;
    }

    /// <summary>Sums stereo down to identical L/R so single-earbud devices don't lose panned content.</summary>
    public bool IsMono
    {
        get => _processingProvider.MonoDownmix;
        set => _processingProvider.MonoDownmix = value;
    }

    /// <summary>
    /// Holds faster devices back using a delay line which the drift balancer cannot erase.
    /// </summary>
    public void SetDelay(double targetMs)
    {
        _buffer.SetDelay(targetMs);
    }

    public double DelayMs => _buffer.DelayMs;
    public void ClearForCalibration() => _buffer.Clear();
    public TimeSpan BufferedDuration => _buffer.BufferedDuration;
    public AudioQueueSnapshot TakeQueueSnapshot() => _buffer.TakeSnapshot();

    public DriverBufferInfo? GetDriverBufferInfo()
    {
        try
        {
            if (_renderClient is null) return null;
            return new(_renderClient.StreamLatency / 10000.0,
                _renderClient.CurrentPadding * 1000.0 / _output.OutputWaveFormat.SampleRate);
        }
        catch { return null; } // Disconnected or unsupported driver: unknown, never zero.
    }

    /// <summary>
    /// Small continuous correction used by the auto-sync balancer to counteract clock drift,
    /// independent of the user-set delay so the slider position stays stable.
    /// </summary>
    public void Nudge(double deltaMs)
    {
        _buffer.Nudge(deltaMs);
    }

    /// <summary>
    /// Shrinks this channel's queued backlog by dropping upcoming captured audio at the feed
    /// side rather than reading it back out of the buffer — used by the auto-sync balancer to
    /// pull a device that's fallen behind (built up more queued audio than the others) back
    /// toward the pack, without racing WasapiOut's own concurrent reads from the same buffer.
    /// </summary>
    public void TrimBacklog(double deltaMs)
    {
        _buffer.TrimBacklog(deltaMs);
    }

    public void Dispose()
    {
        _output.Stop();
        _output.Dispose();
    }
}
