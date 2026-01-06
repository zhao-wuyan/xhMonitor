# 公开API参考

## REST API架构设计

系统采用RESTful架构风格设计HTTP接口,遵循资源导向的设计原则,所有API端点统一挂载于`api/v1`路径下,体现了版本化管理的前瞻性考量。从路由配置来看,系统通过`Program.cs:159`的`app.MapControllers()`实现控制器自动发现与注册,配合`[ApiController]`特性实现参数验证、模型绑定等标准化处理,确保接口行为的一致性。

CORS策略配置于`Program.cs:115-124`,采用白名单机制允许本地开发环境与Electron应用的跨域访问。值得注意的是,策略中包含`app://.`协议支持,这一设计专门针对桌面应用的WebView环境,体现了对多端部署场景的深度考量。服务监听配置通过Kestrel实现,默认绑定`localhost:35179`,通过配置文件可灵活调整主机与端口,为不同部署环境提供了适配能力。

从控制器职责划分来看,系统将API分为三大领域:指标数据查询(`MetricsController`)、系统配置管理(`ConfigController`)、悬浮窗配置(`WidgetConfigController`)。这一划分遵循单一职责原则,各控制器边界清晰,避免了功能耦合。所有控制器均采用依赖注入模式获取服务依赖,通过`IDbContextFactory`实现数据库上下文的线程安全访问,这一设计确保了在高并发场景下的数据访问可靠性。

## 指标数据查询接口

`MetricsController`位于`XhMonitor.Service/Controllers/MetricsController.cs`,提供四个核心端点用于指标数据的多维度查询。`GET /api/v1/metrics/latest`端点支持通过`processId`、`processName`、`keyword`三种维度过滤最新指标快照,查询逻辑通过`MetricsController.cs:31-48`的动态查询构建实现,采用时间戳最大值定位最新批次,确保返回数据的时效性。该端点返回`ProcessMetricRecord`实体数组,每条记录包含进程标识、时间戳及JSON格式的指标集合。

历史数据查询通过`GET /api/v1/metrics/history`端点实现,支持原始数据与聚合数据两种模式。当`aggregation`参数为`raw`时,直接查询`ProcessMetricRecords`表获取原始采样点;当指定为`minute`、`hour`、`day`时,则查询`AggregatedMetricRecords`表获取预聚合数据。这一设计通过`MetricsController.cs:73-122`的分支逻辑实现,有效平衡了查询精度与性能开销。聚合级别映射通过枚举`AggregationLevel`定义,确保了类型安全。

进程列表查询端点`GET /api/v1/metrics/processes`提供进程维度的统计视图,通过`GroupBy`操作聚合同一进程的多次采样记录,返回进程ID、名称、最后出现时间及记录数量。该端点同样支持时间范围与关键词过滤,查询逻辑位于`MetricsController.cs:126-164`,通过`OrderByDescending(p => p.LastSeen)`确保活跃进程优先展示。聚合查询端点`GET /api/v1/metrics/aggregations`则提供跨进程的时序数据视图,用于系统级别的趋势分析。

所有查询端点均采用异步模式实现,通过`IDbContextFactory.CreateDbContextAsync()`获取数据库上下文,配合`await using`语法确保资源的及时释放。查询结果直接返回实体对象,依赖ASP.NET Core的JSON序列化机制自动转换为响应体,这一设计简化了数据传输层的实现复杂度。

## 系统配置管理接口

`ConfigController`位于`XhMonitor.Service/Controllers/ConfigController.cs`,承担系统配置的读写职责。`GET /api/v1/config`端点返回监控配置快照,包括采样间隔(`Monitor:IntervalSeconds`)与关键词列表(`Monitor:Keywords`),配置源自`IConfiguration`服务,支持通过`appsettings.json`或环境变量覆盖。该端点实现于`ConfigController.cs:31-48`,采用匿名对象构建响应结构,体现了配置数据的只读特性。

告警配置管理通过三个端点实现CRUD操作。`GET /api/v1/config/alerts`查询所有告警规则,返回`AlertConfiguration`实体数组,按`MetricId`排序确保展示顺序的稳定性。`POST /api/v1/config/alerts`端点支持创建或更新告警规则,通过`ConfigController.cs:63-86`的逻辑判断实体是否存在,存在则更新阈值与启用状态,不存在则插入新记录。删除操作通过`DELETE /api/v1/config/alerts/{id}`实现,返回`204 NoContent`状态码符合RESTful规范。

指标元数据查询端点`GET /api/v1/config/metrics`提供系统支持的指标清单,数据源自`MetricProviderRegistry`服务。该端点通过`ConfigController.cs:106-139`的映射逻辑,将`IMetricProvider`接口转换为`MetricMetadata`传输对象,附加颜色与图标映射信息用于前端渲染。这一设计将指标定义与UI展示解耦,新增指标时仅需实现`IMetricProvider`接口即可自动暴露于API。

