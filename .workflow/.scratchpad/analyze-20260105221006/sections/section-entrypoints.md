# 系统入口与启动流程

## 双进程架构的启动设计

XhMonitor 系统采用双进程分离架构,整体启动流程围绕桌面应用(XhMonitor.Desktop)与后端服务(XhMonitor.Service)的协同展开。从系统层面审视,桌面应用承担用户交互入口与进程编排职责,后端服务则专注于性能监控数据的采集、处理与分发。这一设计使得系统能够在保证界面响应性的同时,通过独立进程隔离监控逻辑,避免 UI 线程阻塞对数据采集精度的影响。

桌面应用的入口位于 `XhMonitor.Desktop\App.xaml.cs`,该类继承自 WPF 的 Application 基类,通过重写 OnStartup 方法实现启动编排。值得注意的是,系统在应用层即建立了会话终止监听机制(`SessionEnding` 事件,第 26 行),这一设计确保在操作系统关机或注销场景下,后端服务能够优雅退出,避免数据库连接或文件句柄泄漏。启动序列的核心逻辑体现为三个异步任务的并行启动:`StartBackendServerAsync()`、`StartWebAsync()` 以及 FloatingWindow 的实例化与显示(第 29-38 行)。这一并行策略使得后端服务的初始化延迟不会阻塞桌面窗口的呈现,用户在服务就绪前即可看到界面框架,提升了启动体验的流畅性。

后端服务的入口位于 `XhMonitor.Service\Program.cs`,该文件采用 ASP.NET Core 的 Minimal Hosting API 模式组织启动逻辑。系统在进入依赖注入配置前,首先执行控制台编码设置与 Serilog 日志引擎的初始化(第 14-52 行)。日志目录的确定逻辑基于 `AppContext.BaseDirectory`,这一选择确保在 Windows 服务模式下,日志文件能够写入可执行文件所在目录而非工作目录,避免权限问题。Serilog 的配置采用三路输出策略:Console、Debug 与滚动文件,其中文件输出设置了 50MB 的单文件大小限制与 7 天的保留策略,体现了对长期运行场景的考量。

## 依赖注入容器的构建序列

服务容器的构建遵循严格的初始化顺序,首要步骤是数据库上下文工厂的注册(第 69-74 行)。系统使用 `AddDbContextFactory<MonitorDbContext>` 而非传统的 `AddDbContext`,这一选择源于对多线程并发访问的支持需求。在后续的 Worker、AggregationWorker 与 DatabaseCleanupWorker 中,每个后台服务均通过工厂模式创建独立的 DbContext 实例,避免了 Entity Framework Core 在非线程安全上下文下的状态冲突。连接字符串的验证逻辑前置于容器构建阶段(第 63-67 行),若配置文件缺失 `DatabaseConnection` 项,系统将在启动早期抛出 InvalidOperationException,防止进入半初始化状态。

仓储层的注册采用单例模式(`AddSingleton<IProcessMetricRepository, MetricRepository>`,第 76 行),这一决策基于 MetricRepository 内部的并发安全设计。该类通过 DbContextFactory 按需创建上下文,自身不持有可变状态,因此可安全共享于多个后台服务。紧随其后的三个 Hosted Service 注册(第 78-80 行)定义了系统的核心工作负载:Worker 负责进程扫描与指标推送,AggregationWorker 执行原始数据的时间序列聚合,DatabaseCleanupWorker 则根据保留策略清理过期数据。这三个服务的启动顺序由 ASP.NET Core 的 Hosted Service 机制统一管理,系统保证在 Kestrel 服务器启动前完成所有后台服务的 StartAsync 调用。

指标提供者注册表(`MetricProviderRegistry`,第 82-96 行)的初始化体现了插件架构的设计理念。该组件的构造函数接收插件目录路径参数,在实例化时自动扫描并加载符合 IMetricProvider 接口约定的外部程序集。内置提供者的注册通过 `RegisterBuiltInProviders()` 完成,该方法硬编码了 CPU、GPU、Memory 与 VRAM 四类提供者的映射关系(参见 `XhMonitor.Service\Core\MetricProviderRegistry.cs` 第 15-23 行)。插件目录路径的解析逻辑具备回退机制:若配置文件未指定 `MetricProviders:PluginDirectory`,系统将默认使用应用根目录下的 `plugins` 子目录(第 89-93 行)。这一设计使得开发环境与发布版本能够共享相同的配置逻辑,仅需调整 appsettings.json 即可适配不同的部署结构。

