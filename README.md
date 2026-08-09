# XhMonitor - Windows 资源监视器

> 高性能的 Windows 进程资源监控系统，支持 CPU、内存、GPU、显存、功耗、网络等指标的实时采集、聚合分析和可视化展示

[![Rust](https://img.shields.io/badge/Rust-1.82-000000)](https://www.rust-lang.org/)
[![.NET bridge](https://img.shields.io/badge/.NET_bridge-8.0-512BD4)](https://dotnet.microsoft.com/)
[![React](https://img.shields.io/badge/React-19-61DAFB)](https://react.dev/)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)

## Features

- ✅ **多维度监控** - CPU、内存、GPU、显存、硬盘、功耗、网络速度实时监控
- ✅ **智能过滤** - 基于关键词过滤，精准监控目标进程
- ✅ **分层聚合** - 自动生成分钟/小时/天级别统计数据
- ✅ **实时推送** - SignalR 实时推送最新指标，延迟 < 100ms
- ✅ **Web 可视化** - React + TailwindCSS 现代化界面，ECharts 动态图表
- ✅ **桌面悬浮窗** - Rust + Slint 桌面应用，支持进程固定、拖拽、置顶
- ✅ **模块化架构** - `xhm-core` trait 与 Service 模块支持指标能力扩展
- ✅ **配置驱动** - 零前端代码修改，动态扩展指标
- ✅ **国际化支持** - 中英文切换，易于扩展多语言
- ✅ **功耗管理** - RyzenAdj 集成，支持 AMD 平台功耗监控与调节
- ✅ **设备验证** - 设备白名单机制，保护功耗调节功能
- ✅ **安全认证** - 访问密钥认证、IP 白名单、局域网访问控制

## Installation

### Prerequisites

**Rust Service/Desktop 开发**：

- Windows 10/11 x64。
- Rust 1.82 或更高版本，使用 MSVC toolchain。
- Visual Studio 2022 Build Tools，包含 C++ 桌面生成工具。
- Windows PowerShell 5.1 或 PowerShell 7。
- .NET 8 SDK，仅用于构建 `lhm-bridge`；C# Service/Desktop 不是默认开发或生产入口。

**Web 前端**：

- Node.js 18+。
- npm 或 pnpm。

**发行版运行时**：

- Full 绿色版/安装器的 `lhm-bridge` self-contained，不要求目标机安装 .NET Runtime。
- Lite 绿色版/安装器需要 Microsoft.NETCore.App 8（Windows x64）。
- LiteNet8 安装器只内置 Microsoft.NETCore.App 8 runtime。

**权限要求**：

- **推荐**：管理员权限（可监控功耗模式和切换功耗，适配 AI MAX 395）。
- **最低**：普通用户权限（无法进行部分功耗监控和切换）。

### Install

**1. 克隆仓库**

```bash
git clone <repository-url>
cd xhMonitor
```

**2. 启动 Rust Service**

```powershell
# 构建 Rust workspace
cargo build --workspace

# 启动 Rust Service
cargo run -p xhm-service
```

Service 默认在 `http://localhost:35179` 启动。

**3. 启动 Rust Desktop**

在另一个 PowerShell 中执行：

```powershell
$env:SLINT_BACKEND = "winit-software"
cargo run -p xhm-desktop
```

Desktop 缺少同级 `service-endpoints.json` 时会回退到 `http://localhost:35179`。

**4. 启动 Web 前端**

```powershell
cd xhmonitor-web
npm install
npm run dev
```

前端将在 `http://localhost:35180` 启动，并继续使用现有 REST/SignalR 兼容 API。

**5. 构建并启动完整绿色版**

```powershell
.\publish.ps1 -Version "1.0.0"
& ".\release\XhMonitor-v1.0.0\启动服务.bat"
```

根目录 batch 会先启动 Rust Service，等待 health gate 通过，再启动 Rust Desktop；端口 `35179` 被占用时会失败退出。构建安装器使用：

```powershell
.\build-installer.ps1 -Version "1.0.0" -BuildType LiteNet8
```

## Usage

### Quick Start

**1. 启动 Rust Service**

```powershell
cargo run -p xhm-service
```

**2. 启动 Rust Desktop**

```powershell
$env:SLINT_BACKEND = "winit-software"
cargo run -p xhm-desktop
```

Desktop 支持：

- 进程固定（Pin）。
- 拖拽移动与窗口置顶。
- 任务栏指标窗口。
- 功耗调节（需管理员权限、受支持的 AMD 平台与完整 RyzenAdj 工具链）。

**3. 访问 Web 界面**

在 `xhmonitor-web/` 运行 `npm run dev` 后，打开 `http://localhost:35180` 查看实时监控数据。

**4. 配置发布包**

`xhm-service/appsettings.json` 是 Rust Service 的 canonical 配置模板，生产发布不再依赖保留参考的 `XhMonitor.Service/`。绿色版生成后，运行时配置位于 `release/XhMonitor-v<version>/Service/appsettings.json`；Desktop 端点配置位于同一发布根目录的 `Desktop/service-endpoints.json`。

### Examples

**REST API 查询**

```bash
# 获取最新指标
curl http://localhost:35179/api/v1/metrics/latest

# 获取历史数据（分钟聚合）
curl "http://localhost:35179/api/v1/metrics/history?processId=1234&aggregation=minute"

# 获取进程列表
curl http://localhost:35179/api/v1/metrics/processes
```

**SignalR 实时订阅**

```typescript
import * as signalR from "@microsoft/signalr";

const connection = new signalR.HubConnectionBuilder()
  .withUrl("http://localhost:35179/hubs/metrics")
  .withAutomaticReconnect()
  .build();

connection.on("ReceiveSystemUsage", (data) => console.log("ReceiveSystemUsage", data));
connection.on("ReceiveHardwareLimits", (data) => console.log("ReceiveHardwareLimits", data));
connection.on("ReceiveProcessMetrics", (data) => console.log("ReceiveProcessMetrics", data));
connection.on("ReceiveProcessMetadata", (data) => console.log("ReceiveProcessMetadata", data));

await connection.start();
```

## Configuration

### 关键配置（建议优先关注）

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `Monitor:IntervalSeconds` | `3` | Service 进程采集间隔（秒） |
| `Monitor:LlamaMetricsIntervalSeconds` | `1` | llama-server（`/metrics`） 采样间隔（秒），`0` 表示禁用 |
| `Monitor:Keywords` | 示例见 `appsettings.json` | 目标进程过滤关键词 |
| `Server:Port` | `35179` | Service HTTP/SignalR 服务端口 |
| `SignalR:*BufferSize` | `1048576` | SignalR 缓冲上限，影响峰值内存 |
| `Aggregation:BatchSize` | `2000` | 聚合任务分批读取大小，影响聚合阶段峰值内存 |
| `UiOptimization:ProcessRefreshIntervalMs` | `Development=100` `Staging=150` `Production=200` | Desktop 刷新节流间隔 |

完整配置说明（含全部字段）请看：`docs/appsettings-reference.md`  
配置边界说明请看：[Configuration Boundaries](XhMonitor.Service/docs/configuration-boundaries.md)

### 手动采集功耗设备识别信息

功耗方案会从 SMBIOS/WMI 读取硬件平台信息。需要排查设备识别时，优先复制下面“一行版”到 PowerShell 直接执行；不需要保存 `.ps1` 文件，也不需要管理员权限。PowerShell 的续行符是反引号 `` ` ``，不是 Linux shell 的 `\`，而且行尾不能有空格，所以这里用分号拼成单条命令更稳。

```powershell
& { $ErrorActionPreference = 'Stop'; function n($v) { if ([string]::IsNullOrWhiteSpace([string]$v)) { $null } else { ([string]$v).Trim() } }; $computerSystem = Get-CimInstance -ClassName Win32_ComputerSystem; $systemProduct = Get-CimInstance -ClassName Win32_ComputerSystemProduct; $baseBoard = Get-CimInstance -ClassName Win32_BaseBoard; $bios = Get-CimInstance -ClassName Win32_BIOS; $processor = Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1; $hardware = [ordered]@{ system_manufacturer = n $computerSystem.Manufacturer; system_model = n $computerSystem.Model; product_vendor = n $systemProduct.Vendor; product_name = n $systemProduct.Name; baseboard_manufacturer = n $baseBoard.Manufacturer; baseboard_product = n $baseBoard.Product; bios_manufacturer = n $bios.Manufacturer; bios_version = n $bios.SMBIOSBIOSVersion; processor_name = n $processor.Name }; $matchesSixUnited = @($hardware.system_manufacturer, $hardware.product_vendor, $hardware.baseboard_manufacturer) -match '(?i)Six United|Sixunited'; $matchesAxb3502 = @($hardware.system_model, $hardware.product_name, $hardware.baseboard_product) -match '(?i)AXB35-02'; $matchesAmd395 = $hardware.processor_name -match '(?i)AMD Ryzen AI Max.*395'; $isSupported = [bool]$matchesSixUnited -and [bool]$matchesAxb3502; Write-Host ("XhMonitor amd_395 power monitoring verification: " + $(if ($matchesAmd395) { "PASS" } else { "FAIL" })); Write-Host ("XhMonitor AXB35-02 power switching verification: " + $(if ($isSupported) { "PASS" } else { "FAIL" })); [pscustomobject]@{ matches_amd395_monitoring = [bool]$matchesAmd395; matches_six_united_axb3502 = $isSupported; manufacturer_match = [bool]$matchesSixUnited; model_match = [bool]$matchesAxb3502 } | Format-List; [pscustomobject]$hardware | Format-List }
```

下面是同一逻辑的展开版，便于阅读和调整字段：

```powershell
$ErrorActionPreference = 'Stop'
function n($v) { if ([string]::IsNullOrWhiteSpace([string]$v)) { $null } else { ([string]$v).Trim() } }
$computerSystem = Get-CimInstance -ClassName Win32_ComputerSystem
$systemProduct = Get-CimInstance -ClassName Win32_ComputerSystemProduct
$baseBoard = Get-CimInstance -ClassName Win32_BaseBoard
$bios = Get-CimInstance -ClassName Win32_BIOS
$processor = Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1

$hardware = [ordered]@{
    system_manufacturer    = n $computerSystem.Manufacturer
    system_model           = n $computerSystem.Model
    product_vendor         = n $systemProduct.Vendor
    product_name           = n $systemProduct.Name
    baseboard_manufacturer = n $baseBoard.Manufacturer
    baseboard_product      = n $baseBoard.Product
    bios_manufacturer      = n $bios.Manufacturer
    bios_version           = n $bios.SMBIOSBIOSVersion
    processor_name         = n $processor.Name
}

$matchesSixUnited = @($hardware.system_manufacturer, $hardware.product_vendor, $hardware.baseboard_manufacturer) -match '(?i)Six United|Sixunited'
$matchesAxb3502 = @($hardware.system_model, $hardware.product_name, $hardware.baseboard_product) -match '(?i)AXB35-02'
$matchesAmd395 = $hardware.processor_name -match '(?i)AMD Ryzen AI Max.*395'
$isSupported = [bool]$matchesSixUnited -and [bool]$matchesAxb3502

Write-Host ("XhMonitor amd_395 power monitoring verification: " + $(if ($matchesAmd395) { "PASS" } else { "FAIL" }))
Write-Host ("XhMonitor AXB35-02 power switching verification: " + $(if ($isSupported) { "PASS" } else { "FAIL" }))

[pscustomobject]@{
    matches_amd395_monitoring    = [bool]$matchesAmd395
    matches_six_united_axb3502 = $isSupported
    manufacturer_match         = [bool]$matchesSixUnited
    model_match                = [bool]$matchesAxb3502
} | Format-List

[pscustomobject]$hardware | Format-List
```

当前功耗监控启用条件只看本机硬件：`processor_name` 同时包含 `AMD Ryzen AI Max` 和 `395`。默认功耗切换方案识别条件为：`system_manufacturer` / `product_vendor` / `baseboard_manufacturer` 任一字段包含 `Six United` 或 `Sixunited`，并且 `system_model` / `product_name` / `baseboard_product` 任一字段包含 `AXB35-02`。NovaStudio `/device_info` 不再用于开启功耗监控，只保留给没有 SMBIOS 硬件条件的旧设备验证规则。

功耗切换档位通过 `Power:DeviceVerification:SchemeProfiles` 统一配置，设备识别项通过 `SchemeKey` 绑定对应方案。`SchemeKey` 缺失、没有匹配 profile 或未命中 AXB35-02 设备规则时，只禁用功耗切换并写日志，不影响 `amd_395` 平台的功耗监控和展示。

### llama-server（llama.cpp） 指标说明

启用条件：
- 启动 `llama-server` 时带上 `--metrics`，并指定 `--port <PORT>`（或 `--port=<PORT>`）。
- `Monitor:LlamaMetricsIntervalSeconds` > 0（默认 `1` 秒）。

Desktop 的进程行会显示一行类似：

`Port 1234   Gen 43.9 tok/s   Busy 87%   Req 1/0   Out 3071   Dec 3584`

| 字段 | 指标键 | 说明 | 来源 |
|------|--------|------|------|
| `Port` | `llama_port` | metrics 端口（从进程命令行解析） | `--port` |
| `Gen` | `llama_gen_tps_compute` | 生成吞吐（tok/s） | 计算得出 |
| `Busy` | `llama_busy_percent` | 推理忙碌程度（%） | 计算得出 |
| `Req` | `llama_req_processing` / `llama_req_deferred` | 正在处理 / 排队请求数 | `llamacpp:requests_processing` / `llamacpp:requests_deferred` |
| `Out` | `llama_out_tokens_total` | 累计生成 token 数 | `llamacpp:tokens_predicted_total` |
| `Dec` | `llama_decode_total` | 累计 `llama_decode()` 调用次数 | `llamacpp:n_decode_total` |

实时显示说明（Desktop）：
- 部分 `llama-server` 构建下，`llamacpp:tokens_predicted_total` / `llamacpp:tokens_predicted_seconds_total` 可能在推理过程中不连续更新，导致 `Gen` / `Busy` / `Out` 看起来“卡住”。
- 为了让推理过程中也能看到变化，Desktop 会在数值后用 `~` 追加一组 **live 估算值**：
  - `llama_out_tokens_live`：基于 `Δ(llamacpp:n_decode_total)` 的累计估算。
  - `llama_gen_tps_live`：`Δ(llama_out_tokens_live) / Δ(wall_seconds)`。
  - `llama_busy_percent_live`：当 `llama_gen_tps_live > 0` 时为 `100`，否则为 `0`。
- 当原始指标恢复更新或推理进入空闲（两次采样无增量）时，live 估算会回落到原始值（避免长期保留上一次的估算导致误读）。

计算方式（需要两次采样的增量）：
- 相关原始指标含义：
  - `llamacpp:tokens_predicted_total`：累计生成的 token 数（counter，单调递增，重启后从 0 开始）。
  - `llamacpp:tokens_predicted_seconds_total`：llama-server 统计的“生成阶段”累计耗时（秒，counter，单调递增，重启后从 0 开始）。
  - `wall_seconds`：两次采样间的真实经过时间（秒），不是 llama 的 Prometheus 指标；由 Service 侧用 `Stopwatch` 计算。
- 记号说明：
  - `ΔX`：两次采样的差值（`X(t1) - X(t0)`）。
  - `clamp(x, 0, 100)`：将 `x` 限制在 `0` 到 `100` 之间，避免异常值导致显示越界。
- 记第 1 次采样为 `t0`，第 2 次采样为 `t1`：
  - `T0` = `llamacpp:tokens_predicted_total(t0)`，`T1` = `llamacpp:tokens_predicted_total(t1)`
  - `S0` = `llamacpp:tokens_predicted_seconds_total(t0)`，`S1` = `llamacpp:tokens_predicted_seconds_total(t1)`
  - `W` 为两次采样间的墙钟耗时（秒）：`W = wall_seconds(t1) - wall_seconds(t0)`
- 增量：`ΔT = T1 - T0`，`ΔS = S1 - S0`
- `Gen(tok/s)`：`ΔT / ΔS`
- `Busy(%)`：`clamp(ΔS / W * 100, 0, 100)`

注意：
- 第一次采样或计数器重置（例如 `llama-server` 重启）时，`Gen` / `Busy` 可能显示为 `--`。
- 当两次采样间无增量时，`Gen` / `Busy` 会归 `0`（避免长时间保留上一次的非 0 值导致误读；不依赖 `Req` 指标是否可靠）。

## API Reference

### REST API

**Base URL**: `http://localhost:35179/api/v1`

#### Metrics API

**获取最新指标**

```http
GET /metrics/latest?processId={int}&processName={string}&keyword={string}
```

**获取历史数据**

```http
GET /metrics/history?processId={int}&from={datetime}&to={datetime}&aggregation={string}
```

参数：
- `aggregation`: `raw` | `minute` | `hour` | `day`

**获取进程列表**

```http
GET /metrics/processes?from={datetime}&to={datetime}&keyword={string}
```

#### Config API

**获取指标元数据**

```http
GET /config/metrics
```

返回所有已注册的指标提供者信息，用于前端动态渲染。

**获取配置**

```http
GET /config
```

**健康检查**

```http
GET /config/health
```

### SignalR Hub

**Hub URL**: `http://localhost:35179/hubs/metrics`

**事件**：
- `ReceiveHardwareLimits` - 硬件上限（内存 / 显存）
- `ReceiveSystemUsage` - 系统总览（CPU / 内存 / GPU / 显存 / 功耗 / 网速 / 电源方案）
- `ReceiveProcessMetrics` - 进程指标列表
- `ReceiveProcessMetadata` - 进程元数据（名称 / 命令行 / DisplayName）

## Architecture

### 系统架构

XhMonitor 当前生产路径由 Rust Service、Rust Desktop 和独立的 .NET 8 硬件采集 bridge 组成：

```
┌─────────────────────────────────────────────────────────────┐
│  采集层 (Collection Layer)                                   │
│  ├─ xhm-service 进程/系统指标采集                            │
│  ├─ lhm-bridge (.NET 8, JSON Lines IPC)                     │
│  └─ RyzenAdj native DLL / CLI                               │
└─────────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────────┐
│  存储层 (Storage Layer)                                      │
│  ├─ SQLite + rusqlite                                       │
│  ├─ ProcessMetricRecords (原始数据)                          │
│  └─ AggregatedMetricRecords (分层聚合)                       │
└─────────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────────┐
│  服务层 (Service Layer)                                      │
│  ├─ xhm-service (Tokio + Axum REST API)                     │
│  ├─ SSE (Rust Desktop)                                      │
│  └─ SignalR 兼容端点 (Web 前端)                              │
└─────────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────────┐
│  展示层 (Presentation Layer)                                 │
│  ├─ Web 前端 (React 19 + TypeScript)                        │
│  └─ Rust Desktop (Slint + winit software renderer)          │
└─────────────────────────────────────────────────────────────┘
```

### 技术栈

| 类别 | 技术 |
|------|------|
| 共享核心 | Rust 1.82 + `xhm-core` |
| 后端服务 | Rust + Tokio + Axum |
| 桌面应用 | Rust + Slint 1.12.1 + winit software renderer |
| 硬件采集 bridge | C# .NET 8 + LibreHardwareMonitor（独立子进程） |
| Web 前端 | React 19 + TypeScript + Vite 7 |
| 数据库 | SQLite + rusqlite |
| 实时通信 | SSE（Desktop）+ SignalR 兼容端点（Web） |
| 可视化 | ECharts 6 |
| 样式 | TailwindCSS v4 (Glassmorphism) |
| 进程/系统指标 | sysinfo + Windows API + lhm-bridge |
| 功耗管理 | RyzenAdj native DLL / CLI |
| Rust 日志 | tracing |

### 项目结构

```
xhMonitor/
├── Cargo.toml                    # Rust workspace
├── xhm-core/                     # 共享模型、wire 类型与 trait
├── xhm-service/                  # Rust Axum Service
│   └── src/
│       ├── api/                  # REST 路由
│       ├── db/                   # SQLite 数据层
│       ├── lhm/                  # lhm-bridge 生命周期
│       ├── power/                # RyzenAdj 与设备校验
│       └── realtime/             # SSE / SignalR 兼容层
├── xhm-desktop/                  # Rust Slint Desktop
│   └── src/
│       ├── service_client/       # REST / SSE 客户端
│       ├── tray/                 # 原生 Windows 托盘
│       └── ui/                   # 悬浮窗、任务栏窗与设置
├── lhm-bridge/                   # 保留的 .NET 8 IPC bridge
├── xhmonitor-web/                # React 前端
├── tools/RyzenAdj/               # RyzenAdj 功耗管理工具
├── scripts/                      # Rust release 启动/停止 batch
├── publish.ps1                   # Full/Lite 绿色版发布
├── build-installer.ps1           # Lite/LiteNet8/Full 安装器
├── installer/XhMonitor.iss       # Inno Setup 定义
├── XhMonitor.Core/               # C# reference implementation
├── XhMonitor.Service/            # C# reference + 发布配置模板
├── XhMonitor.Desktop/            # C# reference + 端点/图标资源
├── XhMonitor.Tests/              # 保留的 C# 回归参考
└── xhMonitor.sln                 # 保留的 C# solution
```

C# Core/Service/Desktop/Tests 与 `xhMonitor.sln` 按用户决定保留，供后续 bug 对照；正式启动和发布使用 Rust `xhm-service`、Rust `xhm-desktop` 与 `lhm-bridge`。`publish.ps1` 不会把 C# Service/Desktop/Core 二进制放入 Rust release。

## Development

### C# 参考实现中的自定义指标

以下示例保留用于理解既有 C# 指标契约和排查兼容问题，不是当前 Rust 生产实现的开发入口。Rust 共享契约位于 `xhm-core/`，Service 实现位于 `xhm-service/`。

**1. 实现 IMetricProvider 接口**

```csharp
public class CustomMetricProvider : IMetricProvider
{
    public string MetricId => "custom_metric";
    public string DisplayName => "Custom Metric";
    public string Unit => "units";
    public MetricType Type => MetricType.Gauge;

    public bool IsSupported() => true;

    public async Task<MetricValue> CollectAsync(int processId)
    {
        var value = await GetCustomMetricAsync(processId);
        return new MetricValue
        {
            Value = value,
            Unit = Unit,
            DisplayName = DisplayName,
            Timestamp = DateTime.UtcNow
        };
    }

    public void Dispose() { }
}
```

**2. 注册到 MetricProviderRegistry**

提供者会自动被发现并注册。

**3. 前端国际化**

在 `xhmonitor-web/src/i18n.ts` 中添加翻译：

```typescript
export const i18n = {
  zh: {
    'Custom Metric': '自定义指标',
  },
  en: {
    'Custom Metric': 'Custom Metric',
  },
};
```

前端会自动通过 `/api/v1/config/metrics` 获取指标元数据并渲染，无需修改组件代码。

### 运行测试

```powershell
# Rust workspace 确定性门禁
cargo fmt --check
cargo test --workspace -- --test-threads=1
cargo clippy --workspace --all-targets -- -D warnings

# 仅在回归 lhm-bridge 或对照 legacy C# 行为时运行
dotnet test XhMonitor.Tests\XhMonitor.Tests.csproj
```

当前 Rust 单线程 workspace 基线为 286 passed。默认并行测试曾出现非确定性挂起，因此使用 `-- --test-threads=1`。

### 构建发布

```powershell
# Full 绿色版：Rust Service/Desktop + self-contained lhm-bridge
.\publish.ps1 -Version "1.0.0"

# Lite 绿色版：bridge framework-dependent
.\publish.ps1 -Version "1.0.0" -Lite

# 默认 LiteNet8 安装器
.\build-installer.ps1 -Version "1.0.0" -BuildType LiteNet8

# Full 安装器
.\build-installer.ps1 -Version "1.0.0" -BuildType Full
```

绿色版输出到 `release/XhMonitor-v<version>/` 及同名 ZIP；安装器输出到 `release/XhMonitor-v<version>-<type>-Setup.exe`。详细 contract layout、模式差异和验收步骤见：[Rust Service/Desktop 手动测试与打包](docs/rust-service-build-test-package.md)。

## Performance

**当前测试环境**：
- 监控进程数：141
- 采集间隔：3 秒
- 首次周期：102 秒（含缓存构建）
- 后续周期：8-9 秒
- CPU 占用：< 5%
- 内存占用：~50MB

**优化建议**：
- 使用进程关键词过滤减少监控数量
- 调整采集间隔（3-10 秒）
- 定期清理历史数据

## Roadmap

### 已完成

- ✅ 核心架构搭建
- ✅ 监控核心实现
- ✅ 数据持久化与聚合
- ✅ Web API + SignalR
- ✅ Web 前端开发
- ✅ Rust + Slint 桌面悬浮窗
- ✅ 功耗监控（RyzenAdj）
- ✅ 网络监控
- ✅ 进程管理（强制结束）

### 进行中

### 待开发

- ⏳ 进程详情查看

## Contributing

欢迎提交 Issue 和 Pull Request！

### 开发流程

1. Fork 本仓库
2. 创建特性分支 (`git checkout -b feature/AmazingFeature`)
3. 提交更改 (`git commit -m 'feat: Add some AmazingFeature'`)
4. 推送到分支 (`git push origin feature/AmazingFeature`)
5. 开启 Pull Request

### 代码规范

- Rust 代码遵循现有 workspace 约定；C# 约定仅适用于 `lhm-bridge` 和保留的 reference implementation
- 使用有意义的变量和方法名
- 添加必要的注释（非必要不添加）
- 保持代码简洁高效

## License

[MIT License](LICENSE)

---

