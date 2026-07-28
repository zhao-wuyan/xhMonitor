# XhMonitor 绿色版发布脚本 (PowerShell)
# 用法: .\publish.ps1 [-Version "1.0.0"] [-SkipDesktop] [-SkipService] [-NoZip] [-Lite] [-Debug] [-Help]
# 使用 -Help 或 -h 查看详细帮助信息

param(
    [string]$Version,
    [switch]$SkipDesktop,
    [switch]$SkipService,
    [switch]$NoZip,
    [switch]$Lite,  # 轻量级模式，lhm-bridge 使用系统 .NET 8 Runtime
    [switch]$Debug,  # Debug 模式，使用 Rust/bridge Debug 构建并保留符号文件
    [Alias("h")]
    [switch]$Help  # 显示帮助信息
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# 从 Directory.Build.props 读取默认版本号
if (-not $Version) {
    $buildPropsPath = Join-Path $PSScriptRoot "Directory.Build.props"
    if (Test-Path $buildPropsPath) {
        [xml]$buildProps = Get-Content $buildPropsPath
        $Version = $buildProps.Project.PropertyGroup.Version
        Write-Host "从 Directory.Build.props 读取版本号: $Version" -ForegroundColor Cyan
    } else {
        $Version = "0.1.0"
        Write-Host "警告: 未找到 Directory.Build.props，使用默认版本号: $Version" -ForegroundColor Yellow
    }
}

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
    Write-Host "  -Version <版本号>    指定发布版本号 (默认: 从 Directory.Build.props 读取)" -ForegroundColor White
    Write-Host "                       示例: -Version `"1.2.3`"" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  -SkipDesktop         跳过 Rust 桌面应用构建和打包" -ForegroundColor White
    Write-Host "  -SkipService         跳过 Rust 后端服务及 lhm-bridge 发布和打包" -ForegroundColor White
    Write-Host "  -NoZip               不创建 ZIP 压缩包" -ForegroundColor White
    Write-Host ""
    Write-Host "  -Lite                lhm-bridge 使用 framework-dependent 发布" -ForegroundColor White
    Write-Host "                       目标系统只需 Microsoft.NETCore.App 8 (Windows x64)" -ForegroundColor Gray
    Write-Host "                       Rust Service/Desktop 不受此选项影响" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  -Debug               使用 cargo Debug 构建和 bridge Debug 发布" -ForegroundColor White
    Write-Host "                       保留 PDB 符号文件，用于调试和开发" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  -Help, -h            显示此帮助信息" -ForegroundColor White
    Write-Host ""
    Write-Host "示例:" -ForegroundColor Yellow
    Write-Host "  .\publish.ps1" -ForegroundColor White
    Write-Host "    发布完整版：Rust workspace Release + self-contained single-file bridge" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  .\publish.ps1 -Version `"1.0.0`"" -ForegroundColor White
    Write-Host "    发布 v1.0.0 完整版" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  .\publish.ps1 -Lite -NoZip" -ForegroundColor White
    Write-Host "    发布 framework-dependent bridge 版本，不创建压缩包" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  .\publish.ps1 -Debug -SkipDesktop" -ForegroundColor White
    Write-Host "    Debug 模式，仅构建和打包 Rust Service 与 lhm-bridge" -ForegroundColor Gray
    Write-Host ""
    Write-Host "发布模式:" -ForegroundColor Yellow
    Write-Host "  完整版 (默认)        lhm-bridge 为 win-x64 self-contained single-file" -ForegroundColor White
    Write-Host "                       无需额外安装 .NET Runtime" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  轻量级 (-Lite)       lhm-bridge 为 win-x64 framework-dependent" -ForegroundColor White
    Write-Host "                       仅需 .NET Runtime 8 (Microsoft.NETCore.App)" -ForegroundColor Gray
    Write-Host ""
    Write-Host "输出目录:" -ForegroundColor Yellow
    Write-Host "  release\XhMonitor-v<版本号>\" -ForegroundColor White
    Write-Host "  release\XhMonitor-v<版本号>.zip (如果未使用 -NoZip)" -ForegroundColor White
    Write-Host ""
    exit 0
}

# 确定发布配置
$configuration = if ($Debug) { "Debug" } else { "Release" }
$targetProfile = if ($Debug) { "debug" } else { "release" }
$publishMode = if ($Lite) {
    "轻量级 (lhm-bridge framework-dependent)"
} else {
    "完整版 (lhm-bridge self-contained single-file)"
}
if ($Debug) {
    $publishMode += " [DEBUG]"
}

Write-Host "====================================" -ForegroundColor Cyan
Write-Host "  XhMonitor 绿色版发布脚本" -ForegroundColor Cyan
Write-Host "  发布模式: $publishMode" -ForegroundColor Cyan
Write-Host "  编译配置: $configuration" -ForegroundColor Cyan
Write-Host "====================================" -ForegroundColor Cyan
Write-Host ""

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Destination,

        [Parameter(Mandatory = $true)]
        [string]$DisplayName
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "找不到 $DisplayName：$Source"
    }

    $destinationParent = Split-Path -Parent $Destination
    if ($destinationParent) {
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Copy-RequiredDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Destination,

        [Parameter(Mandatory = $true)]
        [string]$DisplayName
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "找不到 $DisplayName：$Source"
    }

    $destinationParent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}

