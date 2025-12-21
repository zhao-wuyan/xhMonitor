# xhMonitor - Windows资源监视器实施计划

## 项目概述

**目标**：开发一个Windows资源监视器，监控进程的CPU、内存、GPU、显存等资源占用情况，支持根据启动命令关键字过滤进程，提供Web界面和桌面悬浮窗两种访问方式。

**技术栈**：
- 后端：C# .NET 8 + ASP.NET Core + SignalR + EF Core + SQLite
- Web前端：React 18 + TypeScript + TailwindCSS + ECharts
- 桌面端：Electron + React
- 监控技术：PDH (Performance Data Helper) + NtQueryInformationProcess

---

## 核心架构

```
Windows Service (监控核心)
├─ 进程扫描 (NtQueryInformationProcess)
├─ 性能监控 (PDH)
├─ 插件化维度系统 (IMetricProvider)
├─ 自定义动作系统 (IMetricAction)
├─ 数据聚合 (SQLite + JSON存储)
└─ Web API + SignalR

        ↓ HTTP/WebSocket

Web界面 (React)          桌面悬浮窗 (Electron)
├─ 实时数据展示         ├─ 多进程列表
├─ 历史趋势图表         ├─ 数字/图表双模式
├─ 配置管理             ├─ 悬浮置顶
└─ 告警中心             └─ 快速操作
```

---

## 实施计划

### 阶段1：核心架构搭建
- 创建解决方案结构
- 定义核心接口 (IMetricProvider, IMetricAction)
- 定义数据模型
- 安装NuGet依赖

### 阶段2：监控核心实现
- 实现 NtQueryInformationProcess (获取进程命令行)
- 实现内置监控提供者 (CPU, Memory, GPU, VRAM)
- 实现监控提供者注册表
- 实现进程扫描器
- 实现性能监控协调器
- 实现主监控服务 (BackgroundService)

### 阶段3：数据持久化
- 定义EF Core实体
- 创建DbContext
- 实现数据仓储
- 配置数据库迁移

### 阶段4：Web API + SignalR
- 配置ASP.NET Core
- 实现REST API (MetricsController, ConfigController, ActionsController)
- 实现SignalR Hub (实时推送)

### 阶段5：Web前端开发
- 创建React项目
- 配置SignalR客户端
- 实现核心组件 (ProcessList, MetricChart, SystemSummary, ConfigPanel)
- 实现自定义Hooks

### 阶段6：Electron桌面端
- 创建Electron项目
- 实现主进程
- 实现悬浮窗UI
- 实现数字/图表双模式

### 阶段7：插件系统
- 实现插件加载器
- 创建示例插件
- 实现动作执行器

### 阶段8：测试和优化
- 单元测试
- 性能测试
- 集成测试

---

## 关键技术点

### 1. 插件化监控维度
```csharp
public interface IMetricProvider
{
    string MetricId { get; }
    string DisplayName { get; }
    string Unit { get; }
    Task<MetricValue> CollectAsync(int processId);
    bool IsSupported();
}
```

### 2. JSON存储 (支持动态维度)
```json
{
  "cpu": {"value": 85.5, "unit": "%"},
  "memory": {"value": 2048, "unit": "MB"},
  "gpu": {"value": 62.3, "unit": "%"},
  "vram": {"value": 1400, "unit": "MB"},
  "custom_metric": {"value": 123, "unit": ""}
}
```

### 3. 自定义动作系统
```csharp
public interface IMetricAction
{
    string ActionId { get; }
    Task<ActionResult> ExecuteAsync(int processId, string metricId);
}
```

---

## 配置示例

```json
{
  "Monitoring": {
    "RefreshIntervalSeconds": 3,
    "EnabledMetrics": ["cpu", "memory", "gpu", "vram"],
    "KeywordMatching": {
      "IgnoreCase": true,
      "Keywords": ["python", "node", "java"]
    }
  },
  "Alerts": {
    "CpuThreshold": 90,
    "MemoryThreshold": 90,
    "GpuThreshold": 90,
    "VramThreshold": 90
  },
  "DataRetention": {
    "RawDataHours": 24,
    "MinuteDataDays": 7,
    "HourDataDays": 30,
    "DayDataDays": -1
  }
}
```

---

## 验收标准

- ✅ 能够监控包含指定关键字的进程
- ✅ 准确获取CPU、内存、GPU、显存数据
- ✅ 数据实时推送到Web和桌面端
- ✅ 历史数据正确保存和查询
- ✅ 告警机制正常工作
- ✅ 支持添加自定义监控维度（插件）
- ✅ 支持自定义动作
- ✅ 悬浮窗支持数字/图表双模式
- ✅ 性能指标达标（CPU占用<5%）

---

生成时间：2025-12-20
