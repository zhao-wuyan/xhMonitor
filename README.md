# XhMonitor - Windows资源监视器

一个高性能的Windows进程资源监控系统，支持CPU、内存、GPU、显存等指标的实时采集、聚合分析和Web可视化。

## 功能特性

### 核心功能
- ✅ **进程监控**：基于关键词过滤，监控指定进程的资源占用
- ✅ **多维度指标**：CPU、内存、GPU、显存（支持插件扩展）
- ✅ **数据持久化**：SQLite存储原始数据和聚合数据
- ✅ **分层聚合**：自动生成分钟/小时/天级别统计数据
- ✅ **Web可视化**：React + TailwindCSS现代化界面
- ✅ **实时推送**：SignalR实时推送最新指标
- ✅ **动态扩展**：配置驱动的指标系统，零前端代码修改
- ✅ **国际化支持**：中英文切换，易于扩展多语言

### 技术特性
- 🔌 **插件化架构**：IMetricProvider接口支持自定义指标
- 📊 **JSON存储**：灵活的指标数据格式
- ⚡ **高性能**：优化的PID→InstanceName映射（O(1)查找）
- 🔒 **线程安全**：SemaphoreSlim保护共享资源
- 🎯 **精确聚合**：存储Sum/Count支持数学正确的加权平均
- 🎨 **Glassmorphism UI**：现代化毛玻璃效果界面

## 技术栈

### 后端
- **框架**：.NET 8 + ASP.NET Core
- **数据库**：SQLite + EF Core 8
- **实时通信**：SignalR
- **性能监控**：LibreHardwareMonitor (系统级) + PerformanceCounter API (进程级)
- **日志**：Serilog

### 前端
- **框架**：React 19 + TypeScript
- **构建工具**：Vite 7
- **样式**：TailwindCSS v4 (Glassmorphism)
- **图表**：ECharts 6
- **实时通信**：@microsoft/signalr
- **图标**：Lucide React

## 监控原理详解

### 1. CPU 监控

**原理**：使用 Windows Performance Counter API

**实现细节**：
```csharp
// 位置：XhMonitor.Core/Providers/CpuMetricProvider.cs
public class CpuMetricProvider : IMetricProvider
{
    // 使用 PerformanceCounter 读取进程CPU使用率
    private PerformanceCounter _counter;

    public async Task<MetricValue> CollectAsync(int processId)
    {
        // 1. 通过PID获取进程实例名
        var instanceName = GetInstanceName(processId);

        // 2. 创建性能计数器
        _counter = new PerformanceCounter(
            "Process",           // 类别
            "% Processor Time",  // 计数器名称
            instanceName,        // 实例名（如 "python#2"）
            true                 // 只读
        );

        // 3. 首次调用初始化
        _counter.NextValue();
        await Task.Delay(100);

        // 4. 获取实际值
        var cpuUsage = _counter.NextValue();

        return new MetricValue { Value = cpuUsage, Unit = "%" };
    }
}
```

**关键API**：
- `PerformanceCounter("Process", "% Processor Time", instanceName)`
- 需要两次调用 `NextValue()` 才能获取准确值
- 实例名格式：`processName#index`（如 `python#2`）

**优化**：
- 使用 `ConcurrentDictionary` 缓存 PID → InstanceName 映射
- O(1) 时间复杂度查找

### 2. 内存监控

**原理**：使用 .NET Process API

**实现细节**：
```csharp
// 位置：XhMonitor.Core/Providers/MemoryMetricProvider.cs
public class MemoryMetricProvider : IMetricProvider
{
    public Task<MetricValue> CollectAsync(int processId)
    {
        // 1. 通过PID获取进程对象
        using var process = Process.GetProcessById(processId);

        // 2. 读取工作集大小（物理内存）
        var bytes = process.WorkingSet64;

        // 3. 转换为MB
        var mb = bytes / 1024.0 / 1024.0;

        return Task.FromResult(new MetricValue
        {
            Value = Math.Round(mb, 1),
            Unit = "MB"
        });
    }
}
```

**关键API**：
- `Process.GetProcessById(processId)` - 获取进程对象
- `Process.WorkingSet64` - 物理内存使用量（字节）
- 其他可用属性：
  - `PrivateMemorySize64` - 私有内存
  - `VirtualMemorySize64` - 虚拟内存
  - `PagedMemorySize64` - 分页内存

