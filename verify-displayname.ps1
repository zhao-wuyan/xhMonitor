# 验证 DisplayName 功能的脚本
Write-Host "=== 验证进程名称显示增强功能 ===" -ForegroundColor Cyan
Write-Host ""

# 1. 检查接口和服务文件
Write-Host "[1/5] 检查核心文件..." -ForegroundColor Yellow
$files = @(
    "XhMonitor.Core\Interfaces\IProcessNameResolver.cs",
    "XhMonitor.Core\Models\ProcessNameRule.cs",
    "XhMonitor.Core\Services\ProcessNameResolver.cs",
    "XhMonitor.Core\Models\ProcessInfo.cs",
    "XhMonitor.Desktop\Models\ProcessInfoDto.cs",
    "XhMonitor.Core\Entities\ProcessMetricRecord.cs"
)

foreach ($file in $files) {
    if (Test-Path $file) {
        Write-Host "  ✓ $file" -ForegroundColor Green
    } else {
        Write-Host "  ✗ $file (缺失)" -ForegroundColor Red
    }
}

# 2. 检查数据库迁移
Write-Host ""
Write-Host "[2/5] 检查数据库迁移..." -ForegroundColor Yellow
$migrationFile = "XhMonitor.Service\Migrations\20260114000000_AddDisplayNameToProcessMetricRecord.cs"
if (Test-Path $migrationFile) {
    Write-Host "  ✓ 迁移文件已创建" -ForegroundColor Green
} else {
    Write-Host "  ✗ 迁移文件缺失" -ForegroundColor Red
}

# 3. 检查 appsettings.json 配置
Write-Host ""
Write-Host "[3/5] 检查配置文件..." -ForegroundColor Yellow
$appsettings = Get-Content "XhMonitor.Service\appsettings.json" -Raw | ConvertFrom-Json
if ($appsettings.Monitor.ProcessNameRules) {
    Write-Host "  ✓ ProcessNameRules 配置存在" -ForegroundColor Green
    Write-Host "    规则数量: $($appsettings.Monitor.ProcessNameRules.Count)" -ForegroundColor Gray
} else {
    Write-Host "  ✗ ProcessNameRules 配置缺失" -ForegroundColor Red
}

# 4. 检查 DI 注册
Write-Host ""
Write-Host "[4/5] 检查依赖注入注册..." -ForegroundColor Yellow
$programCs = Get-Content "XhMonitor.Service\Program.cs" -Raw
if ($programCs -match "IProcessNameResolver.*ProcessNameResolver") {
    Write-Host "  ✓ IProcessNameResolver 已注册" -ForegroundColor Green
} else {
    Write-Host "  ✗ IProcessNameResolver 未注册" -ForegroundColor Red
}

# 5. 编译测试
Write-Host ""
Write-Host "[5/5] 编译测试..." -ForegroundColor Yellow
$buildResult = dotnet build XhMonitor.Service/XhMonitor.Service.csproj -c Release --nologo 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "  ✓ 编译成功" -ForegroundColor Green
} else {
    Write-Host "  ✗ 编译失败" -ForegroundColor Red
    Write-Host $buildResult -ForegroundColor Red
}

Write-Host ""
Write-Host "=== 验证完成 ===" -ForegroundColor Cyan
