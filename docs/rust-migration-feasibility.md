# xhMonitor 迁移可行性分析：Rust / 轻量化方案

> 分支：`analysis/rust-migration-feasibility`
> 日期：2026-07-25
> 状态：分析草稿，待 POC 验证后决策

---

## 1. 背景与动机

xhMonitor 是常驻后台的监视器程序，理想形态是"低感知、低占用、长时间运行"。当前基于 .NET 8 的两进程架构存在以下已测量的内存成本：

| 进程 | Private Bytes（优化后实测） | GC 堆 | 主要来源 |
|------|---:|---:|------|
| `XhMonitor.Service` | ~89–103 MiB | ~43 MiB | ASP.NET Core、SignalR、LHM、EF Core、PDH |
| `XhMonitor.Desktop` | ~149–165 MiB | ~13 MiB | **WPF/WinForms UI 栈**、DirectWrite 字体、AutomationPeer、CLR/JIT loader |

> 数据来源：`docs/memory-optimization-experiments.md`，2026-05-27 dump 对比实验。
> Desktop GC 堆仅 13 MiB，说明应用逻辑本身分配极少；内存成本主体是 WPF 运行时栈，而非托管业务代码。

---

## 2. 架构约束（来自项目知识库）

以下约束在分析中必须遵守，不能因迁移而破坏：

- `spec:project:architecture-constraints`：Desktop 负责 WPF shell、托盘、本地 Web 服务器/代理、桌面设置 UI；Service 负责 HTTP API、持久化、硬件/进程采集
- `spec:project:architecture-constraints-003`：Backend API 变更前必须检查 frontend 和 desktop 消费者
- 项目核心原则：**Never break backward compatibility**——现有 SignalR Hub 协议、REST API shape、配置文件格式均为已稳定的对外契约

---

## 3. Service 模块（`XhMonitor.Service` + `XhMonitor.Core`）

### 3.1 当前依赖清单

| 依赖 | 用途 | Rust 可替代性 |
|------|------|------|
| ASP.NET Core (Kestrel) | HTTP API 服务 | `axum` / `actix-web` ✅ |
| **SignalR Hub** | 向 Desktop 和 React 推送指标 | ⚠️ **阻断级风险**（详见 3.2） |
| EF Core + SQLite | 指标持久化 | `sqlx` / `rusqlite` ✅ |
| Serilog | 结构化日志 | `tracing` + `tracing-subscriber` ✅ |
| LibreHardwareMonitor | CPU/GPU/内存/磁盘/温度传感器 | ❌ **无 Rust 等价实现**（详见 3.3） |
| WMI (`System.Management`) | 硬件平台检测、GPU 厂商 | `wmi` crate ⚠️（成熟度中等） |
| PDH Performance Counters | 进程 CPU/内存采集 | `windows` crate PDH bindings ✅ |
| CsWin32 / P/Invoke | NtQueryInformationProcess、ReadProcessMemory | `windows` crate ✅ |
| RyzenAdj | AMD 功耗采集与调节 | 已是 subprocess，不变 ✅ |
| PawnIO | 底层硬件访问 | 已是 external tool，不变 ✅ |
| DXGI | VRAM 检测 | `windows` crate DXGI bindings ✅ |
| Microsoft.Extensions.Hosting | DI + BackgroundService | `tokio` tasks ✅ |

### 3.2 推送协议迁移：SignalR → SSE

**已确认决策（2026-07-25）**：SignalR Hub 替换为 SSE（Server-Sent Events）。

理由：本项目所有 Hub 方法（`ReceiveHardwareLimits`、`ReceiveSystemUsage`、`ReceiveProcessMetrics`、`ReceiveProcessMetricsLite`、`ReceiveProcessMetadata`）均为**服务端单向推送**，SignalR 的双向 RPC 能力从未使用。SSE 是此场景的自然协议选择，Rust `axum` 内建支持。

**各端迁移工作**：

| 端 | 当前 | 迁移后 |
|------|------|------|
| Service | ASP.NET Core SignalR Hub | `axum` SSE 端点（`/hubs/metrics`） |
| React (`xhmonitor-web`) | `@microsoft/signalr` | `@microsoft/fetch-event-source`（支持自定义 headers） |
| Desktop（Slint/Rust 目标） | `Microsoft.AspNetCore.SignalR.Client` | `eventsource-client` crate 或 `reqwest` streaming SSE 客户端（Rust） |

> 三端需原子同步上线。迁移后客户端按事件名（`system-usage`、`process-metrics` 等）分发，替代 SignalR hub 方法名路由。

