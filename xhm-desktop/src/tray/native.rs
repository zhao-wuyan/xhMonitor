use std::cell::Cell;
use std::io;
use std::mem::size_of;
use std::path::Path;
use std::ptr;

use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, POINT, WPARAM};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::Shell::{
    Shell_NotifyIconW, NIF_ICON, NIF_INFO, NIF_MESSAGE, NIF_TIP, NIIF_INFO, NIM_ADD, NIM_DELETE,
    NIM_MODIFY, NIN_BALLOONSHOW, NIN_BALLOONUSERCLICK, NOTIFYICONDATAW,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CheckMenuItem, CreatePopupMenu, CreateWindowExW, DefWindowProcW, DestroyIcon,
    DestroyMenu, DestroyWindow, GetCursorPos, LoadImageW, PostMessageW, RegisterClassW,
    SetForegroundWindow, TrackPopupMenu, CREATESTRUCTW, CS_DBLCLKS, GWLP_USERDATA, HICON,
    IMAGE_ICON, LR_DEFAULTSIZE, LR_LOADFROMFILE, MF_BYCOMMAND, MF_CHECKED, MF_GRAYED, MF_SEPARATOR,
    MF_STRING, MF_UNCHECKED, TPM_RIGHTBUTTON, WM_APP, WM_COMMAND, WM_LBUTTONDBLCLK, WM_NCCREATE,
    WM_NCDESTROY, WM_NULL, WM_RBUTTONUP, WNDCLASSW, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
    WS_OVERLAPPED,
};

use super::{TrayCommand, TrayCommandSender};

const TRAY_ICON_ID: u32 = 1;
const WM_TRAY_ICON: u32 = WM_APP + 0x51;

const MENU_SHOW_HIDE: u32 = 1_001;
const MENU_OPEN_WEB: u32 = 1_002;
const MENU_CLICK_THROUGH: u32 = 1_003;
const MENU_ADMIN_MODE: u32 = 1_004;
const MENU_SETTINGS: u32 = 1_005;
const MENU_ABOUT: u32 = 1_006;
const MENU_EXIT: u32 = 1_007;

struct NativeTrayData {
    sender: TrayCommandSender,
    menu: windows_sys::Win32::UI::WindowsAndMessaging::HMENU,
    icon: HICON,
}

impl Drop for NativeTrayData {
    fn drop(&mut self) {
        unsafe {
            if !self.menu.is_null() {
                DestroyMenu(self.menu);
            }
            if !self.icon.is_null() {
                DestroyIcon(self.icon);
            }
        }
    }
}

/// Owns the single active `Shell_NotifyIconW` fallback selected by TASK-002.
/// The hidden window, menu, and icon are created and destroyed on Slint's UI thread.
#[derive(Debug)]
pub struct TrayHandle {
    hwnd: HWND,
    menu: windows_sys::Win32::UI::WindowsAndMessaging::HMENU,
    click_through_checked: Cell<bool>,
}

impl TrayHandle {
    pub fn build(tooltip: &str, icon_path: &Path, sender: TrayCommandSender) -> io::Result<Self> {
        unsafe {
            let menu = build_menu()?;
            let icon = match load_icon(icon_path) {
                Ok(icon) => icon,
                Err(error) => {
                    DestroyMenu(menu);
                    return Err(error);
                }
            };

            let instance = GetModuleHandleW(ptr::null());
            if instance.is_null() {
                DestroyMenu(menu);
                DestroyIcon(icon);
                return Err(io::Error::last_os_error());
            }

            let class_name = wide(&format!("xhmonitor_shell_notify_{}", std::process::id()));
            let window_class = WNDCLASSW {
                style: CS_DBLCLKS,
                lpfnWndProc: Some(window_proc),
                hInstance: instance,
                lpszClassName: class_name.as_ptr(),
                ..std::mem::zeroed()
            };
            if RegisterClassW(&window_class) == 0 {
                DestroyMenu(menu);
                DestroyIcon(icon);
                return Err(io::Error::last_os_error());
            }

            let hwnd = CreateWindowExW(
                WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                class_name.as_ptr(),
                ptr::null(),
                WS_OVERLAPPED,
                0,
                0,
                0,
                0,
                ptr::null_mut(),
                ptr::null_mut(),
                instance,
                ptr::null(),
            );
            if hwnd.is_null() {
                DestroyMenu(menu);
                DestroyIcon(icon);
                return Err(io::Error::last_os_error());
            }

            let data = Box::new(NativeTrayData { sender, menu, icon });
            let data_ptr = Box::into_raw(data);
            set_window_data(hwnd, data_ptr as isize);
            if get_window_data(hwnd) != data_ptr as isize {
                let _ = Box::from_raw(data_ptr);
                DestroyWindow(hwnd);
                return Err(io::Error::last_os_error());
            }

            let mut notify = notify_data(hwnd);
            notify.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            notify.uCallbackMessage = WM_TRAY_ICON;
            notify.hIcon = icon;
            copy_wide(&mut notify.szTip, tooltip);
            if Shell_NotifyIconW(NIM_ADD, &notify) == 0 {
                DestroyWindow(hwnd);
                return Err(io::Error::last_os_error());
            }

            Ok(Self {
                hwnd,
                menu,
                click_through_checked: Cell::new(false),
            })
        }
    }

