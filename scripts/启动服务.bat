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
echo              玲珑星核系统监控（第三方）  ^|  v0.1.0  ^|  .NET 8.0   ^|  by 诏无言
echo     ==================================================================================
echo.

endlocal
setlocal EnableDelayedExpansion

echo   [1/3] 正在启动后台服务...

set "ROOT_DIR=%~dp0"
set "SERVICE_DIR=%ROOT_DIR%Service"
set "DESKTOP_DIR=%ROOT_DIR%Desktop"

:: 1. 检查当前目录下的 Service
if exist "%SERVICE_DIR%\XhMonitor.Service.exe" goto :__FOUND_SERVICE

:: 2. 检查上级目录的 release (源码结构: scripts/../release)
set "POTENTIAL_ROOT=%~dp0..\"
if exist "%POTENTIAL_ROOT%release" (
    set "ROOT_DIR=%POTENTIAL_ROOT%"
    set "SERVICE_DIR=!ROOT_DIR!Service"
    set "DESKTOP_DIR=!ROOT_DIR!Desktop"
)
if exist "!SERVICE_DIR!\XhMonitor.Service.exe" goto :__FOUND_SERVICE

:: 3. 检查项目根目录下的 release/<Version>/Service
:: 先重置 ROOT_DIR 为脚本所在目录的上级（项目根）
set "PROJECT_ROOT=%~dp0..\"
if exist "!PROJECT_ROOT!release" (
    for /f "delims=" %%D in ('dir /b /ad /o-d "!PROJECT_ROOT!release" 2^>nul') do (
        set "CHECK_PATH=!PROJECT_ROOT!release\%%D\Service\XhMonitor.Service.exe"
        if exist "!CHECK_PATH!" (
            set "ROOT_DIR=!PROJECT_ROOT!release\%%D\"
            set "SERVICE_DIR=!ROOT_DIR!Service"
            set "DESKTOP_DIR=!ROOT_DIR!Desktop"
            goto :__FOUND_SERVICE
        )
    )
)

:: 未找到
goto :__ERROR_NOT_FOUND

:__FOUND_SERVICE
echo         正在启动: !SERVICE_DIR!\XhMonitor.Service.exe
start "" /d "!SERVICE_DIR!" "XhMonitor.Service.exe"
echo         [OK] 服务进程已启动
echo.
goto :__WAIT_INIT

:__ERROR_NOT_FOUND
echo         [Error] 未找到 XhMonitor.Service.exe
echo         解决方案:
echo           1^) 先在项目根目录运行 publish.ps1 / publish.bat 生成 release 包
echo           2^) 或进入 release\XhMonitor-v* 目录运行 启动服务.bat
echo           3^) 开发调试请运行 start-all.ps1（dotnet run）
echo.
goto :eof

:__WAIT_INIT
echo   [2/3] 等待服务初始化...
for /L %%i in (1,1,8) do (
    timeout /t 1 /nobreak > nul
)
echo         [OK] 服务初始化完成
echo.

echo   [3/3] 正在启动桌面客户端...
if exist "!DESKTOP_DIR!\XhMonitor.Desktop.exe" (
    start "" "!DESKTOP_DIR!\XhMonitor.Desktop.exe"
    echo         [OK] 桌面客户端已启动
) else (
    echo         [Error] 未找到 XhMonitor.Desktop.exe
)
echo.

echo     ==================================================================================
echo                                  启动完成
echo.
echo         SignalR 端口: 35179 (默认)  ^|  Web 端口: 35180 (默认)
echo         端口由 service-endpoints.json 管理，若被占用会自动顺延
echo         日志目录: Service\logs
echo     ==================================================================================
echo.

echo   窗口将在 3 秒后关闭...
timeout /t 3 /nobreak > nul