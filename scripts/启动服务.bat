@echo off
setlocal DisableDelayedExpansion
chcp 65001 >nul
cd /d "%~dp0"
cls

echo.
echo     __  __ __  __   __  __            _ __
echo     \ \/ // / / /  /  ^|/  /___  ____  (_) /_____  _____
echo      \  // /_/ /  / /^|_/ / __ \/ __ \/ / __/ __ \/ ___/
echo      / /\ __  /  / /  / / /_/ / / / / / /_/ /_/ / /
echo     /_/\_\ /_/  /_/  /_/\____/_/ /_/_/\__/\____/_/
echo.
echo     ==================================================================================
echo              玲珑星核系统监控（第三方）  ^|  Rust Edition  ^|  by 诏无言
echo     ==================================================================================
echo.

endlocal
setlocal EnableDelayedExpansion

echo   [1/3] 正在定位 Rust 发布程序...
echo.

set "ROOT_DIR=%~dp0"
set "SERVICE_DIR=!ROOT_DIR!Service"
set "DESKTOP_DIR=!ROOT_DIR!Desktop"
set "SERVICE_EXE=!SERVICE_DIR!\xhm-service.exe"
set "DESKTOP_EXE=!DESKTOP_DIR!\xhm-desktop.exe"
set "HAVE_SERVICE=0"
set "HAVE_DESKTOP=0"

if exist "!SERVICE_EXE!" set "HAVE_SERVICE=1"
if exist "!DESKTOP_EXE!" set "HAVE_DESKTOP=1"
if exist "!SERVICE_EXE!" if exist "!DESKTOP_EXE!" goto :__FOUND_RELEASE

set "RELEASE_DIR=%~dp0..\release"
if exist "!RELEASE_DIR!\" (
    for /f "delims=" %%D in ('dir /b /ad /o-d "!RELEASE_DIR!" 2^>nul') do (
        set "CANDIDATE_ROOT=!RELEASE_DIR!\%%D"
        if exist "!CANDIDATE_ROOT!\Service\xhm-service.exe" set "HAVE_SERVICE=1"
        if exist "!CANDIDATE_ROOT!\Desktop\xhm-desktop.exe" set "HAVE_DESKTOP=1"
        if exist "!CANDIDATE_ROOT!\Service\xhm-service.exe" if exist "!CANDIDATE_ROOT!\Desktop\xhm-desktop.exe" (
            set "ROOT_DIR=!CANDIDATE_ROOT!"
            set "SERVICE_DIR=!ROOT_DIR!\Service"
            set "DESKTOP_DIR=!ROOT_DIR!\Desktop"
            set "SERVICE_EXE=!SERVICE_DIR!\xhm-service.exe"
            set "DESKTOP_EXE=!DESKTOP_DIR!\xhm-desktop.exe"
            goto :__FOUND_RELEASE
        )
    )
)

echo         [Error] 未找到完整的 Rust 发布程序
if "!HAVE_SERVICE!"=="0" echo         [Error] 未找到 Service\xhm-service.exe
if "!HAVE_DESKTOP!"=="0" echo         [Error] 未找到 Desktop\xhm-desktop.exe
if "!HAVE_SERVICE!"=="1" if "!HAVE_DESKTOP!"=="1" echo         [Error] 两个程序不在同一发布根目录
echo.
echo         请先生成 release\XhMonitor-v版本号 发布包，
echo         或从包含 Service 和 Desktop 目录的发布根目录运行本脚本。
echo.
endlocal
exit /b 1

:__FOUND_RELEASE
echo         Service: !SERVICE_EXE!
echo         Desktop: !DESKTOP_EXE!
echo.

set "RUST_LOG=info"
set "SLINT_BACKEND=winit-software"

echo         正在清理已运行的监控进程...
taskkill /F /IM xhm-service.exe > nul 2>&1
taskkill /F /IM xhm-desktop.exe > nul 2>&1
taskkill /F /IM lhm-bridge.exe > nul 2>&1
taskkill /F /IM XhMonitor.Service.exe > nul 2>&1
taskkill /F /IM XhMonitor.Desktop.exe > nul 2>&1
timeout /t 1 /nobreak > nul
powershell.exe -NoLogo -NoProfile -NonInteractive -Command "if (Get-NetTCPConnection -LocalPort 35179 -State Listen -ErrorAction SilentlyContinue) { exit 1 } else { exit 0 }" > nul 2>&1
if errorlevel 1 goto :__PORT_OCCUPIED
goto :__PORT_FREE

:__PORT_OCCUPIED
echo.
echo         [Error] 端口 35179 仍处于 Listen 状态
echo         [Error] Port 35179 is occupied. Stop the elevated or other process first.
echo         [Error] 未启动 Rust Service 和 Rust Desktop
echo.
endlocal
exit /b 1

:__PORT_FREE
echo.

echo   [2/3] 正在启动 Rust Service...
start "" /D "!SERVICE_DIR!" "!SERVICE_EXE!"
for /l %%I in (1,1,10) do (
    powershell.exe -NoLogo -NoProfile -NonInteractive -Command "try { $response = Invoke-RestMethod -Uri 'http://127.0.0.1:35179/api/v1/config/health' -TimeoutSec 1; if ($response.status -eq 'Healthy') { exit 0 }; exit 1 } catch { exit 1 }" > nul 2>&1
    if !errorlevel! equ 0 goto :__SERVICE_HEALTHY
    if %%I lss 10 timeout /t 1 /nobreak > nul
)

echo         [Error] Rust Service 未在约 10 秒内报告 Healthy
echo         [Error] 未启动 Rust Desktop，请检查 Service 配置与端口占用
taskkill /F /IM xhm-service.exe > nul 2>&1
taskkill /F /IM lhm-bridge.exe > nul 2>&1
echo.
endlocal
exit /b 1

:__SERVICE_HEALTHY
echo         [OK] Rust Service 已启动，健康状态: Healthy
echo.

echo   [3/3] 正在启动 Rust Desktop...
start "" /D "!DESKTOP_DIR!" "!DESKTOP_EXE!"
echo         [OK] Rust Desktop 已启动
echo.

echo     ==================================================================================
echo                                  启动完成
echo.
echo         发布目录: !ROOT_DIR!
echo         Service 工作目录: !SERVICE_DIR!
echo         Service 地址: http://127.0.0.1:35179
echo         日志级别: info (RUST_LOG)
echo         Desktop 渲染后端: winit-software
echo     ==================================================================================
echo.

echo   窗口将在 3 秒后关闭...
timeout /t 3 /nobreak > nul
endlocal