    pub fn show_notification(&self, title: &str, body: &str) -> io::Result<()> {
        unsafe {
            let mut notify = notify_data(self.hwnd);
            notify.uFlags = NIF_INFO;
            notify.dwInfoFlags = NIIF_INFO;
            notify.Anonymous.uTimeout = 5_000;
            copy_wide(&mut notify.szInfoTitle, title);
            copy_wide(&mut notify.szInfo, body);
            if Shell_NotifyIconW(NIM_MODIFY, &notify) == 0 {
                return Err(io::Error::last_os_error());
            }
            Ok(())
        }
    }

    pub fn set_click_through_checked(&self, checked: bool) -> io::Result<()> {
        let flags = MF_BYCOMMAND | if checked { MF_CHECKED } else { MF_UNCHECKED };
        let result = unsafe { CheckMenuItem(self.menu, MENU_CLICK_THROUGH, flags) };
        if result == u32::MAX {
            return Err(io::Error::last_os_error());
        }
        self.click_through_checked.set(checked);
        Ok(())
    }

    pub fn is_click_through_checked(&self) -> bool {
        self.click_through_checked.get()
    }
}

impl Drop for TrayHandle {
    fn drop(&mut self) {
        unsafe {
            if !self.hwnd.is_null() {
                let notify = notify_data(self.hwnd);
                Shell_NotifyIconW(NIM_DELETE, &notify);
                DestroyWindow(self.hwnd);
                self.hwnd = ptr::null_mut();
            }
        }
    }
}

unsafe extern "system" fn window_proc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    if message == WM_NCCREATE {
        let _ = lparam as *const CREATESTRUCTW;
        return DefWindowProcW(hwnd, message, wparam, lparam);
    }

    let data_ptr = get_window_data(hwnd) as *mut NativeTrayData;
    if message == WM_NCDESTROY {
        set_window_data(hwnd, 0);
        let result = DefWindowProcW(hwnd, message, wparam, lparam);
        if !data_ptr.is_null() {
            drop(Box::from_raw(data_ptr));
        }
        return result;
    }
    if data_ptr.is_null() {
        return DefWindowProcW(hwnd, message, wparam, lparam);
    }
    let data = &*data_ptr;

    match message {
        WM_COMMAND => {
            if let Some(command) = command_from_native_id((wparam & 0xffff) as u32) {
                forward(data, command, "menu");
            }
            0
        }
        WM_TRAY_ICON => match lparam as u32 {
            WM_RBUTTONUP => {
                show_menu(hwnd, data.menu);
                0
            }
            WM_LBUTTONDBLCLK => {
                forward(data, TrayCommand::ShowHide, "double-click");
                0
            }
            NIN_BALLOONSHOW => {
                tracing::info!("TRAY_MATRIX notification-shown");
                0
            }
            NIN_BALLOONUSERCLICK => {
                tracing::info!("TRAY_MATRIX notification-click");
                forward(data, TrayCommand::About, "notification-click");
                0
            }
            _ => DefWindowProcW(hwnd, message, wparam, lparam),
        },
        _ => DefWindowProcW(hwnd, message, wparam, lparam),
    }
}

unsafe fn show_menu(hwnd: HWND, menu: windows_sys::Win32::UI::WindowsAndMessaging::HMENU) {
    let mut cursor = POINT { x: 0, y: 0 };
    if GetCursorPos(&mut cursor) == 0 {
        return;
    }
    SetForegroundWindow(hwnd);
    TrackPopupMenu(
        menu,
        TPM_RIGHTBUTTON,
        cursor.x,
        cursor.y,
        0,
        hwnd,
        ptr::null(),
    );
    PostMessageW(hwnd, WM_NULL, 0, 0);
}

fn forward(data: &NativeTrayData, command: TrayCommand, source: &'static str) {
    tracing::info!(source, command = %command, "TRAY_MATRIX command-forwarded");
    if let Err(error) = data.sender.send(command) {
        tracing::warn!(%error, "tray command receiver closed");
    }
}

fn command_from_native_id(id: u32) -> Option<TrayCommand> {
    match id {
        MENU_SHOW_HIDE => Some(TrayCommand::ShowHide),
        MENU_OPEN_WEB => Some(TrayCommand::OpenWeb),
        MENU_CLICK_THROUGH => Some(TrayCommand::ClickThrough),
        MENU_ADMIN_MODE => Some(TrayCommand::AdminMode),
        MENU_SETTINGS => Some(TrayCommand::Settings),
        MENU_ABOUT => Some(TrayCommand::About),
        MENU_EXIT => Some(TrayCommand::Exit),
        _ => None,
    }
}

