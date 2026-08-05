@echo off
setlocal
chcp 65001 > nul

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
set "EXIT_CODE=%ERRORLEVEL%"

if not "%EXIT_CODE%"=="0" (
    echo.
    echo Rust 发布失败，退出码: %EXIT_CODE%
)

endlocal & exit /b %EXIT_CODE%
