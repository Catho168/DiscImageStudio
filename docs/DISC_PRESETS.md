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
  "description": "Verbatim DVD-R AZO 43533 实测数据区：23.9968875–57.9779875 mm，共 2,297,888 个扇区。",
  "totalSectors": 2297888,
  "innerRadiusMm": 23.9968875,
  "outerRadiusMm": 57.9779875,
  "channelBitLengthNm": 133.33
}
```

DVD 的 `totalSectors` 必须大于 0 且是 16 的倍数，以满足 ECC Block 和流式刻录布局要求。

## 添加 CD 预设

在 `cdPresets` 数组中复制一个现有对象。CD 预设只包含容量、生成内外半径和生成时使用的 `linearVelocityMmPerSecond`。

图片映射画布、实测半径、实测线速度和起始角属于图片布局或校准设置，不属于盘片预设；选择或修改预设不会覆盖这些独立设置。

## i18n 兼容字段

格式已经预留可选的 `fallbackLanguage`、`displayNameResourceKey` 和 `descriptionResourceKey`。当前版本仍直接显示 `displayName` 与 `description`；以后接入多语言资源时会优先解析资源键，并以现有文本作为回退，因此不需要再次升级 JSON 结构。用户预设无需填写资源键。

## 编辑规则

- 保留顶层的 `"schemaVersion": 2`、`cdPresets`、`dvdPresets`、`disabledCdPresetIds` 和 `disabledDvdPresetIds`。
- 每组预设中的 `id` 必须唯一；`__manual__` 是界面“未保存的自定义参数”状态的保留 ID，不能写进 JSON。
- 除上述可选 i18n 字段外，只能填写模板中列出的生成参数；其他字段会被拒绝，避免把图片布局或校准参数混入盘片预设。
- 半径、线速度和 channel bit 必须是大于 0 的有限数值，生成外半径必须大于生成内半径。
- 文件允许 `//` 或 `/* ... */` 注释以及数组末尾的逗号。
- 如果格式或参数无效，程序会指出问题并继续保留上一次成功加载的预设，不会自动覆盖用户文件。
