@echo off
chcp 65001 > nul
setlocal EnableDelayedExpansion

echo ====================================
echo   XhMonitor 绿色版发布脚本
echo ====================================
echo.

::设置版本号
set VERSION=0.1.0
if not "%1"=="" set VERSION=%1

:: 设置输出目录
set OUTPUT_DIR=release\XhMonitor-v%VERSION%
set SERVICE_DIR=%OUTPUT_DIR%\Service
set DESKTOP_DIR=%OUTPUT_DIR%\Desktop

echo [1/5] 清理旧的发布文件...
if exist release rmdir /s /q release
mkdir "%OUTPUT_DIR%"
mkdir "%SERVICE_DIR%"
mkdir "%DESKTOP_DIR%"

echo.
echo [2/5] 发布后端服务 (XhMonitor.Service)...
dotnet publish XhMonitor.Service\XhMonitor.Service.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:PublishTrimmed=false ^
    -o "%SERVICE_DIR%"

if errorlevel 1 (
    echo 错误: 后端服务发布失败！
    exit /b 1
)

echo.
echo [3/5] 发布桌面应用 (XhMonitor.Desktop)...
dotnet publish XhMonitor.Desktop\XhMonitor.Desktop.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:PublishTrimmed=false ^
    -o "%DESKTOP_DIR%"

if errorlevel 1 (
    echo 错误: 桌面应用发布失败！
    exit /b 1
)

echo.
echo [4/5] 复制配置文件和文档...

:: 复制 Service 配置文件
copy /y "XhMonitor.Service\appsettings.json" "%SERVICE_DIR%\appsettings.json" > nul

::创建启动脚本
echo @echo off > "%OUTPUT_DIR%\启动服务.bat"
echo chcp 65001 ^> nul >> "%OUTPUT_DIR%\启动服务.bat"
echo cd /d "%%~dp0" >> "%OUTPUT_DIR%\启动服务.bat"
echo echo 正在启动服务端... >> "%OUTPUT_DIR%\启动服务.bat"
echo cd Service >> "%OUTPUT_DIR%\启动服务.bat"
echo start /b "" XhMonitor.Service.exe >> "%OUTPUT_DIR%\启动服务.bat"
echo cd .. >> "%OUTPUT_DIR%\启动服务.bat"
echo timeout /t 5 /nobreak ^> nul >> "%OUTPUT_DIR%\启动服务.bat"
echo echo 正在启动桌面应用... >> "%OUTPUT_DIR%\启动服务.bat"
echo start "" "%%~dp0Desktop\XhMonitor.Desktop.exe" >> "%OUTPUT_DIR%\启动服务.bat"

:: 创建停止脚本
echo @echo off > "%OUTPUT_DIR%\停止服务.bat"
echo taskkill /F /IM XhMonitor.Service.exe 2^>nul >> "%OUTPUT_DIR%\停止服务.bat"
echo taskkill /F /IM XhMonitor.Desktop.exe 2^>nul >> "%OUTPUT_DIR%\停止服务.bat"
echo echo 服务已停止 >> "%OUTPUT_DIR%\停止服务.bat"
echo pause >> "%OUTPUT_DIR%\停止服务.bat"

:: 创建 README
echo # XhMonitor 绿色版 v%VERSION% > "%OUTPUT_DIR%\README.txt"
echo. >> "%OUTPUT_DIR%\README.txt"
echo ## 使用说明 >> "%OUTPUT_DIR%\README.txt"
echo. >> "%OUTPUT_DIR%\README.txt"
echo 1. 双击"启动服务.bat"启动应用 >> "%OUTPUT_DIR%\README.txt"
echo 2. 双击"停止服务.bat"停止应用 >> "%OUTPUT_DIR%\README.txt"
echo. >> "%OUTPUT_DIR%\README.txt"
echo ## 配置说明 >> "%OUTPUT_DIR%\README.txt"
echo. >> "%OUTPUT_DIR%\README.txt"
echo - 服务配置文件: Service\appsettings.json >> "%OUTPUT_DIR%\README.txt"
echo - 日志文件位置: Service\logs\ >> "%OUTPUT_DIR%\README.txt"
echo - 数据库文件: Service\xhmonitor.db >> "%OUTPUT_DIR%\README.txt"
echo. >> "%OUTPUT_DIR%\README.txt"
echo ## 配置项说明 >> "%OUTPUT_DIR%\README.txt"
echo. >> "%OUTPUT_DIR%\README.txt"
echo ### 数据库清理 >> "%OUTPUT_DIR%\README.txt"
echo - RetentionDays: 数据保留天数（默认30天） >> "%OUTPUT_DIR%\README.txt"
echo - CleanupIntervalHours: 清理间隔小时数（默认24小时） >> "%OUTPUT_DIR%\README.txt"
echo. >> "%OUTPUT_DIR%\README.txt"
echo ### 监控配置 >> "%OUTPUT_DIR%\README.txt"
echo - IntervalSeconds: 监控间隔秒数 >> "%OUTPUT_DIR%\README.txt"
echo - Keywords: 监控关键词列表 >> "%OUTPUT_DIR%\README.txt"

echo.
echo [5/5] 清理临时文件...
:: 删除不需要的 .pdb 文件
del /s /q "%SERVICE_DIR%\*.pdb" 2>nul
del /s /q "%DESKTOP_DIR%\*.pdb" 2>nul

echo.
echo ====================================
echo   发布完成！
echo ====================================
echo.
echo 输出目录: %OUTPUT_DIR%
echo 版本号: v%VERSION%
echo.
echo 文件结构:
echo   XhMonitor-v%VERSION%\
echo   ├─ Service\              (后端服务)
echo   ├─ Desktop\              (桌面应用)
echo   ├─ 启动服务.bat
echo   ├─ 停止服务.bat
echo   └─ README.txt
echo.

:: 计算发布包大小
for /f "tokens=3" %%a in ('dir "%OUTPUT_DIR%" /s /-c ^| findstr /c:"个文件"') do set SIZE=%%a
echo 发布包大小: %SIZE% 字节
echo.

:: 询问是否打包
set /p PACK="是否压缩为 ZIP 文件？(Y/N): "
if /i "%PACK%"=="Y" (
    echo.
    echo 正在压缩...
    powershell -command "Compress-Archive -Path '%OUTPUT_DIR%' -DestinationPath 'release\XhMonitor-v%VERSION%.zip' -Force"
    if errorlevel 1 (
        echo 警告: 压缩失败，但发布文件已生成
    ) else (
        echo 压缩完成: release\XhMonitor-v%VERSION%.zip
    )
)

echo.
echo 按任意键退出...
pause > nul
