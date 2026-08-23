# Disc Image Studio 光盘绘图工坊

Disc Image Studio 是一个 Windows 光盘图像生成工具，在同一套 WPF 界面中提供 CD-DA 原始轨道和 DVD ISO 图像生成功能。所有输入与输出均在本机处理。

![Disc Image Studio 首页](docs/images/home-screen.png)

## 功能

- CD-DA：2352 字节/扇区 RAW 轨道、延迟交织、CLV 几何与轨道预览。
- DVD：固定快速纹理算法、快速输出、顺时针（CW）螺旋、ISO 与混合数据盘。
- 校准：CD 与 DVD 共用独立的实时灰度预览页，可同步调整生成与实测参数。
- 双入口：不带参数打开 GUI，带参数用于自动化。
- 模块化：CD 与 DVD 都通过 `IOpticalDiscModule` 接入；未来蓝光使用独立模块。
- 商店准备：应用图标、MSIX 清单、打包脚本和 GitHub Actions 工作流。

## 源码结构

```text
src/DiscImageStudio.Core/   介质无关的模块契约、元数据、任务和目录
src/DiscImageStudio.Cd/     CD-DA 生成核心与 CD 模块
src/DiscImageStudio.Dvd/    DVD 引擎适配模块
src/DiscImageStudio.App/    WPF GUI、统一命令行和商店资源
src/DvdImageSolver/         保持隔离的 DVD 编码/求解引擎
tests/                      无第三方测试框架的架构回归测试
packaging/                  MSIX 清单和打包脚本
docs/                       架构、蓝光扩展与发布文档
```

抽象关系与设计边界见 [架构说明](docs/ARCHITECTURE.md)，蓝光接入步骤见 [ADDING_BLURAY.md](docs/ADDING_BLURAY.md)。

## 构建与运行

要求：Windows 10 2004+ 或 Windows 11、.NET 9 SDK。

```powershell
dotnet build DiscImageStudio.slnx --configuration Release
dotnet run --project src/DiscImageStudio.App --configuration Release
```

运行回归检查：

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- selftest
dotnet run --project tests/DiscImageStudio.ArchitectureTests --configuration Release
```

统一程序仍接受原 DVD 命令，并增加 CD 命令：

```text
solve
encode
calibrate
selftest
cd-generate
cd-preview-warp
cd-preview-track
```

完整 DVD 引擎说明见 [docs/DVD_ENGINE.md](docs/DVD_ENGINE.md)，本轮构建与兼容性结果见 [docs/VALIDATION.md](docs/VALIDATION.md)。

## GitHub 发布

仓库已包含 Windows CI 和手动 MSIX 打包工作流。创建空 GitHub 仓库后，可在本目录运行：

```powershell
git init
git add .
git commit -m "Initial release"
git branch -M main
git remote add origin https://github.com/你的账号/DiscImageStudio.git
git push -u origin main
```

正式发布前请先选择许可证；当前仓库没有替你假定开源授权。详见 [LICENSE-NOTICE.md](LICENSE-NOTICE.md) 和 [发布指南](docs/RELEASING.md)。

## 安全与物理介质说明

应用不直接控制刻录机，不安装驱动或服务。完整 CD/DVD 输出可能很大且耗时；实际可见效果取决于盘片、刻录机、固件和写入策略，请先生成校准预览并用测试介质验证。
