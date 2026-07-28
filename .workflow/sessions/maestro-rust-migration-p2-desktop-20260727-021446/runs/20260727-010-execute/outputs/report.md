---
kind: report
schema: report/1.0
run_id: 20260727-010-execute
session_id: maestro-rust-migration-p2-desktop-20260727-021446
verdict: done-with-concerns
summary: "Post-execute repair: 3 G3 parity fixes implemented and tested; bounded memory optimization attempted and did not reduce memory; normal release meets user-approved < 40 MiB fallback."
---

# 20260727-010-execute — Post-Execute Repair

## Scope

Bounded repair after post-execute memory hard gate, per user-approved recovery (retry disposition). Consumes sealed diagnosis `20260727-009-debug` and current-plan.

## Implemented changes

### G3 parity fixes (all tested)

1. **Locked click transition** — `panel_after_click(Locked) -> Expanded`, matching C# `FloatingWindowViewModel.cs:258-270`. Previously Rust returned `Collapsed`. Source: `xhm-desktop/src/ui/floating_window.rs:92-101`. Test: `panel_after_click_parity_matches_csharp_locked_expanded_toggle`.

2. **Pinned overflow** — pinned cards moved from unbounded `VerticalLayout` for-loop to capped `ListView` with `max-visible-pinned: 3` and `pinned-area-height` property. Collapsed window height and details panel geometry consume the capped height, so all pinned rows remain reachable via scroll and no negative/occluded details panel results. Source: `xhm-desktop/ui/floating_window.slint:202-203,276-291`.

3. **Unsnapped drag release clamp** — `finish_drag` now clamps the unsnapped release rect to the monitor work area and calls `SetWindowPos` when the position changed. Pure helper `clamp_rect_to_work_area` added to `floating_interactions.rs:419-432`. Snapped path (24px `EDGE_SNAP_DISTANCE` + occupied taskbar edge exclusion) is unchanged. 2 deterministic tests cover offscreen/negative and oversized/invalid bounds.

### Bounded memory optimization attempt (FAILED)

- `xhm-desktop/Cargo.toml`: tokio normal dependency features narrowed to `rt,sync,time,macros` (was workspace `full`). Reqwest still brings `tokio/net` transitively; `.enable_all()` retained for network I/O.
- `xhm-desktop/src/lib.rs`: dedicated SSE std thread uses `tokio::runtime::Builder::new_current_thread()` (was `new_multi_thread().worker_threads(2)`).

## Verification

| Check | Result |
|---|---|
| `cargo check -p xhm-desktop --all-targets` | pass (8.69s) |
| `cargo clippy -p xhm-desktop --all-targets -- -D warnings` | clean (8.08s) |
| `cargo test -p xhm-desktop` | 128 passed, 0 failed |
| `cargo build -p xhm-desktop --release` | pass (75s) |

## Memory measurement (post-repair)

| Condition | Samples | Min MiB | Max MiB | < 10 MiB |
|---|---|---|---|---|
| UI_SMOKE (same as baseline) | 60 | 37.039 | 37.277 | 0/60 |
| normal (no UI_SMOKE) | 30 | 33.656 | 33.660 | 0/30 |

Baseline UI_SMOKE was min 34.504 MiB. Post-repair UI_SMOKE is min 37.039 MiB under the identical measurement condition. The bounded optimization attempt did not reduce memory; the +2.535 MiB delta is **unproven** — no controlled runtime-only A/B was performed to isolate the cause.

## Outcome

- **Original P2 gate (< 10 MiB): NOT MET.** One bounded optimization attempt did not reduce memory.
- **User-approved fallback (< 40 MiB): MET.** Normal 33.66 MiB; UI_SMOKE 37.04 MiB.
- **G3 parity gaps: CLOSED.** All three fixes implemented with deterministic tests.
- No code was suppressed, no threshold was hidden, no passing evidence was fabricated.

## Blocking concern

The G4 `done_when` and `boundary_contract.definition_of_done` still specify `< 10 MiB`. Meeting the user-approved `< 40 MiB` fallback requires a Session-level goal amendment (`--amend`), which is outside this repair Run's authority. The chain's `post-execute` decision must record the memory gate outcome honestly; goal amendment is a separate user-authorized step.
