---
kind: report
run_id: 20260727-004-execute
session_id: maestro-rust-migration-p2-desktop-20260727-021446
command: execute
goal_ref: G2
verdict: ready
status: completed
sealed: false
summary: "G2 stabilization repaired compilation, Process32NextW failure mapping, and leaked runtime ownership. The incomplete drag/snap production stub was removed and explicitly deferred to G3; G2 pure geometry remains tested. Build and test passed three consecutive rounds with 100 tests, clippy is clean, and a fresh dual-window smoke exited cleanly."
concerns:
  - "Drag/snap production wiring is deferred to G3; G2 provides only the tested pure geometry."
  - "Real smoke observed the current Bottom taskbar edge only; Top/Left/Right remain covered by pure-function tests."
  - "SSE connection retries because no local service was running; shutdown and runtime cleanup were observed."
decisions:
  - {id: D-G2-1, text: "Use the Rust-specific single-instance Mutex namespace.", status: accepted}
  - {id: D-G2-5, text: "Map unexpected Process32FirstW and Process32NextW failures to Other via GetLastError.", status: accepted}
  - {id: D-G2-7, text: "Retain the guard through bounded HWND retry.", status: accepted}
  - {id: D-G2-10, text: "Bound HWND retry to 20 attempts at 50ms.", status: accepted}
  - {id: D-G2-11, text: "Remove incomplete drag/snap production callbacks and defer correct DPI/monitor/SetWindowPos wiring to G3.", status: accepted}
  - {id: D-G2-12, text: "Own controller, guard, timers, and SSE JoinHandle through TrayRuntime resources and release them deterministically.", status: accepted}
next:
  - {command: verify, reason: "G2 stabilization complete; Run intentionally remains unsealed", needs: [current-plan, current-execution]}
---
## 摘要

修复了 `lib.rs` 的不匹配分隔符，并把 `controller_cell`、`guard_cell`、保存 timer 与 SSE runtime thread 纳入 `TrayRuntime` 生命周期。`Process32NextW` 返回 `FALSE` 时，只有 `ERROR_NO_MORE_FILES` 被视为正常结束；其他错误返回 `KillOutcome::Other`。

损坏的 drag/snap production stub 已删除。该 stub 使用窗口本地逻辑坐标、错误地把窗口 RECT 当 work area，并忽略 snapped position。正确生产接线明确延后到 G3，G2 的纯几何实现与测试保留。

## 结论/Verdict

**ready**。`cargo build -p xhm-desktop` 与 `cargo test -p xhm-desktop` 连续 3 轮通过，每轮 100 tests；clippy 0 warning。真机 smoke 观察到 distinct HWND、DPI、Bottom placement，并在关闭双窗后以 exit 0 退出，日志确认 SSE runtime stopped。

## 产物

- `xhm-desktop/src/lib.rs`：编译修复与确定性资源生命周期。
- `xhm-desktop/src/win32/process.rs`：`Process32NextW` 错误映射。
- `xhm-desktop/ui/shell.slint`：删除损坏的 drag callback stub。
- `outputs/*.json`：同步真实验证与 G3 defer 边界。

## 交接/Next

Run 保持 unsealed。下一步是 verify。
