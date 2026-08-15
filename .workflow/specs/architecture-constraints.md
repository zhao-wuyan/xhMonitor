---
title: "Architecture Constraints"
category: arch
---

# Architecture Constraints

## Module Structure

Multi-package repository: a Rust workspace + a .NET solution + a standalone .NET bridge + a web app.

- Rust workspace (`Cargo.toml`, resolver 2, workspace version 0.3.0): `xhm-core` (shared models/traits/errors/wire contract), `xhm-service` (Axum binary, port 35179).
- .NET solution (`xhMonitor.sln`): `XhMonitor.Core` (shared config/models), `XhMonitor.Desktop` (WPF shell, net8.0-windows), `XhMonitor.Desktop.Tests` (xUnit).
- `lhm-bridge/lhm-bridge.csproj` is NOT in the solution — standalone LibreHardwareMonitor sensor subprocess, built/published separately by `publish.ps1`.
- `xhmonitor-web/package.json`: React 19 + Vite 7 SPA (dev port 35180).
- Product version lives in `Directory.Build.props` (0.2.21) and drives .NET assemblies plus `publish.ps1`/`build-installer.ps1`; the Rust workspace version (0.3.0) is independent.

## Layer Boundaries

- `xhm-core` → `xhm-service` is one-way. xhm-core depends only on serde/serde_json/thiserror/chrono — no Axum, no rusqlite. Storage and hardware boundaries are traits (`MetricStore`, `LhmReader`, `RyzenAdjClient`, `Clock`) defined in xhm-core and implemented in xhm-service (`SqliteMetricStore`, `LhmBridgeManager`, `ProductionRyzenAdjClient`).
- `lhm-bridge` runs as a child process of xhm-service (`LhmBridgeManager::start`). Contract: stdout = one `LhmSnapshot` JSON per line, stderr = banner JSON (`is_admin`) + diagnostics, exit codes 0 (graceful) / 1 (LHM init failure) / 2 (`--require-admin` unmet). The service degrades to `MockLhmReader` when the bridge is unavailable.
- `xhmonitor-web` communicates with the service only via REST (`/api/v1/...`) and the SignalR-compatible hub (`/hubs/metrics`) on port 35179; production builds use same-origin relative URLs behind the 35180 gateway (`src/config/endpoints.ts`).
- `XhMonitor.Desktop` owns the service process lifecycle (release: `Service/xhm-service.exe`, dev fallback: `cargo run -p xhm-service`) and hosts web assets on 35180 with YARP reverse-proxying `/api/**` and `/hubs/**` to 35179.
- The wire contract in `xhm-core/src/models.rs` + `wire.rs` is field-for-field aligned with the legacy C# JSON: camelCase properties, verbatim map keys (e.g. `"Appearance"` stays PascalCase), integer enums, explicit nulls. Renaming fields silently breaks the unmodified React frontend.

## Dependency Rules

- Rust dependency versions are centralized in `[workspace.dependencies]` at the root; member crates reference them with `.workspace = true`.
- `XhMonitor.Desktop` references only `XhMonitor.Core`; the test project references `XhMonitor.Desktop`; lhm-bridge shares no project references (JSON contract only).
- Frontend build output feeds the Desktop app: MSBuild target `BuildWebAssets` in `XhMonitor.Desktop.csproj` runs `npm install`/`npm run build` and copies `xhmonitor-web/dist/**` into `wwwroot/` at build and publish time.
- Frontend code must route endpoints through `src/config/endpoints.ts` and layout state through `LayoutContext`.

## Technology Constraints

- Rust 1.82 (`rust-version` in workspace), edition 2021. Release profile: `opt-level = "z"`, `lto = true`, `codegen-units = 1`, `panic = "abort"`, `strip = true`.
- .NET 8: `net8.0-windows` for Desktop/Tests (WPF + WinForms enabled); Desktop takes `FrameworkReference Microsoft.AspNetCore.App` (embedded Kestrel + YARP 2.x).
- Node.js 18+ locally (CI uses Node 20.x), TypeScript ~5.9.3, Vite 7, Tailwind CSS 4 via `@tailwindcss/vite`, `@microsoft/signalr` 10.
- Ports: 35179 = internal API + hub (loopback-bound); 35180 = web gateway (Vite dev server with `strictPort: true`, or service/Desktop hosted; binds 0.0.0.0 only when LAN access security config allows).
- Windows-only runtime: `windows-sys` bindings, WinRing0 driver (bridge sensors), UAC/admin-mode features, RyzenAdj (AMD platform gate).

## Entries

<spec-entry category="arch" keywords="lifecycle,migration,rebuild,sqlite,marker" date="2026-08-10" sid="S-20260810-r6rv" title="Rust 生命周期数据库重建标志策略" description="Rust 后端生命周期数据库重建、占用降级与未来迁移的稳定规则" source="feature/rust-service-backend@901d86c">

### Rust 生命周期数据库重建标志策略

20260810000000_AddMetricLifecycleStorage 是生命周期存储一次性重建完成的唯一标志。不得根据 MetricLifecycleCheckpoints 表是否存在判断重建完成。首次安装由普通 schema 初始化创建该表并使用 CARGO_PKG_VERSION 写入 marker。旧数据库重建遇到 SQLITE_BUSY 或 SQLITE_LOCKED 时最多等待 1 秒，随后保留原数据库继续启动；可创建运行所需 schema，但不得写入该 marker，使下次启动继续尝试重建。数据库损坏、字段缺失、磁盘错误不得降级。未来数据库变更必须新增独立 MigrationId，不得复用现有 marker 或根据 ProductVersion 决定是否执行。

</spec-entry>

<spec-entry category="arch" keywords="recordmetrics,sqlite,datacollection" date="2026-08-10" sid="S-20260810-hqn4" title="指标历史记录默认关闭" description="进程指标持久化开关及实时链路边界" source="feature/rust-service-backend@3a209ac">

### 指标历史记录默认关闭

DataCollection.RecordMetrics 控制进程指标写入 SQLite，默认 false。关闭时实时推送继续运行，但跳过 ProcessMetricRecords 写入以及指标聚合、清理和 WAL checkpoint 生命周期任务；配置 API 保存后热更新。

</spec-entry>
