# 逻辑视点与分层架构

## 架构概述

xhMonitor 系统采用经典的三层架构模式，整体设计围绕关注点分离与依赖倒置原则展开。从宏观视角审视，系统可划分为领域核心层（Core）、服务后端层（Service）、桌面表示层（Desktop）三个主要层次，各层职责明确，边界清晰。这一架构选择源于对可测试性、可维护性与平台解耦的追求，使得业务逻辑得以独立于具体的数据访问技术和用户界面框架而存在。

领域核心层作为系统的业务逻辑承载者，封装了进程监控领域的核心概念与规则。该层通过接口抽象定义契约，将具体实现延迟至外层，体现了依赖倒置原则的应用。值得注意的是，Core 层虽为最底层，却不依赖任何外部项目引用，仅引入 EntityFrameworkCore（用于实体标注）、System.Diagnostics.PerformanceCounter 与 System.Management 等基础设施库。这一设计决策确保了领域模型的纯粹性，使得核心业务逻辑可脱离具体持久化机制与 UI 框架进行单元测试。

服务后端层位于系统的中间位置，承担数据访问、API 暴露、实时通信与后台任务调度等职责。该层基于 ASP.NET Core Web API 框架构建，通过项目引用的方式依赖 Core 层，实现了领域接口的具体化。Service 层引入了 SignalR 用于实时双向通信、Entity Framework Core SQLite 用于数据持久化、Serilog 用于结构化日志记录，以及 Windows Services 托管支持。从依赖方向分析，Service 层单向依赖 Core 层，获取领域模型与接口定义，但 Core 层对 Service 层的实现细节完全无感知，这一架构特征确保了业务逻辑的稳定性与可替换性。

桌面表示层作为用户交互的唯一入口，承担界面渲染、用户输入处理、数据展示等职责。该层采用 WPF（Windows Presentation Foundation）技术栈，遵循 MVVM（Model-View-ViewModel）设计模式组织代码。特别值得关注的是，Desktop 层并未通过项目引用直接依赖 Core 层，而是通过 HTTP REST API 与 SignalR 协议与 Service 层进行远程通信。这一设计使得桌面客户端与后端服务实现了物理解耦，为未来支持多客户端（如 Web 前端、移动端）或分布式部署奠定了基础。Desktop 层仅引入 SignalR.Client 与 System.Text.Json 用于网络通信与序列化，保持了表示层的轻量化。

## 职责分配体系

系统的职责分配遵循单一职责原则与分层架构的本质特征，每一层仅关注其抽象级别对应的问题域。领域核心层的职责聚焦于业务概念的建模与业务规则的封装，具体体现为领域模型（Models）、实体定义（Entities）、接口契约（Interfaces）、枚举类型（Enums）以及指标提供者（Providers）的设计。以 `ProcessMetrics` 类为例，该领域模型封装了进程信息（ProcessInfo）、指标字典（Metrics）与时间戳（Timestamp），通过不可变属性（init-only setters）与必需属性标注（required）确保了对象的完整性与一致性。接口 `IProcessMetricRepository` 定义了数据持久化的抽象契约，其方法签名 `SaveMetricsAsync` 接受领域模型集合，而非数据库实体，体现了领域层对基础设施的隔离。

指标提供者体系是 Core 层职责分配的典型案例。`IMetricProvider` 接口定义了指标采集的统一契约，而 `CpuMetricProvider`、`MemoryMetricProvider` 等具体实现封装了不同类型的性能计数器访问逻辑。这些提供者直接调用 Windows 平台特定的 API（如 PerformanceCounter），但其接口定义保持平台无关性。`CpuMetricProvider` 内部采用批量处理与缓存机制优化性能计数器访问，通过 `Dictionary<int, PerformanceCounter>` 维护进程级别的计数器实例，避免重复创建开销。这一实现细节封装于 Core 层，对外仅暴露 `IMetricProvider.CollectAsync` 方法，使得上层调用者无需关心底层性能优化策略。

服务后端层的职责围绕基础设施与应用服务展开，可进一步细分为数据访问子层、业务编排子层、API 暴露子层、实时通信子层与后台任务子层。数据访问子层通过 `MetricRepository` 类实现 `IProcessMetricRepository` 接口，承担领域模型到数据库实体的映射职责。具体实现中，`MapToEntity` 私有方法将 `ProcessMetrics` 领域模型转换为 `ProcessMetricRecord` 实体，通过 JSON 序列化将灵活的指标字典存储为 TEXT 列，体现了对象关系映射的适配器模式应用。该层使用 `IDbContextFactory<MetricsDbContext>` 而非直接注入 `DbContext`，确保了多线程环境下的线程安全性，这一设计选择源于后台服务与 API 请求并发访问数据库的现实需求。