### 3. GPU 监控

**原理**：使用 Windows Performance Counter API (GPU Engine)

**实现细节**：
```csharp
// 位置：XhMonitor.Core/Providers/GpuMetricProvider.cs
public class GpuMetricProvider : IMetricProvider
{
    public async Task<MetricValue> CollectAsync(int processId)
    {
        // 1. 获取所有GPU引擎实例
        var category = new PerformanceCounterCategory("GPU Engine");
        var instanceNames = category.GetInstanceNames();

        // 2. 过滤当前进程的GPU引擎
        var prefix = $"pid_{processId}_";
        var relevantInstances = instanceNames
            .Where(n => n.Contains(prefix));

        // 3. 累加所有引擎的使用率
        double totalUsage = 0;
        foreach (var instance in relevantInstances)
        {
            using var counter = new PerformanceCounter(
                "GPU Engine",
                "Utilization Percentage",
                instance,
                true
            );

            counter.NextValue();
            await Task.Delay(100);
            totalUsage += counter.NextValue();
        }

        return new MetricValue { Value = totalUsage, Unit = "%" };
    }
}
```

**关键API**：
- `PerformanceCounterCategory("GPU Engine")`
- 计数器：`Utilization Percentage`
- 实例名格式：`pid_1234_luid_0x00000000_0x0000D3C7_phys_0_eng_3_engtype_3D`

**注意事项**：
- 需要 Windows 10 Fall Creators Update (1709) 或更高版本
- 需要支持 WDDM 2.0 的显卡驱动
- 一个进程可能有多个GPU引擎实例（3D、Copy、Video等）

### 4. VRAM (显存) 监控

**原理**：使用 Windows Performance Counter API (GPU Process Memory)

**实现细节**：
```csharp
// 位置：XhMonitor.Core/Providers/VramMetricProvider.cs
public class VramMetricProvider : IMetricProvider
{
    public async Task<MetricValue> CollectAsync(int processId)
    {
        // 1. 获取GPU进程内存类别
        var category = new PerformanceCounterCategory("GPU Process Memory");
        var instanceNames = category.GetInstanceNames();

        // 2. 过滤当前进程的实例
        var prefix = $"pid_{processId}_";
        long totalBytes = 0;

        // 3. 累加所有GPU的显存使用
        foreach (var name in instanceNames.Where(n => n.Contains(prefix)))
        {
            using var counter = new PerformanceCounter(
                "GPU Process Memory",
                "Dedicated Usage",  // 专用显存
                name,
                true
            );

            totalBytes += counter.RawValue;
        }

        // 4. 转换为MB
        var mb = totalBytes / 1024.0 / 1024.0;

        return new MetricValue { Value = Math.Round(mb, 1), Unit = "MB" };
    }
}
```

**关键API**：
- `PerformanceCounterCategory("GPU Process Memory")`
- 计数器：
  - `Dedicated Usage` - 专用显存（独显）
  - `Shared Usage` - 共享显存（集显）
- 实例名格式：`pid_1234_luid_0x00000000_0x0000D3C7_phys_0`

**可用计数器**：
- `Dedicated Usage` - 独立显存使用量
- `Shared Usage` - 共享内存使用量
- `Total Committed` - 总提交内存

### 5. 进程扫描

**原理**：基于关键词过滤进程列表

**实现细节**：
```csharp
// 位置：XhMonitor.Service/Core/ProcessScanner.cs
public class ProcessScanner
{
    private readonly string[] _keywords;

    public IEnumerable<ProcessInfo> ScanProcesses()
    {
        // 1. 获取所有运行中的进程
        var allProcesses = Process.GetProcesses();

        // 2. 根据关键词过滤
        var filtered = allProcesses.Where(p =>
            _keywords.Any(keyword =>
                p.ProcessName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            )
        );

        // 3. 提取进程信息
        return filtered.Select(p => new ProcessInfo
        {
            ProcessId = p.Id,
            ProcessName = p.ProcessName,
            CommandLine = GetCommandLine(p.Id)  // 通过WMI获取
        });
    }
}
```

**关键API**：
- `Process.GetProcesses()` - 获取所有进程
- WMI查询命令行：`SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}`

### 6. 数据聚合

**原理**：时间窗口聚合 + 统计计算

