# 蓝光模式（单层 BD-R）

蓝光实现位于 `src/DiscImageStudio.BluRay`，引用介质无关的 Core 和 `src/DiscImageStudio.Iso`，不依赖 CD/DVD 编码器。依据 ISO/IEC 30190:2021，当前范围为 **120 mm、单层 25 GB BD-R**；不支持双层、BDXL、BD-RE 或视频蓝光制作。

## 在应用中的位置

蓝光不是第六个工作区，而是工程的第三种盘型（CD / DVD / BD）。打开 BD 工程后：

- **设计**：画布与合成图按 BD 生成外半径布局；右栏影调模块支持 2 级、sRGB 线性化、亮度与 3T/6T 极性。
- **预览**：设计投影按生成/实测几何、影调与极性渲染 3T/6T 类别图；成品读回直接分类 ISO 的 AA/44 负载字节，不套用极性，显示的是类别图而不是反射亮度。
- **导出**：生成完整 BD ISO（纯绘图或 UDF 2.01 内圈文件），并在同目录写出 `<iso>.json` sidecar。
- **刻录**：把同一 ISO 流直接送入 MMC 顺序写入，不保存完整镜像；任务坞支持进度与取消。
- **标定**：BD 目标覆盖完整数据区，保存扇区、半径、极性、绘图起止与 ISO 时间戳；拟合使用恒定轨距并回到 BD 参数。

BD 预设与 CD/DVD 一起存放在 `disc-presets.json`（schema 4）的 `bluRayPresets` 中。内置“BD-R 单层 25 GB”和“铼德 BD-R 10x”两条；用户预设可在工程设置对话框底部保存。

## 容量与几何

- 单层容量 12,219,392 个 2048-byte 用户扇区，即 25,025,314,816 bytes；32 扇区组成 65,536-byte cluster。
- 工程分区的扇区数必须是 32 的正倍数且不超过单层容量；外半径不超过 60 mm。
- 生成与刻录共用总扇区数；刻录始终声明完整盘容量，绘图区外的扇区为零负载。
- 名义轨距 0.320 µm，通道位长 74.5 nm，起始角 0°。

## 生成与 ISO

`BluRayIsoGenerator` 先规划 UDF 2.01 目录和文件 extent，再把文件末尾向上对齐到 32 扇区。盘尾保留备份卷描述符与锚点，中间范围用于图片。输出长度固定为 `TotalSectors × 2048`。

- 未选择内圈文件夹：完整数据区纯绘图，不含文件系统。该 `.iso` 可由本程序回读，但普通文件系统工具不能浏览。
- 选择内圈文件夹：写入 UDF 2.01，支持大于 4 GiB 的单文件；内圈文件、填充、图案与盘尾元数据在同一前向流中写出。
- 输出路径必须在源文件夹之外，且不能覆盖源图片。取消或失败保留已有 ISO。
- 生成成功后写出 `<iso>.json`（`format: bd-r-image-v1`）：总扇区、内外半径、绘图起止与半径、3T/6T 极性、`ToneProfile`、卷标、UDF 时间戳、源图与生成时间。预览选中该 ISO 时自动恢复这些参数；没有 sidecar 的外部 ISO 仍可按当前工程几何读回。

## 影调与极性

BD 的影调在源图分辨率一次性完成：sRGB 线性化 → 亮度偏移 → 字节级抖动 → 128 阈值二值化。生成、刻录和预览共用这张二值栅格，不对 250 亿量级的 payload 字节逐个哈希。

`BluRayRunLengthMapping` 只做最后一步：暗部写偏 3T 数量的 `AA`，亮部写偏 6T 数量的 `44`；`InvertMapping` 交换两端。它是图像映射约定，不承诺纯 3T/6T，也不代表实测光学极性。实际明暗方向需通过标定盘观察；极性字段独立于 `ToneProfile` 保存。

## 命令行

```powershell
# 查看单层盘参数，或导出 JSON
dotnet run --project src/DiscImageStudio.App -- bd-info --output bd-parameters.json

# 纯绘图 ISO（1024 扇区仅用于快速功能验证，不是实际整盘配置）
dotnet run --project src/DiscImageStudio.App -- bd-generate --input image.png --output bd-test.iso --total-sectors 1024 --invert true

# 含 UDF 内圈文件的完整容量 ISO
dotnet run --project src/DiscImageStudio.App -- bd-generate --input image.png --output bd-image.iso --data-dir C:\Files --volume-label BD_IMAGE

# 影调：抖动、亮度、sRGB 线性化
dotnet run --project src/DiscImageStudio.App -- bd-generate --input image.png --output bd-tone.iso --tone-dither 0.75 --tone-gamma true --tone-brightness -4

# 原始负载探针与单个 ECC 簇诊断
dotnet run --project src/DiscImageStudio.App -- bd-probe --byte 0xA6 --sectors 32 --output bd-probe.bin
dotnet run --project src/DiscImageStudio.App -- bd-encode --input bd-probe.bin --psn 0x100000 --output bd-ecc.bin
```

`bd-generate` 的 `--preset` 可选 `bd-r-sl-25gb` 或 `bd-r-ritek-10x`；`--total-sectors`、`--r0`、`--r1`、`--start-angle` 可覆盖对应值。`--invert` 接受 `true`/`false`。`bd-generate` 不再接受旧的 `--start-sector` / `--sectors` 切片参数；切片仅保留在 `bd-probe`。

`bd-probe` 的 `--byte` 为 0..255 或 `0x00`..`0xFF`，范围按 32 对齐。`bd-encode` 输入必须恰好 65,536 bytes，输出 76,880 bytes 的 ECC cluster，不含 17PP、DC-control 或文件系统，不能当作可刻录镜像。

## 编码链

| 部分 | ISO/IEC 30190:2021 |
| --- | --- |
| 2048-byte payload + 4-byte EDC | §13.2–13.3 |
| 16-bit LFSR，PSN bits 19..5 派生种子；每个 data frame 复位 | §13.4 |
| LDC RS(248,216)，304 个码字及交织 | §13.5–13.8 |
| 地址、RS(9,5)、BIS RS(62,30) 及复用 | §13.9–13.14 |
| 17PP、DC-control 与同步帧 | §13.15–13.17 |
| Recording unit spacing | §14 |
| 单层 PSN 0x00100000..0x00CA73FF | §17 |

`BluRayRecordingFrameEncoder` 接受调用者明确提供的 DC 控制位；`BluRayGreedyDcControlEncoder` 是前视贪心策略，用于实验，不保证标准要求的最终 DC 最优性，也不承诺与刻录机实际比特序列一致。

## 刻录

BD 使用未格式化单层顺序 BD-R 的无备用区写入通道，按 64 KiB 对齐的 WRITE(10) 下发。仅支持空白 BD-R；中断后同一盘片通常不能继续写入。BD 速度换算以 1× = 4.5 MB/s 计算，自动模式仍选择当前盘片报告的最低配置。

## 验证范围

自动检查覆盖 17PP/EDC/Reed-Solomon、完整 RUB 重放、图像映射、并行生成、ISO/UDF、大于 4 GiB 文件、顺序刻录命令、绘图范围、sidecar 与取消。测试不访问真实刻录设备，也不证明实际盘面的可见亮度或极性。
