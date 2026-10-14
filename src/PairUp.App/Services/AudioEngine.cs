using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using PairUp.App.Audio;

namespace PairUp.App.Services;

public enum AddOutputResult
{
    Success,
    WouldEcho,
    DeviceUnavailable,
    SourceUnavailable,
    CalibrationInProgress,
    UnsupportedFormat
}

public sealed record SyncStatus(string DeviceId, double DriftMs, bool IsReference, double QueueMs = 0,
    double? DriverLatencyMs = null, double? DriverPaddingMs = null);

/// <summary>
/// Orchestrates one loopback capture from the current default device and fans it out
/// to any number of independently-controlled output devices.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly AudioCaptureEngine _capture = new();
    private readonly SpectrumAnalyzer _spectrum = new();
    private readonly Dictionary<string, OutputChannel> _channels = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<double>> _queueHistory = new();
    private Timer? _syncTimer;
    private bool _isCalibrating;
    private string? _calibrationTargetId;
    private Dictionary<string, double>? _calibrationDelays;
    private bool _sourceMuteBeforeCalibration;
    public string? CalibrationRestoreWarning { get; private set; }
    public bool IsCalibrating { get { lock (_lock) return _isCalibrating; } }

    private const double SyncIntervalMs = 400;
    private const double SyncDeadZoneMs = 6;   // ignore differences smaller than this to avoid constant micro-jitter
    private const double SyncMaxStepMs = 12;   // cap per-tick correction so nudges stay inaudible
    private const double MaximumStableBacklogMs = 40;

    public event Action<IReadOnlyList<SyncStatus>>? SyncStatusUpdated;

    private bool _desiredMute;
    public string? VolumeControlError { get; private set; }

    public string? SourceDeviceId => _capture.SourceDeviceId;
    public string? CaptureError { get; private set; }
    public string? SourceControlError { get; private set; }

    public AudioEngine()
    {
        _capture.DataAvailable += OnDataAvailable;
        _capture.CaptureFailed += OnCaptureFailed;
    }

    private void OnCaptureFailed(string message)
    {
        lock (_lock)
        {
            CaptureError = "音源无法继续播放：" + message;
            _queueHistory.Clear();
            foreach (var channel in _channels.Values)
            {
                channel.Volume = 0;
                channel.ClearForCalibration();
            }
        }
    }

    public void EnsureCaptureStarted()
    {
        try
        {
            var restarting = !_capture.IsCapturing;
            if (restarting)
            {
                _capture.Start();
                _desiredMute = _capture.SourceEndpointVolume?.Mute ?? false;
            }
            lock (_lock)
            {
                if (!_capture.IsCapturing) throw new InvalidOperationException("原音源已停止提供声音，请检查连接。");
                CaptureError = null;
                if (restarting)
                    foreach (var (id, channel) in _channels)
                        channel.Volume = PlaybackGain(id);
            }
            _syncTimer ??= new Timer(_ => RebalanceChannels(), null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(SyncIntervalMs));
        }
        catch (Exception ex)
        {
            try { _capture.Stop(); } catch { /* The disconnected stream may already be gone. */ }
            OnCaptureFailed(ex.Message);
            throw new InvalidOperationException(CaptureError, ex);
        }
    }

    /// <summary>
    /// Compare minima observed at actual feed/read boundaries, avoiding timer/chunk aliasing.
    /// Trim relative backlog and sustained common backlog, also with one output. Fixed manual
    /// delays live in a separate ring and are never included. Trim future producer input only;
    /// do not consume the playback thread's buffer or repeatedly insert new silence.
    /// </summary>
    private void RebalanceChannels()
    {
        List<SyncStatus>? statuses = null;

        lock (_lock)
        {
            if (_isCalibrating || _channels.Count == 0) return;
            // Playback threads keep consuming concurrently: take each queue reading once.
            var snapshot = _channels.Select(p => (Id: p.Key, Channel: p.Value, Queue: p.Value.TakeQueueSnapshot())).ToArray();
            var target = snapshot.Min(p => p.Queue.MinimumMs);
            statuses = new List<SyncStatus>(_channels.Count);
            foreach (var item in snapshot)
            {
                if (!_queueHistory.TryGetValue(item.Id, out var history))
                    _queueHistory[item.Id] = history = new Queue<double>();
                history.Enqueue(item.Queue.MinimumMs);
                if (history.Count > 5) history.Dequeue();
                var stableQueue = history.Min();
                var excess = Math.Max(0, item.Queue.MinimumMs - target);
                // A shared slowdown is invisible to peer comparisons; limit sustained backlog
                // separately, including when only one output is enabled. Manual delay is excluded.
                if (history.Count == 5) excess = Math.Max(excess, stableQueue - MaximumStableBacklogMs);
                if (excess >= SyncDeadZoneMs) item.Channel.TrimBacklog(Math.Min(excess, SyncMaxStepMs));
                var driver = item.Channel.GetDriverBufferInfo();
                statuses.Add(new SyncStatus(item.Id, item.Queue.MinimumMs - target, item.Queue.MinimumMs <= target + 0.5,
                    item.Queue.CurrentMs, driver?.LatencyMs, driver?.PaddingMs));
            }
        }

        SyncStatusUpdated?.Invoke(statuses);
    }

    /// <summary>
    /// WouldEcho: the requested device is the same one currently being captured from.
    /// DeviceUnavailable: the device couldn't be opened (e.g. a Bluetooth device that's
    /// paired but out of range, or was unplugged since the last device list refresh).
    /// </summary>
    public AddOutputResult TryAddOutput(
        string deviceId, double delayMs = 0,
        double bassBoostDb = 0, double trebleDb = 0, bool isMono = false, SpatialProfile? spatial = null)
    {
        spatial?.Validate();
        lock (_lock)
            if (_isCalibrating) return AddOutputResult.CalibrationInProgress;
        try { EnsureCaptureStarted(); }
        catch { return AddOutputResult.SourceUnavailable; }

        if (string.Equals(deviceId, _capture.SourceDeviceId, StringComparison.OrdinalIgnoreCase))
            return AddOutputResult.WouldEcho;

        lock (_lock)
        {
            if (_isCalibrating) return AddOutputResult.CalibrationInProgress;
            if (_channels.ContainsKey(deviceId))
                return AddOutputResult.Success;

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(deviceId);
                var channel = new OutputChannel(device, _capture.CaptureFormat!);
                channel.SetDelay(delayMs);
                channel.BassBoostDb = bassBoostDb;
                channel.TrebleDb = trebleDb;
                channel.IsMono = isMono;
                channel.Spatial = spatial ?? new SpatialProfile();
                channel.Volume = PlaybackGain(deviceId);
                _channels[deviceId] = channel;
            }
            catch (NotSupportedException) { return AddOutputResult.UnsupportedFormat; }
            catch (Exception)
            {
                return AddOutputResult.DeviceUnavailable;
            }
        }

        return AddOutputResult.Success;
    }

    public void RemoveOutput(string deviceId)
    {
        lock (_lock)
        {
            if (_isCalibrating) return;
            if (_channels.Remove(deviceId, out var channel))
                channel.Dispose();
            _queueHistory.Remove(deviceId);
        }
    }

    public void SetBassBoost(string deviceId, double gainDb)
    {
        lock (_lock)
        {
            if (_isCalibrating) throw new InvalidOperationException("校准期间暂不调整音色。");
            if (_channels.TryGetValue(deviceId, out var channel))
                channel.BassBoostDb = gainDb;
        }
    }

    public void SetTreble(string deviceId, double gainDb)
    {
        lock (_lock)
        {
            if (_isCalibrating) throw new InvalidOperationException("校准期间暂不调整音色。");
            if (_channels.TryGetValue(deviceId, out var channel))
                channel.TrebleDb = gainDb;
        }
    }

    public void SetMono(string deviceId, bool isMono)
    {
        lock (_lock)
        {
            if (_isCalibrating) throw new InvalidOperationException("校准期间暂不调整声道。");
            if (_channels.TryGetValue(deviceId, out var channel))
                channel.IsMono = isMono;
        }
    }

    public void SetSpatial(string deviceId, SpatialProfile spatial)
    {
        ArgumentNullException.ThrowIfNull(spatial);
        spatial.Validate();
        lock (_lock)
        {
            if (_isCalibrating) throw new InvalidOperationException("校准期间暂不调整音场。");
            if (string.Equals(deviceId, _capture.SourceDeviceId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("主音源由 Windows 直接播放，只能保持原声；请为其他输出设置音场。");
            if (_channels.TryGetValue(deviceId, out var channel)) channel.Spatial = spatial;
        }
    }

    public bool SetVolume(string deviceId, double volumePercent)
    {
        lock (_lock)
        {
            if (_isCalibrating)
            {
                VolumeControlError = "校准期间暂不调整设备音量。";
                return false;
            }
        }
        var success = WindowsDeviceVolume.TrySet(deviceId, volumePercent, out var error);
        VolumeControlError = error;
        return success;
    }

    /// <summary>
    /// Sets real Windows volume for the source and enabled outputs only. The caller serializes
    /// UI and Windows changes on the dispatcher; no endpoint callbacks perform COM writes.
    /// </summary>
    public bool SetMasterVolume(double volumePercent)
    {
        string[] ids;
        lock (_lock)
        {
            if (_isCalibrating || SourceDeviceId is null)
            {
                VolumeControlError = _isCalibrating ? "校准期间暂不调整设备音量。" : "音源尚未就绪，请稍后再调整音量。";
                return false;
            }
            ids = _channels.Keys.Prepend(SourceDeviceId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        var errors = new List<string>();
        foreach (var id in ids)
            if (!WindowsDeviceVolume.TrySet(id, volumePercent, out var error))
                errors.Add(error ?? "设备音量同步失败。");
        VolumeControlError = errors.Count == 0 ? null : string.Join("；", errors.Distinct());
        return errors.Count == 0;
    }

    // Endpoint volume already controls loudness. Software gain only gates calibration/failure;
    // multiplying master and device percentages here would attenuate the same signal twice.
    private float PlaybackGain(string deviceId) => CaptureError is not null ? 0f : _isCalibrating
        ? (deviceId == _calibrationTargetId ? 1f : 0f) : 1f;

    public void SetDelay(string deviceId, double delayMs)
    {
        lock (_lock)
        {
            if (_channels.TryGetValue(deviceId, out var channel))
                channel.SetDelay(delayMs);
        }
    }

    /// <summary>
    /// Mutes the source device's own OS output directly (the device PairUp is capturing from),
    /// so it can be silenced while audio still fans out to the connected devices — there's no
    /// virtual driver making the source silent by default anymore.
    /// </summary>
    public bool TrySetSourceMuted(bool muted)
    {
        try
        {
            EnsureCaptureStarted();
            if (_capture.SourceEndpointVolume is not { } endpointVolume)
                throw new InvalidOperationException("音源音量控制不可用");
            lock (_lock)
            {
                endpointVolume.Mute = muted;
                _desiredMute = muted;
            }
            SourceControlError = null;
            return true;
        }
        catch (Exception ex) { SourceControlError = "无法调整音源静音：" + ex.Message; return false; }
    }

    public bool IsSourceMuted => _desiredMute;

    /// <summary>Keep the real loopback/render path, but isolate one speaker and bypass its old offset.</summary>
    public IDisposable BeginAcousticCalibration()
    {
        EnsureCaptureStarted();
        lock (_lock)
        {
            if (_isCalibrating) throw new InvalidOperationException("已有校准正在进行");
            _sourceMuteBeforeCalibration = _capture.SourceEndpointVolume?.Mute ?? _desiredMute;
            _calibrationDelays = _channels.ToDictionary(p => p.Key, p => p.Value.DelayMs);
            _isCalibrating = true;
            _queueHistory.Clear();
            _calibrationTargetId = null;
            CalibrationRestoreWarning = null;
            foreach (var channel in _channels.Values)
            {
                channel.Volume = 0;
                channel.SetCalibrationBypass(true);
                channel.SetDelay(0);
                channel.ClearForCalibration();
            }
        }
        return new CalibrationScope(this);
    }

    public double SelectCalibrationOutput(string? deviceId, double separationMs = 0)
    {
        if (!double.IsFinite(separationMs) || separationMs < 0 || separationMs > FixedDelaySampleProvider.MaximumDelayMs)
            throw new ArgumentOutOfRangeException(nameof(separationMs));
        var actualDelayMs = 0.0;
        lock (_lock)
        {
            if (!_isCalibrating) throw new InvalidOperationException("校准未启动");
            if (deviceId is not null && !_channels.ContainsKey(deviceId))
                throw new InvalidOperationException("待测音箱已断开，请连接后重试");
            _calibrationTargetId = deviceId;
            foreach (var (id, channel) in _channels)
            {
                channel.Volume = 0;
                channel.ClearForCalibration();
                channel.SetDelay(id == deviceId ? separationMs : 0);
                channel.Volume = id == deviceId ? 1f : 0f;
                if (id == deviceId) actualDelayMs = channel.DelayMs;
            }
        }
        // Silencing this endpoint also silences the loopback on some drivers. Keep it audible;
        // the output's known extra delay separates its probe from the source in the microphone.
        if (!TrySetSourceMuted(false)) throw new InvalidOperationException(SourceControlError ?? "无法取得音源，无法测量每台音箱");
        return actualDelayMs;
    }

    private void EndAcousticCalibration()
    {
        lock (_lock)
        {
            foreach (var (id, channel) in _channels)
            {
                channel.Volume = 0;
                channel.ClearForCalibration();
                if (_calibrationDelays?.TryGetValue(id, out var delay) == true) channel.SetDelay(delay);
                channel.SetCalibrationBypass(false);
                channel.Volume = CaptureError is null ? 1f : 0f;
            }
            _calibrationTargetId = null;
            _calibrationDelays = null;
            _isCalibrating = false;
            _queueHistory.Clear();
            _desiredMute = _sourceMuteBeforeCalibration;
            try
            {
                if (_capture.SourceEndpointVolume is { } volume) volume.Mute = _sourceMuteBeforeCalibration;
            }
            catch { CalibrationRestoreWarning = "音源设备已断开，无法恢复它的静音状态；重新连接后请检查音源静音开关。"; }
        }
    }

    private sealed class CalibrationScope(AudioEngine engine) : IDisposable
    {
        private AudioEngine? _engine = engine;
        public void Dispose() => Interlocked.Exchange(ref _engine, null)?.EndAcousticCalibration();
    }

    private void OnDataAvailable(byte[] buffer, int bytesRecorded)
    {
        lock (_lock)
        {
            foreach (var (id, channel) in _channels)
                if (!_isCalibrating || id == _calibrationTargetId)
                    channel.Feed(buffer, bytesRecorded);
        }

        if (_capture.CaptureFormat is { } format)
            _spectrum.Feed(buffer, bytesRecorded, format);
    }

    /// <summary>Polled by the UI (e.g. once per rendered frame) to drive the audio visualizer.</summary>
    public float[] GetSpectrum(int bandCount) => _spectrum.GetBands(bandCount);

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        _capture.CaptureFailed -= OnCaptureFailed;
        _syncTimer?.Dispose();

        lock (_lock)
        {
            foreach (var channel in _channels.Values)
                channel.Dispose();
            _channels.Clear();
        }

        _capture.Dispose();
    }
}
