using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace PairUp.App.Services;

/// <summary>
/// Temporary endpoint levels and this process's own mixer sessions only. Snapshots are taken
/// before the first write and retained across retries. This class never writes app settings,
/// changes a microphone level, or adjusts another process's mixer volume.
/// </summary>
internal sealed class CalibrationVolumeScope : IDisposable
{
    private sealed class EndpointSnapshot(MMDevice device, string name, float volume, bool muted)
    {
        public MMDevice Device { get; } = device;
        public string Name { get; } = name;
        public float Volume { get; } = volume;
        public bool Muted { get; } = muted;
        public float AppliedVolume { get; set; }
        public bool Changed { get; set; }
    }

    private sealed class SessionSnapshot(AudioSessionControl session, string deviceName, float volume, bool muted)
    {
        public AudioSessionControl Session { get; } = session;
        public string DeviceName { get; } = deviceName;
        public float Volume { get; } = volume;
        public bool Muted { get; } = muted;
        public bool Changed { get; set; }
    }

    private readonly List<EndpointSnapshot> _endpoints = new();
    private readonly Dictionary<string, SessionSnapshot> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (float Volume, bool Muted)> _sessionBaselines = new(StringComparer.Ordinal);
    private readonly List<string> _restoreWarnings = new();
    private readonly Queue<string> _diagnostics = new();
    private string? _lastDiagnostic;
    private string _baselineDescription = "尚未读取测试前音量。";
    private int? _requestedPercent;
    private readonly long _startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    private bool _captured;
    private bool _disposed;
    public IReadOnlyList<string> RestoreWarnings => _restoreWarnings;

    public void Capture(IReadOnlyList<CalibrationTarget> targets)
    {
        if (_captured || _endpoints.Count != 0) throw new InvalidOperationException("校准音量快照已创建。");
        using var enumerator = new MMDeviceEnumerator();
        // Read every endpoint successfully before changing any of them. The caller owns this
        // scope before Capture is called, so a partially opened set is also disposed/restored.
        foreach (var target in targets)
        {
            MMDevice? device = enumerator.GetDevice(target.DeviceId);
            try
            {
                if (device.State != DeviceState.Active) throw new InvalidOperationException($"“{target.Name}”已断开，请重新连接后测量。");
                var volume = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                var muted = device.AudioEndpointVolume.Mute;
                if (!float.IsFinite(volume) || volume < 0 || volume > 1)
                    throw new InvalidOperationException($"无法读取“{target.Name}”的原音量，未开始校准。");
                _endpoints.Add(new(device, target.Name, volume, muted));
                device = null;
            }
            finally { device?.Dispose(); }
        }
        _captured = true;
        _baselineDescription = "测试开始前的实际音量（恢复依据）：" + string.Join("；", _endpoints.Select(endpoint =>
            $"{endpoint.Name} {endpoint.Volume * 100:0.0}%{(endpoint.Muted ? "（静音）" : "")}"));
        RecordDiagnostic("保存原音量", _baselineDescription);
    }

