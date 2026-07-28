//! G2 双窗口 shell 控制器与 Windows 壳生命周期装配（TASK-008）。
//!
//! 纯控制器与 [`crate::win32`] 原生边界分离：控制器只持有窗口标题、
//! [`DesktopState`] 句柄和 SSE cancellation token，便于在单测中以 fake
//! HWND/位置操作验证生命周期；真正的 Slint window、tray 与 SSE session
//! 在 `bootstrap` 中按 POC 顺序接线。

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

use crate::desktop_state::DesktopState;
use crate::win32::{
    find_own_hwnd, place_window_near_taskbar, ClickThroughOps, Placement, SingleInstanceGuard,
    TaskbarQuery, TopmostOps, WindowEnumerator, WindowHandle, WindowPositionOps,
};

/// G2 固定的双窗口标题。两个窗口都在当前 PID 下，HWND 解析靠精确 title 区分。
pub const FLOATING_TITLE: &str = "xhm-desktop";
pub const TASKBAR_TITLE: &str = "xhm-desktop-taskbar";

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ActiveDisplayMode {
    None,
    Floating,
    EdgeDock,
}

/// 单窗口生命周期句柄。drop 时取消对应 SSE 订阅并隐藏窗口。
pub struct WindowSlot {
    pub title: &'static str,
    pub handle: WindowHandle,
    pub state: Arc<std::sync::Mutex<DesktopState>>,
    pub cancel: tokio_util::sync::CancellationToken,
}

impl WindowSlot {
    pub fn new(title: &'static str, pid: u32, enumerator: &dyn WindowEnumerator) -> Option<Self> {
        let handle = find_own_hwnd(enumerator, title, pid)?;
        Some(Self {
            title,
            handle,
            state: Arc::new(std::sync::Mutex::new(DesktopState::new())),
            cancel: tokio_util::sync::CancellationToken::new(),
        })
    }
}

/// 双窗口 shell 控制器。纯逻辑层负责窗口解析、位置、topmost 与 click-through
/// 调度；Slint 窗口的创建/显示由 `bootstrap` 负责。
pub struct ShellController {
    pub floating: WindowSlot,
    pub taskbar: WindowSlot,
    pub active_mode: ActiveDisplayMode,
    pub click_through_enabled: bool,
    pub single_instance: SingleInstanceGuard,
}

impl ShellController {
    pub fn new(
        pid: u32,
        enumerator: &dyn WindowEnumerator,
        single_instance: SingleInstanceGuard,
    ) -> Option<Self> {
        let floating = WindowSlot::new(FLOATING_TITLE, pid, enumerator)?;
        let taskbar = WindowSlot::new(TASKBAR_TITLE, pid, enumerator)?;
        Some(Self {
            floating,
            taskbar,
            active_mode: ActiveDisplayMode::Floating,
            click_through_enabled: false,
            single_instance,
        })
    }

    pub fn resolve_mode(&mut self, mode: ActiveDisplayMode) {
        self.active_mode = mode;
    }

    pub fn place_taskbar_window(
        &mut self,
        query: &dyn TaskbarQuery,
        positioner: &dyn WindowPositionOps,
    ) -> Option<Placement> {
        place_window_near_taskbar(query, positioner, self.taskbar.handle)
    }

    pub fn reassert_topmost(&self, topmost: &dyn TopmostOps) {
        topmost.bring_to_top(self.floating.handle, crate::win32::topmost_flags());
        topmost.bring_to_top(self.taskbar.handle, crate::win32::topmost_flags());
    }

    pub fn toggle_click_through(&mut self, click_ops: &dyn ClickThroughOps) -> Option<u32> {
        self.click_through_enabled = !self.click_through_enabled;
        crate::win32::apply_click_through(
            click_ops,
            self.floating.handle,
            self.click_through_enabled,
        )
    }

    pub fn shutdown(&mut self) {
        self.floating.cancel.cancel();
        self.taskbar.cancel.cancel();
    }
}

