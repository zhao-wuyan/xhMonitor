# xhMonitor Rust 迁移指南

**起始分支**：`analysis/rust-migration-feasibility`
**更新日期**：2026-07-28
**状态**：P3 生产切换已完成；C# reference implementation 与 solution 按用户决定保留，不进入 Rust release

**操作指南**：[Rust Service/Desktop 手动测试与打包](rust-service-build-test-package.md)

---

## 1. 迁移动机

| 指标 | 当前 C# | 目标 Rust | 数据来源 |
|------|--------:|----------:|------|
| Service Private Bytes | ~89–103 MiB | ~26 MiB（POC 实测） | poc/rust-service + lhm-bridge |
| Desktop Private Bytes | ~149–165 MiB | ~4–8 MiB（软件渲染 POC） | poc/slint-desktop |
| 合计（两进程） | ~238–268 MiB | ~30–34 MiB | — |
| 冷启动时间 | ~3–5 s（CLR + JIT） | < 0.5 s（预期） | 未测量 |

监控类工具应为低开销感知基础设施；当前 .NET 8 运行时（CLR + JIT + WPF/WinForms 栈）占用与功能不成比例。

---

## 2. 总体迁移范围

### 生产路径继续保留

- **Web 前端**（`xhmonitor-web/`，React/TypeScript）：继续消费现有 REST 与 SignalR 兼容 API。
- **SQLite 数据库 schema**：Rust Service 沿用既有 schema。
- **RyzenAdj 工具链**（`tools/RyzenAdj/`）：由 Rust Service 通过 native DLL / CLI 使用。
- **LHM bridge**（`lhm-bridge/`）：继续使用 C# .NET 8，以 JSON Lines IPC 向 Rust Service 提供硬件数据。
- **C# reference implementation**：`XhMonitor.Core/`、`XhMonitor.Service/`、`XhMonitor.Desktop/`、`XhMonitor.Tests/` 和 `xhMonitor.sln` 按用户决定保留，供后续 bug 对照。

### Rust 生产路径不再使用

- `System.Management`（WMI）监控路径。
- `System.Diagnostics.PerformanceCounter` 路径。
- Service 进程内直接引用 LibreHardwareMonitor 的路径。
- C# Service/Desktop/Core 的发布二进制和启动入口。

源码保留与生产切换是两个独立边界：C# 项目仍在仓库中，但正式发布只组装 Rust Service、Rust Desktop、`lhm-bridge`、配置与工具依赖。

### 迁移结果

| 原模块 | 当前生产模块 | 语言/框架 | 原源码状态 |
|--------|--------------|-----------|------------|
| `XhMonitor.Core` | `xhm-core` crate | Rust | 保留作参考 |
| `XhMonitor.Service` | `xhm-service` crate | Rust + axum | 保留作参考；生产配置模板已迁至 `xhm-service/appsettings.json` |
| `XhMonitor.Desktop` | `xhm-desktop` crate | Rust + Slint | 保留作参考及端点/图标资源来源 |
| LHM 传感器读取 | `lhm-bridge` 子进程 | C# .NET 8 | 当前生产 bridge |

## 3. Cargo Workspace 结构

```text
xhMonitor/
├── Cargo.toml                  # workspace root
├── xhm-core/
│   └── src/
│       ├── models.rs
│       ├── traits.rs
│       ├── wire.rs
│       ├── time.rs
│       └── error.rs
├── xhm-service/
│   └── src/
│       ├── api/                # REST 路由
│       ├── db/                 # rusqlite 数据层
│       ├── lhm/                # bridge 子进程管理
│       ├── power/              # RyzenAdj 与设备规则
│       ├── realtime/           # SSE / SignalR 兼容层
│       ├── state.rs
│       └── worker.rs
├── xhm-desktop/
│   └── src/
│       ├── service_client/     # REST / SSE
│       ├── tray/               # 原生 Windows 托盘
│       ├── ui/                 # 悬浮窗、任务栏窗与设置
│       ├── config.rs
│       ├── shell.rs
│       └── persistence.rs
├── lhm-bridge/                 # .NET 8 JSON Lines bridge
├── publish.ps1                 # Full/Lite 绿色版
├── build-installer.ps1         # Lite/LiteNet8/Full 安装器
├── installer/XhMonitor.iss
├── tools/RyzenAdj/
├── xhmonitor-web/
├── XhMonitor.Core/             # C# reference
├── XhMonitor.Service/          # C# reference + 配置模板
├── XhMonitor.Desktop/          # C# reference + 发布资源
├── XhMonitor.Tests/            # C# 回归参考
└── xhMonitor.sln               # 保留的 C# solution
```

