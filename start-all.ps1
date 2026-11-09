# XhMonitor 一键启动脚本

$ErrorActionPreference = 'Stop'

Write-Host "正在启动 XhMonitor..." -ForegroundColor Green

# Vite 必须先占用开发端口，Desktop 和 Rust gateway 才能避开它。
if (Get-NetTCPConnection -LocalPort 35180 -State Listen -ErrorAction SilentlyContinue) {
    throw '端口 35180 已被占用，请先关闭已有的 Web 服务。'
}

$webDirectory = (Join-Path $PSScriptRoot 'xhmonitor-web').Replace("'", "''")
$desktopDirectory = (Join-Path $PSScriptRoot 'XhMonitor.Desktop').Replace("'", "''")
Write-Host "启动 Web 开发服务器..." -ForegroundColor Yellow
$webProcess = Start-Process powershell -PassThru -ArgumentList '-NoProfile', '-Command', "Set-Location -LiteralPath '$webDirectory'; npm run dev; exit `$LASTEXITCODE"

$deadline = (Get-Date).AddSeconds(30)
while (-not (Get-NetTCPConnection -LocalPort 35180 -State Listen -ErrorAction SilentlyContinue)) {
    if ($webProcess.HasExited) {
        throw "Vite 启动失败，退出码：$($webProcess.ExitCode)"
    }
    if ((Get-Date) -ge $deadline) {
        taskkill /PID $webProcess.Id /T /F | Out-Null
        throw '等待 Vite 监听端口 35180 超时。'
    }
    Start-Sleep -Milliseconds 200
}

# Desktop 自动拉起 Rust 后端，无需再启动第二个 Service 实例。
Write-Host "启动 C# 桌面悬浮窗（将自动拉起 Rust 后端服务）..." -ForegroundColor Yellow
Start-Process powershell -ArgumentList '-NoExit', '-NoProfile', '-Command', "Set-Location -LiteralPath '$desktopDirectory'; dotnet run"

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "启动命令已发送，请查看各窗口的运行日志。" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "访问地址：" -ForegroundColor Yellow
Write-Host "  - 后端服务:   http://localhost:35179" -ForegroundColor Cyan
Write-Host "  - Web 界面:   http://localhost:35180" -ForegroundColor Cyan
Write-Host "  - SignalR Hub: http://localhost:35179/hubs/metrics" -ForegroundColor Cyan
Write-Host "  - 桌面悬浮窗: 正在构建并启动" -ForegroundColor Cyan
Write-Host ""
Write-Host "注意: C# Desktop 会自动启动 Rust Service，无需手动启动" -ForegroundColor DarkGray
Write-Host ""
