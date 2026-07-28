# xhMonitor Rust Service 手动测试与打包指南

本文面向 Windows x64 开发机，覆盖 `xhm-core`、`xhm-service` 和 `lhm-bridge` 的手动测试、release 运行、ZIP 打包与解包验收。

当前打包结果是便携式 ZIP，不是安装程序。P2 Desktop 和 P3 安装器/正式端口切换不在本文范围内。

## 1. 环境要求

在仓库根目录打开 PowerShell。

必需工具：

- Rust 1.82 或更高版本，使用 MSVC toolchain。
- .NET SDK 8.0。
- Visual Studio 2022 Build Tools，包含 C++ 桌面生成工具。
- Windows PowerShell 5.1 或 PowerShell 7。

检查版本：

```powershell
rustc --version
cargo --version
dotnet --version
```

仓库根目录应包含：

```text
Cargo.toml
xhm-core\
xhm-service\
lhm-bridge\
tools\RyzenAdj\
XhMonitor.Service\appsettings.json
```

## 2. 运行自动化测试

### 2.1 Rust 完整门禁

```powershell
cargo fmt --check
cargo test --workspace
cargo clippy --workspace --all-targets -- -D warnings
```

预期：

- `cargo fmt --check` 无 diff。
- `cargo test --workspace` 全部通过。当前基线为 158 tests。
- `cargo clippy` 零警告。

如果格式检查失败，执行：

```powershell
cargo fmt
cargo fmt --check
```

### 2.2 Rust 分模块测试

排查问题时可单独运行：

```powershell
cargo test -p xhm-core
cargo test -p xhm-service
cargo test -p xhm-service --lib power::tests
cargo test -p xhm-service --lib api::power::tests
cargo test -p xhm-service lhm::tests -- --nocapture
```

### 2.3 LHM bridge 对等测试

```powershell
dotnet test XhMonitor.Tests\XhMonitor.Tests.csproj `
  --filter "FullyQualifiedName~LhmBridgeSelectionTests"
```

该测试覆盖：

- CPU/GPU 温度 sensor 优先级。
- 多 GPU load 聚合。
- 物理/虚拟网卡吞吐选择。
- 低速网络吞吐精度。
- 连续采集失败预算。
- 未使用的 LHM memory subtree 禁用状态。

需要运行整个既有 .NET 测试集时：

```powershell
dotnet test XhMonitor.Tests\XhMonitor.Tests.csproj
```

## 3. 构建 release 产物

### 3.1 构建 Rust Service

```powershell
cargo build -p xhm-service --release
```

产物：

```text
target\release\xhm-service.exe
```

### 3.2 发布 self-contained LHM bridge

```powershell
Remove-Item target\bridge-publish -Recurse -Force -ErrorAction SilentlyContinue
```

```powershell
dotnet publish lhm-bridge\lhm-bridge.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o target\bridge-publish
```

`target\bridge-publish` 至少包含：

```text
lhm-bridge.exe
libMonoPosixHelper.dll
MonoPosixHelper.dll
```

不要只复制 `lhm-bridge.exe`。两个 Posix helper DLL 是 bridge signal/优雅退出路径的运行依赖。`.pdb` 仅用于调试，发布包可以不带。

## 4. 组装便携包

在仓库根目录执行以下 PowerShell：

```powershell
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path .).Path
$BridgePublish = Join-Path $Root "target\bridge-publish"
$PackageDir = Join-Path $Root "target\package\xhm-service-win-x64"
$ZipPath = Join-Path $Root "target\package\xhm-service-win-x64.zip"

Remove-Item $PackageDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $ZipPath -Force -ErrorAction SilentlyContinue
New-Item $PackageDir -ItemType Directory -Force | Out-Null

# Rust Service
Copy-Item "$Root\target\release\xhm-service.exe" $PackageDir -Force

# LHM bridge 及其 publish 依赖；PDB 可省略
Get-ChildItem $BridgePublish -File |
  Where-Object { $_.Extension -ne ".pdb" } |
  Copy-Item -Destination $PackageDir -Force

# Power 设备白名单与 SchemeProfiles
Copy-Item "$Root\XhMonitor.Service\appsettings.json" $PackageDir -Force

# RyzenAdj native + CLI 完整工具链
$RyzenAdjDir = Join-Path $PackageDir "tools\RyzenAdj"
New-Item $RyzenAdjDir -ItemType Directory -Force | Out-Null
Copy-Item "$Root\tools\RyzenAdj\*" $RyzenAdjDir -Recurse -Force

# 创建 ZIP
New-Item (Split-Path $ZipPath) -ItemType Directory -Force | Out-Null
Compress-Archive `
  -Path "$PackageDir\*" `
  -DestinationPath $ZipPath `
  -CompressionLevel Optimal

Write-Host "Package: $PackageDir"
Write-Host "ZIP:     $ZipPath"
```