### workspace Cargo.toml 关键节选

```toml
[workspace]
members = [
    "xhm-core",
    "xhm-service",
    "xhm-desktop",
]
resolver = "2"

[workspace.package]
edition = "2021"
rust-version = "1.82"
```

`lhm-bridge` 不是 Cargo member，由发布脚本使用 `dotnet publish` 单独构建。保留的 C# Core/Service/Desktop/Tests 与 solution 也不属于 Rust workspace。

## 4. 分阶段路线图

### P0 — 基础层（xhm-core + lhm-bridge 完善，已完成）

**结果**：Rust 共享类型层和正式 `lhm-bridge` 已进入生产组合，P1/P2/P3 均基于这一边界。

| 任务 | 验收条件 |
|------|----------|
| 创建 `xhm-core` crate，迁移所有共享模型和 trait | `cargo test -p xhm-core` 全绿；`MetricSnapshot`、`ProcessRecord` 等类型定义完整 |
| 完善 `lhm-bridge`（从 poc/ 提升）：管理员权限验证、`cpu_temp_label` 字段、优雅退出 | 提权运行后输出包含 `cpu_temp` + `cpu_temp_label`；SIGTERM/Ctrl+C 后进程退出码为 0 |
| `LhmReader` trait + 内存 mock 实现用于测试 | `xhm-service` 单元测试可在无 LHM bridge 下运行 |

**Done 状态**：`xhm-core` 已是 workspace member；`lhm-bridge` 位于正式目录并由 `publish.ps1` 发布到 `Service\`。

---

### P1 — Service 迁移（xhm-service，已完成）

**结果**：Rust Service 已切换为当前生产 Service，默认监听正式端口 `35179`。

#### 4.1 REST API 契约（源自 Controllers/，共 20 端点）

所有路由前缀 `api/v1/`，端口默认 35179（`Server:Port` 可配置）。

**ConfigController** → `/api/v1/config`
| 方法 | 路径 | 描述 | 源码 |
|------|------|------|------|
| GET | `/api/v1/config` | 返回 IntervalSeconds / Keywords / PluginDirectory | ConfigController.cs:35 |
| GET | `/api/v1/config/alerts` | 列出所有 AlertConfiguration，按 MetricId 排序 | :53 |
| POST | `/api/v1/config/alerts` | upsert AlertConfiguration | :65 |
| DELETE | `/api/v1/config/alerts/{id}` | 删除 AlertConfiguration | :84 |
| GET | `/api/v1/config/metrics` | 列出指标元数据（id/displayName/unit/color/icon） | :99 |
| GET | `/api/v1/config/health` | DB 连通性探针；200 Healthy / 503 Unhealthy | :146 |
| GET | `/api/v1/config/settings` | 所有 ApplicationSettings 按 Category 分组 | :171 |
| PUT | `/api/v1/config/settings/{category}/{key}` | 更新单条 setting；404 if not found | :183 |
| PUT | `/api/v1/config/settings` | 批量 upsert settings；触发 ProcessKeywords 热重载 | :214 |
| GET | `/api/v1/config/admin-status` | 检查 Service 是否以管理员身份运行 | :352 |

**PowerController** → `/api/v1/power`
| 方法 | 路径 | 描述 | 源码 |
|------|------|------|------|
| GET | `/api/v1/power/status` | CurrentWatts/LimitWatts/SchemeIndex/Limits；404 不支持 | PowerController.cs:22 |
| GET | `/api/v1/power/warmup` | 触发 DeviceVerifier.RetryVerificationAsync | :38 |
| POST | `/api/v1/power/scheme/next` | 切换下一 TDP 档位；403 未授权 / 404 不支持 | :51 |

**WidgetConfigController** → `/api/v1/widgetconfig`
| 方法 | 路径 | 描述 | 源码 |
|------|------|------|------|
| GET | `/api/v1/widgetconfig` | 读取 widget-settings.json（缺失时返回默认值） | WidgetConfigController.cs:23 |
| POST | `/api/v1/widgetconfig` | 写入完整 WidgetSettings | :43 |
| POST | `/api/v1/widgetconfig/{metricId}` | upsert 单条 MetricClickConfig | :62 |

**MetricsController** → `/api/v1/metrics`
| 方法 | 路径 | 描述 | 源码 |
|------|------|------|------|
| GET | `/api/v1/metrics/latest` | 最新 ProcessMetricRecords（?processId ?processName ?keyword） | MetricsController.cs:23 |
| GET | `/api/v1/metrics/history` | 原始/聚合历史（?processId ?from ?to ?aggregation=raw\|minute\|hour\|day） | :50 |
| GET | `/api/v1/metrics/processes` | 时间范围内出现的进程列表 + 记录数 | :99 |
| GET | `/api/v1/metrics/aggregations` | 全进程聚合记录（?from ?to ?aggregation=minute\|hour\|day） | :130 |

#### 4.2 实时推送：SignalR Hub 契约（源自 MetricsHub.cs + IMetricsClient.cs）

**Hub 路径**：`config Server:HubPath`，默认 `/hubs/metrics`（Program.cs:410）。CORS 允许 origins：`:3000 :5173 :35180 app://`。

