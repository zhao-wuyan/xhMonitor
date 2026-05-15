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