业务编排子层以 `PerformanceMonitor` 类为核心，协调进程扫描器（ProcessScanner）与指标提供者注册表（MetricProviderRegistry）完成指标采集流程。其 `CollectAllAsync` 方法采用并行处理策略，通过 `Parallel.ForEachAsync` 并发调用多个 `IMetricProvider` 实例，使用 `SemaphoreSlim` 控制并发度，在性能与资源消耗间取得平衡。该方法返回 `List<ProcessMetrics>` 领域模型集合，而非数据库实体或 DTO 对象，保持了业务逻辑与数据访问技术的解耦。`ProcessScanner` 负责根据关键词匹配规则扫描系统进程，内部通过 P/Invoke 调用 Windows API 读取进程命令行，同样采用并行处理提升扫描效率。这两个组件的职责边界清晰：Scanner 负责"发现哪些进程需要监控"，Monitor 负责"如何采集这些进程的指标"，二者通过依赖注入组合而非继承关联。

API 暴露子层通过 `MetricsController` 提供 RESTful 接口，暴露 `/metrics/latest`、`/metrics/history`、`/metrics/processes`、`/metrics/aggregations` 等端点。Controller 层直接使用 Entity Framework Core 进行数据库查询，而非通过 Repository 抽象，这一设计决策体现了 CQRS（命令查询职责分离）理念的简化应用：写操作通过 Repository 保持领域模型纯粹性，读操作通过 LINQ 直接查询优化性能。实时通信子层以 `MetricsHub` SignalR Hub 为载体，提供服务端推送能力。Hub 本身逻辑极简，主要处理连接生命周期，实际的消息推送由 `MetricsCollectionWorker` 后台服务触发，体现了关注点分离原则。

后台任务子层包含 `MetricsCollectionWorker` 与 `AggregationWorker` 两个继承自 `BackgroundService` 的长时运行服务。`MetricsCollectionWorker` 周期性调用 `PerformanceMonitor` 采集指标，并通过 `IHubContext<MetricsHub>` 向所有连接客户端广播数据。`AggregationWorker` 实现多层次时序数据聚合逻辑，将原始采集数据（Raw）按分钟、小时、天三个粒度进行汇总，采用水位线（Watermark）机制实现增量处理，避免重复计算。该 Worker 通过独立的 `IDbContextFactory` 实例访问数据库，与 API 请求的 DbContext 实例隔离，确保后台长时事务不阻塞在线查询。这一架构选择使得系统在处理大量历史数据聚合时，仍能保持 API 响应的低延迟。

桌面表示层的职责分配严格遵循 MVVM 模式，可分为视图层（Views）、视图模型层（ViewModels）与服务层（Services）。视图层使用 XAML 声明式定义 UI 结构，通过数据绑定（DataBinding）与视图模型交互，不包含任何业务逻辑。视图模型层以 `FloatingWindowViewModel` 为代表，实现 `INotifyPropertyChanged` 接口，管理 UI 状态与数据同步逻辑。该类通过依赖注入获取 `SignalRService` 实例，订阅其 `MetricsReceived` 与 `ProcessDataReceived` 事件，在事件处理器中更新可绑定属性。特别值得注意的是，ViewModel 使用 `System.Windows.Application.Current?.Dispatcher.Invoke` 将 SignalR 回调线程的数据更新编排至 UI 线程，确保了跨线程 UI 更新的线程安全性，这是 WPF 技术栈的特定约束。

服务层的 `SignalRService` 封装 SignalR 客户端连接管理逻辑，提供强类型的事件委托（`Action<MetricsDataDto>`、`Action<ProcessDataDto>`）供上层订阅。该服务实现自动重连机制，通过 `HubConnection.Closed` 事件处理器在连接断开时尝试重新建立连接，提升了客户端的健壮性。服务层还包含 `AppConfigService` 等配置管理组件，负责应用设置的持久化与加载。这些服务通过依赖注入容器注册为单例，在整个应用生命周期内保持状态一致性。Desktop 层的职责边界明确：Views 仅负责渲染与用户交互捕获，ViewModels 负责 UI 状态管理与业务逻辑协调，Services 负责基础设施关注点（网络通信、配置存储），三者通过接口与事件解耦，便于独立测试与替换。

