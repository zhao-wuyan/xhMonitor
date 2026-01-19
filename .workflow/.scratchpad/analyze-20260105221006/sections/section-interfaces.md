# 接口契约与API设计

## 核心接口架构

xhMonitor系统采用基于接口契约的插件化架构，通过抽象接口定义系统扩展点，实现了指标采集、数据持久化、自定义动作等核心功能的解耦与可扩展性。接口设计遵循依赖倒置原则，上层模块依赖抽象而非具体实现，使得系统在保持稳定契约的前提下支持灵活的功能扩展。

从接口层次划分，系统定义了三层契约体系：**指标提供者接口**作为数据采集层的抽象，**数据仓库接口**封装持久化逻辑，**动作执行接口**则支持用户交互触发的自定义操作。这三层接口共同构成了系统的核心契约边界，为插件化扩展提供了清晰的规范约束。

## 指标提供者接口契约

### 接口定义与职责

`IMetricProvider`接口是系统指标采集层的核心抽象，位于`XhMonitor.Core/Interfaces/IMetricProvider.cs:9`。该接口定义了指标插件必须实现的六项契约方法，涵盖元数据声明、数据采集、平台兼容性检测等职责。接口继承自`IDisposable`，要求实现类严格管理资源生命周期，避免内存泄漏或句柄泄漏。

接口签名采用只读属性与异步方法相结合的设计。四个只读属性（`MetricId`、`DisplayName`、`Unit`、`Type`）确保指标元数据在运行时不可变，防止插件实例在注册后修改标识符引发注册表混乱。两个异步方法（`CollectAsync`、`GetSystemTotalAsync`）采用`Task<T>`返回类型，支持I/O密集型采集操作（如查询WMI、读取硬件传感器）在线程池上异步执行，避免阻塞主线程。

```csharp
// XhMonitor.Core/Interfaces/IMetricProvider.cs:9
public interface IMetricProvider : IDisposable
{
    string MetricId { get; }           // 唯一标识，如 "cpu", "memory", "custom_xxx"
    string DisplayName { get; }        // 用户可读名称，如 "CPU使用率"
    string Unit { get; }               // 单位符号，如 "%", "MB", "°C"
    MetricType Type { get; }           // 指标类型枚举：Percentage | Capacity | Custom

    Task<MetricValue> CollectAsync(int processId);  // 采集指定进程的指标值
    bool IsSupported();                             // 检测当前平台是否支持该指标
    Task<double> GetSystemTotalAsync();             // 获取系统总量或总使用率
}
```

### 前置条件与不变式

接口契约对实现类施加了以下约束条件：

**前置条件（Preconditions）**：
`CollectAsync`方法要求传入有效的进程ID（大于0），调用方必须确保进程存在且未终止。若进程已退出，实现类应返回`MetricValue.Error()`而非抛出异常，保持契约的健壮性。`IsSupported`方法必须在对象构造后立即可调用，不依赖外部资源初始化，用于注册表在注册阶段快速淘汰不兼容插件。

**不变式（Invariants）**：
`MetricId`必须在整个对象生命周期内保持唯一性与不变性，注册表依赖该属性作为字典键实现O(1)查找。`Type`枚举值决定了`GetSystemTotalAsync`的语义：当类型为`Percentage`时返回系统总使用率（如CPU总使用率），类型为`Capacity`时返回系统总容量（如总内存大小），实现类不得违反此语义约定。

**后置条件（Postconditions）**：
`CollectAsync`返回的`MetricValue`对象必须包含有效的时间戳，且`IsError`与`ErrorMessage`状态保持一致（若`IsError=true`则`ErrorMessage`非空）。`Dispose`方法调用后，对象进入终止状态，后续对任何方法的调用应抛出`ObjectDisposedException`，防止资源已释放的对象被误用。

### 内置实现示例

系统内置四个实现类（`CpuMetricProvider`、`MemoryMetricProvider`、`GpuMetricProvider`、`VramMetricProvider`），分别采集CPU、内存、GPU、显存指标。这些实现类在`MetricProviderRegistry:131-134`注册阶段自动加载，无需外部配置。

以`CpuMetricProvider`为例，其`CollectAsync`实现通过`Process.GetProcessById(processId).TotalProcessorTime`获取进程CPU时间片，结合两次采样的时间差计算使用率。`IsSupported`方法在Windows平台返回`true`，Linux平台需进一步检测`/proc`文件系统可访问性。`GetSystemTotalAsync`通过查询`PerformanceCounter`全局CPU计数器返回系统总使用率，该方法结果缓存5秒避免频繁查询降低性能。

