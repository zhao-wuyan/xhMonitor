# XhMonitor.Desktop

## 模块职责

WPF 桌面前端应用，负责：
- 启动并管理后端服务进程（XhMonitor.Service）
- 托管嵌入式 Web 前端服务器（Kestrel + YARP 反向代理）
- 渲染系统监控悬浮窗和任务栏贴边迷你窗口
- 系统托盘图标与交互入口
- 通过 SignalR 实时接收后端推送的指标数据

## 技术栈

| 技术 | 用途 |
|------|------|
| .NET 8 WPF | UI 框架，目标 `net8.0-windows` |
| Windows Forms | `Screen`、`Cursor` 多显示器定位辅助 |
| Microsoft.Extensions.Hosting | 泛型主机，DI 容器、托管服务生命周期 |
| Microsoft.Extensions.DependencyInjection | 服务注册与依赖注入 |
| Microsoft.AspNetCore.SignalR.Client | 连接后端 SignalR Hub，接收实时数据 |
| Microsoft.AspNetCore.App (FrameworkReference) | 内嵌 Kestrel Web 服务器 |
| Yarp.ReverseProxy 2.x | Web 服务器反代 `/api/*` 和 `/hubs/*` 到后端 |
| System.Text.Json 8.x | JSON 序列化/反序列化 |
| XhMonitor.Core | 共享配置常量（`ConfigurationDefaults`、`ConfigurationDefaults.Keys`） |

## 目录结构

```
XhMonitor.Desktop/
├── App.xaml / App.xaml.cs          # 应用入口，泛型主机配置，单实例 Mutex
├── MainWindow.xaml / .cs           # 空壳主窗口（实际 UI 由 FloatingWindow / TaskbarMetricsWindow 承载）
├── FloatingWindow.xaml / .cs       # 悬浮监控窗口（可收起/展开/锁定/穿透模式）
├── Configuration/
│   └── UiOptimizationOptions.cs    # UI 性能调优选项（进程列表刷新节流）
├── Constants/
│   └── SignalREvents.cs            # SignalR Hub 事件名常量
├── Converters/                     # WPF IValueConverter 实现（8 个）
├── Dialogs/
│   └── InputDialog.xaml / .cs     # 通用单行输入对话框
├── Extensions/
│   └── ObservableCollectionExtensions.cs  # AddRange / ReplaceAll 扩展
├── Localization/
│   └── RuntimeDependencyPrompts.cs # .NET 运行时缺失时的多语言提示文本
├── Models/                         # 数据模型（DTO、显示设置）
├── Services/                       # 全部业务服务（接口 + 实现）
├── ViewModels/                     # MVVM ViewModel 层
├── Windows/                        # 设置窗口、关于窗口、任务栏贴边窗口
├── wwwroot/                        # 嵌入式 Web 前端资源（构建时从 xhmonitor-web/dist 复制）
├── service-endpoints.json          # 服务端点覆盖配置
├── service-endpoints.schema.json   # 端点配置 JSON Schema
├── appsettings.json                # 泛型主机配置
└── XhMonitor.Desktop.csproj       # 项目文件（含 Web 资产构建 MSBuild Target）
```

## 应用启动流程

```
OnStartup()
  1. CheckRuntimeEnvironment()       # 检查 .NET 8+ 和 Desktop Runtime
  2. WaitForRestartParentExit()      # 重启场景：等待父进程退出（--restart-parent <pid>）
  3. Mutex 单实例检查                  # "XhMonitor_Desktop_SingleInstance"
  4. Host.CreateDefaultBuilder()
       ConfigureAppConfiguration     # appsettings.json + appsettings.{env}.json
       ConfigureServices             # 注册所有单例服务（见服务层章节）
       AddHostedService<ApplicationHostedService>
  5. _host.StartAsync()              # 启动泛型主机 → 触发 ApplicationHostedService
       ApplicationHostedService.ExecuteAsync()
         Task.WhenAll(
           BackendServerService.StartAsync()   # 启动/连接后端服务进程
           WebServerService.StartAsync()       # 启动内嵌 Kestrel Web 服务器
         )
  6. WindowManagementService.InitializeMainWindow()
       创建 FloatingWindow + TaskbarMetricsWindow
       TrayIconService.Initialize()
       加载显示模式配置（后台异步拉取，冷启动先用本地标识）
       ApplyDisplayModes()

OnExit()
  1. WindowManagementService.CloseMainWindow()
  2. host.StopAsync(3s timeout)
  3. Mutex.ReleaseMutex()
```

## 关键窗口

### FloatingWindow（悬浮监控窗口）