# 设置路径
$RootDir = $PSScriptRoot
$ReleaseDir = Join-Path $RootDir "release"
$OutputDir = Join-Path $ReleaseDir "XhMonitor-v$Version"
$ServiceDir = Join-Path $OutputDir "Service"
$DesktopDir = Join-Path $OutputDir "Desktop"
$CargoOutputDir = Join-Path $RootDir "target\$targetProfile"
$BridgePublishDir = Join-Path $RootDir "target\bridge-publish"

# 清理旧文件
Write-Host "[1/5] 清理旧的发布文件..." -ForegroundColor Yellow
if (Test-Path $ReleaseDir) {
    Remove-Item $ReleaseDir -Recurse -Force
}
New-Item -ItemType Directory -Path $ServiceDir -Force | Out-Null
New-Item -ItemType Directory -Path $DesktopDir -Force | Out-Null

# 构建 Rust workspace
Write-Host ""
if ($SkipService -and $SkipDesktop) {
    Write-Host "[2/5] 跳过 Rust Service/Desktop 构建" -ForegroundColor Gray
} else {
    $cargoArgs = @("build")
    if (-not $SkipService -and -not $SkipDesktop) {
        $cargoArgs += "--workspace"
        $rustBuildTarget = "workspace"
    } elseif (-not $SkipService) {
        $cargoArgs += @("-p", "xhm-service")
        $rustBuildTarget = "xhm-service"
    } else {
        $cargoArgs += @("-p", "xhm-desktop")
        $rustBuildTarget = "xhm-desktop"
    }

    if (-not $Debug) {
        $cargoArgs += "--release"
    }

    Write-Host "[2/5] 构建 Rust $rustBuildTarget..." -ForegroundColor Yellow
    & cargo @cargoArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "错误: Rust 构建失败！" -ForegroundColor Red
        exit 1
    }
    Write-Host "✓ Rust $rustBuildTarget 构建成功" -ForegroundColor Green
}

# 发布 lhm-bridge
Write-Host ""
if (-not $SkipService) {
    Write-Host "[3/5] 发布 lhm-bridge..." -ForegroundColor Yellow

    if (Test-Path $BridgePublishDir) {
        Remove-Item $BridgePublishDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $BridgePublishDir -Force | Out-Null

    $bridgePublishArgs = @(
        "publish"
        (Join-Path $RootDir "lhm-bridge\lhm-bridge.csproj")
        "-c"
        $configuration
        "-r"
        "win-x64"
        "-o"
        $BridgePublishDir
        "--nologo"
        "-p:Version=$Version"
        "-p:PublishTrimmed=false"
    )

    if ($Lite) {
        $bridgePublishArgs += @(
            "--self-contained"
            "false"
            "-p:PublishSingleFile=false"
        )
    } else {
        $bridgePublishArgs += @(
            "--self-contained"
            "true"
            "-p:PublishSingleFile=true"
        )
    }

    & dotnet @bridgePublishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "错误: lhm-bridge 发布失败！" -ForegroundColor Red
        exit 1
    }

    $bridgeExecutable = Join-Path $BridgePublishDir "lhm-bridge.exe"
    if (-not (Test-Path -LiteralPath $bridgeExecutable -PathType Leaf)) {
        Write-Host "错误: lhm-bridge 发布产物不存在: $bridgeExecutable" -ForegroundColor Red
        exit 1
    }
    Write-Host "✓ lhm-bridge 发布成功" -ForegroundColor Green
} else {
    Write-Host "[3/5] 跳过 lhm-bridge 发布" -ForegroundColor Gray
}

# 组装共享 release 布局
Write-Host ""
Write-Host "[4/5] 组装发布目录..." -ForegroundColor Yellow

