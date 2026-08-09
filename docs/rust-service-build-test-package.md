# xhMonitor Rust Service/Desktop 手动测试与打包指南

本文面向 Windows x64 开发机，覆盖 `xhm-core`、`xhm-service`、`xhm-desktop` 和 `lhm-bridge` 的自动化门禁、release 构建、绿色版与安装器构建，以及解包/安装验收。P3 已将生产入口切换为 Rust Service 和 Rust Desktop，默认端口为 `35179`；`.NET 8` 仅用于 `lhm-bridge`。

`XhMonitor.Service/`、`XhMonitor.Desktop/`、`XhMonitor.Core/`、`XhMonitor.Tests/` 和 `xhMonitor.sln` 按用户决定保留，供后续 bug 对照与回归参考。它们不是当前生产启动入口，也不会作为 C# Service/Desktop/Core 二进制进入 Rust release。

## 1. 环境要求

在仓库根目录打开 PowerShell。

必需工具：

- Rust 1.82 或更高版本，使用 MSVC toolchain。
- .NET SDK 8.0，仅用于构建 `lhm-bridge` 和按需运行保留的 .NET 参考测试。
- Visual Studio 2022 Build Tools，包含 C++ 桌面生成工具。
- Windows PowerShell 5.1 或 PowerShell 7。
- 构建安装器时需要 Inno Setup 6.x；已使用 Inno Setup 6.7 编译通过。

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
xhm-desktop\
lhm-bridge\
tools\RyzenAdj\
xhm-service\appsettings.json
XhMonitor.Desktop\service-endpoints.json
publish.ps1
build-installer.ps1
installer\XhMonitor.iss
```

发布模式的运行时要求：

| 模式 | `lhm-bridge` 形态 | 目标机要求 |
|------|-------------------|------------|
| Full | win-x64 self-contained single-file | 不需要另装 .NET Runtime |
| Lite | win-x64 framework-dependent | 需要 Microsoft.NETCore.App 8（Windows x64） |
| LiteNet8 安装器 | 与 Lite 相同 | 安装器仅内置 Microsoft.NETCore.App 8 runtime 安装包 |

构建 LiteNet8 安装器前，`tools\RuntimePkg\` 中必须恰好有一个匹配 `dotnet-runtime-8.*-win-x64.exe` 的文件。LiteNet8 不内置 ASP.NET Core Runtime、Desktop Runtime 或 .NET SDK。

## 2. 运行自动化测试

### 2.1 Rust 完整门禁

使用确定性的单线程 workspace 命令：

```powershell
cargo fmt --check
cargo test --workspace -- --test-threads=1
cargo clippy --workspace --all-targets -- -D warnings
```

当前已观测基线：

- `cargo fmt --check` 通过。
- `cargo test --workspace -- --test-threads=1` 为 286 passed。
- `cargo clippy --workspace --all-targets -- -D warnings` 零警告。

第一次使用默认并行度运行 `cargo test --workspace` 曾出现非确定性挂起，因此发布门禁和基线统一使用 `-- --test-threads=1`，不要用默认并行命令替代这条可复现命令。

### 2.2 Rust 分模块测试

排查问题时可单独运行：

```powershell
cargo test -p xhm-core -- --test-threads=1
cargo test -p xhm-service -- --test-threads=1
cargo test -p xhm-desktop -- --test-threads=1
cargo test -p xhm-service --lib power::tests -- --test-threads=1
cargo test -p xhm-service --lib api::power::tests -- --test-threads=1
cargo test -p xhm-service lhm::tests -- --nocapture --test-threads=1
```

分模块命令用于定位问题；发布基线仍以第 2.1 节的单线程 workspace 命令为准。

### 2.3 LHM bridge 与 C# 参考测试

`lhm-bridge` 仍是 .NET 8 子进程。需要回归 bridge 选择逻辑时可运行：

```powershell
dotnet test XhMonitor.Tests\XhMonitor.Tests.csproj `
  --filter "FullyQualifiedName~LhmBridgeSelectionTests"
```

该命令使用保留的 `XhMonitor.Tests` 参考测试，覆盖传感器优先级、GPU 聚合、网卡吞吐和连续失败预算。它不把 C# Service 或 C# Desktop 恢复为生产入口。需要对照旧实现时，也可显式运行完整参考测试项目：

```powershell
dotnet test XhMonitor.Tests\XhMonitor.Tests.csproj
```

## 3. 构建 release 产物

### 3.1 构建 Rust workspace

```powershell
cargo build --workspace --release
```

Rust 产物：

```text
target\release\xhm-service.exe
target\release\xhm-desktop.exe
```

这条命令只构建 Rust workspace，不会组装 `lhm-bridge`、配置、图标、RyzenAdj、batch 或 ZIP。正式发布应使用第 3.2 节的 `publish.ps1`。