unsafe fn build_menu() -> io::Result<windows_sys::Win32::UI::WindowsAndMessaging::HMENU> {
    let menu = CreatePopupMenu();
    if menu.is_null() {
        return Err(io::Error::last_os_error());
    }

    let result = (|| {
        append_text(menu, MF_STRING, MENU_SHOW_HIDE, "显示/隐藏")?;
        append_text(menu, MF_STRING, MENU_OPEN_WEB, "打开 Web 界面")?;
        append_text(menu, MF_STRING, MENU_CLICK_THROUGH, "点击穿透")?;
        append_text(
            menu,
            MF_STRING | MF_GRAYED,
            MENU_ADMIN_MODE,
            "管理员模式（P3 启用）",
        )?;
        append_separator(menu)?;
        append_text(menu, MF_STRING, MENU_SETTINGS, "设置")?;
        append_text(menu, MF_STRING, MENU_ABOUT, "关于")?;
        append_separator(menu)?;
        append_text(menu, MF_STRING, MENU_EXIT, "退出")?;
        Ok(())
    })();

    if let Err(error) = result {
        DestroyMenu(menu);
        return Err(error);
    }
    Ok(menu)
}

unsafe fn append_text(
    menu: windows_sys::Win32::UI::WindowsAndMessaging::HMENU,
    flags: u32,
    id: u32,
    text: &str,
) -> io::Result<()> {
    let text = wide(text);
    if AppendMenuW(menu, flags, id as usize, text.as_ptr()) == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

unsafe fn append_separator(
    menu: windows_sys::Win32::UI::WindowsAndMessaging::HMENU,
) -> io::Result<()> {
    if AppendMenuW(menu, MF_SEPARATOR, 0, ptr::null()) == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

unsafe fn load_icon(path: &Path) -> io::Result<HICON> {
    let path = wide(&path.to_string_lossy());
    let icon = LoadImageW(
        ptr::null_mut(),
        path.as_ptr(),
        IMAGE_ICON,
        0,
        0,
        LR_LOADFROMFILE | LR_DEFAULTSIZE,
    ) as HICON;
    if icon.is_null() {
        return Err(io::Error::last_os_error());
    }
    Ok(icon)
}

fn notify_data(hwnd: HWND) -> NOTIFYICONDATAW {
    NOTIFYICONDATAW {
        cbSize: size_of::<NOTIFYICONDATAW>() as u32,
        hWnd: hwnd,
        uID: TRAY_ICON_ID,
        ..unsafe { std::mem::zeroed() }
    }
}

fn copy_wide<const N: usize>(target: &mut [u16; N], value: &str) {
    target.fill(0);
    for (slot, code_unit) in target
        .iter_mut()
        .take(N.saturating_sub(1))
        .zip(value.encode_utf16())
    {
        *slot = code_unit;
    }
}

fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

#[cfg(target_pointer_width = "64")]
unsafe fn get_window_data(hwnd: HWND) -> isize {
    windows_sys::Win32::UI::WindowsAndMessaging::GetWindowLongPtrW(hwnd, GWLP_USERDATA)
}

#[cfg(target_pointer_width = "64")]
unsafe fn set_window_data(hwnd: HWND, value: isize) {
    windows_sys::Win32::UI::WindowsAndMessaging::SetWindowLongPtrW(hwnd, GWLP_USERDATA, value);
}

#[cfg(target_pointer_width = "32")]
unsafe fn get_window_data(hwnd: HWND) -> isize {
    windows_sys::Win32::UI::WindowsAndMessaging::GetWindowLongW(hwnd, GWLP_USERDATA) as isize
}

#[cfg(target_pointer_width = "32")]
unsafe fn set_window_data(hwnd: HWND, value: isize) {
    windows_sys::Win32::UI::WindowsAndMessaging::SetWindowLongW(hwnd, GWLP_USERDATA, value as i32);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn native_menu_ids_cover_exactly_seven_commands() {
        let ids = [
            MENU_SHOW_HIDE,
            MENU_OPEN_WEB,
            MENU_CLICK_THROUGH,
            MENU_ADMIN_MODE,
            MENU_SETTINGS,
            MENU_ABOUT,
            MENU_EXIT,
        ];
        let commands: Vec<_> = ids
            .into_iter()
            .map(|id| command_from_native_id(id).unwrap())
            .collect();
        assert_eq!(commands.len(), 7);
        assert_eq!(commands[0], TrayCommand::ShowHide);
        assert_eq!(commands[6], TrayCommand::Exit);
        assert!(command_from_native_id(999).is_none());
    }

    #[test]
    fn wide_copy_is_nul_terminated_and_truncated() {
        let mut target = [9_u16; 4];
        copy_wide(&mut target, "abcdef");
        assert_eq!(target, [b'a' as u16, b'b' as u16, b'c' as u16, 0]);
    }
}
