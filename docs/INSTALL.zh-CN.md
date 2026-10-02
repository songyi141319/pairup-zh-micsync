# 安装、使用与卸载

## 下载哪一个

普通使用者推荐 [下载中文 EXE 安装包](https://github.com/songyi141319/pairup-zh-micsync/releases/download/v0.3.6-zh.20261002-preview.2/PairUp-Chinese-Setup-0.3.6-zh.20261002-preview.2-win-x64.exe)。适用于 Windows 10 / 11 的 x64 电脑，已包含运行组件，不需要自己编译或另装 .NET。

需要免安装或保留源码时，可在 [发布页](https://github.com/songyi141319/pairup-zh-micsync/releases/tag/v0.3.6-zh.20261002-preview.2) 下载完整 ZIP 包。`PairUp.App.exe` 是免安装主程序；名字包含 `Setup` 的 EXE 才是安装向导。

## 安装

1. 如已打开 PairUp，请先等校准结束，再在系统托盘菜单选择退出。只关窗口可能仍在后台运行。
2. 双击下载的 `PairUp-Chinese-Setup-0.3.6-zh.20261002-preview.2-win-x64.exe`。
3. 阅读预览版说明和原作者 MIT 许可证，按中文向导安装。默认安装到 `%LOCALAPPDATA%\Programs\PairUp-Chinese`，不需要管理员权限。
4. 桌面快捷方式默认勾选，也会添加开始菜单入口。完成后从桌面或开始菜单打开“PairUp 中文增强版”。

安装包尚未进行数字签名，Windows 可能显示“未知发布者”。请使用本仓库发布页的文件；不需要关闭系统安全保护。

## 初次使用

先连接好音箱，在 Windows 中选好主音源，再打开程序。主音源直接播放，其他音箱通过程序的开关启用。先用较低音量确认出声，再对先响的音箱增加延迟。

新电脑需要重新选设备、调延迟；不同电脑上的设备名称和延迟可能不同。完整多音箱麦克风校准、长时间同步和其他电脑运行仍待实测。本版是开发预览，相关限制见 [当前状态](PROJECT-STATUS.zh-CN.md)。

## 升级与卸载

- 更新增强版请到本仓库发布页下载。程序内“检查更新”仍连接上游原版，可能替换增强功能。
- 安装或卸载前从托盘菜单正常退出程序；安装包不会强制结束音频程序。
- 在 Windows“设置 → 应用”中找到“PairUp 中文增强版”即可卸载。音量和延迟配置保留在 `%APPDATA%\PairUp\settings.json`。
- 增强版与原版的程序安装目录、卸载记录和快捷方式分开；两者仍共用上述配置文件，请只运行其中一个。

## 原作者与许可

基于 [Jamim Mehdi 的 PairUp v0.3.6](https://github.com/jamimmehdi/pair-up-audio)，保留原作者版权和 MIT 许可证。中文增强版由 [songyi141319](https://github.com/songyi141319) 保存和维护。安装目录内随附 `LICENSE`、`CREDITS.md` 和第三方许可证。
