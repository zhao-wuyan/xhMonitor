@echo off
chcp 65001 >nul
echo ======================================
echo 测试 Debug + Lite 模式打包
echo ======================================
echo.

powershell -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -Lite -Debug -Version "debug-test"

echo.
echo ======================================
echo 打包完成！请查看 release 目录
echo ======================================
echo.
echo 运行 Service 查看错误信息：
echo cd release\XhMonitor-vdebug-test\Service
echo XhMonitor.Service.exe
echo.
pause
