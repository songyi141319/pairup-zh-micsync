using PairUp.App.Audio;
using PairUp.App.Services;

namespace PairUp.App;

public partial class MainWindow
{
    private CancellationTokenSource? _devicePreviewCancellation;
    private string? _testingDeviceId;
    private bool _switchingSource;
    private bool _exitAfterDevicePreview;
    private int _auditionVolume = 20;
    private bool AudioBusy => _devicePreviewCancellation is not null || _switchingSource ||
        _acousticCalibrationWindow?.IsMeasuring == true || _audioEngine.IsCalibrating;

    private async Task ChangeMainSourceAsync(AudioDeviceInfo device)
    {
        if (AudioBusy) throw new InvalidOperationException("请先结束正在进行的试听或校准。");
        if (_uiPreview) { await GlassNoticeAsync(new { message = "这是界面预览，不更改 Windows 主音源。" }); return; }
        _calibrator.Stop();
        CalibratePopup.IsOpen = false;
        _switchingSource = true;
        _acousticDeviceSnapshot = _viewModel.Devices.ToDictionary(d => d.Id,
            d => new DeviceSettings(d.Id, d.IsConnected, d.Volume, d.LatencyMs, d.IsFavorite, d.BassBoost, d.Treble, d.IsMono, d.Spatial));
        try
        {
            await SendGlassStateAsync(true);
            SetMainSource(device);
        }
        finally
        {
            _switchingSource = false;
            _acousticDeviceSnapshot = null;
            SyncWindowsVolumes(followSourceChanges: false, resetSourceBaseline: true);
            await SendGlassStateAsync(true);
        }
        var message = "主音源已切换为“" + device.Name + "”。请重新校准延迟；音乐软件若固定了输出设备，请改为跟随系统默认。";
        _viewModel.StatusText = message;
        await GlassNoticeAsync(new { message });
    }

    private async void PreviewDevice_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement { DataContext: AudioDeviceInfo device }) return;
        try
        {
            if (_testingDeviceId == device.Id) { _devicePreviewCancellation?.Cancel(); return; }
            await PreviewDeviceAsync(device);
        }
        catch (Exception ex) { _viewModel.StatusText = "试听未完成：" + ex.Message; }
    }

    private async void SetMainSource_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement { DataContext: AudioDeviceInfo device }) return;
        try { await ChangeMainSourceAsync(device); }
        catch (Exception ex) { _viewModel.StatusText = "音源切换未完成：" + ex.Message; }
    }

    private async Task PreviewDeviceAsync(AudioDeviceInfo device)
    {
        if (AudioBusy) throw new InvalidOperationException("请先结束正在进行的试听或校准。");
        if (_uiPreview)
        {
            await GlassNoticeAsync(new { message = "这是界面预览，不播放真实声音。" });
            return;
        }
        _calibrator.Stop();
        CalibratePopup.IsOpen = false;
        using var cancellation = new CancellationTokenSource();
        _devicePreviewCancellation = cancellation;
        _testingDeviceId = device.Id;
        var preview = new DevicePreview();
        _acousticDeviceSnapshot = _viewModel.Devices.ToDictionary(d => d.Id,
            d => new DeviceSettings(d.Id, d.IsConnected, d.Volume, d.LatencyMs, d.IsFavorite, d.BassBoost, d.Treble, d.IsMono, d.Spatial));
        try
        {
            await SendGlassStateAsync(true);
            // Silence forwarding while a direct source chime is captured, so it cannot ring
            // through every connected speaker. The scope also restores delays and gates.
            using (_audioEngine.BeginAcousticCalibration())
            {
                try { await Task.Run(() => preview.PlayAsync(device.Id, _auditionVolume, cancellation.Token)); }
                finally
                {
                    if (preview.ResumeBlocked && _audioEngine.SuppressCalibrationResume(device.Id) is { } pauseWarning)
                        _viewModel.StatusText = pauseWarning;
                }
            }
            if (!preview.ResumeBlocked)
                await GlassNoticeAsync(new { message = $"“{device.Name}”试听已结束。是否实际出声，请以听到的声音为准。" });
        }
        catch (OperationCanceledException) { }
        finally
        {
            _devicePreviewCancellation = null;
            _testingDeviceId = null;
            _acousticDeviceSnapshot = null;
            SyncWindowsVolumes(followSourceChanges: false, resetSourceBaseline: true);
            await SendGlassStateAsync(true);
            if (_audioEngine.CalibrationRestoreWarning is { } warning)
                await GlassNoticeAsync(new { message = warning, error = true });
            if (preview.RestorationWarning is { } previewWarning)
            {
                _viewModel.StatusText = previewWarning;
                await GlassNoticeAsync(new { message = previewWarning, error = true });
            }
            if (_exitAfterDevicePreview) _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