**客户端 → 服务端（1 个方法）**：
- `SetProcessMetricsSubscription(mode: string, pinnedProcessIds: int[]?)` — `"lite"` 加入 `metrics.processes.lite` 组；其他值加入 `metrics.processes.full` 组。（MetricsHub.cs:40）

**服务端 → 客户端（5 个事件，IMetricsClient.cs）**：
| 事件名 | 接收方 | 核心字段 |
|--------|--------|---------|
| `ReceiveHardwareLimits` | 所有连接 | Timestamp, MaxMemory(MB), MaxVram(MB) |
| `ReceiveSystemUsage` | 所有连接 | Timestamp, TotalCpu/Gpu/Memory/Vram, Upload/DownloadSpeed, Disks[] |
| `ReceiveProcessMetrics` | 组 `metrics.processes.full` | Timestamp, ProcessCount, Processes[{ProcessId, ProcessName, Metrics{}}] |
| `ReceiveProcessMetricsLite` | 组 `metrics.processes.lite` | 同上（Top-N + Pinned 子集） |
| `ReceiveProcessMetadata` | Caller（连接时） | Timestamp, ProcessCount, Processes[{ProcessId, ProcessName, CommandLine, DisplayName}] |

**实现结果**：Rust Service 在 `axum` 上提供 SignalR 兼容端点（negotiate + WebSocket 文本推送），Web 前端继续使用既有 SignalR JS 客户端；Rust Desktop 改用 SSE 直连 `/api/v1/events`，不走 SignalR。

> 上述事件契约与字段仍来自 `MetricsHub.cs` / `IMetricsClient.cs`，保留作 Rust 实现的对等参考；Rust 侧的实际路由与 SSE 实现以 `xhm-service/src/realtime/` 为准。

#### 4.3 SQLite 层

- 选用 `rusqlite`（同步、bundled）。
- Schema 沿用既有迁移结构；Rust 侧通过内嵌 SQL 文件执行迁移。
- **关键约束（debug-notes-001）**：数据库路径必须相对于可执行文件目录，不得依赖 `std::env::current_dir()`：
  ```rust
  let exe_dir = std::env::current_exe()?.parent().unwrap().to_path_buf();
  let db_path = exe_dir.join("xhmonitor.db");
  ```

#### 4.4 LHM bridge 子进程管理

```rust
// xhm-service/src/lhm/mod.rs 示意
pub trait LhmReader: Send + Sync {
    fn snapshot(&self) -> Option<LhmSnapshot>;
}

pub struct LhmBridgeManager { /* child process + stdout reader + supervisor */ }
// 实际测试 mock 见 xhm-service/src/lhm/mod.rs 与 xhm-core traits
```

子进程管理要点：
- 以管理员权限启动（Service 本体以管理员运行，子进程继承）
- stdout JSON Lines 逐行解析，parse 失败跳过不崩溃
- 子进程意外退出 → 重启策略（指数退避，最多 5 次）
- 优雅关闭：SIGTERM → 等待子进程 500ms → 强杀