### 3.2 构建绿色版

下面示例固定版本为 `1.0.0`，便于后续命令直接引用输出路径：

```powershell
# Full：self-contained lhm-bridge，并创建 ZIP
.\publish.ps1 -Version "1.0.0"

# Lite：framework-dependent lhm-bridge，并创建 ZIP
.\publish.ps1 -Version "1.0.0" -Lite
```

脚本会构建 Rust Service/Desktop、发布 `lhm-bridge`，再组装共享 release layout。常用参数：

| 参数 | 行为 |
|------|------|
| `-Version "1.0.0"` | 指定版本；省略时从 `Directory.Build.props` 读取 |
| `-Lite` | 将 bridge 发布为 framework-dependent |
| `-NoZip` | 只保留目录，不创建 ZIP |
| `-SkipService` | 跳过 Rust Service 和 bridge |
| `-SkipDesktop` | 跳过 Rust Desktop |
| `-Debug` | 使用 Debug 构建并保留可用符号文件 |

完整生产包不要使用 `-SkipService` 或 `-SkipDesktop`。

输出：

```text
release\XhMonitor-v1.0.0\
release\XhMonitor-v1.0.0.zip
```

### 3.3 构建安装器

`build-installer.ps1` 支持 3 种构建类型：

```powershell
# Lite：目标机需预装 Microsoft.NETCore.App 8
.\build-installer.ps1 -Version "1.0.0" -BuildType Lite

# LiteNet8：framework-dependent bridge，并内置 Microsoft.NETCore.App 8 安装包
.\build-installer.ps1 -Version "1.0.0" -BuildType LiteNet8

# Full：self-contained bridge
.\build-installer.ps1 -Version "1.0.0" -BuildType Full
```

省略 `-BuildType` 时默认为 `LiteNet8`。脚本先以匹配模式调用 `publish.ps1`，再编译 `installer\XhMonitor.iss`。只有已存在同版本、同模式发布目录时，才使用 `-SkipPublish`。

输出：

```text
release\XhMonitor-v1.0.0-Lite-Setup.exe
release\XhMonitor-v1.0.0-Lite-Net8-Setup.exe
release\XhMonitor-v1.0.0-Full-Setup.exe
```

## 4. 绿色版 contract layout

### 4.1 必需目录结构

完整发布目录必须符合以下 contract：

```text
XhMonitor-v1.0.0\
├── Service\
│   ├── xhm-service.exe
│   ├── lhm-bridge.exe
│   ├── ...                     # 当前 bridge publish 的其余运行依赖
│   ├── appsettings.json
│   └── tools\
│       └── RyzenAdj\
│           ├── libryzenadj.dll
│           ├── ryzenadj.exe
│           └── ...             # RyzenAdj 完整工具链
├── Desktop\
│   ├── xhm-desktop.exe
│   ├── service-endpoints.json
│   └── Assets\
│       └── icon.ico
├── 启动服务.bat
├── 停止服务.bat
└── README.txt
```

布局约束：

- `lhm-bridge.exe` 及其 publish 依赖必须与 `Service\xhm-service.exe` 同级。
- `Service\appsettings.json` 是 Service 配置文件。
- RyzenAdj 固定位于 `Service\tools\RyzenAdj\`。
- `Desktop\service-endpoints.json` 必须与 `xhm-desktop.exe` 同级，当前端点指向 `http://localhost:35179`。
- `Desktop\Assets\icon.ico` 是 Desktop 和安装器快捷方式图标。
- `Service\xhmonitor.db`、`Service\logs\` 和 `Service\data\` 在运行时按需创建，不应从开发机预置进新包。
- 根目录 batch 是正式组合启动/停止入口。

Rust release 不包含 `XhMonitor.Service.exe`、`XhMonitor.Desktop.exe` 或 `XhMonitor.Core.dll`。对应 C# 源码、测试和 solution 只留在仓库中作为参考。

### 4.2 已观测发布证据

当前已完成的发布观测：

| 产物 | 大小 |
|------|-----:|
| Full 绿色版目录 | 76.88 MiB |
| Full 安装器 | 29.89 MiB |
| Lite 绿色版目录 | 12.67 MiB |
| LiteNet8 安装器 | 35.88 MiB |

Inno Setup 6.7 已成功编译安装器。大小是本次构建的观测值，会随版本、bridge publish 内容和 runtime 安装包版本变化，不应作为固定等式。

## 5. 运行打包结果

### 5.1 使用 batch 启动 Service 和 Desktop

构建 `1.0.0` 绿色版后运行：

```powershell
& ".\release\XhMonitor-v1.0.0\启动服务.bat"
```

启动脚本按以下顺序工作：

1. 定位同一发布根目录下的 `Service\xhm-service.exe` 和 `Desktop\xhm-desktop.exe`。
2. 清理已知的 Rust/bridge 进程和升级兼容的旧 .NET 进程。
3. 检查端口 `35179`；若仍被占用，返回失败且不启动 Rust Service/Desktop。
4. 以 `Service\` 为工作目录启动 Rust Service。
5. 最多等待约 10 秒，直到 `/api/v1/config/health` 返回 `Healthy`。
6. health gate 通过后，以 `Desktop\` 为工作目录启动 Rust Desktop。

脚本设置 `RUST_LOG=info` 和 `SLINT_BACKEND=winit-software`。若 Service 未在期限内报告 Healthy，脚本不会启动 Desktop，并会清理本次启动的 Service/bridge。

### 5.2 验证健康状态与监听端口

```powershell
Invoke-RestMethod http://127.0.0.1:35179/api/v1/config/health

