# 蓝光模式（单层 BD-R）

蓝光实现位于 `src/DiscImageStudio.BluRay`，引用介质无关的 Core 和 ISO 文件系统模块，不依赖 CD/DVD 编码器。依据 ISO/IEC 30190:2021，当前范围为 **120 mm、单层 25 GB BD-R**。暂不考虑双层、BDXL 或视频蓝光制作。

## 当前可用

- 侧边栏“BD”页面：单层预设、命名自定义预设、手动总扇区数、数据区半径、源图布局、内圈文件夹及卷标、镜像输出、流式刻录和辅助标定入口。手动修改后显示“自定义参数（未保存）”；“保存为新预设”会独立保存，之后可从下拉框选择。
- BD 自定义预设保存在 `%LOCALAPPDATA%/DiscImageStudio/blu-ray-presets.json`，不修改 CD/DVD 预设文件。支持导入此预设库及旧版 `bd-info` 导出的参数 JSON，保留半径、扇区数与反转设置；当前制作和标定界面仅支持起始角 0°，非零角文件会明确拒绝导入。半径摘要显示完整数据区的原值。
- 内置“铼德 BD-R 10x”采用提供的标定参数：内半径 `23.98822105616562 mm`、外半径 `58.023234731100764 mm`、12,219,392 扇区、起始角 0°、不反转。制作盘面和实时预览均可直接选择；名称中的 10x 是盘片标识，实际刻录速度仍在刻录页选择。
- 名义容量默认 12,219,392 个 2048-byte 用户扇区，即 25,025,314,816 bytes；可手动设置不超过该上限的 32 正倍数。32 扇区组成 65,536-byte cluster。几何、生成与刻录共用此总数。
- 独立 EDC、扰码、GF(256) Reed-Solomon、LDC/BIS 交织与 ECC cluster 编码。
- 独立 17PP 调制和录制帧编码 API，供分析通道数据使用。
- 顺序、不可 Seek 输出流；取消/异常时清除临时文件并保留已有最终文件。
- 图片生成：二阶亮度按 128 分界，默认暗部采用偏 3T 数量的 `AA`，亮部采用偏 6T 数量的 `44`，支持反转。GUI 和 `bd-generate` 在未选择文件夹时输出覆盖全部用户扇区的纯绘图镜像，首尾均用于图案；该 `.iso` 不含文件系统，可由本程序回读，但不能作为普通文件光盘浏览。选择文件夹时输出含 UDF 2.01 的完整 `.iso`，支持大于 4 GiB 的单文件；此时只有绘图 payload 抵消扰码，文件系统、内圈文件与盘尾备份元数据保持正常用户数据。UDF 元数据占用不属于缺陷替换备用区。
- 流式刻录使用同一 ISO 生成器和有界缓冲区，无需先落盘完整镜像；刻录页提供 CD / DVD / 蓝光 三种介质选择、速度读取、确认与取消。蓝光使用未格式化单层顺序 BD-R 的无备用区写入通道，保留默认完整 12,219,392 扇区；不支持已格式化盘、BD-RE、随机记录或多层盘。
- 辅助标定提供 BD：新建标靶覆盖完整用户数据区，保存总扇区、半径、3T/6T 极性与绘图起止位置，生成标定镜像或流式刻录，照片矫正、描线拟合和应用结果回到蓝光参数页。旧 UDF 标定记录继续按保存的边界和时间戳回放，不自动改变已刻图案。

`BluRayRunLengthMapping` 已接入图片生成；`IBluRayPayloadMapping` 和 `IBluRayLuminanceMapping` 保留为扩展点。默认黑白方向是图像映射约定，实际盘片的明暗方向仍需观察，可通过反转交换两端。它偏向 3T / 6T 的 run **数量**，不承诺纯 3T / 6T，也不以总跳变密度的最大差值选字节。

## 命令行

以下命令在 `DiscImageStudio-GitHub` 目录执行；发布后的程序也支持同名参数。

```powershell
# 查看单层盘参数，或者通过 --output 导出 JSON
dotnet run --project src/DiscImageStudio.App -- bd-info --output bd-parameters.json

# 使用铼德 BD-R 10x 的标定参数
dotnet run --project src/DiscImageStudio.App -- bd-info --preset bd-r-ritek-10x

# 按手动容量生成含文件的 UDF ISO；省略 --data-dir 时改为全数据区纯绘图
dotnet run --project src/DiscImageStudio.App -- bd-generate --input image.png --output bd-image.iso --total-sectors 12219392 --data-dir C:\Files --volume-label BD_IMAGE

# 小容量功能验证（2 MiB，几何也按这1024扇区计算；不是实际整盘刻录配置）
dotnet run --project src/DiscImageStudio.App -- bd-generate --input image.png --output bd-test.iso --total-sectors 1024 --invert true

# 输出一个 cluster：每个用户字节在前向扰码后都为指定的 0xA6
dotnet run --project src/DiscImageStudio.App -- bd-probe --byte 0xA6 --sectors 32 --output bd-probe.bin

# 用另一个名义扇区位置的种子生成局部实验样本
dotnet run --project src/DiscImageStudio.App -- bd-probe --byte 0xA6 --start-sector 32 --sectors 64 --output bd-range.bin

# 将恰好一个用户 cluster 编码到 ECC 阶段
dotnet run --project src/DiscImageStudio.App -- bd-encode --input bd-probe.bin --psn 0x100000 --output bd-ecc.bin
```

