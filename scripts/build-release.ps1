param(
    [string]$Configuration = "Release",
    [switch]$SkipRuntimeRestore
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$buildProps = [xml](Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props") -Raw)
$appVersion = [string]$buildProps.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($appVersion)) { throw "Version is missing from Directory.Build.props." }
$publishDir = Join-Path $repoRoot "artifacts\publish"
$portableDir = Join-Path $repoRoot "artifacts\portable"
$portableZip = Join-Path $repoRoot "artifacts\DirectoryMonitor-$appVersion-win-x64-portable.zip"
$customerStageDir = Join-Path $repoRoot "artifacts\customer-package"
$customerRoot = Join-Path $customerStageDir "Directory-Monitor-$appVersion"
$customerZip = Join-Path $repoRoot "artifacts\Directory-Monitor-$appVersion-Customer-Package.zip"
$manifestPath = Join-Path $repoRoot "artifacts\update-manifest-$appVersion.sample.json"
$releaseNotesEn = Join-Path $repoRoot "docs\releases\$appVersion.en.txt"
$releaseNotesVi = Join-Path $repoRoot "docs\releases\$appVersion.vi.txt"
$solution = Join-Path $repoRoot "DirectoryMonitor.slnx"
$presentationProject = Join-Path $repoRoot "src\DirectoryMonitor.Presentation\DirectoryMonitor.Presentation.csproj"

foreach ($target in @($publishDir, $portableDir, $customerStageDir)) {
    $resolvedParent = [System.IO.Path]::GetFullPath((Split-Path -Parent $target))
    $resolvedTarget = [System.IO.Path]::GetFullPath($target)
    if (-not $resolvedTarget.StartsWith($resolvedParent + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe build output path: $resolvedTarget" }
    if (Test-Path $resolvedTarget) { Remove-Item -LiteralPath $resolvedTarget -Recurse -Force }
}

dotnet restore $solution --locked-mode
if ($LASTEXITCODE -ne 0) { throw "Locked solution restore failed with exit code $LASTEXITCODE." }
dotnet build $solution -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
dotnet test $solution -c $Configuration --no-build --no-restore
if ($LASTEXITCODE -ne 0) { throw "Release tests failed with exit code $LASTEXITCODE." }
if (-not $SkipRuntimeRestore) {
    dotnet restore $presentationProject -r win-x64 --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Self-contained runtime restore failed with exit code $LASTEXITCODE." }
}
dotnet publish $presentationProject -c $Configuration -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Self-contained publish failed with exit code $LASTEXITCODE." }

$innoCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
$inno = $innoCandidates | Select-Object -First 1
$installerPath = Join-Path $repoRoot "artifacts\installer\DirectoryMonitor-Setup-$appVersion-x64.exe"
if ($inno) {
    & $inno "/DMyAppVersion=$appVersion" (Join-Path $repoRoot "installer\DirectoryMonitor.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
} else {
    Write-Warning "Inno Setup 6 is not installed; no update installer was generated."
}

if ((Test-Path -LiteralPath $installerPath) -and (Test-Path -LiteralPath $releaseNotesEn) -and (Test-Path -LiteralPath $releaseNotesVi)) {
    & (Join-Path $repoRoot "scripts\New-UpdateManifest.ps1") `
        -InstallerPath $installerPath `
        -InstallerUrl "https://example.com/releases/DirectoryMonitor-Setup-$appVersion-x64.exe" `
        -ReleaseNotesEnPath $releaseNotesEn `
        -ReleaseNotesViPath $releaseNotesVi `
        -OutputPath $manifestPath | Out-Null
}

New-Item -ItemType Directory -Path $portableDir | Out-Null
New-Item -ItemType Directory -Path (Join-Path $portableDir "Data") | Out-Null
New-Item -ItemType Directory -Path (Join-Path $portableDir "Logs") | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot "installer\Data-README.txt") -Destination (Join-Path $portableDir "Data\README.txt")
Copy-Item -LiteralPath (Join-Path $publishDir "Directory Monitor.exe") -Destination $portableDir
Copy-Item -LiteralPath (Join-Path $publishDir "System.Diagnostics.EventLog.Messages.dll") -Destination $portableDir
Copy-Item -LiteralPath (Join-Path $publishDir "appsettings.json") -Destination $portableDir
Compress-Archive -Path (Join-Path $portableDir "*") -DestinationPath $portableZip -CompressionLevel Optimal -Force

New-Item -ItemType Directory -Path (Join-Path $customerRoot "App\Data") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $customerRoot "App\Logs") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $customerRoot "Documentation") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $customerRoot "Source") -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $publishDir "Directory Monitor.exe") -Destination (Join-Path $customerRoot "App")
Copy-Item -LiteralPath (Join-Path $publishDir "System.Diagnostics.EventLog.Messages.dll") -Destination (Join-Path $customerRoot "App")
Copy-Item -LiteralPath (Join-Path $publishDir "appsettings.json") -Destination (Join-Path $customerRoot "App")
if (Test-Path $installerPath) { Copy-Item -LiteralPath $installerPath -Destination (Join-Path $customerRoot "DirectoryMonitor-Setup-$appVersion-x64.exe") }
if (Test-Path $manifestPath) { Copy-Item -LiteralPath $manifestPath -Destination $customerRoot }
Copy-Item -LiteralPath (Join-Path $repoRoot "installer\Data-README.txt") -Destination (Join-Path $customerRoot "App\Data\README.txt")
Copy-Item -Path (Join-Path $repoRoot "docs\*") -Destination (Join-Path $customerRoot "Documentation") -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot "README.md"), (Join-Path $repoRoot "DIRECTORY_MONITOR_PLAN.md"), (Join-Path $repoRoot "CHANGELOG.md"), (Join-Path $repoRoot "CONTRIBUTING.md"), (Join-Path $repoRoot "SECURITY.md") -Destination $customerRoot
Copy-Item -LiteralPath (Join-Path $repoRoot ".editorconfig"), (Join-Path $repoRoot ".gitattributes"), (Join-Path $repoRoot ".gitignore"), (Join-Path $repoRoot "Directory.Build.props"), (Join-Path $repoRoot "Directory.Packages.props"), (Join-Path $repoRoot "DirectoryMonitor.slnx"), (Join-Path $repoRoot "global.json") -Destination (Join-Path $customerRoot "Source")

foreach ($sourceFolder in @(".github", "src", "tests", "installer", "scripts")) {
    $sourceBase = Join-Path $repoRoot $sourceFolder
    Get-ChildItem -LiteralPath $sourceBase -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object {
            $relative = $_.FullName.Substring($sourceBase.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar)
            $targetFile = Join-Path (Join-Path $customerRoot "Source\$sourceFolder") $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $targetFile) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $targetFile
        }
}
Compress-Archive -Path $customerRoot -DestinationPath $customerZip -CompressionLevel Optimal -Force