Get-NetTCPConnection -LocalPort 35179 -State Listen |
  Select-Object LocalAddress, LocalPort, OwningProcess
```

health 应返回 `status: Healthy`、`database: Connected`。确认执行路径来自同一发布目录：

```powershell
Get-Process xhm-service, xhm-desktop, lhm-bridge -ErrorAction SilentlyContinue |
  Select-Object Id, ProcessName, Path
```

`xhm-service` 和 `xhm-desktop` 必须分别来自 `Service\` 与 `Desktop\`；bridge 可因硬件或运行时条件暂时不可用，但出现时必须来自 `Service\`。

### 5.3 验证 REST 与实时推送

```powershell
Invoke-RestMethod http://127.0.0.1:35179/api/v1/config
Invoke-RestMethod http://127.0.0.1:35179/api/v1/metrics/latest

try {
    Invoke-RestMethod http://127.0.0.1:35179/api/v1/power/status
} catch {
    Write-Host "Power status HTTP:" ([int]$_.Exception.Response.StatusCode)
}
```

Power endpoint 在非支持设备、未配置 profile 或未部署驱动时返回 403/404/503 属于预期拒绝；不能为了通过验收而绕过设备/profile gate。

验证 Desktop 使用的 SSE：

```powershell
curl.exe -N "http://127.0.0.1:35179/api/v1/events?mode=full"
```

应持续看到 `ReceiveSystemUsage` 等事件。验证 Web 前端仍兼容的 SignalR negotiate：

```powershell
Invoke-RestMethod `
  -Method Post `
  "http://127.0.0.1:35179/hubs/metrics/negotiate?negotiateVersion=1"
```

返回值的 `availableTransports` 应包含 `WebSockets`。

### 5.4 验证 Desktop 配置

确认发布文件：

```powershell
Get-Content ".\release\XhMonitor-v1.0.0\Desktop\service-endpoints.json"
```

`ApiBaseUrl` 和 `SignalRUrl` 应使用端口 `35179`。Desktop 的生产数据通路使用 REST + SSE；`SignalRUrl` 保留兼容配置。启动后确认 `xhm-desktop.exe` 存活，且只有在 health gate 通过后才由 batch 启动。

### 5.5 停止与 child 清理

```powershell
& ".\release\XhMonitor-v1.0.0\停止服务.bat"

Get-Process xhm-service, xhm-desktop, lhm-bridge -ErrorAction SilentlyContinue
```

发布目录对应的 Rust Service、Rust Desktop 和 bridge 都应消失。停止脚本还会清理升级场景可能残留的旧 .NET Service/Desktop 进程；这是兼容性清理，不表示旧 C# 程序仍是生产入口。

## 6. 单独验证管理员 LHM bridge

CPU/GPU 温度通常需要管理员权限。以管理员身份打开 PowerShell：

```powershell
cd ".\release\XhMonitor-v1.0.0\Service"
.\lhm-bridge.exe --require-admin --interval 1000
```

预期：

- banner 中 `is_admin` 为 `true`。
- stdout 持续输出 JSON Lines。
- 可用硬件上包含 `cpu_temp`、`cpu_temp_label`、`gpu_temp` 等字段。
- 按 `Ctrl+C` 后进程退出。

Lite/LiteNet8 的 bridge 需要 Microsoft.NETCore.App 8；Full bridge self-contained。非管理员运行时 bridge 会警告部分温度不可用，但 Rust Service、REST、进程指标和其他可用传感器仍应工作。

## 7. 解包与安装验收

### 7.1 绿色版解包

不要只在源码目录验证。将 ZIP 解压到全新目录：

```powershell
$ZipPath = (Resolve-Path ".\release\XhMonitor-v1.0.0.zip").Path
$VerifyDir = Join-Path $env:TEMP "xhmonitor-package-verify"