    public void ApplyLevel(int percent, bool manual = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_captured) throw new InvalidOperationException("尚未保存原音量，不能开始校准。");
        if (percent < 1 || percent > (manual ? 100 : 50)) throw new ArgumentOutOfRangeException(nameof(percent));
        _requestedPercent = percent;
        RecordCurrentVolumes("设置临时音量前");
        foreach (var endpoint in _endpoints)
        {
            EnsureConnected(endpoint);
            var volume = endpoint.Device.AudioEndpointVolume;
            endpoint.Changed = true; // A failing setter may already have changed native state.
            volume.MasterVolumeLevelScalar = percent / 100f;
            using var enumerator = new MMDeviceEnumerator();
            var applied = ReadEndpoint(endpoint, enumerator);
            RecordDiagnostic("写入临时音量后", applied.Description);
            if (!Matches(applied.Current, percent / 100f, 0.02f) || !Matches(applied.Retained, applied.Current))
                throw new InvalidOperationException($"“{endpoint.Name}”未能设置为 {percent}% 临时音量。{applied.Description}。已停止测试。");
            endpoint.AppliedVolume = applied.Current;
            if (volume.Mute) volume.Mute = false;
            var unmuted = ReadEndpoint(endpoint, enumerator);
            RecordDiagnostic("确认设备解除静音后", unmuted.Description);
            VerifyApplied(endpoint, unmuted, "确认设备解除静音后");
        }
        PrepareOwnSessions();
    }

    /// <summary>Call after creating each probe stream, before Play, to include its new session.</summary>
    public void PrepareOwnSessions(string stage = "调整声屿应用音量后")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var endpoint in _endpoints)
        {
            EnsureConnected(endpoint);
            var manager = endpoint.Device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                AudioSessionControl? session = sessions[i];
                try
                {
                    if (session.GetProcessID != Environment.ProcessId || session.State == AudioSessionState.AudioSessionStateExpired) continue;
                    var identity = session.GetSessionInstanceIdentifier;
                    var logicalIdentity = session.GetSessionIdentifier;
                    if (string.IsNullOrEmpty(identity) || string.IsNullOrEmpty(logicalIdentity))
                        throw new InvalidOperationException($"无法识别“{endpoint.Name}”上的声屿音频会话，已停止校准以保留原设置。");
                    var key = endpoint.Device.ID + "\n" + identity;
                    if (!_sessions.TryGetValue(key, out var snapshot))
                    {
                        var volume = session.SimpleAudioVolume.Volume;
                        var muted = session.SimpleAudioVolume.Mute;
                        if (!float.IsFinite(volume) || volume < 0 || volume > 1)
                            throw new InvalidOperationException($"无法读取“{endpoint.Name}”的声屿原应用音量。");
                        // A new stream instance can reuse the same persisted app session.
                        // It must inherit the pre-calibration baseline, not our temporary 100%.
                        var baselineKey = endpoint.Device.ID + "\n" + logicalIdentity;
                        if (!_sessionBaselines.TryGetValue(baselineKey, out var baseline))
                            _sessionBaselines.Add(baselineKey, baseline = (volume, muted));
                        snapshot = new(session, endpoint.Name, baseline.Volume, baseline.Muted);
                        _sessions.Add(key, snapshot);
                        session = null; // Snapshot owns the handle until restoration completes.
                    }
                    snapshot.Changed = true;
                    var volumeControl = snapshot.Session.SimpleAudioVolume;
                    if (Math.Abs(volumeControl.Volume - 1f) > 0.0001f) volumeControl.Volume = 1f;
                    if (volumeControl.Mute) volumeControl.Mute = false;
                }
                finally { session?.Dispose(); }
            }
        }
        EnsureReady(stage);
    }

    /// <summary>
    /// Key stages compare the existing control with a newly opened endpoint. During playback,
    /// reuse the control to avoid repeatedly activating COM objects on the capture timeline;
    /// any changed value is checked once more through a fresh handle before stopping.
    /// </summary>
    public void EnsureReady(string stage = "播放前检查", bool freshRead = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var enumerator = freshRead ? new MMDeviceEnumerator() : null;
        var descriptions = new List<string>();
        foreach (var endpoint in _endpoints)
        {
            var reading = ReadEndpoint(endpoint, enumerator);
            if (!freshRead && (!Matches(reading.Current, endpoint.AppliedVolume) || reading.Muted))
            {
                using var verification = new MMDeviceEnumerator();
                reading = ReadEndpoint(endpoint, verification);
            }
            descriptions.Add(reading.Description);
            try { VerifyApplied(endpoint, reading, stage); }
            catch
            {
                RecordDiagnostic(stage, string.Join("；", descriptions));
                throw;
            }
        }
        RecordDiagnostic(stage, string.Join("；", descriptions));
        foreach (var snapshot in _sessions.Values)
        {
            if (snapshot.Session.State == AudioSessionState.AudioSessionStateExpired) continue;
            var volume = snapshot.Session.SimpleAudioVolume;
            var current = volume.Volume;
            if (volume.Mute || !float.IsFinite(current) || current < 0.995f)
                throw new InvalidOperationException($"“{snapshot.DeviceName}”的声屿应用音量或静音在校准期间发生变化，已停止测量；不会因此提高音量。");
        }
    }

    private readonly record struct EndpointReading(float Current, bool Muted, float Retained, bool RetainedMuted, string Description);

    private static EndpointReading ReadEndpoint(EndpointSnapshot endpoint, MMDeviceEnumerator? enumerator)
    {
        EnsureConnected(endpoint);
        var existing = endpoint.Device.AudioEndpointVolume;
        var retained = existing.MasterVolumeLevelScalar;
        var retainedMuted = existing.Mute;
        var current = retained;
        var muted = retainedMuted;
        if (enumerator is not null)
        {
            using var fresh = enumerator.GetDevice(endpoint.Device.ID);
            if (fresh.State != DeviceState.Active) throw new InvalidOperationException($"“{endpoint.Name}”已断开。");
            current = fresh.AudioEndpointVolume.MasterVolumeLevelScalar;
            muted = fresh.AudioEndpointVolume.Mute;
        }
        var description = $"{endpoint.Name} {current * 100:0.0}%{(muted ? "（静音）" : "")}";
        if (enumerator is not null)
            description += $"（重新读取；原控制 {retained * 100:0.0}%{(retainedMuted ? "、静音" : "") }）";
        return new(current, muted, retained, retainedMuted, description);
    }

    private static bool Matches(float actual, float expected, float tolerance = 0.005f) =>
        float.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance;

    private static void VerifyApplied(EndpointSnapshot endpoint, EndpointReading reading, string stage)
    {
        if (reading.Muted || reading.RetainedMuted || !Matches(reading.Current, endpoint.AppliedVolume) ||
            !Matches(reading.Retained, endpoint.AppliedVolume))
            throw new InvalidOperationException($"在“{stage}”发现设备音量或静音变化：{reading.Description}；" +
                $"本轮已确认的临时音量为 {endpoint.AppliedVolume * 100:0.0}%。已停止测试，不会因此提高音量。");
    }

    private void RecordDiagnostic(string stage, string values)
    {
        var entry = stage + "：" + values;
        if (entry == _lastDiagnostic) return;
        _lastDiagnostic = entry;
        if (_diagnostics.Count == 24) _diagnostics.Dequeue();
        _diagnostics.Enqueue($"{System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt).TotalSeconds:0.00}s {entry}");
    }

    private void RecordCurrentVolumes(string stage)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var values = new List<string>();
            foreach (var endpoint in _endpoints)
            {
                try { values.Add(ReadEndpoint(endpoint, enumerator).Description); }
                catch (Exception ex) { values.Add(endpoint.Name + " 无法读取：" + ex.Message); }
            }
            RecordDiagnostic(stage, string.Join("；", values));
        }
        catch (Exception ex) { RecordDiagnostic(stage, "无法读取：" + ex.Message); }
    }

    /// <summary>Read-only failure details, collected before Dispose restores normal volumes.</summary>
    public string DescribeDiagnostics()
    {
        RecordCurrentVolumes("失败时，恢复原音量前");
        return _baselineDescription + "\n本次临时目标音量：" + (_requestedPercent is { } percent ? percent + "%" : "尚未设置") +
            "\n音量阶段记录：\n" + string.Join("\n", _diagnostics);
    }

    private static void EnsureConnected(EndpointSnapshot endpoint)
    {
        if (endpoint.Device.State != DeviceState.Active)
            throw new InvalidOperationException($"“{endpoint.Name}”已断开，请重新连接后测量。");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Probe playback has already stopped. Other applications remain muted until this
        // scope and the engine routing scope have both restored their original states.
        foreach (var snapshot in _sessions.Values.Reverse())
        {
            if (snapshot.Changed)
            {
                RestoreSession(snapshot, () =>
                {
                    var volume = snapshot.Session.SimpleAudioVolume;
                    volume.Volume = snapshot.Volume;
                    var restored = volume.Volume;
                    if (!float.IsFinite(restored) || Math.Abs(restored - snapshot.Volume) > 0.005f)
                        throw new InvalidOperationException("应用音量未恢复到原值。");
                }, "原应用音量");
                RestoreSession(snapshot, () =>
                {
                    var volume = snapshot.Session.SimpleAudioVolume;
                    volume.Mute = snapshot.Muted;
                    if (volume.Mute != snapshot.Muted) throw new InvalidOperationException("应用静音未恢复到原值。");
                }, "原应用静音状态");
            }
            TryRestore(() => snapshot.Session.Dispose(), $"“{snapshot.DeviceName}”的声屿音量控制未能完全释放。");
        }
        _sessions.Clear();
        _sessionBaselines.Clear();
        foreach (var endpoint in _endpoints.AsEnumerable().Reverse())
        {
            if (endpoint.Changed)
            {
                TryRestore(() =>
                {
                    var volume = endpoint.Device.AudioEndpointVolume;
                    volume.MasterVolumeLevelScalar = endpoint.Volume;
                    var restored = volume.MasterVolumeLevelScalar;
                    if (!float.IsFinite(restored) || Math.Abs(restored - endpoint.Volume) > 0.005f)
                        throw new InvalidOperationException("设备音量未恢复到原值。");
                }, $"“{endpoint.Name}”的原设备音量未能恢复，请重新连接后检查。");
                // Restore mute separately even if restoring the scalar failed. Volume writes
                // may unmute an endpoint, so its original mute must always be written last.
                TryRestore(() =>
                {
                    var volume = endpoint.Device.AudioEndpointVolume;
                    volume.Mute = endpoint.Muted;
                    if (volume.Mute != endpoint.Muted) throw new InvalidOperationException("设备静音未恢复到原值。");
                }, $"“{endpoint.Name}”的原设备静音状态未能恢复，请重新连接后检查。");
            }
            TryRestore(() => endpoint.Device.Dispose(), $"“{endpoint.Name}”的设备音量控制未能完全释放。");
        }
        _endpoints.Clear();
    }

    private void RestoreSession(SessionSnapshot snapshot, Action action, string description)
    {
        try { action(); }
        catch
        {
            // A finished probe stream can legitimately expire. A still-existing session or
            // disconnected device whose state cannot be read must surface a warning.
            try { if (snapshot.Session.State == AudioSessionState.AudioSessionStateExpired) return; }
            catch { }
            _restoreWarnings.Add($"“{snapshot.DeviceName}”的声屿{description}未能恢复，请检查 Windows 音量混合器。");
        }
    }

    private void TryRestore(Action action, string warning)
    {
        try { action(); }
        catch { _restoreWarnings.Add(warning); }
    }
}