应用设置管理通过`GET /api/v1/config/settings`与`PUT /api/v1/config/settings`端点实现。查询端点返回按分类分组的配置字典,分组逻辑位于`ConfigController.cs:174-192`,通过`GroupBy`操作将扁平化的`ApplicationSettings`实体转换为嵌套结构,便于前端按分类渲染设置界面。更新端点支持单项更新(`PUT /api/v1/config/settings/{category}/{key}`)与批量更新(`PUT /api/v1/config/settings`),批量更新通过`ConfigController.cs:233-265`的嵌套循环遍历分类与键值对,统一时间戳确保更新操作的原子性。

健康检查端点`GET /api/v1/config/health`提供服务状态监控能力,通过`Database.CanConnectAsync()`验证数据库连接,返回状态码`200`表示健康,`503`表示不健康。该端点实现于`ConfigController.cs:141-167`,异常信息包含于响应体中,便于运维人员快速定位故障原因。

## 悬浮窗配置接口

`WidgetConfigController`位于`XhMonitor.Service/Controllers/WidgetConfigController.cs`,专门管理桌面悬浮窗的交互配置。配置数据持久化于文件系统而非数据库,存储路径为`{AppContext.BaseDirectory}/data/widget-settings.json`,这一设计选择源于配置数据的轻量级特性与独立性考量,避免了数据库依赖。

`GET /api/v1/widgetconfig`端点读取配置文件并反序列化为`WidgetSettings`对象,若文件不存在则返回默认配置。默认配置通过`WidgetConfigController.cs:94-116`的工厂方法生成,包含五个预定义指标(cpu、memory、gpu、vram、power)的点击配置模板,其中power指标预设了`togglePowerMode`动作及模式参数,体现了对常见使用场景的预判。

配置更新通过`POST /api/v1/widgetconfig`端点实现,接收完整的`WidgetSettings`对象并序列化写入文件,采用`WriteIndented`选项确保JSON格式的可读性。单指标配置更新端点`POST /api/v1/widgetconfig/{metricId}`提供细粒度的更新能力,通过`WidgetConfigController.cs:72-92`的逻辑先读取现有配置,更新指定指标的`MetricClickConfig`,再整体写回文件。这一设计避免了并发更新时的数据覆盖风险。

`WidgetSettings`数据模型定义于`XhMonitor.Core/Models/WidgetSettings.cs`,包含全局开关`EnableMetricClick`与指标级配置字典`MetricClickActions`。`MetricClickConfig`结构包含启用标志、动作类型及参数字典,参数字典采用`Dictionary<string, string>`类型,提供了动作参数的灵活扩展能力。这一模型设计支持未来新增自定义动作类型而无需修改数据结构。

## SignalR实时通信

系统通过SignalR实现服务端向客户端的实时数据推送,Hub端点挂载于`/hubs/metrics`路径,配置位于`Program.cs:160-161`。`MetricsHub`类定义于`XhMonitor.Service/Hubs/MetricsHub.cs`,继承自`Hub`基类,实现了连接生命周期的日志记录,通过`OnConnectedAsync`与`OnDisconnectedAsync`方法追踪客户端连接状态。

数据推送逻辑位于后台服务`Worker.cs`,通过`IHubContext<MetricsHub>`服务获取Hub上下文。系统定义三个推送事件,事件名称常量位于`XhMonitor.Core/Constants/SignalREvents.cs`:

- `metrics.hardware`事件推送硬件限制信息,包含内存与显存的最大容量,推送时机为系统启动时与硬件变更时,实现位于`Worker.cs:132`与`Worker.cs:181`。该事件携带时间戳、最大内存、最大显存三个字段,采用匿名对象构建消息体。

- `metrics.system`事件推送系统级使用率,包含CPU、GPU、内存、显存的总使用量及硬件容量,推送频率与监控采样周期一致,实现位于`Worker.cs:220`。该事件通过`SystemMetricProvider`聚合各指标提供者的数据,实现了跨指标的统一推送。

- `metrics.processes`事件推送进程级指标数据,包含进程列表及各进程的指标集合,推送逻辑位于`Worker.cs:248`。消息体包含时间戳、进程数量及进程数组,每个进程对象包含ID、名称、命令行及指标字典,指标字典的键为`MetricId`,值为`MetricValue`对象。

客户端连接通过`@microsoft/signalr`库实现,连接配置位于`xhmonitor-web/src/hooks/useMetricsHub.ts:14-18`,启用自动重连机制确保连接稳定性。事件订阅通过`connection.on('metrics.latest', callback)`语法实现,回调函数接收服务端推送的数据对象。桌面客户端通过`XhMonitor.Desktop/Services/SignalRService.cs`实现连接管理,采用`HubConnectionBuilder`构建连接,配置与Web客户端保持一致。

SignalR传输协议采用WebSocket优先,降级至Server-Sent Events或Long Polling,协议协商由SignalR框架自动完成。CORS策略配置确保了跨域连接的可行性,`AllowCredentials`选项支持携带认证凭据,为未来的身份验证扩展预留了空间。

## 插件扩展接口

