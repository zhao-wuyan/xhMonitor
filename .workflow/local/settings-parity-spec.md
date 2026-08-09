# Settings Panel Redesign Spec (Rust Slint parity with C# WPF)

## Goal
Redesign the Rust `xhm-desktop` settings window (`ui/settings.slint` + `src/ui/settings.rs`) to:
1. **Use Chinese labels** (currently all English).
2. **Better/cleaner layout** — you may use a sidebar-nav or grouped-card layout; NOT required to pixel-match C#, but must be visually coherent and dark-themed to match the floating window (bg ~#101217, accent cyan #38bdf8, cards #ffffff08 borders #ffffff18).
3. **No fewer feature points than C#.** Add the 2 genuinely-missing editable config fields (see below).

The user explicitly said: "设置面板你可以用更合适的UI布局处理，不必追求 c# 原版复刻，但是功能点不能少。"

## Renderer constraint (CRITICAL)
The app is LOCKED to Slint software renderer (`SLINT_BACKEND=winit-software`). Keep the UI cheap: no heavy gradients/blur beyond what exists. Static layout, no per-frame animation except tiny toggle slides already present.

## Current Rust settings.slint fields (KEEP ALL)
opacity-text, process-keywords, monitor-cpu/memory/gpu/vram/power/network, enable-floating-mode, enable-edge-dock-mode, bar-visual, dock-column-gap, dock-cpu/memory/gpu/vram/power/upload/download-label, admin-mode, start-with-windows, enable-lan-access, enable-access-key, access-key, ip-whitelist, local-ip. Callbacks: load-settings(), save-settings(), close-window().

## ADD these 2 missing editable fields (highest priority — genuinely absent)
| Field | Slint prop | Config group.key | Default | Range/format |
|---|---|---|---|---|
| Top 进程数量 | `top-process-count` (string) | `DataCollection.TopProcessCount` | "10" | integer, clamp 1..=100 |
| 历史数据保留时长(天) | `data-retention-days` (string) | `DataCollection.DataRetentionDays` | "30" | integer, clamp 1..=365 |

## Chinese labels for existing fields (use these exact strings)
- Section 外观: 悬浮窗透明度 (Opacity 20-100), 悬浮窗模式 (enable-floating-mode), 迷你/贴边模式 (enable-edge-dock-mode), 迷你/贴边风格 Bar/文本 toggle (bar-visual: true=柱状条 false=文本).
- Section 迷你/贴边指标名称: CPU名称/内存名称/GPU名称/VRAM名称/功耗名称/上传前缀/下载前缀 (dock-*-label), 组间距(px,0-24) (dock-column-gap).
- Section 数据采集: 进程监控关键词 (process-keywords, JSON array; hint: 每行一个关键词，支持正则，! 前缀排除), Top进程数量 (top-process-count), 历史数据保留时长(天) (data-retention-days).
- Section 监控项: CPU/内存/显卡GPU/显存VRAM/功耗/网络 (monitor-* toggles).
- Section 系统: 开机自启动 (start-with-windows), 管理员模式 (admin-mode), 启用局域网访问 (enable-lan-access), 要求访问密钥 (enable-access-key), 访问密钥 (access-key, password; disabled/greyed unless enable-access-key; hint: 留空将自动生成), IP白名单 (ip-whitelist, multiline, hint: 每行一个 IP 或 CIDR), 本机IP显示 (local-ip, read-only green text).
- Buttons: 保存 (save-settings, primary), 重新加载 (load-settings), 关闭 (close-window). Optionally add 恢复默认 button — SKIP unless trivial (it needs reset logic).

## Rust plumbing changes (`src/ui/settings.rs` + `src/ui/taskbar_metrics.rs`)
The 2 new fields must round-trip through the existing settings pipeline. Follow the EXACT existing pattern for `process_keywords`:

1. **`src/ui/taskbar_metrics.rs`** — add to `TaskbarSettings` struct (after `process_keywords`):
   - `pub top_process_count: u32,` (default 10)
   - `pub data_retention_days: u32,` (default 30)
   Add defaults in `impl Default`. In `normalized()`, clamp: `top_process_count.clamp(1,100)`, `data_retention_days.clamp(1,365)`.
   In `apply_allowed_groups`, read from DataCollection group:
   ```rust
   if let Some(value) = data_collection.and_then(|g| g.get("TopProcessCount")) {
       self.top_process_count = value.trim().parse().map(|v: u32| v.clamp(1,100)).unwrap_or(self.top_process_count);
   }
   if let Some(value) = data_collection.and_then(|g| g.get("DataRetentionDays")) {
       self.data_retention_days = value.trim().parse().map(|v: u32| v.clamp(1,365)).unwrap_or(self.data_retention_days);
   }
   ```

2. **`src/ui/settings.rs`**:
   - `allowed_subset()` — into the `data_collection` map insert `"TopProcessCount"` and `"DataRetentionDays"` (`.to_string()`).
   - `collect_from_window()` — parse `app.get_top_process_count()` / `app.get_data_retention_days()` (use a u32 parse+clamp helper; there's `parse_input` for u8 — add a u32 variant or inline parse). On invalid, return an existing-style `SettingsError::InvalidNumber { field }`.
   - `apply_to_window()` — `app.set_top_process_count(settings.top_process_count.to_string().into())`, same for retention.

3. Update any tests in `taskbar_metrics.rs`/`settings.rs` that construct `TaskbarSettings { .. }` literally — add the two new fields (or use `..TaskbarSettings::default()`).

## Constraints
- Match existing Rust code style (the `Field`/`Switch`/`ActionButton` Slint components in settings.slint are reusable — extend them; a numeric Field is just a `Field` with `input-type: number`).
- Do NOT touch floating_window.slint, floating_window.rs, floating_interactions.rs, win32/, shell.rs, lib.rs — another agent owns those.
- Do NOT run cargo build/test/clippy — the main agent builds everything at the end.
- The Slint window is compiled via `shell.slint` (root) which `export { SettingsWindow } from "settings.slint"`. Keep the `export component SettingsWindow inherits Window` signature; only add properties/callbacks and restructure children.
