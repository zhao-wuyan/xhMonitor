@echo off
:: 先禁用延迟扩展，防止感叹号等符号被误解析
setlocal DisableDelayedExpansion
:: 切换到 UTF-8 编码
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

:: 下面的逻辑需要变量延迟扩展，重新开启
endlocal
setlocal EnableDelayedExpansion

echo   [1/3] 正在启动后台服务...
cd Service
if exist "XhMonitor.Service.exe" (
    start /b "" XhMonitor.Service.exe
    cd ..
    echo         [OK] 服务进程已启动
) else (
    cd ..
    echo         [Error] 未找到 XhMonitor.Service.exe
)
echo.

echo   [2/3] 等待服务初始化...
for /L %%i in (1,1,8) do (
    timeout /t 1 /nobreak > nul
)
echo         [OK] 服务初始化完成
echo.

echo   [3/3] 正在启动桌面客户端...
if exist "%~dp0Desktop\XhMonitor.Desktop.exe" (
    start "" "%~dp0Desktop\XhMonitor.Desktop.exe"
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