## 数据仓库接口契约

### 接口定义与异步批量操作

`IProcessMetricRepository`接口封装了指标数据持久化逻辑，位于`XhMonitor.Core/Interfaces/IProcessMetricRepository.cs:5`。接口设计采用批量操作模式，单次调用支持写入多个进程的指标记录，减少数据库往返次数提升吞吐量。

```csharp
// XhMonitor.Core/Interfaces/IProcessMetricRepository.cs:7
Task SaveMetricsAsync(
    IReadOnlyCollection<ProcessMetrics> metrics,
    DateTime cycleTimestamp,
    CancellationToken cancellationToken = default);
```

方法签名接受只读集合参数`IReadOnlyCollection<ProcessMetrics>`，防止仓库实现意外修改传入数据引发调用方状态污染。`cycleTimestamp`参数标记本次采集周期的统一时间戳，确保批量记录的时间一致性，避免单条记录各自取当前时间导致时序分析出现偏差。`CancellationToken`参数支持长时间运行的批量写入操作响应外部取消请求，配合后台Worker优雅关闭。

### 事务语义与错误处理

接口契约隐式要求实现类提供原子性保证：要么全部记录成功写入，要么全部回滚，不得出现部分写入状态。当前默认实现`MetricRepository`通过Entity Framework Core的`DbContext`事务机制实现该语义，单个`SaveChangesAsync`调用包裹所有记录的插入操作。

若数据库连接失败或磁盘空间不足导致写入失败，实现类应抛出具体异常（如`DbUpdateException`）而非吞噬错误。调用方（Worker后台服务）捕获异常后记录日志，并根据重试策略决定是否放弃本次数据或稍后重试。接口未定义返回值，意味着成功写入无需返回确认，失败通过异常传播，符合.NET异步编程最佳实践。

### 依赖注入与工厂模式

系统在`Program.cs:76`通过依赖注入容器注册仓库实例：

```csharp
builder.Services.AddSingleton<IProcessMetricRepository, MetricRepository>();
```

值得注意的是，`MetricRepository`实现类依赖`IDbContextFactory<MonitorDbContext>`而非直接注入`DbContext`。工厂模式确保每次数据库操作创建独立的上下文实例，避免长生命周期Singleton服务持有短生命周期Scoped上下文导致的并发冲突。该设计使得多个Worker线程可安全地并发调用`SaveMetricsAsync`，每个调用通过工厂获取独立上下文实例，互不干扰。

## 动作执行接口契约

### 接口定义与插件化扩展

`IMetricAction`接口定义了用户点击悬浮窗指标时触发的自定义操作，位于`XhMonitor.Core/Interfaces/IMetricAction.cs:8`。该接口支持插件化扩展，第三方开发者可实现自定义动作（如清理内存、重启进程、调节性能模式）并通过插件目录动态加载。

```csharp
// XhMonitor.Core/Interfaces/IMetricAction.cs:8
public interface IMetricAction
{
    string ActionId { get; }        // 动作唯一标识，如 "clear_memory", "restart_process"
    string DisplayName { get; }     // 用户可读名称，如 "清理内存"
    string Icon { get; }            // 图标名称，用于UI显示

    Task<ActionResult> ExecuteAsync(int processId, string metricId);
}
```

接口采用与`IMetricProvider`相似的元数据声明模式，通过只读属性暴露标识符与显示信息。`ExecuteAsync`方法接受进程ID与指标ID双参数，允许动作根据触发来源做差异化处理（如点击CPU指标与点击内存指标触发同一动作时执行不同逻辑）。

### 返回契约与错误封装

方法返回`ActionResult`对象而非直接抛出异常，采用结果对象模式（Result Pattern）封装执行状态。`ActionResult`包含四个字段：`Success`标识执行成功或失败，`Message`承载用户友好的结果描述，`ElapsedMilliseconds`记录执行耗时用于性能监控，`Data`字典存储附加元数据（如重启后的新进程ID）。

该设计避免了异常用于流程控制的反模式，使得调用方无需捕获异常即可判断执行结果。失败场景（如进程不存在、权限不足）通过`ActionResult.Fail(message)`返回，成功场景通过`ActionResult.Ok(message)`返回，调用方根据`Success`字段决定UI反馈策略（如显示成功提示或错误对话框）。

### 异步执行与超时控制

