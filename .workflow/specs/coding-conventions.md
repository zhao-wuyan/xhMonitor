---
title: "Coding Conventions"
category: coding
---

# Coding Conventions

Detected 2026-08-13 from real sources: Rust workspace (`xhm-core`, `xhm-service`), .NET 8 solution (`XhMonitor.Core`, `XhMonitor.Desktop`, `XhMonitor.Desktop.Tests`) plus standalone `lhm-bridge`, and React web app (`xhmonitor-web`).

## Rust (xhm-core, xhm-service)

### Formatting

- 4-space indentation, rustfmt-default style. No `rustfmt.toml` / `clippy.toml` anywhere in the repo — toolchain defaults apply.
- Trailing commas in multiline constructs; long expressions wrapped rustfmt-style.
- Module docs use `//!` (Chinese prose is the norm, see `xhm-service/src/main.rs`, `xhm-core/src/models.rs`); item docs use `///`.

### Naming

- `snake_case` for functions, modules and files (`load_process_name_rules`, `web.rs`, `api/mod.rs`); `PascalCase` for types/traits/enum variants (`SqliteMetricStore`, `AggregationLevel::Minute`); `SCREAMING_SNAKE_CASE` for consts (`DEFAULT_WEB_PORT`).
- Wire/DTO structs use `#[serde(rename_all = "camelCase")]` to match the legacy C# JSON contract; map keys are preserved verbatim (some stay PascalCase, e.g. `"Appearance"`); wire enums serialize as integers (`#[serde(into = "i32", try_from = "i32")]`).

### Imports

- Grouped with blank lines: `std` → external crates → own crates (`xhm_core`, `xhm_service`); nested `use` braces (`use tracing_subscriber::{filter::LevelFilter, layer::SubscriberExt, ...}`).

### Patterns

- Errors: `thiserror` for library errors (`xhm_core::error::CoreError`), `anyhow::Result` at binary entry points; failure paths log via `tracing::warn!`/`tracing::error!` and degrade instead of aborting (e.g. missing lhm-bridge falls back to `MockLhmReader`).
- Structured logging with `tracing` field syntax: `tracing::info!(db = %paths.db_path.display(), "opening database")`.
- Storage/hardware boundaries are trait objects from `xhm_core::traits` (`MetricStore`, `LhmReader`, `RyzenAdjClient`, `Clock`), held as `Arc<dyn Trait>`.
- Async: Tokio runtime, graceful shutdown via `CancellationToken` + `tokio::select!`.
- Unit tests co-located in `#[cfg(test)]` modules inside each source file (all 16 `src/*.rs` files have them).

## C# (.NET 8)

### Formatting

- 4-space indentation, Allman braces, file-scoped namespaces (`namespace XhMonitor.Desktop;`). No `.editorconfig` — SDK defaults.
- `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>` in every csproj.

### Naming

- `PascalCase` for types/methods/properties/consts (`MutexName`, `DefaultIntervalMs`); `_camelCase` for private fields (`_mutex`, `_host`); `I` prefix for interfaces (`IWindowManagementService`); `Async` suffix for async methods.
- One type per file, file named after the type; folders by role (`Services/`, `ViewModels/`, `Converters/`, `Models/`, `Windows/`).

### Imports

- `using` order: `System.*` → `Microsoft.*` → project namespaces; alias to disambiguate WPF vs WinForms (`using WpfApplication = System.Windows.Application;`).

### Patterns

- Desktop: Generic Host DI (`Host.CreateDefaultBuilder`), `interface + class` services registered as singletons in `App.xaml.cs`, MVVM ViewModels, `IHostedService` for startup orchestration; UI updates marshalled through `Dispatcher`.
- lhm-bridge: single-file top-level-statements `Program.cs`; records for wire payloads; strict process contract — stdout carries JSON Lines data only, stderr carries a banner JSON plus diagnostics, exit codes 0/1/2.
- XML doc comments (`/// <summary>`) written in Chinese where present.

## TypeScript / React (xhmonitor-web)

### Formatting

- 2-space indentation, single quotes, semicolons. No Prettier config — style is by convention; ESLint flat config has no stylistic rules.
- Strict tsconfig (`tsconfig.app.json`): `strict`, `noUnusedLocals`, `noUnusedParameters`, `verbatimModuleSyntax`, `noFallthroughCasesInSwitch`, `moduleResolution: "bundler"`, `jsx: "react-jsx"`.

### Naming

- Components: `PascalCase.tsx` with named exports (`StatCard.tsx` → `export const StatCard`); hooks: `useXxx.ts` (`useMetricsHub.ts`); contexts: `XxxContext.tsx` with companion `useXxx.ts` hook files; plain modules: `camelCase.ts` (`endpoints.ts`, `apiFetch.ts`).
- Per-component props interface (`interface StatCardProps`).

### Imports

- External packages first, then relative imports; type-only imports must use `import type { ... }` (enforced by `verbatimModuleSyntax`).

### Patterns

- All user-visible text goes through `t(key)` from `src/i18n.ts` (default locale zh).
- Endpoints only from `src/config/endpoints.ts` (`API_V1_BASE`, `METRICS_HUB_URL`); never hardcode ports/URLs.
- High-frequency render paths use `memo` / `useMemo`; theming via CSS variables (`--xh-card-accent`) passed through inline style.
- Layout state changes only via `LayoutContext` (`updateLayout`), never raw `localStorage`; large background images go to IndexedDB (`utils/backgroundImageStore.ts`).

## Entries
