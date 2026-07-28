//! Tray event bridge: native window callbacks -> pure Rust [`TrayCommand`].
//!
//! The callback only sends to the UI queue. The Slint timer owns all component
//! and window updates, preserving the active-loop boundary required by TASK-002.

use std::sync::mpsc;

/// 托盘可投递的七类 UI 命令（对齐 `TrayIconService.cs` 右键菜单语义）。
///
/// `id()` is the stable diagnostic/event contract; `from_menu_id` is its inverse.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum TrayCommand {
    ShowHide,
    OpenWeb,
    ClickThrough,
    AdminMode,
    Settings,
    About,
    Exit,
}

impl TrayCommand {
    /// Stable command id shared by diagnostics, tests, and artifact evidence.
    pub fn id(self) -> &'static str {
        match self {
            TrayCommand::ShowHide => "tray.show_hide",
            TrayCommand::OpenWeb => "tray.open_web",
            TrayCommand::ClickThrough => "tray.click_through",
            TrayCommand::AdminMode => "tray.admin_mode",
            TrayCommand::Settings => "tray.settings",
            TrayCommand::About => "tray.about",
            TrayCommand::Exit => "tray.exit",
        }
    }

    /// 菜单事件 id → command 的逆映射；未知 id 返回 `None`。
    pub fn from_menu_id(id: &str) -> Option<Self> {
        match id {
            "tray.show_hide" => Some(TrayCommand::ShowHide),
            "tray.open_web" => Some(TrayCommand::OpenWeb),
            "tray.click_through" => Some(TrayCommand::ClickThrough),
            "tray.admin_mode" => Some(TrayCommand::AdminMode),
            "tray.settings" => Some(TrayCommand::Settings),
            "tray.about" => Some(TrayCommand::About),
            "tray.exit" => Some(TrayCommand::Exit),
            _ => None,
        }
    }
}

impl std::fmt::Display for TrayCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.id())
    }
}

/// std::sync::mpsc 异步安全包装：handler 线程 send，UI 线程 try_recv。
pub type TrayCommandSender = mpsc::Sender<TrayCommand>;
/// UI 侧轮询 receiver（在 Slint Timer 中 `try_recv`）。
pub type TrayCommandReceiver = mpsc::Receiver<TrayCommand>;

/// 创建托盘命令 channel。
pub fn channel() -> (TrayCommandSender, TrayCommandReceiver) {
    mpsc::channel()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn command_id_round_trip() {
        let all = [
            TrayCommand::ShowHide,
            TrayCommand::OpenWeb,
            TrayCommand::ClickThrough,
            TrayCommand::AdminMode,
            TrayCommand::Settings,
            TrayCommand::About,
            TrayCommand::Exit,
        ];
        for command in all {
            assert_eq!(TrayCommand::from_menu_id(command.id()), Some(command));
        }
    }

    #[test]
    fn unknown_menu_id_returns_none() {
        assert!(TrayCommand::from_menu_id("tray.unknown").is_none());
        assert!(TrayCommand::from_menu_id("").is_none());
    }

    #[test]
    fn channel_delivers_commands_in_order() {
        let (tx, rx) = channel();
        tx.send(TrayCommand::ShowHide).unwrap();
        tx.send(TrayCommand::Exit).unwrap();
        assert_eq!(rx.try_recv(), Ok(TrayCommand::ShowHide));
        assert_eq!(rx.try_recv(), Ok(TrayCommand::Exit));
        assert!(rx.try_recv().is_err());
    }

    #[test]
    fn sender_failure_when_receiver_dropped() {
        let (tx, rx) = channel();
        drop(rx);
        assert!(tx.send(TrayCommand::Exit).is_err());
    }
}
