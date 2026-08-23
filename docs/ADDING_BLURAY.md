# 添加蓝光模块

当前版本没有实现蓝光物理编码。本文件说明如何在不改动 CD、DVD 引擎的前提下接入未来实现。

## 1. 创建项目

创建 `src/DiscImageStudio.Bluray/DiscImageStudio.Bluray.csproj`，只引用 `DiscImageStudio.Core`。蓝光编码器、纠错、调制、盘片容量配置和文件系统实现都放在该项目或它自己的内部库中。

```xml
<ItemGroup>
  <ProjectReference Include="..\DiscImageStudio.Core\DiscImageStudio.Core.csproj" />
</ItemGroup>
```

不要引用 `DiscImageStudio.Cd`；也不要把 DVD EFMPlus/ECC 类型当作蓝光编码基类。

## 2. 实现模块契约

```csharp
using DiscImageStudio.Core;

namespace DiscImageStudio.Bluray;

public sealed class BluRayDiscModule : IOpticalDiscModule
{
    public DiscModuleDescriptor Descriptor { get; } = new(
        "bluray-data",
        "Blu-ray data disc",
        OpticalDiscFamily.BluRay,
        "Blu-ray image generation.",
        SupportsCancellation: true,
        Commands:
        [
            new DiscCommandDescriptor(
                "bd-generate",
                "Generate Blu-ray image",
                "Builds a Blu-ray data image.",
                DiscModuleCapabilities.DataImageGeneration
                    | DiscModuleCapabilities.ErrorCorrectionEncoding,
                [
                    new("input", "Source image", DiscOptionValueType.InputFile, Required: true),
                    new("output", "Output image", DiscOptionValueType.OutputFile, Required: true),
                ]),
        ]);

    public async Task<DiscJobResult> ExecuteAsync(
        DiscJobRequest request,
        IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Parse module-specific arguments, run the Blu-ray engine, report progress,
        // and return a normalized result. Do not access GUI controls here.
        await Task.CompletedTask;
        return new DiscJobResult(Descriptor.Id, request.Command, 0);
    }
}
```

## 3. 注册模块

在 `DiscImageStudio.App/UnifiedCommandRunner.cs` 的目录初始化处增加：

```csharp
new BluRayDiscModule(),
```

统一命令行随后会自动路由 `bd-generate`。GUI 可以根据 `DiscCommandDescriptor.Options` 构建蓝光表单，或新增专用蓝光页面；CD/DVD 页面无需修改。

## 4. 必须补充的测试

- 模块 ID 与命令冲突检查。
- 最小容量或测试范围的确定性输出。
- 扇区、纠错和调制的标准向量。
- 文件系统可挂载性。
- 取消后不留下被误认为完整镜像的输出。
- 从模块入口和底层编码器入口生成的结果一致。
- 最终 MSIX 的 WACK 与离线运行测试。

## 5. 规格与授权

实现前应明确目标介质类型、写入模式、文件系统和所依据的正式规格，并确认相关规范、商标与专利许可要求。不要仅通过调整 DVD 扇区数或半径来宣称兼容蓝光。