- 默认启动入口的主显示窗口
- 面板状态机：`Collapsed` → `Expanded`（Hover）→ `Locked`（Click）→ `Clickthrough`（穿透模式）
- 展开后显示进程列表（Top 5 + 置顶进程）
- 支持电源方案快速切换（长按功耗指标）
- 支持进程强制终止（Kill 操作）
- 设置 `ViewModel: FloatingWindowViewModel`

### TaskbarMetricsWindow（任务栏贴边迷你窗口）

- 可吸附到屏幕四边（Top / Bottom / Left / Right）
- 拖拽时自动检测贴边距离（80px 阈值）或窗口超出屏幕一半时自动吸附
- 支持两种视觉风格：`Text`（纯文本）和 `Bar`（彩色进度条）
- 贴边到左/右时自动切换为竖向布局（Vertical Orientation）
- 通过 Win32 `SetWindowPos` 保持 TopMost（HWND_TOPMOST）
- 关闭按钮触发 `Hide()`，仅在 `AllowClose()` 后真正关闭
- ViewModel: `TaskbarMetricsViewModel`

### SettingsWindow（设置窗口）

- 通过托盘菜单打开
- ViewModel: `SettingsViewModel`（Transient，每次新建实例）
- 依赖 `StartupManager`（开机启动）、`AdminModeManager`（管理员模式）、`BackendServerService`（服务重启）

### AboutWindow / InputDialog

- `AboutWindow`：显示版本信息的对话框
- `InputDialog`：通用单行输入，用于添加进程关键词

## 服务层架构

所有服务均以 `interface + class` 模式实现，在 `App.xaml.cs` 中以 Singleton 注册。

### 服务接口总览

| 接口 | 实现类 | 职责 |
|------|--------|------|
| `IServiceDiscovery` | `ServiceDiscovery` | 从 service-endpoints.json 解析端口，探测后端健康状态确定实际端口 |
| `IBackendServerService` | `BackendServerService` | 启动/停止 XhMonitor.Service 进程，支持开发模式（dotnet run）和发布模式（exe） |
| `IWebServerService` | `WebServerService` | 内嵌 Kestrel + YARP 反代，托管 wwwroot 静态文件，支持局域网访问和访问密钥 |
| `ITrayIconService` | `TrayIconService` | 系统托盘图标，NotifyIcon 封装，提供菜单项回调 |
| `IWindowManagementService` | `WindowManagementService` | 管理 FloatingWindow 与 TaskbarMetricsWindow 的显示模式切换与协调 |
| `IAdminModeManager` | `AdminModeManager` | 管理员权限检测、UAC 重启、admin-mode.flag 持久化 |
| `ITaskbarPlacementService` | `TaskbarPlacementService` | 任务栏位置探测辅助 |
| `IProcessManager` | `ProcessManager` | 进程终止操作封装 |
| `IPowerControlService` | `PowerControlService` | 电源方案切换（通过后端 API）、设备验证预热 |
| `IStartupManager` | `StartupManager` | 开机启动注册（注册表） |
| `IDesktopLaunchModeFlagManager` | `DesktopLaunchModeFlagManager` | 持久化上次启动模式标识（FloatingWindow / MiniEdgeDock） |
| `ApplicationHostedService` | - | IHostedService，并发启动 Backend + Web 服务 |

### ServiceDiscovery 端口解析

启动时按以下顺序确定端口：
1. 读取 `service-endpoints.json`（`ServiceEndpoints.ApiBaseUrl`）
2. 对配置端口 +0 到 +10 范围探测健康检查接口 `/api/v1/config/health`
3. 确认返回 `{"status":"Healthy"}` 或 `{"status":"Unhealthy"}` 的端口为实际端口
4. WebPort 从 `ConfigurationDefaults.System.WebPort` 开始找第一个空闲端口（跳过 ApiPort）

默认端口：`ApiPort = 35179`，`WebPort = 35180`

### BackendServerService 双模式启动

| 场景 | 判断条件 | 行为 |
|------|----------|------|
| 发布模式 | `../Service/XhMonitor.Service.exe` 存在 | 直接启动 exe，支持管理员 UAC（Verb="runas"） |
| 开发模式 | exe 不存在 | `dotnet run --project <XhMonitor.Service 路径>` |
| 已运行 | 目标端口已占用 | 直接返回，跳过启动 |

启动后轮询端口占用（最多 30s / 15s）确认服务就绪，端口就绪后再等 1s 确保 SignalR Hub 初始化完成。

### WebServerService 架构

```
http://localhost:WebPort
  ├── /api/**        → YARP 反代 → http://localhost:ApiPort/api/**
  ├── /hubs/**       → YARP 反代 → http://localhost:ApiPort/hubs/**
  └── /**            → PhysicalFileProvider(wwwroot) 静态文件 + index.html SPA 回退
```