**实现细节**：
```csharp
// 位置：XhMonitor.Service/Workers/AggregationWorker.cs
public class AggregationWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // 1. 原始数据 → 分钟聚合
            await AggregateRawToMinute();

            // 2. 分钟聚合 → 小时聚合
            await AggregateMinuteToHour();

            // 3. 小时聚合 → 天聚合
            await AggregateHourToDay();

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task AggregateRawToMinute()
    {
        // 按进程和分钟分组
        var groups = rawRecords
            .GroupBy(r => new {
                r.ProcessId,
                Minute = r.Timestamp.TruncateToMinute()
            });

        foreach (var group in groups)
        {
            // 解析JSON并计算统计值
            var metrics = group.Select(r =>
                JsonSerializer.Deserialize<Dictionary<string, MetricValue>>(r.MetricsJson)
            );

            // 计算 Min, Max, Avg, Sum, Count
            var aggregated = CalculateStatistics(metrics);

            // 保存聚合结果
            await SaveAggregation(aggregated, AggregationLevel.Minute);
        }
    }
}
```

**聚合算法**：
- **Min**: 最小值
- **Max**: 最大值
- **Avg**: 加权平均 = Sum / Count
- **Sum**: 累加和
- **Count**: 样本数量

### 7. 实时推送 (SignalR)

**原理**：WebSocket 双向通信

**实现细节**：
```csharp
// 位置：XhMonitor.Service/Worker.cs
public class Worker : BackgroundService
{
    private readonly IHubContext<MetricsHub> _hubContext;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // 1. 采集指标
            var metrics = await _monitor.CollectAllAsync();

            // 2. 保存到数据库
            await _repository.SaveMetricsAsync(metrics, timestamp);

            // 3. 推送到所有连接的客户端
            await _hubContext.Clients.All.SendAsync(
                "metrics.latest",  // 事件名
                new {
                    Timestamp = timestamp,
                    ProcessCount = metrics.Count,
                    Processes = metrics
                },
                stoppingToken
            );

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
```

**SignalR Hub**：
```csharp
// 位置：XhMonitor.Service/Hubs/MetricsHub.cs
public sealed class MetricsHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}",
            Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}",
            Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
```

**前端连接**：
```typescript
// 位置：xhmonitor-web/src/hooks/useMetricsHub.ts
const connection = new signalR.HubConnectionBuilder()
  .withUrl('http://localhost:35179/hubs/metrics')
  .withAutomaticReconnect()  // 自动重连
  .configureLogging(signalR.LogLevel.Information)
  .build();

connection.on('metrics.latest', (data: MetricsData) => {
  setMetricsData(data);  // 更新React状态
});

await connection.start();
```

## 系统要求

### 后端
- **操作系统**：Windows 10/11 (1709+)
- **.NET 版本**：.NET 8 SDK
- **开发工具**：Visual Studio 2022 或 VS Code
- **权限要求**：
  - **推荐**：管理员权限（启用 LibreHardwareMonitor 混合架构，获取更准确的系统级指标）
  - **最低**：普通用户权限（自动回退到 PerformanceCounter，功能完整但系统级指标精度略低）

### 混合架构说明

本项目采用 **LibreHardwareMonitor + PerformanceCounter 混合架构**：

| 指标类型 | 数据源 | 权限要求 | 说明 |
|---------|--------|---------|------|
| **系统级指标** | LibreHardwareMonitor | 管理员权限 | CPU/GPU/Memory 总使用率，精度更高 |
| **进程级指标** | PerformanceCounter | 普通用户权限 | 单个进程的资源占用，功能完整 |

**自动回退机制**：
- 有管理员权限：系统级指标使用 LibreHardwareMonitor，进程级指标使用 PerformanceCounter
- 无管理员权限：所有指标自动回退到 PerformanceCounter，功能不受影响

## 快速开始

### 环境要求

**后端**：
- Windows 10/11 (1709+)
- .NET 8 SDK
- Visual Studio 2022 或 VS Code

**前端**：
- Node.js 18+
- npm 或 pnpm

### 安装步骤

#### 1. 后端服务

**克隆仓库**
```bash
git clone <repository-url>
cd xhMonitor
```

**还原依赖**
```bash
dotnet restore
```

