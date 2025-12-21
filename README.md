# XhMonitor - Windows资源监视器

一个高性能的Windows进程资源监控系统，支持CPU、内存、GPU、显存等指标的实时采集、聚合分析和Web API访问。

## 功能特性

### 核心功能
- ✅ **进程监控**：基于关键词过滤，监控指定进程的资源占用
- ✅ **多维度指标**：CPU、内存、GPU、显存（支持插件扩展）
- ✅ **数据持久化**：SQLite存储原始数据和聚合数据
- ✅ **分层聚合**：自动生成分钟/小时/天级别统计数据
- ✅ **Web API**：RESTful API查询历史数据
- ✅ **实时推送**：SignalR实时推送最新指标
- ✅ **健康检查**：服务状态和数据库连接监控

### 技术特性
- 🔌 **插件化架构**：IMetricProvider接口支持自定义指标
- 📊 **JSON存储**：灵活的指标数据格式
- ⚡ **高性能**：优化的PID→InstanceName映射（O(1)查找）
- 🔒 **线程安全**：SemaphoreSlim保护共享资源
- 🎯 **精确聚合**：存储Sum/Count支持数学正确的加权平均

## 技术栈

- **后端框架**：.NET 8 + ASP.NET Core
- **数据库**：SQLite + EF Core 8
- **实时通信**：SignalR
- **性能监控**：PerformanceCounter API
- **日志**：Microsoft.Extensions.Logging

## 快速开始

### 环境要求

- Windows 10/11
- .NET 8 SDK
- Visual Studio 2022 或 VS Code

### 安装步骤

1. **克隆仓库**
```bash
git clone <repository-url>
cd xhMonitor
```

2. **还原依赖**
```bash
dotnet restore
```

3. **应用数据库迁移**
```bash
cd XhMonitor.Service
dotnet ef database update
```

4. **配置监控关键词**

编辑 `XhMonitor.Service/appsettings.json`：
```json
{
  "Monitor": {
    "IntervalSeconds": 5,
    "Keywords": ["python", "node", "docker"]
  }
}
```

5. **启动服务**
```bash
dotnet run --project XhMonitor.Service
```

服务将在 `http://localhost:35179` 启动。

### 验证运行

**健康检查**
```bash
curl http://localhost:35179/api/v1/config/health
```

**查询最新指标**
```bash
curl http://localhost:35179/api/v1/metrics/latest
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

**1. 获取配置**
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
    "Keywords": ["python", "node", "docker"]
  },
  "MetricProviders": {
    "PluginDirectory": ""
  }
}
```

**配置项说明**：

- `Monitor:IntervalSeconds`: 数据采集间隔（秒）
- `Monitor:Keywords`: 进程过滤关键词数组
- `MetricProviders:PluginDirectory`: 自定义指标插件目录

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
├── XhMonitor.Service/           # 主服务
│   ├── Controllers/             # API控制器
│   ├── Core/                    # 核心逻辑
│   ├── Data/                    # 数据访问
│   ├── Hubs/                    # SignalR Hub
│   └── Workers/                 # 后台服务
└── KNOWN_LIMITATIONS.md         # 已知限制文档
```

### 添加自定义指标

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

### 进行中

- 🚧 **阶段5**: Web前端开发（React + TypeScript）
- 🚧 **阶段6**: Electron桌面端

### 待开发

- ⏳ **阶段7**: 测试与优化
- ⏳ **阶段8**: 部署与文档

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
