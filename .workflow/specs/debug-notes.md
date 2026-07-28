---
title: "Debug Notes"
dimension: specs
category: debug
keywords:
  - debug
  - root-cause
  - diagnostics
readMode: optional
priority: normal
---

# Debug Notes

<spec-entry category="debug" keywords="sqlite,current-directory,ryzenadj,relative-path,native-interop" date="2026-05-18" source="XhMonitor.Service/Data/SqliteConnectionStringResolver.cs:1; XhMonitor.Core/Services/RyzenAdjNativeClient.cs:218">

### SQLite 相对路径不得依赖全局当前目录

服务端 SQLite 连接串中的相对 `Data Source` 必须在启动时解析为基于应用目录的绝对路径，不能依赖 `Environment.CurrentDirectory`。后台服务、native interop、外部进程或第三方库可能临时修改进程级当前目录，导致 SQLite 创建或连接到错误位置的空数据库。

本次故障表现为日志持续出现 `SQLite Error 1: 'no such table: ProcessMetricRecords'`、`ApplicationSettings`、`AggregatedMetricRecords`，同时现场出现 `Service/tools/RyzenAdj/xhmonitor.db` 这个 4 KB 空库。根因是 `RyzenAdjNativeClient` 为加载 native DLL 修改了全局 `Environment.CurrentDirectory`，而连接串 `Data Source=xhmonitor.db` 在并发创建连接时被解析到了 `tools/RyzenAdj`。

规则：

- SQLite `Data Source` 如果是相对路径，启动时必须基于 `ContentRootPath` / 应用目录转成绝对路径。
- native interop 不得修改全局 `Environment.CurrentDirectory`；优先使用显式 DLL 路径、`SetDllDirectory` 或受控加载策略。
- 诊断类似缺表错误时，同时检查实际连接的数据库文件路径、误创建的空库、文件创建时间和日志首个异常时间。
- 修复这类问题时应补充路径解析测试，覆盖相对路径、绝对路径和 `:memory:`。

</spec-entry>


<spec-entry category="debug" keywords="时区,时间戳,数据缺陷,rust迁移,聚合" date="2026-07-26" sid="S-20260726-n9zy" title="ProcessMetricRecords.Timestamp 混存 UTC 与本地时间" description="C# 侧 SpecifyKind 假转换导致 llama 历史行时区偏移；Rust 侧不复刻不补偿" source="analysis/rust-migration-feasibility@2d3c220">

### ProcessMetricRecords.Timestamp 混存 UTC 与本地时间

既有 C# 实现有一个持久化时间基准缺陷：MetricRepository.MapToEntity:79 用 DateTime.SpecifyKind(cycleTimestamp, DateTimeKind.Utc) —— 只贴 Kind 标签不做换算。而调用侧基准不统一：Worker.cs:455-461 的 llama 采样路径传 DateTime.Now（本地），PerformanceMonitor.cs:32 传 DateTime.UtcNow。结果同一列混存两种基准，llama 行整体偏移一个时区（东八区 +8h），而 AggregationWorker 水位线与 DatabaseCleanupWorker 保留期裁剪又一律按 UTC 比较，导致 llama 行的聚合归桶与裁剪时机都是歪的。Rust 迁移的取舍：只接受 DateTime<Utc> 落库，不复刻 SpecifyKind；不做读取端补偿（补偿需猜测每行来源 provider，会把可见的数据缺陷变成不可见的启发式）。

</spec-entry>