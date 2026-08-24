# Disc Image Studio 光盘绘图工坊

Disc Image Studio 是一个 Windows 光盘图像生成工具，在同一套 WPF 界面中提供 CD-DA 原始轨道和 DVD ISO 图像生成功能。所有输入与输出均在本机处理。

![Disc Image Studio 首页](docs/images/home-screen.png)

## 功能

- CD-DA：2352 字节/扇区 RAW 轨道、延迟交织、1200 mm/s 默认扫描速度、CLV 几何与轨道预览。
- DVD：固定快速纹理算法、快速输出、顺时针（CW）螺旋、ISO 与混合数据盘。
- 图片处理：可将同一张源图自动复制多份并朝外环形排列；放不下时整张图片会等比缩小，保证不变形、不裁切、不重叠；内外安全边界可调，避免图案贴近不可读区域。正式生成使用 8192×8192 高分辨率中间图，实时预览使用独立的轻量分辨率。
- 流式刻录（实验性）：CD 与纯绘图 DVD 可通过 Windows IMAPI2 边生成边刻录，只使用约 16 MiB 内存缓冲，不保存完整临时镜像；当前已通过自动化与接口顺序测试，但尚待实体盘验证。
- 盘片预设：内置 CD 80/74 分钟、铼德医疗水蓝盘、Verbatim CD-R AZO (43438) 实测参数、12 cm 单层 DVD 和 8 cm 单层 Mini DVD；选择后自动填写容量与几何参数，也可继续手动自定义。
- 校准：CD 与 DVD 共用独立的实时灰度预览页，可同步调整生成与实测参数。
- 双入口：不带参数打开 GUI，带参数用于自动化。
- 模块化：CD 与 DVD 都通过 `IOpticalDiscModule` 接入；未来蓝光使用独立模块。
- 商店准备：应用图标、MSIX 清单、打包脚本和 GitHub Actions 工作流。

## 源码结构

```text
src/DiscImageStudio.Core/   介质无关的模块契约、元数据、任务和目录
src/DiscImageStudio.Imaging/ CD/DVD/未来蓝光可复用的图片预处理层
src/DiscImageStudio.Burning/ Windows IMAPI2 设备枚举、流缓冲与安全刻录层
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

应用不安装驱动或服务；直接刻录使用 Windows 自带 IMAPI2，只写入用户明确选择并二次确认的刻录机，且拒绝非空白介质。流式刻录目前属于待实盘验证的实验性功能，请只使用可报废的测试介质；刻录中断、断电或生成速度不足仍可能使盘片报废。实际可见效果取决于盘片、刻录机、固件和写入策略，请先生成校准预览并用测试介质验证。DVD 混合文件夹需要随机写文件系统，因此仍应先生成 ISO；纯绘图 DVD 和 CD 可使用流式刻录。
