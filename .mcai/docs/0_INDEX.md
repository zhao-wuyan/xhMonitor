# XhMonitor 文档

XhMonitor 是一个高性能的 Windows 进程资源监控系统，提供实时采集、聚合分析和可视化展示。本文档涵盖系统架构、接口定义、开发指南、核心概念和模块说明，帮助开发者和使用者快速理解和扩展系统。

**快速链接**: [架构](./1_ARCHITECTURE.md) | [接口](./2_INTERFACES.md) | [开发者指南](./3_DEVELOPER_GUIDE.md)

---

## 核心文档

### [架构](./1_ARCHITECTURE.md)
系统设计、技术栈、组件结构和数据流程。从这里开始了解系统如何运作。

### [接口](./2_INTERFACES.md)
公开 API、SignalR Hub、REST API 和库接口。集成或使用此系统的参考。

### [开发者指南](./3_DEVELOPER_GUIDE.md)
环境搭建、开发工作流、编码规范和常见任务。贡献者必读。

---

## 模块

| 模块 | 描述 | README |
|------|------|--------|
| `XhMonitor.Service/Core` | 后端核心业务逻辑，负责监控协调 | [README](./模块/XhMonitor.Service.Core.md) |
| `xhmonitor-web` | React 前端应用，提供可视化界面 | [README](./模块/xhmonitor-web.md) |
| `XhMonitor.Core` | 核心库，定义接口和实体 | - |
| `XhMonitor.Service/Controllers` | REST API 控制器 | - |
| `XhMonitor.Service/Hubs` | SignalR Hub 实时通信 | - |
| `XhMonitor.Service/Data` | 数据访问层和 EF Core | - |
| `XhMonitor.Service/Workers` | 后台任务（聚合、采集） | - |

---

## 核心概念

理解这些领域概念有助于导航代码库：

| 概念 | 描述 |
|------|------|
| [MetricProvider](./专有概念/MetricProvider.md) | 指标采集器接口，定义如何从系统采集数据 |
| [ProcessMetrics](./专有概念/ProcessMetrics.md) | 进程指标数据结构，包含所有指标值 |
| [AggregationWorker](./专有概念/AggregationWorker.md) | 数据聚合后台服务，生成分层统计数据 |

---

## 入门指南

### 项目新人？

