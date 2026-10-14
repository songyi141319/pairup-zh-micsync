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
    private bool _isChangingSource;
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
    /// Rebuilds the graph for an explicitly selected source, retaining this engine instance.
    /// The caller owns the Windows-role and persisted/UI snapshots. Its callbacks participate
    /// in the same rollback; no Windows endpoint volume or mute is changed by this operation.
    /// Capture disposal must stay outside _lock because it joins the native capture callback.
    /// </summary>
    public void ChangeSource(string deviceId, IReadOnlyCollection<DeviceSettings> outputs,
        Action changeWindowsDefaults, Action restoreWindowsDefaults, Action applySettings, Action restoreSettings)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("请选择有效的主音源。", nameof(deviceId));
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(changeWindowsDefaults);
        ArgumentNullException.ThrowIfNull(restoreWindowsDefaults);
        ArgumentNullException.ThrowIfNull(applySettings);
        ArgumentNullException.ThrowIfNull(restoreSettings);
        var requested = outputs.ToArray();
        foreach (var output in requested)
        {
            if (string.Equals(output.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("新主音源不能同时接收转发，否则会产生回授。");
            if (!double.IsFinite(output.LatencyMs) || output.LatencyMs < 0 || output.LatencyMs > FixedDelaySampleProvider.MaximumDelayMs ||
                !double.IsFinite(output.BassBoost) || !double.IsFinite(output.Treble))
                throw new InvalidOperationException("设备的延迟或音色设置无效，暂不能切换主音源。");
            output.Spatial?.Validate();
        }
        if (requested.Select(output => output.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Length)
            throw new InvalidOperationException("转发设备列表存在重复项。");

        string? previousSource;
        DeviceSettings[] previousOutputs;
        lock (_lock)
        {
            if (_isCalibrating || _isChangingSource) throw new InvalidOperationException("正在校准或切换主音源，请稍后再试。");
            previousSource = _capture.SourceDeviceId;
            previousOutputs = _channels.Select(item => new DeviceSettings(item.Key, true, 0, item.Value.DelayMs,
                BassBoost: item.Value.BassBoostDb, Treble: item.Value.TrebleDb, IsMono: item.Value.IsMono, Spatial: item.Value.Spatial)).ToArray();
            _isChangingSource = true;
            foreach (var channel in _channels.Values) channel.Volume = 0;
            _queueHistory.Clear();
        }

        try
        {
            _capture.Stop();
            ReleaseSourceOutputs();
            changeWindowsDefaults();
            _capture.ChangeSource(deviceId);
            _capture.Start();
            _desiredMute = _capture.SourceEndpointVolume?.Mute ?? false;
            foreach (var output in requested) AddSourceOutput(output);
            applySettings();
            lock (_lock)
            {
                if (!_capture.IsCapturing) throw new InvalidOperationException(CaptureError ?? "新主音源未能持续提供声音。");
                CaptureError = null;
                SourceControlError = null;
                _isChangingSource = false;
                foreach (var (id, channel) in _channels) channel.Volume = PlaybackGain(id);
            }
            _syncTimer ??= new Timer(_ => RebalanceChannels(), null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(SyncIntervalMs));
        }
        catch (Exception failure)
        {
            var recovery = new List<string>();
            try { _capture.Stop(); } catch (Exception ex) { recovery.Add("停止新音源：" + ex.Message); }
            try { ReleaseSourceOutputs(); } catch (Exception ex) { recovery.Add("释放新转发：" + ex.Message); }
            try { restoreWindowsDefaults(); } catch (Exception ex) { recovery.Add("恢复 Windows 默认设备：" + ex.Message); }
            try { _capture.ChangeSource(previousSource); _capture.Start(); }
            catch (Exception ex) { recovery.Add("恢复原音源：" + ex.Message); }
            foreach (var output in previousOutputs)
                try { AddSourceOutput(output); }
                catch (Exception ex) { recovery.Add("恢复原转发设备：" + ex.Message); }
            var audioRestored = recovery.Count == 0;
            try { restoreSettings(); } catch (Exception ex) { recovery.Add("恢复原设置：" + ex.Message); }
            lock (_lock)
            {
                _isChangingSource = false;
                _queueHistory.Clear();
                if (audioRestored && _capture.IsCapturing)
                {
                    CaptureError = null;
                    try { _desiredMute = _capture.SourceEndpointVolume?.Mute ?? _desiredMute; }
                    catch (Exception ex) { recovery.Add("读取原音源静音：" + ex.Message); }
                }
                else
                {
                    if (!_capture.IsCapturing) recovery.Add("原音源未能持续提供声音。");
                    CaptureError = "主音源切换失败，原播放连接未能完整恢复：" + string.Join("；", recovery);
                }
                foreach (var (id, channel) in _channels) channel.Volume = PlaybackGain(id);
            }
            var detail = recovery.Count == 0 ? "已恢复原主音源和转发设置。" : "恢复时仍有问题：" + string.Join("；", recovery);
            throw new InvalidOperationException("未能切换主音源：" + failure.Message + "\n" + detail, failure);
        }
    }

    private void AddSourceOutput(DeviceSettings settings)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(settings.DeviceId);
        if (device.State != DeviceState.Active) throw new InvalidOperationException("转发设备已断开：" + device.FriendlyName);
        if (_capture.CaptureFormat is not { } format) throw new InvalidOperationException("主音源格式尚未就绪。");
        OutputChannel? channel = null;
        try
        {
            channel = new OutputChannel(device, format) { Volume = 0 };
            channel.SetDelay(settings.LatencyMs);
            channel.BassBoostDb = settings.BassBoost;
            channel.TrebleDb = settings.Treble;
            channel.IsMono = settings.IsMono;
            channel.Spatial = settings.Spatial ?? new();
            lock (_lock) _channels.Add(settings.DeviceId, channel);
        }
        catch
        {
            try { channel?.Dispose(); } catch { /* Keep the original channel-opening error. */ }
            throw;
        }
    }

    private void ReleaseSourceOutputs()
    {
        OutputChannel[] channels;
        lock (_lock)
        {
            channels = _channels.Values.ToArray();
            _channels.Clear();
            _queueHistory.Clear();
        }
        var errors = new List<string>();
        foreach (var channel in channels)
            try { channel.Dispose(); }
            catch (Exception ex) { errors.Add(ex.Message); }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors));
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
            if (_isCalibrating || _isChangingSource || _channels.Count == 0) return;
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

            OutputChannel? pendingChannel = null;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(deviceId);
                var channel = pendingChannel = new OutputChannel(device, _capture.CaptureFormat!);
                channel.SetDelay(delayMs);
                channel.BassBoostDb = bassBoostDb;
                channel.TrebleDb = trebleDb;
                channel.IsMono = isMono;
                channel.Spatial = spatial ?? new SpatialProfile();
                channel.Volume = PlaybackGain(deviceId);
                _channels[deviceId] = channel;
                pendingChannel = null;
            }
            catch (NotSupportedException) { return AddOutputResult.UnsupportedFormat; }
            catch (Exception)
            {
                return AddOutputResult.DeviceUnavailable;
            }
            finally { try { pendingChannel?.Dispose(); } catch { /* Preserve the connection result. */ } }
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
    private float PlaybackGain(string deviceId) => CaptureError is not null || _isChangingSource ? 0f : _isCalibrating
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
            if (_isCalibrating || _isChangingSource) throw new InvalidOperationException("已有校准或主音源切换正在进行");
            _sourceMuteBeforeCalibration = _capture.SourceEndpointVolume?.Mute ?? _desiredMute;
            _calibrationDelays = _channels.ToDictionary(p => p.Key, p => p.Value.DelayMs);
            _isCalibrating = true;
            _queueHistory.Clear();
            _calibrationTargetId = null;
            CalibrationRestoreWarning = null;
            try
            {
                foreach (var channel in _channels.Values)
                {
                    channel.Volume = 0;
                    channel.SetCalibrationBypass(true);
                    channel.SetDelay(0);
                    channel.ClearForCalibration();
                }
            }
            catch (Exception ex)
            {
                // No scope has been returned yet. Restore all touched channels here.
                EndAcousticCalibration();
                throw new InvalidOperationException("无法准备声音隔离：" + ex.Message +
                    (CalibrationRestoreWarning is null ? "；已恢复原设置。" : "；" + CalibrationRestoreWarning), ex);
            }
        }
        return new CalibrationScope(this);
    }

    /// <summary>Stops feeding isolated outputs and clears queued probe audio before volume restoration.</summary>
    public void SilenceCalibrationOutputs()
    {
        lock (_lock)
        {
            if (!_isCalibrating) return;
            _calibrationTargetId = null;
            var errors = new List<string>();
            foreach (var channel in _channels.Values)
            {
                try { channel.Volume = 0; }
                catch (Exception ex) { errors.Add("静音转发输出：" + ex.Message); }
                try { channel.ClearForCalibration(); }
                catch (Exception ex) { errors.Add("清除转发余音：" + ex.Message); }
            }
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors));
        }
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
            var errors = new List<string>();
            foreach (var (id, channel) in _channels)
            {
                var failed = false;
                void RestorePart(Action restore)
                {
                    try { restore(); }
                    catch (Exception ex) { failed = true; errors.Add("恢复转发设备 " + id + "：" + ex.Message); }
                }
                RestorePart(() => channel.Volume = 0);
                RestorePart(channel.ClearForCalibration);
                if (_calibrationDelays?.TryGetValue(id, out var delay) == true) RestorePart(() => channel.SetDelay(delay));
                RestorePart(() => channel.SetCalibrationBypass(false));
                RestorePart(() => channel.Volume = !failed && CaptureError is null ? 1f : 0f);
            }
            _calibrationTargetId = null;
            _calibrationDelays = null;
            _isCalibrating = false;
            _queueHistory.Clear();
            _desiredMute = _sourceMuteBeforeCalibration;
            try
            {
                var volume = _capture.SourceEndpointVolume ?? throw new InvalidOperationException("原音源音量控制已不可用。");
                volume.Mute = _sourceMuteBeforeCalibration;
            }
            catch (Exception ex) { errors.Add("无法恢复原音源静音状态：" + ex.Message); }
            CalibrationRestoreWarning = errors.Count == 0 ? null : string.Join("；", errors);
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
            if (_isChangingSource) return;
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
