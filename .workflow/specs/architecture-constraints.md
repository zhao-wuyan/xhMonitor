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


<spec-entry category="arch" keywords="lhm-bridge,memory,gc,private-bytes" date="2026-07-26" sid="S-20260726-y9oz" title="xhm-service bridge 内存门禁" description="Rust service 与 .NET bridge 的内存约束及门禁判据" source="analysis/rust-migration-feasibility@2d3c220" status="deprecated" superseded-by="S-20260726-pn0q">

### xhm-service bridge 内存门禁

xhm-service 与 lhm-bridge 组合 Private Bytes 必须低于 30 MiB。LaunchSpec::production 必须为 bridge child 设置 DOTNET_GCConserveMemory=9、DOTNET_GCHeapHardLimit=0x800000、DOTNET_gcConcurrent=0、DOTNET_GCRetainVM=0。验证时 warm-up 后每 5 秒采样，连续 60 秒内所有样本均须低于 30 MiB；不得用中位数或最终值覆盖峰值。

</spec-entry>

<spec-entry category="arch" keywords="lhm-bridge,memory,gc,private-bytes" date="2026-07-26" sid="S-20260726-ulc1" title="xhm-service bridge 内存门禁（无硬上限）" description="无硬 heap 限制的 bridge 内存门禁和 cadence 验证" source="analysis/rust-migration-feasibility@2d3c220" status="deprecated" superseded-by="S-20260726-pn0q">

### xhm-service bridge 内存门禁（无硬上限）

xhm-service 与 lhm-bridge 组合 Private Bytes 必须低于 30 MiB。不得为 bridge 设置 DOTNET_GCHeapHardLimit；该硬上限会把 host 传感器规模差异转为 OOM/restart 风险。bridge 禁用未消费的 IsMemoryEnabled 子树，并在每 5 次成功 snapshot flush 后执行 compacting GC；保留 DOTNET_GCConserveMemory=9、DOTNET_gcConcurrent=0、DOTNET_GCRetainVM=0。验证必须使用 release service 加实际 bridge，连续 60 秒每秒采样且全部样本 <30 MiB，同时确认 WS 连续事件最大间隔不超过 2 秒。

</spec-entry>

<spec-entry category="arch" keywords="lhm-bridge,memory,gc,private-bytes" date="2026-07-26" sid="S-20260726-pn0q" title="xhm-service bridge 内存行为门禁" description="bridge 内存与实时 cadence 的可验收行为门禁" source="analysis/rust-migration-feasibility@2d3c220" supersedes="S-20260726-y9oz,S-20260726-ulc1">

### xhm-service bridge 内存行为门禁

xhm-service 与 lhm-bridge 组合 Private Bytes 必须低于 30 MiB，且不得通过 DOTNET_GCHeapHardLimit 等全局 managed heap 硬上限把宿主机传感器规模差异转化为 OOM/restart 风险。验收必须使用 release service 加实际 bridge，连续 60 秒每秒采样，全部样本 <30 MiB；同时 SignalR WS 连续实时事件最大间隔不得超过 2 秒。内存回收实现可替换，但必须满足这些可观察行为。

</spec-entry>