按此路径学习：
1. **[架构](./1_ARCHITECTURE.md)** - 了解全局
2. **[核心概念](#核心概念)** - 学习领域术语
3. **[开发者指南](./3_DEVELOPER_GUIDE.md)** - 搭建环境
4. **[接口](./2_INTERFACES.md)** - 探索公开 API

### 需要集成？

1. **[接口](./2_INTERFACES.md)** - API 契约和认证
2. **[架构](./1_ARCHITECTURE.md)** - 系统边界和数据流

### 首次贡献？

1. **[开发者指南](./3_DEVELOPER_GUIDE.md)** - 搭建和工作流
2. **[安全起步点](./3_DEVELOPER_GUIDE.md#修改建议区域)** - 低风险区域
3. **[常见任务](./3_DEVELOPER_GUIDE.md#常见任务)** - 分步指南

---

## 快速参考

### 命令

```bash
# 后端
cd XhMonitor.Service
dotnet run              # 启动服务
dotnet build            # 编译项目
dotnet test             # 运行测试

# 前端
cd xhmonitor-web
npm install             # 安装依赖
npm run dev             # 启动开发服务器
npm run build           # 构建生产版本
npm run lint            # 代码检查
```

### 重要文件

| 文件 | 目的 |
|------|------|
| `XhMonitor.Service/Program.cs` | 服务启动和依赖注入配置 |
| `XhMonitor.Service/Worker.cs` | 主监控循环入口 |
| `XhMonitor.Service/Core/PerformanceMonitor.cs` | 监控核心协调器 |
| `xhmonitor-web/src/main.tsx` | React 应用入口 |
| `xhmonitor-web/src/App.tsx` | 主应用组件 |

### 端点

| 类型 | 端点 |
|------|------|
| REST API | `http://localhost:35179/api/v1` |
| SignalR Hub | `http://localhost:35179/hubs/metrics` |
| 前端开发服务器 | `http://localhost:5173` |

### 关键类

| 类 | 位置 | 描述 |
|------|------|------|
| `PerformanceMonitor` | `XhMonitor.Service/Core` | 监控核心引擎 |
| `MetricProviderRegistry` | `XhMonitor.Service/Core` | 指标提供者管理 |
| `AggregationWorker` | `XhMonitor.Service/Workers` | 数据聚合服务 |
| `MetricsHub` | `XhMonitor.Service/Hubs` | SignalR 实时推送 |

---

## 项目概览

### 系统架构

XhMonitor 采用分层架构设计：

- **采集层**: PerformanceMonitor 协调进程扫描和指标采集
- **存储层**: SQLite 数据库 + EF Core，支持分层聚合存储
- **服务层**: REST API 和 SignalR Hub
- **展示层**: React 前端应用，ECharts 可视化

### 技术栈

| 类别 | 技术 |
|------|------|
| 后端框架 | .NET 8 + ASP.NET Core |
| 前端框架 | React 19 + TypeScript |
| 数据库 | SQLite + EF Core 8 |
| 实时通信 | SignalR |
| 可视化 | ECharts 6 |
| 样式 | TailwindCSS v4 |

### 核心功能

- ✅ 实时监控 Windows 进程资源（CPU、内存、GPU、显存）
- ✅ 分层聚合存储（原始数据、分钟级、小时级、天级）
- ✅ 配置驱动的指标扩展，无需修改前端代码
- ✅ SignalR 实时推送
- ✅ REST API 历史数据查询
- ✅ Glassmorphism 现代化 UI
- ✅ 国际化支持（中英文）

---

## 扩展开发

### 添加新指标

1. 实现 `IMetricProvider` 接口
2. 在 `MetricProviderRegistry` 中注册
3. 前端自动发现，无需修改 UI 代码

详见: [MetricProvider 文档](./专有概念/MetricProvider.md)

### 添加新聚合级别

1. 修改 `AggregationLevel` 枚举
2. 在 `AggregationWorker` 中添加聚合逻辑
3. 更新数据库迁移

详见: [AggregationWorker 文档](./专有概念/AggregationWorker.md)

### 添加前端组件

1. 创建组件到 `src/components/`
2. 使用 `useMetricsHub` Hook 获取实时数据
3. 使用 ECharts 渲染图表

详见: [xhmonitor-web 文档](./模块/xhmonitor-web.md)

---

## 故障排查

### 常见问题

| 问题 | 解决方案 |
|------|----------|
| SignalR 连接失败 | 检查后端服务是否运行在 `http://localhost:35179` |
| 指标采集超时 | 查看 [开发者指南](./3_DEVELOPER_GUIDE.md#故障排查) |
| 数据库连接失败 | 检查连接字符串和迁移状态 |
| 前端构建失败 | 重新安装依赖：`rm -rf node_modules package-lock.json && npm install` |

### 调试技巧

- **后端**: 使用 `appsettings.Development.json` 启用详细日志
- **前端**: 使用浏览器开发者工具和 React DevTools
- **数据库**: 使用 SQLite 工具查看数据

---

## 贡献

欢迎贡献！详见 [开发者指南](./3_DEVELOPER_GUIDE.md) 了解开发流程和规范。

### 开发流程

1. Fork 仓库
2. 创建特性分支 (`git checkout -b feature/AmazingFeature`)
3. 编写代码和测试
4. 提交更改 (`git commit -m 'Add some AmazingFeature'`)
5. 推送到分支 (`git push origin feature/AmazingFeature`)
6. 开启 Pull Request

### 代码规范

- C#: 遵循 C# Coding Conventions
- TypeScript: 遵循 ESLint 配置
- 提交信息: 使用约定式提交 (Conventional Commits)

---

## 许可证

MIT License

---

## 文档版本

- **最后更新**: 2025-01-09
- **项目版本**: v0.5.0

---

## 相关资源

- [项目 README](../../README.md)
- [已知限制](../../KNOWN_LIMITATIONS.md)
