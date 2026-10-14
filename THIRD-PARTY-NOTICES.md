# 第三方组件说明

声屿新增的离线玻璃界面直接复用用户提供的工作台 iOS27 主题固定源码。原折射组件为 Sam Asante 的 liquid-glass 0.1.1，固定提交 4e7b769e1df7e5a7d3669fef22417fe3d2f79ade；React、React DOM 与 Scheduler 一并包含在离线运行脚本中。源码、来源记录、各许可文本保留在 `src/PairUp.App/Web/glass`（发布目录 `Web/glass`）。iOS27 是此主题的名称。

界面宿主使用 Microsoft.Web.WebView2 SDK 1.0.4258.31，许可及声明见 `licenses/Microsoft-WebView2-*`。需要电脑已安装微软 Edge WebView2 Runtime；缺少时保留原生界面。安装包不从网络自动下载组件。

原 PairUp 及本项目自有代码依照根目录 LICENSE 的 MIT 条款提供。第三方目录按各自许可证提供，不能把它们全部视为 MIT。自包含程序还使用下列组件，相关版权与许可属于各原作者：

- NAudio 2.2.1，Mark Heath 与贡献者：https://github.com/naudio/NAudio 。许可原文见 `licenses/NAudio-2.2.1.txt`。
- React Bits / David Haz 的 Strands 动画，固定源码提交 `496968c4117e21278f13917201b7d615bc5e7450`，来源 https://github.com/DavidHDev/react-bits 。使用 MIT + Commons Clause 条款，作为声屿应用的一部分分发。许可全文、未改动的原源码与适配说明保留在 `src/PairUp.App/Web/strands`，发布时为 `Web/strands`；这不授予将组件本身单独出售、再许可或作为组件包再分发的权利。其 OGL 和 React 依赖许可证一并保留。
- QRCoder 1.6.0，Raffael Herrmann：https://github.com/codebude/QRCoder 。许可原文见 `licenses/QRCoder-1.6.0.txt`。
- Microsoft .NET 8.0.31（包括桌面和 ASP.NET Core 运行组件）及 Microsoft 的相关依赖。对应许可和第三方声明保存在 `licenses` 中。
- EXE 安装包由 Inno Setup 6.7.3 构建，原作者为 Jordan Russell 和 Martijn Laan；项目为 https://github.com/jrsoftware/issrc 。许可原文见 `licenses/Inno-Setup-6.7.3-LICENSE.TXT`。
- 中文安装界面使用 Inno Setup 源码仓库 `is-6_7_3` 中的社区简体中文翻译，维护者为 Zhenghan Yang。原文件及署名完整保留在 `installer/Languages/ChineseSimplified.isl`，来源说明见 `installer/Languages/README.md`。

这些文件用于保留随程序分发的声明，不改变各组件的原有许可。原 PairUp 作者与第三方作者未为本增强版本的校准效果背书。
