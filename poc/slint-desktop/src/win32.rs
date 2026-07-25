// Slint Desktop POC — win32.rs
// Win32 集成验证：
// 1. FindWindowW by title + GetWindowThreadProcessId PID 验证（保证只改自己的窗口）
// 2. WS_EX_TRANSPARENT 点击穿透切换（必须配合 WS_EX_LAYERED）
// 3. HWND_TOPMOST 始终置顶
// 4. FindWindow("Shell_TrayWnd") + GetWindowRect + SetWindowPos 任务栏附近定位
// 5. RegisterHotKey Ctrl+Alt+M（独立线程），invoke_from_event_loop 回调 Slint

use std::sync::Arc;
use parking_lot::Mutex;
use slint::Weak;

use windows::Win32::{
    Foundation::{HWND, RECT, LPARAM, BOOL, TRUE, FALSE},
    UI::WindowsAndMessaging::{
        EnumWindows, FindWindowW, GetWindowRect, GetWindowTextW,
        GetWindowThreadProcessId, SetWindowPos,
        GetWindowLongW, SetWindowLongW,
        GWL_EXSTYLE, HWND_TOPMOST,
        SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE,
        WS_EX_LAYERED, WS_EX_TRANSPARENT,
        GetMessageW, MSG, WM_HOTKEY,
    },
    UI::Input::KeyboardAndMouse::{
        RegisterHotKey, MOD_CONTROL, MOD_ALT,
    },
    System::{
        ProcessStatus::{GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS},
        Threading::{GetCurrentProcess, GetCurrentProcessId},
    },
};
use windows::core::w;

// ── HWND resolution ─────────────────────────────────────────────────────────

/// Find our window by iterating all top-level windows and matching both title
/// AND the current process PID.  Title alone is not proof of ownership.
pub fn find_own_hwnd(title: &str) -> Option<HWND> {
    let target_title = title.to_owned();
    let current_pid  = unsafe { GetCurrentProcessId() };

    struct SearchState {
        target_title: String,
        current_pid:  u32,
        found:        Option<HWND>,
    }

    let state = Arc::new(Mutex::new(SearchState {
        target_title,
        current_pid,
        found: None,
    }));
    let state_ptr = Arc::as_ptr(&state) as isize;

    unsafe extern "system" fn enum_cb(hwnd: HWND, lparam: LPARAM) -> windows::Win32::Foundation::BOOL {
        let state = &*(lparam.0 as *const Mutex<SearchState>);
        let mut st = state.lock();

        // Check PID first — cheap, no string allocation
        let mut pid: u32 = 0;
        GetWindowThreadProcessId(hwnd, Some(&mut pid));
        if pid != st.current_pid {
            return windows::Win32::Foundation::TRUE;  // keep enumerating
        }

        // Read window title
        let mut buf = [0u16; 256];
        let len = windows::Win32::UI::WindowsAndMessaging::GetWindowTextW(hwnd, &mut buf);
        if len == 0 {
            return windows::Win32::Foundation::TRUE;
        }
        let title = String::from_utf16_lossy(&buf[..len as usize]);
        if title == st.target_title {
            st.found = Some(hwnd);
            return windows::Win32::Foundation::FALSE;  // stop
        }
        windows::Win32::Foundation::TRUE
    }

    unsafe {
        let _ = EnumWindows(Some(enum_cb), LPARAM(state_ptr));
    }

    Arc::try_unwrap(state).ok()?.into_inner().found
}

// ── topmost ──────────────────────────────────────────────────────────────────

