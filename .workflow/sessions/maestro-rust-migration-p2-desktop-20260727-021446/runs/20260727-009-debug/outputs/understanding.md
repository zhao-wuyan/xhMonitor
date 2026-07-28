---
status: partial
target: xhm-desktop release Private Bytes
current_hypothesis: stable anonymous startup footprint dominates; exact arena ownership remains inferred
updated_at: 2026-07-27T23:27:44+08:00
---
# Understanding

## Symptoms

Expected: `xhm-desktop` release in `winit-software` mode remains below 10 MiB Private Bytes.

Actual: accepted evidence has 60/60 failures at 34.504-34.719 MiB. Fresh ordinary release has 30/30 failures at 33.492-33.531 MiB. Fresh reconstructed `XHM_DESKTOP_UI_SMOKE` has 60/60 failures at 37.184-37.559 MiB.

The accepted condition string omits `XHM_DESKTOP_UI_SMOKE`. Its pass/fail evidence is valid, but it is not an ordinary collapsed-panel baseline: the fixture expands the main panel, loads 36 process rows, and spawns a ping child. Child processes were excluded from all desktop PID samples.

## Hypotheses

| ID | Status | Result |
|---|---|---|
| H1: leak or SSE traffic dominates | Refuted | Stable plateaus; live connected SSE is 0.237 MiB below unavailable/retrying normal state. |
| H2: visible software-rendered windows are material | Confirmed | Settings adds 4.648 MiB; About adds another 1.621 MiB in the same process. |
| H3: stable anonymous runtime allocations dominate | Partial | MEM_PRIVATE is 31.609 MiB; six anonymous RW allocation bases contain 29.672 MiB (93.87%). Exact Slint/allocator/Tokio ownership is not symbol-confirmed. |

Only one hypothesis was refuted, so the three-strike escalation limit was not reached.

## Backward Trace

1. The gate fails at `Process.PrivateMemorySize64` in every ordinary/smoke/connected variant.
2. `VirtualQueryEx` traces the dominant relevant map category to 31.609 MiB of committed `MEM_PRIVATE`, not the 67.473 MiB mapped image space.
3. Six anonymous allocation bases account for 93.87% of `MEM_PRIVATE`, showing concentration in allocator/runtime arenas rather than a broad module leak.
4. One-variable window actions show rendering state is material: Settings/About visibility adds 6.269 MiB cumulatively.
5. Source traces the retained normal baseline through four Slint component constructions, main/taskbar show, hidden Settings/About retention, native tray, and a dedicated SSE thread containing a two-worker Tokio runtime.
6. Cargo traces the first removable runtime surface to `tokio/full` plus `new_multi_thread`; accessibility is not isolated and is deliberately unchanged.

## Root Cause

Confirmed at category/architecture level: the <10 MiB failure is a stable startup-footprint problem in the current Slint/winit-software, Windows UI, and async-client architecture, not a minute-scale leak or SSE transport accumulation. Ordinary release mean is 33.514 MiB, leaving a 23.514 MiB gap and requiring a 70.16% reduction.

Partial at component level: the stripped binary and read-only boundary do not permit assigning the six large anonymous arenas to exact Slint renderer, Windows allocator/UI, accessibility, Tokio, tray, or individual base windows. No such component MiB numbers are claimed.

## Fix Direction

The conservative first attempt is one isolated runtime change: narrow direct Tokio features to `rt/sync/time/macros` and use `new_current_thread` inside the existing dedicated SSE thread. Preserve dual streams, resubscribe/cancel ordering, retries, shutdown, tray, windows, and accessibility. Expected impact is two fewer Tokio worker threads plus lower stack/control and feature/code surface; no MiB saving or <10 MiB pass is promised before measurement. Revert both changes together on behavior regression or immaterial benefit.

Separate low-risk G3 repairs remain:

- Locked click: Rust `Locked -> Collapsed` must match C# `Locked -> Expanded`; Collapsed click stays ignored.
- Pinned overflow: cap the visible pinned viewport at four rows and keep overflow scrollable.
- Unsnapped release: clamp the physical rectangle to the selected monitor `work_area` before preserving floating state.

## Confidence

| Dimension | Score |
|---|---:|
| hypothesis_quality | 0.92 |
| evidence_completeness | 0.90 |
| root_cause_isolation | 0.78 |
| fix_confidence | 0.72 |
| overall | 0.84 |

Pressure pass completed. The historical/fresh UI-smoke numeric mismatch is preserved as contradictory evidence; it does not alter the 0/60 gate result. Readiness passes because reproduction, immutable evidence, specific affected files, rollback, and bounded verification are present.