**此决策的影响**：SignalR 不再是 Rust Service 化的阻断项。

### 3.3 LibreHardwareMonitor——采集层架构决策

> **架构决策（2026-07-25）**：
> - **系统级指标**（CPU/GPU 温度、负载、网络速率、磁盘速率、功耗等）：**统一由 LHM bridge 提供**，不单独用 PDH/DXGI/WMI 重新实现。理由：各指标的个别实现精度与一致性存疑，LHM 已是经过验证的统一抽象层。
> - **进程级指标**（per-process CPU%、内存、GPU 进程占用等）：保留现有采集方式（`windows` crate PDH、`NtQueryInformationProcess`、`D3DKMTQueryStatistics`），与 LHM bridge 互不干涉。

**本项目消费的 LHM 传感器类型**（来自 `SystemMetricProvider` + `LibreHardwareMonitorGpuProvider` 代码审查；底层实现路径需对照 LHM 源码或运行时日志确认，此处不作推断）：

| 传感器 | HardwareType | SensorType | 底层实现路径 |
|--------|-------------|-----------|------------|
| CPU 温度（Tctl/Tdie/Core Max） | `Cpu` | `Temperature` | 待以 LHM 源码 / 运行时日志验证 |
| GPU 温度（Hot Spot） | `GpuAmd/GpuNvidia/GpuIntel` | `Temperature` | 待以 LHM 源码 / 运行时日志验证 |
| GPU 负载（GPU Core/GPU Usage） | `GpuAmd/GpuNvidia/GpuIntel` | `Load` | 待以 LHM 源码 / 运行时日志验证 |
| 网络上传/下载速率 | `Network` | `Throughput` | 待以 LHM 源码 / 运行时日志验证 |
| 磁盘读写速率 | `Storage` | `Throughput` | 待以 LHM 源码 / 运行时日志验证 |
| 磁盘总空间/剩余空间 | `Storage` | `Data` | 待以 LHM 源码 / 运行时日志验证 |

> 注：以上仅说明本项目请求的 HardwareType + SensorType 组合，不能从中推断 LHM 的底层实现是否依赖 WinRing0 内核驱动。在 Rust 迁移前，需针对每类传感器逐项查验 LHM 源码（`LibreHardwareMonitor/Hardware/` 对应子目录）或捕获运行时日志，确认哪些路径需要内核级访问。

**迁移策略**：

| 策略 | 描述 | 代价 | 评级 |
|------|------|------|------|
| **LHM as subprocess**（已选定） | 将 LHM 封装为独立 .NET 小进程，JSON Lines 输出，Rust 通过 stdin/stdout 或 named pipe 读取；提供全部系统级传感器数据 | 需新增 IPC 层；LHM 进程内存待实测 | ✅ 最低迁移风险，仍待 POC |
| 继续使用 LHM（.NET 壳留存） | LHM 作为 .NET 库留在 Service 进程 | Service 无法完全 Rust 化 | ✅ 零风险，现状维持 |
| WinRing0 FFI（只读 POC） | Rust 通过 FFI 调用已随包发布的 `WinRing0x64.dll`，尝试复现温度/功耗读取 | ⚠️ **高维护成本**：(1) dll 存在≠驱动已加载；(2) HVCI 可能阻断；(3) AMD Zen 5 / AI MAX 395 寄存器布局未验证；(4) 不应暴露 `WriteMsr` | ⚠️ 仅作只读 POC，不作生产计划 |
| ~~PDH / DXGI 系统级直接替代~~ | ~~用 `windows` crate 逐项替代 LHM 系统指标~~ | ~~精度/一致性存疑~~ | ❌ **已排除**——系统级指标统一由 LHM bridge 提供，此路径不再考虑 |

### 3.4 Service Rust 化内存预期

> ⚠️ **以下为估算，非实测，需 POC 验证**

纯 Rust 异步 HTTP 服务（axum + tokio + sqlx）在无 JIT 开销情况下，类似规模监控服务的文献参考范围为 **15–40 MiB private**。但本项目受制于：
- 若保留 LHM .NET 子进程：总内存 = Rust 主服务 + .NET LHM 进程，节省有限
- SignalR 客户端兼容要求若保留 .NET SignalR 壳：节省归零

**路径 C 混合架构的现实预期**：Service 端内存节省极为有限（20–40 MiB），且引入 IPC 复杂度。**迁移 ROI 存疑**。

---

## 4. Desktop 模块（`XhMonitor.Desktop`）

