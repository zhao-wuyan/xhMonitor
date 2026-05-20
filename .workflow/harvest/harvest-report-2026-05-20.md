# Harvest Report - 2026-05-20

Source: commit:b37b357 / PR #26
Mode: direct session harvest fallback (no .workflow/state.json present)

## Routed Items

- HRV-c639c05c -> spec: .workflow/specs/debug-notes.md
  - Title: SQLite 相对路径不得依赖全局当前目录
  - Category: debug
  - Confidence: 0.95
  - Tags: sqlite, current-directory, ryzenadj, relative-path, native-interop

## Extracted Knowledge

服务端 SQLite 相对 Data Source 必须基于应用目录解析为绝对路径，不能依赖进程级 Environment.CurrentDirectory。native interop 不得修改全局当前目录，否则并发数据库连接可能连到错误目录并创建空库。

## Evidence

- Fix commit: b37b357 fix: 修复 SQLite 相对路径误连空库
- PR: https://github.com/zhao-wuyan/xhMonitor/pull/26
- Spec target: .workflow/specs/debug-notes.md
- Source anchors: XhMonitor.Service/Data/SqliteConnectionStringResolver.cs:1, XhMonitor.Core/Services/RyzenAdjNativeClient.cs:218

## Notes

.workflow/state.json is absent, so full workflow artifact discovery was not available. This harvest records provenance for the already-routed debug spec entry and skips duplicate spec writes.
