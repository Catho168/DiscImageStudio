# 发布指南

## GitHub Release

1. 选择并添加明确的许可证。
2. 更新 `CHANGELOG.md`、项目版本和隐私政策联系方式。
3. 确认 GitHub Actions 的 `build` 工作流通过。
4. 创建版本标签，例如 `v1.0.0`。
5. 在 GitHub Release 中附上源代码和经过验证的安装包。

## Microsoft Store MSIX

先在 Partner Center 保留产品名，并从 Product identity 页面复制：

- Package/Identity/Name
- Package/Identity/Publisher
- Package/Properties/PublisherDisplayName

本地执行：

```powershell
powershell -ExecutionPolicy Bypass -File packaging/Build-MSIX.ps1 `
  -IdentityName "Partner Center Identity Name" `
  -Publisher "CN=Partner Center Publisher" `
  -PublisherDisplayName "Publisher display name" `
  -DisplayName "Reserved product name" `
  -Version "1.0.0.0"
```

也可以在 GitHub Actions 手动运行 `package-msix` 工作流并填写相同值。生成的 Store MSIX 可以保持未签名；若要旁加载测试，需使用 Publisher 匹配的证书签名。

## 本机签名测试包

为本机测试创建主题与测试 Publisher 完全一致的代码签名证书，把公钥证书放到：

```text
artifacts/certificates/DiscImageStudio-Test.cer
```

使用证书指纹运行 `Build-MSIX.ps1 -CertificateThumbprint <thumbprint>` 生成签名包。然后双击 `packaging/Install-TestPackage.cmd`，或运行：

```powershell
powershell -ExecutionPolicy Bypass -File packaging/Install-TestPackage.ps1
```

安装脚本会请求一次管理员权限，将测试证书公钥导入“本地计算机/受信任人”，并安装签名 MSIX。不要把私钥或 PFX 文件提交到 GitHub。

## Windows 11 未签名快速测试（可选）

未签名测试包必须使用独立的 OID 发布者命名空间，不能与商店正式身份混用。生成本机测试包：

```powershell
powershell -ExecutionPolicy Bypass -File packaging/Build-MSIX.ps1 `
  -IdentityName "12345.DiscImageStudioArchitectureTest" `
  -Publisher "CN=Disc Image Studio Test" `
  -PublisherDisplayName "Disc Image Studio Test" `
  -DisplayName "Disc Image Studio Test" `
  -Version "1.0.0.0" `
  -UnsignedDevelopmentPackage
```

然后用管理员 PowerShell 安装：

```powershell
Add-AppxPackage -Path ".\artifacts\msix\DiscImageStudio_1.0.0.0_x64_unsigned-dev.msix" -AllowUnsigned
```

`-UnsignedDevelopmentPackage` 只用于支持该功能的 Windows 11 系统快速测试；部分 Windows 版本即使提供 `-AllowUnsigned` 仍可能拒绝可执行包，此时使用上面的签名测试方案。准备分发或提交 Microsoft Store 时不要使用该选项，正式包仍需使用 Partner Center 的真实身份。

正式提交前：

- 用真实身份重新打包，不能提交测试 Identity 包。
- 在干净的 Windows 设备上测试安装、卸载和离线运行。
- 对最终包运行 Windows App Certification Kit。
- 提供至少一张 1366×768 或更大的真实桌面截图。
- 将 `PRIVACY.md` 发布到公开 HTTPS 页面并填写真实支持联系方式。
- 在认证备注中说明应用生成大文件、无需登录、不联网且不直接刻录光盘。