`bd-info` / `bd-probe` / `bd-generate` 的 `--preset` 可选 `bd-r-sl-25gb` 或 `bd-r-ritek-10x`，未指定时使用前者。总扇区、内外半径和起始角默认取所选预设；`--total-sectors`、`--r0`、`--r1`、`--start-angle` 可分别覆盖对应值。数字使用小数点。`bd-info` / `bd-generate` 的 `--invert` 接受 `true` / `false`，默认取预设极性；参数 JSON 记录当前极性。`bd-generate` 始终输出整个所选容量，不再接受旧的 `--start-sector` / `--sectors` 切片参数。

`bd-probe` 保留原始用户扇区实验：`--byte` 为 0..255 或 `0x00`..`0xFF`，`--start-sector` / `--sectors` 必须按 32 对齐且不超过总容量，默认 0 / 32。其 `.bin` 不含文件系统，也不使用图片映射；非零起始范围不能当成 LBA 0 的整盘 ISO。CLI 拒绝未知、重复和缺值选项。

## 内圈文件与 ISO

`BluRayIsoGenerator.Prepare` 先扫描文件夹、规划 UDF 2.01 目录和文件 extent，将文件末尾向上对齐到 32 扇区。盘尾保留 288 扇区，用于备份卷描述符及锚点；中间范围用于图片。总长度固定为 `TotalSectors × 2048`，包含系统区、文件、对齐填充、图案和尾部元数据。未指定文件夹时仍保留空卷；必须至少剩余一个完整绘图簇，最小测试容量为 640 扇区。

内圈文件使用普通用户字节，不能进行图案反扰码；图案继续使用原始整盘扇区地址和全盘几何，不将剩余图案区重新拉伸到整盘。UDF 分区只覆盖内圈文件系统，图案不被标为文件系统的可用空间。界面的“计算文件布局”显示文件数量、数据大小、绘图起止扇区及半径。源图预览展示整个数据区的布局，实际可绘图区由文件占用与盘尾备份区共同决定。

文件/流式共用前向写入路径。输出路径必须位于内圈源文件夹之外，并且不能覆盖源图片；取消或失败保留原有 ISO。规划后源文件或目录内容发生变化时会拒绝继续使用过期布局。文件长度采用 64 位字段，大文件拆成多个不超过 `0x3ffff800` 字节的分配 extent，使用 128 KiB 缓冲顺序读取；单文件大小由盘片剩余容量限制，不再受 4 GiB 限制。DVD 的 ISO9660/Joliet 路径保持不变。