#### 4.5 RyzenAdj 封装（源自 RyzenAdjNativeClient.cs + RyzenAdjCli.cs）

原实现使用 **`RyzenAdjFallbackClient`** 装饰器模式：**主路径为 native DLL P/Invoke**，首次 native 异常后永久切换至 CLI 备用路径。

| 后端 | 机制 | 源码 |
|------|------|------|
| **Primary**：`RyzenAdjNativeClient` | P/Invoke `libryzenadj.dll`（StdCall）：`init_ryzenadj()` / `refresh_table()` / `get_stapm_limit` / `set_stapm_limit` | RyzenAdjNativeClient.cs:241–278 |
| **Fallback**：`RyzenAdjCli` | 子进程 `ryzenadj.exe -i`（读取状态）；`--stapm-limit=N --fast-limit=N --slow-limit=N`（N = Watts×1000 毫瓦）| RyzenAdjCli.cs:42–80 |

**Rust 迁移策略**（镜像 C# 结构）：
- Primary：`libloading` crate 动态加载 `libryzenadj.dll`，同样使用 `SetDllDirectory` 作用域保护（参考 `NativeLibraryDirectoryScope`）
- Fallback：`std::process::Command` 调用 `ryzenadj.exe`，参数完全对应 C# CLI 路径
- `RyzenAdjFallbackClient` → Rust 结构体持有 `primary: Option<NativeClient>` + `cli: CliClient`；primary 失败后设 `primary = None` 并永久走 CLI

**P1 Done 状态**：

- `xhm-service` 默认使用正式端口 `35179`。
- 既有 REST API、Desktop 使用的 SSE 与 Web 使用的 SignalR 兼容端点均由 Rust Service 提供。
- `lhm-bridge` 由 Rust Service 作为独立子进程管理。
- 当前确定性 workspace 测试基线包含 Service 测试，见第 5.3 节。

---

### P2 — Desktop 迁移（xhm-desktop，已完成）

**结果**：Rust + Slint Desktop 已成为当前生产 Desktop，并通过 `service-endpoints.json` 连接 Rust Service。

#### 4.6 Win32 集成（POC 已验证基础层）

已验证能力（poc/slint-desktop）：
- ✅ HWND 定位（`EnumWindows` + PID 验证）
- ✅ 置顶（`HWND_TOPMOST` + `SWP_NOSIZE`，不覆盖 Slint 逻辑尺寸）
- ✅ 任务栏贴近定位
- ⚠️ 点击穿透（实现已确认，交互行为待手动验证）
- ❌ 全局热键（Ctrl+Alt+Shift+X）**不迁移**（用户决策，POC 已移除）

需新增实现：
- `TaskbarPlacementService` 等价逻辑：`FindWindow("Shell_TrayWnd")` + `FindWindowEx("TrayNotifyWnd" / "MSTaskListWClass")`，检测任务栏边缘（Bottom/Top/Left/Right），8px/6px 间距，虚拟屏幕边界 clamp（TaskbarPlacementService.cs）
- `TaskbarMetricsWindow` 等价：第二个悬浮窗，边缘吸附停靠，拖拽转浮动，独立 SignalR/SSE 连接（TaskbarMetricsWindow.xaml.cs）
- 托盘图标（`Shell_NotifyIcon`，WinForms `NotifyIcon`）
- 托盘右键菜单：显示/隐藏、打开 Web 界面、点击穿透切换、管理员模式、设置、关于、退出（TrayIconService.cs）
- 窗口位置持久化（`%AppData%`，WindowPositionStore）
- 多显示器 DPI 感知定位
- 单实例检查（Mutex）

#### 4.7 UI 功能对等清单（源自 FloatingWindow.xaml + TaskbarMetricsWindow.xaml）

