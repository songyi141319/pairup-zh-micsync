# preview.4 软件检查记录

## 已完成

- Release 编译：0 个错误、0 个警告（2026-10-14）。
- Web 页面 JavaScript 语法检查通过。
- 校准窗口实际 XAML 在后台渲染为自动、手动、按时段三张图片；未显示桌面窗口。示例设备名称用于排版，不是读取本机设备。
- Chromium 无窗口渲染主页面试听音量。使用演示数据，不操作真实设备。
- 静态复核覆盖音量范围、固定本次时段、取消与恢复失败不应用延迟、录音时间记录诊断、托盘首次打开和设备刷新恢复。

## 没有完成的实体检查

没有运行自动化测试，没有播放音频或录音，没有实际拔插设备或操作托盘。尚不能确认实际校准成功率、各设备响度、设备拔插恢复与长时间同步已经通过验收。

“麦克风录音中断”的原提示也涵盖声音时间记录不连续。已改进诊断并修正无法定位的录音包处理，但用户反复失败的具体原因仍需下一次手动运行后的诊断确认。

## 命令

```powershell
dotnet build src/PairUp.App/PairUp.App.csproj -c Release -p:NuGetAudit=false -p:OutputPath=bin/CalibrationPreview/ --no-restore
node --check src/PairUp.App/Web/app.js
```

后台渲染图片见 `docs/screenshots/preview.4`；音量选项说明见 `CALIBRATION-OPTIONS.zh-CN.md`。

最终静态复核：先前发现的试听恢复失败后恢复音乐、刷新误解除暂停、旧播放通道失效和缓存旧主音源状态问题均已修正。当前审查范围内无剩余已确认重要问题。
