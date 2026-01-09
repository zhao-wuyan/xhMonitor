# 接口定义

## 接口类型

本仓库暴露以下接口：

| 类型 | 存在 | 位置 |
|------|------|------|
| HTTP/REST API | 是 | `XhMonitor.Service/Controllers/` |
| SignalR Hub | 是 | `XhMonitor.Service/Hubs/MetricsHub.cs` |
| 库接口 | 是 | `XhMonitor.Core/Interfaces/` |

## HTTP API

### 基础信息

**Base URL**: `http://localhost:35179/api/v1` 
**CORS**: 支持 `http://localhost:5173`, `http://localhost:35180`, `app://.`

### 健康检查

#### `GET /config/health`

**描述**: 检查服务健康状态和数据库连接
**认证**: 公开

**请求**: 无

**响应** (`200 OK`):
```json
{
  "status": "Healthy",
  "timestamp": "2025-01-09T12:00:00Z",
  "database": "Connected"
}
```

**错误**:
- `503` - 数据库连接失败

---

### 配置管理

#### `GET /config`

**描述**: 获取监控服务配置
**认证**: 公开

**请求**: 无

**响应** (`200 OK`):
```json
{
  "monitor": {
    "intervalSeconds": 5,
    "keywords": ["chrome", "python", "node"]
  },
  "metricProviders": {
    "pluginDirectory": "plugins"
  }
}
```

---

#### `GET /config/metrics`

**描述**: 获取所有可用指标的元数据
**认证**: 公开

**响应** (`200 OK`):
```json
[
  {
    "metricId": "cpu",
    "displayName": "CPU Usage",
    "unit": "%",
    "type": "Gauge",
    "category": "Gauge",
    "color": "#3b82f6",
    "icon": "Cpu"
  },
  {
    "metricId": "memory",
    "displayName": "Memory",
    "unit": "MB",
    "type": "Gauge",
    "category": "Gauge",
    "color": "#10b981",
    "icon": "MemoryStick"
  }
]
```

---

### 告警配置

#### `GET /config/alerts`

**描述**: 获取所有告警配置
**认证**: 公开

**请求**: 无

**响应** (`200 OK`):
```json
[
  {
    "id": 1,
    "metricId": "cpu",
    "threshold": 80.0,
    "operator": "GreaterThan",
    "isEnabled": true,
    "createdAt": "2025-01-09T10:00:00Z",
    "updatedAt": "2025-01-09T10:00:00Z"
  }
]
```

---

#### `POST /config/alerts`

**描述**: 创建或更新告警配置
**认证**: 公开

**请求**:
```json
{
  "id": 0,
  "metricId": "cpu",
  "threshold": 90.0,
  "operator": "GreaterThan",
  "isEnabled": true
}
```

**响应** (`200 OK`): 返回创建或更新的告警配置

**错误**:
- `400` - 无效的请求体

---

#### `DELETE /config/alerts/{id}`

**描述**: 删除告警配置
**认证**: 公开

**请求参数**:
- `id` (path) - 告警 ID

**响应** (`204 No Content`)

**错误**:
- `404` - 告警配置不存在

---

### 指标查询

#### `GET /metrics/latest`

**描述**: 获取最新的指标数据
**认证**: 公开

**查询参数**:
- `processId` (optional) - 按进程 ID 过滤
- `processName` (optional) - 按进程名过滤（模糊匹配）
- `keyword` (optional) - 按关键词过滤（进程名或命令行）

**响应** (`200 OK`):
```json
[
  {
    "id": 12345,
    "processId": 1234,
    "processName": "chrome.exe",
    "commandLine": "C:\\Program Files\\Chrome\\chrome.exe",
    "metricId": "cpu",
    "metricValue": 45.5,
    "metricUnit": "%",
    "timestamp": "2025-01-09T12:00:00Z"
  }
]
```

---

#### `GET /metrics/history`

**描述**: 获取指定进程的历史指标数据
**认证**: 公开

**查询参数**:
- `processId` (required) - 进程 ID
- `from` (optional) - 开始时间（ISO 8601）
- `to` (optional) - 结束时间（ISO 8601）
- `aggregation` (optional) - 聚合级别：`raw`, `minute`, `hour`, `day`（默认: `raw`）

**响应** (`200 OK`):
```json
[
  {
    "id": 12345,
    "processId": 1234,
    "processName": "chrome.exe",
    "metricId": "cpu",
    "sum": 450.0,
    "count": 10,
    "avg": 45.0,
    "min": 30.0,
    "max": 60.0,
    "timestamp": "2025-01-09T12:00:00Z"
  }
]
```

**错误**:
- `400` - 无效的参数

---

#### `GET /metrics/processes`

**描述**: 获取进程列表
**认证**: 公开

**查询参数**:
- `from` (optional) - 开始时间（ISO 8601）
- `to` (optional) - 结束时间（ISO 8601）
- `keyword` (optional) - 按关键词过滤

**响应** (`200 OK`):
```json
[
  {
    "processId": 1234,
    "processName": "chrome.exe",
    "lastSeen": "2025-01-09T12:00:00Z",
    "recordCount": 100
  }
]
```

---

#### `GET /metrics/aggregations`

**描述**: 获取聚合指标数据
**认证**: 公开

**查询参数**:
- `from` (required) - 开始时间（ISO 8601）
- `to` (required) - 结束时间（ISO 8601）
- `aggregation` (optional) - 聚合级别：`minute`, `hour`, `day`（默认: `minute`）

