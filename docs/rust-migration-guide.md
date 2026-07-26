# xhMonitor Rust 迁移指南

**分支**：`analysis/rust-migration-feasibility`  
**日期**：2026-07-26  
**状态**：设计文档 + 分阶段路线图（不含实现代码）

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

### 保留不变
- **Web 前端**（`xhmonitor-web/`，React/TypeScript）——仅需 API 契约对齐，不改一行前端代码。
- **SQLite 数据库 schema**——Rust 侧完全沿用；新增字段用迁移脚本追加。
- **RyzenAdj 工具链**（`tools/RyzenAdj/`）——保留 exe + DLL；Rust 通过 `std::process::Command` 调用。

### 删除
- `System.Management`（WMI）监控路径
- `System.Diagnostics.PerformanceCounter` 路径
- LibreHardwareMonitor 直接引用（改为 LHM bridge 子进程 IPC）

### 迁移
| 原模块 | 新模块 | 语言/框架 |
|--------|--------|-----------|
| `XhMonitor.Core` | `xhm-core` crate | Rust |
| `XhMonitor.Service` | `xhm-service` crate | Rust + axum |
| `XhMonitor.Desktop` | `xhm-desktop` crate | Rust + Slint |
| LHM 传感器读取 | `lhm-bridge` .NET 子进程 | C# .NET 8（保留为 IPC 桥） |

---

## 3. Cargo Workspace 结构

```
xhMonitor/
├── Cargo.toml                  # workspace root
├── xhm-core/                   # 共享类型、trait、模型
│   ├── Cargo.toml
│   └── src/
│       ├── lib.rs
│       ├── models.rs           # MetricSnapshot, ProcessRecord, AlertRule …
│       ├── traits.rs           # MetricSource, MetricStore, RyzenAdjClient
│       └── error.rs
├── xhm-service/                # axum web 服务
│   ├── Cargo.toml
│   └── src/
│       ├── main.rs
│       ├── api/                # REST 路由
│       ├── sse/                # SSE 推送（替代 SignalR）
│       ├── db/                 # rusqlite 层
│       ├── lhm/                # LHM bridge 子进程管理
│       ├── process/            # 进程指标采集
│       ├── ryzenadj/           # RyzenAdj CLI 封装
│       └── config.rs
├── xhm-desktop/                # Slint 悬浮窗应用
│   ├── Cargo.toml
│   └── src/
│       ├── main.rs
│       ├── win32.rs            # HWND / topmost / click-through（POC 已验证）
│       ├── service_client.rs   # SSE 订阅 + REST 调用
│       ├── config.rs           # service-endpoints.json 读取
│       └── ui/
│           └── app.slint       # 悬浮窗 UI 定义
├── lhm-bridge/                 # .NET 8 子进程（保留 C#）
│   ├── lhm-bridge.csproj
│   └── Program.cs              # 已在 POC 中实现，输出 JSON Lines
├── tools/
│   └── RyzenAdj/               # 原样保留
└── xhmonitor-web/              # 原样保留（React/TS 前端）
```

### workspace Cargo.toml 示例

```toml
[workspace]
members = [
    "xhm-core",
    "xhm-service",
    "xhm-desktop",
]
resolver = "2"

[workspace.dependencies]
tokio       = { version = "1", features = ["full"] }
serde       = { version = "1", features = ["derive"] }
serde_json  = "1"
anyhow      = "1"
tracing     = "0.1"
```

---

## 4. 分阶段路线图

### P0 — 基础层（xhm-core + lhm-bridge 完善）

**目标**：建立 Rust 共享类型层和经验证的 LHM bridge，后续阶段均依赖此基础。

| 任务 | 验收条件 |
|------|----------|
| 创建 `xhm-core` crate，迁移所有共享模型和 trait | `cargo test -p xhm-core` 全绿；`MetricSnapshot`、`ProcessRecord` 等类型定义完整 |
| 完善 `lhm-bridge`（从 poc/ 提升）：管理员权限验证、`cpu_temp_label` 字段、优雅退出 | 提权运行后输出包含 `cpu_temp` + `cpu_temp_label`；SIGTERM/Ctrl+C 后进程退出码为 0 |
| `LhmReader` trait + 内存 mock 实现用于测试 | `xhm-service` 单元测试可在无 LHM bridge 下运行 |

