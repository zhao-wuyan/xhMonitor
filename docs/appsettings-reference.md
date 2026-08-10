# appsettings 配置全量说明

本文档汇总项目中与 `appsettings*.json` 相关的主要配置项，按 Service 与 Desktop 分开说明。

## 1. Service 配置（`xhm-service/appsettings.json`）

### 1.1 日志

Rust Service 不读取 `appsettings.json` 中遗留的 `Serilog` 段。日志规则如下：

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `RUST_LOG` | `info` | 控制台日志过滤规则 |
| 文件日志 | `debug` | 写入 `logs/xhmonitor.YYYY-MM-DD.log`，按天滚动 |

### 1.2 数据库

Rust Service 不读取遗留的 `ConnectionStrings:DatabaseConnection`。SQLite 数据库固定为 Service 可执行文件目录下的 `xhmonitor.db`。

### 1.3 `Monitor`

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `Monitor:IntervalSeconds` | int | `3` | 进程采样周期（秒） |
| `Monitor:SystemUsageIntervalSeconds` | int | `1` | 系统指标采样周期（秒） |
| `Monitor:Keywords` | string[] | 示例见文件 | 进程过滤关键词 |
| `Monitor:ProcessNameRules` | array | 示例见文件 | 进程显示名规则（Direct/Regex） |

### 1.4 `MetricProviders`

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `MetricProviders:PluginDirectory` | string | `""` | 外部指标插件目录 |
| `MetricProviders:PreferLibreHardwareMonitor` | bool | `true` | 是否优先用 LibreHardwareMonitor |

### 1.5 `Power`

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `Power:Backend` | string | `Native` | 功耗后端：`Native` 常驻加载 `libryzenadj.dll`，`Cli` 调用 `ryzenadj.exe` |
| `Power:NativeFallbackToCli` | bool | `true` | Native 后端初始化或调用失败时是否自动回退到 CLI 后端 |
| `Power:RyzenAdjPath` | string | `""` | RyzenAdj 路径（可填目录、`libryzenadj.dll` 或 `ryzenadj.exe`；Native 会从同目录查找 DLL，CLI 会从同目录查找 exe） |
| `Power:PollingIntervalSeconds` | int | `3` | 功耗采样周期（秒） |
| `Power:DeviceVerification:Endpoint` | string | `http://127.0.0.1:5050/device_info` | 旧设备验证 API；不再用于开启功耗监控 |
| `Power:DeviceVerification:TimeoutSeconds` | int | `5` | 设备验证超时（秒） |
| `Power:DeviceVerification:SchemeProfiles` | object | 示例见文件 | 可复用功耗方案集合；key 由设备项的 `SchemeKey` 引用 |
| `Power:DeviceVerification:Devices` | array | 示例见文件 | 设备识别规则；匹配成功后通过 `SchemeKey` 绑定功耗切换方案 |

功耗监控只按本机 CPU 名称启用：`processor_name` 同时包含 `AMD Ryzen AI Max` 和 `395`。NovaStudio `/device_info` 不再用于开启功耗监控，只保留给没有 SMBIOS 硬件条件的旧设备验证规则。`Devices` 主要控制功耗切换方案绑定。`Devices[*].SchemeKey` 没有匹配到 `SchemeProfiles`，或功耗监控已启用但未命中具体设备规则时，只禁用功耗切换并记录日志；功耗监控和功耗展示继续按 RyzenAdj 可用性运行。

### 1.6 `Server`

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `Server:Host` | string | `localhost` | 服务监听主机 |
| `Server:Port` | int | `35179` | 服务端口 |
| `Server:HubPath` | string | `/hubs/metrics` | SignalR Hub 路径 |

### 1.7 `SignalR`

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `SignalR:MaximumReceiveMessageSize` | long | `1048576` | 单消息接收上限（字节） |
| `SignalR:ApplicationMaxBufferSize` | long | `1048576` | 应用层缓冲上限（字节） |
| `SignalR:TransportMaxBufferSize` | long | `1048576` | 传输层缓冲上限（字节） |

### 1.8 `Database`

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `Database:RetentionDays` | int | `30` | 原始数据保留天数 |
| `Database:CleanupIntervalHours` | int | `24` | 清理任务间隔（小时） |

### 1.9 `Aggregation`

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `Aggregation:BatchSize` | int | `2000` | 聚合任务单批读取条数；值越大吞吐更高但峰值内存更高 |

建议范围：
- 常规：`1000-5000`
- 低内存机器：`500-2000`
- 高吞吐场景：`2000-10000`（建议先压测）

---

## 2. Desktop 配置（`XhMonitor.Desktop/appsettings*.json`）

Desktop 采用环境分层配置：先加载 `appsettings.json`，再按环境加载 `appsettings.{Environment}.json` 覆盖。

### 2.1 基础配置（`appsettings.json`）

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `ServiceExecutablePath` | string | `../Service/xhm-service.exe` | Rust Service 可执行文件路径 |
| `UiOptimization:EnableProcessRefreshThrottling` | bool | `true` | 是否启用进程列表刷新节流 |
| `UiOptimization:ProcessRefreshIntervalMs` | int | `150` | 节流间隔（毫秒），兜底值 |

### 2.2 环境覆盖配置

| 文件 | `ProcessRefreshIntervalMs` | 说明 |
|------|----------------------------|------|
| `appsettings.Development.json` | `100` | 开发环境，偏重交互流畅度 |
| `appsettings.Staging.json` | `150` | 预发布环境，平衡体验与资源 |
| `appsettings.Production.json` | `200` | 生产环境，偏重稳态资源占用 |

Desktop 通过 `DOTNET_ENVIRONMENT` 决定加载哪个环境文件。未设置时通常为 `Production`。

---

## 3. 数据库运行时设置（非 appsettings）

以下配置不在 `appsettings.json`，而在数据库 `ApplicationSettings` 中维护（可运行时调整）：

- `Appearance.ThemeColor`
- `Appearance.Opacity`
- `DataCollection.ProcessKeywords`
- `DataCollection.TopProcessCount`
- `System.StartWithWindows`
- `System.EnableLanAccess`
- `System.EnableAccessKey`
- `System.AccessKey`
- `System.IpWhitelist`

Service 的运行时设置通过 `/api/v1/config` 读取和更新，并持久化到 SQLite。
