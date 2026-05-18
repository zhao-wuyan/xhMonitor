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