## 数据流向与约束

系统的数据流动呈现清晰的单向流特征，从底层操作系统指标采集，经过领域模型封装、持久化存储、API 暴露，最终到达桌面客户端展示，每一环节均施加了明确的数据约束与转换规则。数据流的起点位于 Core 层的指标提供者组件，`CpuMetricProvider` 等实现类通过 Windows Performance Counter API 读取系统级别的原始性能数据。这些原始数据以 `float` 或 `long` 类型返回，提供者将其封装为 `MetricValue` 值对象，该值对象包含 `Value`、`Unit`、`Timestamp` 三个维度，并支持通过 `MetricValue.Error()` 静态方法表示采集失败状态。这一封装策略使得上层调用者能够统一处理正常值与异常情况，无需通过异常机制传递采集失败信息，降低了控制流的复杂度。

`PerformanceMonitor` 作为编排者，调用 `ProcessScanner.ScanProcesses()` 获取目标进程列表，随后并行调用所有已注册的 `IMetricProvider` 实例为每个进程采集指标。采集结果通过 `ProcessMetrics` 领域模型聚合，其核心结构为 `Dictionary<string, MetricValue>` 指标字典，键为指标标识符（如 "cpu_usage"、"memory_working_set"），值为前述的 `MetricValue` 对象。这一字典结构体现了指标体系的可扩展性设计，新增指标类型无需修改 `ProcessMetrics` 类定义，仅需注册新的 `IMetricProvider` 实现。领域模型 `ProcessMetrics` 携带 `DateTime Timestamp` 属性记录采集时刻，确保时序数据的可追溯性。整个采集流程的输出为 `List<ProcessMetrics>`，该集合随后传递至 `IProcessMetricRepository.SaveMetricsAsync` 方法进行持久化。

跨越领域层到基础设施层的边界时，数据发生了第一次显式转换。`MetricRepository.SaveMetricsAsync` 方法接收 `ProcessMetrics` 领域模型集合与周期时间戳参数，内部调用 `MapToEntity` 私有方法执行对象映射。该映射过程将领域模型的 `Metrics` 字典序列化为 JSON 字符串，存储于 `ProcessMetricRecord` 实体的 `MetricsJson` TEXT 列。这一设计选择牺牲了部分查询灵活性（无法在 SQL 层面直接过滤特定指标），但换取了领域模型的演化自由度——增删指标字段无需执行数据库 Schema 迁移。实体类 `ProcessMetricRecord` 使用 `[Index(nameof(ProcessId), nameof(Timestamp))]` 标注复合索引，优化按进程与时间范围查询的性能。`MetricRepository` 使用 `IDbContextFactory<MetricsDbContext>` 创建短生命周期的 DbContext 实例，通过 `AddRangeAsync` 批量插入，最后调用 `SaveChangesAsync` 提交事务。这一流程施加的约束包括：所有指标必须可 JSON 序列化，时间戳必须为 UTC 时间，进程 ID 必须有效。

数据从数据库流向 API 层时，经历了查询优化与 DTO 转换两个阶段。`MetricsController.GetLatest` 端点通过 LINQ 查询 `ProcessMetricRecords` 表，使用 `OrderByDescending(Timestamp).Take(N)` 获取最新 N 条记录，随后反序列化 `MetricsJson` 列为字典对象。Controller 未直接返回 EF Core 实体，而是构造匿名对象或 DTO（Data Transfer Object），剔除数据库主键等内部字段，仅暴露客户端所需的业务数据。这一转换隐式施加了约束：API 响应的 JSON Schema 与数据库 Schema 解耦，允许二者独立演化。查询历史数据的 `/metrics/history` 端点接受时间范围参数，通过 `Where(m => m.Timestamp >= start && m.Timestamp <= end)` 过滤记录，利用前述的复合索引加速查询。该端点返回的数据按时间戳升序排列，便于客户端绘制时序图表。