**应用数据库迁移**
```bash
cd XhMonitor.Service
dotnet ef database update
```

**配置监控关键词**

编辑 `XhMonitor.Service/appsettings.json`：
```json
{
  "Monitor": {
    "IntervalSeconds": 5,
    "Keywords": ["python", "node", "docker"]
  }
}
```

**启动后端服务**
```bash
dotnet run --project XhMonitor.Service
```

服务将在 `http://localhost:35179` 启动。

#### 2. 前端界面

**进入前端目录**
```bash
cd xhmonitor-web
```

**安装依赖**
```bash
npm install
```

**启动开发服务器**
```bash
npm run dev
```

前端将在 `http://localhost:35180` 启动。

**构建生产版本**
```bash
npm run build
```

### 验证运行

**健康检查**
```bash
curl http://localhost:35179/api/v1/config/health
```

**查询最新指标**
```bash
curl http://localhost:35179/api/v1/metrics/latest
```

**访问Web界面**
```
http://localhost:35180
```

## API文档

### REST API

#### 基础信息
- **Base URL**: `http://localhost:35179/api/v1`
- **Content-Type**: `application/json`
- **认证**: 无（本地使用）

#### Metrics API

**1. 获取最新指标**
```http
GET /metrics/latest?processId={int}&processName={string}&keyword={string}
```

查询参数（可选）：
- `processId`: 进程ID
- `processName`: 进程名称（模糊匹配）
- `keyword`: 关键词（匹配进程名或命令行）

响应示例：
```json
[
  {
    "id": 1234,
    "processId": 5678,
    "processName": "python",
    "commandLine": "python app.py",
    "timestamp": "2025-12-21T10:30:00Z",
    "metricsJson": "{\"cpu\":{\"value\":15.2,\"unit\":\"%\"},\"memory\":{\"value\":256.5,\"unit\":\"MB\"}}"
  }
]
```

**2. 获取历史数据**
```http
GET /metrics/history?processId={int}&from={datetime}&to={datetime}&aggregation={string}
```

查询参数：
- `processId` (必需): 进程ID
- `from` (可选): 开始时间（ISO 8601格式）
- `to` (可选): 结束时间
- `aggregation` (可选): `raw`(默认) | `minute` | `hour` | `day`

响应示例（聚合数据）：
```json
[
  {
    "id": 1,
    "processId": 5678,
    "processName": "python",
    "aggregationLevel": 1,
    "timestamp": "2025-12-21T10:30:00Z",
    "metricsJson": "{\"cpu\":{\"min\":10.0,\"max\":20.0,\"avg\":15.0,\"sum\":900.0,\"count\":60,\"unit\":\"%\"}}"
  }
]
```

**3. 获取进程列表**
```http
GET /metrics/processes?from={datetime}&to={datetime}&keyword={string}
```

查询参数（可选）：
- `from`: 开始时间
- `to`: 结束时间
- `keyword`: 关键词过滤

响应示例：
```json
[
  {
    "processId": 5678,
    "processName": "python",
    "lastSeen": "2025-12-21T10:30:00Z",
    "recordCount": 120
  }
]
```

**4. 获取聚合数据**
```http
GET /metrics/aggregations?from={datetime}&to={datetime}&aggregation={string}
```

查询参数：
- `from` (必需): 开始时间
- `to` (必需): 结束时间
- `aggregation` (可选): `minute`(默认) | `hour` | `day`

#### Config API

**1. 获取指标元数据** ⭐ 新增
```http
GET /config/metrics
```

返回所有已注册的指标提供者信息，用于前端动态渲染。

响应示例：
```json
[
  {
    "metricId": "cpu",
    "displayName": "CPU Usage",
    "unit": "%",
    "type": "Percentage",
    "category": "Percentage",
    "color": "#3b82f6",
    "icon": "Cpu"
  },
  {
    "metricId": "memory",
    "displayName": "Memory Usage",
    "unit": "MB",
    "type": "Size",
    "category": "Size",
    "color": "#10b981",
    "icon": "MemoryStick"
  }
]
```

**字段说明**：
- `metricId`: 指标唯一标识（如 cpu, memory, gpu, vram）
- `displayName`: 显示名称（支持国际化映射）
- `unit`: 单位（%, MB, GB, °C等）
- `type`: 指标类型（Percentage, Size, Gauge等）
- `color`: 前端显示颜色（十六进制）
- `icon`: Lucide图标名称