**Done 条件**：`xhm-core` crate 发布到 workspace；`lhm-bridge` 从 poc/ 移出进入正式目录并通过管理员权限测试。

---

### P1 — Service 迁移（xhm-service）

**目标**：Rust Service 达到与 C# Service 的 API 功能对等，可并行运行（双端口）。

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

**迁移策略**：Web 前端使用 SignalR JS 客户端，不可修改。xhm-service 必须实现完整 SignalR over WebSocket 文本协议（negotiate + subscribe + 上述5个事件推送）。Desktop（Slint）改用 SSE 直连，不走 SignalR。

> ⚠️ **决策点**：SignalR 文本协议兼容实现建议使用 `axum` + WebSocket 手写（参考 ASP.NET Core SignalR 文本协议规范），或寻找现有 Rust SignalR 服务端 crate。复杂度中等，需在 P1 开始时评估。

#### 4.3 SQLite 层

- 使用 `rusqlite`（同步）或 `sqlx`（异步，推荐与 tokio 配合）。
- Schema 完全沿用现有迁移文件（`Migrations/` 目录）；Rust 侧用 `refinery` 或内嵌 SQL 文件执行迁移。
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

pub struct LhmBridgeProcess { /* child process + stdout reader */ }
pub struct MockLhmReader { snapshot: Option<LhmSnapshot> }  // 测试用
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

**P1 Done 条件**：
- `xhm-service` 在端口 35179（或新端口 35181 并行）运行
- 全部 REST 端点 curl 测试通过
- SSE 推送 Desktop 可接收
- `cargo test -p xhm-service` 全绿（LHM/RyzenAdj 均 mock）
- Private Bytes < 30 MiB（稳态，含 lhm-bridge 子进程）

---

### P2 — Desktop 迁移（xhm-desktop）

**目标**：Slint 悬浮窗完全复原原版功能，并行运行验证后替换 WPF 版本。

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

#### 4.8 Service 发现（修复 issue-DSC-20260119）

原版 Desktop 硬编码 `localhost:35179`。迁移时同步修复：

```rust
// xhm-desktop/src/config.rs
#[derive(Deserialize)]
pub struct ServiceEndpoints {
    pub service_url: String,       // "http://localhost:35179"
    pub sse_path: String,          // "/api/events"
}

impl ServiceEndpoints {
    pub fn load() -> anyhow::Result<Self> {
        let exe_dir = std::env::current_exe()?.parent().unwrap().to_path_buf();
        let path = exe_dir.join("service-endpoints.json");
        // 回退到默认值，不 panic
        Ok(serde_json::from_reader(File::open(path)?)
            .unwrap_or_default())
    }
}
```

**P2 Done 条件**：
- 悬浮窗在软件渲染模式下运行，UI 功能对等清单核心项全部通过
- Private Bytes < 10 MiB（软件渲染）
- 任务栏贴近定位在 4K 显示器验证通过
- `cargo test -p xhm-desktop` 全绿（Win32 调用 mock）

---

### P3 — 切换与废弃

**目标**：完全切换到 Rust 实现，删除 C# Service / Desktop 代码，保留 lhm-bridge。

| 步骤 | 操作 |
|------|------|
| 3.1 | Rust Service 切换到正式端口 35179（停止 C# Service） |
| 3.2 | 更新 `publish.ps1`、`build-installer.ps1` 指向 Rust 二进制 |
| 3.3 | 删除 `XhMonitor.Service/`、`XhMonitor.Desktop/`、`XhMonitor.Core/` |
| 3.4 | 更新 `.sln` / `slnx`（保留 `XhMonitor.Tests` 中仍有价值的集成测试，迁移为 Rust 测试） |
| 3.5 | 更新安装包脚本；`lhm-bridge.exe` 打包进 `tools/` |
| 3.6 | 更新 `README` 和 `CLAUDE.md` |

