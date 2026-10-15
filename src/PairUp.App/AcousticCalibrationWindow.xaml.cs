using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using PairUp.App.Audio;
using PairUp.App.Services;

namespace PairUp.App;

public partial class AcousticCalibrationWindow : Window
{
    private sealed record MicrophoneChoice(string DeviceId, string Name);
    private sealed record MeasurementRow(string Name, string Timing, string Repeatability, string Delay, string State);
    private sealed class TargetChoice
    {
        public required string DeviceId { get; init; }
        public required string Name { get; init; }
        public bool IsSelected { get; set; }
        public bool CanChangeSelection { get; init; } = true;
        public string Hint => CanChangeSelection ? "参与本次测量" : "当前主音源 · 固定对齐基准";
    }

    private readonly AudioEngine _engine;
    private readonly Action<Dictionary<string, double>> _apply;
    private readonly List<TargetChoice> _targets;
    private List<CalibrationTarget> _currentTargets = [];
    private string? _measurementSourceId;
    private CancellationTokenSource? _cancel;
    private AcousticCalibrationReport? _report;
    private CalibrationOptions _lastValidOptions = new();
    private bool _running;
    private bool _closeAfterCancellation;
    private bool _uiReady;
    private bool _updatingManual;
    private bool _optionsValid;
    private bool _reportApplied;
    private bool _hasRestorationWarning;
    public bool IsMeasuring => _running;
    public event Action? MeasurementStarting;