if (-not $SkipService) {
    Copy-RequiredFile `
        -Source (Join-Path $CargoOutputDir "xhm-service.exe") `
        -Destination (Join-Path $ServiceDir "xhm-service.exe") `
        -DisplayName "Rust Service 可执行文件"

    if ($Debug) {
        $servicePdb = Join-Path $CargoOutputDir "xhm-service.pdb"
        if (Test-Path -LiteralPath $servicePdb -PathType Leaf) {
            Copy-Item -LiteralPath $servicePdb -Destination $ServiceDir -Force
        }
    }

    Get-ChildItem -LiteralPath $BridgePublishDir -Force |
        Copy-Item -Destination $ServiceDir -Recurse -Force

    Copy-RequiredFile `
        -Source (Join-Path $RootDir "XhMonitor.Service\appsettings.json") `
        -Destination (Join-Path $ServiceDir "appsettings.json") `
        -DisplayName "appsettings.json"

    Copy-RequiredDirectory `
        -Source (Join-Path $RootDir "tools\RyzenAdj") `
        -Destination (Join-Path $ServiceDir "tools\RyzenAdj") `
        -DisplayName "RyzenAdj 工具目录"

    Write-Host "✓ Service 已包含 Rust 二进制、bridge 依赖、配置和 RyzenAdj" -ForegroundColor Green
} else {
    Write-Host "  跳过 Service 文件组装" -ForegroundColor Gray
}

if (-not $SkipDesktop) {
    Copy-RequiredFile `
        -Source (Join-Path $CargoOutputDir "xhm-desktop.exe") `
        -Destination (Join-Path $DesktopDir "xhm-desktop.exe") `
        -DisplayName "Rust Desktop 可执行文件"

    if ($Debug) {
        $desktopPdb = Join-Path $CargoOutputDir "xhm-desktop.pdb"
        if (Test-Path -LiteralPath $desktopPdb -PathType Leaf) {
            Copy-Item -LiteralPath $desktopPdb -Destination $DesktopDir -Force
        }
    }

    Copy-RequiredFile `
        -Source (Join-Path $RootDir "XhMonitor.Desktop\service-endpoints.json") `
        -Destination (Join-Path $DesktopDir "service-endpoints.json") `
        -DisplayName "service-endpoints.json"

    Copy-RequiredFile `
        -Source (Join-Path $RootDir "XhMonitor.Desktop\Assets\icon.ico") `
        -Destination (Join-Path $DesktopDir "Assets\icon.ico") `
        -DisplayName "Desktop 图标"

    Write-Host "✓ Desktop 已包含 Rust 二进制、端点配置和图标" -ForegroundColor Green
} else {
    Write-Host "  跳过 Desktop 文件组装" -ForegroundColor Gray
}
  
# 复制启动/停止脚本
Copy-RequiredFile `
    -Source (Join-Path $RootDir "scripts\启动服务.bat") `
    -Destination (Join-Path $OutputDir "启动服务.bat") `
    -DisplayName "启动服务.bat"
Copy-RequiredFile `
    -Source (Join-Path $RootDir "scripts\停止服务.bat") `
    -Destination (Join-Path $OutputDir "停止服务.bat") `
    -DisplayName "停止服务.bat"

# 创建 README
$systemRequirement = if ($Lite) {
    @"
- Windows 10/11 x64
- lhm-bridge 需要 .NET Runtime 8（Microsoft.NETCore.App 8.0.x，Windows x64）

安装步骤：
1. 访问官方下载页：https://dotnet.microsoft.com/download/dotnet/8.0
2. 安装 ".NET Runtime 8" 的 Windows x64 版本

只需安装上述 .NET Runtime，不需要 .NET SDK 或其他运行时类型。

验证安装：
打开命令提示符，运行：dotnet --list-runtimes
应该看到：
- Microsoft.NETCore.App 8.0.x
"@
} else {
    "- Windows 10/11 x64`n- lhm-bridge 已 self-contained，无需另装 .NET Runtime"
}

$readme = @"
# XhMonitor 绿色版 v$Version ($publishMode)

## 使用说明

1. 双击 "启动服务.bat" 启动应用
2. 双击 "停止服务.bat" 停止应用

## 目录结构

```
XhMonitor-v$Version/
├─ Service/                 # 后端服务
│  ├─ xhm-service.exe
│  ├─ lhm-bridge.exe        # 以及 bridge publish 的全部运行依赖
│  ├─ appsettings.json
│  ├─ logs/                 # 日志目录（自动创建）
│  ├─ xhmonitor.db          # 数据库文件（自动创建）
│  └─ tools/
│     └─ RyzenAdj/
├─ Desktop/                 # 桌面应用
│  ├─ xhm-desktop.exe
│  ├─ service-endpoints.json
│  └─ Assets/
│     └─ icon.ico
├─ 启动服务.bat
├─ 停止服务.bat
└─ README.txt
```

## 配置说明

服务配置文件：`Service\appsettings.json`
桌面端点配置：`Desktop\service-endpoints.json`

### 数据库清理配置

```json
"Database": {
  "RetentionDays": 30,
  "CleanupIntervalHours": 24
}
```

### 监控配置

```json
"Monitor": {
  "IntervalSeconds": 3,
  "SystemUsageIntervalSeconds": 1,
  "Keywords": ["--port 8188", "llama-server"]
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

## 日志与数据库

- 日志位置：`Service\logs\`
- 数据库文件：`Service\xhmonitor.db`
- 首次运行时自动创建所需目录和数据库

## 系统要求

$systemRequirement

## 版本信息

- 版本：v$Version
- 发布模式：$publishMode
- 发布日期：$(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
"@
Set-Content -Path (Join-Path $OutputDir "README.txt") -Value $readme -Encoding UTF8
  
Write-Host "✓ 共享发布目录已组装" -ForegroundColor Green

# 清理符号文件
Write-Host ""
Write-Host "[5/5] 清理发布文件..." -ForegroundColor Yellow
if (-not $Debug) {
    Get-ChildItem -Path $OutputDir -Recurse -File -Filter "*.pdb" | Remove-Item -Force
    Write-Host "✓ 已从非 Debug 发布包移除 PDB" -ForegroundColor Green
} else {
    Write-Host "Debug 模式：保留可用的 PDB 符号文件" -ForegroundColor Yellow
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