实时数据流通过 SignalR 通道从 Service 层推送至 Desktop 层，绕过了传统的请求-响应模式。`MetricsCollectionWorker` 在每个采集周期结束后，调用 `IHubContext<MetricsHub>.Clients.All.SendAsync("ReceiveMetrics", data)` 向所有连接客户端广播数据。广播的数据对象为 `MetricsDataDto`，该 DTO 包含 `TotalCpu`、`TotalMemory`、`ProcessMetrics` 等字段，结构设计贴合客户端 UI 展示需求。SignalR 框架自动执行 JSON 序列化，通过 WebSocket 协议（或降级到 Long Polling）传输至客户端。这一数据流施加的约束包括：DTO 必须可序列化，消息大小应控制在合理范围（避免超过 WebSocket 帧限制），推送频率需平衡实时性与网络开销。

桌面客户端接收实时数据时，经历反序列化、事件传播、UI 线程编排三个环节。`SignalRService` 注册 `HubConnection.On<MetricsDataDto>("ReceiveMetrics", handler)` 回调，在收到服务端消息时触发 `MetricsReceived` 事件。订阅该事件的 `FloatingWindowViewModel` 在事件处理器中更新可绑定属性（如 `TotalCpu`、`Processes` 集合），但由于 SignalR 回调运行在后台线程，直接修改属性会引发 `InvalidOperationException`（跨线程访问 UI 对象）。ViewModel 通过 `Application.Current.Dispatcher.Invoke` 将属性更新操作封送至 UI 线程执行，确保 WPF 数据绑定机制的线程安全性。这一编排过程的约束为：所有 UI 状态修改必须在 UI 线程执行，事件处理器应避免长时阻塞操作（会冻结界面）。

聚合数据流展现了系统对时序数据的多层次处理能力。`AggregationWorker` 从 `ProcessMetricRecords` 表读取原始采集数据，按时间窗口（1 分钟、1 小时、1 天）分组，计算每组内的统计指标（平均值、最大值、最小值），将结果写入 `MinuteAggregations`、`HourAggregations`、`DayAggregations` 聚合表。该流程采用增量处理策略，维护水位线标记（Watermark）记录已聚合的最大时间戳，每次仅处理新增的原始数据。聚合过程施加的约束包括：时间窗口对齐（如小时聚合必须对齐到整点），原始数据不可变（聚合后不删除），聚合结果幂等（重复执行产生相同结果）。这一设计使得系统能够支持历史数据回溯查询，同时通过预聚合降低长时间范围查询的计算开销。

整体而言，数据在系统中的流动遵循"采集 → 封装 → 持久化 → 暴露 → 展示"的单向链路，每一环节的输入输出类型明确，转换规则显式定义。领域模型（ProcessMetrics）作为核心数据载体，在 Core 层与 Service 层间传递，确保业务语义的一致性。数据库实体（ProcessMetricRecord）封装持久化细节，仅在 Repository 边界内可见。DTO 对象（MetricsDataDto）适配网络传输与客户端需求,在 API 与 SignalR 边界转换。这一分层数据模型策略虽引入了对象映射开销，但换取了各层的独立演化能力与清晰的职责边界，体现了架构设计中"显式优于隐式"的哲学。

## 边界隔离策略

系统通过接口抽象、依赖注入、项目引用约束、DTO 转换四重机制实现层间边界的严格隔离,确保高层策略不依赖低层细节,低层实现可独立替换。接口抽象是边界隔离的第一道防线,Core 层定义的 `IMetricProvider`、`IProcessMetricRepository`、`IMetricAction` 等接口充当依赖倒置的契约。以 `IProcessMetricRepository` 为例,其方法签名 `Task SaveMetricsAsync(IReadOnlyCollection<ProcessMetrics> metrics, DateTime cycleTimestamp, CancellationToken cancellationToken)` 仅引用领域模型与基础类型,不包含任何 EF Core 特定类型（如 DbContext、DbSet）。这一设计使得 Core 层的业务逻辑（如 `PerformanceMonitor`）可针对接口编程,无需感知 Service 层是使用 SQLite、SQL Server 还是内存存储实现持久化。接口的返回类型同样保持技术无关性,`IMetricProvider.CollectAsync` 返回领域模型 `ProcessMetrics`,而非数据库实体或 JSON 字符串,确保业务逻辑与序列化技术解耦。