## 数据库迁移的自动化流程

应用构建完成后,系统立即执行数据库迁移检查与应用逻辑(第 136-148 行)。该过程使用作用域服务(`CreateScope`)获取 DbContext 实例,通过 `Database.Migrate()` 方法触发 Entity Framework Core 的迁移引擎。这一设计的关键优势在于自动化:无论是首次部署还是版本升级,系统均能自主检测当前数据库模式与代码模型的差异,并应用必要的 DDL 变更。异常处理策略采用降级模式,若迁移失败仅记录警告日志而非终止启动(第 145-148 行),这一选择平衡了可用性与一致性——在数据库文件损坏等极端场景下,系统仍可启动以便管理员介入修复,而非进入完全不可用状态。

迁移完成后的启动流程进入中间件管道配置阶段。CORS 策略的注册(第 115-124 行)显式允许来自 localhost:3000、localhost:5173、localhost:35180 以及 `app://.` 的跨域请求,这些端口分别对应开发环境的 React DevServer、Vite DevServer、内嵌 Kestrel 静态服务器以及桌面应用的 WebView2 协议。Credentials 的启用(`AllowCredentials()`)表明系统在跨域场景下需要传递认证凭据,这与 SignalR 的连接建立机制相关——Hub 连接的协商过程需要携带 Cookie 或 Authorization Header 以完成身份验证。

## 桌面应用的进程编排策略

回到桌面应用的启动流程,`StartBackendServerAsync()` 方法展现了复杂的进程管理逻辑(第 207-320 行)。系统首先检测是否为发布版本,通过探测相对路径 `../Service/XhMonitor.Service.exe` 的存在性判断运行模式(第 212-213 行)。在发布模式下,系统假设后端服务由独立的批处理脚本或系统服务启动,因此进入等待逻辑:轮询 35179 端口的监听状态,超时后向用户显示提示对话框(第 217-240 行)。这一设计避免了双重启动的资源浪费,同时为用户提供了明确的故障诊断信息。

在开发模式下,系统通过 `dotnet run` 命令启动后端项目(第 266-278 行)。进程的工作目录设置为 XhMonitor.Service 项目的绝对路径,标准输出与错误流均被重定向至 Debug 输出(第 280-290 行),这一配置使得开发者能够在 Visual Studio 的 Output 窗口统一查看两个进程的日志,简化了调试流程。启动后的就绪检测逻辑通过 `WaitForServerReadyAsync()` 实现,该方法以 500 毫秒为间隔轮询端口状态,最长等待 30 秒(第 322-340 行)。端口检测成功后,系统额外等待 1 秒以确保 SignalR Hub 的注册完成,这一延迟是对 ASP.NET Core 启动生命周期的经验性补偿——Kestrel 开始监听端口的时刻略早于所有中间件完成初始化的时刻。

Web 前端的启动流程(`StartWebAsync()`,第 383-471 行)采用内嵌 Kestrel 服务器的方案,而非外部 Node.js 进程。系统检测 `xhmonitor-web/dist` 目录的存在性,若缺失则触发 `npm install` 与 `npm run build` 的自动构建流程(第 403-419 行)。构建完成后,使用 `WebApplication.CreateBuilder()` 创建独立的 Kestrel 实例,监听 35180 端口,并配置静态文件中间件与 SPA 回退路由(第 427-452 行)。这一设计的优势在于简化部署:最终用户无需安装 Node.js 运行时或配置反向代理,桌面应用即可自主提供完整的 Web 界面访问能力。服务器的生命周期通过 CancellationTokenSource 管理(第 422、437-440 行),在应用退出时触发优雅关闭。

## 三阶段启动序列的执行逻辑

