# 本地验证记录

验证日期：2026 年 8 月 22 日

- `dotnet build DiscImageStudio.slnx --configuration Release`：0 警告、0 错误。
- DVD 引擎直接运行 `selftest`：全部通过。
- `DiscImageStudio.ArchitectureTests`：全部通过，包括测试蓝光模块注册、路由、执行、未知命令和重复命令拒绝。
- CD 原程序与新模块使用相同图片和 2 个扇区生成的 RAW 均为 4704 字节，SHA-256 相同：
  `D12F5561B731203E24F4FECDFFDFF5F433CDC15C2EE847A6D9A439BF99E943EA`
- DVD 原入口与新模块适配器生成的校准 PNG SHA-256 相同：
  `C1115713A63C406638FBCA36CD458088633BD34CD3EBF8DD2B91DAA53C8F41C9`
- GUI 已从独立源码目录屏幕外渲染为 1366×768 PNG。
- 已确认旧测试包崩溃原因为 `System.Globalization.Invariant=true` 导致 WPF 字体缓存无法创建 `en` 区域；应用发布配置现已显式设为 `false`，并增加构建期防回归检查。
- 修复后从已安装的 MSIX 运行 GUI 1366×768 渲染与小型 CD 几何预览均成功，退出码均为 0。
- 独立实时预览页面已通过 CD、DVD 两种模式的整页渲染检查：左侧参数可滚动编辑并与生成页双向同步，右侧显示预览图，运行日志保留为单独页面。
- CD 几何预览已改用与 DVD 相同的二值采样平均灰度模式。以 512 px、每扇区 4 次采样检查，CD 有 3074 个中间灰度像素，DVD 有 298 个中间灰度像素。
- DVD 应用模块固定覆写为 `dispersion`、快速输出、每块 1 次迭代和 `cw`；传入显式 `ccw` 后，校准 JSON 仍记录为 `cw`。
- DVD 实时刷新端到端检查通过：只设置源图片可在 450ms 防抖后自动生成预览；再把起始角从 `0` 改为 `45`，产生的界面截图 SHA-256 分别为 `B6C96227AACFFCC75448C1E588BD80B17B22FD89CFC66FA9781BE520B676A5D7` 与 `D2FCBD5DBAD6D438A1C33F1DDBEE6666D54DE431F09AC8D6E9D4A0135755BA1B`。
- Windows SDK MakeAppx 10.0.26100.8249 成功生成模块化版本的测试 MSIX。
- SignTool 成功使用主题 `CN=Disc Image Studio Test` 的专用代码签名证书签名，证书主题与清单 Publisher 一致。
- 签名测试包 `DiscImageStudio_1.0.5.0_x64.msix` SHA-256：
  `73B5BAECED533486BC7C6A05810E97396B46E910604A633CC06AC414A64B0B25`
- 1.0.5.0 已在当前账户完成升级安装；从已安装包执行 DVD 实时预览成功，界面左下角显示版本 1.0.5。
- `Install-TestPackage.ps1` 已通过 PowerShell 语法检查；脚本会请求 UAC，把公钥导入本地计算机“受信任人”后安装测试包。

测试 MSIX 使用测试 Identity/Publisher 和自签名测试证书，只用于本机安装验证，不能提交商店。正式包必须使用 Partner Center 的真实身份重新生成。
