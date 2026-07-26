slint::include_modules!();

use slint::ComponentHandle;

mod win32;

const WINDOW_TITLE: &str = "xhm-poc-slint";

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let app = AppWindow::new()?;

    // ── Win32 初始化 ────────────────────────────────────────────────────────
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
                // 默认左上角，不穿透（用户可正常交互）
                win32::position_top_left(hwnd);
                win32::set_click_through(hwnd, false);
                if let Some(app) = weak.upgrade() {
                    app.set_status_text("穿透: OFF".into());
                }
            },
        );
        std::mem::forget(timer);
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
    println!("[poc] 左上角，不穿透；关闭窗口退出");
    app.run()?;
    Ok(())
}
