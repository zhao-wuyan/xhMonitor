param(
    [string]$Version = "latest",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$Repo = "FlyGoat/RyzenAdj"
$RootDir = Split-Path -Parent $PSScriptRoot
$RyzenAdjDir = Join-Path $RootDir "tools\RyzenAdj"
$VersionFile = Join-Path $RyzenAdjDir "VERSION.txt"
$RequiredFiles = @(
    "ryzenadj.exe",
    "libryzenadj.dll",
    "WinRing0x64.dll",
    "WinRing0x64.sys",
    "inpoutx64.dll",
    "LICENSE.txt",
    "VERSION.txt"
)

function Get-GitHubRelease {
    param([string]$RequestedVersion)

    $headers = @{
        "User-Agent" = "xhMonitor-build"
        "Accept" = "application/vnd.github+json"
    }

    if ($RequestedVersion -eq "latest") {
        return Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$Repo/releases/latest"
    }

    $tag = if ($RequestedVersion.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
        $RequestedVersion
    } else {
        "v$RequestedVersion"
    }

    return Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$Repo/releases/tags/$tag"
}

function Assert-RyzenAdjFiles {
    param([string]$Directory)

    $missing = @(
        foreach ($file in $RequiredFiles) {
            $path = Join-Path $Directory $file
            if (-not (Test-Path $path -PathType Leaf)) {
                $file
            }
        }
    )

    if ($missing.Count -gt 0) {
        throw "RyzenAdj package is incomplete. Missing: $($missing -join ', ')"
    }
}

function Copy-FirstMatchingFile {
    param(
        [string]$SourceRoot,
        [string]$FileName,
        [string]$DestinationRoot
    )

    $match = Get-ChildItem -LiteralPath $SourceRoot -Recurse -File -Filter $FileName |
        Select-Object -First 1

    if ($null -eq $match) {
        throw "Downloaded RyzenAdj archive did not contain $FileName"
    }

    Copy-Item -LiteralPath $match.FullName -Destination (Join-Path $DestinationRoot $FileName) -Force
}

New-Item -ItemType Directory -Force -Path $RyzenAdjDir | Out-Null

$release = Get-GitHubRelease -RequestedVersion $Version
$targetVersion = [string]$release.tag_name
if ([string]::IsNullOrWhiteSpace($targetVersion)) {
    throw "Failed to resolve RyzenAdj release tag."
}

$currentVersion = if (Test-Path $VersionFile -PathType Leaf) {
    (Get-Content -LiteralPath $VersionFile -Raw).Trim()
} else {
    ""
}

$missingRequiredFile = $false
foreach ($file in $RequiredFiles) {
    if (-not (Test-Path (Join-Path $RyzenAdjDir $file) -PathType Leaf)) {
        $missingRequiredFile = $true
        break
    }
}

if (-not $Force -and -not $missingRequiredFile -and $currentVersion -eq $targetVersion) {
    Write-Host "RyzenAdj $targetVersion is already present."
    Assert-RyzenAdjFiles -Directory $RyzenAdjDir
    exit 0
}

Write-Host "Updating RyzenAdj from '$currentVersion' to '$targetVersion'..."

$assetName = "ryzenadj-win64.zip"
$asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
if ($null -eq $asset -or [string]::IsNullOrWhiteSpace([string]$asset.browser_download_url)) {
    throw "RyzenAdj release $targetVersion does not include $assetName"
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("xhmonitor-ryzenadj-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null

try {
    $zipPath = Join-Path $tempDir $assetName
    $extractDir = Join-Path $tempDir ([System.IO.Path]::GetFileNameWithoutExtension($assetName))

    Write-Host "Downloading $assetName..."
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath

    New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force

    foreach ($file in @("ryzenadj.exe", "libryzenadj.dll", "WinRing0x64.dll", "WinRing0x64.sys", "inpoutx64.dll")) {
        Copy-FirstMatchingFile -SourceRoot $extractDir -FileName $file -DestinationRoot $RyzenAdjDir
    }

    if (-not (Test-Path (Join-Path $RyzenAdjDir "LICENSE.txt") -PathType Leaf)) {
        Invoke-WebRequest -Uri "https://raw.githubusercontent.com/FlyGoat/RyzenAdj/master/LICENSE" `
            -OutFile (Join-Path $RyzenAdjDir "LICENSE.txt")
    }

    Set-Content -LiteralPath $VersionFile -Value $targetVersion -Encoding ASCII
    Assert-RyzenAdjFiles -Directory $RyzenAdjDir

    Write-Host "RyzenAdj $targetVersion is ready in $RyzenAdjDir"
}
finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