依赖注入容器作为第二道隔离机制,在运行时装配接口与实现的绑定关系。Service 层的 `Program.cs` 通过 `services.AddScoped<IProcessMetricRepository, MetricRepository>()` 注册实现类,使得 `MetricsCollectionWorker` 等消费者可通过构造函数注入 `IProcessMetricRepository` 依赖,而无需使用 `new` 关键字直接实例化 `MetricRepository`。这一机制的价值在于依赖关系的配置化与可测试性提升：单元测试时可注册 Mock 实现替代真实 Repository,避免数据库依赖；切换持久化技术时仅需修改注册代码,消费者代码零改动。值得注意的是,系统使用 `IDbContextFactory<MetricsDbContext>` 而非直接注入 `DbContext`,这是针对多线程环境的边界强化策略——DbContext 非线程安全,通过工厂模式确保每个操作使用独立实例,避免跨线程共享状态导致的并发问题。

项目引用约束构成第三道隔离屏障,通过物理边界强制依赖方向的正确性。.csproj 文件定义的引用关系表明：Service 层引用 Core 层（`<ProjectReference Include="..\XhMonitor.Core\XhMonitor.Core.csproj" />`）,但 Core 层的项目文件不包含任何 ProjectReference 节点,确保核心业务逻辑不依赖基础设施实现。Desktop 层更进一步,完全不引用 Core 层项目,仅通过网络协议与 Service 层通信。这一约束使得编译器成为架构守护者：若开发者尝试在 Core 层直接使用 `MetricsDbContext`,编译时会因类型不可见而失败；若 Desktop 层试图直接实例化 `ProcessMetrics` 领域模型,同样会遭遇编译错误。物理隔离的代价是需要在边界处执行显式的数据转换,但收益是架构腐化的早期发现与依赖关系的可视化验证。

DTO（Data Transfer Object）转换是第四道隔离机制,在网络边界与持久化边界实施数据形态的适配。从领域模型到数据库实体的转换发生在 Repository 层,`MapToEntity` 方法将 `ProcessMetrics` 的 `Metrics` 字典序列化为 JSON 字符串,映射为 `ProcessMetricRecord.MetricsJson` 列。这一转换隔离了领域模型的演化与数据库 Schema 的稳定性：增加新指标类型仅需扩展字典内容,无需 ALTER TABLE。从数据库实体到 API 响应的转换发生在 Controller 层,`GetLatest` 方法将 EF Core 查询结果投影为匿名对象,剔除 `Id`、`CreatedAt` 等内部字段。这一转换隔离了 API 契约与数据库设计：重构表结构无需更新 API 文档,客户端代码无需随之变更。SignalR 推送使用的 `MetricsDataDto` 是专为实时通信设计的 DTO,其字段命名与结构贴合前端组件需求,与后端领域模型保持独立。

边界隔离策略的综合应用体现在跨层交互流程中。当 `MetricsCollectionWorker` 需要保存采集数据时,其代码仅调用 `_repository.SaveMetricsAsync(metrics, timestamp)`,传递的是领域模型集合。Worker 不知道 Repository 内部使用了 EF Core,也不关心数据以何种格式存储。`MetricRepository` 的 `MapToEntity` 方法完成领域模型到实体的转换,`DbContext.SaveChangesAsync` 将实体持久化为 SQLite 数据库文件。整个过程中,Worker（业务逻辑）、Repository（持久化抽象）、DbContext（ORM 框架）、SQLite（数据库引擎）四个层次各司其职,通过接口与 DTO 隔离。若未来需要将 SQLite 替换为 PostgreSQL,仅需实现新的 `MetricsDbContext` 配置,业务逻辑代码无需改动。若需要将 EF Core 替换为 Dapper,仅需重新实现 `MetricRepository`,接口契约保持不变。

Desktop 层与 Service 层的边界隔离更为严格,采用网络协议作为物理隔离手段。桌面应用通过 `HttpClient` 调用 REST API 或通过 `HubConnection` 连接 SignalR,传输的数据格式为 JSON。这一隔离策略使得客户端与服务端可使用不同技术栈：Desktop 当前使用 WPF,未来可替换为 Electron、Flutter 或 Web 前端,只要遵循相同的 HTTP/WebSocket 协议即可。`SignalRService` 封装了 SignalR 客户端的连接管理细节,对外暴露强类型事件（`Action<MetricsDataDto>`）,ViewModel 订阅事件而非直接操作 `HubConnection`。这一封装策略在 SignalR 客户端与业务逻辑间建立了缓冲层,未来若需要支持其他实时协议（如 gRPC Streaming、Server-Sent Events）,仅需替换 `SignalRService` 实现,ViewModel 代码保持稳定。

