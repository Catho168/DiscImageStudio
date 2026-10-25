# 验证说明

本页列出当前源码的可复现检查。自动化回归验证算法、文件布局和应用工作流；实际刻录效果仍取决于刻录机、固件与盘片。

## 本地验证

最近一次完整回归：2026-10-25，Windows、.NET SDK 9.0.317，Release 配置。完整 solution 构建（0 警告）与架构回归全部通过；测试入口直接运行自研 Exe harness，不使用 `dotnet test`。

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

- CD/DVD：黑白映射、抖动、sRGB 伽马、文件与流式一致性、SIMD 与标量采样一致性。
- BD：17PP/EDC/Reed-Solomon、3T/6T 映射与极性、绘图范围清零、UDF 2.01、大于 4 GiB 文件、sidecar 往返与无效边界拒绝。
- 三态模型：工程 schema 2 往返与 schema 1 升级、预设 schema 4 与旧 schema 合并、`ProjectSession` 的 BD apply/capture。
- 刻录：操作互斥、进度、顺序写入、超时、取消和资源清理，均使用模拟接口而非真实刻录机。
- 标定：三次轨距、BD 绘图范围与极性、照片矫正、曲线拟合、会话保存恢复、盘型预设同步与参数精度。
- 预览与窗口：CD/DVD/BD 投影与读回、多显示器和任务栏边界。

GUI 可通过现有命令离屏生成截图：

```powershell
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- ui-snapshot --output artifacts/validation/home.png --tab 0
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- ui-snapshot --output artifacts/validation/export-bd.png --tab 3 --project sample.discproj
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- ui-snapshot --output artifacts/validation/preview-bd.png --tab 2 --project sample.discproj --live-input image.png --live-disc bd --live-mode calibrate
```

`build` 工作流另执行模块目录的 DVD 自检、两扇区 CD 输出检查和主页截图。发布单文件 EXE 的附加检查见[发布指南](RELEASING.md)。

## 实盘验证边界

此次代码整理没有执行实盘刻录。自动化测试不证明设备写入策略、实际反射亮度或刻录成功率；蓝光功能仍为实验性。

已经记录的 DVD 三次轨距实拍与参数见 [README 中的前后对比](../README.md#辅助标定展示)。物理模型历史审计见 [PHYSICAL_MODEL_AUDIT.md](PHYSICAL_MODEL_AUDIT.md)，其中结论保留其标明的日期与代码基准，不能代替当前源码验证。

后续实盘记录应包含版本或提交、刻录机和固件、盘片型号、预设参数、速度、生成/刻录入口与原始照片，避免把某一设备的结果泛化到所有盘片。
