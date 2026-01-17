# XhMonitor 绿色版发布脚本 (PowerShell)
# 用法: .\publish.ps1 [-Version "1.0.0"] [-SkipDesktop] [-SkipService] [-NoZip] [-Lite] [-Debug] [-Help]
# 使用 -Help 或 -h 查看详细帮助信息

param(
    [string]$Version = "0.1.0",
    [switch]$SkipDesktop,
    [switch]$SkipService,
    [switch]$NoZip,
    [switch]$Lite,  # 轻量级模式，不包含.NET运行时
    [switch]$Debug,  # Debug模式，保留符号文件和控制台窗口
    [Alias("h")]
    [switch]$Help  # 显示帮助信息
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# 显示帮助信息
if ($Help) {
    Write-Host ""
    Write-Host "====================================" -ForegroundColor Cyan
    Write-Host "  XhMonitor 绿色版发布脚本" -ForegroundColor Cyan
    Write-Host "====================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "用法:" -ForegroundColor Yellow
    Write-Host "  .\publish.ps1 [参数]" -ForegroundColor White
    Write-Host ""
    Write-Host "参数:" -ForegroundColor Yellow
    Write-Host "  -Version <版本号>    指定发布版本号 (默认: 0.1.0)" -ForegroundColor White
    Write-Host "                       示例: -Version `"1.2.3`"" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  -SkipDesktop         跳过桌面应用发布" -ForegroundColor White
    Write-Host "  -SkipService         跳过后端服务发布" -ForegroundColor White
    Write-Host "  -NoZip               不创建 ZIP 压缩包" -ForegroundColor White
    Write-Host ""
    Write-Host "  -Lite                轻量级模式 (不包含 .NET 运行时)" -ForegroundColor White
    Write-Host "                       需要目标系统预装 .NET 8 Runtime" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  -Debug               Debug 模式 (保留符号文件和控制台窗口)" -ForegroundColor White
    Write-Host "                       用于调试和开发" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  -Help, -h            显示此帮助信息" -ForegroundColor White
    Write-Host ""
    Write-Host "示例:" -ForegroundColor Yellow
    Write-Host "  .\publish.ps1" -ForegroundColor White
    Write-Host "    使用默认设置发布完整版" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  .\publish.ps1 -Version `"1.0.0`"" -ForegroundColor White
    Write-Host "    发布 v1.0.0 版本" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  .\publish.ps1 -Lite -NoZip" -ForegroundColor White
    Write-Host "    发布轻量级版本，不创建压缩包" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  .\publish.ps1 -Debug -SkipDesktop" -ForegroundColor White
    Write-Host "    Debug 模式发布，仅发布后端服务" -ForegroundColor Gray
    Write-Host ""
    Write-Host "发布模式:" -ForegroundColor Yellow
    Write-Host "  完整版 (默认)        包含 .NET 8 运行时，单文件发布" -ForegroundColor White
    Write-Host "                       优点: 无需安装依赖，开箱即用" -ForegroundColor Gray
    Write-Host "                       缺点: 文件体积较大 (~100MB)" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  轻量级 (-Lite)       不包含运行时，需要系统预装 .NET 8" -ForegroundColor White
    Write-Host "                       优点: 文件体积小 (~10MB)" -ForegroundColor Gray
    Write-Host "                       缺点: 需要预装 .NET 8 Runtime" -ForegroundColor Gray
    Write-Host ""
    Write-Host "输出目录:" -ForegroundColor Yellow
    Write-Host "  release\XhMonitor-v<版本号>\" -ForegroundColor White
    Write-Host "  release\XhMonitor-v<版本号>.zip (如果未使用 -NoZip)" -ForegroundColor White
    Write-Host ""
    exit 0
}

# 确定发布配置
$configuration = if ($Debug) { "Debug" } else { "Release" }
$publishMode = if ($Lite) { "轻量级 (需要系统 .NET 8)" } else { "完整版 (含 .NET 运行时)" }
if ($Debug) {
    $publishMode += " [DEBUG]"
}

Write-Host "====================================" -ForegroundColor Cyan
Write-Host "  XhMonitor 绿色版发布脚本" -ForegroundColor Cyan
Write-Host "  发布模式: $publishMode" -ForegroundColor Cyan
Write-Host "  编译配置: $configuration" -ForegroundColor Cyan
Write-Host "====================================" -ForegroundColor Cyan
Write-Host ""
  
# 设置路径
$RootDir = $PSScriptRoot
$OutputDir = Join-Path $RootDir "release\XhMonitor-v$Version"
$ServiceDir = Join-Path $OutputDir "Service"
$DesktopDir = Join-Path $OutputDir "Desktop"
  
# 清理旧文件
Write-Host "[1/5] 清理旧的发布文件..." -ForegroundColor Yellow
if (Test-Path (Join-Path $RootDir "release")) {
    Remove-Item (Join-Path $RootDir "release") -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
New-Item -ItemType Directory -Path $ServiceDir -Force | Out-Null
New-Item -ItemType Directory -Path $DesktopDir -Force | Out-Null
  
# 发布后端服务
if (-not $SkipService) {
    Write-Host ""
    Write-Host "[2/5] 发布后端服务 (XhMonitor.Service)..." -ForegroundColor Yellow

    $publishArgs = @(
        "publish"
        (Join-Path $RootDir "XhMonitor.Service\XhMonitor.Service.csproj")
        "-c"
        $configuration
        "-o"
        $ServiceDir
        "--nologo"
    )

    if ($Lite) {
        # 轻量级模式：不包含运行时，但需要指定 RID 以复制原生依赖
        $publishArgs += "-r"
        $publishArgs += "win-x64"
        $publishArgs += "--self-contained"
        $publishArgs += "false"
    } else {
        # 完整模式：包含运行时，单文件
        $publishArgs += "-r"
        $publishArgs += "win-x64"
        $publishArgs += "--self-contained"
        $publishArgs += "true"
        $publishArgs += "-p:PublishSingleFile=true"
        $publishArgs += "-p:IncludeNativeLibrariesForSelfExtract=true"
        $publishArgs += "-p:PublishTrimmed=false"
    }

    & dotnet @publishArgs

    if ($LASTEXITCODE -ne 0) {
        Write-Host "错误: 后端服务发布失败！" -ForegroundColor Red
        exit 1
    }
    Write-Host "✓ 后端服务发布成功" -ForegroundColor Green
} else {
    Write-Host "[2/5] 跳过后端服务发布" -ForegroundColor Gray
}
  
# 发布桌面应用
if (-not $SkipDesktop) {
    Write-Host ""
    Write-Host "[3/5] 发布桌面应用 (XhMonitor.Desktop)..." -ForegroundColor Yellow

    $publishArgs = @(
        "publish"
        (Join-Path $RootDir "XhMonitor.Desktop\XhMonitor.Desktop.csproj")
        "-c"
        $configuration
        "-o"
        $DesktopDir
        "--nologo"
    )

    if ($Lite) {
        # 轻量级模式：不包含运行时，但需要指定 RID 以复制原生依赖
        $publishArgs += "-r"
        $publishArgs += "win-x64"
        $publishArgs += "--self-contained"
        $publishArgs += "false"
    } else {
        # 完整模式：包含运行时，单文件
        $publishArgs += "-r"
        $publishArgs += "win-x64"
        $publishArgs += "--self-contained"
        $publishArgs += "true"
        $publishArgs += "-p:PublishSingleFile=true"
        $publishArgs += "-p:IncludeNativeLibrariesForSelfExtract=true"
        $publishArgs += "-p:PublishTrimmed=false"
    }

    & dotnet @publishArgs

    if ($LASTEXITCODE -ne 0) {
        Write-Host "错误: 桌面应用发布失败！" -ForegroundColor Red
        exit 1
    }
    Write-Host "✓ 桌面应用发布成功" -ForegroundColor Green
} else {
    Write-Host "[3/5] 跳过桌面应用发布" -ForegroundColor Gray
}
  
# 复制配置文件和创建脚本
Write-Host ""
Write-Host "[4/5] 复制配置文件和文档..." -ForegroundColor Yellow

# 复制 Service 配置文件（强制覆盖，确保发布后有配置）
if (-not $SkipService) {
    $sourceConfig = Join-Path $RootDir "XhMonitor.Service\appsettings.json"
    $destConfig = Join-Path $ServiceDir "appsettings.json"

    if (Test-Path $sourceConfig) {
        Copy-Item $sourceConfig $destConfig -Force
        Write-Host "✓ 已复制 appsettings.json" -ForegroundColor Green
    } else {
        Write-Host "警告: 找不到 appsettings.json" -ForegroundColor Yellow
    }

    # 如果是轻量级模式，需要确保 Migrations 目录被复制
    if ($Lite) {
        $sourceMigrations = Join-Path $RootDir "XhMonitor.Service\Migrations"
        $destMigrations = Join-Path $ServiceDir "Migrations"

        if (Test-Path $sourceMigrations) {
            Copy-Item $sourceMigrations $destMigrations -Recurse -Force
            Write-Host "✓ 已复制 Migrations 目录" -ForegroundColor Green
        }
    }
}
  
# 复制启动/停止脚本
Copy-Item (Join-Path $RootDir "scripts\启动服务.bat") (Join-Path $OutputDir "启动服务.bat") -Force
Copy-Item (Join-Path $RootDir "scripts\停止服务.bat") (Join-Path $OutputDir "停止服务.bat") -Force

# 创建 README
$systemRequirement = if ($Lite) {
    "- Windows 10/11 x64`n- 需要预先安装 .NET 8 Runtime`n- 下载地址: https://dotnet.microsoft.com/download/dotnet/8.0"
} else {
    "- Windows 10/11 x64`n- 无需安装 .NET Runtime（已包含）"
}

$readme = @"
# XhMonitor 绿色版 v$Version ($publishMode)

## 使用说明

1. 双击 "启动服务.bat" 启动应用
2. 双击 "停止服务.bat" 停止应用

## 目录结构

```
XhMonitor-v$Version/
├─ Service/              # 后端服务
│  ├─ XhMonitor.Service.exe
│  ├─ appsettings.json   # 配置文件
│  ├─ logs/              # 日志目录（自动创建）
│  └─ xhmonitor.db       # 数据库文件（自动创建）
├─ Desktop/              # 桌面应用
│  └─ XhMonitor.Desktop.exe
├─ 启动服务.bat
├─ 停止服务.bat
└─ README.txt
```
 
## 配置说明
 
配置文件位置: `Service\appsettings.json`
 
### 数据库清理配置
 
```json
"Database": {
  "RetentionDays": 30,           // 数据保留天数（默认30天）
  "CleanupIntervalHours": 24     // 清理间隔小时数（默认24小时）
}
```
 
### 监控配置
 
```json
"Monitor": {
  "IntervalSeconds": 3,                    // 监控间隔秒数
  "Keywords": ["--port 8188", "llama-server"]  // 监控关键词
}
```
 
### 服务器配置
 
```json
"Server": {
  "Host": "localhost",
  "Port": 35179,
  "HubPath": "/hubs/metrics"
}
```
 
## 日志管理
 
- 日志位置: `Service\logs\`
- 日志轮转: 每日或达到50MB时自动轮转
- 保留期限: 最近7天
 
## 数据库管理
 
- 数据库文件: `Service\xhmonitor.db`
- 自动清理: 每24小时清理30天前的数据
- 自动优化: 清理后执行 VACUUM 优化
 
## 系统要求

$systemRequirement

## 版本信息

- 版本: v$Version
- 发布模式: $publishMode
- 发布日期: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
"@
Set-Content -Path (Join-Path $OutputDir "README.txt") -Value $readme -Encoding UTF8
  
Write-Host "✓ 配置文件和文档已创建" -ForegroundColor Green
  
# 清理临时文件
Write-Host ""
Write-Host "[5/5] 清理临时文件..." -ForegroundColor Yellow
if (-not $Debug) {
    Get-ChildItem -Path $OutputDir -Recurse -Filter "*.pdb" | Remove-Item -Force
    Write-Host "✓ 临时文件已清理" -ForegroundColor Green
} else {
    Write-Host "⚠ Debug 模式：保留 .pdb 符号文件用于调试" -ForegroundColor Yellow
}
  
# 计算大小
$totalSize = (Get-ChildItem -Path $OutputDir -Recurse | Measure-Object -Property Length -Sum).Sum
$sizeInMB = [math]::Round($totalSize / 1MB, 2)
  
Write-Host ""
Write-Host "====================================" -ForegroundColor Cyan
Write-Host "  发布完成！" -ForegroundColor Green
Write-Host "====================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "输出目录: $OutputDir" -ForegroundColor White
Write-Host "版本号: v$Version" -ForegroundColor White
Write-Host "发布包大小: $sizeInMB MB" -ForegroundColor White
Write-Host ""
  
# 压缩
if (-not $NoZip) {
    $zipPath = Join-Path $RootDir "release\XhMonitor-v$Version.zip"
    Write-Host "正在压缩..." -ForegroundColor Yellow
    Compress-Archive -Path $OutputDir -DestinationPath $zipPath -Force
  
    $zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Write-Host "✓ 压缩完成: release\XhMonitor-v$Version.zip ($zipSize MB)" -ForegroundColor Green
    Write-Host ""
}
  
Write-Host "发布成功！" -ForegroundColor Green