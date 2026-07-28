---
title: "Coding Conventions"
dimension: specs
category: general
keywords:
  - csharp
  - dotnet
  - wpf
  - aspnetcore
  - typescript
  - react
  - naming
  - style
  - convention
readMode: required
priority: high
---

# Coding Conventions

## C# / .NET

### Naming

- Use PascalCase for namespaces, types, methods, and public members
- Use camelCase for parameters and local variables
- Use `_camelCase` for private fields (e.g., `_connection`, `_logger`)
- Prefix interfaces with `I` (e.g., `IMetricProvider`)
- Suffix async methods with `Async` (e.g., `ConnectAsync`)

### Patterns

- Prefer dependency injection; avoid global mutable state
- Use early returns / guard clauses to reduce nesting
- Keep classes and methods small and focused (single responsibility)

### Error handling & logging

- Use try/catch around IO, interop, and external process calls; log with context via `ILogger`/Serilog
- Do not leak secrets or internal details in user-facing messages; log detail at appropriate levels
- If a failure is non-fatal, degrade gracefully and ensure cleanup continues

### Async & threading

- Avoid `.Result`/`.Wait()`; use `await` end-to-end
- In background/library code, use `ConfigureAwait(false)` where appropriate
- In WPF, marshal UI updates onto the UI thread via `Dispatcher`/`Dispatcher.BeginInvoke`

## TypeScript / React (xhmonitor-web)

### Naming & structure

- Use PascalCase for React components (file + export), camelCase for functions/variables
- Custom hooks are `useXxx` (e.g., `useLayoutState`, `useMetricsHub`)
- Centralize endpoints in `src/config/endpoints.ts`; do not hardcode ports/URLs
- All user-visible text goes through i18n (`t(key)` from `src/i18n.ts`)

### Formatting & linting

- 2-space indentation; single quotes; trailing commas in multiline constructs
- Keep ESLint clean: run `npm run lint` before finishing a change

## XAML / WPF

- Prefer MVVM: keep UI logic in ViewModels/services; keep code-behind minimal
- Use converters for presentation-only transforms; avoid heavy logic in XAML bindings
- Background callbacks (e.g., SignalR) must not touch UI objects directly (use `Dispatcher`)


<spec-entry category="coding" keywords="时间格式,sqlite,序列化,rust迁移,efcore" date="2026-07-26" sid="S-20260726-sug8" title="xhMonitor Rust 迁移：.NET 时间格式三分法" description="迁移到 Rust 时必须区分 SQLite TEXT / REST UTC-Z / SignalR 本地偏移三种时间格式" source="analysis/rust-migration-feasibility@2d3c220">

### xhMonitor Rust 迁移：.NET 时间格式三分法

迁移中同时存在三种互不相同的时间表示，混用会静默破坏兼容性：(1) SQLite TEXT 列 = 'yyyy-MM-dd HH:mm:ss.FFFFFFF'（空格分隔、无时区）；(2) REST 响应 = ISO-8601 带 Z；(3) SignalR 推送 = ISO-8601 带本地 UTC 偏移（非 Z，因为源是 DateTime.Now）。三者共用 .NET 规则：最多 7 位小数（tick=100ns）、去掉尾随零、全零时连小数点一并省略。对 SQLite 尤其关键——Timestamp 是 TEXT 列，范围查询是字典序比较，多写少写一位都会查错历史数据。实现见 xhm-core/src/time.rs，含排序不变量测试。

</spec-entry>