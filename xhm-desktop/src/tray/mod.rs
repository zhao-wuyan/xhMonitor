//! TASK-002 Windows tray decision and active-loop bridge.
//!
//! `tray-icon` 0.24.1 was evaluated first as required. Its public API exposes
//! icon/menu events but no balloon notification or notification-click event, so
//! it cannot complete the required matrix. The active implementation is therefore
//! the planned, mutually exclusive `Shell_NotifyIconW` fallback in [`native`].
//! Its window procedure only forwards [`TrayCommand`] values; Slint state changes
//! remain on the active-loop timer in `lib.rs`.

pub mod event_bridge;

pub use event_bridge::{channel, TrayCommand, TrayCommandReceiver, TrayCommandSender};

use std::path::Path;

#[cfg(windows)]
mod native;
#[cfg(windows)]
pub use native::TrayHandle;

#[cfg(windows)]
pub fn build_tray(
    tooltip: &str,
    icon_path: &Path,
    sender: TrayCommandSender,
) -> std::io::Result<TrayHandle> {
    TrayHandle::build(tooltip, icon_path, sender)
}

#[cfg(not(windows))]
#[derive(Debug)]
pub struct TrayHandle;

#[cfg(not(windows))]
pub fn build_tray(
    _tooltip: &str,
    _icon_path: &Path,
    _sender: TrayCommandSender,
) -> std::io::Result<TrayHandle> {
    Err(std::io::Error::new(
        std::io::ErrorKind::Unsupported,
        "Windows notification area is unavailable",
    ))
}
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn menu_id_strings_are_unique_and_stable() {
        // id 字符串是 plan 七项 command 的稳定契约；变更需同步 event_bridge。
        let ids = vec![
            TrayCommand::ShowHide.id(),
            TrayCommand::OpenWeb.id(),
            TrayCommand::ClickThrough.id(),
            TrayCommand::AdminMode.id(),
            TrayCommand::Settings.id(),
            TrayCommand::About.id(),
            TrayCommand::Exit.id(),
        ];
        let mut sorted = ids.clone();
        sorted.sort_unstable();
        sorted.dedup();
        assert_eq!(sorted.len(), ids.len(), "tray command ids must be unique");

        // 从 id 字符串回到 command 的双向映射。
        for id in ids {
            assert!(
                TrayCommand::from_menu_id(id).is_some(),
                "id {id} must round-trip to a command"
            );
        }
    }

    #[test]
    fn from_menu_id_returns_none_for_unknown() {
        assert!(TrayCommand::from_menu_id("does.not.exist").is_none());
        assert!(TrayCommand::from_menu_id("").is_none());
    }
}