**响应** (`200 OK`):
```json
[
  {
    "id": 12345,
    "processId": 1234,
    "processName": "chrome.exe",
    "metricId": "cpu",
    "aggregationLevel": "Minute",
    "sum": 450.0,
    "count": 10,
    "avg": 45.0,
    "min": 30.0,
    "max": 60.0,
    "timestamp": "2025-01-09T12:00:00Z"
  }
]
```

---

## SignalR Hub

### 连接

**Hub URL**: `http://localhost:35179/hubs/metrics`

### 消息

#### BroadcastMetrics

**描述**: 广播最新的指标数据到所有连接的客户端

**触发**: 每次完成指标采集周期后

**负载**:
```json
[
  {
    "processId": 1234,
    "processName": "chrome.exe",
    "metrics": {
      "cpu": { "value": 45.5, "unit": "%", "timestamp": "2025-01-09T12:00:00Z" },
      "memory": { "value": 1024.5, "unit": "MB", "timestamp": "2025-01-09T12:00:00Z" }
    }
  }
]
```

---

## 库接口

### IMetricProvider

**位置**: `XhMonitor.Core/Interfaces/IMetricProvider.cs`

**描述**: 定义指标采集器的接口

```csharp
public interface IMetricProvider : IDisposable
{
    /// <summary>
    /// 指标的唯一标识符
    /// </summary>
    string MetricId { get; }

    /// <summary>
    /// 指标的显示名称
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// 指标的单位
    /// </summary>
    string Unit { get; }

    /// <summary>
    /// 指标的类型（Gauge, Counter, etc.）
    /// </summary>
    MetricType Type { get; }

    /// <summary>
    /// 检查此指标提供者是否受支持
    /// </summary>
    bool IsSupported();

    /// <summary>
    /// 异步采集指标数据
    /// </summary>
    /// <param name="processId">进程 ID</param>
    /// <returns>指标值</returns>
    Task<MetricValue> CollectAsync(int processId);
}
```

---

### IProcessMetricRepository

**位置**: `XhMonitor.Core/Interfaces/IProcessMetricRepository.cs`

**描述**: 定义指标数据仓库的接口

```csharp
public interface IProcessMetricRepository
{
    /// <summary>
    /// 保存原始指标记录
    /// </summary>
    Task SaveMetricAsync(ProcessMetricRecord record);

    /// <summary>
    /// 批量保存指标记录
    /// </summary>
    Task SaveMetricsAsync(IEnumerable<ProcessMetricRecord> records);

    /// <summary>
    /// 保存聚合指标记录
    /// </summary>
    Task SaveAggregatedMetricAsync(AggregatedMetricRecord record);

    /// <summary>
    /// 查询原始指标记录
    /// </summary>
    Task<List<ProcessMetricRecord>> GetMetricsAsync(
        int processId,
        DateTime? from = null,
        DateTime? to = null);

    /// <summary>
    /// 查询聚合指标记录
    /// </summary>
    Task<List<AggregatedMetricRecord>> GetAggregatedMetricsAsync(
        int processId,
        AggregationLevel level,
        DateTime? from = null,
        DateTime? to = null);

    /// <summary>
    /// 查询最新指标记录
    /// </summary>
    Task<List<ProcessMetricRecord>> GetLatestMetricsAsync(
        string? processName = null,
        string? keyword = null);
}
```

---

### IMetricAction

**位置**: `XhMonitor.Core/Interfaces/IMetricAction.cs`

**描述**: 定义指标动作的接口（用于告警触发等）

```csharp
public interface IMetricAction
{
    /// <summary>
    /// 动作类型
    /// </summary>
    ActionType Type { get; }

    /// <summary>
    /// 执行动作
    /// </summary>
    Task<ActionResult> ExecuteAsync(AlertConfiguration config, ProcessMetrics metrics);
}
```

---

## 接口稳定性

### 稳定（可安全依赖）
- `GET /api/v1/config/metrics` - 指标元数据 API
- `GET /api/v1/metrics/latest` - 最新指标 API
- `GET /api/v1/metrics/history` - 历史指标 API
- `GET /api/v1/metrics/processes` - 进程列表 API
- `IMetricProvider` - 核心指标接口
- `IProcessMetricRepository` - 数据仓库接口
- SignalR Hub `BroadcastMetrics` 消息

### 实验性（可能变更）
- `POST /config/alerts` - 告警配置 API
- `DELETE /config/alerts/{id}` - 告警删除 API
- `IMetricAction` - 动作接口

### 内部（请勿外部使用）
- 所有内部 API 端点
- 数据库模型实体
- 迁移脚本

### 破坏性操作
⚠️ 以下操作不可撤销：
- `DELETE /config/alerts/{id}` - 永久删除告警配置

---

## 前端接口使用示例

### 获取指标配置

```typescript
import { useMetricConfig } from './hooks/useMetricConfig';

const { metrics, isLoading } = useMetricConfig();

metrics.forEach(metric => {
  console.log(metric.metricId, metric.displayName, metric.color);
});
```

### 订阅实时数据

```typescript
import { useMetricsHub } from './hooks/useMetricsHub';

const { metricsData, connectionStatus } = useMetricsHub();

// metricsData 自动更新
metricsData.forEach(data => {
  console.log(data.processName, data.metrics);
});
```

### 查询历史数据

```typescript
const fetchHistory = async (processId: number) => {
  const response = await fetch(
    `http://localhost:35179/api/v1/metrics/history?processId=${processId}&aggregation=minute`
  );
  const data = await response.json();
  return data;
};
```

