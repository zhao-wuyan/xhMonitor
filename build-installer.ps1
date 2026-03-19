# XhMonitor 安装程序构建脚本
# 用法: .\build-installer.ps1 [-Version "0.2.1"] [-SkipPublish] [-BuildType <Lite|LiteNet8|Full>] [-Help]
#
# 构建类型：
#   - Lite      : Lite 不带 .NET（最精简，约 2MB，需要系统预装 .NET 8）
#   - LiteNet8  : Lite 带 .NET 安装包（中等，约 60MB，可自动安装运行时）
#   - Full      : 全量 self-contained（最大，约 100MB，无需额外运行时）

param(
    [string]$Version,  # 版本号参数（字符串类型）
    [switch]$SkipPublish,        # 跳过发布步骤，直接编译安装程序
    [ValidateSet("Lite", "LiteNet8", "Full")]
    [string]$BuildType = "LiteNet8",  # 构建类型：Lite（不带运行时）/ LiteNet8（带运行时安装包）/ Full（全量）
    [Alias("h")]
    [switch]$Help
)

$ErrorActionPreference = "Stop"

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
    Write-Host "  星核监视器 安装程序构建脚本" -ForegroundColor Cyan
    Write-Host "====================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "用法:" -ForegroundColor Yellow
    Write-Host "  .\build-installer.ps1 [参数]" -ForegroundColor White
    Write-Host ""
    Write-Host "参数:" -ForegroundColor Yellow
    Write-Host "  -Version <版本号>    指定版本号 (默认: 从 Directory.Build.props 读取)" -ForegroundColor White
    Write-Host "                       示例: -Version `"1.0.0`"" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  -SkipPublish         跳过发布步骤，直接编译安装程序" -ForegroundColor White
    Write-Host "                       需要已有发布文件 (release\XhMonitor-v<版本号>\)" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  -BuildType <类型>    构建类型 (默认: LiteNet8)" -ForegroundColor White
    Write-Host "                       Lite     - Lite 不带 .NET（最精简，需系统预装 .NET 8）" -ForegroundColor Gray
    Write-Host "                       LiteNet8 - Lite 带 .NET 安装包（可自动安装运行时）" -ForegroundColor Gray
    Write-Host "                       Full     - 全量 self-contained（无需额外运行时）" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  -Help, -h            显示此帮助信息" -ForegroundColor White
    Write-Host ""
    Write-Host "前置条件:" -ForegroundColor Yellow
    Write-Host "  安装 Inno Setup 6.x" -ForegroundColor White
    Write-Host "  下载地址: https://jrsoftware.org/isdl.php" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  确保 ISCC.exe 在 PATH 中，或安装在默认位置:" -ForegroundColor White
    Write-Host "  - C:\Program Files (x86)\Inno Setup 6\ISCC.exe" -ForegroundColor Gray
    Write-Host "  - C:\Program Files\Inno Setup 6\ISCC.exe" -ForegroundColor Gray
    Write-Host ""
    Write-Host "示例:" -ForegroundColor Yellow
    Write-Host "  .\build-installer.ps1" -ForegroundColor White
    Write-Host "    默认构建：LiteNet8 版本（带 .NET 安装包）" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  .\build-installer.ps1 -BuildType Lite" -ForegroundColor White
    Write-Host "    构建 Lite 版本（最精简，需系统预装 .NET 8）" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  .\build-installer.ps1 -BuildType Full -Version `"1.0.0`"" -ForegroundColor White
    Write-Host "    构建 v1.0.0 全量版本（包含完整运行时）" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  .\build-installer.ps1 -SkipPublish -BuildType LiteNet8" -ForegroundColor White
    Write-Host "    跳过发布，仅编译安装程序（需要已有发布文件）" -ForegroundColor Gray
    Write-Host ""
    Write-Host "安装程序功能:" -ForegroundColor Yellow
    Write-Host "  - 软件名称: 星核监视器 (XhMonitor)" -ForegroundColor White
    Write-Host "  - 创建开始菜单快捷方式" -ForegroundColor White
    Write-Host "  - 创建桌面快捷方式（可选）" -ForegroundColor White
    Write-Host "  - 开机自启动（可选）" -ForegroundColor White
    Write-Host "  - 完整卸载支持（自动停止服务、清理数据）" -ForegroundColor White
    Write-Host "  - 中英文界面支持" -ForegroundColor White
    Write-Host ""
    Write-Host "输出文件命名规则:" -ForegroundColor Yellow
    Write-Host "  Lite:     release\XhMonitor-v<版本号>-Lite-Setup.exe" -ForegroundColor White
    Write-Host "  LiteNet8: release\XhMonitor-v<版本号>-Lite-Net8-Setup.exe" -ForegroundColor White
    Write-Host "  Full:     release\XhMonitor-v<版本号>-Full-Setup.exe" -ForegroundColor White
    Write-Host ""
    exit 0
}

$RootDir = $PSScriptRoot
$InstallerDir = Join-Path $RootDir "installer"
$IssFile = Join-Path $InstallerDir "XhMonitor.iss"