**2. 获取配置**
```http
GET /config
```

响应示例：
```json
{
  "monitor": {
    "intervalSeconds": 5,
    "keywords": ["python", "node", "docker"]
  },
  "metricProviders": {
    "pluginDirectory": ""
  }
}
```

**2. 获取告警配置**
```http
GET /config/alerts
```

响应示例：
```json
[
  {
    "id": 1,
    "metricId": "cpu",
    "threshold": 90.0,
    "isEnabled": true,
    "createdAt": "2024-01-01T00:00:00Z",
    "updatedAt": "2024-01-01T00:00:00Z"
  }
]
```

**3. 更新告警配置**
```http
POST /config/alerts
Content-Type: application/json

{
  "id": 1,
  "metricId": "cpu",
  "threshold": 85.0,
  "isEnabled": true
}
```

**4. 删除告警配置**
```http
DELETE /config/alerts/{id}
```

**5. 健康检查**
```http
GET /config/health
```

响应示例：
```json
{
  "status": "Healthy",
  "timestamp": "2025-12-21T10:30:00Z",
  "database": "Connected"
}
```

### SignalR Hub

#### 连接信息
- **Hub URL**: `http://localhost:35179/hubs/metrics`
- **协议**: WebSocket (自动降级到Server-Sent Events或Long Polling)

#### 事件

**1. metrics.latest**

每5秒推送一次最新指标数据。

事件数据格式：
```json
{
  "timestamp": "2025-12-21T10:30:00Z",
  "processCount": 42,
  "processes": [
    {
      "processId": 5678,
      "processName": "python",
      "commandLine": "python app.py",
      "metrics": {
        "cpu": {
          "value": 15.2,
          "unit": "%",
          "displayName": "CPU Usage",
          "timestamp": "2025-12-21T10:30:00Z"
        },
        "memory": {
          "value": 256.5,
          "unit": "MB",
          "displayName": "Memory Usage",
          "timestamp": "2025-12-21T10:30:00Z"
        }
      }
    }
  ]
}
```

#### JavaScript客户端示例

```javascript
import * as signalR from "@microsoft/signalr";

const connection = new signalR.HubConnectionBuilder()
  .withUrl("http://localhost:35179/hubs/metrics")
  .withAutomaticReconnect()
  .build();

connection.on("metrics.latest", (data) => {
  console.log(`Received ${data.processCount} processes`);
  data.processes.forEach(p => {
    console.log(`${p.processName}: CPU=${p.metrics.cpu.value}%`);
  });
});

await connection.start();
console.log("Connected to XhMonitor");
```

## 配置说明

### appsettings.json

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information",
      "Microsoft.EntityFrameworkCore": "Warning",
      "XhMonitor": "Debug"
    }
  },
  "ConnectionStrings": {
    "DatabaseConnection": "Data Source=xhmonitor.db"
  },
  "Monitor": {
    "IntervalSeconds": 5,
    "SystemUsageIntervalSeconds": 1,
    "Keywords": ["python", "node", "docker"]
  },
  "MetricProviders": {
    "PluginDirectory": "",
    "PreferLibreHardwareMonitor": true
  }
}
```

**配置项说明**：

### Configuration Management

XhMonitor 的配置分为两类来源：

- `appsettings.json`：基础设施/部署/系统级配置（通常需要重启服务生效）
- 数据库 `ApplicationSettings`：用户运行时偏好（可由设置界面修改，通常无需重启）

> 说明：端口、连接字符串、采集间隔等属于基础设施/系统级配置；UI 外观、筛选关键词、展示偏好等属于用户偏好。

#### 端口发现与回退

Desktop 端通过 `service-endpoints.json` 读取 API/SignalR 地址（默认 `http://localhost:35179`）。当默认端口被占用时，`ServiceDiscovery` 会尝试在 `+1 ~ +10` 范围内寻找可用端口并自动回退。Web 前端默认端口为 `35180`，同样支持自动回退。

如需固定端口，请更新 `service-endpoints.json` 并确保端口未被占用。

### Error Handling

对于**可预期的失败**（例如配置缺失、输入校验失败、网络不可达），优先使用 `Result<T, TError>` 进行结果返回，避免用异常作为流程控制。对于**不可预期的错误**（例如程序缺陷、环境异常），继续使用异常并记录日志。