**P3 Done 条件**：
- 仓库中无 `XhMonitor.Service/`、`XhMonitor.Desktop/`、`XhMonitor.Core/` 目录
- `cargo build --release` 一条命令产出两个可执行文件（`xhm-service.exe`、`xhm-desktop.exe`）
- 现有集成测试套件（迁移后）全绿
- 安装包大小 ≤ 原版（含 lhm-bridge.exe + Slint 软件渲染无额外 DLL）

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

- `cargo test --workspace` 必须在**无管理员权限、无真实硬件**的 CI 环境下通过。
- 依赖真实 LHM/RyzenAdj 的测试用 `#[ignore]` 标记，手动在开发机上运行。
- `cargo clippy --workspace -- -D warnings` 零警告。

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

## 7. 关键风险与缓解

| 风险 | 可能性 | 缓解方案 |
|------|--------|---------|
| Slint 有机矩阵动画视觉还原度不足 | 中 | P2 早期出原型让用户确认；不达标时考虑 Skia canvas |
| SSE 替代 SignalR：Web 前端兼容 | 中 | P1 早期确认 polyfill 方案；最坏情况：xhm-service 内嵌最小 WS 兼容层 |
| LHM bridge 管理员权限 + 反病毒拦截 | 低-中 | 已在 POC 观察到；安装包签名；测试 Windows Defender 白名单行为 |
| RyzenAdj FFI / CLI 解析变更 | 低 | 固定 RyzenAdj 版本；CLI stdout 解析加容错 |
| Slint 软件渲染 CPU 占用 | 待测 | P2 前测量稳态 CPU（10s 间隔刷新时）；若不可接受回退 GPU 渲染并 profile 根因 |

---

## 8. 构建与发布

### 开发期

```powershell
# 构建全部
cargo build --workspace

# 运行 Service（开发）
cargo run -p xhm-service

# 运行 Desktop（软件渲染）
$env:SLINT_BACKEND = "winit-software"; cargo run -p xhm-desktop

# 全量测试
cargo test --workspace
```

### 发布包（替代 publish.ps1）

```powershell
# Service（self-contained，无 .NET 运行时依赖）
cargo build -p xhm-service --release

# Desktop（软件渲染，无 GPU 驱动依赖）
cargo build -p xhm-desktop --release

# 打包：两个 exe + lhm-bridge.exe + tools/RyzenAdj/ + wwwroot/
# 预计总体积：~20–25 MiB（对比当前 ~70 MiB self-contained）
```

### 启动脚本（替代现有 scripts/）

```powershell
# start-service.ps1
$env:RUST_LOG = "info"
Start-Process -FilePath ".\xhm-service.exe" -WorkingDirectory $PSScriptRoot

# start-desktop.ps1
$env:SLINT_BACKEND = "winit-software"
Start-Process -FilePath ".\xhm-desktop.exe" -WorkingDirectory $PSScriptRoot
```

---

## 9. 已知协议偏离说明

`maestro session` 创建过程遇到 CLI 版本与 `run-mode.md` 协议不符：

| 协议文档要求 | 实际 CLI 行为 |
|-------------|-------------|
| `session create --no-dispatch` | `--no-dispatch` 不是有效 flag；`session create` 默认不分发（`session start` 才分发） |
| chain-file `decisions` 顶层键 | Runtime 拒绝："Unrecognized key: decisions" |
| decision step `{ decision_ref }` | Runtime 要求 `command` 字段；D1 定义位置未知 |

决策：用户选择绕过 maestro session，直接产出本文档。D1 范围决策点由工程师在 P1 开始前人工评审本文档后决定。

---

## 10. 下一步行动

| 优先级 | 行动 |
|--------|------|
| **立即** | 评审本文档；确认 SignalR → SSE Web 前端兼容方案（第4.2节决策点） |
| **P0** | 创建 `xhm-core` crate；将 `poc/lhm-bridge` 提升为正式 `lhm-bridge/` |
| **P0** | 验证 lhm-bridge 管理员权限运行（cpu_temp + cpu_temp_label 输出正确） |
| **P1** | 实现 `xhm-service`（axum）；并行端口 35181 运行；curl 验证全 API |
| **P2** | 实现 `xhm-desktop` Slint 完整 UI；矩阵动画原型确认 |
| **P3** | 切换正式端口；删除 C# 项目；更新发布脚本 |