/// `bootstrap` 使用的线程安全 click-through 观察值。
#[derive(Debug, Default)]
pub struct ClickThroughFlag {
    enabled: AtomicBool,
}

impl ClickThroughFlag {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn set(&self, enabled: bool) {
        self.enabled.store(enabled, Ordering::SeqCst);
    }

    pub fn get(&self) -> bool {
        self.enabled.load(Ordering::SeqCst)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::win32::{
        ClickThroughOps, FakeWindowEnumerator, PhysicalPoint, PhysicalRect, PhysicalSize,
        PlacementCalculator, PlacementInput, TaskbarEdge, TaskbarSnapshot, TopmostOps, WindowEntry,
        WindowHandle,
    };
    use std::cell::RefCell;
    use std::collections::HashMap;

    fn fake_windows() -> FakeWindowEnumerator {
        FakeWindowEnumerator::new(vec![
            WindowEntry {
                handle: WindowHandle(1001),
                pid: 4321,
                title: FLOATING_TITLE.into(),
            },
            WindowEntry {
                handle: WindowHandle(1002),
                pid: 4321,
                title: TASKBAR_TITLE.into(),
            },
            WindowEntry {
                handle: WindowHandle(1003),
                pid: 9999,
                title: FLOATING_TITLE.into(),
            },
        ])
    }

    #[derive(Default)]
    struct FakeOps {
        ex_styles: RefCell<HashMap<WindowHandle, u32>>,
        topmost: RefCell<HashMap<WindowHandle, u32>>,
        window_rects: RefCell<HashMap<WindowHandle, PhysicalRect>>,
        moved: RefCell<HashMap<WindowHandle, PhysicalPoint>>,
    }

    impl ClickThroughOps for FakeOps {
        fn ex_style(&self, handle: WindowHandle) -> Option<u32> {
            self.ex_styles.borrow().get(&handle).copied()
        }
        fn set_ex_style(&self, handle: WindowHandle, value: u32) -> bool {
            self.ex_styles.borrow_mut().insert(handle, value);
            true
        }
    }

    impl TopmostOps for FakeOps {
        fn bring_to_top(&self, handle: WindowHandle, flags: u32) -> bool {
            self.topmost.borrow_mut().insert(handle, flags);
            true
        }
    }

    impl WindowPositionOps for FakeOps {
        fn window_rect(&self, handle: WindowHandle) -> Option<PhysicalRect> {
            self.window_rects.borrow().get(&handle).copied()
        }
        fn move_topmost(&self, handle: WindowHandle, x: i32, y: i32) -> bool {
            self.moved
                .borrow_mut()
                .insert(handle, PhysicalPoint::new(x, y));
            true
        }
    }

    struct FakeTaskbarQuery(Option<TaskbarSnapshot>);

    impl TaskbarQuery for FakeTaskbarQuery {
        fn snapshot(&self) -> Option<TaskbarSnapshot> {
            self.0
        }
    }

    fn fake_snapshot() -> TaskbarSnapshot {
        TaskbarSnapshot {
            taskbar: PhysicalRect::new(0, 1040, 1920, 1080),
            tray: Some(PhysicalRect::new(1700, 1040, 1920, 1080)),
            task_list: Some(PhysicalRect::new(100, 1040, 1500, 1080)),
            virtual_screen: PhysicalRect::new(0, 0, 1920, 1080),
        }
    }

    fn fake_controller(guard: SingleInstanceGuard) -> ShellController {
        let controller = ShellController::new(4321, &fake_windows(), guard).unwrap();
        assert_eq!(controller.floating.handle, WindowHandle(1001));
        assert_eq!(controller.taskbar.handle, WindowHandle(1002));
        controller
    }

    #[test]
    fn resolves_only_current_pid_windows_with_distinct_titles() {
        let guard = SingleInstanceGuard::acquire("xhm-desktop-tests-resolve").unwrap();
        let mut controller = fake_controller(guard);
        assert_eq!(controller.active_mode, ActiveDisplayMode::Floating);
        controller.resolve_mode(ActiveDisplayMode::EdgeDock);
        assert_eq!(controller.active_mode, ActiveDisplayMode::EdgeDock);
    }

    #[test]
    fn place_taskbar_routes_through_calculator_and_preserves_size() {
        let guard = SingleInstanceGuard::acquire("xhm-desktop-tests-place").unwrap();
        let mut controller = fake_controller(guard);
        let ops = FakeOps::default();
        ops.window_rects.borrow_mut().insert(
            controller.taskbar.handle,
            PhysicalRect::new(20, 20, 280, 92),
        );

        let placement = controller
            .place_taskbar_window(&FakeTaskbarQuery(Some(fake_snapshot())), &ops)
            .unwrap();
        assert_eq!(placement.edge, TaskbarEdge::Bottom);
        assert_eq!(placement.origin, PhysicalPoint::new(1508, 1008));
        assert_eq!(
            ops.moved.borrow().get(&controller.taskbar.handle).copied(),
            Some(PhysicalPoint::new(1508, 1008))
        );
    }

    #[test]
    fn reassert_topmost_touches_both_windows() {
        let guard = SingleInstanceGuard::acquire("xhm-desktop-tests-topmost").unwrap();
        let controller = fake_controller(guard);
        let ops = FakeOps::default();
        controller.reassert_topmost(&ops);
        assert!(ops
            .topmost
            .borrow()
            .contains_key(&controller.floating.handle));
        assert!(ops
            .topmost
            .borrow()
            .contains_key(&controller.taskbar.handle));
    }

    #[test]
    fn toggle_click_through_flips_only_floating_window() {
        let guard = SingleInstanceGuard::acquire("xhm-desktop-tests-click").unwrap();
        let mut controller = fake_controller(guard);
        let ops = FakeOps::default();
        ops.ex_styles
            .borrow_mut()
            .insert(controller.floating.handle, 0);

        controller.toggle_click_through(&ops);
        assert!(controller.click_through_enabled);
        assert_eq!(
            *ops.ex_styles
                .borrow()
                .get(&controller.floating.handle)
                .unwrap(),
            crate::win32::EX_LAYERED | crate::win32::EX_TRANSPARENT
        );

        controller.toggle_click_through(&ops);
        assert!(!controller.click_through_enabled);
        assert_eq!(
            *ops.ex_styles
                .borrow()
                .get(&controller.floating.handle)
                .unwrap()
                & crate::win32::EX_TRANSPARENT,
            0
        );
        assert!(!ops
            .ex_styles
            .borrow()
            .contains_key(&controller.taskbar.handle));
    }

    #[test]
    fn shutdown_cancels_both_subscriptions() {
        let guard = SingleInstanceGuard::acquire("xhm-desktop-tests-shutdown").unwrap();
        let mut controller = fake_controller(guard);
        assert!(!controller.floating.cancel.is_cancelled());
        assert!(!controller.taskbar.cancel.is_cancelled());
        controller.shutdown();
        assert!(controller.floating.cancel.is_cancelled());
        assert!(controller.taskbar.cancel.is_cancelled());
    }

    #[test]
    fn placement_calculator_matches_pure_path() {
        let placement = PlacementCalculator::calculate(PlacementInput {
            window_size: PhysicalSize::new(260, 32),
            taskbar: PhysicalRect::new(0, 0, 1920, 40),
            tray: Some(PhysicalRect::new(1700, 0, 1920, 40)),
            task_list: None,
            virtual_screen: PhysicalRect::new(0, 0, 1920, 1080),
        })
        .unwrap();
        assert_eq!(placement.edge, TaskbarEdge::Top);
        assert_eq!(placement.origin, PhysicalPoint::new(1432, 4));
    }
}
