slint::include_modules!();

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use slint::ComponentHandle;

mod win32;

const WINDOW_TITLE: &str = "xhm-poc-slint";
const WIN_W: i32 = 260;
const WIN_H: i32 = 72;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let app = AppWindow::new()?;

    // ── Win32 初始化 ────────────────────────────────────────────────────────
    // 窗口必须先进入事件循环才能被 FindWindowW 找到；使用短延迟 Timer
    {
        let weak = app.as_weak();
        let timer = slint::Timer::default();
        timer.start(
            slint::TimerMode::SingleShot,
            std::time::Duration::from_millis(200),
            move || {
                let Some(hwnd) = win32::find_own_hwnd(WINDOW_TITLE) else {
                    eprintln!("[poc] HWND not found; Win32 integration skipped");
                    return;
                };
                println!("[poc] HWND = {hwnd:?}");
                win32::set_topmost(hwnd);
                win32::position_near_taskbar(hwnd, WIN_W, WIN_H);
                win32::set_click_through(hwnd, true);
                if let Some(app) = weak.upgrade() {
                    app.set_status_text("click-through: ON  (Ctrl+Alt+M)".into());
                }
            },
        );
        // Timer must stay alive until fired
        std::mem::forget(timer);
    }

    // ── 热键切换（Ctrl+Alt+M）─────────────────────────────────────────────
    // WS_EX_TRANSPARENT 启用后 TouchArea 收不到鼠标事件，热键是唯一入口
    {
        let click_through = Arc::new(AtomicBool::new(true));
        win32::start_hotkey_thread(app.as_weak(), move |app| {
            let enabled = !click_through.load(Ordering::Relaxed);
            click_through.store(enabled, Ordering::Relaxed);

            let Some(hwnd) = win32::find_own_hwnd(WINDOW_TITLE) else { return; };
            win32::set_click_through(hwnd, enabled);
            app.set_status_text(if enabled {
                "click-through: ON  (Ctrl+Alt+M)".into()
            } else {
                "click-through: OFF (Ctrl+Alt+M)".into()
            });
        });
    }

    // ── 内存定期上报（10s）────────────────────────────────────────────────
    {
        let timer = slint::Timer::default();
        timer.start(
            slint::TimerMode::Repeated,
            std::time::Duration::from_secs(10),
            || win32::print_memory(),
        );
        std::mem::forget(timer);
    }

    win32::print_memory();
    println!("[poc] running — Ctrl+Alt+M toggles click-through, close window to exit");
    app.run()?;
    Ok(())
}