| 原 WPF 功能 | 源文件 | Slint 实现方案 | 优先级 |
|-------------|--------|---------------|--------|
| 半透明深色背景（#990A0A0A）+ CornerRadius=8 + DropShadow | FloatingWindow.xaml | Slint Rectangle + border-radius + drop-shadow | P2-核心 |
| CPU/GPU/MEM/NET/Disk 实时数值 + 阈值颜色（绿/黄/红） | FloatingWindow.xaml | Slint 数据绑定 + 条件样式 | P2-核心 |
| ThinProgressBar（3px 进度条） | FloatingWindow.xaml | Slint Rectangle 动态宽度 | P2-核心 |
| PinnedStack 固定进程卡片 | FloatingWindow.xaml | Slint `for` 循环 model | P2-核心 |
| 进程列表弹出（虚拟化 ListBox） | FloatingWindow.xaml | Slint `ListView` | P2-核心 |
| 拖拽移动 + 边缘吸附（24px snap 距离） | FloatingWindow.xaml.cs | Win32 WM_LBUTTONDOWN + snap logic | P2-核心 |
| 长按缩小动画（2s ScaleTransform → 0.90） | FloatingWindow.xaml.cs | Slint `animate` + Timer | P2-核心 |
| 点击反馈关键帧动画（50ms/150ms） | FloatingWindow.xaml.cs | Slint keyframe animation | P2-核心 |
| Kill 按钮二次确认 + 倒计时圆弧动画 | FloatingWindow.xaml.cs | Slint Canvas arc + Timer | P2-核心 |
| 全局热键（原版 Ctrl+Alt+Shift+X） | FloatingWindow.xaml.cs | **不迁移（用户决策）** | — |
| TaskbarMetricsWindow：边缘停靠 + 4 种柱状样式（文字/进度条 × 横/竖） | TaskbarMetricsWindow.xaml | Slint 第二个窗口 + orientation binding | P2-核心 |
| 托盘图标 + 上下文菜单（管理员模式、Web 界面等） | TrayIconService.cs | WinForms NotifyIcon（保留策略）或 win32 Shell_NotifyIcon | P2-核心 |
| 设置窗口（切换监控项、关键词、功耗预设） | SettingsWindow.xaml | Slint 设置页 | P2-扩展 |
| 关于窗口 + 更新检查 | AboutWindow.xaml | Slint 关于页 | P2-扩展 |
| Toast 通知（阈值告警） | FloatingWindow.xaml | Slint Popup/Overlay | P2-扩展 |

#### 4.8 Service 发现（issue-DSC-20260119，已实现）

