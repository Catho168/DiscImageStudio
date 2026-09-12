# CD 音轨格式、字节序与重影排查

## 推荐用法

新生成任务默认输出 `cd-track.wav`，同时生成 `cd-track.cue`。WAV 为无压缩 PCM：44.1 kHz、16 位、双声道，数据区为小端。将这两个文件放在同一目录，移动或改名时也要同步更新 CUE 中的文件名。

| 刻录入口 | 使用方式 |
| --- | --- |
| cdrecord | `cdrecord dev=设备 speed=速度 -audio "cd-track.wav"`，不要额外添加 `-swab` |
| ImgBurn | 在“Write image file to disc”中打开 `cd-track.cue` |
| 程序内流式刻录 | 自动生成同样的小端 PCM 数据，不包含 WAV 文件头，不生成完整临时音轨 |
| 旧 RAW 工作流 | 输出仍可选择 `.raw`；`cdrecord -audio "cd-track.raw"` 不加 `-swab` |

输出后缀决定真实格式，只接受 `.wav` 和 `.raw`。不能通过重命名把旧 RAW 变成 WAV；必须重新生成。WAV 的配套 CUE 使用 `FILE "cd-track.wav" WAVE`，不使用 `BINARY`。Unicode 文件名使用带 BOM 的 UTF-8 CUE，支持空格和中文。ImgBurn 作者曾验证这种 CUE 的 Unicode 文件名支持，并指出早期 2.5.0.0 的相关问题在 2.5.1.0 修复。[ImgBurn 作者说明](https://forum.imgburn.com/topic/13526-unicode-filename-problem-in-cue-file/)

命令行生成示例（几何参数应使用自己的实测设置）：

```powershell
DiscImageStudio.exe cd-generate --input picture.png --output cd-track.wav --interleave true
```

WAV 的文件长度为 `扇区数 × 2352 + 44`；音频长度、生成进度和流式刻录容量仍为 `扇区数 × 2352`。文件头不占盘片音轨扇区，也不改变图案几何。

## 已确认的问题

2026-09-12 的用户实盘对照：同一份既有 RAW，用 cdrecord 的 `-audio` 和 `speed` 正常；添加 `-swab` 即可复现重影。这确认了既有 RAW 应按 cdrecord 默认的大端音频输入解释。

此前文件导出和流式刻录共用完全相同的 `GenerateToStream` 输出，流式路径没有执行字节序转换。但不同输入入口的约定不同：

| 输入 | 音频数据的要求 |
| --- | --- |
| cdrecord 无头 `-audio` | 默认大端 |
| cdrecord 无头 `-audio -swab` | 小端；该选项声明需要交换的输入顺序 |
| cdrecord WAV | 解析文件头，自动识别音频字节序 |
| CUE 的 `FILE ... BINARY` | 小端；不能把既有大端 RAW 直接套入 BINARY CUE |
| IMAPI `AddTrack(IMAPI_CD_SECTOR_AUDIO, stream)` | 与 16 位双声道 44.1 kHz WAV 数据区相同的样本布局，即无头小端 PCM |

上述 cdrecord 行为及 BINARY 定义来自 [cdrecord 官方手册](https://cdrtools.sourceforge.net/private/man/cdrecord/cdrecord.1.html)；IMAPI 的音频样本布局来自 [Microsoft 的 AUDIO 扇区定义](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/ne-imapi2-imapi_cd_sector_type)。ImgBurn 作者也提供了成功刻录 `FILE ... WAVE` CUE 的例子。[ImgBurn WAVE CUE 实例](https://forum.imgburn.com/topic/25173-extract-only-session-two-from-audio-cd/page/2/)

这里的 `AudioSectorType = 0` 是 IMAPI 枚举的 AUDIO 值，不是把音轨写成物理 Mode 0。`IRawCDImageCreator::AddTrack` 当前只支持 AUDIO 类型。[Microsoft AddTrack 文档](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/nf-imapi2-irawcdimagecreator-addtrack)

## 为什么会形成两组轮廓

现有 `CddaInterleaver` 为目标字节执行延迟补偿。交换最终输出每个 16 位样本的两个字节，会把原本属于不同延迟位置的字节交叉送入编码链。因此不能把变化理解为普通音频数值变化，也不能在图像采样之前交换一次来抵消。

针对现有延迟表进行整数标签排列实验，排除头尾填充后，额外的样本字节交换会让逆映射后的偶数位置偏移 `+73` 个目标字节，奇数位置偏移 `-73` 个目标字节。这解释了双轮廓机制；它是程序延迟排列模型的推导，并非完整光驱 CIRC 模型或实体盘测量。

修正保留采样和延迟表，在补偿完成后的 2352 字节扇区输出边界选择字节序：旧 RAW 大端；WAV 和 IMAPI 小端。WAV 文件头解决外部工具对格式和字节序的识别，流式接口仍按其约定只接收数据区。

## 验证与边界

- RAW 与 WAV：对非对称图案，交织开启与关闭两种情况下，WAV 的 data 区均严格等于旧 RAW 逐 16 位交换后的字节序列；长度保持不变。
- 流式输出：在禁止 Seek/Position 的前向流中生成小端音频，结果逐字节等于 WAV 的 data 区，且不包含文件头。
- WAV 预览：解析 RIFF 的 `fmt ` / `data`，跳过未知块及奇数长度填充，限制读取范围，归一化字节序；拒绝非 PCM、非 44.1 kHz、非 16 位或非双声道数据。不同采样步长下与对应 RAW 预览一致。预览仍是文件字节映射，未模拟完整物理 CIRC/EFM，不能替代实盘判定。
- Windows 内存实验：向现有 `MsftRawCDImageCreator` 输入 300 个非对称音频扇区，扫描完整的 2448 字节 RAW 结果扇区，全部音频字节原样保留。生成器不会替调用者交换字节。测试不创建刻录设备、不写入光盘。
- 2026-09-12 用户在本次修复后完成实盘试验，反馈已无重影。这是用户提供的实体盘结果；流式、cdrecord、ImgBurn 三个入口的逐项实测细节未单独记录。

微软 `WriteMedia` 文档没有说明它内部的字节交换实现；上述结论没有把内存实验等同于观察到设备传输时的交换。[Microsoft WriteMedia 文档](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/nf-imapi2-idiscformat2rawcd-writemedia)