### 4.1 内存构成分析（关键结论）

Desktop GC 堆仅 13 MiB，私有字节 ~149 MiB。**差值 ~136 MiB 来自运行时栈**，具体：
- WPF Presentation 层（PresentationCore、PresentationFramework DLL 映射）
- WinForms 互操作（`UseWindowsForms=true`，用于 `Screen` / `Cursor` API）
- DirectWrite 字体子系统
- AutomationPeer（UI Automation 基础设施，WPF 默认启用）
- CLR / JIT loader（代码页映射入进程虚拟地址空间）

**结论**：替换 Desktop 的收益空间在于消除整个 CLR + WPF 运行时，而非优化托管代码逻辑。

### 4.2 Desktop 需要完整复原的 UI/OS 能力清单

这是选型的硬性约束：

| 能力 | 当前实现 | 复原难度 |
|------|------|------|
| 透明悬浮窗（无背景） | `WS_EX_LAYERED` + `AllowsTransparency` | 所有候选框架均支持 ✅ |
| 点击穿透 (`WS_EX_TRANSPARENT`) | `SetWindowLong` 动态切换 | 需要框架暴露 Win32 style 或提供 FFI ⚠️ |
| 始终置顶 (`HWND_TOPMOST`) | `SetWindowPos` | 所有候选框架均支持 ✅ |
| 任务栏附近定位（`TaskbarMetricsWindow`） | `FindWindow("Shell_TrayWnd")` + `GetWindowRect` + `SetWindowPos`；**不使用 AppBar/SHAppBarMessage**（已读 `TaskbarPlacementService.cs` 确认） | 需要 Win32 FFI；`FindWindow`/`GetWindowRect`/`SetWindowPos` 均为标准 P/Invoke，Slint FFI 可直接调用 ⚠️ |
| 系统托盘 + 右键菜单 | `TrayIconService` / WinForms `NotifyIcon` | Tauri/Slint/Flutter 均有支持 ✅ |
| 全局热键 (`RegisterHotKey`) | Win32 P/Invoke | 需要框架提供或 FFI ⚠️ |
| 边缘吸附拖拽 | 自定义拖拽逻辑 + 屏幕坐标计算 | 可在任意框架中实现 ✅ |
| 长按手势（Timer 计时） | `DispatcherTimer` | 可在任意框架中实现 ✅ |
| 平滑动画（Storyboard） | WPF `Storyboard` / `DoubleAnimation` | 框架特定实现，需重写 ⚠️ |
| 弹出详情面板（Popup） | WPF `Popup` | 需框架级支持或手动窗口 ⚠️ |
| 设置窗口（73KB XAML，复杂表单） | WPF XAML MVVM | **最大重写工作量** ❌ |
| 管理员权限提升 | UAC + `ShellExecute runas` | 均可实现 ✅ |
| 开机自启 | 注册表 / 任务计划 | 均可实现 ✅ |
| 内嵌 Kestrel+YARP Web 服务器 | `WebServerService`（端口 35180） | Rust 内置 `axum` 静态文件服务 ✅ |

### 4.3 候选语言/框架评估

#### 4.3.1 Tauri（Rust + 系统 WebView2）

**思路**：Rust 主进程负责 Win32 集成，WebView2 渲染现有 React UI（`xhmonitor-web`）。

| 项目 | 评估 |
|------|------|
| 保留 React 代码 | 现有 `xhmonitor-web` 可基本复用 ✅ |
| Win32 集成 | Tauri 暴露 `WiryHandle` 可操作 Win32，点击穿透/AppBar 需自行 FFI ⚠️ |
| 内存预期 | **待 POC 验证**（见 4.4）；WebView2 进程内存与页面复杂度强相关，参考值宽泛 |
| 任务栏嵌入 | Tauri 无内建 AppBar 支持，需 Rust 端 P/Invoke `SHAppBarMessage` ⚠️ |
| 构建复杂度 | Rust + Node.js + WebView2，工具链较重 ⚠️ |
| 与当前架构契合度 | 高——Desktop 已有独立 React 前端和 Kestrel Web 服务器 ✅ |

#### 4.3.2 Slint（Rust，自定义 GPU 渲染器）

**思路**：完全用 Rust + Slint DSL 重写 UI，无浏览器依赖。