接口约定`ExecuteAsync`为异步方法，支持耗时操作（如等待进程退出、调用外部工具）在后台线程执行。实现类应遵循超时约定：单次动作执行时长不应超过30秒，若操作可能长时间阻塞（如等待用户确认），应通过`CancellationToken`支持提前取消。

当前系统未在接口签名中强制要求`CancellationToken`参数，但建议实现类内部实现超时机制，避免UI线程因等待动作完成而冻结。未来版本可能将`CancellationToken`纳入契约，确保所有实现类统一支持取消操作。

## RESTful API设计规范

### 统一资源路由模式

系统RESTful API采用语义化路由设计，所有控制器路由前缀统一为`api/v1/[controller]`，体现了API版本化策略。控制器名称自动映射为资源路径（如`MetricsController`对应`/api/v1/metrics`），符合ASP.NET Core约定路由规范。

三个主要API端点及其职责如下：

**指标查询API**（`/api/v1/metrics`）：提供原始指标记录与聚合数据的查询能力。`GET /metrics/latest`返回最新一轮采集的所有进程记录，支持通过`processId`、`processName`、`keyword`查询参数过滤结果。`GET /metrics/history`返回指定进程的历史时序数据，支持`aggregation`参数选择原始数据（`raw`）或聚合粒度（`minute`/`hour`/`day`），时间范围通过`from`与`to`参数控制。

**配置管理API**（`/api/v1/config`）：暴露系统配置的读写接口。`GET /config`返回当前监控配置（采集间隔、关键词过滤），`GET /config/alerts`查询告警阈值配置，`POST /config/alerts`创建或更新告警规则，`DELETE /config/alerts/{id}`删除指定告警。`GET /config/settings`与`PUT /config/settings`提供应用设置的批量读写，支持按分类分组的嵌套JSON结构。

**悬浮窗配置API**（`/api/v1/widgetconfig`）：管理桌面悬浮窗的交互配置。`GET /widgetconfig`读取全局设置与各指标点击动作配置，`POST /widgetconfig`批量更新配置，`POST /widgetconfig/{metricId}`更新单个指标的点击行为。配置持久化至文件系统（`data/widget-settings.json`）而非数据库，确保轻量级客户端无需访问数据库即可读取配置。

### HTTP动词语义化使用

API设计严格遵循HTTP动词语义约定：`GET`用于幂等查询，`POST`用于创建资源或触发非幂等操作，`PUT`用于完整替换资源状态，`DELETE`用于资源删除。值得注意的是，系统未使用`PATCH`进行部分更新，而是通过`PUT`方法接受部分字段（如`ConfigController:200`的`PUT /config/settings/{category}/{key}`）实现单字段更新，该设计简化了客户端逻辑，避免JSON Patch格式的复杂性。

响应状态码遵循RESTful最佳实践：成功查询返回`200 OK`，资源创建返回`200 OK`（而非`201 Created`，简化客户端处理），资源不存在返回`404 Not Found`，服务端错误返回`500 Internal Server Error`，健康检查失败返回`503 Service Unavailable`。所有错误响应均包含`{ success: false, error: "..." }`结构体，提供用户友好的错误描述。

### 查询参数设计与分页策略

查询接口广泛使用可选查询参数实现灵活过滤，所有参数均通过`[FromQuery]`特性绑定，支持URL拼接传参。以`MetricsController:24`的`GetLatest`方法为例，三个可选参数（`processId`、`processName`、`keyword`）采用`int?`与`string?`可空类型，允许调用方省略不需要的过滤条件。

系统当前未实现全局分页机制，大多数查询接口返回完整结果集。这一设计源于指标数据的时间窗口特性：`GET /metrics/latest`仅返回最新一轮采集（通常几十条记录），`GET /metrics/history`通过时间范围参数（`from`/`to`）天然限制结果集大小。未来若单次查询记录数超过千条，建议引入`limit`与`offset`参数实现基于游标的分页。

## SignalR实时推送契约

### Hub接口与事件订阅

系统通过SignalR Hub提供实时指标推送能力，客户端连接至`/hubs/metrics`端点后自动订阅四类事件：`hardware.limits`（硬件最大容量），`system.usage`（系统总使用率），`process.metrics`（进程详细指标），`metrics.latest`（最新采集数据）。

