//! Rust 桌面壳窗口位置持久化。
//!
//! 字段语义复制 `FloatingWindow.xaml.cs:1438-1497` 的 Left/Top/Width/Height，
//! 但路径隔离为 `%APPDATA%\\XhMonitor\\xhm-desktop\\window.json`，不会写入
//! C# 的 `%APPDATA%\\XhMonitor.Desktop\\window.json`。

use crate::win32::{clamp_axis, PhysicalPoint, PhysicalRect, PhysicalSize};
use serde::{Deserialize, Serialize};
use std::io;
use std::path::{Path, PathBuf};

const VENDOR_DIR: &str = "XhMonitor";
const PRODUCT_DIR: &str = "xhm-desktop";
const FILE_NAME: &str = "window.json";

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct WindowPlacement {
    pub left: f64,
    pub top: f64,
    pub width: f64,
    pub height: f64,
}

impl WindowPlacement {
    pub fn from_rect(rect: PhysicalRect) -> Option<Self> {
        rect.is_valid().then_some(Self {
            left: f64::from(rect.left),
            top: f64::from(rect.top),
            width: f64::from(rect.width()),
            height: f64::from(rect.height()),
        })
    }

    pub fn clamped_rect(self, virtual_screen: PhysicalRect) -> Option<PhysicalRect> {
        if !virtual_screen.is_valid()
            || !self.left.is_finite()
            || !self.top.is_finite()
            || !self.width.is_finite()
            || !self.height.is_finite()
            || self.width <= 0.0
            || self.height <= 0.0
        {
            return None;
        }

        let width = round_i32(self.width).clamp(1, virtual_screen.width());
        let height = round_i32(self.height).clamp(1, virtual_screen.height());
        let left = clamp_axis(
            round_i32(self.left),
            virtual_screen.left,
            virtual_screen.right - width,
        );
        let top = clamp_axis(
            round_i32(self.top),
            virtual_screen.top,
            virtual_screen.bottom - height,
        );
        Some(PhysicalRect::from_origin_size(
            PhysicalPoint::new(left, top),
            PhysicalSize::new(width, height),
        ))
    }
}

#[derive(Debug, Clone)]
pub struct WindowPositionStore {
    file_path: PathBuf,
}

impl WindowPositionStore {
    pub fn from_environment() -> io::Result<Self> {
        let app_data = std::env::var_os("APPDATA").ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::NotFound,
                "APPDATA environment variable is missing",
            )
        })?;
        Ok(Self::from_app_data_dir(app_data))
    }

    /// 注入 AppData 根目录，测试不会访问真实用户目录。
    pub fn from_app_data_dir(base: impl AsRef<Path>) -> Self {
        Self {
            file_path: base
                .as_ref()
                .join(VENDOR_DIR)
                .join(PRODUCT_DIR)
                .join(FILE_NAME),
        }
    }

    pub fn file_path(&self) -> &Path {
        &self.file_path
    }

    pub fn load(&self, virtual_screen: PhysicalRect) -> Option<PhysicalRect> {
        let bytes = std::fs::read(&self.file_path).ok()?;
        let placement: WindowPlacement = serde_json::from_slice(&bytes).ok()?;
        placement.clamped_rect(virtual_screen)
    }

    pub fn save(&self, rect: PhysicalRect) -> io::Result<()> {
        let placement = WindowPlacement::from_rect(rect).ok_or_else(|| {
            io::Error::new(io::ErrorKind::InvalidInput, "window rectangle is invalid")
        })?;
        if let Some(parent) = self.file_path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let json = serde_json::to_vec_pretty(&placement)
            .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
        std::fs::write(&self.file_path, json)
    }
}

fn round_i32(value: f64) -> i32 {
    value
        .round()
        .clamp(f64::from(i32::MIN), f64::from(i32::MAX)) as i32
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn temp_app_data(case: &str) -> PathBuf {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        std::env::temp_dir().join(format!(
            "xhm-desktop-persistence-{}-{case}-{nonce}",
            std::process::id()
        ))
    }

    #[test]
    fn path_uses_rust_specific_namespace() {
        let store = WindowPositionStore::from_app_data_dir("C:/Users/test/AppData/Roaming");
        let normalized = store.file_path().to_string_lossy().replace('\\', "/");
        assert!(normalized.ends_with("XhMonitor/xhm-desktop/window.json"));
        assert!(!normalized.contains("XhMonitor.Desktop"));
    }

    #[test]
    fn temp_directory_round_trip_preserves_rect() {
        let base = temp_app_data("round-trip");
        let store = WindowPositionStore::from_app_data_dir(&base);
        let rect = PhysicalRect::new(-1800, 120, -1540, 192);
        store.save(rect).unwrap();
        assert_eq!(
            store.load(PhysicalRect::new(-1920, 0, 1920, 1080)),
            Some(rect)
        );
        std::fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn restart_load_clamps_position_and_size_to_current_virtual_screen() {
        let placement = WindowPlacement {
            left: 5000.0,
            top: -5000.0,
            width: 5000.0,
            height: 3000.0,
        };
        assert_eq!(
            placement.clamped_rect(PhysicalRect::new(-1920, -1080, 1920, 1080)),
            Some(PhysicalRect::new(-1920, -1080, 1920, 1080))
        );
    }

    #[test]
    fn missing_bad_json_and_invalid_numbers_fall_back() {
        let base = temp_app_data("bad");
        let store = WindowPositionStore::from_app_data_dir(&base);
        let virtual_screen = PhysicalRect::new(0, 0, 1920, 1080);
        assert_eq!(store.load(virtual_screen), None);

        std::fs::create_dir_all(store.file_path().parent().unwrap()).unwrap();
        std::fs::write(store.file_path(), b"{not-json").unwrap();
        assert_eq!(store.load(virtual_screen), None);

        std::fs::write(
            store.file_path(),
            br#"{"left":0.0,"top":0.0,"width":0.0,"height":72.0}"#,
        )
        .unwrap();
        assert_eq!(store.load(virtual_screen), None);
        std::fs::remove_dir_all(base).unwrap();
    }

    #[test]
    fn save_rejects_invalid_rect() {
        let base = temp_app_data("invalid-save");
        let store = WindowPositionStore::from_app_data_dir(&base);
        let error = store.save(PhysicalRect::new(10, 10, 10, 20)).unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::InvalidInput);
        assert!(!store.file_path().exists());
    }
}
