# Contributing

感谢参与 Disc Image Studio。

1. 从 `main` 创建功能分支。
2. 保持介质专用逻辑位于对应模块，不要把 CD/DVD/蓝光格式细节加入 Core。
3. 构建 `DiscImageStudio.slnx`，运行 DVD 自检和架构自检。
4. 新增命令时补充 `DiscCommandDescriptor` 和路由测试。
5. 提交信息应说明行为变化、验证方法和可能生成的大文件。

提交 Pull Request 前运行：

```powershell
dotnet build DiscImageStudio.slnx --configuration Release
dotnet run --project src/DvdImageSolver --configuration Release -- selftest
dotnet run --project tests/DiscImageStudio.ArchitectureTests --configuration Release
```