`MetricsHub`类位于`XhMonitor.Service/Hubs/MetricsHub.cs:5`，继承自`Hub`基类，仅重写连接与断开事件用于日志记录，未定义客户端调用的服务端方法（Server Methods）。该设计体现了单向推送模式：服务端主动推送数据至所有连接客户端，客户端仅消费数据无需回调。

客户端订阅事件的协议由`SignalRService:46-83`定义，使用`connection.On<JsonElement>(eventName, handler)`模式注册四个事件监听器。事件载荷采用JSON序列化的DTO对象（如`HardwareLimitsDto`、`SystemUsageDto`），属性名通过`JsonPropertyName`特性映射为小驼峰格式（如`maxMemory`），符合JavaScript命名习惯。

### 自动重连与连接状态管理

客户端连接配置（`SignalRService:39-42`）启用自动重连机制（`WithAutomaticReconnect()`），当网络中断或服务端重启时，SignalR客户端库自动执行指数退避重连策略（首次1秒，后续2秒、4秒、8秒，最大32秒）。重连成功后自动重新订阅事件，无需手动重新绑定监听器。

连接状态通过三个事件暴露给上层应用：`Reconnecting`触发时通知UI显示断线状态，`Reconnected`触发时恢复在线状态，`Closed`触发时标记连接彻底失败（如服务端下线超过重连超时）。这三个事件均通过`ConnectionStateChanged`委托统一对外通知，简化了UI层的状态订阅逻辑。

### 跨域配置与安全策略

服务端CORS策略（`Program.cs:115-124`）限制允许的源地址为开发环境常用端口（`localhost:3000`、`localhost:5173`、`localhost:35180`）与Electron应用协议（`app://.`），拒绝任意域的跨域请求。该配置在生产环境部署时需调整为实际Web前端域名，避免安全风险。

SignalR Hub路径（`/hubs/metrics`）通过配置文件`Server:HubPath`可自定义，但客户端连接URL必须与之匹配。当前客户端硬编码Hub地址为`http://localhost:35179/hubs/metrics`（`SignalRService:12`），生产环境部署需改为从配置文件读取，支持多环境切换。

## 接口版本演化策略

### URL路径版本控制

系统采用URL路径版本控制策略（`/api/v1/[controller]`），通过路由前缀`v1`标识当前API版本。该策略的优势在于版本显式可见，客户端可同时访问不同版本API实现灰度迁移，新版本接口与旧版本接口通过不同路由前缀隔离，避免破坏性变更影响现有客户端。

未来引入`v2`版本时，系统可保留`v1`控制器继续提供旧版本服务，新客户端通过`/api/v2/[controller]`访问新接口。版本间的数据模型差异通过独立DTO类隔离（如`MetricsV1Dto`与`MetricsV2Dto`），避免单一DTO类的字段污染。当`v1`版本用户量降至阈值以下时，通过HTTP 410 Gone状态码标记废弃，引导客户端升级至新版本。

### 接口契约向后兼容原则

所有已发布的接口契约变更必须保持向后兼容，具体约束如下：

**添加性变更**（允许）：新增查询参数、响应字段、枚举值、可选请求属性。例如，`GET /metrics/latest`可新增`sortBy`查询参数实现排序，现有客户端省略该参数时保持原有行为。响应DTO新增字段（如`MetricsDataDto`新增`serverTimestamp`）不破坏现有客户端的反序列化逻辑。

**修改性变更**（禁止）：修改已有字段类型（如`processId`从`int`改为`string`）、删除响应字段、修改HTTP状态码语义、调整查询参数必选性（从可选改为必填）。此类变更必须通过新版本API实现，如`/api/v2/metrics/latest`采用新数据模型。

**移除性变更**（需废弃流程）：删除端点、参数或字段需提前至少两个版本周期标记为废弃（Deprecated），通过响应头`Deprecated: true`与文档告警通知客户端。废弃期间接口仍正常工作，但日志记录调用方信息用于统计迁移进度。

### 数据传输对象（DTO）演化

系统通过独立DTO类隔离API契约与内部领域模型，避免内部重构影响外部接口。例如，`MetricsDataDto`（`XhMonitor.Desktop/Models/MetricsDataDto.cs:5`）与内部`ProcessMetrics`模型（`XhMonitor.Core/Models/ProcessMetrics.cs:3`）字段结构不同，前者采用嵌套结构（`systemStats`子对象），后者采用扁平字典（`Metrics`字典）。

DTO属性通过`JsonPropertyName`特性显式映射JSON字段名（如`[JsonPropertyName("processCount")]`），确保序列化格式不受C#命名约定变更影响。当内部模型重构（如`ProcessMetrics`字段重命名）时，仅需调整映射逻辑，DTO类保持不变，外部API契约不受影响。

