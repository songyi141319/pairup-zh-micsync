# 原作者、来源与致谢

## 原始工程

- 项目：PairUp / pair-up-audio
- 原作者：Jamim Mehdi（GitHub：jamimmehdi）
- 原始仓库：https://github.com/jamimmehdi/pair-up-audio
- 来源版本：v0.3.6
- 标签对象：98bd6ddd9e24ba8b633e494c4a3308fdb7c6ca92
- 标签指向提交：ce056567d3b44ea0857134fedfd35a2e519f24eb
- 原许可证：MIT，原文见根目录 LICENSE。

公开仓库保留原版权声明、原 README 和原项目截图。原版材料不代表增强版的实际验证结果。

## 衍生版本

本仓库由 songyi141319 保存和维护，中文界面、延迟范围及精细调节、软件积压修复和麦克风校准由 Codex 协助实现。此版本独立于原作者官方发布；未声称获原作者背书。

## 方法参考

麦克风校准参考 [Resonalyze](https://github.com/DIMOSUS/Resonalyze) 的参考声音与实际收音对比思路；本项目的扫频信号、归一化相关识别及延迟存储独立实现，没有复制该项目代码或引入其整套依赖。

声音时间记录参考 [微软 IAudioCaptureClient.GetBuffer 文档](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)。驱动延迟含义参考 [IAudioClient.GetStreamLatency](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-getstreamlatency)。

## 依赖

NAudio、QRCoder 与 .NET 运行组件的声明见 THIRD-PARTY-NOTICES.md 和 licenses 文件夹。
