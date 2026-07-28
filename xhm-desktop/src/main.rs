//! xhm-desktop 可执行入口（TASK-001）。
//!
//! 只调用 [`xhm_desktop::bootstrap`]；不承载客户端、Win32 或 UI 状态逻辑
//! （TASK-001 convergence：main.rs 仅保留 bootstrap 调用，G2-G4 逻辑未堆进入口）。

fn main() -> anyhow::Result<()> {
    xhm_desktop::bootstrap()
}