最终目录必须是以下同级布局：

```text
xhm-service-win-x64\
├── xhm-service.exe
├── lhm-bridge.exe
├── libMonoPosixHelper.dll
├── MonoPosixHelper.dll
├── appsettings.json
└── tools\
    └── RyzenAdj\
        ├── libryzenadj.dll
        ├── ryzenadj.exe
        ├── WinRing0x64.dll
        ├── WinRing0x64.sys
        ├── inpoutx64.dll
        ├── VERSION.txt
        ├── README.md
        └── LICENSE.txt
```

路径不能调整：

- `lhm-bridge.exe` 必须与 `xhm-service.exe` 同级。
- `appsettings.json` 必须与 `xhm-service.exe` 同级，否则 Power 设备/profile 配置不会加载。
- RyzenAdj 固定位于 `tools\RyzenAdj\`。
- `xhmonitor.db` 会在 `xhm-service.exe` 同级自动创建。
- `data\widget-settings.json` 在首次保存 Widget 设置时自动创建。

不要把开发机上的 `xhmonitor.db`、`xhmonitor.db-wal`、`xhmonitor.db-shm` 打入新安装包。升级已有安装时，应先停止 Service，再单独备份并保留用户数据库。

## 5. 运行打包结果

### 5.1 启动 Service

```powershell
$PackageDir = (Resolve-Path "target\package\xhm-service-win-x64").Path
$env:RUST_LOG = "info"
& "$PackageDir\xhm-service.exe"
```

预期日志包含：

```text
listening on 127.0.0.1:35181
lhm-bridge started
```

Service 只监听 loopback，不应监听 `0.0.0.0`。

### 5.2 验证健康状态与监听面

另开一个 PowerShell：

```powershell
Invoke-RestMethod http://127.0.0.1:35181/api/v1/config/health

Get-NetTCPConnection -LocalPort 35181 -State Listen |
  Select-Object LocalAddress, LocalPort, OwningProcess
```

预期：

- health 返回 `status: Healthy`、`database: Connected`。
- `LocalAddress` 仅为 `127.0.0.1`，不能是 `0.0.0.0`。

确认执行的确实是打包目录中的 Service 和 bridge：

```powershell
Get-Process xhm-service, lhm-bridge |
  Select-Object Id, ProcessName, Path
```

两个 `Path` 都应指向 `target\package\xhm-service-win-x64`。

### 5.3 验证 REST 与实时推送

```powershell
Invoke-RestMethod http://127.0.0.1:35181/api/v1/config
Invoke-RestMethod http://127.0.0.1:35181/api/v1/metrics/latest

try {
    Invoke-RestMethod http://127.0.0.1:35181/api/v1/power/status
} catch {
    Write-Host "Power status HTTP:" ([int]$_.Exception.Response.StatusCode)
}
```

Power endpoint 在非支持设备、未配置 profile 或未部署驱动时返回 403/404/503 属于预期拒绝；不能为了通过测试而绕过设备/profile gate。

验证 SSE：

```powershell
curl.exe -N "http://127.0.0.1:35181/api/v1/events?mode=full"
```

应持续看到：

```text
event: ReceiveSystemUsage
data: {...}
```

事件通常约每秒一次。按 `Ctrl+C` 停止 `curl.exe`。

验证 SignalR negotiate：

```powershell
Invoke-RestMethod `
  -Method Post `
  "http://127.0.0.1:35181/hubs/metrics/negotiate?negotiateVersion=1"
```

返回值的 `availableTransports` 应包含 `WebSockets`。

### 5.4 验证组合内存

Service 和 bridge 运行后执行：

```powershell
$PackageDir = (Resolve-Path "target\package\xhm-service-win-x64").Path
$samples = @()

0..60 | ForEach-Object {
    $service = Get-Process xhm-service |
      Where-Object { $_.Path -like "$PackageDir\*" } |
      Select-Object -First 1
    $bridge = Get-Process lhm-bridge |
      Where-Object { $_.Path -like "$PackageDir\*" } |
      Select-Object -First 1

    if (-not $service -or -not $bridge) {
        throw "xhm-service or lhm-bridge is not running from the package directory"
    }

    $serviceMiB = $service.PrivateMemorySize64 / 1MB
    $bridgeMiB = $bridge.PrivateMemorySize64 / 1MB
    $samples += [pscustomobject]@{
        Second = $_
        ServiceMiB = [math]::Round($serviceMiB, 2)
        BridgeMiB = [math]::Round($bridgeMiB, 2)
        TotalMiB = [math]::Round($serviceMiB + $bridgeMiB, 2)
    }

    if ($_ -lt 60) { Start-Sleep -Seconds 1 }
}