| 项目 | 评估 |
|------|------|
| 内存基线 | 极低，自定义渲染器无 WebView 开销，典型 ~5–20 MiB（待 POC 验证） |
| UI 重写代价 | **极高**——全部 XAML/React UI 需用 Slint DSL 重写 ❌ |
| Win32 特性支持 | Slint 提供窗口 handle，点击穿透/AppBar 需 FFI ⚠️ |
| 动画能力 | Slint 有动画系统，但与 WPF Storyboard 语义差异大 ⚠️ |
| 放弃 React 生态 | ECharts 图表、SignalR 客户端均失效，需重写数据消费层 ❌ |

#### 4.3.3 Flutter（Dart，Skia/Impeller 渲染）

**思路**：用 Flutter Windows 重写 Desktop UI。

| 项目 | 评估 |
|------|------|
| 内存基线 | 典型 Windows Flutter app ~30–60 MiB（待 POC 验证） |
| UI 重写代价 | **高**——需重写全部 UI，但 Flutter widget 体系表达能力强 ⚠️ |
| Win32 特性支持 | `win32` package 覆盖 AppBar、热键等 ✅ |
| 语言 | Dart，非 Rust ⚠️（但达成轻量目标） |
| 放弃 React 生态 | 同上，SignalR 客户端需替换（Dart SignalR 包存在但不官方）⚠️ |

#### 4.3.4 原生 C++ / Win32 直接

| 项目 | 评估 |
|------|------|
| 内存基线 | 最低，~5–15 MiB 可达 |
| 开发代价 | **极高**，现代 Win32 UI 开发效率极低 ❌ |
| UI 能力 | 完整 Win32 支持，点击穿透/AppBar/热键全部原生 ✅ |
| 适合度 | 不适合——现有团队无 C++ UI 背景，维护成本极高 |

### 4.4 POC 验证方法论（内存测量规范）

> 所有候选框架的内存数字在此文档中均为**预估/文献参考**，不是项目实测。必须通过以下方法在相同场景下实测。

**测量场景（基准）**：
- 程序启动后 5 分钟稳态（JIT/初始化开销已消退）
- 已连接 Service，SignalR 数据正常推送（或等效的 WebSocket 推送）
- 悬浮窗可见（非折叠态），显示 ≥1 个进程指标
- 无 Settings 窗口打开

**测量指标**：

| 指标 | 采集命令 | 说明 |
|------|------|------|
| Private Bytes | `(Get-Process -Name "<exe>").PrivateMemorySize64 / 1MB` | 主要信号 |
| Working Set | `(Get-Process -Name "<exe>").WorkingSet64 / 1MB` | 次要信号（受 OS trim 影响） |
| 进程树 Private Bytes 合计 | 见下方脚本 | **Tauri 必须包含 WebView2 子进程** |

**进程树测量脚本**（PowerShell）：
```powershell
function Get-ProcessTreeMemory {
    param([int]$ParentPid)
    $allProcs = Get-CimInstance Win32_Process
    $tree = @()
    $queue = [System.Collections.Queue]::new()
    $queue.Enqueue($ParentPid)
    while ($queue.Count -gt 0) {
        $pid = $queue.Dequeue()
        $proc = $allProcs | Where-Object { $_.ProcessId -eq $pid }
        if ($proc) {
            $tree += $proc
            $children = $allProcs | Where-Object { $_.ParentProcessId -eq $pid }
            $children | ForEach-Object { $queue.Enqueue($_.ProcessId) }
        }
    }
    $totalPrivate = ($tree | Measure-Object -Property PrivatePageCount -Sum).Sum / 1MB
    Write-Host "进程树 Private Bytes 合计: $([math]::Round($totalPrivate, 1)) MiB"
    $tree | Select-Object Name, ProcessId, @{N='Private_MiB';E={[math]::Round($_.PrivatePageCount/1MB,1)}} | Sort-Object Private_MiB -Descending | Format-Table
}

# 用法：
# Get-ProcessTreeMemory -ParentPid (Get-Process -Name "tauri-app-name").Id
```

> **Tauri 特别说明**：WebView2 使用 Chromium 多进程架构，主窗口会生成多个 `msedgewebview2.exe` 子进程（GPU 进程、renderer 进程等）。**必须对整个进程树求和**，否则数字严重低估实际内存占用。

**对比基准（当前 WPF）**：
```powershell
# Desktop 单进程（当前无子进程）
(Get-Process -Name "XhMonitor.Desktop").PrivateMemorySize64 / 1MB
# 参考值：~149 MiB（优化后实测）
```

---

## 5. 总体迁移路线建议

### 优先级矩阵