示例：
```csharp
var result = await _viewModel.LoadSettingsAsync();
if (result.IsFailure)
{
    MessageBox.Show(result.Error, "错误");
    return;
}
```

#### `appsettings.json`（服务端）常见配置

- `Server:Host`, `Server:Port`, `Server:HubPath`
- `ConnectionStrings:DatabaseConnection`
- `Monitor:IntervalSeconds`, `Monitor:SystemUsageIntervalSeconds`, `Monitor:Keywords`
- `MetricProviders:PluginDirectory`, `MetricProviders:PreferLibreHardwareMonitor`
- `Database:RetentionDays`, `Database:CleanupIntervalHours`

#### 数据库 `ApplicationSettings`（用户偏好）常见配置

- `Appearance`: `ThemeColor`, `Opacity`
- `DataCollection`: `ProcessKeywords`, `TopProcessCount`, `DataRetentionDays`
- `System`: `StartWithWindows`

#### 配置位置速查表

| Setting | Location | Rationale |
| --- | --- | --- |
| `Server:Host` | `appsettings.json` | 基础设施配置，影响服务绑定地址 |
| `Server:Port` | `appsettings.json` | 基础设施配置，影响服务端口；需重启 |
| `Server:HubPath` | `appsettings.json` | 基础设施配置，影响 Hub 路由；需重启 |
| `ConnectionStrings:DatabaseConnection` | `appsettings.json` | 部署配置/敏感信息，不应由 UI 修改 |
| `Monitor:IntervalSeconds` | `appsettings.json` | 系统级采集节奏；需重启以保证一致性 |
| `Monitor:SystemUsageIntervalSeconds` | `appsettings.json` | 系统级采集节奏；需重启以保证一致性 |
| `Monitor:Keywords` | `appsettings.json` | 系统级筛选规则；通常随部署调整 |
| `MetricProviders:PluginDirectory` | `appsettings.json` | 部署路径配置；需重启 |
| `MetricProviders:PreferLibreHardwareMonitor` | `appsettings.json` | 系统级采集策略；需重启 |
| `Database:RetentionDays` | `appsettings.json` | 系统级数据保留策略；需重启 |
| `Database:CleanupIntervalHours` | `appsettings.json` | 系统级后台任务调度；需重启 |
| `Appearance.ThemeColor` | 数据库 `ApplicationSettings` | 用户外观偏好；运行时可修改 |
| `Appearance.Opacity` | 数据库 `ApplicationSettings` | 用户外观偏好；运行时可修改 |
| `DataCollection.ProcessKeywords` | 数据库 `ApplicationSettings` | 用户筛选偏好；运行时可修改 |
| `DataCollection.TopProcessCount` | 数据库 `ApplicationSettings` | 用户展示偏好；运行时可修改 |
| `DataCollection.DataRetentionDays` | 数据库 `ApplicationSettings` | 用户数据偏好；运行时可修改 |
| `System.StartWithWindows` | 数据库 `ApplicationSettings` | 用户系统偏好；运行时可修改 |

更多边界规则与迁移策略参考：`XhMonitor.Service/docs/configuration-boundaries.md`

- `Monitor:IntervalSeconds`: 进程采集间隔（秒）
- `Monitor:SystemUsageIntervalSeconds`: 系统使用率采集间隔（秒）
- `Monitor:Keywords`: 进程过滤关键词数组
- `MetricProviders:PluginDirectory`: 自定义指标插件目录
- `MetricProviders:PreferLibreHardwareMonitor`: 是否优先使用 LibreHardwareMonitor 混合架构
  - `true`（默认）：系统级指标使用 LibreHardwareMonitor（需管理员权限），进程级指标使用 PerformanceCounter
  - `false`：所有指标使用传统 PerformanceCounter
  - **注意**：无管理员权限时自动回退到 PerformanceCounter，无需手动配置

### 数据库

**位置**: `XhMonitor.Service/xhmonitor.db`

**表结构**:
- `ProcessMetricRecords`: 原始指标数据
- `AggregatedMetricRecords`: 聚合数据（分钟/小时/天）
- `AlertConfigurations`: 告警配置

**数据保留建议**:
- 原始数据：7天
- 分钟聚合：30天
- 小时聚合：90天
- 天聚合：永久