局域网访问启用时（`EnableLanAccess=true`）：
- Kestrel 监听 `0.0.0.0:WebPort`
- 安全中间件：IP 白名单（`IpWhitelistMatcher`）+ 访问密钥（`X-Access-Key` 请求头 / `Authorization: Bearer` / `?access_token` 查询参数）
- 本机请求（loopback + 本地网卡地址）始终放行，不受安全策略限制
- 安全快照缓存 200ms 避免高频请求重复读取配置

## ViewModels

### TaskbarMetricsViewModel

- 职责：任务栏贴边窗口的数据计算和布局尺寸推算
- SignalR 订阅：`SystemUsageReceived`、`HardwareLimitsReceived`
- `RebuildColumns()`：每次数据更新时重新计算所有指标的显示文本和像素尺寸
- 使用 `FormattedText` 精确测量 JetBrains Mono 字体宽高，避免截断
- 支持两种视觉风格（Text / Bar）和两种方向（Horizontal / Vertical）
- `SyncColumns()`：最小化 ObservableCollection 变更，直接更新现有对象属性避免重新创建

指标颜色（固定，与 xhmonitor-web 一致）：
| 指标 | 颜色 |
|------|------|
| 网络 | `#56B4E9` |
| CPU | `#D55E00` |
| 内存 | `#F0E442` |
| GPU | `#E69F00` |
| 显存 | `#009E73` |
| 功耗 | `#CC79A7` |

### FloatingWindowViewModel

- 职责：悬浮窗的全量数据状态和进程列表管理
- SignalR 订阅：`SystemUsageReceived`、`HardwareLimitsReceived`、`ProcessDataReceived`、`ProcessMetaReceived`、`ConnectionStateChanged`
- 进程列表维护：`_processIndex`（Dictionary keyed by PID）、`TopProcesses`（内存+显存前5）、`PinnedProcesses`（置顶）、`AllProcesses`（全量）
- 进程刷新节流：`DispatcherTimer` + 可配置间隔（默认 150ms），由 `UiOptimizationOptions` 控制
- 面板状态：`Collapsed` / `Expanded` / `Locked` / `Clickthrough`

### SettingsViewModel

- 职责：设置界面的表单数据绑定和 API 读写
- 通过 `HttpClient` GET `/api/v1/config/settings` 加载，POST 保存
- 属性：主题色、透明度、进程关键词、采集间隔、端口配置、开机启动等
- 实现 `INotifyPropertyChanged`，Transient 生命周期（每次打开设置窗口新建实例）

## SignalRService

连接到 `http://localhost:{SignalRPort}/hubs/metrics`，订阅以下 Hub 事件：

| Hub 事件名 | 事件委托 | 数据类型 |
|-----------|---------|---------|
| `ReceiveHardwareLimits` | `HardwareLimitsReceived` | `HardwareLimitsDto`（内存/显存上限） |
| `ReceiveSystemUsage` | `SystemUsageReceived` | `SystemUsageDto`（CPU/内存/GPU/显存/功耗/网速/电源方案） |
| `ReceiveProcessMetrics` | `ProcessDataReceived` | `ProcessDataDto`（进程指标列表） |
| `ReceiveProcessMetadata` | `ProcessMetaReceived` | `ProcessMetaDto`（进程元数据：名称/命令行/DisplayName） |

特性：
- `WithAutomaticReconnect()` 自动重连
- `ConnectionStateChanged` 事件通知连接状态变化
- `ReconnectAsync()`：主动断开重连，用于服务重启后刷新连接

## WPF Value Converters

| 文件 | 输入 | 输出 | 用途 |
|------|------|------|------|
| `MemoryPercentageColorConverter.cs` | 内存使用率 (double) | Brush | 内存使用率阈值颜色（低/中/高） |
| `MemoryUnitConverter.cs` | 字节数 (double) | string | 自动选择 MB/GB 显示单位 |
| `MetricValueColorConverter.cs` | 指标值 (double) | Brush | 通用指标阈值颜色 |
| `MetricValueConverter.cs` | 指标键值对 (MetricValue) | string | 格式化指标显示值 |
| `MiddleEllipsisConverter.cs` | 长字符串 | string | 中间省略号截断进程命令行 |
| `NetworkSpeedConverter.cs` | 网速 MB/s (double) | string | 自动选择 KB/s、MB/s 单位 |
| `PowerValueConverter.cs` | 功耗 W (double) | string | 格式化功耗显示值 |
| `ProgressWidthConverter.cs` | 百分比 + 容器宽度 | double | 进度条填充宽度（MultiBinding） |

## 任务栏贴边集成

`TaskbarMetricsWindow` 与 `TaskbarPlacementService` 协作实现贴边逻辑：

