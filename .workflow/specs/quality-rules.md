---
title: "Quality Rules"
category: quality
---

# Quality Rules

## Linters & Formatters

- Rust: rustfmt + clippy at toolchain defaults — no `rustfmt.toml`/`clippy.toml` in the repo. Gate changes with `cargo fmt --check` and `cargo clippy --workspace`.
- Web: ESLint 9 flat config (`xhmonitor-web/eslint.config.js`) — `@eslint/js` recommended + `typescript-eslint` recommended + `eslint-plugin-react-hooks` (flat recommended) + `eslint-plugin-react-refresh` (vite), applied to `**/*.{ts,tsx}`, `dist` ignored. Run `npm run lint`. No Prettier config exists.
- TypeScript compiler acts as a gate: `npm run build` runs `tsc -b` before `vite build`; strict flags include `strict`, `noUnusedLocals`, `noUnusedParameters`, `noFallthroughCasesInSwitch`, `noUncheckedSideEffectImports`.
- C#: no `.editorconfig` or analyzer package configured; `<Nullable>enable</Nullable>` is the null-safety gate; `NoWarn CA1416` only in the test project.

## CI (GitHub Actions)

- `.github/workflows/release-lite.yml` — on GitHub release publish, windows-latest: setup-dotnet 8.0.x + setup-node 20.x, syncs version into `Directory.Build.props`, then builds Lite/Lite-Net8/Full ZIPs and Inno Setup installers via `publish.ps1` / `build-installer.ps1` and uploads release assets.
- `sync-latest-release.yml`, `sync-gitee-release.yml` — release mirroring workflows.
- There is no lint/test CI workflow; linting and tests are run locally before release.

## Coverage

- .NET: coverlet.collector is referenced — `dotnet test --collect:"XPlat Code Coverage"` works.
- Rust and web: no coverage tooling configured.

## Entries

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