跨层异常传播同样遵循边界隔离原则。Core 层的 `IMetricProvider` 实现在采集失败时不抛出异常,而是返回 `MetricValue.Error()`,将错误状态编码为领域模型的一部分。这一设计避免了异常穿透多层边界,简化了上层的错误处理逻辑。Service 层的 Repository 在数据库操作失败时抛出 `DbUpdateException`,但该异常被 Worker 的 try-catch 块捕获,转换为结构化日志记录（通过 Serilog）,而非传播至 SignalR 推送逻辑。API Controller 使用 ASP.NET Core 的全局异常过滤器将未处理异常转换为标准 HTTP 错误响应（如 500 Internal Server Error）,避免将栈追踪等内部信息泄露给客户端。Desktop 层的 `SignalRService` 在连接断开时触发 `ConnectionClosed` 事件,由 ViewModel 决定是否显示重连提示,而非直接弹出异常对话框中断用户操作。这一分层错误处理策略确保了异常的本地化处理,每层仅处理其职责范围内的错误,跨层边界传递语义化的错误信号而非技术细节。

## 异常处理流

系统的异常处理策略采用分层捕获、本地恢复、语义转换的设计理念,在确保系统稳定性的同时保持各层边界的清晰性。异常处理流的设计遵循"早检测、早恢复、晚传播"原则,优先在异常发生层进行本地修复,仅在无法恢复时才向上层传播语义化的错误信息。这一策略避免了技术异常穿透业务逻辑层,降低了上层代码的复杂度。

在操作系统指标采集的最底层,`CpuMetricProvider` 等提供者面对的主要异常场景包括性能计数器不存在、进程已退出、权限不足等。传统做法是抛出异常由调用者处理,但这会导致单个进程采集失败拖累整个批次。系统采用的策略是将异常吸收并转换为领域语义：`CollectAsync` 方法内部使用 try-catch 包裹 `PerformanceCounter.NextValue()` 调用,捕获 `InvalidOperationException` 或 `UnauthorizedAccessException` 后,不再抛出,而是返回 `MetricValue.Error(errorMessage)`。这一错误值对象包含 `IsError` 标志与错误描述,使得上层可通过正常的控制流（if 判断）而非异常机制处理采集失败。这种设计体现了"异常用于异常情况,返回值用于预期情况"的原则——进程退出是监控系统的常见场景,不应视为异常流。

`PerformanceMonitor` 在编排多个提供者时,采用并行容错机制处理采集异常。其 `CollectAllAsync` 方法使用 `Parallel.ForEachAsync` 并发调用提供者,每个提供者的异常被独立捕获,不会导致整个并行操作中止。具体实现中,外层的 try-catch 包裹整个并行块,捕获 `AggregateException` 等并行框架抛出的异常；内层的 try-catch 位于 lambda 表达式内,捕获单个提供者的异常。两层捕获确保了局部失败不影响其他提供者的正常执行,同时记录详细的错误日志便于运维排查。值得注意的是,`PerformanceMonitor` 设置了超时保护（通过 `CancellationToken` 与 `Task.WaitAsync`）,防止某个提供者的长时阻塞影响整体采集周期。超时触发时抛出 `TimeoutException`,该异常同样被转换为错误日志而非向上传播。

数据持久化边界的异常处理体现了事务一致性与降级策略的权衡。`MetricRepository.SaveMetricsAsync` 在执行 `DbContext.SaveChangesAsync` 时可能遇到数据库锁定、磁盘空间不足、唯一索引冲突等异常。该方法不捕获这些异常,而是直接向上抛出,因为持久化失败属于基础设施故障,调用者（如 `MetricsCollectionWorker`）需要感知并决定降级策略。Worker 的异常处理逻辑使用 try-catch 包裹 `_repository.SaveMetricsAsync` 调用,捕获 `DbUpdateException` 后记录错误日志,但不中止后台服务——下一个采集周期仍会正常执行。这一设计使得短暂的数据库故障（如瞬时锁等待超时）不会导致监控服务崩溃,体现了"优雅降级"理念。同时,Worker 通过 Serilog 记录详细的异常上下文（包括采集周期时间戳、进程数量）,便于事后分析数据丢失的影响范围。

