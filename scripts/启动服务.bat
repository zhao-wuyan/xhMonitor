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

echo   [1/2] 正在查找桌面客户端...

set "ROOT_DIR=%~dp0"
set "DESKTOP_DIR=%ROOT_DIR%Desktop"

:: 1. 检查当前目录下的 Desktop
if exist "%DESKTOP_DIR%\XhMonitor.Desktop.exe" goto :__FOUND_DESKTOP

:: 2. 检查上级目录的 release (源码结构: scripts/../release)
set "POTENTIAL_ROOT=%~dp0..\"
if exist "%POTENTIAL_ROOT%release" (
    set "ROOT_DIR=%POTENTIAL_ROOT%"
    set "DESKTOP_DIR=!ROOT_DIR!Desktop"
)
if exist "!DESKTOP_DIR!\XhMonitor.Desktop.exe" goto :__FOUND_DESKTOP

:: 3. 检查项目根目录下的 release/<Version>/Desktop
set "PROJECT_ROOT=%~dp0..\"
if exist "!PROJECT_ROOT!release" (
    for /f "delims=" %%D in ('dir /b /ad /o-d "!PROJECT_ROOT!release" 2^>nul') do (
        set "CHECK_PATH=!PROJECT_ROOT!release\%%D\Desktop\XhMonitor.Desktop.exe"
        if exist "!CHECK_PATH!" (
            set "ROOT_DIR=!PROJECT_ROOT!release\%%D\"
            set "DESKTOP_DIR=!ROOT_DIR!Desktop"
            goto :__FOUND_DESKTOP
        )
    )
)

:: 未找到
goto :__ERROR_NOT_FOUND

:__FOUND_DESKTOP
echo         找到: !DESKTOP_DIR!\XhMonitor.Desktop.exe
echo.

echo   [2/2] 正在启动桌面客户端（将自动拉起后台服务）...
start "" "!DESKTOP_DIR!\XhMonitor.Desktop.exe"
echo         [OK] 桌面客户端已启动
echo.
goto :__DONE

:__ERROR_NOT_FOUND
echo         [Error] 未找到 XhMonitor.Desktop.exe
echo         解决方案:
echo           1^) 先在项目根目录运行 publish.ps1 / publish.bat 生成 release 包
echo           2^) 或进入 release\XhMonitor-v* 目录运行 启动服务.bat
echo           3^) 开发调试请运行 start-all.ps1（dotnet run）
echo.
goto :eof

:__DONE
echo     ==================================================================================
echo                                  启动完成
echo.
echo         Desktop 会自动启动 Service，无需手动启动
echo         SignalR 端口: 35179 (默认)  ^|  Web 端口: 35180 (默认)
echo         端口由 service-endpoints.json 管理，若被占用会自动顺延
echo         日志目录: Service\logs
echo     ==================================================================================
echo.

echo   窗口将在 3 秒后关闭...
timeout /t 3 /nobreak > nul