## 开发指南

### 项目结构

```
xhMonitor/
├── XhMonitor.Core/              # 核心库
│   ├── Entities/                # EF Core实体
│   ├── Enums/                   # 枚举定义
│   ├── Interfaces/              # 接口定义
│   ├── Models/                  # 数据模型
│   └── Providers/               # 内置指标提供者
│       ├── CpuMetricProvider.cs
│       ├── MemoryMetricProvider.cs
│       ├── GpuMetricProvider.cs
│       └── VramMetricProvider.cs
├── XhMonitor.Service/           # 主服务
│   ├── Controllers/             # API控制器
│   │   ├── MetricsController.cs
│   │   └── ConfigController.cs
│   ├── Core/                    # 核心逻辑
│   │   ├── ProcessMonitor.cs
│   │   └── ProcessScanner.cs
│   ├── Data/                    # 数据访问
│   │   ├── AppDbContext.cs
│   │   └── MetricsRepository.cs
│   ├── Hubs/                    # SignalR Hub
│   │   └── MetricsHub.cs
│   ├── Workers/                 # 后台服务
│   │   ├── Worker.cs
│   │   └── AggregationWorker.cs
│   ├── appsettings.json         # 配置文件
│   └── xhmonitor.db             # SQLite数据库
├── xhmonitor-web/               # 前端项目
│   ├── src/
│   │   ├── components/          # React组件
│   │   │   ├── SystemSummary.tsx
│   │   │   ├── ProcessList.tsx
│   │   │   └── MetricChart.tsx
│   │   ├── hooks/               # 自定义Hooks
│   │   │   └── useMetricsHub.ts
│   │   ├── i18n.ts              # 国际化配置
│   │   ├── types.ts             # TypeScript类型定义
│   │   ├── utils.ts             # 工具函数
│   │   ├── App.tsx              # 主应用组件
│   │   └── main.tsx             # 入口文件
│   ├── public/                  # 静态资源
│   ├── package.json             # 依赖配置
│   ├── vite.config.ts           # Vite配置
│   ├── tailwind.config.js       # TailwindCSS配置
│   └── I18N.md                  # 国际化说明文档
├── KNOWN_LIMITATIONS.md         # 已知限制文档
└── README.md                    # 项目文档
```

### 添加自定义指标

#### 后端实现

实现`IMetricProvider`接口：

```csharp
public class CustomMetricProvider : IMetricProvider
{
    public string MetricId => "custom_metric";
    public string DisplayName => "Custom Metric";
    public string Unit => "units";
    public MetricType Type => MetricType.Gauge;

    public bool IsSupported() => true;

    public async Task<MetricValue> CollectAsync(int processId)
    {
        // 实现指标采集逻辑
        var value = await GetCustomMetricAsync(processId);

        return new MetricValue
        {
            Value = value,
            Unit = Unit,
            DisplayName = DisplayName,
            Timestamp = DateTime.UtcNow
        };
    }

    public void Dispose() { }
}
```

**编码规范**：对有依赖注入参数的 Provider，优先使用 C# 12 primary constructor，例如：

```csharp
public class CustomMetricProvider(ILogger<CustomMetricProvider>? logger = null) : IMetricProvider
{
    // ...
}
```

#### 前端国际化

在 `xhmonitor-web/src/i18n.ts` 中添加翻译：

```typescript
export const i18n = {
  zh: {
    'Custom Metric': '自定义指标',
  },
  en: {
    'Custom Metric': 'Custom Metric',
  },
};
```

前端会自动通过 `/api/v1/config/metrics` 获取指标元数据并渲染，无需修改组件代码。

### 前端开发

#### 启动开发服务器

```bash
cd xhmonitor-web
npm install
npm run dev
```

#### 添加新组件

在 `src/components/` 目录下创建新组件：

```typescript
import { t } from '../i18n';

export const MyComponent = () => {
  return (
    <div className="glass rounded-xl p-6">
      <h2 className="text-2xl font-bold">{t('My Component')}</h2>
      {/* 组件内容 */}
    </div>
  );
};
```

#### 使用SignalR连接

```typescript
import { useMetricsHub } from './hooks/useMetricsHub';

export const MyComponent = () => {
  const { metricsData, connectionStatus } = useMetricsHub();

  // 使用实时数据
  return <div>{connectionStatus}</div>;
};
```

