//! Deterministic floating-window interaction state and native drag/snap bridge.

use std::time::Instant;

use crate::win32::drag::{exceeds_drag_threshold, snap_floating_window};
use crate::win32::{PhysicalPoint, PhysicalRect, TaskbarEdge};

pub const LONG_PRESS_MS: u64 = 2_000;
pub const LONG_PRESS_CANCEL_MS: u64 = 200;
pub const CLICK_DOWN_MS: u64 = 50;
pub const CLICK_RECOVER_MS: u64 = 150;
pub const KILL_CONFIRM_MS: u64 = 1_000;
pub const KILL_ARC_CIRCUMFERENCE: f32 = 43.98;

pub trait MonotonicClock {
    fn now_ms(&self) -> u64;
}

#[derive(Debug)]
pub struct SystemClock {
    started: Instant,
}

impl SystemClock {
    pub fn new() -> Self {
        Self {
            started: Instant::now(),
        }
    }
}

impl Default for SystemClock {
    fn default() -> Self {
        Self::new()
    }
}

impl MonotonicClock for SystemClock {
    fn now_ms(&self) -> u64 {
        self.started.elapsed().as_millis().min(u128::from(u64::MAX)) as u64
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PointerAction {
    BeginDrag,
    Drag,
    EndDrag,
    Click(String),
    LongPress(String),
}

#[derive(Debug, Clone, PartialEq)]
enum PointerPhase {
    Idle,
    Pressed {
        start: PhysicalPoint,
        metric: String,
        pressed_at_ms: u64,
    },
    Dragging,
    LongPressed {
        metric: String,
    },
    Recovering {
        metric: String,
        started_at_ms: u64,
        from_scale: f32,
        duration_ms: u64,
    },
    ClickFeedback {
        metric: String,
        started_at_ms: u64,
    },
}

#[derive(Debug, Clone, PartialEq)]
pub struct MetricVisual {
    pub metric: String,
    pub scale: f32,
}

#[derive(Debug, Clone, PartialEq)]
pub struct PointerMachine {
    phase: PointerPhase,
}

impl Default for PointerMachine {
    fn default() -> Self {
        Self {
            phase: PointerPhase::Idle,
        }
    }
}

impl PointerMachine {
    pub fn press(&mut self, point: PhysicalPoint, metric: impl Into<String>, now_ms: u64) {
        self.phase = PointerPhase::Pressed {
            start: point,
            metric: metric.into(),
            pressed_at_ms: now_ms,
        };
    }

    pub fn move_pointer(&mut self, point: PhysicalPoint, now_ms: u64) -> Option<PointerAction> {
        match &self.phase {
            PointerPhase::Pressed { start, .. } if exceeds_drag_threshold(*start, point) => {
                self.phase = PointerPhase::Dragging;
                Some(PointerAction::BeginDrag)
            }
            PointerPhase::Dragging => Some(PointerAction::Drag),
            PointerPhase::Pressed { pressed_at_ms, .. }
                if now_ms.saturating_sub(*pressed_at_ms) >= LONG_PRESS_MS =>
            {
                self.tick(now_ms)
            }
            _ => None,
        }
    }

    pub fn tick(&mut self, now_ms: u64) -> Option<PointerAction> {
        let PointerPhase::Pressed {
            metric,
            pressed_at_ms,
            ..
        } = &self.phase
        else {
            return None;
        };
        if now_ms.saturating_sub(*pressed_at_ms) < LONG_PRESS_MS {
            return None;
        }
        let metric = metric.clone();
        self.phase = PointerPhase::LongPressed {
            metric: metric.clone(),
        };
        Some(PointerAction::LongPress(metric))
    }

    pub fn release(&mut self, now_ms: u64) -> Option<PointerAction> {
        match std::mem::replace(&mut self.phase, PointerPhase::Idle) {
            PointerPhase::Pressed {
                metric,
                pressed_at_ms,
                ..
            } if now_ms.saturating_sub(pressed_at_ms) >= LONG_PRESS_MS => {
                self.phase = PointerPhase::Recovering {
                    metric: metric.clone(),
                    started_at_ms: now_ms,
                    from_scale: 0.90,
                    duration_ms: LONG_PRESS_CANCEL_MS,
                };
                Some(PointerAction::LongPress(metric))
            }
            PointerPhase::Pressed { metric, .. } => {
                self.phase = PointerPhase::ClickFeedback {
                    metric: metric.clone(),
                    started_at_ms: now_ms,
                };
                Some(PointerAction::Click(metric))
            }
            PointerPhase::Dragging => Some(PointerAction::EndDrag),
            PointerPhase::LongPressed { metric } => {
                self.phase = PointerPhase::Recovering {
                    metric,
                    started_at_ms: now_ms,
                    from_scale: 0.90,
                    duration_ms: LONG_PRESS_CANCEL_MS,
                };
                None
            }
            phase @ (PointerPhase::Recovering { .. } | PointerPhase::ClickFeedback { .. }) => {
                self.phase = phase;
                None
            }
            PointerPhase::Idle => None,
        }
    }

    pub fn cancel(&mut self, now_ms: u64) -> Option<PointerAction> {
        let visual = self.visual(now_ms);
        let was_dragging = matches!(self.phase, PointerPhase::Dragging);
        if let Some(visual) = visual {
            self.phase = PointerPhase::Recovering {
                metric: visual.metric,
                started_at_ms: now_ms,
                from_scale: visual.scale,
                duration_ms: LONG_PRESS_CANCEL_MS,
            };
        } else {
            self.phase = PointerPhase::Idle;
        }
        was_dragging.then_some(PointerAction::EndDrag)
    }

    pub fn visual(&mut self, now_ms: u64) -> Option<MetricVisual> {
        let visual = match &self.phase {
            PointerPhase::Idle | PointerPhase::Dragging => None,
            PointerPhase::Pressed {
                metric,
                pressed_at_ms,
                ..
            } => {
                let elapsed = now_ms.saturating_sub(*pressed_at_ms).min(LONG_PRESS_MS);
                Some(MetricVisual {
                    metric: metric.clone(),
                    scale: 1.0 - 0.10 * elapsed as f32 / LONG_PRESS_MS as f32,
                })
            }
            PointerPhase::LongPressed { metric } => Some(MetricVisual {
                metric: metric.clone(),
                scale: 0.90,
            }),
            PointerPhase::Recovering {
                metric,
                started_at_ms,
                from_scale,
                duration_ms,
            } => {
                let elapsed = now_ms.saturating_sub(*started_at_ms);
                if elapsed >= *duration_ms {
                    None
                } else {
                    let ratio = elapsed as f32 / *duration_ms as f32;
                    Some(MetricVisual {
                        metric: metric.clone(),
                        scale: *from_scale + (1.0 - *from_scale) * ratio,
                    })
                }
            }
            PointerPhase::ClickFeedback {
                metric,
                started_at_ms,
            } => {
                let elapsed = now_ms.saturating_sub(*started_at_ms);
                if elapsed >= CLICK_RECOVER_MS {
                    None
                } else if elapsed <= CLICK_DOWN_MS {
                    Some(MetricVisual {
                        metric: metric.clone(),
                        scale: 1.0 - 0.08 * elapsed as f32 / CLICK_DOWN_MS as f32,
                    })
                } else {
                    Some(MetricVisual {
                        metric: metric.clone(),
                        scale: 0.92
                            + 0.08 * (elapsed - CLICK_DOWN_MS) as f32
                                / (CLICK_RECOVER_MS - CLICK_DOWN_MS) as f32,
                    })
                }
            }
        };
        if visual.is_none()
            && matches!(
                self.phase,
                PointerPhase::Recovering { .. } | PointerPhase::ClickFeedback { .. }
            )
        {
            self.phase = PointerPhase::Idle;
        }
        visual
    }

    pub fn is_dragging(&self) -> bool {
        matches!(self.phase, PointerPhase::Dragging)
    }

    /// True while a pointer gesture owns the bar (pressed, dragging, or holding
    /// a long-press). Hover-driven panel toggles are suppressed during this
    /// window so drag-time hover jitter cannot thrash the SSE subscription.
    pub fn is_active(&self) -> bool {
        matches!(
            self.phase,
            PointerPhase::Pressed { .. } | PointerPhase::Dragging | PointerPhase::LongPressed { .. }
        )
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum KillDecision {
    Confirm { pid: u32, name: String },
    Execute { pid: u32, name: String },
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum KillPhase {
    Idle,
    Confirming {
        pid: u32,
        name: String,
        started_at_ms: u64,
    },
    Killing {
        pid: u32,
    },
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct KillMachine {
    phase: KillPhase,
}

impl Default for KillMachine {
    fn default() -> Self {
        Self {
            phase: KillPhase::Idle,
        }
    }
}

impl KillMachine {
    pub fn click(
        &mut self,
        pid: u32,
        name: impl Into<String>,
        now_ms: u64,
    ) -> Option<KillDecision> {
        let name = name.into();
        match &self.phase {
            KillPhase::Confirming {
                pid: confirming_pid,
                name: confirming_name,
                started_at_ms,
            } if *confirming_pid == pid
                && now_ms.saturating_sub(*started_at_ms) < KILL_CONFIRM_MS =>
            {
                let decision = KillDecision::Execute {
                    pid,
                    name: confirming_name.clone(),
                };
                self.phase = KillPhase::Killing { pid };
                Some(decision)
            }
            KillPhase::Killing { .. } => None,
            _ => {
                self.phase = KillPhase::Confirming {
                    pid,
                    name: name.clone(),
                    started_at_ms: now_ms,
                };
                Some(KillDecision::Confirm { pid, name })
            }
        }
    }

    pub fn tick(&mut self, now_ms: u64) {
        if matches!(
            self.phase,
            KillPhase::Confirming { started_at_ms, .. }
                if now_ms.saturating_sub(started_at_ms) >= KILL_CONFIRM_MS
        ) {
            self.phase = KillPhase::Idle;
        }
    }

    pub fn confirming(&self, now_ms: u64) -> Option<(u32, f32)> {
        let KillPhase::Confirming {
            pid, started_at_ms, ..
        } = self.phase
        else {
            return None;
        };
        let elapsed = now_ms.saturating_sub(started_at_ms);
        (elapsed < KILL_CONFIRM_MS).then_some((pid, elapsed as f32 / KILL_CONFIRM_MS as f32))
    }

    pub fn complete(&mut self, pid: u32) {
        if matches!(self.phase, KillPhase::Killing { pid: active } if active == pid) {
            self.phase = KillPhase::Idle;
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MonitorGeometry {
    pub bounds: PhysicalRect,
    pub work_area: PhysicalRect,
    pub occupied_taskbar_edge: Option<TaskbarEdge>,
}

pub fn logical_pointer_to_physical(
    window_rect: PhysicalRect,
    logical_x: f32,
    logical_y: f32,
    dpi: u32,
) -> PhysicalPoint {
    let scale = dpi.max(1) as f64 / 96.0;
    PhysicalPoint::new(
        window_rect
            .left
            .saturating_add(round_i32(f64::from(logical_x) * scale)),
        window_rect
            .top
            .saturating_add(round_i32(f64::from(logical_y) * scale)),
    )
}

pub fn occupied_taskbar_edge(bounds: PhysicalRect, work_area: PhysicalRect) -> Option<TaskbarEdge> {
    if !bounds.is_valid() || !work_area.is_valid() {
        return None;
    }
    let gaps = [
        (TaskbarEdge::Left, (work_area.left - bounds.left).max(0)),
        (TaskbarEdge::Right, (bounds.right - work_area.right).max(0)),
        (TaskbarEdge::Top, (work_area.top - bounds.top).max(0)),
        (
            TaskbarEdge::Bottom,
            (bounds.bottom - work_area.bottom).max(0),
        ),
    ];
    gaps.into_iter()
        .filter(|(_, gap)| *gap > 0)
        .max_by_key(|(_, gap)| *gap)
        .map(|(edge, _)| edge)
}

pub fn snapped_release_rect(
    window: PhysicalRect,
    monitor: MonitorGeometry,
) -> Option<(PhysicalRect, TaskbarEdge)> {
    snap_floating_window(window, monitor.work_area, monitor.occupied_taskbar_edge)
}

/// 将未吸附拖放的窗口矩形 clamp 到显示器工作区，保证窗口完全可见。
/// 窗口宽/高超过工作区时，左上角优先对齐工作区原点（max 下界 = work_area.left）。
pub fn clamp_rect_to_work_area(window: PhysicalRect, work_area: PhysicalRect) -> PhysicalRect {
    if !window.is_valid() || !work_area.is_valid() {
        return window;
    }
    let width = window.width();
    let height = window.height();
    let max_left = work_area.right.saturating_sub(width).max(work_area.left);
    let max_top = work_area.bottom.saturating_sub(height).max(work_area.top);
    let left = window.left.clamp(work_area.left, max_left);
    let top = window.top.clamp(work_area.top, max_top);
    PhysicalRect::new(
        left,
        top,
        left.saturating_add(width),
        top.saturating_add(height),
    )
}

fn round_i32(value: f64) -> i32 {
    value
        .round()
        .clamp(f64::from(i32::MIN), f64::from(i32::MAX)) as i32
}

#[cfg(windows)]
pub mod native {
    use super::{occupied_taskbar_edge, MonitorGeometry};
    use crate::win32::{PhysicalRect, WindowHandle};
    use windows_sys::Win32::Graphics::Gdi::{
        GetMonitorInfoW, MonitorFromWindow, MONITORINFO, MONITOR_DEFAULTTONEAREST,
    };

    pub fn monitor_geometry(handle: WindowHandle) -> Option<MonitorGeometry> {
        if handle.is_null() {
            return None;
        }
        unsafe {
            let monitor = MonitorFromWindow(handle.raw() as _, MONITOR_DEFAULTTONEAREST);
            if monitor.is_null() {
                return None;
            }
            let mut info: MONITORINFO = std::mem::zeroed();
            info.cbSize = std::mem::size_of::<MONITORINFO>() as u32;
            if GetMonitorInfoW(monitor, &mut info) == 0 {
                return None;
            }
            let bounds = PhysicalRect::new(
                info.rcMonitor.left,
                info.rcMonitor.top,
                info.rcMonitor.right,
                info.rcMonitor.bottom,
            );
            let work_area = PhysicalRect::new(
                info.rcWork.left,
                info.rcWork.top,
                info.rcWork.right,
                info.rcWork.bottom,
            );
            Some(MonitorGeometry {
                bounds,
                work_area,
                occupied_taskbar_edge: occupied_taskbar_edge(bounds, work_area),
            })
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Default)]
    struct ManualClock(u64);

    impl MonotonicClock for ManualClock {
        fn now_ms(&self) -> u64 {
            self.0
        }
    }

    #[test]
    fn long_press_and_cancel_use_exact_2000_and_200ms_boundaries() {
        let mut pointer = PointerMachine::default();
        pointer.press(PhysicalPoint::new(10, 10), "cpu", 0);
        assert_eq!(pointer.tick(1_999), None);
        assert_eq!(pointer.visual(1_999).unwrap().scale, 0.90005);
        assert_eq!(
            pointer.tick(2_000),
            Some(PointerAction::LongPress("cpu".into()))
        );
        assert_eq!(pointer.visual(2_000).unwrap().scale, 0.90);
        assert_eq!(pointer.release(2_000), None);
        assert!((pointer.visual(2_100).unwrap().scale - 0.95).abs() < 0.0001);
        assert_eq!(pointer.visual(2_200), None);
    }

    #[test]
    fn click_feedback_hits_50_and_150ms_and_emits_one_click() {
        let mut pointer = PointerMachine::default();
        pointer.press(PhysicalPoint::new(10, 10), "gpu", 0);
        assert_eq!(
            pointer.release(40),
            Some(PointerAction::Click("gpu".into()))
        );
        assert!((pointer.visual(90).unwrap().scale - 0.92).abs() < 0.0001);
        assert_eq!(pointer.visual(190), None);
    }

    #[test]
    fn drag_precedes_click_and_lost_mouse_up_ends_drag() {
        let mut pointer = PointerMachine::default();
        pointer.press(PhysicalPoint::new(100, 100), "ram", 0);
        assert_eq!(pointer.move_pointer(PhysicalPoint::new(105, 100), 5), None);
        assert_eq!(
            pointer.move_pointer(PhysicalPoint::new(106, 100), 6),
            Some(PointerAction::BeginDrag)
        );
        assert_eq!(pointer.visual(6), None);
        assert_eq!(pointer.cancel(10), Some(PointerAction::EndDrag));
        assert!(!pointer.is_dragging());
    }

    #[test]
    fn is_active_covers_press_drag_hold_and_clears_on_release() {
        let mut pointer = PointerMachine::default();
        assert!(!pointer.is_active(), "idle is inactive");
        pointer.press(PhysicalPoint::new(10, 10), "cpu", 0);
        assert!(pointer.is_active(), "pressed is active");
        assert_eq!(
            pointer.move_pointer(PhysicalPoint::new(20, 10), 5),
            Some(PointerAction::BeginDrag)
        );
        assert!(pointer.is_active(), "dragging is active");
        assert_eq!(pointer.release(6), Some(PointerAction::EndDrag));
        assert!(!pointer.is_active(), "released drag is inactive");
        // Long-press hold is also active (suppresses hover toggles mid-hold).
        pointer.press(PhysicalPoint::new(10, 10), "power", 100);
        assert_eq!(
            pointer.tick(100 + LONG_PRESS_MS),
            Some(PointerAction::LongPress("power".into()))
        );
        assert!(pointer.is_active(), "long-pressed hold is active");
    }

    #[test]
    fn kill_second_click_executes_once_and_timeout_never_kills() {
        let mut kill = KillMachine::default();
        assert_eq!(
            kill.click(42, "fixture", 0),
            Some(KillDecision::Confirm {
                pid: 42,
                name: "fixture".into()
            })
        );
        assert_eq!(kill.confirming(999), Some((42, 0.999)));
        kill.tick(1_000);
        assert_eq!(kill.confirming(1_000), None);
        assert_eq!(
            kill.click(42, "fixture", 1_001),
            Some(KillDecision::Confirm {
                pid: 42,
                name: "fixture".into()
            })
        );
        assert_eq!(
            kill.click(42, "fixture", 1_500),
            Some(KillDecision::Execute {
                pid: 42,
                name: "fixture".into()
            })
        );
        assert_eq!(kill.click(42, "fixture", 1_501), None);
    }

    #[test]
    fn logical_pointer_mapping_covers_100_150_and_200_percent_dpi() {
        let rect = PhysicalRect::new(-500, 200, 120, 720);
        assert_eq!(
            logical_pointer_to_physical(rect, 20.0, 12.0, 96),
            PhysicalPoint::new(-480, 212)
        );
        assert_eq!(
            logical_pointer_to_physical(rect, 20.0, 12.0, 144),
            PhysicalPoint::new(-470, 218)
        );
        assert_eq!(
            logical_pointer_to_physical(rect, 20.0, 12.0, 192),
            PhysicalPoint::new(-460, 224)
        );
    }

    #[test]
    fn release_snap_is_24px_and_routes_to_physical_rect() {
        let monitor = MonitorGeometry {
            bounds: PhysicalRect::new(0, 0, 1920, 1080),
            work_area: PhysicalRect::new(0, 0, 1920, 1040),
            occupied_taskbar_edge: Some(TaskbarEdge::Bottom),
        };
        let near_left = PhysicalRect::new(24, 200, 644, 720);
        let (snapped, edge) = snapped_release_rect(near_left, monitor).unwrap();
        assert_eq!(edge, TaskbarEdge::Left);
        assert_eq!(snapped.left, 0);
        assert_eq!(
            snapped_release_rect(PhysicalRect::new(25, 200, 645, 720), monitor),
            None
        );
    }

    #[test]
    fn monitor_taskbar_edge_comes_from_work_area_gap() {
        let bounds = PhysicalRect::new(-1920, 0, 0, 1080);
        let work = PhysicalRect::new(-1920, 0, 0, 1040);
        assert_eq!(
            occupied_taskbar_edge(bounds, work),
            Some(TaskbarEdge::Bottom)
        );
        assert_eq!(KILL_ARC_CIRCUMFERENCE, 43.98);
        assert_eq!(ManualClock(50).now_ms(), 50);
    }

    #[test]
    fn clamp_to_work_area_offscreen_left_right_and_negative_origin() {
        let work = PhysicalRect::new(0, 0, 1920, 1080);

        // 完全在左侧外 → 贴左
        let offscreen_left = PhysicalRect::new(-700, 200, -100, 272);
        let clamped = clamp_rect_to_work_area(offscreen_left, work);
        assert_eq!(clamped.left, 0);
        assert_eq!(clamped.top, 200);

        // 完全在右侧外 → 右边贴齐
        let offscreen_right = PhysicalRect::new(2000, 200, 2600, 272);
        let clamped = clamp_rect_to_work_area(offscreen_right, work);
        assert_eq!(clamped.right, 1920);
        assert_eq!(clamped.left, 1320);

        // 负坐标 → 左上角贴齐
        let negative = PhysicalRect::new(-50, -30, 570, 302);
        let clamped = clamp_rect_to_work_area(negative, work);
        assert_eq!(clamped.left, 0);
        assert_eq!(clamped.top, 0);
        assert_eq!(clamped.right, 620);
        assert_eq!(clamped.bottom, 332);
    }

    #[test]
    fn clamp_to_work_area_oversized_and_unchanged_and_invalid() {
        let work = PhysicalRect::new(0, 0, 1920, 1080);

        // 窗口大于工作区：左上角对齐工作区原点
        let oversized = PhysicalRect::new(-100, -50, 2200, 1200);
        let clamped = clamp_rect_to_work_area(oversized, work);
        assert_eq!(clamped.left, 0);
        assert_eq!(clamped.top, 0);
        assert_eq!(clamped.width(), oversized.width());
        assert_eq!(clamped.height(), oversized.height());

        // 完全在内部：不变
        let inside = PhysicalRect::new(100, 100, 700, 600);
        assert_eq!(clamp_rect_to_work_area(inside, work), inside);

        // 无效矩形：原样返回
        let invalid = PhysicalRect::new(100, 100, 50, 50);
        assert_eq!(clamp_rect_to_work_area(invalid, work), invalid);
    }
}