API 层的异常处理采用集中式策略,通过 ASP.NET Core 的异常中间件统一捕获未处理异常。`Program.cs` 配置的 `app.UseExceptionHandler` 中间件拦截 Controller 抛出的所有异常,根据异常类型返回相应的 HTTP 状态码与标准错误响应。例如,`ArgumentException` 转换为 400 Bad Request,`NotFoundException` 转换为 404 Not Found,未知异常转换为 500 Internal Server Error。错误响应体包含错误码、用户友好的错误消息、请求跟踪 ID,但不包含栈追踪等敏感信息,避免泄露系统内部细节。这一机制确保了 API 契约的一致性,客户端可依赖标准的 HTTP 语义处理错误,无需解析特定格式的异常消息。同时,中间件将完整的异常信息记录到 Serilog,包括请求路径、查询参数、用户标识等上下文,便于后续排查。

SignalR 实时推送的异常处理面临特殊挑战,因为 SignalR 采用异步推送模型,服务端无法直接获知客户端的消息处理结果。`MetricsCollectionWorker` 在调用 `_hubContext.Clients.All.SendAsync` 推送数据时,该方法返回的 Task 仅表示消息已提交到 SignalR 框架,而非客户端已接收。若客户端连接断开或处理消息时抛出异常,服务端无法感知。系统采用的策略是在推送逻辑外包裹 try-catch,捕获 SignalR 框架层面的异常（如序列化失败、连接池耗尽）,但不尝试处理客户端侧的异常。这一选择源于分布式系统的"最终一致性"理念：服务端确保消息可靠推送,客户端负责自身的异常恢复,双方通过心跳与重连机制维持同步。

桌面客户端的异常处理分为网络层、服务层、UI 层三个层次。网络层的 `SignalRService` 处理连接异常,通过订阅 `HubConnection.Closed` 事件感知连接断开,自动触发重连逻辑。重连采用指数退避策略（首次延迟 2 秒,后续延迟翻倍,最大 60 秒）,避免在服务端故障时产生过度重试压力。连接异常不直接显示给用户,而是触发 `ConnectionStateChanged` 事件,由 ViewModel 决定是否更新 UI 状态（如显示"连接中断"提示）。服务层异常（如 HTTP 请求失败）被 ViewModel 捕获,转换为用户友好的错误消息通过 MessageBox 或通知栏展示。UI 层异常（如数据绑定错误、控件初始化失败）由 WPF 的 `Application.DispatcherUnhandledException` 全局处理器捕获,记录日志并显示通用错误对话框,避免应用崩溃。

跨层异常传播的语义转换是异常处理流的关键设计。Core 层的技术异常（如 `PerformanceCounterException`）在 Provider 边界被转换为领域错误（`MetricValue.Error`）,避免技术细节泄露到业务逻辑层。Service 层的数据库异常（如 `DbUpdateException`）在 Repository 边界向上抛出,但被 Worker 转换为结构化日志,避免持久化失败中断业务流程。API 层的所有异常在中间件边界被转换为标准 HTTP 错误响应,避免内部实现细节暴露给客户端。Desktop 层的网络异常在 Service 边界被转换为状态事件,避免底层协议细节影响 UI 逻辑。这一层层转换确保了每层仅处理其抽象级别对应的错误语义,维持了分层架构的清晰性。

系统还实施了异常预防机制,通过设计减少异常发生的概率。输入验证在 API 边界执行,使用 FluentValidation 等库在数据进入业务逻辑前拦截非法输入。并发控制通过 `SemaphoreSlim` 限制并行度,避免资源耗尽异常。超时保护通过 `CancellationToken` 实现,防止长时操作阻塞关键路径。资源管理通过 `IDbContextFactory` 与 `IDisposable` 模式确保连接及时释放,避免泄漏。健康检查通过 ASP.NET Core Health Checks 监控数据库连接、磁盘空间等依赖项,在故障发生前触发告警。这些预防措施与异常处理机制共同构成了系统的防御纵深体系,确保了在复杂运行环境下的高可用性。

综合来看,xhMonitor 的异常处理流体现了"局部失败局部恢复"的分布式系统设计理念。各层定义清晰的异常边界,优先在本地吸收并转换异常,仅在无法恢复时才向上传播语义化的错误信号。这一策略在保证系统整体稳定性的同时,维持了分层架构的边界清晰性,为长期演化奠定了坚实基础。
