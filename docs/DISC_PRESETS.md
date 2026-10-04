# 可编辑盘片预设

盘片预设分为两层：

- 内置层随应用发布和更新，保存官方预设。
- 用户层位于以下文件，只保存用户新增、覆盖和禁用设置：

```text
%LOCALAPPDATA%\DiscImageStudio\disc-presets.json
```

程序第一次启动时会在用户层放入“自定义 CD 参数”和“自定义 DVD 参数”两个可编辑预设。内置预设不会复制到用户文件，因此应用更新后的参数修正和新增预设会自动生效。

在 CD 或 DVD 生成页点击“打开 JSON”即可用系统默认编辑器打开用户层。保存后回到程序点击“重新加载”，无需重启。

## 合并规则

- 用户预设使用新 `id` 时，会追加到内置预设之后。
- 用户预设使用与内置预设相同的 `id` 时，会完整覆盖该内置预设。
- `disabledCdPresetIds` 和 `disabledDvdPresetIds` 可以隐藏不需要的内置或用户预设。
- 内置预设更新时，未被同 ID 用户项覆盖的参数会自动更新。

## 添加 DVD 预设

在 `dvdPresets` 数组中复制初始化的用户对象，修改 `id`、名称和参数。例如：

```json
{
  "id": "verbatim-dvd-r-azo-43533",
  "displayName": "Verbatim DVD-R AZO (43533)（实测）",
  "description": "Verbatim DVD-R AZO 43533 已验证标定参数：23.9914606277–57.9648936579 mm，共 2,297,888 个扇区；采用三次轨距分布。",
  "totalSectors": 2297888,
  "innerRadiusMm": 23.991460627731737,
  "outerRadiusMm": 57.964893657925366,
  "channelBitLengthNm": 133.3,
  "pitchLinear": -3.9256854319227684E-05,
  "pitchQuadratic": 6.964795915429256E-05,
  "pitchCubic": -4.670918950137523E-05
}
```

DVD 的 `totalSectors` 必须大于 0 且是 16 的倍数，以满足 ECC Block 和流式刻录布局要求。`channelBitLengthNm` 见下节，属于保留字段。

此 Verbatim DVD 预设来自用户确认已验证的 `calibration-f2f851f7.calibration.json`，保留记录中的完整参数精度。

`pitchLinear`、`pitchQuadratic`、`pitchCubic` 是三次轨距的三个相对形状系数，缺省为 0，兼容旧预设。模型为 `p(x)=p0*(1+pitchLinear*x+pitchQuadratic*x²+pitchCubic*x³)`，`x=(r-innerRadiusMm)/(outerRadiusMm-innerRadiusMm)`。完整多项式有四项；`p0` 根据总轨长自动计算，界面显示四项的实际 μm 数值。整个 `0≤x≤1` 区间内轨距必须严格为正，不能仅检查内外边界。修改任一系数会切换到未保存的自定义状态，重新选择内置等轨距预设会恢复零系数。

## 添加 CD 预设

在 `cdPresets` 数组中复制一个现有对象，修改 `id`、名称、扇区数和内外半径。例如：

```json
{
  "id": "cd-74",
  "displayName": "CD-R/RW 74 分钟（650 MB）",
  "description": "适合标称 74 分钟的 120 mm 空白 CD。",
  "sectors": 333000,
  "innerRadiusMm": 24.5,
  "outerRadiusMm": 56.8,
  "linearVelocityMmPerSecond": 1200
}
```

## 固定参数与保留字段

扫描速度、起始角、图片外半径与 DVD channel bit 长度是方案固定的物理量，界面不提供对应表单，程序生成时一律采用 1200 mm/s（ECMA-130）、0°、57.5 mm 与 133.3 nm。

它们在 JSON 中仍作为字段保留，原因是在旧版的配置文件中这几个参数是必要的，因此需要保证旧版本写出的用户文件可以直接使用。这两个值不参与生成计算。

## i18n 兼容字段

格式已经预留可选的 `fallbackLanguage`、`displayNameResourceKey` 和 `descriptionResourceKey`。当前版本仍直接显示 `displayName` 与 `description`；以后接入多语言资源时会优先解析资源键，并以现有文本作为回退，因此不需要再次升级 JSON 结构。用户预设无需填写资源键。

## 编辑规则

- 保留顶层的 `"schemaVersion": 2`、`cdPresets`、`dvdPresets`、`disabledCdPresetIds` 和 `disabledDvdPresetIds`。程序也接受临时版本的 `"schemaVersion": 3`，缺少保留字段时按上面的标准值补齐。
- 每组预设中的 `id` 必须唯一；`__manual__` 是界面“未保存的自定义参数”状态的保留 ID，不能写进 JSON。
- 只能填写模板中列出的生成参数；其他字段会被拒绝，避免把图片布局或校准参数混入盘片预设。
- 半径、`linearVelocityMmPerSecond` 和 `channelBitLengthNm` 必须是大于 0 的有限数值，生成外半径必须大于生成内半径。
- 文件允许 `//` 或 `/* ... */` 注释以及数组末尾的逗号。
- 如果格式或参数无效，程序会指出问题并继续保留上一次成功加载的预设，不会自动覆盖用户文件。
