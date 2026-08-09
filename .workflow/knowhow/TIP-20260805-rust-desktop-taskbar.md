---
title: Rust Desktop Taskbar 默认隐藏
description: Taskbar 先创建 HWND，接线后默认隐藏；smoke 模式例外。
type: tip
category: ui
created: 2026-08-05T03:11:26.810Z
tags:
  - xhm-desktop
  - taskbar
  - slint
  - startup
---

xhm-desktop 的双窗口接线依赖 TaskbarWindow 先 show 以创建 HWND。默认 Floating 模式下，应在 wire_dual_window 成功后调用 Slint hide；XHM_DESKTOP_UI_SMOKE 与 XHM_DESKTOP_G4_SMOKE 显式保留可见。TaskbarSettings::default 和 settings.slint 的 enable_edge_dock_mode 都应为 false，避免默认设置层显示为启用。
