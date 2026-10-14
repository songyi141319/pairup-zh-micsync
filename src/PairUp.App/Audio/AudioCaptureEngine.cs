using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PairUp.App.Audio;

/// <summary>
/// Captures whatever is playing on the current default output device via WASAPI loopback,
/// so it can be fanned out to other devices without disturbing the original playback.
/// </summary>
public sealed class AudioCaptureEngine : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private MMDevice? _sourceDevice;
    private WaveFormat? _sourceFormat;
    private bool _intentionalStop;

    public event Action<byte[], int>? DataAvailable;
    public event Action<string>? CaptureFailed;
    public WaveFormat? CaptureFormat => _capture?.WaveFormat;
    public string? SourceDeviceId { get; private set; }
    public bool IsCapturing { get; private set; }

    /// <summary>
    /// The captured device's OS volume control (the same one Windows' Quick Settings / volume
    /// flyout shows). PairUp links its percentage to the enabled output endpoints directly.
    /// </summary>
    public AudioEndpointVolume? SourceEndpointVolume => _sourceDevice?.AudioEndpointVolume;

    public void Start()
    {
        if (IsCapturing) return;

        // A stopped capture object still owns its native stream and event handlers.
        ReleaseCapture();

        using var enumerator = new MMDeviceEnumerator();
        // Reconnecting must retain the original source. Falling back to an existing output
        // would feed it back into itself and could decode new bytes with the old format.
        _sourceDevice = SourceDeviceId is null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(SourceDeviceId);
        if (_sourceDevice.State != DeviceState.Active)
            throw new InvalidOperationException("原主音源已断开。请重新连接后刷新设备，或在声屿中选择其他主音源；原输出选择已保留。");
        SourceDeviceId = _sourceDevice.ID;
        _capture = new WasapiLoopbackCapture(_sourceDevice);
        if (_sourceFormat is not null && !SameCaptureFormat(_sourceFormat, _capture.WaveFormat))
        {
            _capture.Dispose(); _capture = null;
            throw new InvalidOperationException("音源的声音格式已改变，请恢复原 Windows 声音格式后刷新，或重新打开声屿以建立新的播放连接。");
        }
        _sourceFormat ??= _capture.WaveFormat;
        ReduceCaptureBufferLatency(_capture);
        var activeCapture = _capture;
        _capture.DataAvailable += (_, e) =>
        {
            if (ReferenceEquals(_capture, activeCapture)) DataAvailable?.Invoke(e.Buffer, e.BytesRecorded);
        };
        _capture.RecordingStopped += (_, e) =>
        {
            if (!ReferenceEquals(_capture, activeCapture)) return;
            IsCapturing = false;
            if (!_intentionalStop)
                CaptureFailed?.Invoke(e.Exception?.Message ?? "原音源已停止提供声音，请检查连接。");
        };
        _intentionalStop = false;
        IsCapturing = true;
        try { _capture.StartRecording(); }
        catch { IsCapturing = false; throw; }
    }

    /// <summary>
    /// Explicit source selection starts a new format contract. Ordinary reconnects still
    /// retain their source and reject format changes in Start(). Call without an engine lock:
    /// disposing the native capture waits for its DataAvailable callback to finish.
    /// </summary>
    public void ChangeSource(string? deviceId)
    {
        if (deviceId is not null && string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("请选择有效的主音源。", nameof(deviceId));
        try { Stop(); } catch { /* An unplugged stream must not prevent selecting a new source. */ }
        ReleaseCapture();
        SourceDeviceId = deviceId;
        _sourceFormat = null;
    }

    public static bool SameCaptureFormat(WaveFormat a, WaveFormat b) =>
        a.Encoding == b.Encoding && a.SampleRate == b.SampleRate && a.Channels == b.Channels &&
        a.BitsPerSample == b.BitsPerSample && a.BlockAlign == b.BlockAlign &&
        a.AverageBytesPerSecond == b.AverageBytesPerSecond &&
        (a is not WaveFormatExtensible ax || b is WaveFormatExtensible bx && ax.SubFormat == bx.SubFormat);

    /// <summary>
    /// NAudio sizes WasapiCapture's internal buffer at 100ms and doesn't expose that publicly,
    /// which alone dominated PairUp's added latency: measured, the default delivered captured
    /// audio in ~62ms chunks, so nothing could reach an output device sooner than that. Dropping
    /// it to 20ms cuts delivery to ~16ms chunks (~47ms saved). Going lower gains nothing — the
    /// shared-mode device period floors it around 16ms regardless.
    /// Set via reflection since there's no public setter; failure is non-fatal (we just keep
    /// NAudio's default) so a future NAudio version renaming the field can't break capture.
    /// </summary>
    private static void ReduceCaptureBufferLatency(WasapiCapture capture)
    {
        try
        {
            typeof(WasapiCapture)
                .GetField("audioBufferMillisecondsLength",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(capture, CaptureBufferMs);
        }
        catch
        {
            // Keep NAudio's default buffer; higher latency, but capture still works.
        }
    }

    private const int CaptureBufferMs = 20;

    public void Stop()
    {
        _intentionalStop = true;
        try { _capture?.StopRecording(); }
        finally { IsCapturing = false; }
    }

    private void ReleaseCapture()
    {
        _intentionalStop = true;
        IsCapturing = false;
        var capture = _capture;
        var device = _sourceDevice;
        // Detach before native disposal. A stale callback cannot feed the current graph even
        // if the disconnected driver reports a cleanup failure.
        _capture = null;
        _sourceDevice = null;
        try { capture?.Dispose(); }
        catch { /* Disconnected native handles may already be invalid. */ }
        finally
        {
            try { device?.Dispose(); }
            catch { /* Continue cleanup/reconnection after device removal. */ }
        }
    }

    public void Dispose() => ReleaseCapture();
}