Rust Desktop 从可执行文件同级读取 `service-endpoints.json`。发布脚本将 `XhMonitor.Desktop/service-endpoints.json` 复制到 `Desktop\`，当前配置为：

```json
{
  "ServiceEndpoints": {
    "ApiBaseUrl": "http://localhost:35179",
    "SignalRUrl": "http://localhost:35179/hubs/metrics"
  }
}
```

配置缺失、不可读或无效时，Desktop 回退到生产默认端口 `35179`，不会因配置加载失败 panic。Desktop 生产数据通路使用 REST + SSE；`SignalRUrl` 作为既有兼容配置保留。

**P2 Done 状态**：

- `xhm-desktop` 已加入 Rust workspace，并由 `publish.ps1` 生成 release 二进制。
- 生产发布固定包含 `Desktop\xhm-desktop.exe`、`Desktop\service-endpoints.json` 和 `Desktop\Assets\icon.ico`。
- 启动 batch 只在 Rust Service health gate 通过后启动 Rust Desktop。
- 当前确定性 workspace 测试基线包含 Desktop 测试，见第 5.3 节。

---

### P3 — 生产切换（已完成，源码清理例外已确认）

**结果**：正式端口、绿色版、安装器和生命周期脚本均已切换到 Rust Service/Desktop。C# reference implementation 与 solution 的删除按用户决定延期并保留，不构成 P3 生产切换阻塞项。

| 步骤 | 状态 | 当前结果 |
|------|------|----------|
| 3.1 | 已完成 | Rust Service 使用正式端口 `35179`；组合启动不再启动 C# Service |
| 3.2 | 已完成 | `publish.ps1` 构建并组装 Rust `xhm-service.exe`、Rust `xhm-desktop.exe` 与 `lhm-bridge` |
| 3.3 | 用户决定延期/保留 | 不删除 `XhMonitor.Service/`、`XhMonitor.Desktop/`、`XhMonitor.Core/`，留作后续 bug 对照；不进入 Rust release |
| 3.4 | 用户决定延期/保留 | 不删除 `XhMonitor.Tests/` 与 `xhMonitor.sln`；它们是 C# 回归参考，不是当前生产构建入口 |
| 3.5 | 已完成 | `build-installer.ps1` 支持 Lite、LiteNet8、Full；安装器使用与绿色版相同的 `Service\` / `Desktop\` contract layout |
| 3.6 | 已完成 | README、操作指南与本迁移指南同步到 P3 生产入口和发布命令 |

**P3 Done 条件与证据**：

- 生产默认端口为 `35179`；`Desktop\service-endpoints.json` 与启动 health gate 使用同一端口。
- `启动服务.bat` 先启动 Rust Service，health 返回 Healthy 后再启动 Rust Desktop；端口占用时失败退出。
- `publish.ps1` 产出 `Service\{xhm-service.exe, lhm-bridge publish 依赖, appsettings.json, tools\RyzenAdj}`、`Desktop\{xhm-desktop.exe, service-endpoints.json, Assets\icon.ico}` 和根目录 batch/`README.txt`。
- Rust release 不包含 C# Service/Desktop/Core 二进制；保留源码和 solution 是用户批准的明确例外。
- `cargo fmt --check` 通过；`cargo test --workspace -- --test-threads=1` 为 286 passed；`cargo clippy --workspace --all-targets -- -D warnings` 零警告。
- 已观测 Full 绿色版 76.88 MiB、Full 安装器 29.89 MiB、Lite 绿色版 12.67 MiB、LiteNet8 安装器 35.88 MiB。
- Inno Setup 6.7 已成功编译安装器。

---

## 5. 单元测试策略

遵循 `test-conventions-002`：LHM bridge、Win32、SQLite、HTTP 均属外部边界，**必须隔离**。

### 5.1 隔离边界

| 边界 | 隔离方式 |
|------|----------|
| LHM bridge（子进程） | `LhmReader` trait + `MockLhmReader`（返回固定快照） |
| RyzenAdj CLI | `RyzenAdjClient` trait + `MockRyzenAdjClient` |
| Win32 API（HWND 等） | `#[cfg(test)]` 条件编译 + stub 函数；或 `win32` module 注入 |
| SQLite | `rusqlite` / `sqlx` 内存数据库（`":memory:"`） |
| SSE / HTTP 客户端 | `axum::Server` 测试实例（`axum-test` crate）；Desktop 侧用 mock HTTP server |
| 时钟 / 定时器 | 注入 `Clock` trait（`SystemClock` vs `MockClock`） |

### 5.2 测试分层

```
xhm-core/tests/          单元测试：模型序列化、trait 契约
xhm-service/tests/
  unit/                  业务逻辑（全 mock 边界）
  integration/           axum TestServer + 内存 SQLite（不需要真实 LHM/RyzenAdj）
xhm-desktop/tests/
  unit/                  UI 状态逻辑、service_client 解析（mock HTTP）
  # 无 Win32 集成测试——Win32 行为通过 POC / 手动验证
```

### 5.3 CI 约束

- `cargo test --workspace -- --test-threads=1` 是确定性 workspace 门禁，当前基线为 286 passed。
- 第一次默认并行 workspace 测试曾非确定性挂起，因此文档和发布基线不使用默认并行命令。
- `cargo clippy --workspace --all-targets -- -D warnings` 零警告。

---

## 6. 架构优化（随迁移一并修正）

| 原问题 | 修正方案 |
|--------|---------|
| Desktop 硬编码端口 `35179`（issue-DSC-20260119） | `service-endpoints.json` 动态读取，`ServiceEndpoints::load()` 有默认值回退 |
| SQLite 路径依赖 CWD（debug-notes-001） | 全部 path 计算基于 `current_exe().parent()` |
| WMI / PerformanceCounter 路径与 LHM 并存 | 删除，统一走 LHM bridge |
| SignalR 强依赖（Desktop + Service 双侧耦合） | Service 改 SSE；Desktop 用 SSE；Web 前端保持不变 |
| `App.xaml.cs` 臃肿（issue-ISS-1768808736538-0） | Desktop `main.rs` 按职责分模块（config / win32 / service_client / ui） |
| LibreHardwareMonitor 直接 in-process（Service 重量级） | 改为 lhm-bridge 子进程，Service 进程不加载 LHM DLL |

