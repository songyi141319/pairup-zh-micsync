using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PairUp.App.Audio;
using PairUp.App.Controls;
using PairUp.App.Services;

namespace PairUp.App;

public partial class MainWindow : Window
{
    public const string AppVersion = "0.4.0";

    private readonly AudioDeviceManager _deviceManager = new();
    private readonly MainWindowViewModel _viewModel = new();
    private readonly AudioEngine _audioEngine = new();
    private readonly SettingsStore _settingsStore = new();
    private bool _isRestoringSettings = true;
    private bool _reflectingWindowsVolume;
    private double? _lastSourceVolume;
    private readonly DispatcherTimer _volumeSyncTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private LayeredWaveVisualizer? _visualizer;
    private GuestServer? _guestServer;
    private readonly ClickCalibrator _calibrator = new();
    private AudioDeviceInfo? _calibratingDevice;
    private const int CalibrationTapTarget = 5;
    private TrayIconService? _tray;
    private bool _hasShownTrayBalloon;
    private AcousticCalibrationWindow? _acousticCalibrationWindow;
    private bool _exitAfterAcousticCalibration;
    private Dictionary<string, DeviceSettings>? _acousticDeviceSnapshot;
    private bool _rejectingCalibrationChanges;
    private string? _deviceConnectionWarning;

    private readonly bool _uiPreview;

