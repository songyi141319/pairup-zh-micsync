using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NAudio.CoreAudioApi;
using PairUp.App.Audio;
using PairUp.App.Services;

namespace PairUp.App;

public partial class MainWindow
{
    private const string GlassOrigin = "https://shengyu.local";
    private WebView2? _glass;
    private UIElement? _legacyContent;
    private Grid? _glassShell;
    private Button? _returnToGlass;
    private bool _glassReady;
    private bool _glassDisposed;
    private bool _glassUnavailable;
    private bool _primaryInterfaceReady;
    private string? _lastGlassState;
    private readonly DispatcherTimer _glassStateTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _glassStartupTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DeviceNotesStore _deviceNotesStore = new();
    private Dictionary<string, string> _deviceNotes = new(StringComparer.Ordinal);
    private string? _deviceNotesLoadWarning;

    public event EventHandler? PrimaryInterfaceReady;

    private void CompleteInterfaceStartup()
    {
        if (_primaryInterfaceReady || _glassDisposed) return;
        _primaryInterfaceReady = true;
        _glassStartupTimer.Stop();
        PrimaryInterfaceReady?.Invoke(this, EventArgs.Empty);
    }

    private void LoadPreviewDevices()
    {
        var names = new[] { "主音源 · 原声", "前左 · 书架音箱", "前右 · 书架音箱", "左侧 · 蓝牙音箱", "右侧 · 蓝牙音箱", "低音辅助 · 有源音箱" };
        for (var i = 0; i < names.Length; i++)
            _viewModel.Devices.Add(new AudioDeviceInfo
            {
                Id = "demo-" + i, Name = names[i], Kind = i is 3 or 4 ? DeviceKind.Bluetooth : DeviceKind.Wired,
                State = DeviceState.Active, IsSystemDefault = i == 0, IsConnected = i != 0,
                Volume = 30, LatencyMs = i == 0 ? 0 : 187.5, IsFavorite = i is 1 or 2,
                SyncStatusText = "演示数据 · 未连接真实设备", SyncDetailsText = "界面预览不会播放或录制声音。"
            });
        _viewModel.SourceName = names[0];
        _viewModel.DefaultDevice = _viewModel.Devices[0];
        _viewModel.MasterVolume = 30;
        _viewModel.StatusText = "演示预览 · 不操作真实音频设备";
        _isRestoringSettings = false;
        UpdateConnectedSummary();
    }

