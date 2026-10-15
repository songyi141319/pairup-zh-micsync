# 校准方案离线比较实验

此工具只处理生成的数字数组，不录音、不播放、不操作桌面、不写声屿配置。它不是已通过实体音箱验收的新校准器，正式程序未修改。

## 比较对象

- 正式 C# `AcousticDelayEstimator.Find`：直接调用原实现，没有移植或改门槛。
- GCC-PHAT：直接调用 MIT 的 Pyroomacoustics `sync.correlate(phat=True)`，使用同一 220ms 测试音。
- 短扫频对照：同一上游相关函数、不加 PHAT、同一 220ms 测试音，使用实验判定规则，帮助区分时长影响。它与正式算法的预处理和相关计分仍不同，不能单独隔离规则贡献。
- 长扫频匹配：上游相关函数、不加 PHAT、1s 线性扫频。
- pyfar 指数扫频：实际调用 pyfar 0.7.5 生成 1s 指数扫频并反卷积，使用实验判定规则。

实验候选统一采用峰值相对背景显著性不低于10、主峰外2ms的次峰比例小于0.88。该规则由本项目实验代码实现，不是上游产品的验收标准；它与正式规则不同。候选失真检查使用整段累计样点，正式算法使用滑动窗口，因此不是对正式整条安全链的等效替代。所有候选均未接入自动应用延迟。

14种场景，每种12个固定随机种子；总计168组、5种方法、840条结果。种子还改变已知到达位置。误差不超过2ms计作“测准”；目标不存在时的接受记录为误接受。更强回声的目标真值定义为最早直达声，不是最强声。模拟只覆盖短录音、16kHz，不代表长时间真实蓝牙时钟行为。

峰值相同，但1s测试音比220ms测试音的时长与能量更大，收益不能全归因于算法。单个候选只是数值分析器；尤其“目标静音但有同码参考回声”没有经过正式程序的三次不同间隔核对，不能据此断言正式校准必然会误应用。

## 可复现命令

在仓库根目录，使用 Python 3.12 和 .NET 8。依赖只安装到本地 `artifacts`：

```powershell
$env:TEMP = Join-Path (Get-Location) 'artifacts/test-temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
python -m pip install --target artifacts/calibration-lab-packages -r tools/CalibrationLab/requirements-lock.txt
$env:PYTHONPATH = Join-Path (Get-Location) 'artifacts/calibration-lab-packages'
$env:MPLBACKEND = 'Agg'
$env:MPLCONFIGDIR = Join-Path (Get-Location) 'artifacts/calibration-lab/mpl'
dotnet run --project tools/CalibrationLab/Baseline/Baseline.csproj -c Release -- probe artifacts/calibration-lab
python tools/CalibrationLab/compare.py prepare artifacts/calibration-lab
dotnet run --project tools/CalibrationLab/Baseline/Baseline.csproj -c Release -- evaluate artifacts/calibration-lab
python tools/CalibrationLab/compare.py summarize artifacts/calibration-lab
python tools/CalibrationLab/test_compare.py
```

`prepare` 写生成的测试数组与候选结果，`evaluate` 调用正式算法，`summarize` 校验数量、唯一性后评分。`baseline.json`、`candidates.json`、`results.json`、`summary.csv` 均在输出目录。6项实验工具自检覆盖正延迟的方向与单位、静音、双峰、失真、重复生成一致性与结果完整性；不是6项硬件测试。

上游 `vendor/pyroomacoustics_sync.py` 按 `provenance.json` 中提交固定，保持原样，附MIT许可。pyfar及依赖通过独立目录加载，不进入声屿安装包。Google Audio Sync Kit、Open Sound Meter、REW仅作研究参考，没有复制其实现或声称运行完整软件。