    public MainWindow(bool uiPreview = false)
    {
        _uiPreview = uiPreview;
        if (!uiPreview) _auditionVolume = CalibrationPreferences.LoadAuditionPercent();
        InitializeComponent();
        DataContext = _viewModel;
        ThemeService.ApplyCurrentSystemTheme();
        if (_uiPreview) LoadPreviewDevices(); else LoadDevices();
        _volumeSyncTimer.Tick += (_, _) => SyncWindowsVolumes();
        SourceInitialized += (_, _) =>
        {
            ApplyRoundedCorners();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        };
        Closing += (_, e) =>
        {
            if (_devicePreviewCancellation is not null)
            {
                e.Cancel = true;
                _exitAfterDevicePreview = true;
                _devicePreviewCancellation.Cancel();
                return;
            }
            if (_acousticCalibrationWindow?.IsMeasuring != true) return;
            // A normal tray/external exit must finish restoring Windows audio before disposal.
            e.Cancel = true;
            _exitAfterAcousticCalibration = true;
            _acousticCalibrationWindow.CancelAndClose();
        };
        Closed += (_, _) =>
        {
            _volumeSyncTimer.Stop();
            _visualizer?.Stop();
            _audioEngine.Dispose();
            _guestServer?.Dispose();
            _calibrator.Dispose();
            _tray?.Dispose();
            DisposeGlass();

            // NAudio's native WASAPI capture/render threads aren't always true .NET background
            // threads, so the process can outlive every window by several seconds even after
            // full disposal — which the update installer sees as the exe still being locked and
            // fails to close it. Force a real process exit once cleanup is done.
            Environment.Exit(0);
        };

        if (!_uiPreview) _tray = new TrayIconService(
            () => _viewModel.Devices,
            device => device.IsConnected = !device.IsConnected,
            RestoreFromTray,
            ExitApp)
        { Visible = true };

        Loaded += (_, _) =>
        {
            _ = InitializeGlassAsync();
            if (_uiPreview) return;
            // Start capture immediately so the visualizer has real audio to react to right away,
            // instead of only once the user connects a device or touches mute.
            try { _audioEngine.EnsureCaptureStarted(); }
            catch (InvalidOperationException ex) { MessageBox.Show(ex.Message, "声屿", MessageBoxButton.OK, MessageBoxImage.Warning); }
            SyncWindowsVolumes(followSourceChanges: false, resetSourceBaseline: true);
            _volumeSyncTimer.Start();
            _visualizer = new LayeredWaveVisualizer(VisualizerCanvas, _audioEngine);
            _visualizer.Start();

            _guestServer = new GuestServer(() => _viewModel.Devices, Dispatcher);
            try { _guestServer.Start(); }
            catch { /* Guest sharing just won't be available; nothing else depends on it. */ }

            _ = CheckForUpdatesOnLaunchAsync();
        };
        _audioEngine.SyncStatusUpdated += AudioEngine_SyncStatusUpdated;
        StateChanged += (_, _) =>
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
            if (!_uiPreview && WindowState == WindowState.Minimized)
            {
                Hide();
                ShowInTaskbar = false;
                if (!_hasShownTrayBalloon)
                {
                    _hasShownTrayBalloon = true;
                    _tray?.ShowBalloon("声屿", "声屿仍在系统托盘中运行。双击图标可重新打开，右键可切换设备。");
                }
            }
        };
    }

    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void ExitApp() => Dispatcher.Invoke(Close);

    private void AudioEngine_SyncStatusUpdated(IReadOnlyList<SyncStatus> statuses)
    {
        Dispatcher.Invoke(() =>
        {
            foreach (var status in statuses)
            {
                var device = _viewModel.Devices.FirstOrDefault(d => d.Id == status.DeviceId);
                if (device is null) continue;

                if (_audioEngine.CaptureError is { } captureError)
                {
                    device.SyncStatusText = "音源连接中断";
                    device.SyncDetailsText = captureError;
                    continue;
                }

                var driverQueue = status.DriverPaddingMs is { } padding ? $"{padding:0} 毫秒" : "未知";
                var driverMaximum = status.DriverLatencyMs is { } maximum ? $"{maximum:0.0} 毫秒" : "未知";
                device.SyncStatusText = $"软件等待 {status.QueueMs:0} 毫秒\n驱动排队 {driverQueue}";
                device.SyncDetailsText = $"软件中尚未播放的声音：{status.QueueMs:0.0} 毫秒（不含你设置的延迟）。\n" +
                    $"正在播放的系统声音流排队：{driverQueue}。\n驱动报告的最大延迟：{driverMaximum}。\n" +
                    $"你设置的延迟：{device.LatencyMs:0.0} 毫秒。\n" +
                    "系统数据不能完整反映蓝牙传输和音箱内部处理时间；实际同步仍需麦克风测量或试听。";
            }
        });
    }

    private void SyncWindowsVolumes(bool followSourceChanges = true, bool resetSourceBaseline = false)
    {
        if (_uiPreview) return;
        if (_isRestoringSettings || AudioBusy) return;
        var sourceId = _audioEngine.SourceDeviceId ?? _viewModel.DefaultDevice?.Id;
        var ids = _viewModel.Devices.Select(d => d.Id).ToList();
        if (sourceId is not null) ids.Add(sourceId);
        var volumes = WindowsDeviceVolume.Read(ids);
        double? observedSource = sourceId is not null && volumes.TryGetValue(sourceId, out var observed)
            ? observed.Percent : null;
        if (sourceId is not null && volumes.TryGetValue(sourceId, out var source) &&
            followSourceChanges && _lastSourceVolume is { } previous && Math.Abs(source.Percent - previous) >= 0.05)
        {
            // Windows flyout/media keys: fan out only once, outside any native callback.
            _audioEngine.SetMasterVolume(source.Percent);
            volumes = WindowsDeviceVolume.Read(ids); // Show actual, possibly quantized device values.
            RefreshStatusText();
        }

        var changed = false;
        _reflectingWindowsVolume = true;
        try
        {
            if (sourceId is not null && volumes.TryGetValue(sourceId, out var current))
            {
                changed |= Math.Abs(_viewModel.MasterVolume - current.Percent) >= 0.05;
                // A device-only readback must not consume a pending Windows master change.
                // Remember the initial observation, not a newer change during the fan-out.
                if (followSourceChanges || resetSourceBaseline || _lastSourceVolume is null)
                    _lastSourceVolume = observedSource;
                _viewModel.MasterVolume = current.Percent;
                _viewModel.IsSourceMuted = current.Muted;
            }
            foreach (var device in _viewModel.Devices)
                if (volumes.TryGetValue(device.Id, out var state) && Math.Abs(device.Volume - state.Percent) >= 0.05)
                {
                    device.Volume = state.Percent;
                    changed = true;
                }
        }
        finally { _reflectingWindowsVolume = false; }
        if (changed) SaveSettings();
    }

    private void MuteSource_Click(object sender, RoutedEventArgs e)
    {
        if (_uiPreview) return;
        var checkbox = (CheckBox)sender;
        if (AudioBusy) { checkbox.IsChecked = _viewModel.IsSourceMuted; return; }
        if (!_audioEngine.TrySetSourceMuted(checkbox.IsChecked == true))
        {
            checkbox.IsChecked = !checkbox.IsChecked; // revert — no source device available yet
            MessageBox.Show(_audioEngine.SourceControlError ?? "无法调整音源静音，请检查连接。",
                "声屿", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private void ApplyRoundedCorners()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    private const int WM_SETTINGCHANGE = 0x001A;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero &&
            Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            ThemeService.ApplyCurrentSystemTheme();
        }

        return IntPtr.Zero;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_uiPreview) return;
        var button = (Button)sender;
        var originalContent = button.Content;
        button.IsEnabled = false;
        button.Content = "正在检查…";

        try
        {
            var result = await UpdateChecker.CheckAsync(AppVersion);

            if (result.UpdateAvailable)
            {
                if (result.InstallerDownloadUrl is null)
                {
                    // No installer asset published on the release — fall back to just opening it.
                    var openRelease = MessageBox.Show(
                        $"声屿 v{result.LatestVersion} 已发布（当前版本 v{AppVersion}）。\n\n" +
                        "该版本没有提供安装包，因此无法自动更新。要打开版本发布页面吗？",
                        "发现更新", MessageBoxButton.YesNo, MessageBoxImage.Information);

                    if (openRelease == MessageBoxResult.Yes)
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = result.ReleaseUrl,
                            UseShellExecute = true
                        });
                    }
                    return;
                }

                var install = MessageBox.Show(
                    $"声屿 v{result.LatestVersion} 已发布（当前版本 v{AppVersion}）。\n\n" +
                    "现在下载并安装吗？声屿随后会退出并打开安装程序。\n\n" +
                    "该安装程序尚未经过数字签名，Windows 可能会显示“已保护你的电脑”提示。要继续，请选择“更多信息”，再选择“仍要运行”。",
                    "发现更新", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (install != MessageBoxResult.Yes) return;

                UpdateProgressBar.Value = 0;
                UpdateProgressBar.Visibility = Visibility.Visible;
                var progress = new Progress<double>(p =>
                {
                    button.Content = $"正在下载 {p:0}%";
                    UpdateProgressBar.Value = p;
                });
                var installerPath = await UpdateChecker.DownloadInstallerAsync(
                    result.InstallerDownloadUrl, result.InstallerAssetName!, AppVersion, progress);

                // Not silent: SmartScreen's "Windows protected your PC" prompt still fires for an
                // unsigned installer regardless of /VERYSILENT, so running silently just hides the
                // one thing the user actually needs to click through — the install would silently
                // stall with nothing visible happening. Letting the installer show its own UI (and
                // that prompt, if it appears) means the user can actually see and complete it; our
                // [Run] entry relaunches PairUp automatically once the wizard finishes.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true
                });

                ExitApp();
            }
            else
            {
                MessageBox.Show($"当前已是最新版本（v{AppVersion}）。", "声屿",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch
        {
            MessageBox.Show("检查更新失败，请检查网络连接后重试。", "声屿",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            button.Content = originalContent;
            button.IsEnabled = true;
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            ResetUpdateButtonStyle();
            RefreshStatusText();
        }
    }

    /// <summary>
    /// Quietly checks for an update shortly after launch, with no "Checking…" UI and no popup
    /// on failure or when already up to date — only surfaces something when there's genuinely a
    /// newer version, via a status line, an accented Check for Updates button, and a tray balloon.
    /// </summary>
    private async Task CheckForUpdatesOnLaunchAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            var result = await UpdateChecker.CheckAsync(AppVersion);
            if (!result.UpdateAvailable) return;

            _viewModel.StatusText = $"发现更新：v{result.LatestVersion}（当前版本 v{AppVersion}）。点击“检查更新”进行安装。";
            CheckUpdateButton.Background = (System.Windows.Media.Brush)FindResource("AccentBrush");
            CheckUpdateButton.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0C, 0x15, 0x12));
            _tray?.ShowBalloon("声屿有可用更新",
                $"v{result.LatestVersion} 已就绪。打开声屿并点击“检查更新”即可安装。");
        }
        catch
        {
            // Silent by design — a background check shouldn't interrupt anyone with network errors.
        }
    }

    private void ResetUpdateButtonStyle()
    {
        CheckUpdateButton.Background = (System.Windows.Media.Brush)FindResource("Surface3Brush");
        CheckUpdateButton.Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
    }

    private void CreditButton_Click(object sender, RoutedEventArgs e) => CreditPopup.IsOpen = !CreditPopup.IsOpen;

    private void CreditLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string url) return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });

        CreditPopup.IsOpen = false;
    }

    private void LicenseLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        CreditPopup.IsOpen = false;
        LicensePopup.IsOpen = true;
    }

    private void LicenseClose_Click(object sender, RoutedEventArgs e) => LicensePopup.IsOpen = false;

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // The window's own X button minimizes to tray rather than exiting (matching the tray-mode
    // feature); every other close path — Application.Shutdown() during self-update, the tray's
    // own Exit item, or an external close request like the update installer's Restart Manager
    // check — should actually close the app, so there's no blanket Closing handler to fight them.
    private void Close_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void LoadDevices()
    {
        if (_uiPreview) return;
        var previousDevices = _viewModel.Devices.ToArray();
        var warnings = new List<string>();
        try
        {
            // Prepare detached rows first. PropertyChanged must never initiate a connection
            // while an incomplete list is being rebuilt or overwrite a saved output choice.
            var snapshot = _deviceManager.GetOutputSnapshot();
            warnings.AddRange(snapshot.Warnings);
            var (savedMasterVolume, savedSettings) = _settingsStore.Load();
            foreach (var previous in previousDevices)
                savedSettings[previous.Id] = new(previous.Id, previous.IsConnected, previous.Volume, previous.LatencyMs,
                    previous.IsFavorite, previous.BassBoost, previous.Treble, previous.IsMono, previous.Spatial);
            var defaultDeviceId = _audioEngine.SourceDeviceId ?? _viewModel.DefaultDevice?.Id;
            if (defaultDeviceId is null)
            {
                defaultDeviceId = _deviceManager.TryGetDefaultDeviceId(out var defaultWarning);
                if (defaultWarning is not null) warnings.Add(defaultWarning);
            }
            var devices = snapshot.Devices.ToList();
            var seen = new HashSet<string>(devices.Select(device => device.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var previous in previousDevices)
                if (seen.Add(previous.Id))
                    devices.Add(new AudioDeviceInfo
                    {
                        Id = previous.Id, Name = previous.Name, Kind = previous.Kind,
                        State = snapshot.IsComplete ? NAudio.CoreAudioApi.DeviceState.NotPresent : previous.State
                    });
            foreach (var saved in savedSettings.Values.Where(saved => saved.IsConnected || saved.IsFavorite))
                if (seen.Add(saved.DeviceId))
                    devices.Add(new AudioDeviceInfo
                    {
                        Id = saved.DeviceId, Name = "未连接的已保存设备", Kind = DeviceKind.Other,
                        State = NAudio.CoreAudioApi.DeviceState.NotPresent
                    });
            if (defaultDeviceId is not null && seen.Add(defaultDeviceId))
                devices.Add(new AudioDeviceInfo
                {
                    Id = defaultDeviceId, Name = "原主音源", Kind = DeviceKind.Other,
                    State = NAudio.CoreAudioApi.DeviceState.NotPresent
                });
            var volumes = WindowsDeviceVolume.Read(devices.Select(device => device.Id));
            foreach (var device in devices)
            {
                device.IsSystemDefault = string.Equals(device.Id, defaultDeviceId, StringComparison.OrdinalIgnoreCase);
                if (savedSettings.TryGetValue(device.Id, out var saved))
                {
                    device.Volume = volumes.TryGetValue(device.Id, out var state) ? state.Percent : saved.Volume;
                    device.LatencyMs = saved.LatencyMs;
                    device.IsFavorite = saved.IsFavorite;
                    device.BassBoost = saved.BassBoost;
                    device.Treble = saved.Treble;
                    device.IsMono = saved.IsMono;
                    device.Spatial = saved.Spatial?.TryValidate(out _) == true ? saved.Spatial : new SpatialProfile();
                    device.IsConnected = saved.IsConnected && !device.IsSystemDefault;
                }
                else if (volumes.TryGetValue(device.Id, out var state)) device.Volume = state.Percent;
                device.IsOutOfRange = device.State != NAudio.CoreAudioApi.DeviceState.Active;
            }

            var defaultDevice = devices.FirstOrDefault(device => device.IsSystemDefault);
            var sourceReady = _audioEngine.RefreshSourceAvailability(defaultDevice?.State == NAudio.CoreAudioApi.DeviceState.Active);
            if (!sourceReady) warnings.Add(_audioEngine.CaptureError ?? "主音源暂时不可用，输出选择已保留。");
            foreach (var device in devices)
            {
                if (device.IsSystemDefault)
                {
                    device.IsOutOfRange = !sourceReady;
                    device.SyncStatusText = sourceReady ? "主音源已连接" : "主音源等待恢复";
                    device.SyncDetailsText = sourceReady ? "声音直接由 Windows 播放。" : _audioEngine.CaptureError ?? "重新连接后刷新，或选择其他主音源。";
                    continue;
                }
                if (!device.IsConnected || device.State != NAudio.CoreAudioApi.DeviceState.Active)
                    _audioEngine.RemoveOutput(device.Id);
                if (_audioEngine.IsOutputResumeSuppressed(device.Id))
                {
                    device.IsOutOfRange = true;
                    device.SyncStatusText = "音量恢复未完成，已暂停";
                    device.SyncDetailsText = "请先确认该设备的 Windows 音量，再手动重新开启输出；刷新不会自动恢复播放。";
                    warnings.Add($"“{device.Name}”仍处于安全暂停，请确认 Windows 音量后重新开启。");
                    continue;
                }
                if (!device.IsConnected) continue;
                if (device.State != NAudio.CoreAudioApi.DeviceState.Active || !sourceReady)
                {
                    device.IsOutOfRange = true;
                    device.SyncStatusText = device.State != NAudio.CoreAudioApi.DeviceState.Active ? "设备未连接，已保留选择" : "等待主音源恢复";
                    device.SyncDetailsText = "原音量、延迟与输出选择已保留；设备重新连接后刷新即可重试。";
                    continue;
                }
                var result = _audioEngine.TryAddOutput(device.Id, device.LatencyMs, device.BassBoost, device.Treble, device.IsMono, device.Spatial);
                if (result != AddOutputResult.Success)
                {
                    device.IsOutOfRange = true;
                    device.SyncStatusText = "暂未恢复，已保留选择";
                    device.SyncDetailsText = result == AddOutputResult.UnsupportedFormat
                        ? "音源和输出需使用单声道或立体声，请检查 Windows 声音格式。"
                        : _audioEngine.CaptureError ?? "设备暂时无法连接，请检查连接后刷新重试。";
                    warnings.Add($"“{device.Name}”：{device.SyncDetailsText}");
                }
            }

            // All endpoint reads and connection attempts are finished before replacing rows.
            _isRestoringSettings = true;
            foreach (var previous in previousDevices) previous.PropertyChanged -= Device_PropertyChanged;
            _viewModel.Devices.Clear();
            foreach (var device in devices) _viewModel.Devices.Add(device);
            foreach (var device in devices) device.PropertyChanged += Device_PropertyChanged;
            if (defaultDeviceId is not null && volumes.TryGetValue(defaultDeviceId, out var source))
            {
                _viewModel.MasterVolume = source.Percent;
                _viewModel.IsSourceMuted = source.Muted;
                _lastSourceVolume = source.Percent;
            }
            else if (previousDevices.Length == 0) _viewModel.MasterVolume = savedMasterVolume;
            _viewModel.DefaultDevice = defaultDevice;
            _viewModel.SourceName = defaultDevice is null ? "主音源未连接" : defaultDevice.Name + (sourceReady ? "" : "（等待恢复）");
            _deviceConnectionWarning = warnings.Count == 0 ? null : string.Join("；", warnings.Distinct());
            var playing = devices.Count(device => device.IsConnected && !device.IsOutOfRange);
            var waiting = devices.Count(device => device.IsConnected && device.IsOutOfRange);
            _viewModel.ConnectedSummary = waiting == 0 ? $"{playing} / {devices.Count} 个设备正在同步播放" : $"{playing} 个设备正在同步播放，{waiting} 个已选择的设备等待恢复";
            ResortDevices();
            RebuildFavorites();
        }
        catch (Exception ex)
        {
            _deviceConnectionWarning = "刷新设备未完成，已保留原设备列表和输出选择：" + ex.Message;
        }
        finally
        {
            _isRestoringSettings = false;
        }
        RefreshStatusText();
    }

    private void RefreshStatusText() =>
        _viewModel.StatusText = _deviceConnectionWarning ?? _audioEngine.VolumeControlError ??
            $"声屿 v{AppVersion} · Windows 音量联动 · 延迟 0–3000 毫秒 · {_viewModel.Devices.Count} 个输出设备";

    private void SaveSettings()
    {
        if (_uiPreview || _devicePreviewCancellation is not null ||
            (_acousticCalibrationWindow?.IsMeasuring == true && (!_isRestoringSettings || _audioEngine.IsCalibrating))) return;
        var settings = _viewModel.Devices.Select(d =>
            new DeviceSettings(d.Id, d.IsConnected, d.Volume, d.LatencyMs, d.IsFavorite,
                d.BassBoost, d.Treble, d.IsMono, d.Spatial));
        _settingsStore.Save(_viewModel.MasterVolume, settings);
    }

    private void MasterVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_uiPreview || _isRestoringSettings || _reflectingWindowsVolume) return;
        if (AudioBusy)
        {
            _reflectingWindowsVolume = true;
            try { _viewModel.MasterVolume = e.OldValue; }
            finally { _reflectingWindowsVolume = false; }
            return;
        }

        if (!_audioEngine.SetMasterVolume(e.NewValue))
        {
            _reflectingWindowsVolume = true;
            try { _viewModel.MasterVolume = e.OldValue; }
            finally { _reflectingWindowsVolume = false; }
        }
        else _lastSourceVolume = e.NewValue; // This request has already been broadcast.
        SyncWindowsVolumes(followSourceChanges: false);
        RefreshStatusText();
        SaveSettings();
    }

    /// <summary>Connected devices float to the top of the main list; ties break alphabetically.</summary>
    private void ResortDevices()
    {
        var sorted = _viewModel.Devices.OrderByDescending(d => d.IsConnected).ThenBy(d => d.Name).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var currentIndex = _viewModel.Devices.IndexOf(sorted[i]);
            if (currentIndex != i)
                _viewModel.Devices.Move(currentIndex, i);
        }
    }

    private void RebuildFavorites()
    {
        _viewModel.FavoriteDevices.Clear();
        foreach (var device in _viewModel.Devices.Where(d => d.IsFavorite).OrderBy(d => d.Name))
            _viewModel.FavoriteDevices.Add(device);

        _viewModel.HasFavorites = _viewModel.FavoriteDevices.Count > 0;
    }

    private void Device_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_uiPreview) return;
        if (sender is not AudioDeviceInfo device) return;
        if (_reflectingWindowsVolume) return;
        if (_rejectingCalibrationChanges) return;
        if (!_isRestoringSettings && AudioBusy && _acousticDeviceSnapshot?.TryGetValue(device.Id, out var original) == true)
        {
            // Tray and sharing controls must not alter the measured routing or effects mid-run.
            _rejectingCalibrationChanges = true;
            try
            {
                switch (e.PropertyName)
                {
                    case nameof(AudioDeviceInfo.IsConnected): device.IsConnected = original.IsConnected; return;
                    case nameof(AudioDeviceInfo.Volume): device.Volume = original.Volume; return;
                    case nameof(AudioDeviceInfo.LatencyMs): device.LatencyMs = original.LatencyMs; return;
                    case nameof(AudioDeviceInfo.BassBoost): device.BassBoost = original.BassBoost; return;
                    case nameof(AudioDeviceInfo.Treble): device.Treble = original.Treble; return;
                    case nameof(AudioDeviceInfo.IsMono): device.IsMono = original.IsMono; return;
                    case nameof(AudioDeviceInfo.Spatial): device.Spatial = original.Spatial ?? new(); return;
                    case nameof(AudioDeviceInfo.IsFavorite): device.IsFavorite = original.IsFavorite; return;
                }
            }
            finally { _rejectingCalibrationChanges = false; }
        }

        switch (e.PropertyName)
        {
            case nameof(AudioDeviceInfo.IsConnected):
                if (device.IsConnected)
                {
                    var result = _audioEngine.TryAddOutput(
                        device.Id, device.LatencyMs,
                        device.BassBoost, device.Treble, device.IsMono, device.Spatial,
                        explicitReconnect: !_isRestoringSettings);

                    if (result == AddOutputResult.WouldEcho)
                    {
                        device.IsConnected = false; // triggers the else-branch below, clearing IsOutOfRange
                        MessageBox.Show(
                            $"无法添加“{device.Name}”：它目前是系统默认播放设备；把声音再转发到同一设备会造成回声。请先更换系统默认输出设备，再重试。",
                            "声屿", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else if (result == AddOutputResult.DeviceUnavailable)
                    {
                        // Set IsConnected=false first (re-enters this handler and clears IsOutOfRange
                        // via the else-branch), then set IsOutOfRange=true after so it isn't wiped out.
                        device.IsConnected = false;
                        device.IsOutOfRange = true;
                        MessageBox.Show(
                            $"暂时无法连接“{device.Name}”。请检查它是否超出蓝牙范围或已关闭；重新连接前，它会显示为“超出范围”。",
                            "声屿", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else if (result == AddOutputResult.UnsupportedFormat)
                    {
                        device.IsConnected = false;
                        _deviceConnectionWarning = $"“{device.Name}”暂未开启：音源和输出都需要设为单声道或立体声，目前不支持 6/8 声道。请调整 Windows 声音格式后重新打开程序。";
                        if (!_isRestoringSettings) MessageBox.Show(_deviceConnectionWarning, "声屿", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else if (result == AddOutputResult.CalibrationInProgress)
                        device.IsConnected = false;
                    else if (result == AddOutputResult.SafetyPaused)
                    {
                        device.IsOutOfRange = true;
                        device.SyncStatusText = "音量恢复未完成，已暂停";
                        device.SyncDetailsText = "请确认 Windows 音量后手动重新开启输出；刷新不会解除安全暂停。";
                        _deviceConnectionWarning = $"“{device.Name}”仍处于安全暂停。";
                    }
                    else if (result == AddOutputResult.SourceUnavailable)
                    {
                        device.IsConnected = false;
                        MessageBox.Show(_audioEngine.CaptureError ?? "音源不可用，请检查原音源连接。",
                            "声屿", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else if (result == AddOutputResult.Success && !_isRestoringSettings)
                    {
                        _deviceConnectionWarning = null;
                        // A newly enabled output joins the current master level. Restoring an
                        // existing setup at startup only reads Windows volumes, never raises them.
                        _audioEngine.SetVolume(device.Id, _viewModel.MasterVolume);
                        SyncWindowsVolumes(followSourceChanges: false);
                        RefreshStatusText();
                    }
                }
                else
                {
                    _audioEngine.RemoveOutput(device.Id);
                    device.IsOutOfRange = false;
                }
                UpdateConnectedSummary();
                ResortDevices();
                break;

            case nameof(AudioDeviceInfo.Volume):
                if (_isRestoringSettings) break;
                _audioEngine.SetVolume(device.Id, device.Volume);
                SyncWindowsVolumes(followSourceChanges: false);
                RefreshStatusText();
                break;

            case nameof(AudioDeviceInfo.LatencyMs):
                _audioEngine.SetDelay(device.Id, device.LatencyMs);
                break;

            case nameof(AudioDeviceInfo.IsFavorite):
                RebuildFavorites();
                break;

            case nameof(AudioDeviceInfo.BassBoost):
                _audioEngine.SetBassBoost(device.Id, device.BassBoost);
                break;

            case nameof(AudioDeviceInfo.Treble):
                _audioEngine.SetTreble(device.Id, device.Treble);
                break;

            case nameof(AudioDeviceInfo.IsMono):
                _audioEngine.SetMono(device.Id, device.IsMono);
                break;

            case nameof(AudioDeviceInfo.Spatial):
                if (!device.IsSystemDefault) _audioEngine.SetSpatial(device.Id, device.Spatial);
                break;

            case nameof(AudioDeviceInfo.IsProcessingExpanded):
            case nameof(AudioDeviceInfo.SyncStatusText):
            case nameof(AudioDeviceInfo.SyncDetailsText):
                return; // UI-only, nothing to forward to the engine or save
        }

        if (!_isRestoringSettings)
            SaveSettings();
    }

    private void UpdateConnectedSummary()
    {
        var connected = _viewModel.Devices.Count(d => d.IsConnected);
        _viewModel.ConnectedSummary = $"{connected} / {_viewModel.Devices.Count} 个设备正在同步播放";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_uiPreview || AudioBusy) return;
        var button = (Button)sender;
        var originalContent = button.Content;
        button.IsEnabled = false;
        button.Content = "正在刷新…";

        // LoadDevices runs synchronously on the UI thread (WASAPI enumeration needs the STA
        // thread), so without yielding here the "Refreshing…" label would never actually paint —
        // the whole operation would complete within a single frame.
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            // A queued audition/calibration may have started while the UI painted.
            if (!AudioBusy) LoadDevices();
        }
        catch (Exception ex) { _viewModel.StatusText = "刷新设备未完成：" + ex.Message; }
        finally
        {
            button.Content = originalContent;
            button.IsEnabled = true;
        }
    }

    private void DelayInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || sender is not TextBox textBox) return;
        var binding = textBox.GetBindingExpression(TextBox.TextProperty);
        if (binding?.IsDirty == true) binding.UpdateSource();
        binding?.UpdateTarget();
        e.Handled = true;
    }

    private void DelayStep_Click(object sender, RoutedEventArgs e)
    {
        if (_acousticCalibrationWindow?.IsMeasuring == true) return;
        if (sender is not FrameworkElement { DataContext: AudioDeviceInfo device } element || !device.IsConnected) return;
        if (double.TryParse(element.Tag?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var step))
            device.AdjustDelay(step);
    }

    private void MicrophoneCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (_uiPreview || AudioBusy) return;
        _calibrator.Stop();
        CalibratePopup.IsOpen = false;
        var window = new AcousticCalibrationWindow(_audioEngine, _viewModel.Devices, ApplyAcousticDelays) { Owner = this };
        window.MeasurementStarting += () => _acousticDeviceSnapshot = _viewModel.Devices.ToDictionary(d => d.Id,
            d => new DeviceSettings(d.Id, d.IsConnected, d.Volume, d.LatencyMs, d.IsFavorite, d.BassBoost, d.Treble, d.IsMono, d.Spatial));
        _acousticCalibrationWindow = window;
        try { window.ShowDialog(); }
        finally
        {
            _acousticCalibrationWindow = null;
            _acousticDeviceSnapshot = null;
            if (_exitAfterAcousticCalibration) Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void ApplyAcousticDelays(Dictionary<string, double> delays)
    {
        var selected = delays.Select(p => (Device: _viewModel.Devices.SingleOrDefault(d => d.Id == p.Key), Delay: p.Value)).ToList();
        if (selected.Any(p => p.Device is null || !p.Device.IsConnected)) throw new InvalidOperationException("待校准设备已断开，请重测");
        var oldDelays = selected.ToDictionary(p => p.Device!.Id, p => p.Device!.LatencyMs);
        _isRestoringSettings = true;
        try
        {
            foreach (var (device, delay) in selected) device!.LatencyMs = delay;
            SaveSettings();
        }
        catch
        {
            foreach (var (device, _) in selected) device!.LatencyMs = oldDelays[device.Id];
            throw;
        }
        finally { _isRestoringSettings = false; }
    }

    private void FavoriteToggle_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is AudioDeviceInfo device)
            device.IsFavorite = !device.IsFavorite;
    }

    private void EqToggle_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is AudioDeviceInfo device)
            device.IsProcessingExpanded = !device.IsProcessingExpanded;
    }

    private void CalibrateToggle_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.DataContext is not AudioDeviceInfo device) return;

        _calibrator.Stop();
        _calibratingDevice = device;

        CalibratePopup.PlacementTarget = button;
        CalibrateDeviceName.Text = device.Name;
        CalibrateNotEnoughText.Visibility = Visibility.Collapsed;
        CalibrateIdlePanel.Visibility = Visibility.Visible;
        CalibrateRunningPanel.Visibility = Visibility.Collapsed;
        CalibrateDonePanel.Visibility = Visibility.Collapsed;

        CalibratePopup.IsOpen = true;
    }

    private void CalibrateStart_Click(object sender, RoutedEventArgs e)
    {
        if (_uiPreview || AudioBusy) return;
        if (_calibratingDevice is null) return;

        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var device = enumerator.GetDevice(_calibratingDevice.Id);
            _calibrator.Start(device);
        }
        catch
        {
            CalibratePopup.IsOpen = false;
            return;
        }

        CalibrateTapCountText.Text = $"0 / {CalibrationTapTarget} 次";
        CalibrateNotEnoughText.Visibility = Visibility.Collapsed;
        CalibrateIdlePanel.Visibility = Visibility.Collapsed;
        CalibrateDonePanel.Visibility = Visibility.Collapsed;
        CalibrateRunningPanel.Visibility = Visibility.Visible;
    }

    private void CalibrateTap_Click(object sender, RoutedEventArgs e)
    {
        _calibrator.RecordTap();
        CalibrateTapCountText.Text = $"{_calibrator.TapCount} / {CalibrationTapTarget} 次";

        if (_calibrator.TapCount < CalibrationTapTarget) return;

        var estimate = _calibrator.GetEstimatedDelayMs();
        _calibrator.Stop();
        CalibrateRunningPanel.Visibility = Visibility.Collapsed;

        if (estimate is double ms)
        {
            CalibrateResultText.Text = $"{ms:0} 毫秒";
            CalibrateDonePanel.Visibility = Visibility.Visible;
        }
        else
        {
            CalibrateNotEnoughText.Visibility = Visibility.Visible;
            CalibrateIdlePanel.Visibility = Visibility.Visible;
        }
    }

    private void CalibrateCancel_Click(object sender, RoutedEventArgs e)
    {
        _calibrator.Stop();
        CalibratePopup.IsOpen = false;
    }

    private void CalibrateApply_Click(object sender, RoutedEventArgs e)
    {
        if (_calibratingDevice is not null)
        {
            var estimate = _calibrator.GetEstimatedDelayMs();
            if (estimate is double ms)
                _calibratingDevice.LatencyMs = ms;
        }

        _calibrator.Stop();
        CalibratePopup.IsOpen = false;
    }

    private void CalibratePopup_Closed(object sender, EventArgs e)
    {
        _calibrator.Stop();
        _calibratingDevice = null;
    }

    private void ShareToggle_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.DataContext is not AudioDeviceInfo device) return;

        GuestSharePopup.PlacementTarget = button;
        GuestShareDeviceName.Text = device.Name;

        if (_guestServer is not { IsRunning: true } server)
        {
            GuestShareUnavailableText.Visibility = Visibility.Visible;
            GuestShareQrImage.Source = null;
            GuestSharePinText.Text = "------";
        }
        else
        {
            GuestShareUnavailableText.Visibility = Visibility.Collapsed;
            var link = server.GetLinkForDevice(device.Id);
            GuestShareQrImage.Source = PngBytesToImage(server.GetQrPng(link));
            GuestSharePinText.Text = server.Pin;
        }

        GuestSharePopup.IsOpen = true;
    }

    private static System.Windows.Media.Imaging.BitmapImage PngBytesToImage(byte[] png)
    {
        var image = new System.Windows.Media.Imaging.BitmapImage();
        using var stream = new System.IO.MemoryStream(png);
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> FavoriteDevices { get; } = new();

    private bool _hasFavorites;
    public bool HasFavorites
    {
        get => _hasFavorites;
        set { _hasFavorites = value; OnPropertyChanged(); }
    }

    private string _statusText = "声屿";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private string _connectedSummary = "0 / 0 个设备正在同步播放";
    public string ConnectedSummary
    {
        get => _connectedSummary;
        set { _connectedSummary = value; OnPropertyChanged(); }
    }

    private string _sourceName = "系统播放音源";
    public string SourceName
    {
        get => _sourceName;
        set { _sourceName = value; OnPropertyChanged(); }
    }

    private double _masterVolume = 100;
    public double MasterVolume
    {
        get => _masterVolume;
        set { _masterVolume = value; OnPropertyChanged(); }
    }

    private bool _isSourceMuted;
    public bool IsSourceMuted
    {
        get => _isSourceMuted;
        set { _isSourceMuted = value; OnPropertyChanged(); }
    }

    private AudioDeviceInfo? _defaultDevice;
    public AudioDeviceInfo? DefaultDevice
    {
        get => _defaultDevice;
        set { _defaultDevice = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDefaultDevice)); }
    }

    public bool HasDefaultDevice => DefaultDevice is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