- 贴边方向：`EdgeDockSide` 枚举（`Top` / `Bottom` / `Left` / `Right`）
- 吸附触发：拖拽释放时检测最近边距（< 80px）或窗口超出屏幕 50%
- 布局切换：Left/Right 贴边 → Vertical 布局（竖排指标），Top/Bottom → Horizontal 布局
- 视觉状态：背景色、圆角、边框颜色、发光效果随贴边方向动态调整
- TopMost 维持：`Deactivated` 事件触发 `ReassertTopMost()`（Win32 P/Invoke）
- 窗口宽度限制：Left/Right 贴边时强制 `[14px, 24px]` 避免过宽

## 显示模式管理

`WindowManagementService` 管理两种显示模式的互斥/共存：

| 模式 | 对应窗口 | 配置开关 |
|------|----------|---------|
| `FloatingWindow` | `FloatingWindow` | `EnableFloatingMode` |
| `EdgeDock` | `TaskbarMetricsWindow` | `EnableEdgeDockMode` |

规则：
- 两种模式均开启时，默认激活 `FloatingWindow`
- 从贴边窗口拖离（`UndockedFromEdge` 事件）时，若两种模式均开启，自动切换回悬浮窗
- 启动时先用本地 `DesktopLaunchMode` 标识（`desktop-launch-mode.flag` 文件）决定初始模式，后台异步拉取后端配置后再同步

## 管理员模式

`AdminModeManager` 通过 `admin-mode.flag` 文件（位于应用目录）持久化管理员模式开关：
- `IsAdminModeEnabled()`：检测标志文件是否存在
- `SetAdminModeEnabled(true)`：写入标志文件
- `RestartAsAdministrator()`：使用 `Verb = "runas"` 触发 UAC 重启应用
- `BackendServerService` 在管理员模式下以 `runas` 启动 `XhMonitor.Service.exe`（用于需要高权限的硬件监控）

## 配置文件

### service-endpoints.json

覆盖默认服务端口，结构：
```json
{
  "ServiceEndpoints": {
    "ApiBaseUrl": "http://localhost:35179",
    "SignalRUrl": "http://localhost:35179/hubs/metrics"
  }
}
```
文件不存在时使用内置默认值，`ServiceDiscovery` 会通过健康检查探测实际端口。

### appsettings.json

泛型主机标准配置，支持：
```json
{
  "UiOptimization": {
    "EnableProcessRefreshThrottling": true,
    "ProcessRefreshIntervalMs": 150
  },
  "ServiceExecutablePath": ""
}
```
`ServiceExecutablePath` 可覆盖后端服务 exe 路径（绝对或相对于应用目录）。

## Web 资产构建

`XhMonitor.Desktop.csproj` 内置 MSBuild Target：

1. `BuildWebAssets`（BeforeTargets=BeforeBuild,BeforePublish）：
   - 若 `xhmonitor-web/node_modules` 不存在则执行 `npm install`
   - 执行 `npm run build`
   - 将 `xhmonitor-web/dist/**` 复制到 `XhMonitor.Desktop/wwwroot/`

2. `CopyWebAssetsToOutput`（AfterTargets=BuildWebAssets）：
   - 清空并重新复制到 `$(OutDir)wwwroot/`（确保 bin 目录不含陈旧资产）

3. `CopyWebAssetsToPublish`（AfterTargets=Publish）：
   - 清空并重新复制到 `$(PublishDir)wwwroot/`

## 扩展点

`WindowManagementService` 中标记了 `[Plugin Extension Point]` 的回调：
- `OnMetricLongPressStarted`：指标长按开始，当前用于电源方案切换预热设备验证
- `OnMetricActionRequested`：指标动作触发，当前实现长按功耗指标切换电源方案
- `OnProcessActionRequested`：进程动作触发，当前实现 kill 操作

## 开发注意事项

- 所有 UI 更新必须通过 `Dispatcher.BeginInvoke()` 切换到主线程
- `SignalRService` 事件回调在 SignalR 后台线程触发，不能直接操作 UI
- `TaskbarMetricsViewModel.RebuildColumns()` 频繁调用，使用 `SyncColumns()` 原地更新避免 ObservableCollection 频繁 Add/Remove
- `FloatingWindowViewModel` 的进程刷新有节流机制，由 `UiOptimizationOptions.ProcessRefreshIntervalMs` 控制（默认 150ms，有效范围 16ms-2000ms）
- 窗口关闭拦截：`FloatingWindow` 和 `TaskbarMetricsWindow` 的 `Closing` 事件默认 Cancel，必须先调用 `AllowClose()` 才能真正关闭
- `SettingsViewModel` 是 Transient，不要在 Singleton 服务中长期持有其引用