未来若需支持多种数据格式（如XML、Protocol Buffers），可通过内容协商（Content Negotiation）机制根据`Accept`请求头返回不同格式，DTO类作为中间层统一承载数据，序列化器根据格式差异生成不同输出。

## 契约测试与验证策略

### 接口契约单元测试

每个接口实现类应配套契约单元测试，验证前置条件、后置条件、不变式是否满足。以`IMetricProvider`为例，测试用例应覆盖以下场景：

**元数据不变性测试**：多次调用`MetricId`属性返回相同值，`Type`枚举值在对象生命周期内不变。
**平台兼容性测试**：在不支持的平台（如Linux上的GPU指标）调用`IsSupported()`返回`false`，注册表正确跳过该插件。
**错误处理测试**：对已退出的进程调用`CollectAsync`返回`MetricValue`对象且`IsError=true`，不抛出异常。
**资源释放测试**：调用`Dispose()`后再调用`CollectAsync`抛出`ObjectDisposedException`。

### API集成测试与契约验证

RESTful API应通过集成测试验证HTTP契约，使用`WebApplicationFactory`启动内存中的测试服务器，发送真实HTTP请求验证响应状态码、响应体结构、错误处理逻辑。

以`GET /api/v1/metrics/latest`为例，集成测试应验证：
**正常场景**：无查询参数时返回200状态码，响应体为JSON数组，每个元素包含必填字段（`processId`、`timestamp`）。
**过滤场景**：传入`processName=chrome`时仅返回匹配记录，空结果返回空数组而非404。
**错误场景**：传入无效参数（如`processId=-1`）返回400 Bad Request，响应体包含错误描述。

### SignalR契约测试

SignalR推送契约通过客户端集成测试验证，启动测试服务器后建立SignalR连接，订阅事件并验证推送消息的结构与频率。测试应覆盖：

**事件载荷验证**：订阅`hardware.limits`事件后收到的消息可反序列化为`HardwareLimitsDto`，所有必填字段非空。
**重连机制验证**：中断网络连接后触发`Reconnecting`事件，恢复连接后触发`Reconnected`事件，事件订阅保持有效。
**并发推送验证**：多个客户端同时连接时，所有客户端均收到推送消息，消息顺序与服务端发送顺序一致。

## 接口文档生成与维护

### OpenAPI（Swagger）规范

系统可集成Swashbuckle库自动生成OpenAPI 3.0文档，通过控制器注释与特性标注生成端点描述、参数约束、响应模型。建议在每个控制器方法添加XML文档注释（`/// <summary>`），通过`[ProducesResponseType]`特性声明可能的响应状态码（如`ConfigController:173`已标注返回类型）。

生成的Swagger UI界面提供交互式API测试能力，开发者可直接在浏览器中调用接口查看响应，降低API学习成本。生产环境应禁用Swagger UI或限制访问权限，避免敏感接口暴露。

### 接口变更日志（Changelog）

每次API版本发布应同步更新变更日志，记录新增端点、修改参数、废弃接口等信息。日志格式建议采用Keep a Changelog规范，按版本号分组，按变更类型分类（Added、Changed、Deprecated、Removed）。

示例变更日志条目：

```
## [v1.1.0] - 2025-01-15
### Added
- GET /api/v1/metrics/aggregations - 新增聚合指标查询接口，支持按分钟/小时/天聚合

### Changed
- GET /api/v1/metrics/history - 新增aggregation查询参数，默认为raw

### Deprecated
- GET /api/v1/config/alerts - 该端点将在v2.0移除，请迁移至/api/v2/alerts
```

### 接口示例与最佳实践

文档应包含典型调用示例与最佳实践指南，帮助客户端开发者快速上手。例如，指标查询API文档应说明：

**时间范围查询**：`GET /api/v1/metrics/history?processId=1234&from=2025-01-01T00:00:00Z&to=2025-01-02T00:00:00Z`
**聚合粒度选择**：小于1小时窗口使用`aggregation=minute`，小于1天窗口使用`aggregation=hour`，大于1天窗口使用`aggregation=day`
**错误处理**：客户端应捕获404错误（进程不存在）与503错误（数据库不可用），实现重试或降级逻辑

通过详尽的示例与场景说明，减少客户端开发者的试错成本，提升API可用性与开发效率。
