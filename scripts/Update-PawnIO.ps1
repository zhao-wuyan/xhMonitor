# Downloads the PawnIO installer used by the Inno Setup package.
# Source: https://github.com/namazso/PawnIO.Setup/releases

param(
    [string]$Version = "latest"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$RootDir = Split-Path -Parent $PSScriptRoot
$PawnIODir = Join-Path $RootDir "tools\PawnIO"
$InstallerPath = Join-Path $PawnIODir "PawnIO_setup.exe"
$VersionFile = Join-Path $PawnIODir "VERSION.txt"
$Repo = "namazso/PawnIO.Setup"

New-Item -ItemType Directory -Force -Path $PawnIODir | Out-Null

if ($Version -eq "latest") {
    $release = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$Repo/releases/latest" `
        -Headers @{ "User-Agent" = "XhMonitor-PawnIO-Updater" }
} else {
    $release = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$Repo/releases/tags/$Version" `
        -Headers @{ "User-Agent" = "XhMonitor-PawnIO-Updater" }
}

$asset = $release.assets | Where-Object { $_.name -eq "PawnIO_setup.exe" } | Select-Object -First 1
if ($null -eq $asset) {
    throw "Release '$($release.tag_name)' does not include PawnIO_setup.exe"
}

Write-Host "Downloading PawnIO $($release.tag_name)..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $InstallerPath
Set-Content -Path $VersionFile -Value $release.tag_name -Encoding ASCII

Write-Host "PawnIO $($release.tag_name) is ready in $PawnIODir" -ForegroundColor Green
