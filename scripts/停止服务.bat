@echo off
:: 切换至 UTF-8 编码以支持中文
chcp 65001 >nul
cd /d "%~dp0"

cls

echo.
echo    _  __ __      __  __            _ __            
echo   ^| ^|/ // /_    /  ^|/  /___  ____  (_) /_____  _____
echo   ^|   // __ \  / /^|_/ / __ \/ __ \/ / __/ __ \/ ___/
echo  /   ^|/ / / / / /  / / /_/ / / / / / /_/ /_/ / /    
echo /_/ ^|_/_/ /_/ /_/  /_/\____/_/ /_/_/\__/\____/_/     
echo.
echo     =========================================================================
echo                           停止所有服务 (Stop Services)
echo     =========================================================================
echo.

echo   [1/5] 正在停止 Rust Service...
taskkill /F /IM xhm-service.exe > nul 2>&1
if %errorlevel% equ 0 (
    echo         [OK] xhm-service.exe 已停止
) else (
    echo         [-] xhm-service.exe 未运行
)
echo.

echo   [2/5] 正在停止 Rust Desktop...
taskkill /F /IM xhm-desktop.exe > nul 2>&1
if %errorlevel% equ 0 (
    echo         [OK] xhm-desktop.exe 已停止
) else (
    echo         [-] xhm-desktop.exe 未运行
)
echo.

echo   [3/5] 正在停止硬件采集 bridge...
taskkill /F /IM lhm-bridge.exe > nul 2>&1
if %errorlevel% equ 0 (
    echo         [OK] lhm-bridge.exe 已停止
) else (
    echo         [-] lhm-bridge.exe 未运行
)
echo.

echo   [4/5] 正在停止旧版 .NET 进程（升级兼容）...
taskkill /F /IM XhMonitor.Service.exe > nul 2>&1
if %errorlevel% equ 0 (
    echo         [OK] XhMonitor.Service.exe 已停止
) else (
    echo         [-] XhMonitor.Service.exe 未运行
)
taskkill /F /IM XhMonitor.Desktop.exe > nul 2>&1
if %errorlevel% equ 0 (
    echo         [OK] XhMonitor.Desktop.exe 已停止
) else (
    echo         [-] XhMonitor.Desktop.exe 未运行
)
echo.

echo   [5/5] 正在停止 WinRing0 驱动...
sc stop WinRing0_1_2_0 > nul 2>&1
if %errorlevel% equ 0 (
    echo         [OK] WinRing0 驱动已停止
) else (
    echo         [-] WinRing0 驱动未运行或无需停止
)
echo.

echo     =========================================================================
echo                              操作完成 (Operation Complete)
echo     =========================================================================
echo.

echo   窗口将在 3 秒后关闭...
timeout /t 3 /nobreak > nul
