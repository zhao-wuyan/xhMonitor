# 类结构与职责划分

## 核心抽象层设计

系统采用接口抽象驱动的分层架构,通过三个核心接口定义了系统的扩展边界与职责分配。`IMetricProvider`接口位于整个指标采集体系的顶端,定义了插件化扩展的契约。该接口规定了每个指标提供者必须实现的六个核心方法:指标标识符(`MetricId`)、显示名称(`DisplayName`)、单位(`Unit`)、指标类型(`Type`)、数据采集(`CollectAsync`)、系统支持检测(`IsSupported`)以及系统总量获取(`GetSystemTotalAsync`)。这一设计体现了策略模式的应用,使得新增指标类型时无需修改核心监控逻辑,仅需实现该接口并注册至提供者注册表即可。

`IMetricAction`接口为系统预留了交互扩展能力,定义了指标点击触发的自定义操作契约。接口包含动作标识符(`ActionId`)、显示名称(`DisplayName`)、图标(`Icon`)以及异步执行方法(`ExecuteAsync`)。尽管当前代码库中尚未发现该接口的具体实现,但其存在表明系统架构已为未来的交互式功能(如一键清理内存、重启进程等)预留了扩展点。这种前瞻性设计体现了开闭原则,确保后续功能增强时核心架构的稳定性。

`IProcessMetricRepository`接口定义了数据持久化的抽象边界,通过单一方法`SaveMetricsAsync`将指标采集层与数据存储层解耦。接口采用批量保存策略,接受`IReadOnlyCollection<ProcessMetrics>`作为参数,这一设计选择有效减少了数据库交互频次,提升了高频采集场景下的性能表现。方法签名中的`cycleTimestamp`参数确保了同一采集周期内所有进程指标共享同一时间戳,为后续数据聚合与时间序列分析奠定了基础。

## 指标提供者实现体系

系统内置四个具体指标提供者,均实现了`IMetricProvider`接口。`CpuMetricProvider`采用批量性能计数器读取策略,通过缓存机制优化频繁采集场景。该类维护了`_cachedCpuData`和`_previousSamples`两个内部状态,前者存储最近一次的进程CPU使用率快照,后者记录上一次采样的原始值与时间戳。通过计算相邻两次采样的差值并除以时间间隔,类实现了基于Windows性能计数器的CPU使用率计算。特别值得注意的是`GetBatchCpuDataAsync`方法的设计:该方法使用`PerformanceCounterCategory.ReadCategory()`一次性读取所有进程的性能数据,避免了逐进程创建计数器导致的性能开销。缓存机制配合`SemaphoreSlim`并发控制,确保了在高并发采集请求下的线程安全与性能稳定。

`MemoryMetricProvider`采用直接读取进程工作集(`WorkingSet64`)的简洁实现策略。与CPU提供者的批量缓存不同,该类每次调用`CollectAsync`时都直接通过`Process.GetProcessById`获取目标进程对象并读取内存数据。这一设计选择源于内存数据的即时性需求与进程对象创建的低开销特性。`GetSystemTotalAsync`方法展现了跨平台兼容性考量:在Windows平台优先尝试通过WMI(`ManagementObjectSearcher`)获取物理内存总量,失败后回退至性能计数器读取提交限制(`Commit Limit`);在非Windows平台则使用.NET的垃圾回收器内存信息(`GC.GetGCMemoryInfo`)作为替代方案。

`GpuMetricProvider`面对GPU性能数据的特殊性,采用了进程级计数器缓存策略。该类维护`_counters`字典,以进程ID为键存储每个进程的GPU引擎计数器集合。`InitCounters`方法通过实例名前缀匹配(`pid_{processId}_`)定位目标进程的所有GPU引擎实例,这一实现依赖于Windows GPU Engine性能计数器的命名约定。系统总量采集方面,类通过`_systemCounters`缓存所有GPU引擎实例的计数器,避免每次调用时重复枚举实例。`_systemCountersLock`锁保证了初始化过程的线程安全,`_systemCountersInitialized`标志确保初始化逻辑仅执行一次。

`VramMetricProvider`(代码中引用但未在已读文件中展示)按照架构模式推测应遵循与GPU提供者类似的实现策略,专注于显存使用量的采集。四个提供者共同构成了系统的指标采集矩阵,通过统一的`IMetricProvider`接口实现了可替换性与可扩展性,体现了依赖倒置原则的应用。