    private async Task InitializeGlassAsync()
    {
        try
        {
            _legacyContent = (UIElement)Content;
            _legacyContent.Visibility = Visibility.Collapsed;
            _glassStartupTimer.Tick += (_, _) => GlassFailed("界面加载时间较长，请稍后重新打开程序");
            _glassStartupTimer.Start();
            if (!_uiPreview)
            {
                try { _deviceNotes = _deviceNotesStore.Load(); }
                catch { _deviceNotesLoadWarning = "设备备注暂时无法读取，原备注文件已保留。保存备注前请检查文件是否可访问。"; }
            }
            var webRoot = Path.Combine(AppContext.BaseDirectory, "Web");
            if (!File.Exists(Path.Combine(webRoot, "index.html"))) throw new IOException("缺少随程序提供的 Web 界面文件夹");
            Content = null;
            var root = new Grid();
            root.Children.Add(_legacyContent);
            _glassShell = new Grid { Visibility = Visibility.Hidden };
            _glassShell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            _glassShell.RowDefinitions.Add(new RowDefinition());
            var caption = new Grid { Background = new SolidColorBrush(Color.FromRgb(239, 243, 247)) };
            caption.Children.Add(new TextBlock { Text = "声屿 · 多音箱音乐空间", Foreground = Brushes.Black, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
            var captionButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            void CaptionButton(string label, string help, Action action)
            {
                var button = new Button { Content = label, ToolTip = help, Width = 44, Height = 32, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Brushes.Black };
                WindowChrome.SetIsHitTestVisibleInChrome(button, true);
                button.Click += (_, _) => action();
                captionButtons.Children.Add(button);
            }
            CaptionButton("－", "最小化到托盘", () => WindowState = WindowState.Minimized);
            CaptionButton("□", "最大化或还原", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
            CaptionButton("×", "收起到托盘", () => { if (_uiPreview) Close(); else WindowState = WindowState.Minimized; });
            caption.Children.Add(captionButtons);
            _glassShell.Children.Add(caption);
            _glass = new WebView2();
            Grid.SetRow(_glass, 1);
            _glassShell.Children.Add(_glass);
            root.Children.Add(_glassShell);
            _returnToGlass = new Button { Content = "返回玻璃界面", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 45, 18, 0), Padding = new Thickness(12, 7, 12, 7), Visibility = Visibility.Collapsed };
            _returnToGlass.Click += (_, _) => ShowGlass(true);
            root.Children.Add(_returnToGlass);
            Content = root;
            var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PairUp", _uiPreview ? "WebViewPreview" : "WebView");
            var environment = await CoreWebView2Environment.CreateAsync(null, dataDirectory);
            if (_glassDisposed || _glassUnavailable) return;
            await _glass.EnsureCoreWebView2Async(environment);
            if (_glassDisposed || _glassUnavailable) return;
            var core = _glass.CoreWebView2;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = _uiPreview;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.SetVirtualHostNameToFolderMapping("shengyu.local", webRoot, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, args) => { if (!IsGlassDocument(args.Uri)) args.Cancel = true; };
            core.FrameNavigationStarting += (_, args) => args.Cancel = true;
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (args.IsUserInitiated && IsGuideLink(args.Uri))
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args.Uri) { UseShellExecute = true }); }
                    catch { _viewModel.StatusText = "无法打开外部参考链接，请稍后重试。"; }
            };
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.ProcessFailed += (_, _) => Dispatcher.BeginInvoke(new Action(() => GlassFailed("玻璃显示进程已停止")));
            core.WebMessageReceived += GlassMessageReceived;
            core.NavigationCompleted += async (_, args) =>
            {
                if (_glassDisposed || _glassUnavailable) return;
                if (!args.IsSuccess) { GlassFailed("页面未能加载：" + args.WebErrorStatus); return; }
                _glassReady = true;
                ShowGlass(true);
                await SendGlassStateAsync(true);
                if (!_glassReady) return;
                _glassStateTimer.Start();
                if (_uiPreview) await RunPreviewChecksAsync();
            };
            _glassStateTimer.Tick += async (_, _) => await SendGlassStateAsync();
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible && _glassReady && _glassShell?.Visibility == Visibility.Visible) _glassStateTimer.Start();
                else _glassStateTimer.Stop();
            };
            core.Navigate(GlassOrigin + "/index.html" + (_uiPreview ? "?demo=1" : ""));
        }
        catch (Exception ex)
        {
            GlassFailed(ex.Message);
        }
    }

    private void GlassFailed(string reason)
    {
        if (_glassDisposed) return;
        _glassUnavailable = true;
        _glassReady = false;
        _glassStateTimer.Stop();
        _viewModel.StatusText = "玻璃界面暂不可用，已保留原生界面：" + reason;
        _glassShell?.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
        _legacyContent?.SetCurrentValue(VisibilityProperty, Visibility.Visible);
        _returnToGlass?.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
        if (!_uiPreview) _visualizer?.Start();
        CompleteInterfaceStartup();
    }

    private static bool IsGuideLink(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && uri.Host is "www.dolby.com" or "www.genelec.com" or "github.com";

    private static bool IsGlassDocument(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "shengyu.local" && uri.IsDefaultPort && uri.AbsolutePath == "/index.html";

    private void ShowGlass(bool show)
    {
        if (_glassShell is null || _legacyContent is null) return;
        _glassShell.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _legacyContent.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        _returnToGlass!.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show) { _visualizer?.Stop(); _glassStateTimer.Start(); }
        else { _glassStateTimer.Stop(); if (!_uiPreview) _visualizer?.Start(); }
    }

    private async Task SendGlassStateAsync(bool force = false)
    {
        if (!_glassReady || _glassDisposed || _glass?.CoreWebView2 is null || (!force && (!IsVisible || _glassShell?.Visibility != Visibility.Visible))) return;
        var state = JsonSerializer.Serialize(new
        {
            demo = _uiPreview, sourceName = _viewModel.SourceName,
            sourceId = _viewModel.DefaultDevice?.Id, masterVolume = _viewModel.MasterVolume,
            isSourceMuted = _viewModel.IsSourceMuted, status = _viewModel.StatusText,
            isCalibrating = _acousticCalibrationWindow?.IsMeasuring == true,
            isTesting = _devicePreviewCancellation is not null, testingDeviceId = _testingDeviceId,
            audioBusy = AudioBusy,
            auditionVolume = _auditionVolume,
            devices = _viewModel.Devices.Select(d => new
            {
                id = d.Id, name = d.Name, kind = d.KindDisplayName, isConnected = d.IsConnected,
                isAvailable = d.State == DeviceState.Active, note = _deviceNotes.GetValueOrDefault(d.Id, string.Empty),
                isSystemDefault = d.IsSystemDefault, volume = d.Volume, latencyMs = d.LatencyMs,
                bassBoost = d.BassBoost, treble = d.Treble, isMono = d.IsMono, isFavorite = d.IsFavorite,
                syncStatusText = d.SyncStatusText, syncDetailsText = d.SyncDetailsText,
                spatial = new { role = d.Spatial.Role.ToString(), lowCutHz = d.Spatial.LowCutHz, highCutHz = d.Spatial.HighCutHz, trimDb = d.Spatial.TrimDb }
            })
        });
        if (!force && state == _lastGlassState) return;
        _lastGlassState = state;
        try { await _glass.CoreWebView2.ExecuteScriptAsync("window.shengyuReceive && window.shengyuReceive(" + state + ");"); }
        catch (Exception ex) { GlassFailed(ex.Message); }
    }

    private async void GlassMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsGlassDocument(args.Source) || _glassDisposed) return;
        try
        {
            var json = args.WebMessageAsJson;
            if (json.Length > 32768) throw new InvalidOperationException("操作数据过长");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var data = document.RootElement;
            var action = data.GetProperty("action").GetString();
            if (action == "ready") { await SendGlassStateAsync(true); return; }
            if (action == "viewReady")
            {
                if (_glassReady && !_glassUnavailable)
                {
                    CompleteInterfaceStartup();
                    if (_deviceNotesLoadWarning is { } warning)
                    {
                        _deviceNotesLoadWarning = null;
                        await GlassNoticeAsync(new { message = warning, error = true });
                    }
                }
                return;
            }
            if (action == "minimize") { WindowState = WindowState.Minimized; return; }
            if (action == "maximize") { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; return; }
            if (action == "close") { if (_uiPreview) Close(); else WindowState = WindowState.Minimized; return; }
            if (action == "exit") { Close(); return; }
            if (action == "legacy") { ShowGlass(false); return; }
            if (action == "stopDeviceTest") { _devicePreviewCancellation?.Cancel(); return; }
            // Notes are metadata and can be edited even while sound controls are busy.
            if (action == "deviceNote")
            {
                var noteId = data.GetProperty("id").GetString();
                if (noteId is null || !_viewModel.Devices.Any(d => d.Id == noteId))
                    throw new InvalidOperationException("设备列表已改变，请刷新后再编辑备注。");
                var note = DeviceNotesStore.Normalize(data.GetProperty("note").GetString());
                if (_uiPreview)
                {
                    if (note.Length == 0) _deviceNotes.Remove(noteId); else _deviceNotes[noteId] = note;
                }
                else
                {
                    try { _deviceNotes = _deviceNotesStore.Set(noteId, note); }
                    catch (Exception ex) { throw new InvalidOperationException("备注未能保存，请检查设备备注文件是否可读写。原有备注已保留。", ex); }
                }
                await SendGlassStateAsync(true);
                await GlassNoticeAsync(new { message = _uiPreview ? "演示备注已更新，仅保留在本次预览。" : note.Length == 0 ? "设备备注已移除。" : "设备备注已保存。" });
                return;
            }
            if (AudioBusy)
                throw new InvalidOperationException("正在试听、校准或切换音源，请等待结束后再调整。");
            AudioDeviceInfo Device(JsonElement item) => _viewModel.Devices.SingleOrDefault(d => d.Id == item.GetProperty("id").GetString())
                ?? throw new InvalidOperationException("设备列表已改变，请刷新后再试。");
            switch (action)
            {
                case "auditionVolume":
                    var auditionPercent = (int)Math.Round(Number(data.GetProperty("value"), 1, 100));
                    if (!_uiPreview) PairUp.App.Services.CalibrationPreferences.SaveAuditionPercent(auditionPercent);
                    _auditionVolume = auditionPercent;
                    break;
                case "testDevice":
                    await PreviewDeviceAsync(Device(data));
                    break;
                case "setSource":
                    await ChangeMainSourceAsync(Device(data));
                    break;
                case "master":
                    var volume = Number(data.GetProperty("value"), 0, 100);
                    if (!ApplyMasterVolume(volume))
                        throw new InvalidOperationException(_audioEngine.VolumeControlError ?? "整体音量未能调整，请结束试听或校准后再试。");
                    break;
                case "mute":
                    var muted = data.GetProperty("value").GetBoolean();
                    if (!_uiPreview && !_audioEngine.TrySetSourceMuted(muted)) throw new InvalidOperationException(_audioEngine.SourceControlError);
                    _viewModel.IsSourceMuted = muted;
                    break;
                case "device":
                    var device = Device(data);
                    var field = data.GetProperty("field").GetString();
                    var value = data.GetProperty("value");
                    if (device.IsSystemDefault && field is not ("volume" or "isFavorite")) throw new InvalidOperationException("主音源由 Windows 直接播放，不能在转发中再次处理。");
                    if (device.State != DeviceState.Active && field != "isFavorite" && !(field == "isConnected" && value.ValueKind == JsonValueKind.False))
                        throw new InvalidOperationException("Windows 显示这台设备尚未连接。连接后刷新设备列表再调整。");
                    switch (field)
                    {
                        case "isConnected": device.IsConnected = value.GetBoolean(); break;
                        case "volume": device.Volume = Number(value, 0, 100); break;
                        case "latencyMs": device.LatencyMs = Math.Round(Number(value, 0, 3000), 1); break;
                        case "bassBoost": device.BassBoost = Number(value, -12, 12); break;
                        case "treble": device.Treble = Number(value, -12, 12); break;
                        case "isMono": device.IsMono = value.GetBoolean(); break;
                        case "isFavorite": device.IsFavorite = value.GetBoolean(); break;
                        default: throw new InvalidOperationException("不支持的设备设置");
                    }
                    break;
                case "spatial":
                case "spatialBatch":
                    var items = action == "spatial" ? new[] { data } : data.GetProperty("items").EnumerateArray().ToArray();
                    if (items.Length is < 1 or > 32) throw new InvalidOperationException("请选择 1–32 台输出音箱");
                    var changes = items.Select(item => (Device: Device(item), Profile: ReadSpatial(item))).ToArray();
                    if (changes.Select(c => c.Device.Id).Distinct().Count() != changes.Length || changes.Any(c => c.Device.IsSystemDefault || !c.Device.IsConnected || c.Device.State != DeviceState.Active))
                        throw new InvalidOperationException("音场只可应用到 Windows 已连接且已开启的转发输出，每台设备只能分配一次。");
                    var previous = changes.Select(c => (c.Device, c.Device.Spatial)).ToArray();
                    _isRestoringSettings = true;
                    try
                    {
                        foreach (var change in changes) change.Device.Spatial = change.Profile;
                        SaveSettings();
                    }
                    catch { foreach (var old in previous) old.Device.Spatial = old.Spatial; throw; }
                    finally { _isRestoringSettings = false; }
                    await GlassNoticeAsync(new { message = _uiPreview ? "演示音场已更新，未操作真实设备。" : "音场分工已保存，音量和同步延迟保持原值。" });
                    break;
                case "calibrate":
                    if (_uiPreview) await GlassNoticeAsync(new { message = "这是界面预览，不播放校准测试音。" });
                    else MicrophoneCalibration_Click(this, new RoutedEventArgs());
                    break;
                case "refresh":
                    if (!_uiPreview) Refresh_Click(new Button(), new RoutedEventArgs());
                    break;
                case "guest":
                    var shared = Device(data);
                    if (_uiPreview) await GlassNoticeAsync(new { message = "预览不启动访客服务。" });
                    else if (_guestServer is { IsRunning: true } server)
                    {
                        var link = server.GetLinkForDevice(shared.Id);
                        await GlassNoticeAsync(new { message = "让访客连接同一 Wi-Fi 后扫码；仅控制这台音箱。", link, pin = server.Pin, qr = "data:image/png;base64," + Convert.ToBase64String(server.GetQrPng(link)) });
                    }
                    else throw new InvalidOperationException("访客服务不可用，请检查局域网连接。");
                    break;
                case "update":
                    if (_uiPreview) await GlassNoticeAsync(new { message = "预览不会下载或安装更新。" });
                    else CheckUpdate_Click(CheckUpdateButton, new RoutedEventArgs());
                    break;
                default: throw new InvalidOperationException("不支持的操作");
            }
            await SendGlassStateAsync(true);
        }
        catch (Exception ex)
        {
            var summary = ex.Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "请稍后重试。";
            await GlassNoticeAsync(new { message = summary, details = ex.ToString(), error = true });
            await SendGlassStateAsync(true);
        }
    }

    private static double Number(JsonElement element, double min, double max)
    {
        if (!element.TryGetDouble(out var value) || !double.IsFinite(value) || value < min || value > max)
            throw new InvalidOperationException($"数值必须在 {min}–{max} 之间");
        return value;
    }

    private static SpatialProfile ReadSpatial(JsonElement item)
    {
        if (!Enum.TryParse<SpatialRole>(item.GetProperty("role").GetString(), out var role) || !Enum.IsDefined(role))
            throw new InvalidOperationException("请选择有效的音箱分工");
        var result = new SpatialProfile(role, Number(item.GetProperty("lowCutHz"), 0, 20000), Number(item.GetProperty("highCutHz"), 0, 20000), Number(item.GetProperty("trimDb"), -24, 0));
        result.Validate();
        return result;
    }

    private async Task GlassNoticeAsync(object notice)
    {
        if (_glassDisposed || _glass?.CoreWebView2 is null) return;
        try { await _glass.CoreWebView2.ExecuteScriptAsync("window.shengyuMessage && window.shengyuMessage(" + JsonSerializer.Serialize(notice) + ");"); }
        catch (Exception ex) { GlassFailed(ex.Message); }
    }

    private void DisposeGlass()
    {
        _glassDisposed = true;
        _glassReady = false;
        _glassStateTimer.Stop();
        _glassStartupTimer.Stop();
        _glass?.Dispose();
    }

    private async Task RunPreviewChecksAsync()
    {
        // A local-only UI harness. It cannot run in the real-audio mode.
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--ui-checks");
        if (!_uiPreview || index < 0 || index + 2 >= args.Length || _glass?.CoreWebView2 is null) return;
        try
        {
            var script = await File.ReadAllTextAsync(Path.GetFullPath(args[index + 1]));
            var directory = Path.GetFullPath(args[index + 2]);
            Directory.CreateDirectory(directory);
            await Task.Delay(700);
            await _glass.CoreWebView2.ExecuteScriptAsync(script);
            for (var n = 0; n < 400; n++)
            {
                var progress = await _glass.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.__uiCheck || null)");
                var decoded = JsonSerializer.Deserialize<string>(progress);
                if (decoded is not null && decoded != "null")
                {
                    using var state = JsonDocument.Parse(decoded);
                    var snapshot = state.RootElement;
                    if (snapshot.TryGetProperty("resize", out var resize))
                    {
                        Width = Number(resize.GetProperty("width"), 640, 1920);
                        Height = Number(resize.GetProperty("height"), 420, 1200);
                        await Task.Delay(250);
                        await _glass.CoreWebView2.ExecuteScriptAsync("window.__resizeDone = true;");
                    }
                    if (snapshot.TryGetProperty("media", out var media))
                    {
                        await _glass.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", media.GetRawText());
                        await _glass.CoreWebView2.ExecuteScriptAsync("window.__mediaDone = true;");
                    }
                    if (snapshot.TryGetProperty("capture", out var capture) && capture.GetString() is { } name)
                    {
                        using var file = File.Create(Path.Combine(directory, Path.GetFileName(name) + ".png"));
                        await _glass.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                        await _glass.CoreWebView2.ExecuteScriptAsync("window.__captureDone = true;");
                    }
                    if (snapshot.TryGetProperty("done", out var done) && done.GetBoolean())
                    {
                        await File.WriteAllTextAsync(Path.Combine(directory, "ui-results.json"), decoded);
                        await File.WriteAllTextAsync(Path.Combine(directory, "runtime.txt"), CoreWebView2Environment.GetAvailableBrowserVersionString());
                        return;
                    }
                }
                await Task.Delay(300);
            }
            throw new TimeoutException("界面检查未在限定时间内完成");
        }
        catch (Exception ex)
        {
            if (index + 2 < args.Length) await File.WriteAllTextAsync(Path.Combine(Path.GetFullPath(args[index + 2]), "ui-error.txt"), ex.ToString());
        }
    }
}