Write-Host ""
Write-Host "====================================" -ForegroundColor Cyan
Write-Host "  星核监视器 安装程序构建" -ForegroundColor Cyan
Write-Host "  版本: v$Version" -ForegroundColor Cyan
Write-Host "  类型: $BuildType" -ForegroundColor Cyan
Write-Host "====================================" -ForegroundColor Cyan
Write-Host ""

# 查找 Inno Setup 编译器
function Find-InnoSetup {
    $possiblePaths = @(
        "ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "C:\my_program\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )

    foreach ($path in $possiblePaths) {
        if ($path -eq "ISCC.exe") {
            $found = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
            if ($found) {
                return $found.Source
            }
        } elseif (Test-Path $path) {
            return $path
        }
    }

    return $null
}

# 检查 Inno Setup
Write-Host "[1/3] 检查 Inno Setup..." -ForegroundColor Yellow
$isccPath = Find-InnoSetup

if (-not $isccPath) {
    Write-Host ""
    Write-Host "错误: 未找到 Inno Setup 编译器 (ISCC.exe)" -ForegroundColor Red
    Write-Host ""
    Write-Host "请安装 Inno Setup 6.x:" -ForegroundColor Yellow
    Write-Host "  下载地址: https://jrsoftware.org/isdl.php" -ForegroundColor White
    Write-Host ""
    exit 1
}

Write-Host "✓ 找到 Inno Setup: $isccPath" -ForegroundColor Green

# 发布应用程序
if (-not $SkipPublish) {
    Write-Host ""
    Write-Host "[2/3] 发布应用程序..." -ForegroundColor Yellow

    $publishScript = Join-Path $RootDir "publish.ps1"
    if (-not (Test-Path $publishScript)) {
        Write-Host "错误: 找不到发布脚本 publish.ps1" -ForegroundColor Red
        exit 1
    }

    # 根据构建类型选择发布模式
    # Lite 和 LiteNet8 都使用 -Lite 发布（framework-dependent）
    # Full 使用完整发布（self-contained）
    if ($BuildType -eq "Lite" -or $BuildType -eq "LiteNet8") {
        & $publishScript -Version $Version -Lite
    } else {
        & $publishScript -Version $Version
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Host "错误: 发布失败！" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host ""
    Write-Host "[2/3] 跳过发布步骤" -ForegroundColor Gray

    # 检查发布文件是否存在
    $releaseDir = Join-Path $RootDir "release\XhMonitor-v$Version"
    if (-not (Test-Path $releaseDir)) {
        Write-Host "错误: 发布目录不存在: $releaseDir" -ForegroundColor Red
        Write-Host "请先运行 .\publish.ps1 -Version `"$Version`" 或移除 -SkipPublish 参数" -ForegroundColor Yellow
        exit 1
    }
}

# 更新 ISS 文件中的版本号
Write-Host ""
Write-Host "[3/3] 编译安装程序..." -ForegroundColor Yellow

try {
    # 使用 ISCC 命令行参数传递版本号和构建类型
    Push-Location $InstallerDir
    $isccArgs = @(
        "/DMyAppVersion=$Version",
        "/DBuildType=$BuildType",
        $IssFile
    )
    & $isccPath @isccArgs

    if ($LASTEXITCODE -ne 0) {
        Write-Host "错误: 安装程序编译失败！" -ForegroundColor Red
        exit 1
    }
} finally {
    Pop-Location
}

# 确定输出文件后缀
$outputSuffix = switch ($BuildType) {
    "Lite" { "Lite" }
    "LiteNet8" { "Lite-Net8" }
    "Full" { "Full" }
    default { "Lite" }
}

$setupFile = Join-Path $RootDir "release\XhMonitor-v$Version-$outputSuffix-Setup.exe"

if (Test-Path $setupFile) {
    $setupSize = [math]::Round((Get-Item $setupFile).Length / 1MB, 2)

    Write-Host ""
    Write-Host "====================================" -ForegroundColor Cyan
    Write-Host "  安装程序构建完成！" -ForegroundColor Green
    Write-Host "====================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "输出文件: $setupFile" -ForegroundColor White
    Write-Host "文件大小: $setupSize MB" -ForegroundColor White
    Write-Host "构建类型: $BuildType" -ForegroundColor White
    Write-Host ""
    Write-Host "安装程序功能:" -ForegroundColor Yellow
    Write-Host "  - 创建开始菜单快捷方式" -ForegroundColor White
    Write-Host "  - 创建桌面快捷方式（可选）" -ForegroundColor White
    Write-Host "  - 开机自启动（可选）" -ForegroundColor White
    Write-Host "  - 完整卸载支持" -ForegroundColor White
    if ($BuildType -eq "LiteNet8") {
        Write-Host "  - 自动安装 .NET 运行时（可选）" -ForegroundColor White
    }
    Write-Host ""
} else {
    Write-Host "错误: 安装程序文件未生成" -ForegroundColor Red
    exit 1
}
