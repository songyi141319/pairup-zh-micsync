using System.ComponentModel;
using System.Text;
using System.Windows;
using NAudio.CoreAudioApi;
using PairUp.App.Audio;
using PairUp.App.Services;

namespace PairUp.App;

public partial class AcousticCalibrationWindow : Window
{
    private sealed record MicrophoneChoice(string DeviceId, string Name);
    private sealed class TargetChoice
    {
        public required string DeviceId { get; init; }
        public required string Name { get; init; }
        public bool IsSelected { get; set; }
        public bool CanChangeSelection { get; init; } = true;
    }
    private readonly AudioEngine _engine;
    private readonly Action<Dictionary<string, double>> _apply;
    private readonly List<TargetChoice> _targets;
    private CancellationTokenSource? _cancel;
    private AcousticCalibrationReport? _report;
    private bool _running;
    private bool _closeAfterCancellation;
    public bool IsMeasuring => _running;
    public event Action? MeasurementStarting;

    public void CancelAndClose()
    {
        if (!_running) { Close(); return; }
        _closeAfterCancellation = true;
        _cancel?.Cancel();
        StatusText.Text = "正在取消并恢复原播放状态…";
    }

    public AcousticCalibrationWindow(AudioEngine engine, IEnumerable<AudioDeviceInfo> devices, Action<Dictionary<string, double>> apply)
    {
        InitializeComponent();
        _engine = engine;
        _apply = apply;
        var sourceId = engine.SourceDeviceId;
        _targets = devices.Where(d => d.IsConnected || d.Id == sourceId).Select(d => new TargetChoice
        {
            DeviceId = d.Id,
            Name = d.Name + (d.Id == sourceId ? "（当前音源 / 对齐基准）" : ""),
            // Windows may name a wired speaker output "Headphones". Follow enabled routing, not names.
            IsSelected = true,
            CanChangeSelection = d.Id != sourceId
        }).ToList();
        TargetsList.ItemsSource = _targets;
        SourceText.Text = "以当前音源作为对齐基准。输出音箱会补上比音源提前的时间，可调范围为 0–3000 毫秒。";
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var microphones = new List<MicrophoneChoice>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device) microphones.Add(new(device.ID, device.FriendlyName));
            }
            MicrophoneCombo.ItemsSource = microphones;
            MicrophoneCombo.SelectedItem = microphones.FirstOrDefault(m => m.Name.Contains("KO-STAR", StringComparison.OrdinalIgnoreCase))
                ?? microphones.FirstOrDefault(m => !m.Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase));
            if (microphones.Count == 0)
            { StatusText.Text = "没有找到可用麦克风，请连接麦克风后重新打开本窗口。"; StartButton.IsEnabled = false; }
        }
        catch { StatusText.Text = "无法列出麦克风，请检查麦克风连接后重新打开本窗口。"; StartButton.IsEnabled = false; }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (MicrophoneCombo.SelectedItem is not MicrophoneChoice microphone)
        { StatusText.Text = "请先选择收音麦克风。"; return; }
        if (microphone.Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) || microphone.Name.Contains("Joy", StringComparison.OrdinalIgnoreCase))
        { StatusText.Text = "请选择独立麦克风，避免蓝牙音箱切换到通话模式。"; return; }
        var selected = _targets.Where(t => t.IsSelected).Select(t => new CalibrationTarget(t.DeviceId, t.Name)).ToList();
        if (selected.Count < 2) { StatusText.Text = "请选择至少一台输出音箱。"; return; }
        MeasurementStarting?.Invoke();
        _report = null;
        ApplyButton.IsEnabled = false;
        ResultsText.Text = "";
        _running = true;
        SelectionPanel.IsEnabled = false;
        StartButton.IsEnabled = false;
        CloseButton.Content = "取消测量";
        ProgressBar.Visibility = Visibility.Visible;
        _cancel = new CancellationTokenSource();
        var progress = new Progress<string>(text => StatusText.Text = text);
        var service = new AcousticCalibrator(_engine);
        try
        {
            _report = await Task.Run(() => service.MeasureAsync(microphone.DeviceId, selected, progress, _cancel.Token));
            // Cancellation can arrive while the service is restoring Windows audio state.
            _cancel.Token.ThrowIfCancellationRequested();
            var text = new StringBuilder();
            var source = _report.Measurements.Single(m => m.DeviceId == _engine.SourceDeviceId);
            foreach (var result in _report.Measurements)
            {
                var relative = result.ArrivalMs - source.ArrivalMs;
                var timing = result.DeviceId == source.DeviceId ? "对齐基准" : relative < 0 ? $"比音源先响 {-relative:0.0} 毫秒" : $"比音源晚响 {relative:0.0} 毫秒";
                text.AppendLine($"{result.Name}\n{timing}；三次相差 {result.SpreadMs:0.0} 毫秒。");
            }
            text.AppendLine(_report.Plan.Explanation);
            ResultsText.Text = text.ToString();
            ApplyButton.IsEnabled = _report.Plan.CanApply;
            StatusText.Text = _report.Plan.CanApply ? "测量完成，原音量、静音和播放状态已恢复。" : "测量完成，当前条件无法自动对齐，原音量、静音和延迟已恢复。";
            if (_report.Plan.CanApply && AutoApplyCheck.IsChecked == true &&
                service.RestorationWarning is null && _engine.CalibrationRestoreWarning is null) ApplyResults();
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消测量，原音量、静音和延迟已恢复。"; }
        catch (Exception ex)
        {
            StatusText.Text = "校准未完成，未应用新的延迟。原因见下方：";
            ResultsText.Text = ex.Message;
        }
        finally
        {
            var warnings = new[] { service.RestorationWarning, _engine.CalibrationRestoreWarning }.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct().ToArray();
            if (warnings.Length > 0)
            {
                ResultsText.Text += "\n恢复播放状态时需要注意：\n" + string.Join("\n", warnings);
                StatusText.Text = "测量已结束，但部分原播放状态未能恢复。未应用新的延迟，请查看下方说明。";
                ApplyButton.IsEnabled = false;
                _report = null;
                _closeAfterCancellation = false; // Keep restoration failures visible on cancel/close.
            }
            _running = false;
            _cancel.Dispose();
            _cancel = null;
            SelectionPanel.IsEnabled = true;
            StartButton.IsEnabled = true;
            ProgressBar.Visibility = Visibility.Collapsed;
            CloseButton.Content = "关闭";
            if (_closeAfterCancellation) Close();
        }
    }

    private void ApplyResults()
    {
        if (_report?.Plan.CanApply != true) return;
        _apply(_report.Plan.DelaysMs);
        StatusText.Text = "已自动应用并保存延迟。可以关闭窗口，继续听音乐；音箱位置或蓝牙连接改变后可重新测量。";
        ResultsText.Text += "\n已应用：\n" + string.Join("\n", _report.Measurements.Where(m => _report.Plan.DelaysMs.ContainsKey(m.DeviceId))
            .Select(m => $"{m.Name}：{_report.Plan.DelaysMs[m.DeviceId]:0.0} 毫秒"));
        ApplyButton.IsEnabled = false;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try { ApplyResults(); }
        catch (Exception ex) { StatusText.Text = "无法应用测量结果：" + ex.Message; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) { if (_running) _cancel?.Cancel(); else Close(); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_running) return;
        e.Cancel = true;
        _closeAfterCancellation = true;
        _cancel?.Cancel();
        StatusText.Text = "正在取消并恢复原播放状态…";
    }
}
