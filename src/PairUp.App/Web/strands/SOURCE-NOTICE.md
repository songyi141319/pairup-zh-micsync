# 声屿彩色波形：来源、许可与适配

## 原始组件

- 组件：React Bits / Strands
- 作者：David Haz
- 仓库：https://github.com/DavidHDev/react-bits
- 固定提交：`496968c4117e21278f13917201b7d615bc5e7450`
- 原始 JavaScript / CSS 文件：`vendor/Strands.jsx`、`vendor/Strands.css`
- 参数范围依据：同一提交的 `vendor/StrandsDemo.reference.jsx`
- 完整许可：`vendor/LICENSE-react-bits.md`

原始组件采用 **MIT + Commons Clause**，不能笼统称为不附加条件的 MIT 组件。许可允许其作为应用、网站或产品的一部分使用和分发，并对组件本身的销售、再许可和再分发作出限制。以随附全文为准。声屿将其用于完整音乐应用中的装饰波形，没有把组件本身作为产品出售。

## 原文保留与本地构建

`vendor/` 中的上游原始文件保持不变。`tools/StrandsBuild/build.mjs` 仅在忽略的 `.generated/` 目录生成适配副本。构建前后对 VERT、FRAG、GLASS_FRAG 三个原始着色器计算 SHA-256，任何一个不一致都会停止构建。原始调色、混合、参数更新和绘制顺序保留。`build-manifest.json` 记录源码和三个着色器的哈希。

生命周期与画布布局适配包括：

1. 增加可选静态帧，响应减少动态、关闭柔和动态或速度为零。
2. 页面隐藏、切离当前页面、装饰区离开可见区域时卸载，返回后恢复。
3. 监听容器大小变化，避免以零宽高创建绘制尺寸。
4. 卸载时取消动画帧、移除观察器与事件、移除画布并释放图形上下文。
5. 开始绘制之前明确确认 WebGL 2 可用，并分别检查波形与玻璃球两个绘制程序成功链接。原组件着色器需要 WebGL 2，不允许静默回退到 WebGL 1。
6. 初始化失败时删除已分配的程序、着色器、几何缓冲、纹理与帧缓冲，并尝试释放图形上下文；错误交给显示层统一降级。即使错误在挂载过程中被捕获，也不会登记为活动实例。
7. 波形采用固定 1.7∶1 的逻辑画布，再用 CSS 映射到用户选择的显示宽高。玻璃球的逻辑画布与显示区域都保持 1∶1。原始着色器、颜色与 18 项配方值不改动。

始终最多一个 Strands 实例。播放页和外观调节预览共享同一份偏好，不会同时保留两张画布。原液态玻璃渲染器独立保留，导航及浮层最多占两个折射表面，给可选波形玻璃球预留一个，总计最多三个活动折射表面。图形环境不支持时显示静态说明，不生成替代假波形。

降级状态为 `unavailable`，活动实例数为 0；不启动持续动画帧，也不把绘制失败报告成 `animated`。这里只判断图形能力，不能据此证明音频链路或真实设备正常。WebGL 2 不可用和绘制程序链接失败的实际反例由后台浏览器检查覆盖。

## 依赖与许可证

构建工具版本锁定在 `tools/StrandsBuild/package.json` 和 `package-lock.json`：

| 内容 | 版本 | 许可 |
| --- | --- | --- |
| React | 19.2.0 | MIT，`LICENSE-react.txt` |
| React DOM | 19.2.0 | MIT，`LICENSE-react-dom.txt` |
| Scheduler | 以 package-lock.json 为准 | MIT，`LICENSE-scheduler.txt` |
| OGL | 1.0.11 | Unlicense，`LICENSE-ogl.txt`，原文来自该 npm 包 README |
| esbuild，仅用于构建 | 0.25.11 | MIT，依赖包内保留 |

`strands-runtime.js` / `.css` 是可直接离线加载的本地构建产物。不使用 CDN、外部字体、运行时网络下载，也不连接音频、麦克风或真实设备。

重建命令，在 `tools/StrandsBuild` 目录执行：

```powershell
npm.cmd ci --ignore-scripts --no-audit --no-fund
npm.cmd run build
```

## 18 项外观偏好

用户指定默认：`colors=[#F97316,#7C3AED,#06B6D4]`、`count=3`、`speed=0.5`、`amplitude=1`、`waviness=3`、`thickness=0.7`、`glow=2.6`、`taper=0.5`、`spread=1`、`intensity=0.5`、`saturation=2`、`opacity=1`、`scale=2.2`、`glass=false`、`refraction=1`、`dispersion=1`、`glassSize=1`、`hueShift=0`。

数值范围逐项沿用原始演示。支持 0–8 个自选颜色，零颜色启用原组件内置彩虹。`strands-settings.js` 的 18 项独立偏好使用声屿真实或演示存储前缀，不影响 31 项液态玻璃配方或音频设置。

## 独立布局与横向适配

布局由 `strands-layout.js` 保存到当前前缀的 `strands-layout` 键：正式为 `shengyu-app-v1-strands-layout`，演示为 `shengyu-preview-v1-strands-layout`。默认值为 `widthPercent=100`、`heightPx=150`、`align=center`、`sphereSizePx=200`。长度范围 20–100%，波形高度和球形区域范围均为 80–320 像素，数值步长均为 1。

“波形 / 玻璃球”分段按钮继续只使用原配方的 `glass` 字段作为模式状态，布局中不保存第二个模式。切换模式保留各自尺寸。铺满（100%、150）、适中（60%、150）、迷你（30%、100）、玻璃球（200 像素）快捷按钮只修改对应布局尺寸及 `glass` 模式；颜色、速度、波纹、辉光等配方值不变。“恢复布局”只恢复四项布局字段，当前模式也不改变。

原始 `FRAG` 按画布高度归一化横坐标，仅增加容器宽度会增加空白，而不会按比例拉长波形。因此适配器先确定逻辑纵横比：波形 1.7，玻璃球 1；逻辑高度取 `ceil(max(显示高度, 显示宽度 / 逻辑纵横比))`，逻辑宽度为逻辑高度乘该比例后四舍五入。渲染完成后，将画布通过 CSS 映射到显示区域。这样波形实际像素随选择的长度比例变化，同时避免把低分辨率图像横向放大。玻璃球的显示区域使用正方形比例，窄窗口只会等比缩小，不拉成长椭圆。

这种映射属于视口布局适配；仍使用原始三个着色器和原始绘制顺序。`build-manifest.json` 的 `viewportMapping` 明确记录比例，构建继续校验原始源码和三个着色器。小窗口将文字与动效上下排列，不覆盖完整设备名称。

API 为 `window.ShengyuStrandsLayout.get/set/reset/applyPreset`；`window.ShengyuStrandsStatus` 可读取 `displayMode`、`logicalAspectRatio` 和布局副本。布局控件为 `[data-strands-layout=widthPercent|heightPx|sphereSizePx]`，对齐为 `[data-strands-align]`，模式为 `[data-strands-mode]`，快捷尺寸为 `[data-strands-preset]`，恢复按钮为 `#strands-layout-reset`。
