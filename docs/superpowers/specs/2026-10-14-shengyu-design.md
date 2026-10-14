# 声屿：音乐音场与玻璃界面

## 用户目标
主要听音乐，先做通用预设；多蓝牙/有线音箱可以按位置及频率分工。重做中文布局，沿用共享 iOS27 主题原始组件、鼠标边缘光、自适应文字、三档透感、DIY。保留波浪，增加可关闭的彩色氛围光。提供摆放教程、真实截图与测试证据。此前要求暂不替换正在运行的程序。

后续明确：停止操控用户电脑，不再操作桌面、鼠标、键盘或打开可见窗口；继续后台源码及无窗口检查。波形改用 React Bits Strands，用户给定的全部 18 项参数进入 DIY，默认值逐项照用。标志/图标按 apple-design 重新设计；正式设备名称必须完整显示。

## 已确认环境与设计选择
现有 .NET 8 WPF / NAudio 2.2.1，Windows x64。采用 WebView2 承载离线玻璃页面，复用源组件与适配器；原 WPF 控制层、同步/音量/校准保留。页面不可导航外部或调用任意本机命令。WebView2 不可用时展示原生界面及说明。

产品中文名由本次设计选定为“声屿”，署名和许可证保留 PairUp / Jamim Mehdi。四个页面：播放、音场、摆放指南、外观。播放页保留设备开关/音量/精确延迟/均衡器/收藏/访客分享/校准/更新等现有入口。音场图从听音位置看前后左右；用途预设只改声音分工，不自动开关设备或改变系统音量、同步延迟。

## 音频约定
这是立体声音乐的矩阵扩展和频率分工，不声称杜比全景声解码或独立原生 5.1。原音源直接由 Windows 播放，不能由转发链处理；显示“主音源 · 原声”，其音场控件禁用。其他输出支持 FullRange / FrontLeft / FrontRight / Center / SurroundLeft / SurroundRight / Bass，配置高低截止频率和负增益。位置与音色是独立概念；不根据蓝牙设备名猜低音能力。

音场处理在输出声道转换之前，保留左右信息；旁路时与原输出一致。参数按音频读取边界原子切换，校准临时旁路新音效以免低通滤掉测试音。旧设置缺字段时原声，不改变旧配置。失败/取消仍保留旧延迟。

矩阵规则：前左 L，前右 R，中置/低音 (L+R)/2，左环绕 0.65L-0.35R，右环绕 0.65R-0.35L，分工结果复制到设备双声道；原声保留 L/R。系数绝对和不超过1，后环绕保留小量共同内容，单声道输入等价 L=R，避免完全静音。中置不是人声分离。超过双声道明确拒绝，不能把未知声道顺序当作立体声。

## 界面协议（宿主与网页唯一接口）
网页用 `window.chrome.webview.postMessage({action,id,value,...})`，宿主仅处理白名单动作。状态通过 `window.shengyuReceive(state)`；state 包含 demo, sourceName, sourceId, masterVolume, isSourceMuted, status, devices[]。device 包含 id,name,kind,isConnected,isSystemDefault,volume,latencyMs,bassBoost,treble,isMono,isFavorite,syncStatusText,syncDetailsText,spatial（role,lowCutHz,highCutHz,trimDb）。数值校验及校准期间修改限制在宿主执行。

动作：ready, master(value), mute(value), device(id,field,value)（isConnected/volume/latencyMs/bassBoost/treble/isMono/isFavorite），spatial(id,role,lowCutHz,highCutHz,trimDb)，calibrate，refresh，guest，update，legacy，minimize，maximize，close，exit。预设批量用 spatialBatch(items[])，先整体校验再应用；只对输出设备。

## 验证边界
新增离线音频信号测试，不播放音频。`--ui-preview` 使用演示数据，不枚举/操作设备、不启动访客服务/更新、不读取真实设置；使用独立外观存储。实际 WebView2 窗口亮暗 × 三档截图，网页交互/缩放/键盘/减少动态/高对比检查。真实蓝牙听感仍需用户试听；本轮不替换现用程序、不擅自播放测试音。
