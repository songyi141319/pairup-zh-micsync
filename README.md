# PairUp 中文增强版

面向 Windows 的多音箱同时播放工具。本仓库保存中文界面、精细延迟调节、软件积压修复和麦克风校准功能，方便在其他电脑使用或继续开发。

**状态：可运行的开发预览版。35 项软件检查通过；新版完整三音箱校准和长时间同步尚待实测。**

## 先下载

- [下载完整包：程序、源码、使用说明和接续文档](https://github.com/songyi141319/pairup-zh-micsync/releases/download/v0.3.6-zh.20261002-preview.1/PairUp-Chinese-win-x64-full-20261002.zip)
- [查看本次发布和校验文件](https://github.com/songyi141319/pairup-zh-micsync/releases/tag/v0.3.6-zh.20261002-preview.1)

完整包适用于 Windows x64，已包含运行所需的 .NET 组件。解压整个文件夹，双击 `PairUp.App.exe`。只使用程序时不需要安装开发工具。完整源码在包内 `source` 文件夹。

GitHub 的绿色 Code → Download ZIP 只下载源码；需要现成程序，请下载上面的完整包。

## 底子是谁的

本项目基于 **[Jamim Mehdi 的 PairUp](https://github.com/jamimmehdi/pair-up-audio)**，来源版本为 **v0.3.6**，原始提交为 `ce056567d3b44ea0857134fedfd35a2e519f24eb`。

原项目版权为 `Copyright (c) 2026 Jamim Mehdi`，使用 MIT 许可证。原始 [LICENSE](LICENSE) 保持完整，原版介绍保存在 [README.upstream.md](README.upstream.md)。本仓库由 [songyi141319](https://github.com/songyi141319) 保存和维护，中文化及功能增强由 Codex 协助完成；这是衍生版本，不是原作者的官方发行版。

更多依赖和方法来源见 [CREDITS.md](CREDITS.md) 与 [第三方声明](THIRD-PARTY-NOTICES.md)。

## 这一版增加了什么

- 中文界面和操作提示。
- 每台输出支持 0–3000 毫秒延迟，支持直接输入小数。
- 滑杆粗调，四个按钮按 1 或 0.1 毫秒增减，按住可连续调整。
- 人工延迟独立保存，软件排队修正不会抵消它。
- 控制单台或多台输出持续增加的软件积压，修复重启后重复转发的路径。
- 显示软件等待和系统驱动排队数据。把鼠标放在状态上可查看说明。
- 麦克风自动校准：每台三次测量，只有完整、可靠、可应用的结果才覆盖旧延迟。
- 测输出音箱时保持当前音源出声，通过三个不同的已知间隔分开识别测试音。
- 捕获中断时停止转发并清除旧声音；重连固定原音源，声音格式改变时提示重新打开。

0.1 毫秒是调节步长，不代表蓝牙能始终保持 0.1 毫秒误差。系统驱动数据也不能完整代表音箱内部处理时间。

## 换电脑怎么用

1. 解压完整包，先连接音箱和独立麦克风。
2. 在 Windows 里选好主音源，再打开 PairUp。当前音源自己直接播放，其余音箱在 PairUp 中打开。
3. 按设备实际声音确认 AUX 对应项。原电脑的 AUX 叫“耳机（2- Realtek(R) Audio）”；其他电脑名称可能不同。
4. 先用较低音量确认每台都有声音，然后调延迟。需要麦克风测量时，按 [使用说明](docs/MIC-SYNC.zh-CN.md) 操作。
5. 换音源后正常退出并重新打开 PairUp。右上角关闭按钮可能只是收起到托盘，彻底退出请用托盘菜单。

配置保存在 `%APPDATA%\PairUp\settings.json`。新电脑的设备标识和声音延迟会变化，需要重新选设备和调节；旧电脑的数值只能作为参考。

**本版程序内“检查更新”仍指向原作者仓库。更新此增强版请从本仓库 Releases 下载；安装原版更新可能覆盖这些增强功能。**

## 下次从哪里继续

- [当前状态、未完成事项和实测步骤](docs/PROJECT-STATUS.zh-CN.md)
- [已有检查结果和已知限制](docs/VALIDATION.zh-CN.md)
- [构建和继续开发](docs/BUILD.zh-CN.md)
- [源码文件说明](docs/CODEMAP.zh-CN.md)

项目按用户要求暂时停止声音测试。本次发布不会代表真实音箱校准已经验收。

## 源码构建

Windows 上安装 .NET 8 SDK，在项目根目录执行：

```powershell
dotnet build .\src\PairUp.App\PairUp.App.csproj -c Release
```

发布可独立运行的 Windows x64 程序：

```powershell
dotnet publish .\src\PairUp.App\PairUp.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o .\publish
```

完整说明见 [BUILD.zh-CN.md](docs/BUILD.zh-CN.md)。

## 许可证

[MIT](LICENSE)。保留原作者版权和许可声明。第三方组件的声明在 [licenses](licenses) 中。