$samples | Format-Table
$maxTotal = ($samples | Measure-Object TotalMiB -Maximum).Maximum
Write-Host "Maximum combined Private Bytes: $maxTotal MiB"

if ($maxTotal -ge 30) {
    throw "Memory gate failed: combined Private Bytes reached $maxTotal MiB"
}
```

判定标准是连续 60 秒、每秒采样、所有样本都小于 30 MiB。不能用平均值、中位数或最终值覆盖超限峰值。

### 5.5 停止与 child 清理

在运行 Service 的窗口按 `Ctrl+C`。随后检查：

```powershell
Get-Process xhm-service, lhm-bridge -ErrorAction SilentlyContinue
```

打包目录对应的两个进程都应消失。若 bridge 仍存活，child lifecycle 验证失败。

## 6. 单独验证管理员 LHM bridge

CPU/GPU 温度通常需要管理员权限。以管理员身份打开 PowerShell：

```powershell
cd target\package\xhm-service-win-x64
.\lhm-bridge.exe --require-admin --interval 1000
```

预期：

- banner 中 `is_admin` 为 `true`。
- stdout 持续输出 JSON Lines。
- 可用硬件上包含 `cpu_temp`、`cpu_temp_label`、`gpu_temp` 等字段。
- 按 `Ctrl+C` 后进程退出。

非管理员运行时 bridge 会警告温度不可用，但 Service、REST、进程指标和其他可用传感器仍应工作。

## 7. 解包验收

不要只在源码目录验证。将 ZIP 解压到一个全新目录：

```powershell
$ZipPath = (Resolve-Path "target\package\xhm-service-win-x64.zip").Path
$VerifyDir = Join-Path $env:TEMP "xhm-service-package-verify"

Remove-Item $VerifyDir -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive $ZipPath $VerifyDir

$env:RUST_LOG = "info"
& "$VerifyDir\xhm-service.exe"
```

重复第 5 节的 health、listener、process path、SSE、内存和 child cleanup 检查。只有解包目录通过，才能说明 ZIP 包有效。

## 8. 常见问题

### `lhm-bridge unavailable` 或 bridge 启动后立即退出

检查以下文件是否与 `xhm-service.exe` 同级：

```text
lhm-bridge.exe
libMonoPosixHelper.dll
MonoPosixHelper.dll
```

重新执行 `dotnet publish`，不要使用不完整的手工单文件复制。

### Power status 返回 404

可能原因：

- 当前不是 AMD GPU + AMD Ryzen AI Max 395 平台。
- `tools\RyzenAdj` 文件不完整。
- CLI 连续失败 3 次后熔断。

这是安全降级，不应通过删除 platform gate 解决。

### Power switch 返回 403，提示方案未配置

检查同级 `appsettings.json` 中：

- `Power:DeviceVerification:Devices` 是否匹配设备。
- 设备的 `SchemeKey` 是否存在。
- `Power:DeviceVerification:SchemeProfiles` 是否包含对应方案。

### 端口 35181 被占用

```powershell
Get-NetTCPConnection -LocalPort 35181 -State Listen |
  Select-Object LocalAddress, LocalPort, OwningProcess
```

先停止旧 Service。当前 P1 固定使用并行端口 35181。

### 数据库无法替换或 ZIP 中出现 WAL/SHM

先停止 `xhm-service.exe`，确认 `lhm-bridge.exe` 也退出。不要在 Service 运行时复制或压缩 `xhmonitor.db*`。

### Defender/驱动拦截 RyzenAdj

`WinRing0x64.sys` 可能触发安全软件。不要关闭平台、设备或管理员校验；应检查文件来源、签名和安全策略，并在受控测试机上验证。

## 9. 发布前检查清单

- [ ] `cargo fmt --check` 通过。
- [ ] `cargo test --workspace` 通过。
- [ ] `cargo clippy --workspace --all-targets -- -D warnings` 通过。
- [ ] `LhmBridgeSelectionTests` 通过。
- [ ] Rust release build 和 bridge publish 成功。
- [ ] ZIP 目录布局正确，包含两个 Posix helper DLL。
- [ ] 解包目录中的 health 为 Healthy。
- [ ] 仅监听 `127.0.0.1:35181`。
- [ ] SSE 持续收到 `ReceiveSystemUsage`。
- [ ] 60 秒每秒内存采样全部小于 30 MiB。
- [ ] `Ctrl+C` 后 Service 与 bridge 都退出。
- [ ] 管理员测试机验证 LHM 温度字段和 RyzenAdj 行为。