| 模块 | Rust 化可行性 | 内存收益 | 风险 | 建议 |
|------|------|------|------|------|
| **Service**（完整 Rust 化） | 低——SignalR + LHM 双重阻断 | 理论 60–90 MiB，实际受制于 LHM subprocess | 高 | 不建议近期 |
| **Service**（混合：Rust 采集内核 + .NET SignalR 壳） | 中 | 20–40 MiB，ROI 存疑 | 中 | 需先 POC 评估是否值得 |
| **Desktop → Tauri** | 高（保留 React，Win32 FFI 可行） | **待 POC 验证**；若 WebView2 进程树 > 100 MiB 则收益消失 | 中（Win32 特性需 FFI） | **首选 POC 目标** |
| **Desktop → Slint** | 高（技术上可行） | 最优，~5–20 MiB | 极高（全量 UI 重写） | 长期备选 |
| **Desktop → Flutter** | 高（技术上可行） | 中，~30–60 MiB | 高（UI 重写） | 长期备选 |

### 推荐执行顺序

```
阶段 0（当前）
  └─ 继续推进 C# 层面的内存优化（M05/M10 实验），建立更准确的生产基线

阶段 1：Desktop Tauri POC（2–4 周）
  ├─ 用 Tauri 搭建最小可用 Desktop 壳
  ├─ 复现点击穿透、置顶、任务栏嵌入三个核心 Win32 特性
  ├─ 嵌入现有 React 前端（xhmonitor-web）
  └─ 按 4.4 方法论测量进程树内存，与 WPF 基线对比
     → 决策门控：WebView2 进程树 < 80 MiB → 继续；≥ 100 MiB → 转评 Slint

阶段 2：根据 POC 结果二选一
  路径 A（Tauri 可行）：逐步迁移 Desktop，保留 Service 不变
  路径 B（Tauri 内存不达标）：评估 Slint 全量 UI 重写，或接受当前 C# 水位

阶段 3（可选，远期）：
  └─ 评估 Service 混合架构（仅在 Desktop 迁移完成、运维模式稳定后再考虑）
```

---

## 6. 风险矩阵

| 风险 | 触发场景 | 严重度 | 缓解措施 |
|------|------|------|------|
| SignalR 协议不兼容 | Service 任何部分迁移到非 .NET SignalR 服务端 | 严重 | 保持 .NET SignalR 壳；协议迁移必须三端原子同步 |
| LibreHardwareMonitor 功能丢失 | Service Rust 化放弃 LHM | 高 | 保留 LHM .NET 子进程或接受温度/功耗数据退化 |
| Tauri WebView2 内存超出预期 | POC 实测进程树 > 100 MiB | 中 | 决策门控设定，阶段 1 结束即判断 |
| Win32 特性不完整 | 点击穿透/AppBar 在非 WPF 框架下缺失 | 高 | POC 第一步必须验证这三个特性 |
| UI 重写工作量低估 | Slint/Flutter 路径 | 高 | 设置明确工时预算上限；超出则维持现状 |
| "Never break backward compatibility" 违反 | SignalR/API shape 变更未同步客户端 | 严重 | 架构约束 spec 要求变更前检查所有消费者 |
| WinRing0 FFI 驱动加载失败 / HVCI 阻断 | 在 Service Rust 化过程中尝试直接调用 WinRing0 读取温度/功耗 | 高 | 将 WinRing0 FFI 仅作只读 POC；必须先验证驱动加载、管理员权限、HVCI 策略和目标 CPU 寄存器映射，不作生产计划 |
| WinRing0 寄存器映射未验证 | AMD Zen 5 / AI MAX 395 MSR 布局与 Zen 3/4 文档不同 | 高 | 使用前须逐项对照 LHM 源码（`Hardware/CPU/AMD` 路径）确认寄存器偏移和固件 variant 处理 |

---

## 7. 结论

1. **Service 短期内不适合完整 Rust 化**：SignalR Hub 协议和 LibreHardwareMonitor 是双重阻断，绕过任一条均引入高风险或功能退化。

2. **Desktop 是更值得先投入的方向**：内存浪费主体是 CLR+WPF 运行时（~136 MiB），替换框架可消除此开销；React 前端已存在，Tauri 路径可最大化复用。

3. **所有内存数字（非 C# 基线部分）均为待验证预期**：必须按 4.4 方法论在相同场景下实测，特别是 Tauri 需测量整个 WebView2 进程树，而不仅是主进程。

4. **建议下一步**：在此分支上创建 `poc/desktop-tauri` 子目录，搭建最小 Tauri 壳，优先验证点击穿透 + 任务栏嵌入 + WebView2 进程树内存三项，给出可量化的决策数据。
