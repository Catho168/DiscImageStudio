# 验证说明

本页列出当前源码的可复现检查。自动化回归验证算法、文件布局和应用工作流；实际刻录效果仍取决于刻录机、固件与盘片。

## 本地验证

最近一次完整回归：2026-10-25，Windows、.NET SDK 9.0.317，Release 配置。完整 solution 构建、架构回归、DVD 编码自检与发布脚本的 45 项测试均通过。

在仓库根目录执行：

```powershell
dotnet restore DiscImageStudio.slnx
dotnet build DiscImageStudio.slnx --configuration Release --no-restore
dotnet run --project tests/DiscImageStudio.ArchitectureTests --configuration Release --no-build
dotnet run --project src/DvdImageSolver --configuration Release --no-build -- selftest
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- selftest
./tests/Test-ReleaseTooling.ps1
```

架构回归覆盖：

- CD/DVD 黑白映射、抖动和 sRGB 伽马处理，文件与流式输出一致性，SIMD 与标量采样一致性。
- 蓝光编码向量、3T/6T 映射、UDF 描述符与 CRC、分段 extent、大于 4 GiB 的稀疏源文件及有界流式生成。
- 刻录操作互斥、设备写入进度、速率显示、顺序写入、超时、取消和资源清理，均使用模拟接口而非真实刻录机。
- 三次轨距、照片矫正、曲线拟合、标定会话保存恢复、盘片预设同步与参数精度。
- 实时预览尺寸、布局、1:1 像素、缩放拖动，以及多显示器和任务栏边界。

可按范围运行较快的子集，在架构回归命令后追加 `-- --ui-checks`、`-- --burning-checks`、`-- --image-processing-checks` 或 `-- --calibration-checks`。

GUI 可通过现有命令离屏生成截图：

```powershell
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- ui-snapshot --output artifacts/validation/about.png --tab 6
```

`build` 工作流另执行模块目录的 DVD 自检、两扇区 CD 输出检查和主页截图。发布单文件 EXE 的附加检查见[发布指南](RELEASING.md)。

## 实盘验证边界

此次代码整理没有执行实盘刻录。自动化测试不证明设备写入策略、实际反射亮度或刻录成功率；蓝光功能仍为实验性。

已经记录的 DVD 三次轨距实拍与参数见 [README 中的前后对比](../README.md#dvd-三次轨距校准实盘前后对比)。物理模型历史审计见 [PHYSICAL_MODEL_AUDIT.md](PHYSICAL_MODEL_AUDIT.md)，其中结论保留其标明的日期与代码基准，不能代替当前源码验证。

后续实盘记录应包含版本或提交、刻录机和固件、盘片型号、预设参数、速度、生成/刻录入口与原始照片，避免把某一设备的结果泛化到所有盘片。
