using PairUp.App.Audio;
using PairUp.App.Services;

namespace PairUp.App;

public partial class MainWindow
{
    /// <summary>
    /// Called on the dispatcher after the host has excluded calibration, audition and other
    /// source changes. The engine gates forwarding before any Windows playback role changes.
    /// </summary>
    private void SetMainSource(AudioDeviceInfo device)
    {
        var selected = _viewModel.Devices.SingleOrDefault(item => string.Equals(item.Id, device.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("设备列表已改变，请刷新后再选择主音源。");
        var previousId = _audioEngine.SourceDeviceId ?? _viewModel.DefaultDevice?.Id;
        if (string.Equals(previousId, selected.Id, StringComparison.OrdinalIgnoreCase)) return;
        var previousDevice = _viewModel.Devices.FirstOrDefault(item => string.Equals(item.Id, previousId, StringComparison.OrdinalIgnoreCase));
        if (previousDevice is null) throw new InvalidOperationException("原主音源不在设备列表中，请刷新设备后再切换。");

        var snapshots = _viewModel.Devices.Select(item => new
        {
            Device = item,
            Settings = new DeviceSettings(item.Id, item.IsConnected, item.Volume, item.LatencyMs, item.IsFavorite,
                item.BassBoost, item.Treble, item.IsMono, item.Spatial),
            item.IsSystemDefault, item.IsOutOfRange, item.SyncStatusText, item.SyncDetailsText
        }).ToArray();
        var previousMaster = _viewModel.MasterVolume;
        var previousMuted = _viewModel.IsSourceMuted;
        var previousName = _viewModel.SourceName;
        var previousDefault = _viewModel.DefaultDevice;
        var previousBaseline = _lastSourceVolume;
        var previousWarning = _deviceConnectionWarning;
        var settingsTouched = false;
        var sourceVolume = _uiPreview ? new DeviceVolumeState(selected.Volume, false)
            : WindowsDeviceVolume.Read(new[] { selected.Id }).GetValueOrDefault(selected.Id)
                ?? throw new InvalidOperationException("暂时无法读取所选主音源的 Windows 音量，请确认设备已连接。");

        void Reflect(Action action)
        {
            var wasReflecting = _reflectingWindowsVolume;
            _reflectingWindowsVolume = true;
            try { action(); }
            finally { _reflectingWindowsVolume = wasReflecting; }
        }
        void Persist()
        {
            // This is an intentional source transaction while the ordinary polling/save path
            // is paused by the host. No temporary calibration volume enters this snapshot.
            if (!_uiPreview) _settingsStore.Save(_viewModel.MasterVolume, _viewModel.Devices.Select(item =>
                new DeviceSettings(item.Id, item.IsConnected, item.Volume, item.LatencyMs, item.IsFavorite,
                    item.BassBoost, item.Treble, item.IsMono, item.Spatial)));
        }
        void ApplySettings() => Reflect(() =>
        {
            settingsTouched = true;
            foreach (var item in _viewModel.Devices)
            {
                item.IsSystemDefault = string.Equals(item.Id, selected.Id, StringComparison.OrdinalIgnoreCase);
                if (item.IsSystemDefault) item.IsConnected = false;
                else if (string.Equals(item.Id, previousId, StringComparison.OrdinalIgnoreCase)) item.IsConnected = true;
                if (item.IsSystemDefault || string.Equals(item.Id, previousId, StringComparison.OrdinalIgnoreCase)) item.IsOutOfRange = false;
            }
            _viewModel.DefaultDevice = selected;
            _viewModel.SourceName = selected.Name;
            _viewModel.MasterVolume = sourceVolume.Percent;
            _viewModel.IsSourceMuted = _uiPreview ? sourceVolume.Muted : _audioEngine.IsSourceMuted;
            _lastSourceVolume = sourceVolume.Percent;
            _deviceConnectionWarning = $"主音源已切换为“{selected.Name}”。已保留原延迟数值；参考音源已改变，请重新校准或试听调整。";
            UpdateConnectedSummary();
            ResortDevices();
            RebuildFavorites();
            RefreshStatusText();
            Persist();
        });
        void RestoreSettings()
        {
            if (!settingsTouched) return;
            Reflect(() =>
            {
                foreach (var snapshot in snapshots)
                {
                    var item = snapshot.Device;
                    item.IsSystemDefault = snapshot.IsSystemDefault;
                    item.IsConnected = snapshot.Settings.IsConnected;
                    item.Volume = snapshot.Settings.Volume;
                    item.LatencyMs = snapshot.Settings.LatencyMs;
                    item.IsFavorite = snapshot.Settings.IsFavorite;
                    item.BassBoost = snapshot.Settings.BassBoost;
                    item.Treble = snapshot.Settings.Treble;
                    item.IsMono = snapshot.Settings.IsMono;
                    item.Spatial = snapshot.Settings.Spatial ?? new();
                    item.IsOutOfRange = snapshot.IsOutOfRange;
                    item.SyncStatusText = snapshot.SyncStatusText;
                    item.SyncDetailsText = snapshot.SyncDetailsText;
                }
                _viewModel.DefaultDevice = previousDefault;
                _viewModel.SourceName = previousName;
                _viewModel.MasterVolume = previousMaster;
                _viewModel.IsSourceMuted = previousMuted;
                _lastSourceVolume = previousBaseline;
                _deviceConnectionWarning = previousWarning;
                UpdateConnectedSummary();
                ResortDevices();
                RebuildFavorites();
                RefreshStatusText();
                Persist();
            });
        }

        if (_uiPreview) { ApplySettings(); return; }
        var defaults = WindowsDefaultAudioDevice.Capture();
        var outputs = snapshots.Where(snapshot => !string.Equals(snapshot.Device.Id, selected.Id, StringComparison.OrdinalIgnoreCase) &&
            (snapshot.Settings.IsConnected || string.Equals(snapshot.Device.Id, previousId, StringComparison.OrdinalIgnoreCase)))
            .Select(snapshot => snapshot.Settings with { IsConnected = true }).ToArray();
        _audioEngine.ChangeSource(selected.Id, outputs, () => WindowsDefaultAudioDevice.Set(selected.Id),
            () => WindowsDefaultAudioDevice.Restore(defaults), ApplySettings, RestoreSettings);
    }
}
