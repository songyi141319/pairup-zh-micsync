# 源码定位

| 要处理的部分 | 文件 |
|---|---|
| 主界面、中文控件、延迟输入和保存 | `src/PairUp.App/MainWindow.xaml`、`MainWindow.xaml.cs` |
| 设备数据与 0.1 毫秒增减 | `src/PairUp.App/Audio/AudioDeviceInfo.cs` |
| 0–3000 毫秒固定延迟 | `src/PairUp.App/Audio/FixedDelaySampleProvider.cs` |
| 软件声音队列及实际最低积压 | `src/PairUp.App/Audio/ChannelAudioBuffer.cs` |
| 每路输出、音效、正在播放流的驱动数据 | `src/PairUp.App/Audio/OutputChannel.cs` |
| 原音源捕获、身份固定、重连格式检查 | `src/PairUp.App/Audio/AudioCaptureEngine.cs` |
| 转发协调、积压修正、音量、校准隔离与恢复 | `src/PairUp.App/Services/AudioEngine.cs` |
| 麦克风校准窗口、设备选择和完整结果应用 | `src/PairUp.App/AcousticCalibrationWindow.xaml`、`.xaml.cs` |
| 每台三次、不同已知间隔、音乐静音恢复 | `src/PairUp.App/Services/AcousticCalibrator.cs` |
| 带 Windows 时间记录的录音 | `src/PairUp.App/Audio/TimestampedAudioCapture.cs` |
| 测试音生成、滤杂音、识别与可靠性判定 | `src/PairUp.App/Audio/AcousticDelayEstimator.cs` |
| 参考与麦克风时间比较、扣除已知间隔 | `src/PairUp.App/Audio/AcousticProbeAnalysis.cs` |
| 设置保存和写入失败保护 | `src/PairUp.App/Services/SettingsStore.cs` |
| 当前仍指向上游的更新检查 | `src/PairUp.App/Services/UpdateChecker.cs` |
| 35 项不出声检查 | `tools/SyncChecks/Program.cs` |

注意：`OutputChannel` 通过 NAudio 2.2.1 内部字段取得正在播放的音频流；`AudioCaptureEngine` 也使用该库的内部缓冲配置。升级 NAudio 后须检查兼容性，读取不到时驱动数据显示未知。