---

## 7. 已知运行边界

| 边界 | 当前处理 |
|------|----------|
| Rust Desktop 渲染 | 发布和启动 batch 设置 `SLINT_BACKEND=winit-software` |
| LHM 管理员权限与硬件可用性 | `lhm-bridge` 独立运行；缺失权限或硬件时 Service 继续提供其余可用能力 |
| Lite bridge 运行时 | 目标机需要 Microsoft.NETCore.App 8 |
| LiteNet8 runtime | 安装器只内置 Microsoft.NETCore.App 8，不包含其他 .NET runtime 或 SDK |
| 端口冲突 | `启动服务.bat` 检查 `35179`；占用时失败且不启动 Rust Service/Desktop |
| C# reference 源码 | 留作 bug 对照，不得误接回正式发布或启动路径 |

---

## 8. 构建与发布

### 开发期

```powershell
# 构建 Rust workspace
cargo build --workspace

# 启动 Rust Service（默认端口 35179）
cargo run -p xhm-service

# 在另一个 PowerShell 启动 Rust Desktop
$env:SLINT_BACKEND = "winit-software"
cargo run -p xhm-desktop

# 确定性 workspace 测试
cargo test --workspace -- --test-threads=1
```

`.NET 8` 开发命令只用于 `lhm-bridge` 或保留的 C# reference tests，不用于启动正式 Service/Desktop。

### 绿色版

```powershell
# Full：self-contained bridge
.\publish.ps1 -Version "1.0.0"

# Lite：framework-dependent bridge
.\publish.ps1 -Version "1.0.0" -Lite
```

两种模式都构建同一 Rust workspace，并输出 `release\XhMonitor-v1.0.0\` 与同名 ZIP。Full 的 bridge self-contained；Lite 需要目标机安装 Microsoft.NETCore.App 8。

### 安装器

```powershell
.\build-installer.ps1 -Version "1.0.0" -BuildType Lite
.\build-installer.ps1 -Version "1.0.0" -BuildType LiteNet8
.\build-installer.ps1 -Version "1.0.0" -BuildType Full
```

LiteNet8 使用与 Lite 相同的 framework-dependent bridge，只额外内置 Microsoft.NETCore.App 8 runtime 安装包；Full 使用 self-contained bridge。安装器沿用绿色版的 `Service\`、`Desktop\`、根 batch 和 `README.txt` 布局。

### 启动脚本

```powershell
& ".\release\XhMonitor-v1.0.0\启动服务.bat"
```

脚本在端口 `35179` 空闲时启动 Rust Service，等待 `/api/v1/config/health` 返回 Healthy，再启动 Rust Desktop；端口占用或 health gate 失败时退出，不继续启动 Desktop。详细命令和 contract layout 以操作指南为准。

---

## 9. C# 保留例外

用户已明确决定保留以下内容：

- `XhMonitor.Core/`
- `XhMonitor.Service/`
- `XhMonitor.Desktop/`
- `XhMonitor.Tests/`
- `xhMonitor.sln`

保留目的仅为后续 bug 对照、旧行为定位和 bridge/契约回归参考。P3 不删除这些源码，也不把它们的 Service/Desktop/Core 构建产物装入绿色版或安装器。正式发布和启动入口始终是 Rust `xhm-service`、Rust `xhm-desktop` 与 .NET 8 `lhm-bridge`。

---

## 10. 下一步行动

| 优先级 | 行动 |
|--------|------|
| **发布** | 使用 `publish.ps1` 构建 Full/Lite 绿色版，使用 `build-installer.ps1 -BuildType Lite|LiteNet8|Full` 构建安装器 |
| **回归** | 使用 `cargo test --workspace -- --test-threads=1` 维持 286 passed 的确定性基线 |
| **运行** | 通过 release 根目录 `启动服务.bat` 执行 Service health gate 后启动 Desktop |
| **C# 参考** | 保留 C# 项目、Tests 与 solution；仅在 bug 对照时使用，不作为正式启动或发布入口 |
| **文档** | 发布 contract 或脚本参数变化时，同步 README、操作指南和本节 |