#### 构建生产版本

```bash
npm run build
# 输出到 dist/ 目录
```

### 运行测试

```bash
# 单元测试
dotnet test

# 集成测试
dotnet test --filter Category=Integration
```

### 构建发布

```bash
# 发布为单文件可执行程序
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# 输出目录
# XhMonitor.Service/bin/Release/net8.0/win-x64/publish/
```

## 性能指标

**当前测试环境**:
- 监控进程数：141
- 采集间隔：5秒
- 首次周期：102秒（含缓存构建）
- 后续周期：8-9秒
- CPU占用：<5%
- 内存占用：~50MB

**优化建议**:
- 使用进程关键词过滤减少监控数量
- 调整采集间隔（5-10秒）
- 定期清理历史数据

## 已知限制

详见 [KNOWN_LIMITATIONS.md](./KNOWN_LIMITATIONS.md)

**主要限制**:
1. MaxDegreeOfParallelism=1（串行收集）
2. PerformanceCounter同步阻塞
3. 2秒provider超时可能过严
4. 无数据重试机制

**计划优化**:
- 替换为WMI异步API
- 实现数据重试队列
- 配置化硬编码参数

## 当前状态

### 已完成阶段

- ✅ **阶段1**: 核心架构搭建
- ✅ **阶段2**: 监控核心实现
- ✅ **阶段3**: 数据持久化与聚合
- ✅ **阶段4**: Web API + SignalR
- ✅ **阶段5**: Web前端开发（React + TypeScript）
  - ✅ 实时数据展示
  - ✅ 进程列表与搜索
  - ✅ 动态图表渲染
  - ✅ 国际化支持（中英文）
  - ✅ Glassmorphism UI设计

### 进行中

- 🚧 **阶段6**: Electron桌面端

### 待开发

- ⏳ **阶段7**: 测试与优化
- ⏳ **阶段8**: 部署与文档

#### 小功能点

- 桌面端：进程详情，双击悬浮卡片进程名称可以查看进程详情。
- 桌面端：可以管理进程，鼠标悬浮在对应进程行上，进程最后会有一个关闭按点击后强制结束进程，需二次确认。
- 整体：新增网速监控（集成），功耗监控（插件）

## 贡献指南

欢迎提交Issue和Pull Request！

### 开发流程

1. Fork本仓库
2. 创建特性分支 (`git checkout -b feature/AmazingFeature`)
3. 提交更改 (`git commit -m 'Add some AmazingFeature'`)
4. 推送到分支 (`git push origin feature/AmazingFeature`)
5. 开启Pull Request

### 代码规范

- 遵循C# Coding Conventions
- 使用有意义的变量和方法名
- 添加必要的注释（非必要不添加）
- 保持代码简洁高效

## 许可证

[MIT License](LICENSE)

## 联系方式

- 项目地址：<repository-url>
- Issue追踪：<repository-url>/issues

## 更新日志

### v0.5.0 (2025-12-21)
- ✨ 完成Web前端开发（React 19 + TypeScript）
- ✨ 实现实时数据展示和SignalR连接
- ✨ 添加进程列表、搜索和排序功能
- ✨ 集成ECharts动态图表
- ✨ 实现国际化支持（中英文切换）
- 🎨 采用Glassmorphism毛玻璃UI设计
- ✨ 支持动态指标扩展（零前端代码修改）
- 📝 添加前端国际化文档（I18N.md）

### v0.4.0 (2025-12-21)
- ✨ 新增Web API和SignalR支持
- ✨ 实现REST API查询接口
- ✨ 实现实时数据推送
- 🐛 修复CpuMetricProvider线程安全问题
- ⚡ 优化GetInstanceName为O(1)查找

### v0.3.0 (2025-12-21)
- ✨ 实现数据聚合功能（分钟/小时/天）
- ✨ 新增AggregationWorker后台服务
- 📝 记录已知限制文档

### v0.2.0 (2025-12-21)
- ✨ 实现Repository模式
- ✨ 集成EF Core和SQLite
- 🐛 修复嵌套并行导致的死锁

### v0.1.0 (2025-12-20)
- 🎉 初始版本
- ✨ 实现核心监控功能
- ✨ 支持CPU、内存、GPU、显存监控