## 服务层协调者架构

`MetricProviderRegistry`类扮演了提供者生命周期管理器的角色,采用注册表模式协调所有指标提供者。该类通过`ConcurrentDictionary<string, IMetricProvider>`存储已注册的提供者,以指标ID作为键实现O(1)时间复杂度的快速检索。`RegisterProvider`方法展现了严格的注册验证流程:首先检查MetricId非空,随后调用`IsSupported()`验证当前系统兼容性,最后尝试字典插入并处理冲突。任一环节失败都会触发提供者的立即释放(`Dispose()`),防止资源泄漏。

类的构造函数分两阶段初始化提供者池:第一阶段通过`RegisterBuiltInProviders`注册四个内置提供者,第二阶段通过`LoadFromDirectory`扫描插件目录并动态加载外部程序集。插件加载机制采用反射技术,`LoadFromAssembly`方法枚举程序集中所有实现`IMetricProvider`接口的非抽象类,通过无参构造函数实例化并注册。异常处理覆盖了文件加载、类型加载、实例化等多个环节,确保单个插件的失败不影响整体系统运行。这一设计体现了插件架构的核心价值:在保证核心系统稳定性的前提下,为第三方扩展提供标准化接入通道。

`PerformanceMonitor`类作为指标采集的总指挥,协调了进程扫描、提供者调用、并发控制三大子系统。`CollectAllAsync`方法的执行流程体现了管道式数据处理模式:首先调用`ProcessScanner.ScanProcesses()`获取待监控进程列表,随后通过`Parallel.ForEachAsync`启动并行采集任务,最终将采集结果封装为`ProcessMetrics`对象集合。并发控制策略采用双层限流:外层通过`ParallelOptions.MaxDegreeOfParallelism = 4`限制并行处理的进程数量,内层通过`_providerSemaphore = new SemaphoreSlim(8, 8)`限制同时执行的提供者调用。这一设计平衡了CPU利用率与系统负载,防止性能监控本身成为性能瓶颈。

`CollectMetricSafeAsync`方法展现了防御式编程的最佳实践。方法为每个提供者调用设置2秒超时限制,通过`Task.WhenAny`实现非阻塞超时检测。超时或异常情况下,方法返回`MetricValue.Error`而非抛出异常,确保单个指标采集失败不中断整体采集周期。异常捕获与日志记录为问题诊断提供了详尽的上下文信息,包括提供者ID、进程ID、错误消息等关键参数。

`ProcessScanner`类专注于目标进程发现,采用关键词过滤策略筛选监控范围。`ScanProcesses`方法通过`Process.GetProcesses()`枚举系统所有进程,并行调用`ProcessSingleProcess`提取命令行参数并执行关键词匹配。命令行获取依赖`ProcessCommandLineReader.GetCommandLine`方法,该方法封装了Windows平台特定的Interop调用。异常处理策略采用静默跳过(`UnauthorizedAccessException`、`InvalidOperationException`)而非中断,这一选择适应了系统进程访问受限的现实约束。关键词匹配采用大小写不敏感的子串包含算法,当关键词列表为空时,扫描器返回所有可访问进程,为全局监控场景提供支持。

## 数据访问层设计

`MonitorDbContext`类继承自Entity Framework Core的`DbContext`,定义了四张数据表的映射:`ProcessMetricRecords`、`AggregatedMetricRecords`、`AlertConfigurations`、`ApplicationSettings`。构造函数中执行的`PRAGMA journal_mode=WAL`命令将SQLite数据库切换至WAL(Write-Ahead Logging)模式,这一配置显著提升了读写并发能力,适配了高频数据写入与低频查询读取的监控场景特征。

`OnModelCreating`方法配置了多项数据完整性约束。两个指标表(`ProcessMetricRecords`、`AggregatedMetricRecords`)均应用了JSON有效性检查约束(`json_valid(MetricsJson)`),在数据库层面确保了JSON字段的结构完整性。UTC时间转换器的全局注册解决了跨时区数据存储的一致性问题:所有`DateTime`与`DateTime?`类型字段在写入时强制转换为UTC,读取时标记为UTC Kind,避免了时区混淆导致的时间计算错误。`ApplicationSettings`表的复合唯一索引(`Category`, `Key`)确保了配置项的唯一性,防止重复配置导致的歧义。种子数据的预置为系统首次启动提供了合理的默认配置,涵盖外观、数据采集、系统三大类共10项设置。

