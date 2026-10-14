# preview.3 软件检查记录

## 已完成

- .NET Release 编译：0 个错误、0 个警告。
- JavaScript 语法检查：app.js、startup.js 通过。
- 静态代码复查：修复刷新与校准交错的问题，并补齐播放通道初始化失败/停止失败的资源清理；复核未发现剩余的已确认重要问题。
- git diff --check：通过，只有仓库的 LF/CRLF 换行提示。
- Chrome 无窗口渲染了播放页和启动波纹。截图使用示例设备状态，不是本机实体音箱验证。

编译命令（源码根目录）：

```powershell
dotnet build src/PairUp.App/PairUp.App.csproj -c Release -p:NuGetAudit=false -p:OutputPath=bin/DeviceActionsPreview/ --no-restore
```

## 未完成的实体验收

没有运行自动测试，没有自动播放音频或录音，也没有调用真实 Windows 默认音源切换。以下仍需实际设备验收：每台音箱铃声是否清楚、20%至50%校准是否成功、取消/断连后的实际恢复、切换主音源后的音乐软件行为、长时间同步稳定性。

之前 preview.2 的测试数量不计作本轮新功能通过。先前校准无声不一定只有音量一个原因，不能据此宣称全部解决。
