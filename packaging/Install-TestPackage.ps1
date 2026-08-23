[CmdletBinding()]
param(
    [string]$PackagePath,
    [string]$CertificatePath
)

$ErrorActionPreference = "Stop"

$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $packageDirectory = Join-Path $workspaceRoot "artifacts\msix"
    $packages = @(
        Get-ChildItem -LiteralPath $packageDirectory -Filter "DiscImageStudio_*_x64.msix" -File |
            Sort-Object LastWriteTime -Descending
    )
    if ($packages.Count -ne 1) {
        throw "Expected exactly one signed test MSIX in $packageDirectory, but found $($packages.Count)."
    }

    $PackagePath = $packages[0].FullName
}
if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    $CertificatePath = Join-Path $workspaceRoot "artifacts\certificates\DiscImageStudio-Test.cer"
}

$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$CertificatePath = [IO.Path]::GetFullPath($CertificatePath)

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "Signed test package was not found: $PackagePath"
}
if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
    throw "Test certificate was not found: $CertificatePath"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdministrator) {
    $arguments = @(
        "-NoProfile"
        "-ExecutionPolicy Bypass"
        "-File `"$PSCommandPath`""
        "-PackagePath `"$PackagePath`""
        "-CertificatePath `"$CertificatePath`""
    ) -join " "

    $process = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $process.ExitCode
}

$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
$trustedCertificatePath = "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)"

if (-not (Test-Path -LiteralPath $trustedCertificatePath)) {
    Import-Certificate `
        -FilePath $CertificatePath `
        -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
}

Add-AppxPackage -Path $PackagePath

$installedPackage = Get-AppxPackage -Name "12345.DiscImageStudioArchitectureTest"
if ($null -eq $installedPackage) {
    throw "Windows did not report the test package as installed."
}

Write-Host ""
Write-Host "Disc Image Studio Test installed successfully." -ForegroundColor Green
Write-Host "Open it from the Start menu by searching for: Disc Image Studio Test"
Write-Host "Press Enter to close this window."
[void](Read-Host)