    public AcousticCalibrationWindow(AudioEngine engine, IEnumerable<AudioDeviceInfo> devices, Action<Dictionary<string, double>> apply)
    {
        InitializeComponent();
        _engine = engine;
        _apply = apply;
        ConfigurePalette();
        Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 40));
        var sourceId = engine.SourceDeviceId;
        _targets = devices.Where(d => d.IsConnected || SameDevice(d.Id, sourceId)).Select(d => new TargetChoice
        {
            DeviceId = d.Id,
            Name = d.Name,
            // Endpoint names may say "Headphones" for a wired speaker; do not guess by name.
            IsSelected = true,
            CanChangeSelection = !SameDevice(d.Id, sourceId)
        }).ToList();
        TargetsList.ItemsSource = _targets;
        SourceText.Text = "主音源保持原声。其它音箱补上提前到达的时间，可调范围为 0–3000 毫秒。";
        _lastValidOptions = CalibrationPreferences.Load();
        SetVolumeOptions(_lastValidOptions);
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
            {
                StepTitle.Text = "先连接麦克风";
                StatusText.Text = "没有找到可用麦克风。连接后重新打开这个窗口即可。";
            }
        }
        catch (Exception ex)
        {
            StepTitle.Text = "暂时无法读取麦克风";
            StatusText.Text = "请检查麦克风连接，再重新打开这个窗口。";
            AppendDetail("列出麦克风失败：" + ex);
        }
        _uiReady = true;
        RefreshVolumePlan();
        RefreshSelection();
    }

    private void ConfigurePalette()
    {
        if (SystemParameters.HighContrast)
        {
            Resources["CalBackground"] = SystemColors.WindowBrush;
            Resources["CalSurface"] = SystemColors.WindowBrush;
            Resources["CalSecondary"] = SystemColors.ControlBrush;
            Resources["CalBorder"] = SystemColors.WindowTextBrush;
            Resources["CalText"] = SystemColors.WindowTextBrush;
            Resources["CalMuted"] = SystemColors.WindowTextBrush;
            Resources["CalAccent"] = SystemColors.HighlightBrush;
            Resources["CalOnAccent"] = SystemColors.HighlightTextBrush;
            Resources["CalAccentSoft"] = SystemColors.ControlBrush;
            Resources["CalWarning"] = SystemColors.WindowTextBrush;
            Resources["CalWarningFill"] = SystemColors.WindowBrush;
            return;
        }
        if (ThemeService.IsLightTheme()) return;
        foreach (var (key, color) in new Dictionary<string, string>
        {
            ["CalBackground"] = "#141C29", ["CalSurface"] = "#202C3D", ["CalSecondary"] = "#29374B",
            ["CalBorder"] = "#3C4A61", ["CalText"] = "#EFF4FF", ["CalMuted"] = "#BBC9DE",
            ["CalAccent"] = "#B5C9FF", ["CalOnAccent"] = "#18243A", ["CalAccentSoft"] = "#2C3C59",
            ["CalWarning"] = "#F2C88B", ["CalWarningFill"] = "#3D3020"
        }) Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static bool SameDevice(string? first, string? second) =>
        !string.IsNullOrEmpty(first) && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private void SetVolumeOptions(CalibrationOptions options)
    {
        AutomaticMode.IsChecked = options.Mode == CalibrationVolumeMode.Automatic;
        ManualMode.IsChecked = options.Mode == CalibrationVolumeMode.Manual;
        ManualPercentBox.Text = options.ManualPercent.ToString(CultureInfo.InvariantCulture);
        ManualVolumeSlider.Value = options.ManualPercent;
        StartPercentBox.Text = options.StartPercent.ToString(CultureInfo.InvariantCulture);
        MaximumPercentBox.Text = options.MaximumPercent.ToString(CultureInfo.InvariantCulture);
        UseTimeScheduleCheck.IsChecked = options.UseTimeSchedule;
        NightPercentBox.Text = options.NightPercent.ToString(CultureInfo.InvariantCulture);
        MorningPercentBox.Text = options.MorningPercent.ToString(CultureInfo.InvariantCulture);
        DayPercentBox.Text = options.DayPercent.ToString(CultureInfo.InvariantCulture);
        NightMaximumPercentBox.Text = options.NightMaximumPercent.ToString(CultureInfo.InvariantCulture);
        MorningMaximumPercentBox.Text = options.MorningMaximumPercent.ToString(CultureInfo.InvariantCulture);
        DayMaximumPercentBox.Text = options.DayMaximumPercent.ToString(CultureInfo.InvariantCulture);
    }

    private static int ReadPercent(TextBox box, string label, int maximum)
    {
        if (!int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1 || value > maximum)
            throw new InvalidOperationException($"请将{label}设为 1–{maximum}% 之间的整数。");
        return value;
    }

    private static int ReadInactivePercent(TextBox box, int maximum, int fallback) =>
        int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 1 && value <= maximum ? value : fallback;

    private CalibrationOptions ReadVolumeOptions()
    {
        var automatic = AutomaticMode.IsChecked == true;
        var scheduled = UseTimeScheduleCheck.IsChecked == true;
        int Read(TextBox box, string label, int maximum, int fallback, bool active) =>
            active ? ReadPercent(box, label, maximum) : ReadInactivePercent(box, maximum, fallback);
        var options = new CalibrationOptions(
            Mode: automatic ? CalibrationVolumeMode.Automatic : CalibrationVolumeMode.Manual,
            ManualPercent: Read(ManualPercentBox, "手动音量", 100, _lastValidOptions.ManualPercent, !automatic),
            StartPercent: Read(StartPercentBox, "自定起始音量", 50, _lastValidOptions.StartPercent, automatic && !scheduled),
            MaximumPercent: Read(MaximumPercentBox, "自动上限", 50, _lastValidOptions.MaximumPercent, automatic),
            UseTimeSchedule: scheduled,
            NightPercent: Read(NightPercentBox, "夜间起始音量", 50, _lastValidOptions.NightPercent, automatic && scheduled),
            MorningPercent: Read(MorningPercentBox, "早间起始音量", 50, _lastValidOptions.MorningPercent, automatic && scheduled),
            DayPercent: Read(DayPercentBox, "日间起始音量", 50, _lastValidOptions.DayPercent, automatic && scheduled),
            NightMaximumPercent: Read(NightMaximumPercentBox, "夜间上限", 50, _lastValidOptions.NightMaximumPercent, automatic && scheduled),
            MorningMaximumPercent: Read(MorningMaximumPercentBox, "早间上限", 50, _lastValidOptions.MorningMaximumPercent, automatic && scheduled),
            DayMaximumPercent: Read(DayMaximumPercentBox, "日间上限", 50, _lastValidOptions.DayMaximumPercent, automatic && scheduled));
        options.Validate();
        return options;
    }

    private void RefreshVolumePlan()
    {
        if (!_uiReady) return;
        var automatic = AutomaticMode.IsChecked == true;
        AutomaticVolumePanel.Visibility = automatic ? Visibility.Visible : Visibility.Collapsed;
        ManualVolumePanel.Visibility = automatic ? Visibility.Collapsed : Visibility.Visible;
        TimeSchedulePanel.Visibility = UseTimeScheduleCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        StartPercentBox.IsEnabled = UseTimeScheduleCheck.IsChecked != true;
        ManualLoudHint.Visibility = !automatic && int.TryParse(ManualPercentBox.Text, out var manual) && manual > 50 ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            var options = ReadVolumeOptions();
            var now = DateTime.Now;
            var levels = options.GetLevels(now);
            _lastValidOptions = options;
            VolumePlanText.Text = string.Join(" → ", levels.Select(level => level + "%"));
            if (options.Mode == CalibrationVolumeMode.Manual)
                VolumePlanHint.Text = "保持固定音量；测试音偏弱时也不会自动升档。";
            else
            {
                var start = options.StartPercent;
                var prefix = "仅在测试音偏弱时进入下一档。";
                if (options.UseTimeSchedule)
                {
                    var night = now.Hour < 8 || now.Hour >= 22;
                    var morning = now.Hour >= 8 && now.Hour < 12;
                    start = night ? options.NightPercent : morning ? options.MorningPercent : options.DayPercent;
                    prefix = $"本机 {now:HH:mm} · {(night ? "夜间" : morning ? "早间" : "日间")}，本次有效上限 {levels[^1]}%。";
                }
                VolumePlanHint.Text = prefix + (start > levels[0] ? $" 起始值高于有效上限，因此从 {levels[0]}% 开始。" : " 达到本次上限仍不可靠时停止。");
            }
            VolumeValidationText.Visibility = Visibility.Collapsed;
            _optionsValid = true;
        }
        catch (Exception ex)
        {
            VolumePlanText.Text = "请检查音量设置";
            VolumePlanHint.Text = "修正后会显示这次实际使用的音量档位。";
            VolumeValidationText.Text = ex.Message;
            VolumeValidationText.Visibility = Visibility.Visible;
            _optionsValid = false;
        }
        RefreshStartAvailability();
    }

    private void VolumeOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _running) return;
        InvalidateReport();
        RefreshVolumePlan();
    }

    private void ManualPercent_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady || _updatingManual || _running) return;
        _updatingManual = true;
        if (int.TryParse(ManualPercentBox.Text, out var percent) && percent is >= 1 and <= 100) ManualVolumeSlider.Value = percent;
        _updatingManual = false;
        VolumeOptions_Changed(sender, e);
    }

    private void ManualSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || _updatingManual || _running) return;
        _updatingManual = true;
        ManualPercentBox.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture);
        _updatingManual = false;
        VolumeOptions_Changed(sender, e);
    }

    private void Microphone_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _running) return;
        InvalidateReport();
        RefreshStartAvailability();
    }

    private void TargetSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _running) return;
        if (sender is CheckBox { DataContext: TargetChoice choice } checkbox) choice.IsSelected = checkbox.IsChecked == true;
        InvalidateReport();
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        TargetCountText.Text = $"主音源 + {_targets.Count(target => target.CanChangeSelection && target.IsSelected)} 台输出";
        RefreshStartAvailability();
    }

    private void RefreshStartAvailability()
    {
        if (!_uiReady) return;
        StartButton.IsEnabled = !_running && _optionsValid && MicrophoneCombo.SelectedItem is MicrophoneChoice &&
            _targets.Any(target => target.IsSelected && !target.CanChangeSelection) && _targets.Any(target => target.IsSelected && target.CanChangeSelection);
    }

    private void InvalidateReport()
    {
        if (_report is null && !_reportApplied) return;
        _report = null;
        _reportApplied = false;
        ApplyButton.IsEnabled = false;
        ResultsList.ItemsSource = null;
        ResultsEmptyText.Visibility = Visibility.Visible;
        ResultsEmptyText.Text = "设置已调整，请重新测量后再应用结果。";
        ResultsSummaryText.Visibility = Visibility.Collapsed;
        StepTitle.Text = "准备重新测量";
        StatusText.Text = "确认设置后，再次点击开始校准。";
        CurrentVolumeText.Text = "未开始";
        ProgressPanel.Visibility = Visibility.Collapsed;
    }

    private void AppendDetail(string text)
    {
        if (DetailsText.Text == "还没有开始测量。") DetailsText.Clear();
        DetailsText.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n");
        CopyDiagnosticsButton.IsEnabled = true;
        CopyDiagnosticsButton.Content = "复制诊断";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (MicrophoneCombo.SelectedItem is not MicrophoneChoice microphone)
        { StatusText.Text = "请先选择收音麦克风。"; return; }
        if (microphone.Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) || microphone.Name.Contains("Joy", StringComparison.OrdinalIgnoreCase))
        { StepTitle.Text = "请更换收音麦克风"; StatusText.Text = "请选择独立麦克风，避免蓝牙音箱切换到通话模式。"; return; }
        var selected = _targets.Where(target => target.IsSelected).Select(target => new CalibrationTarget(target.DeviceId, target.Name)).ToList();
        if (selected.Count < 2) { StatusText.Text = "请选择至少一台输出音箱。"; return; }
        CalibrationOptions options;
        CalibrationOptions measurementOptions;
        int[] levels;
        var startedAt = DateTime.Now;
        try
        {
            options = ReadVolumeOptions();
            levels = options.GetLevels(startedAt);
            // Preserve the user's schedule in preferences, but hand the service
            // the same immutable levels that this run's summary displays.
            measurementOptions = options.Mode == CalibrationVolumeMode.Automatic && options.UseTimeSchedule
                ? options with { UseTimeSchedule = false, StartPercent = levels[0], MaximumPercent = levels[^1] }
                : options;
            CalibrationPreferences.Save(options);
            MeasurementStarting?.Invoke();
        }
        catch (Exception ex)
        {
            StepTitle.Text = "暂时无法开始";
            StatusText.Text = ShortMessage(ex.Message);
            AppendDetail("开始前准备失败：" + ex);
            return;
        }

        _measurementSourceId = _engine.SourceDeviceId;
        _currentTargets = selected.OrderBy(target => SameDevice(target.DeviceId, _measurementSourceId) ? 0 : 1).ToList();
        _report = null;
        _reportApplied = false;
        _hasRestorationWarning = false;
        _closeAfterCancellation = false;
        _running = true;
        ApplyButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        SelectionPanel.IsEnabled = false;
        SelectionPanel.Visibility = Visibility.Collapsed;
        SelectionSummaryPanel.Visibility = Visibility.Visible;
        SelectionSummaryText.Text = $"麦克风：{microphone.Name}\n主音源与 {selected.Count - 1} 台输出 · {(options.Mode == CalibrationVolumeMode.Manual ? "手动固定" : "自动调节")} · {string.Join(" → ", levels.Select(level => level + "%"))}";
        EditSettingsButton.IsEnabled = false;
        AutoApplyCheck.IsEnabled = false;
        RestorationWarningPanel.Visibility = Visibility.Collapsed;
        ResultsList.ItemsSource = null;
        ResultsSummaryText.Visibility = Visibility.Collapsed;
        ResultsEmptyText.Visibility = Visibility.Visible;
        ResultsEmptyText.Text = "正在测量。完成后会逐台列出到达时间与补偿建议。";
        DetailsText.Clear();
        DetailsExpander.IsExpanded = false;
        AppendDetail("麦克风：" + microphone.Name);
        AppendDetail("本次设置：" + options);
        AppendDetail($"本机开始时间：{startedAt:yyyy-MM-dd HH:mm:ss}；本次固定档位：{string.Join("、", levels.Select(level => level + "%"))}");
        StepTitle.Text = "正在准备测量";
        CurrentDeviceText.Text = "暂时静音音乐，准备麦克风与音箱。";
        CurrentVolumeText.Text = "准备中";
        StatusText.Text = "请保持安静。原音量和静音会在测量结束后恢复。";
        FooterStatusText.Text = "可随时取消，窗口会等待播放状态恢复。";
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = true;
        ProgressBar.Value = 0;
        ProgressCaption.Text = "正在准备设备";
        CloseButton.Content = "取消测量";
        CloseButton.IsEnabled = true;
        ContentScroll.ScrollToTop();
        StepTitle.BringIntoView();
        var cancellation = new CancellationTokenSource();
        _cancel = cancellation;
        var progress = new Progress<string>(ReportProgress);
        var service = new AcousticCalibrator(_engine);
        var shouldAutoApply = false;
        var wasCancelled = false;
        try
        {
            _report = await Task.Run(() => service.MeasureAsync(microphone.DeviceId, selected, progress, cancellation.Token, measurementOptions));
            // Cancellation while the service restores Windows state must still win.
            cancellation.Token.ThrowIfCancellationRequested();
            RenderResults(false);
            shouldAutoApply = _report.Plan.CanApply && AutoApplyCheck.IsChecked == true;
            StepTitle.Text = "测量完成";
            CurrentDeviceText.Text = $"已完成 {_report.Measurements.Count} 台设备的测量。";
            StatusText.Text = _report.Plan.CanApply ? "结果已准备好，应用后才会改变音箱延迟。" : "这组设备暂时无法自动对齐。请查看结果中的说明。";
            ResultsSummaryText.Text = _report.Plan.Explanation;
            ResultsSummaryText.Visibility = Visibility.Visible;
            CurrentVolumeText.Text = "已完成";
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 100;
            ProgressCaption.Text = "本次测量已结束";
            AppendDetail("测量计划：" + _report.Plan.Explanation);
        }
        catch (OperationCanceledException)
        {
            _report = null;
            StepTitle.Text = "已取消测量";
            StatusText.Text = "未应用新的延迟。准备好后可以重新开始。";
            ResultsEmptyText.Text = "本次已取消，没有生成可应用的结果。";
            CurrentVolumeText.Text = "已取消";
            AppendDetail("用户取消了本次测量。");
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _report = null;
            StepTitle.Text = "校准未完成";
            StatusText.Text = ShortMessage(ex.Message);
            ResultsEmptyText.Text = FailureAdvice(ex.Message);
            CurrentVolumeText.Text = "未完成";
            AppendDetail("测量失败：" + ex);
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
        finally
        {
            wasCancelled = cancellation.IsCancellationRequested;
            var warnings = new[] { service.RestorationWarning, _engine.CalibrationRestoreWarning }
                .Where(warning => !string.IsNullOrWhiteSpace(warning)).Distinct().ToArray();
            _hasRestorationWarning = warnings.Length > 0;
            if (_hasRestorationWarning)
            {
                RestorationWarningText.Text = string.Join("\n", warnings);
                RestorationWarningPanel.Visibility = Visibility.Visible;
                StepTitle.Text = "测量结束，请检查播放状态";
                CurrentVolumeText.Text = "需检查";
                StatusText.Text = "部分原音量、静音或播放状态未能恢复。没有应用新的延迟。";
                FooterStatusText.Text = "恢复提醒会保留，确认后可关闭窗口。";
                ResultsSummaryText.Text = "由于播放状态没有完整恢复，本次结果不会被应用。";
                ResultsSummaryText.Visibility = Visibility.Visible;
                AppendDetail("恢复播放状态时需要注意：\n" + string.Join("\n", warnings));
                _report = null;
                _closeAfterCancellation = false; // Keep restoration failures visible, including cancel/close.
            }
            else FooterStatusText.Text = "原音量、静音和播放状态已恢复。";
            _running = false;
            cancellation.Dispose();
            _cancel = null;
            SelectionPanel.IsEnabled = true;
            EditSettingsButton.IsEnabled = true;
            AutoApplyCheck.IsEnabled = true;
            CloseButton.IsEnabled = true;
            CloseButton.Content = "关闭";
            ApplyButton.IsEnabled = _report?.Plan.CanApply == true && !_hasRestorationWarning && !wasCancelled;
            RefreshStartAvailability();
        }
        // No application happens until all restoration checks have completed.
        if (shouldAutoApply && !wasCancelled && !_hasRestorationWarning)
        {
            try { ApplyResults(); }
            catch (Exception ex) { ShowApplyFailure(ex); }
        }
        if (_closeAfterCancellation) Close();
        else _ = Dispatcher.BeginInvoke(new Action(() => ResultsCard.BringIntoView()));
    }

    private void ReportProgress(string text)
    {
        if (!_running) return;
        AppendDetail(text);
        if (_cancel?.IsCancellationRequested == true) return;
        var match = Regex.Match(text, @"^当前\s+(?<percent>\d+)%[，,]\s*正在测量\s+(?<name>.+)[，,]\s*第\s+(?<repeat>[1-3])/3\s*次", RegexOptions.CultureInvariant);
        if (match.Success)
        {
            var name = match.Groups["name"].Value;
            var repeat = int.Parse(match.Groups["repeat"].Value, CultureInfo.InvariantCulture);
            // The existing progress contract has names, not endpoint IDs. If two
            // endpoints share a name, do not invent a device index or percentage.
            var index = _currentTargets.Count(target => target.Name == name) == 1
                ? _currentTargets.FindIndex(target => target.Name == name) : -1;
            StepTitle.Text = index == 0 ? "正在测量主音源" : index > 0 ? "正在测量输出音箱" : "正在测量音箱";
            CurrentDeviceText.Text = name;
            CurrentDeviceText.ToolTip = name;
            CurrentVolumeText.Text = match.Groups["percent"].Value + "%";
            StatusText.Text = index == 0 ? "请保持安静，稍后会听到短测试音。" : index > 0 ? "先响主音源，再响当前音箱。两声之间留有识别间隔。" : "请保持安静，音箱与麦克风保持原位。";
            ProgressBar.IsIndeterminate = index < 0;
            if (index >= 0) ProgressBar.Value = (index * 3 + repeat - 1) * 100.0 / Math.Max(1, _currentTargets.Count * 3);
            ProgressCaption.Text = (index >= 0 ? $"第 {index + 1}/{_currentTargets.Count} 台 · " : "") + $"正在第 {repeat}/3 次测量 · 进度按当前音量档位计算";
        }
        else if (text.Contains("测试音仍偏弱", StringComparison.Ordinal))
        {
            StepTitle.Text = "测试音偏弱，准备重测";
            CurrentDeviceText.Text = "按已选择的音量安排，重新测量整组音箱。";
            StatusText.Text = "不会混用不同音量下的结果；达到本次上限仍不可靠时停止。";
            ProgressBar.IsIndeterminate = true;
            ProgressCaption.Text = "准备下一档音量";
        }
        else if (text.Contains("临时统一为", StringComparison.Ordinal))
        {
            var level = Regex.Match(text, @"临时统一为\s+(\d+)%", RegexOptions.CultureInvariant);
            if (level.Success) CurrentVolumeText.Text = level.Groups[1].Value + "%";
            StepTitle.Text = "正在准备这一档音量";
            CurrentDeviceText.Text = "主音源与所选音箱使用同一档测试音量。";
            StatusText.Text = "每台会测量三次。请保持安静，音箱和麦克风保持原位。";
            ProgressBar.IsIndeterminate = true;
            ProgressCaption.Text = "准备开始本档测量";
        }
        else
        {
            StepTitle.Text = "正在校准";
            StatusText.Text = ShortMessage(text);
        }
    }

    private void RenderResults(bool applied)
    {
        var report = _report;
        if (report is null) return;
        var source = report.Measurements.Single(result => SameDevice(result.DeviceId, _measurementSourceId));
        ResultsList.ItemsSource = report.Measurements.Select(result =>
        {
            var isSource = SameDevice(result.DeviceId, source.DeviceId);
            var relative = result.ArrivalMs - source.ArrivalMs;
            var timing = isSource ? "主音源 · 对齐基准" : relative < 0 ? $"比主音源提前 {-relative:0.0} 毫秒到达" : $"比主音源晚 {relative:0.0} 毫秒到达";
            var hasDelay = report.Plan.DelaysMs.TryGetValue(result.DeviceId, out var delay);
            return new MeasurementRow(result.Name, timing, $"三次测量相差 {result.SpreadMs:0.0} 毫秒", isSource ? "基准" : hasDelay ? $"{delay:0.0} 毫秒" : "—",
                isSource ? "保持原声" : hasDelay ? applied ? "已应用补偿" : "测得补偿" : "暂不应用");
        }).ToArray();
        ResultsEmptyText.Visibility = Visibility.Collapsed;
    }

    private void ApplyResults()
    {
        if (_running || _hasRestorationWarning || _reportApplied || _report?.Plan.CanApply != true) return;
        _apply(_report.Plan.DelaysMs);
        _reportApplied = true;
        RenderResults(true);
        StepTitle.Text = "同步设置已完成";
        StatusText.Text = "延迟已应用并保存，可以继续听音乐。音箱位置或蓝牙连接改变后可重新测量。";
        ResultsSummaryText.Text = "已应用并保存这次补偿。其它音量和静音保持测量前的状态。";
        ResultsSummaryText.Visibility = Visibility.Visible;
        AppendDetail("已应用：\n" + string.Join("\n", _report.Measurements.Where(result => _report.Plan.DelaysMs.ContainsKey(result.DeviceId))
            .Select(result => $"{result.Name}：{_report.Plan.DelaysMs[result.DeviceId]:0.0} 毫秒")));
        ApplyButton.IsEnabled = false;
    }

    private void ShowApplyFailure(Exception ex)
    {
        _report = null;
        ApplyButton.IsEnabled = false;
        StepTitle.Text = "未能完成应用";
        StatusText.Text = "请检查设备连接后重新测量。";
        ResultsSummaryText.Text = "应用没有完成，完整原因已记录在技术详情。";
        ResultsSummaryText.Visibility = Visibility.Visible;
        AppendDetail("应用结果失败：" + ex);
    }

    private static string ShortMessage(string text)
    {
        var firstLine = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "请检查设备连接和麦克风设置。";
        return firstLine.Length <= 140 ? firstLine : firstLine[..137] + "…";
    }

    private static string FailureAdvice(string text)
    {
        // Classify the actual failure line, not later volume snapshots or stack traces.
        var reason = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
        const string retained = "原延迟没有改变，详细记录已保留。";
        if (reason.Contains("时间记录") || reason.Contains("时间排列"))
            return "这次是录音计时未通过检查，提高音量不能解决。不需要反复试听或重配音箱。" + retained;
        if (reason.Contains("音量或静音") || reason.Contains("临时音量"))
            return "这次是设备音量未能保持在测量值，程序不会强行调高或反复重试。" + retained;
        if (reason.Contains("失真") || reason.Contains("无效样点"))
            return "这次录音失真或无效，不能继续提高音量。可降低音量或让麦克风稍远离音箱。" + retained;
        if (reason.Contains("中断") || reason.Contains("断开") || reason.Contains("播放连接"))
            return "这次播放或收音连接发生变化。请先确认提示中的设备恢复连接，再决定是否重测。" + retained;
        return "这次没有得到可应用的结果，先保留现有设置；完整原因可在技术详情中查看或复制。";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try { ApplyResults(); }
        catch (Exception ex) { ShowApplyFailure(ex); }
    }

    private void EditSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        SelectionPanel.Visibility = Visibility.Visible;
        SelectionSummaryPanel.Visibility = Visibility.Collapsed;
        ContentScroll.ScrollToTop();
        RefreshVolumePlan();
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(DetailsText.Text); CopyDiagnosticsButton.Content = "已复制"; }
        catch { CopyDiagnosticsButton.Content = "复制失败"; FooterStatusText.Text = "可在技术详情里选中文字后复制。"; }
    }

    private void RequestCancellation(bool closeAfter)
    {
        if (!_running) { if (closeAfter) Close(); return; }
        _closeAfterCancellation |= closeAfter;
        _cancel?.Cancel();
        StepTitle.Text = "正在取消并恢复";
        StatusText.Text = "正在停止测量，恢复原音量、静音与播放状态。请稍候。";
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = true;
        ProgressCaption.Text = "等待恢复完成";
        FooterStatusText.Text = "恢复完成后才会结束，取消期间不会应用延迟。";
        CloseButton.Content = "正在取消…";
        CloseButton.IsEnabled = false;
    }

    public void CancelAndClose() => RequestCancellation(true);
    private void Close_Click(object sender, RoutedEventArgs e) { if (_running) RequestCancellation(false); else Close(); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_running) return;
        e.Cancel = true;
        RequestCancellation(true);
    }
}