后端服务启动后,Worker 类的 ExecuteAsync 方法进入执行(参见 `XhMonitor.Service\Worker.cs` 第 40-76 行)。系统采用三阶段启动模式,各阶段具备明确的职责边界与时序依赖。第一阶段为内存限制检测(第 44-46 行),通过 `SendMemoryLimitAsync()` 获取系统物理内存总量并推送至客户端,该步骤的执行时机早于任何性能计数器的初始化,避免计数器创建过程中的内存分配干扰基线测量。第二阶段为性能计数器预热(第 48-52 行),`WarmupPerformanceCountersAsync()` 方法内部创建进程级别的 CPU 与内存计数器,并执行首次 NextValue() 调用。这一预热操作源于 Windows Performance Counter API 的特性:首次查询返回值通常为 0,仅在第二次调用后才能获得有效数据,因此系统通过显式预热确保后续采集的准确性。

第二点五阶段启动两个独立的后台任务循环:`RunVramLimitCheckAsync()` 与 `RunSystemUsageLoopAsync()`(第 54-59 行)。这两个任务以 Task.Run 方式在线程池启动,与主执行流并行运行,互不阻塞。VRAM 限制检测任务每 30 秒查询一次显卡显存总量,该数据的变化频率极低,因此采用低频轮询以降低 GPU 驱动调用开销。系统使用率任务则以配置的监控间隔(默认 3 秒)持续推送 CPU、内存、GPU 与 VRAM 的实时使用率,该循环的执行不依赖进程扫描结果,确保即使无关键进程运行,系统级指标仍可正常上报。

第三阶段为首次进程数据采集(第 61-63 行),`SendProcessDataAsync()` 方法触发进程扫描器(`ProcessScanner`)的执行,该组件根据 appsettings.json 中的 `Monitor:Keywords` 配置过滤目标进程。扫描完成后,系统通过 SignalR Hub 推送进程列表与详细指标至所有连接的客户端。三阶段启动完成后,Worker 进入主循环(第 65-76 行),以配置的间隔周期性执行进程数据采集,直至 CancellationToken 触发停止信号。每次循环迭代均记录执行耗时至 Debug 日志,这一遥测数据为性能调优提供了量化依据——若单次采集耗时超过监控间隔,即表明需要优化扫描算法或调整进程过滤策略。

## 桌面窗口的异步初始化模式

桌面应用的 FloatingWindow 类采用分阶段初始化设计,构造函数仅执行同步的基础设置(参见 `XhMonitor.Desktop\FloatingWindow.xaml.cs` 第 46-67 行),包括 ViewModel 绑定、窗口位置存储器创建以及事件订阅。真正的异步初始化逻辑延迟至 Loaded 事件触发时执行(第 188-203 行),该事件由 WPF 框架在窗口完成布局计算后引发,此时 HWND 句柄已创建,可安全执行 Win32 互操作调用。OnLoaded 方法内部调用 ViewModel 的 `InitializeAsync()`,该方法实现了带重试的 SignalR 连接建立逻辑(参见 `XhMonitor.Desktop\ViewModels\FloatingWindowViewModel.cs` 第 207-233 行)。

连接重试机制设定了 10 次最大尝试次数与 2 秒的固定间隔,这一策略应对后端服务启动延迟的场景。由于桌面应用与后端服务的启动为异步并行,SignalR 客户端可能在服务端 Hub 注册完成前发起连接,此时将遇到 HttpRequestException。重试逻辑通过循环捕获异常并等待固定时长,逐步逼近服务就绪时刻,最终建立稳定连接。若 10 次尝试均失败,系统抛出异常至上层,触发全局异常处理器的错误提示。这一设计的容错性体现在对时序不确定性的包容:即使后端服务因首次数据库迁移或插件加载延迟而启动缓慢,桌面应用仍可通过重试机制自动恢复连接,无需用户干预。

窗口位置的恢复逻辑通过 `WindowPositionStore` 实现(第 59 行),该组件在 SourceInitialized 事件中读取注册表或配置文件存储的坐标值,并应用至窗口的 Left、Top、Width 与 Height 属性。位置存储的时机选择在 Closing 事件而非 Closed 事件,这一细节确保在窗口销毁前完成数据持久化,避免异常退出场景下的状态丢失。系统托盘图标的初始化(`InitializeTrayIcon()`,第 72-82 行)在窗口显示后执行,菜单项的构建逻辑封装于 `BuildTrayMenu()` 方法,包含显示/隐藏、Web 界面启动、点击穿透切换、设置对话框以及退出等功能入口。

