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

echo   [1/2] 正在停止后台服务...
taskkill /F /IM XhMonitor.Service.exe > nul 2>&1
if %errorlevel% equ 0 (
    echo         [OK] 后台服务已停止
) else (
    echo         [-] 后台服务未运行
)
echo.

echo   [2/2] 正在停止桌面客户端...
taskkill /F /IM XhMonitor.Desktop.exe > nul 2>&1
if %errorlevel% equ 0 (
    echo         [OK] 桌面客户端已停止
) else (
    echo         [-] 桌面客户端未运行
)
echo.

echo     =========================================================================
echo                              操作完成 (Operation Complete)
echo     =========================================================================
echo.

echo   窗口将在 3 秒后关闭...
timeout /t 3 /nobreak > nul
