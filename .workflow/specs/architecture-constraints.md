---
title: "Architecture Constraints"
dimension: specs
category: architecture
keywords:
  - architecture
  - desktop
  - service
  - web
  - boundaries
readMode: required
priority: high
---

# Architecture Constraints

## Runtime Boundaries

- `XhMonitor.Core` owns shared models, configuration defaults, and core abstractions.
- `XhMonitor.Service` owns hosted service, HTTP API, persistence, hardware/process metrics, and background workers.
- `XhMonitor.Desktop` owns WPF shell, tray integration, local web server/proxy, desktop settings UI, and desktop-only OS integration.
- `xhmonitor-web` owns the browser UI and must communicate through configured API/SignalR endpoints instead of hardcoded service URLs.

## Configuration

- Shared defaults live in `ConfigurationDefaults` where practical.
- Runtime user settings are persisted through the config API/database path, not by editing appsettings from UI.
- Infrastructure-level ports and service endpoints remain in configuration/discovery services, not in user-facing settings unless explicitly designed.

## Integration

- Desktop UI should use services (`IServiceDiscovery`, `IBackendServerService`, `IWindowManagementService`, etc.) rather than constructing process/network behavior inline.
- Backend API changes require checking frontend and desktop consumers before modifying response shapes.


<spec-entry category="arch" keywords="lifecycle,migration,rebuild,sqlite,marker" date="2026-08-10" sid="S-20260810-r6rv" title="Rust 生命周期数据库重建标志策略" description="Rust 后端生命周期数据库重建、占用降级与未来迁移的稳定规则" source="feature/rust-service-backend@901d86c">

### Rust 生命周期数据库重建标志策略

20260810000000_AddMetricLifecycleStorage 是生命周期存储一次性重建完成的唯一标志。不得根据 MetricLifecycleCheckpoints 表是否存在判断重建完成。首次安装由普通 schema 初始化创建该表并使用 CARGO_PKG_VERSION 写入 marker。旧数据库重建遇到 SQLITE_BUSY 或 SQLITE_LOCKED 时最多等待 1 秒，随后保留原数据库继续启动；可创建运行所需 schema，但不得写入该 marker，使下次启动继续尝试重建。数据库损坏、字段缺失、磁盘错误不得降级。未来数据库变更必须新增独立 MigrationId，不得复用现有 marker 或根据 ProductVersion 决定是否执行。

</spec-entry>

<spec-entry category="arch" keywords="recordmetrics,sqlite,datacollection" date="2026-08-10" sid="S-20260810-hqn4" title="指标历史记录默认关闭" description="进程指标持久化开关及实时链路边界" source="feature/rust-service-backend@3a209ac">

### 指标历史记录默认关闭

DataCollection.RecordMetrics 控制进程指标写入 SQLite，默认 false。关闭时实时推送继续运行，但跳过 ProcessMetricRecords 写入以及指标聚合、清理和 WAL checkpoint 生命周期任务；配置 API 保存后热更新。

</spec-entry>