pub fn set_topmost(hwnd: HWND) {
    unsafe {
        let _ = SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
    println!("[win32] topmost set");
}

// ── click-through ─────────────────────────────────────────────────────────────

pub fn set_click_through(hwnd: HWND, enable: bool) {
    unsafe {
        let cur = GetWindowLongW(hwnd, GWL_EXSTYLE) as u32;
        let next = if enable {
            cur | WS_EX_LAYERED.0 | WS_EX_TRANSPARENT.0
        } else {
            (cur | WS_EX_LAYERED.0) & !WS_EX_TRANSPARENT.0
        };
        SetWindowLongW(hwnd, GWL_EXSTYLE, next as i32);
    }
    println!("[win32] click-through = {enable}");
}

// ── taskbar positioning ───────────────────────────────────────────────────────

/// 与 TaskbarPlacementService 相同策略：
/// FindWindow("Shell_TrayWnd") → GetWindowRect → SetWindowPos
/// 不使用 AppBar/SHAppBarMessage（已读源码确认）
pub fn position_near_taskbar(hwnd: HWND, win_w: i32, win_h: i32) {
    unsafe {
        let Ok(taskbar) = FindWindowW(w!("Shell_TrayWnd"), None) else {
            println!("[win32] Shell_TrayWnd not found");
            return;
        };
        let mut tr = RECT::default();
        if GetWindowRect(taskbar, &mut tr).is_err() { return; }

        // Centre vertically in taskbar, 220px gap left of tray area
        let left = tr.right - win_w - 220 - 8;
        let top  = tr.top + (tr.bottom - tr.top - win_h) / 2;

        let _ = SetWindowPos(hwnd, HWND_TOPMOST, left, top, win_w, win_h, SWP_NOACTIVATE);
        println!("[win32] placed at ({left}, {top}), taskbar=({},{},{},{})",
            tr.left, tr.top, tr.right, tr.bottom);
    }
}

// ── global hotkey Ctrl+Alt+M ─────────────────────────────────────────────────

/// Registers Ctrl+Alt+M on a dedicated thread.
/// WS_EX_TRANSPARENT means the window gets no mouse events — hotkey is the only toggle.
pub fn start_hotkey_thread<A>(app_weak: Weak<A>, on_toggle: impl Fn(&A) + Send + 'static)
where
    A: slint::ComponentHandle + 'static,
{
    let cb = Arc::new(Mutex::new(on_toggle));
    std::thread::Builder::new()
        .name("hotkey".into())
        .spawn(move || {
            const HOTKEY_ID: i32 = 1;
            const VK_M: u32      = 0x4D;

            unsafe {
                if RegisterHotKey(None, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_M).is_err() {
                    eprintln!("[win32] RegisterHotKey Ctrl+Alt+M failed (already in use?)");
                    return;
                }
                println!("[win32] hotkey Ctrl+Alt+M registered");

                let mut msg = MSG::default();
                loop {
                    // GetMessageW returns BOOL: >0 normal, 0 WM_QUIT, -1 error
                    let r = GetMessageW(&mut msg, None, WM_HOTKEY, WM_HOTKEY);
                    if r.0 <= 0 { break; }
                    if msg.message == WM_HOTKEY && msg.wParam.0 as i32 == HOTKEY_ID {
                        let cb2 = cb.clone();
                        let w2  = app_weak.clone();
                        slint::invoke_from_event_loop(move || {
                            if let Some(app) = w2.upgrade() {
                                (cb2.lock())(&app);
                            }
                        }).ok();
                    }
                }
            }
        })
        .expect("hotkey thread spawn failed");
}

// ── memory reporter ───────────────────────────────────────────────────────────

pub fn print_memory() {
    unsafe {
        let mut pmc = PROCESS_MEMORY_COUNTERS::default();
        pmc.cb = std::mem::size_of::<PROCESS_MEMORY_COUNTERS>() as u32;
        if GetProcessMemoryInfo(GetCurrentProcess(), &mut pmc, pmc.cb).is_ok() {
            println!(
                "[memory] WorkingSet={:.1} MiB  PrivateBytes={:.1} MiB",
                pmc.WorkingSetSize as f64 / 1_048_576.0,
                pmc.PagefileUsage  as f64 / 1_048_576.0,
            );
        }
    }
}