`MetricRepository`类实现了`IProcessMetricRepository`接口,采用`IDbContextFactory<MonitorDbContext>`创建短生命周期的DbContext实例,符合Entity Framework Core的最佳实践。`SaveMetricsAsync`方法的实现展现了批量插入优化:通过`AddRangeAsync`一次性提交所有记录,避免逐条插入导致的事务开销。`MapToEntity`静态方法负责领域模型到实体的转换,将`ProcessMetrics`对象的`Metrics`字典序列化为JSON字符串存储。异常处理策略采用日志记录而非重抛,这一选择优先保证了采集主流程的连续性,避免数据库异常导致的监控服务中断。

## 数据聚合工作者

`AggregationWorker`类继承自`BackgroundService`,作为后台任务定期执行三级数据聚合:原始数据聚合至分钟级、分钟聚合至小时级、小时聚合至天级。每个聚合级别采用统一的水位线(Watermark)追踪策略:通过查询目标聚合表的最大时间戳确定已处理的时间窗口,仅对新产生的未聚合数据执行聚合操作。这一设计确保了聚合任务的幂等性与增量处理能力,避免了重复聚合导致的数据不一致。

`AggregateRawToMinute`方法展现了时间窗口聚合的典型实现模式。方法首先通过`TruncateToMinute`扩展方法将时间戳截断至分钟边界,随后按进程ID、进程名、截断后时间戳三元组进行分组。组内数据通过反序列化`MetricsJson`字段还原为指标字典,逐指标计算最小值(`Min`)、最大值(`Max`)、平均值(`Avg`)、总和(`Sum`)、计数(`Count`)五个统计量。这一聚合粒度平衡了数据精度与存储效率,为趋势分析与异常检测提供了足够的统计信息。

`AggregateToHigherLevel`方法通过函数式参数`truncateFunc`实现了分钟到小时、小时到天两级聚合的代码复用。方法接受已聚合数据作为输入,对聚合统计量执行二次聚合:最小值取各组最小值的最小值,最大值取各组最大值的最大值,平均值通过总和除以总计数重新计算。这一递归聚合策略保证了跨时间窗口的统计量准确性,同时避免了返回原始数据重新计算的性能开销。

`DateTimeExtensions`静态类提供的三个时间截断扩展方法(`TruncateToMinute`、`TruncateToHour`、`TruncateToDay`)通过`DateTime`构造函数的选择性参数传递实现了精确的时间边界对齐。方法保留了原始时间的`Kind`属性,确保UTC时间在截断后仍保持UTC标记,这一细节处理避免了时区转换导致的聚合窗口偏移。

## 视图模型与状态管理

`FloatingWindowViewModel`类采用MVVM模式实现了桌面端悬浮窗的视图逻辑,通过`INotifyPropertyChanged`接口支持属性变更通知。类维护了三个进程集合:`TopProcesses`(CPU占用前五)、`PinnedProcesses`(用户固定)、`AllProcesses`(全部进程),通过`ObservableCollection<ProcessRowViewModel>`实现视图的自动更新。内部索引`_processIndex`以进程ID为键缓存进程行视图模型,避免每次数据更新时重新创建对象,这一优化显著降低了内存分配压力与UI刷新开销。

状态机设计通过`PanelState`枚举定义了四种面板状态:`Collapsed`(折叠)、`Expanded`(展开)、`Locked`(锁定)、`Clickthrough`(点击穿透)。状态转换逻辑封装在`OnBarPointerEnter`、`OnBarPointerLeave`、`OnBarClick`、`EnterClickthrough`、`ExitClickthrough`五个方法中,形成了清晰的交互状态机。特别值得注意的是点击穿透模式的实现:进入穿透模式时,`_stateBeforeClickthrough`字段保存当前状态,退出时恢复至保存的状态,确保了穿透模式的可逆性与用户体验的连续性。

