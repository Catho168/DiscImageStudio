param(
    [string]$Tag,
    [switch]$RequireReleaseNotes
)

$ErrorActionPreference = "Stop"
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
[xml]$buildProps = Get-Content -LiteralPath (Join-Path $workspaceRoot "Directory.Build.props") -Raw
$baseVersion = [string]$buildProps.Project.PropertyGroup.VersionPrefix
$suffixNode = $buildProps.SelectSingleNode('/Project/PropertyGroup/VersionSuffix')
$configuredSuffix = if ($null -ne $suffixNode) { $suffixNode.InnerText } else { '' }
if ($configuredSuffix -and $configuredSuffix -cnotmatch '^(alpha|beta|rc)\.[1-9][0-9]*$') {
    throw "VersionSuffix must be empty or (alpha|beta|rc).N, with N starting at 1."
}
$number = '(0|[1-9][0-9]*)'
if ($baseVersion -cnotmatch "^$number\.$number\.$number$") {
    throw "Directory.Build.props must define one VersionPrefix in A.B.C format."
}
foreach ($part in $baseVersion.Split('.')) {
    # AssemblyVersion components must be below UInt16.MaxValue.
    if ([decimal]$part -gt 65534) {
        throw "Version components must be in 0..65534 for .NET assembly versions."
    }
}
if ([string]::IsNullOrEmpty($Tag)) {
    $Tag = "v$baseVersion"
    if ($configuredSuffix) { $Tag += "-$configuredSuffix" }
}
if ($Tag -cnotmatch "^v(?<base>$number\.$number\.$number)(?<suffix>-(alpha|beta|rc)\.[1-9][0-9]*)?$") {
    throw "Tag must be vA.B.C or vA.B.C-(alpha|beta|rc).N, with N starting at 1."
}
if ($Matches['base'] -cne $baseVersion) {
    throw "Tag $Tag does not match VersionPrefix $baseVersion in Directory.Build.props."
}
$prerelease = -not [string]::IsNullOrEmpty($Matches['suffix'])
$version = $Tag.Substring(1)
$notes = $null
if ($RequireReleaseNotes) {
    $changelog = Get-Content -LiteralPath (Join-Path $workspaceRoot "CHANGELOG.md") -Raw
    $sectionPattern = '(?ms)^## \[' + [regex]::Escape($version) + '\] - (?<date>\d{4}-\d{2}-\d{2})\r?\n(?<notes>.*?)(?=^## |\z)'
    $sections = [regex]::Matches($changelog, $sectionPattern)
    if ($sections.Count -ne 1) {
        throw "CHANGELOG.md must contain exactly one '## [$version] - YYYY-MM-DD' section."
    }
    $null = [datetime]::ParseExact($sections[0].Groups['date'].Value, 'yyyy-MM-dd', [cultureinfo]::InvariantCulture)
    $notes = $sections[0].Groups['notes'].Value.Trim()
    if ([string]::IsNullOrWhiteSpace($notes)) {
        throw "The changelog section for $version must not be empty."
    }
}

[pscustomobject]@{
    Tag = $Tag
    Version = $version
    BaseVersion = $baseVersion
    FileVersion = "$baseVersion.0"
    Prerelease = $prerelease
    Notes = $notes
}