UDF 实现依据 [ECMA-167](https://ecma-international.org/publications-and-standards/standards/ecma-167/) 与 [ECMA TR/112-4（UDF 2.01）](https://ecma-international.org/wp-content/uploads/ECMA_TR-112-4_1st_edition_december_2023.pdf)，写入封闭只读卷。它用于数据盘，不提供视频蓝光制作。

`bd-encode` 输入必须恰好为 65,536 bytes；输出是 496 × 155 = 76,880 bytes 的 ECC cluster。`--psn` 必须落在单层数据区且按 32 对齐。该文件不含 17PP/DC-control、RUB framing 或文件系统，不能交给刻录程序当作用户数据镜像。

## 编码与几何

| 部分 | ISO/IEC 30190:2021 |
| --- | --- |
| 2048-byte payload + 4-byte EDC | §13.2–13.3 |
| 16-bit LFSR，PSN bits 19..5 派生种子；每个 data frame 复位 | §13.4 |
| LDC RS(248,216)，304 个码字及交织 | §13.5–13.8 |
| 地址、RS(9,5)、BIS RS(62,30) 及复用 | §13.9–13.14 |
| 17PP、DC-control 与同步帧 | §13.15–13.17 |
| Recording unit spacing | §14 |
| 名义轨距 0.320 µm | §15.4.3 |
| 单层 PSN 0x00100000..0x00CA73FF | §17 |
| 单层数据区 381,856 clusters | §19 |

图案测试写入的是**反扰码后的用户 payload**。实际驱动器会计算 EDC，再对完整 2052-byte data frame 扰码；EDC、纠错、地址、同步等数据不能被指定字节探针任意覆盖。

`BluRayRecordingFrameEncoder` 接受调用者明确提供的 28 个 DC 控制位。`BluRayRecordingUnitEncoder` 另外提供包含 run-in、496 帧、run-out 和可选 Guard3 的诊断模拟，每个 modulation bit / NRZI level 用一个 byte 表示。自动策略明确命名为 `BluRayGreedyDcControlEncoder`：使用尚未决定的后续位为 0 的前视贪心选择，后续控制位定案后可能改变之前的局部最佳选择；返回的 `SuboptimalDcControlDecisions` 会报告这种情况。该策略用于实验，不保证标准要求的最终 DC 最优性，也不承诺与刻录机的实际比特序列一致。CLI 的 `bd-encode` 只导出确定的 ECC 阶段，不把这项诊断模拟作为可刻录镜像输出。

`BluRayTrackGeometry` 使用独立恒定轨距螺旋，以手动总扇区对应的总轨长和内外半径求有效轨距；最后扇区到达设置的外半径。从数据面看，固定盘片坐标的取样轨迹顺时针向外（§9.8、§15.3）；屏幕转换只反转一次 Y 轴。每个 recording unit 的跨度是 `2760 + 496 × 1932 + 1104 = 962136` channel bits，位长 74.5 nm。`bd-probe` 的局部范围沿用完整总容量的几何。序列尾部 Guard3 不包含在每个 recording unit 的重复跨度中。

默认 24–58 mm 是名义几何，不是特定盘片的实测参数。内置流式刻录通过保持 BD-R 未格式化、不分配缺陷管理备用区来匹配 `sector → PSN` 模型。实际半径、位置与光学对比度仍需实盘标定；外部软件刻录 ISO 时也应采用无备用区方式。此版本提供 UDF 2.01、源图投影与 ISO 读回预览。

## 无备用区流式刻录

`IDiscFormat2Data` 对未格式化 BD-R 报告的可用容量可能是默认备用区方案的 11,826,176 扇区，比原始 12,219,392 扇区少 768 MiB。蓝光通道通过 `IDiscRecorder2Ex.SendCommandSendDataToDevice` 从 LBA 0 顺序发送 `WRITE(10)`，每次 32 扇区（64 KiB），单命令超时为 200 秒，给首写初始化留出等待时间。缓冲区遵循适配器的地址对齐和最大传输限制。此通道不调用数据刻录接口的 `Write`、`FORMAT UNIT` 或分配备用区的操作。

公开的 `IWriteEngine2` 接口没有可设置的命令超时属性，因此查询、刷新缓存的超时设置无法延长 `WriteSection` 内部的等待时间。当前显式命令通道会报告失败命令的 LBA、扇区数、实际耗时、HRESULT 和 Sense。超时意味着设备端结果可能不确定，程序不会自动重发该范围。

失败日志还保留最近 8 次成功写入的时序，分别统计源流读取等待、设备命令等待及累计确认的数据量。无有效 Sense 时明确显示“未返回有效的设备 Sense 数据”；全零返回不能解释为盘片没有错误。`0x8007048F` 表示 Windows 报告设备未连接。如果标注 200 秒超时的命令在几毫秒内失败，随后设备重新枚举，需要调查设备连接、固件及 USB 事件，不能只按等待时间不足处理。所有本实例的设备查询和刻录排队执行，避免尚未结束的查询与写盘重叠。

在独占锁定盘片后，程序用 MMC 读取当前 profile、盘片信息、格式容量和轨道信息。必须是空白、未格式化的单层 BD-R SRM，首轨从 0 开始，原始可写容量完整；未知、已使用或已格式化的布局均拒绝，不自动降低总扇区数。界面设置的总扇区数按 2048 字节换算为同一请求长度，UDF 前缀、绘图区和尾元数据全部包含其中；较小的手动容量仍写入相同数量的扇区。

写入完成后刷新缓存、关闭轨道，并用 BD-R 的 finalize 功能 6 封盘。取消在当前设备命令结束后生效；取消或写入失败只尽力刷新缓存和释放设备，不把未完成镜像作为成功产物封盘。各项解锁仅在成功后标记为完成，失败项会再尝试一次，错误也会保留在诊断信息中；已成功的计数配对操作不重复执行。进入封盘后等待命令完成。无备用区模式没有可用于坏块重映射的备用容量。

BD 实时速率按刻录机确认接收的写入命令计算，包含设备缓存的影响，不等于激光在盘面上的瞬时速度；最终成功仍以刷新缓存及封盘完成为准。4× 的标称用户数据速率为 18 MB/s，界面采用十进制 MB/s，不能用请求倍速代替实际吞吐。

写入前立即记录设备返回的设置速度；设置值与实时吞吐分别保留。写入过程中每 10 秒及最后一块后记录一个诊断窗口，区分源流读取用时（含等待和拷贝）与 WRITE 命令用时（含传输及设备等待），并记录确认接收的平均速率。低频日志不增加设备查询、不改写进度或刷新速率的有效期；源流读取用时不能当作生产线程的纯计算耗时，命令用时也不能单独区分 USB 传输与盘面写入。

图片供流采用最多 8 个工作线程、16 个簇（1 MiB）的有界批次，完成计算后按原顺序写出。内建 3T/6T 阈值映射及扰码 XOR 使用整数 SIMD，位置计算保留原精度；不再为每个字节重复检查已经验证的盘片参数。不可变栅格图自动启用并行，内建标定图案显式声明采样线程安全；自定义采样回调默认保持串行。预缓冲只能暂时吸收速度差异，持续刻录仍要求供流速度高于所选设备速度。

协议和工作流测试只使用假设备及 1024 扇区（2 MiB）的 UDF 数据流；完整 12,219,392 扇区仅验证容量与几何元数据，不生成全盘 ISO。物理刻录结果仍需实盘验证。

实现依据：[微软发送数据与命令超时接口](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/nf-imapi2-idiscrecorder2ex-sendcommandsenddatatodevice)、[libburn MMC 实现](https://dev.lovelyhq.com/libburnia/libburn/src/branch/master/libburn/mmc.c)、[libburn 写入超时](https://dev.lovelyhq.com/libburnia/libburn/src/branch/master/libburn/spc.h)。

## 3T / 6T 数量映射与扩展

图片链路使用 `BluRayImageLoader` 解码并合成透明白底，再通过 `BluRayImagePayloadMapping` 取样。图片保持宽高比，最长边对应盘片外径，空白区域为白色。取样位置包含每帧 EDC 占位、LDC 交织、BIS 插入、DC 位、同步和 run-in；由 2:3 调制率计算的是名义 byte 中心，不是变长 17PP 码字的精确边界。

内建 `BluRayRunLengthMapping` 先以 128 分界，再返回扰码域 `AA` 或 `44`。选择依据是同样 958,272 channel bits 的完整簇中，相邻 modulation ONE 间隔为 3T / 6T 的次数，mark 与 space 都计入，不是某一种电平的占空比。基准 PSN `0x100000` 的 256 重复字节扫描中，`AA` 有 265,136 个 3T，`44` 有 116,575 个 6T，分别为目标次数最高；四地址复核仍保留强偏向，但不保证每个地址均排名第一。细节见[字节分析](BLURAY_BYTE_DENSITY.md#按-3t--6t-数量接入绘图)。

实现 `IBluRayLuminanceMapping.Map` 可以替换策略，为源图亮度和名义 byte 位置返回所选扰码域 byte。对非图片实验，还可以直接实现 `IBluRayPayloadMapping.FillScrambledPayload`，为扇区的 2048 个用户 byte 位置填入数据，再交给 `BluRayStreamingGenerator`；回调接收名义扇区、PSN 和盘片参数。

`BluRayDiscModule` 通过构造器注入图片解码委托，保持不依赖 WPF；App 的 CLI 使用 `BluRayImageLoader.Load`。直接使用模块 API 生成图片时也应提供该委托。`BluRayConstantByteMapping` 仍只用于显式数字实验。图片与标定都通过 `BluRayIsoGenerator` 生成文件或向刻录器供流。

候选筛选见 [字节与位周期密度分析](BLURAY_BYTE_DENSITY.md)。`tools/BluRayByteProbe` 可重现全部 256 字节和细粒度周期的完整簇统计；周期位串属于其他实验策略，当前数量映射采用重复字节。分析结果仍未指定实盘黑白极性。

## 验证

```powershell
dotnet build DiscImageStudio.slnx --configuration Release
dotnet run --project tests/DiscImageStudio.ArchitectureTests --configuration Release
```

回归覆盖独立 EDC/扰码向量、RS syndrome、交织、17PP、3T/6T 数量偏向、可调容量几何、UDF 目录及 Unicode 文件、描述符 CRC 和校验和、主备卷与锚点、图案与元数据互不覆盖、非 Seek 流一致性、取消/失败保留输出、媒体类型/速度、标定坐标和会话恢复。落盘 UDF 测试仅 1024 扇区，单个 ISO 最大 2 MiB。大文件测试先启用并验证稀疏属性，再创建超过 4 GiB 的逻辑源文件（实际分配小于 1 MiB），向校验后丢弃数据的流顺序输出，只保留少量元数据和边界样本，不写大 ISO。真实盘容量只做数学验证，不生成 25 GB 测试 ISO，也不访问刻录设备。
