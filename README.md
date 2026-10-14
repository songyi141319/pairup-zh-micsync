# 声屿 · PairUp 中文增强版

面向 Windows 的多音箱同时播放工具。本仓库保存中文界面、精细延迟调节、软件积压修复和麦克风校准功能，方便在其他电脑使用或继续开发。当前开发版新增“声屿”玻璃界面、音乐音场分工和摆放教程。

**当前开发预览：0.4.0-preview.2。包含声屿中文玻璃界面、音乐音场、可调长度的彩色波形与玻璃球。真实音箱听感、完整多音箱校准和长时间同步仍待实测。**

## 当前开发版

- **音乐用途优先**：全屋同播、左右立体声、音乐环绕、低音辅助；为每台已开启的转发音箱指定位置、频段与分工衰减。
- **中文操作界面**：播放、音场、摆放指南、外观。真实设备沿用 Windows 完整名称。
- **iOS27 主题迁移**：复用现有玻璃组件，保留三档透感、31 项玻璃参数、鼠标边缘亮光、自适应文字和可关闭的彩色氛围。
- **Strands 波形与玻璃球**：保留 React Bits 原组件的 18 项效果参数；波形长度、高度、位置与球形尺寸单独保存，提供四种快捷样式和恢复布局。
- **原有功能保留**：Windows 音量联动、0.1 毫秒延迟微调、麦克风校准、均衡、收藏、分享和原版界面回退。

音乐环绕是普通立体声的模拟扩展，不是原生独立 5.1 或杜比全景声解码。主音源保持 Windows 原声直出，其他转发音箱可分工。说明见 [音乐音场与摆放指南](docs/SHENGYU-GUIDE.zh-CN.md)。

本地开发预览的检查与截图范围见 [2026-10-14 验证记录](docs/VALIDATION-20261014.zh-CN.md)。

玻璃界面需要 Microsoft Edge WebView2 显示组件；缺少时使用原版界面。发布目录里的整个 `Web` 文件夹需要和主程序一起分发。

## 下载安装（推荐）

- **[下载声屿 EXE 安装包](https://github.com/songyi141319/pairup-zh-micsync/releases/download/v0.4.0-preview.2/Shengyu-Setup-0.4.0-preview.2-win-x64.exe)**
- [下载免安装便携包](https://github.com/songyi141319/pairup-zh-micsync/releases/download/v0.4.0-preview.2/Shengyu-Portable-0.4.0-preview.2-win-x64.zip)
- [下载完整源码包](https://github.com/songyi141319/pairup-zh-micsync/releases/download/v0.4.0-preview.2/Shengyu-Source-0.4.0-preview.2.zip)
- [查看本次发布、截图、中文教程和校验文件](https://github.com/songyi141319/pairup-zh-micsync/releases/tag/v0.4.0-preview.2)
- [旧版本下载与回退](https://github.com/songyi141319/pairup-zh-micsync/releases/tag/v0.3.6-zh.20261002-preview.2)

适用于 Windows 10 / 11 的 x64 电脑。安装包已自带运行所需的 .NET 组件，双击下载的 `Shengyu-Setup-…-win-x64.exe`，按中文向导安装，再从桌面或开始菜单打开“声屿 · 多音箱音乐空间”。不需要编译源码或安装开发工具。

安装到当前用户，可创建快捷方式，也可以从 Windows“设置 → 应用”卸载。详细步骤见 [安装与卸载说明](docs/INSTALL.zh-CN.md)。本版未进行数字签名，Windows 可能显示“未知发布者”。

便携包完整解压后双击 `PairUp.App.exe` 即可，请保留旁边的 `Web` 文件夹。源码包单独下载。GitHub 的绿色 Code → Download ZIP 只下载源码，需要现成程序请使用上面的下载入口。

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

1. 安装上面的 EXE 安装包，或解压免安装完整包；先连接音箱和独立麦克风。
2. 在 Windows 里选好主音源，再打开 PairUp。当前音源自己直接播放，其余音箱在 PairUp 中打开。
3. 按设备实际声音确认 AUX 对应项。原电脑的 AUX 叫“耳机（2- Realtek(R) Audio）”；其他电脑名称可能不同。
4. 先用较低音量确认每台都有声音，然后调延迟。需要麦克风测量时，按 [使用说明](docs/MIC-SYNC.zh-CN.md) 操作。
5. 换音源后正常退出并重新打开 PairUp。右上角关闭按钮可能只是收起到托盘，彻底退出请用托盘菜单。

配置保存在 `%APPDATA%\PairUp\settings.json`。新电脑的设备标识和声音延迟会变化，需要重新选设备和调节；旧电脑的数值只能作为参考。原版和增强版使用同一个配置位置，请只运行其中一个。

**开发预览版请使用本页下载链接更新。0.4.0 的“检查更新”已指向本增强版仓库，但自动检查可能不显示预览版；旧的 0.3.6 下载包仍可能指向上游。**

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

原项目及本仓库自有修改遵循 [MIT](LICENSE)，保留原作者版权。第三方组件分别遵循其许可证：React Bits Strands 为 **MIT + Commons Clause**，包含额外的商业销售限制，并非无附加条件的 MIT。完整声明见 [第三方声明](THIRD-PARTY-NOTICES.md) 与 [licenses](licenses)。
