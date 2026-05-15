---
title: "Quality Rules"
dimension: specs
category: execution
keywords:
  - quality
  - testing
  - coverage
  - xunit
  - moq
  - fluentassertions
  - eslint
  - security
readMode: required
priority: high
---

# Quality Rules

## Testing

- Add or update tests for all new/changed public behavior
- Test edge cases and error conditions (timeouts, null/empty inputs, invalid config, external failures)
- Mock external dependencies (hardware providers, SignalR, filesystem, external processes) to keep tests deterministic
- Target >= 80% coverage for new code (pragmatic, focus on critical paths)

### .NET

- Use xUnit + Moq + FluentAssertions
- Prefer fast unit tests; add integration tests only when needed (EF Core/SQLite, SignalR, hosted services)

### Frontend (xhmonitor-web)

- Use `node --test` via `npm run test`
- Keep tests fast and deterministic; avoid real network calls

## Reliability

- Prefer cancellation-aware loops in background services where practical
- Ensure resources are disposed (`IDisposable`/`IAsyncDisposable`, `await using`) and connections are stopped on shutdown

## Security & Secrets

- Validate and sanitize input at boundaries (HTTP endpoints, config, plugin inputs)
- Never commit secrets (keys/tokens/passwords); use environment variables or config overrides
- Prefer parameterized queries / EF Core LINQ; avoid building SQL strings manually

## Error Handling

- Clear, actionable error messages; do not expose sensitive info
- Log with context (ids, modes, endpoints) and appropriate severity

<spec-entry category="quality" keywords="wpf,async-ui,reentrancy,save-button,dialog-storm" date="2026-05-15" source="XhMonitor.Desktop/Windows/SettingsWindow.xaml.cs:100">

### WPF async 保存操作必须防重入

WPF `async void` UI 事件如果会执行耗时操作、外部进程、UAC、防火墙配置、HTTP 保存或弹框确认，必须在入口处加单次执行保护，并立即禁用触发按钮。

本次设置页保存问题的根因是 `Save_Click` 在局域网访问变更时会等待防火墙配置和保存 API，但保存按钮没有重入保护。用户连续点击会并发启动多条保存链路，导致 UI 看起来卡住，并在异步流程陆续完成后弹出多组确认/错误弹框。

规则：

- `async void` 事件入口只做重入门闩、按钮状态切换和调用实际 `Task` 方法。
- 实际保存逻辑放到 `Task` 方法中，便于测试和复用。
- 保存期间按钮必须保持 disabled，重复点击应被忽略，而不是排队执行。
- 成功提示只负责展示状态，不应绕过外层保存门闩提前重新启用按钮。
- 需要为门闩或等价防重入逻辑补充并发/重复点击单测。

</spec-entry>
