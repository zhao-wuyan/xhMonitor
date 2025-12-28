@echo off
chcp 65001 >nul
echo ======================================
echo 测试 Release Lite 模式是否真的启动失败
echo ======================================
echo.

echo [步骤 1] 打包 Release Lite 版本...
powershell -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -Lite -Version "test-lite" -NoZip

if errorlevel 1 (
    echo 打包失败！
    pause
    exit /b 1
)

echo.
echo [步骤 2] 进入 Service 目录...
cd /d "%~dp0release\XhMonitor-vtest-lite\Service"

echo.
echo [步骤 3] 启动 Service...
echo 注意：Release 模式无控制台窗口，需要通过任务管理器查看
start "" XhMonitor.Service.exe

echo.
echo [步骤 4] 等待 5 秒后检查进程...
timeout /t 5 /nobreak >nul

echo.
echo [步骤 5] 检查进程是否在运行...
tasklist | findstr /i "XhMonitor.Service.exe"
if errorlevel 1 (
    echo ❌ Service 未运行！进程启动后立即崩溃
    echo.
    echo 检查日志文件：
    if exist logs\xhmonitor-*.log (
        echo 找到日志文件：
        dir /b logs\xhmonitor-*.log
        echo.
        echo 日志内容：
        type logs\xhmonitor-*.log | more
    ) else (
        echo ⚠ 日志文件不存在！说明程序在日志初始化前就崩溃了
    )
) else (
    echo ✅ Service 正在运行！
    echo.
    echo 检查日志：
    if exist logs\xhmonitor-*.log (
        type logs\xhmonitor-*.log
    )
    echo.
    echo 按任意键停止 Service...
    pause
    taskkill /F /IM XhMonitor.Service.exe
)

echo.
pause