系统通过`IMetricProvider`接口定义指标提供者的扩展点,接口位于`XhMonitor.Core/Interfaces/IMetricProvider.cs`。该接口要求实现者提供指标标识(`MetricId`)、显示名称(`DisplayName`)、单位(`Unit`)、类型(`Type`)四个元数据属性,以及`CollectAsync`、`IsSupported`、`GetSystemTotalAsync`三个行为方法。

`CollectAsync`方法接收进程ID参数,返回`MetricValue`对象,该对象包含数值、单位及时间戳。`IsSupported`方法用于运行时检测当前系统是否支持该指标,例如GPU指标在无独立显卡的系统上应返回`false`。`GetSystemTotalAsync`方法返回系统级总量或使用率,用于百分比类型指标的基准计算,例如内存指标返回物理内存总量,CPU指标返回系统总使用率。

插件加载机制通过`MetricProviderRegistry`实现,该类位于`XhMonitor.Service/Core/MetricProviderRegistry.cs`,在服务启动时扫描插件目录,通过反射加载实现`IMetricProvider`接口的类型。插件目录路径通过配置项`MetricProviders:PluginDirectory`指定,默认为应用根目录下的`plugins`文件夹,配置逻辑位于`Program.cs:89-95`。

`IMetricAction`接口定义指标动作的扩展点,接口位于`XhMonitor.Core/Interfaces/IMetricAction.cs`。该接口要求实现动作标识(`ActionId`)、显示名称(`DisplayName`)、图标(`Icon`)三个元数据属性,以及`ExecuteAsync`方法。`ExecuteAsync`方法接收进程ID与指标ID参数,返回`ActionResult`对象,该对象包含执行状态、消息及可选的返回数据。

动作扩展机制与指标扩展类似,通过注册表模式管理动作实例,支持运行时动态注册。悬浮窗配置中的`Action`字段对应动作标识,`Parameters`字典传递给`ExecuteAsync`方法,实现了配置驱动的动作执行。这一设计使得新增自定义动作时,无需修改核心代码,仅需实现接口并注册即可。

## 数据模型规范

系统采用分层的数据模型设计,实体层(`Entities`)定义数据库表结构,传输层(`Models`/`Dto`)定义API输入输出格式。实体类位于`XhMonitor.Core/Entities`命名空间,采用EF Core的Code First模式,通过特性标注定义表名、索引、字段约束。

`ProcessMetricRecord`实体定义于`XhMonitor.Core/Entities/ProcessMetricRecord.cs`,表示原始指标采样记录。该实体包含进程标识、时间戳及JSON格式的指标数据,`MetricsJson`字段采用`TEXT`类型存储,通过序列化机制支持动态指标集合。索引配置于`ProcessMetricRecord.cs:8-9`,包含`(ProcessId, Timestamp)`复合索引与`Timestamp`单列索引,优化了按进程查询历史与按时间范围查询的性能。

`AggregatedMetricRecord`实体定义于`XhMonitor.Core/Entities/AggregatedMetricRecord.cs`,表示预聚合的指标数据。该实体增加了`AggregationLevel`字段标识聚合粒度,索引配置包含三组:`(ProcessId, AggregationLevel, Timestamp)`支持按进程查询特定粒度的聚合数据,`(ProcessId, Timestamp)`支持跨粒度查询,`(AggregationLevel, Timestamp)`支持系统级聚合查询。

`AlertConfiguration`实体定义于`XhMonitor.Core/Entities/AlertConfiguration.cs`,表示告警规则配置。该实体包含指标标识、阈值、启用状态及时间戳,`MetricId`字段限制长度为100字符,通过`MaxLength`特性约束。`ApplicationSettings`实体定义于`XhMonitor.Core/Entities/ApplicationSettings.cs`,采用键值对结构存储应用配置,`Category`字段用于分类,`Value`字段存储JSON格式的配置值,支持复杂对象的序列化存储。

传输对象层包含多个DTO类,`MetricsDataDto`定义于`XhMonitor.Desktop/Models/MetricsDataDto.cs`,用于SignalR推送的消息体。该类包含时间戳、进程数量、进程列表及系统统计四个字段,通过`JsonPropertyName`特性映射为驼峰命名,符合JavaScript命名规范。`ProcessInfoDto`包含进程标识与指标字典,指标字典的值类型为`MetricValue`,该类型定义于`XhMonitor.Core/Models/MetricValue.cs`,包含数值、单位、时间戳三个字段。

`MetricMetadata`类定义于`XhMonitor.Service/Models/MetricMetadata.cs`,用于指标元数据的传输。该类采用`required`修饰符标记必填字段,确保对象构造时的完整性。`UpdateSettingRequest`类定义于`XhMonitor.Service/Models/SettingsDto.cs`,用于配置更新请求的参数绑定,采用单字段设计简化了请求体结构。

所有传输对象均采用不可变设计或属性初始化器模式,避免了对象状态的意外修改。JSON序列化配置采用系统默认的`System.Text.Json`,通过`JsonPropertyName`特性实现字段名映射,通过`JsonIgnore`特性排除不需要序列化的字段。这一设计确保了API契约的稳定性与向后兼容性。