## 配置加载与生命周期管理

配置文件的加载由 ASP.NET Core 的 Configuration 系统自动完成,默认读取 appsettings.json 与环境特定的 appsettings.{Environment}.json。系统通过 `builder.Configuration` 访问合并后的配置树,关键配置项包括数据库连接字符串(第 63 行)、监控间隔与关键词(第 14-15 行,appsettings.json)、服务器端口与 Hub 路径(第 20-23 行)以及数据库保留策略(第 25-28 行)。配置的热重载机制未启用,这一选择基于监控服务的特性:运行时修改监控间隔或关键词可能导致数据采集的不连续性,因此系统要求重启服务以应用配置变更,确保时间序列数据的一致性。

生命周期管理的核心在于优雅关闭逻辑的设计。桌面应用的 OnExit 方法(第 43-63 行)与 OnSessionEnding 事件处理器(第 65-70 行)均调用 `StopBackendServer()` 与 `StopWebServer()`,确保在应用正常退出或系统关机场景下,子进程与后台任务均能完成清理。后端服务进程的终止使用 `Kill(entireProcessTree: true)`(第 350 行),该调用确保 `dotnet run` 启动的子进程树被完整终止,避免孤儿进程残留。随后的 `WaitForExit(5000)` 设定了 5 秒的最大等待时长,超时后强制返回,这一策略平衡了优雅性与响应性——在进程卡死场景下,用户的关闭操作不会无限期挂起。

Web 服务器的停止通过 CancellationTokenSource 的取消传播实现(第 549 行),该 Token 在 Kestrel 的 RunAsync 方法中被监听,取消信号触发后,服务器将拒绝新连接并等待现有请求完成后退出。Task.Wait 的 5 秒超时与后端服务的等待策略保持一致,形成统一的关闭时限。这一设计确保在极端场景下,桌面应用的关闭操作能够在可接受的时间窗口(约 10 秒)内完成,避免用户感知到的"卡死"现象。数据库连接的释放由 Entity Framework Core 的 Dispose 机制自动处理,DbContext 实例在作用域结束时触发连接池归还逻辑,无需显式调用 Close 方法。

## 启动流程的容错与诊断

系统在启动流程的关键节点均嵌入了容错逻辑与诊断输出。后端服务的日志配置采用结构化日志框架 Serilog,所有启动阶段的操作均通过 `Log.Information` 记录至文件与控制台(如第 56、141、143、163-165 行)。致命错误通过 `Log.Fatal` 记录并重新抛出异常(第 167-170 行),这一模式确保启动失败的根因能够被完整捕获,而非被全局异常处理器吞没。日志输出模板包含时间戳、日志级别、源上下文与消息内容,其中源上下文字段(`{SourceContext}`)由 Serilog 自动填充为日志记录器的类型名称,便于在多组件系统中定位日志来源。

桌面应用的错误提示通过 MessageBox 呈现,关键场景包括后端服务启动失败(第 234-239、255-262、311-318 行)与 Web 界面打开失败(第 152-156 行)。提示文本包含具体的错误原因(`ex.Message`)与补救措施,如"请先运行根目录的'启动服务.bat'"或"请手动访问 http://localhost:35180",降低了用户的故障排查难度。端口占用检测(`IsPortInUse()`,第 368-381 行)通过尝试绑定 TcpListener 实现,该方法在检测到端口已占用时返回 true,避免重复启动服务。这一检测逻辑在开发模式下尤为重要,防止多次调试启动导致的端口冲突与资源泄漏。

从整体视角审视,XhMonitor 的启动流程体现了分层解耦、异步并行与容错优先的设计理念。双进程架构通过进程隔离实现了 UI 与监控逻辑的独立演进,依赖注入容器的构建序列确保了组件初始化的可预测性,三阶段启动模式则为后台服务的复杂初始化提供了清晰的执行结构。配置加载的自动化与数据库迁移的自主执行降低了部署复杂度,而遍布关键路径的重试机制与错误提示则为系统在不确定环境下的可靠运行提供了保障。这一启动架构为 XhMonitor 的长期运行稳定性与可维护性奠定了坚实基础。
