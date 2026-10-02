# 构建与继续开发

环境：Windows、.NET 8 SDK。现成下载包使用 Windows x64，自包含发布，无需另装运行环境。源码项目为 WPF/C#，即 Windows 桌面程序。

## 获取源码

```powershell
git clone https://github.com/songyi141319/pairup-zh-micsync.git
cd pairup-zh-micsync
```

也可以解压完整包里的 `source` 文件夹；从这里开始执行下面的命令。

## 构建

```powershell
dotnet restore .\PairUp.sln
dotnet build .\src\PairUp.App\PairUp.App.csproj -c Release
```

首次还原依赖需要联网。主要依赖锁定为 NAudio 2.2.1、QRCoder 1.6.0；发布时使用的 SDK 为 8.0.425，运行组件为 8.0.31。

## 不出声的软件检查

```powershell
dotnet run --project .\tools\SyncChecks\SyncChecks.csproj -c Release
```

当前基线为 35 项通过。这些检查不能代替真实麦克风测量。

## 生成单文件程序

```powershell
dotnet publish .\src\PairUp.App\PairUp.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o .\publish
```

结果在 `publish\PairUp.App.exe`。同一项目的构建、检查、发布应依次执行，避免同时修改构建中间文件。

需要安装包时可以参考原项目 `installer`，但本次发布使用免安装解压包；旧安装脚本中的版本号和上游地址需要先复核。

## 文件与设置

- 运行配置：`%APPDATA%\PairUp\settings.json`，包含本机设备标识。新电脑应重新选择设备。
- 先用托盘菜单正常退出旧程序，再替换文件。校准期间退出必须等到程序恢复音量与静音状态。
- 日常麦克风校准使用主界面按钮。`tools/AcousticProbeCheck` 会播放测试音并录音。公开版已移除本机麦克风标识，测量时必须通过 `--microphone` 提供当前电脑的设备标识；没有参数时只显示用法，不会打开麦克风。
- `tools/AcousticObserver` 只做参考声音的内存统计。`AudioDiag` 是历史诊断工具，其按名称分类的说明不代表现版实际缓冲策略。
- `docs/index.html`、`README.upstream.md` 和 `docs/screenshots` 保留自上游，可能描述原版功能；当前行为以根目录中文 README 和本次交接文档为准。

## 继续工作前

先读 `docs/PROJECT-STATUS.zh-CN.md`，核实真实设备状态。不要运行诊断声音或麦克风测试，直到用户明确表示方便。不要把“所有软件检查通过”写成“实体音箱已经同步”。