`SyncProcessIndex`方法实现了增量更新策略:通过`seen`集合记录本轮接收到的进程ID,对已存在的进程执行`UpdateFrom`更新数据,对新进程创建`ProcessRowViewModel`实例,对已退出的进程从索引中移除。这一策略最小化了对象创建销毁开销,同时保证了视图模型与后端数据的一致性。`SyncCollectionOrder`方法采用就地调整算法同步集合顺序:首先移除不在目标列表中的元素,随后通过`Move`或`Insert`操作调整元素位置。算法通过`ReferenceEquals`判断对象身份而非值相等,确保了即使数据相同但对象不同的情况下仍能正确同步。

`ProcessRowViewModel`嵌套类封装了单个进程行的数据与状态,通过属性设置器中的`SetField`泛型方法实现了变更通知的统一处理。`UpdateFrom`方法从DTO对象提取指标值并更新属性,通过`GetValueOrDefault`安全访问指标字典,缺失指标时回退至默认值0,这一防御式设计确保了UI渲染的稳定性。

## 通信层实现

`SignalRService`类封装了SignalR客户端连接与事件订阅逻辑,通过事件模式向上层视图模型暴露数据推送能力。类定义了五个公开事件:`MetricsReceived`、`HardwareLimitsReceived`、`SystemUsageReceived`、`ProcessDataReceived`、`ConnectionStateChanged`,对应后端推送的不同消息类型。`ConnectAsync`方法配置了自动重连策略(`WithAutomaticReconnect`),确保网络抖动时的连接稳定性。

事件处理器采用泛型`JsonElement`接收原始JSON数据,通过`JsonSerializer.Deserialize`反序列化为强类型DTO对象。这一两阶段处理策略的优势在于:第一阶段SignalR接收原始JSON避免了反序列化失败导致的消息丢失,第二阶段应用层反序列化失败时可通过异常捕获记录详细错误信息而不中断连接。`PropertyNameCaseInsensitive = true`选项容忍了前后端命名约定的差异,提升了接口兼容性。

连接状态变更通过三个Hub事件监听实现:`Reconnecting`、`Reconnected`、`Closed`分别对应连接中断、恢复、关闭场景。`ConnectionStateChanged`事件的触发为视图层提供了连接状态指示的数据源,支持断线提示等用户体验优化。`DisconnectAsync`方法的异常抑制设计(`try-catch`捕获但不重抛)确保了资源清理逻辑的完整执行,避免停止失败影响对象销毁。

`MetricsHub`类作为服务端SignalR Hub的最小实现,仅重写了连接与断开事件的日志记录逻辑。实际的消息推送通过外部`IHubContext<MetricsHub>`注入实现,这一关注点分离设计使得Hub类保持轻量,便于单元测试与功能扩展。

## Web API控制器

`MetricsController`提供了RESTful风格的历史数据查询接口,通过`IDbContextFactory<MonitorDbContext>`创建短生命周期的数据库上下文。`GetLatest`方法采用两阶段查询策略:首先通过`MaxAsync`确定最新时间戳,随后过滤该时间戳的所有记录。这一设计避免了数据采集周期内记录时间戳的微小差异导致的数据遗漏,确保返回完整的最新采集快照。

`GetHistory`方法通过`aggregation`参数实现了多粒度数据查询:原始数据查询访问`ProcessMetricRecords`表,聚合数据查询根据粒度级别访问`AggregatedMetricRecords`表的对应数据。时间范围过滤采用开区间语义(`>`、`<`),与聚合逻辑的窗口定义保持一致,避免了边界数据的重复计入。`GetProcesses`方法通过分组聚合返回进程元数据,`LastSeen`字段的降序排序将最近活跃的进程置于列表前端,符合监控场景的查询需求特征。

## 职责分配总结

系统类结构呈现清晰的职责分层:接口层定义扩展契约与依赖抽象,提供者层封装平台特定的指标采集逻辑,服务层协调并发采集与生命周期管理,数据层抽象持久化操作与聚合计算,视图模型层管理UI状态与事件响应,通信层处理实时数据推送,控制器层提供历史数据访问。每个类遵循单一职责原则,通过接口依赖而非具体实现实现松耦合。插件架构的应用使得指标类型扩展无需修改核心代码,工厂模式的提供者注册表支持运行时动态加载,MVVM模式的视图模型实现了视图与逻辑的分离,后台服务的数据聚合确保了存储效率与查询性能的平衡。整体架构体现了SOLID原则的综合应用,为系统的可维护性与可扩展性奠定了坚实基础。
