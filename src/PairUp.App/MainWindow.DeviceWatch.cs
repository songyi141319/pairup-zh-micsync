using System.Windows.Threading;
using PairUp.App.Services;

namespace PairUp.App;

public partial class MainWindow
{
    private AudioDeviceWatcher? _deviceWatcher;
    private bool _deviceWatchClosed;
    private readonly DispatcherTimer _deviceRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };

    private void StartDeviceWatch()
    {
        _deviceRefreshTimer.Tick += (_, _) =>
        {
            if (_deviceWatchClosed) { _deviceRefreshTimer.Stop(); return; }
            // Keep the measurement's selected endpoints stable until its window closes.
            if (AudioBusy || _acousticCalibrationWindow is not null) return;
            _deviceRefreshTimer.Stop();
            LoadDevices();
            _ = SendGlassStateAsync(true);
        };
        try
        {
            _deviceWatcher = new AudioDeviceWatcher(() =>
            {
                if (Dispatcher.HasShutdownStarted) return;
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_deviceWatchClosed) return;
                    _deviceRefreshTimer.Stop();
                    _deviceRefreshTimer.Start();
                }));
            });
        }
        catch { _viewModel.StatusText = "自动识别设备暂不可用，可点击刷新设备。"; }
    }

    private void StopDeviceWatch()
    {
        _deviceWatchClosed = true;
        _deviceRefreshTimer.Stop();
        try { _deviceWatcher?.Dispose(); }
        catch { /* Closing must still restore and dispose the audio engine. */ }
        _deviceWatcher = null;
    }
}