Remove-Item $VerifyDir -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive $ZipPath $VerifyDir
& "$VerifyDir\XhMonitor-v1.0.0\启动服务.bat"
```

重复第 4 节 layout 和第 5 节 health、端口、process path、REST/SSE、Desktop、停止检查。只有解包目录通过，才能说明绿色版有效。

### 7.2 安装器

按构建类型运行对应安装器：

```powershell
Start-Process `
  ".\release\XhMonitor-v1.0.0-Lite-Net8-Setup.exe" `
  -Wait
```

安装后确认安装目录仍包含第 4.1 节的 `Service\`、`Desktop\`、根 batch 和 `README.txt`，再通过“Start Service”快捷方式或安装目录中的 `启动服务.bat` 执行第 5 节验收。

- Lite：目标机未安装 Microsoft.NETCore.App 8 时，bridge 不可用；Rust Service/Desktop 仍是同一套二进制。
- LiteNet8：可选安装内置的 Microsoft.NETCore.App 8；不会安装其他 .NET runtime。
- Full：bridge self-contained，不依赖目标机 .NET Runtime。

## 8. 常见问题

### `lhm-bridge unavailable` 或 bridge 启动后立即退出

先确认 `lhm-bridge.exe` 和该模式的全部 publish 依赖位于 `Service\`。Lite/LiteNet8 还要确认：

```powershell
dotnet --list-runtimes
```

输出应包含 `Microsoft.NETCore.App 8.0.x`。Full 不依赖系统 .NET Runtime。

### Power status 返回 404

可能原因：

- 当前不是受支持的 AMD 平台。
- `Service\tools\RyzenAdj` 文件不完整。
- CLI 连续失败后熔断。

这是安全降级，不应通过删除 platform gate 解决。

### Power switch 返回 403，提示方案未配置

检查 `Service\appsettings.json`：

- `Power:DeviceVerification:Devices` 是否匹配设备。
- 设备的 `SchemeKey` 是否存在。
- `Power:DeviceVerification:SchemeProfiles` 是否包含对应方案。

### 端口 35179 被占用

```powershell
Get-NetTCPConnection -LocalPort 35179 -State Listen |
  Select-Object LocalAddress, LocalPort, OwningProcess
```

先停止占用者，再重新运行 `启动服务.bat`。脚本在端口仍处于 Listen 状态时会失败退出，不会继续启动 Rust Service 或 Rust Desktop。

### Desktop 无法连接 Service

检查 `Desktop\service-endpoints.json` 是否存在、是否使用 `35179`，再确认 `/api/v1/config/health` 返回 Healthy。不要把开发目录与其他版本的 Service/Desktop 混用。

### LiteNet8 报告缺少 runtime 安装包

`tools\RuntimePkg\` 中只保留一个匹配 `dotnet-runtime-8.*-win-x64.exe` 的 Microsoft.NETCore.App 8 安装包。不要用 ASP.NET Core Runtime 或 Desktop Runtime 替代。

### 数据库无法替换或 ZIP 中出现 WAL/SHM

先运行 `停止服务.bat`。不要在 Service 运行时复制或压缩 `Service\xhmonitor.db*`，也不要将开发机数据库打入新包。

### Defender/驱动拦截 RyzenAdj

`WinRing0x64.sys` 可能触发安全软件。不要关闭平台、设备或管理员校验；应检查文件来源、签名和安全策略，并在受控测试机上验证。

## 9. 发布前检查清单

- [ ] `cargo fmt --check` 通过。
- [ ] `cargo test --workspace -- --test-threads=1` 通过，基线为 286 passed。
- [ ] `cargo clippy --workspace --all-targets -- -D warnings` 通过且零警告。
- [ ] `publish.ps1` 同时产出 Rust `xhm-service.exe` 和 `xhm-desktop.exe`。
- [ ] `Service\` 包含 bridge publish 依赖、`appsettings.json` 和完整 `tools\RyzenAdj\`。
- [ ] `Desktop\` 包含 `service-endpoints.json` 和 `Assets\icon.ico`。
- [ ] release 根目录包含启动/停止 batch 和 `README.txt`。
- [ ] C# Service/Desktop/Core 二进制未进入 Rust release。
- [ ] `启动服务.bat` 在端口 `35179` 被占用时失败退出。
- [ ] Service health 为 Healthy 后，batch 才启动 Rust Desktop。
- [ ] REST、SSE 和 SignalR 兼容端点均使用 `35179`。
- [ ] Full、Lite/LiteNet8 的 bridge 运行时要求与目标发行模式一致。
- [ ] 安装器使用对应的 Lite、LiteNet8 或 Full 模式编译成功。
- [ ] `停止服务.bat` 后 Rust Service、Rust Desktop 和 bridge 均退出。
