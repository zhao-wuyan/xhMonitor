---
title: Rust VRAM 为零时先核对实际 lhm-bridge 路径
description: "开发模式下 target/debug 的旧 bridge 会掩盖已更新的 C# build；管理员权限不是 VRAM 数据的必要条件。"
type: tip
category: debug
created: 2026-08-04T12:13:03.803Z
keywords:
  - vram
  - lhm-bridge
  - stale-binary
  - non-admin
tags:
  - debug
  - vram
  - lhm-bridge
  - windows
specCategory: debug
---

排查 Rust Service VRAM 为零时，先直接运行 Service 日志中的 `lhm-bridge.exe` 并比较原始 JSON。Windows 非管理员进程仍可从 `GPU Adapter Memory\Dedicated Usage` 获得系统/进程显存，并从显示适配器 Registry `HardwareInformation.qwMemorySize` 获得容量；管理员权限只影响部分温度与硬件传感器。开发运行时应根据 `target/debug` 或 `target/release` 选择仓库 `lhm-bridge/bin/Debug|Release/net8.0/win-x64/lhm-bridge.exe`，避免同级 `target/*/lhm-bridge.exe` 旧副本优先。验证应同时检查 `ReceiveSystemUsage.totalVram/maxVram` 和 `ReceiveProcessMetrics.metrics.vram` 的非零样本。
