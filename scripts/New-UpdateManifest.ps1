param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][string]$InstallerUrl,
    [string]$Version,
    [Parameter(Mandatory = $true)][string]$ReleaseNotesEnPath,
    [Parameter(Mandatory = $true)][string]$ReleaseNotesViPath,
    [string]$OutputPath,
    [datetime]$PublishedAtUtc = [datetime]::UtcNow
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$buildProps = [xml](Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props") -Raw)
$centralVersion = [string]$buildProps.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($centralVersion)) { throw "Version is missing from Directory.Build.props." }
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $centralVersion }
if ($Version -ne $centralVersion) { throw "Version '$Version' does not match centralized version '$centralVersion'." }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must use major.minor.patch." }
$uri = $null
if (-not [Uri]::TryCreate($InstallerUrl, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https') { throw "InstallerUrl must be an absolute HTTPS URL." }
$installer = Get-Item -LiteralPath $InstallerPath
if ($installer.Length -le 0 -or $installer.Length -gt 1GB) { throw "Installer size must be between 1 byte and 1 GB." }
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "artifacts\update-manifest.json"
}

function Read-ReleaseNotes([string]$path) {
    return @(Get-Content -LiteralPath $path -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ } | ForEach-Object { $_ -replace '^[-*]\s+', '' })
}

$manifest = [ordered]@{
    schemaVersion = 1
    version = $Version
    publishedAtUtc = $PublishedAtUtc.ToUniversalTime().ToString('O')
    releaseNotes = [ordered]@{
        en = Read-ReleaseNotes $ReleaseNotesEnPath
        vi = Read-ReleaseNotes $ReleaseNotesViPath
    }
    installer = [ordered]@{
        url = $uri.AbsoluteUri
        sha256 = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash
        sizeBytes = $installer.Length
    }
}

$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $outputFullPath) -Force | Out-Null
$json = $manifest | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText($outputFullPath, $json, (New-Object Text.UTF8Encoding($false)))
Write-Output $outputFullPath
