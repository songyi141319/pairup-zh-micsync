# 构建与继续开发

环境：Windows、.NET 8 SDK。现成下载包使用 Windows x64，自带 .NET。源码项目为 WPF/C#，即 Windows 桌面程序；0.4.0 使用 Microsoft Edge WebView2 显示离线玻璃界面，缺少此显示组件时回退原版界面。

## 获取源码

```powershell
git clone https://github.com/songyi141319/pairup-zh-micsync.git
cd pairup-zh-micsync
```

也可以解压旧完整包里的 `source` 文件夹，或本轮 `Shengyu-Source-0.4.0-preview.2.zip` 里的 `Shengyu-Source` 文件夹；从源码根目录开始执行下面的命令。发布标记 v0.4.0-preview.2 对应本轮声屿功能；旧版本保留在发布历史中。

## 构建

```powershell
dotnet restore .\PairUp.sln
dotnet build .\src\PairUp.App\PairUp.App.csproj -c Release
```

首次还原依赖需要联网。主要依赖锁定为 NAudio 2.2.1、QRCoder 1.6.0、Microsoft.Web.WebView2 1.0.4258.31；.NET 运行组件为 8.0.31。波形的源码和固定依赖见 `tools/StrandsBuild`，预编译后的离线网页资源已随源码保存。

## 不出声的软件检查

```powershell
dotnet run --project .\tools\SyncChecks\SyncChecks.csproj -c Release
dotnet run --project .\tools\SpatialChecks\SpatialChecks.csproj -c Release
dotnet run --project .\tools\BridgeChecks\BridgeChecks.csproj -c Release
node .\src\PairUp.App\Web\checks.cjs
```

同步检查基线为 35 项、音场信号检查 29 项。桥接与网页检查分别覆盖输入校验和界面基础行为。这些检查不能代替真实麦克风测量。

后台渲染检查需 Playwright、Sharp 和 Chromium。设置 `SHENGYU_BROWSER_PATH` 为后台 Chromium 程序后，依次执行 `node tools/UiChecks/headless-ui.cjs <结果目录>`、`lifecycle-checks.cjs`、`graphics-checks.cjs` 和 `layout-checks.cjs`（均位于同一目录）。它们固定使用无窗口模式，不打开音频设备；布局检查还会测量波形/球体截图的实际像素。

若正在运行从此源码目录启动的预览程序，请对 `dotnet build` 指定另外的 `-p:OutputPath=...`，再运行该目录生成的检查 DLL，避免覆盖正在使用的文件。不要为了构建强行结束用户正在使用的声屿。

## 重新构建彩色波形

已经随源码包含可直接分发的离线波形包。仅修改波形适配代码时才需要以下步骤：

```powershell
cd .\tools\StrandsBuild
npm.cmd ci
npm.cmd run build
```

需要 Node.js。依赖版本由 `package-lock.json` 固定，首次安装依赖需要联网。原始组件和三个绘制程序均核对逐字节哈希；固定上游文件已在 `.gitattributes` 中禁止换行转换，其他 Windows 电脑无需修改全局设置。

只改参数不必重新构建，在程序“外观 → 彩色波形”内调整即可。Strands 使用 MIT + Commons Clause，来源和完整许可见 `Web/strands/SOURCE-NOTICE.md` 及 `vendor/LICENSE-react-bits.md`。

## 生成程序与离线界面

```powershell
dotnet publish .\src\PairUp.App\PairUp.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o .\publish
```

结果在 `publish\PairUp.App.exe`，并附有 `publish\Web`。必须一起分发，不可只复制 exe。音频程序和 .NET 仍打包到 exe，WebView2 由系统提供。同一项目的构建、检查、发布应依次执行，避免同时修改构建中间文件。

## 生成中文 EXE 安装包

准备 [Inno Setup](https://jrsoftware.org/isinfo.php) 编译工具；本次固定使用 6.7.3，官方文件来源为 [is-6_7_3](https://github.com/jrsoftware/issrc/releases/tag/is-6_7_3)。在项目根目录执行：

```powershell
.\installer\build-installer.ps1
```

脚本先生成包含 .NET 运行组件的程序与离线 Web 目录，再编译安装包。结果在 `installer\Output\Shengyu-Setup-0.4.0-preview.2-win-x64.exe`。构建过程不会打开声屿，也不会执行生成的安装包。

若编译工具不在默认安装目录，用 `-IsccPath` 指定 `ISCC.exe`。如已准备好自包含程序，可直接打包，避免重新构建程序：

```powershell
.\installer\build-installer.ps1 -BinaryPath .\publish\PairUp.App.exe -IsccPath 'C:\Tools\Inno Setup 6\ISCC.exe'
```

也可以提供 `-ExpectedBinarySha256` 核对指定程序，`-DotNetPath` 指定 .NET 工具路径。发布新版本时同时指定 `-Version`（显示及文件名版本）和 `-WindowsVersion`（四段数字版本）。

指定 `-BinaryPath` 时会使用同目录的 `Web` 文件夹，也可用 `-WebRoot` 明确指定与该程序匹配的网页目录。安装包源码、中文语言文件和安装说明保存在 `installer`，Inno Setup 的许可证保存在 `licenses`。

安装包有独立的增强版名称、安装位置和卸载登记，不强制结束正在运行的音频程序。升级或卸载前，使用者需要正常退出 PairUp。安装完成页的“立即打开”默认不勾选；静默安装也不会自动启动程序。

## 文件与设置

- 运行配置：`%APPDATA%\PairUp\settings.json`，包含本机设备标识。新电脑应重新选择设备。
- 先用托盘菜单正常退出旧程序，再替换文件。校准期间退出必须等到程序恢复音量与静音状态。
- 日常麦克风校准使用主界面按钮。`tools/AcousticProbeCheck` 会播放测试音并录音。公开版已移除本机麦克风标识，测量时必须通过 `--microphone` 提供当前电脑的设备标识；没有参数时只显示用法，不会打开麦克风。
- `tools/AcousticObserver` 只做参考声音的内存统计。`AudioDiag` 是历史诊断工具，其按名称分类的说明不代表现版实际缓冲策略。
- `docs/index.html`、`README.upstream.md` 和 `docs/screenshots` 保留自上游，可能描述原版功能；当前行为以根目录中文 README 和本次交接文档为准。

## 继续工作前

先读 `docs/PROJECT-STATUS.zh-CN.md`，核实真实设备状态。不要运行诊断声音或麦克风测试，直到用户明确表示方便。不要把“所有软件检查通过”写成“实体音箱已经同步”。

