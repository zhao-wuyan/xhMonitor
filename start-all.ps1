# XhMonitor 一键启动脚本

Write-Host "正在启动 XhMonitor..." -ForegroundColor Green

# 启动后端服务
Write-Host "启动后端服务..." -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$PSScriptRoot\XhMonitor.Service'; dotnet run"

# 等待 2 秒让服务启动
Start-Sleep -Seconds 2

# 启动桌面悬浮窗
Write-Host "启动桌面悬浮窗..." -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$PSScriptRoot\XhMonitor.Desktop'; dotnet run"

# 启动 Web 界面
Write-Host "启动 Web 界面..." -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$PSScriptRoot\xhmonitor-web'; npm run dev"

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "所有服务已启动！" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "访问地址：" -ForegroundColor Yellow
Write-Host "  - 后端服务:   http://localhost:35179" -ForegroundColor Cyan
Write-Host "  - Web 界面:   http://localhost:35180" -ForegroundColor Cyan
Write-Host "  - SignalR Hub: http://localhost:35179/hubs/metrics" -ForegroundColor Cyan
Write-Host "  - 桌面悬浮窗: 已显示在屏幕上" -ForegroundColor Cyan
Write-Host ""
