//! Feature-local projection from the shared DesktopState into generated Slint models.

use std::cmp::Ordering;

use slint::{Color, ModelRc, SharedString, VecModel};

use crate::desktop_state::{DesktopState, PanelState, ProcessRow};
use crate::{DiskData, MetricData, ProcessData, Shell};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ThresholdTone {
    Green,
    Yellow,
    Red,
}

#[derive(Debug, Clone, PartialEq)]
pub struct MetricView {
    pub id: String,
    pub label: String,
    pub value: String,
    pub detail: String,
    pub ratio: f32,
    pub tone: Option<ThresholdTone>,
    pub accent: (u8, u8, u8),
}

#[derive(Debug, Clone, PartialEq)]
pub struct ProcessView {
    pub pid: i32,
    pub name: String,
    pub cpu: f64,
    pub memory: f64,
    pub gpu: f64,
    pub vram: f64,
    pub max_memory: f64,
    pub max_vram: f64,
    pub pinned: bool,
}

#[derive(Debug, Clone, PartialEq)]
pub struct DiskView {
    pub name: String,
    pub read_speed: Option<f64>,
    pub write_speed: Option<f64>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct FloatingProjection {
    pub connected: bool,
    pub details_visible: bool,
    pub panel_locked: bool,
    pub metrics: Vec<MetricView>,
    pub pinned: Vec<ProcessView>,
    pub processes: Vec<ProcessView>,
    pub disks: Vec<DiskView>,
}

pub fn threshold_tone(value: f64) -> ThresholdTone {
    if value < 50.0 {
        ThresholdTone::Green
    } else if value < 80.0 {
        ThresholdTone::Yellow
    } else {
        ThresholdTone::Red
    }
}

pub fn memory_tone(current: f64, maximum: f64) -> ThresholdTone {
    if maximum <= 0.0 || !maximum.is_finite() {
        ThresholdTone::Green
    } else {
        threshold_tone(current / maximum * 100.0)
    }
}

pub fn clamped_ratio(current: f64, maximum: f64) -> f32 {
    if !current.is_finite() || !maximum.is_finite() || maximum <= 0.0 {
        return 0.0;
    }
    (current / maximum).clamp(0.0, 1.0) as f32
}

pub fn panel_after_hover(panel: PanelState, inside: bool) -> PanelState {
    match (panel, inside) {
        (PanelState::Collapsed, true) => PanelState::Expanded,
        (PanelState::Expanded, false) => PanelState::Collapsed,
        (other, _) => other,
    }
}

pub fn panel_after_click(panel: PanelState) -> PanelState {
    // C# FloatingWindowViewModel.cs:258-270：仅 Expanded↔Locked 互切，
    // Collapsed 不响应点击（需先 hover 进入 Expanded），Clickthrough 透传。
    match panel {
        PanelState::Expanded => PanelState::Locked,
        PanelState::Locked => PanelState::Expanded,
        PanelState::Collapsed => PanelState::Collapsed,
        PanelState::Clickthrough => PanelState::Clickthrough,
    }
}

pub fn project_state(state: &DesktopState) -> FloatingProjection {
    let usage = state.usage.as_ref();
    let max_memory = usage
        .map(|value| value.max_memory)
        .or_else(|| state.limits.as_ref().map(|value| value.max_memory))
        .unwrap_or(0.0);
    let max_vram = usage
        .map(|value| value.max_vram)
        .or_else(|| state.limits.as_ref().map(|value| value.max_vram))
        .unwrap_or(0.0);

    let cpu = usage.map(|value| value.total_cpu).unwrap_or(0.0);
    let gpu = usage.map(|value| value.total_gpu).unwrap_or(0.0);
    let memory = usage.map(|value| value.total_memory).unwrap_or(0.0);
    let vram = usage.map(|value| value.total_vram).unwrap_or(0.0);
    let upload = usage.map(|value| value.upload_speed).unwrap_or(0.0);
    let download = usage.map(|value| value.download_speed).unwrap_or(0.0);
    let power = usage.map(|value| value.total_power).unwrap_or(0.0);
    let max_power = usage.map(|value| value.max_power).unwrap_or(0.0);

    let metrics = vec![
        MetricView {
            id: "net".into(),
            label: "NET".into(),
            value: format!("{upload:.1}/{download:.1}"),
            detail: "up/down MB/s".into(),
            ratio: 0.0,
            tone: None,
            accent: (255, 255, 255),
        },
        MetricView {
            id: "cpu".into(),
            label: "CPU".into(),
            value: format!("{cpu:.0}%"),
            detail: usage
                .and_then(|value| value.cpu_temperature)
                .map(|value| format!("{value:.0} C"))
                .unwrap_or_else(|| "-- C".into()),
            ratio: clamped_ratio(cpu, 100.0),
            tone: Some(threshold_tone(cpu)),
            accent: (0, 0, 0),
        },
        MetricView {
            id: "ram".into(),
            label: "RAM".into(),
            value: format_memory(memory),
            detail: format!("of {}", format_memory(max_memory)),
            ratio: clamped_ratio(memory, max_memory),
            tone: Some(memory_tone(memory, max_memory)),
            accent: (0, 0, 0),
        },
        MetricView {
            id: "gpu".into(),
            label: "GPU".into(),
            value: format!("{gpu:.0}%"),
            detail: usage
                .and_then(|value| value.gpu_temperature)
                .map(|value| format!("{value:.0} C"))
                .unwrap_or_else(|| "-- C".into()),
            ratio: clamped_ratio(gpu, 100.0),
            tone: Some(threshold_tone(gpu)),
            accent: (0, 0, 0),
        },
        MetricView {
            id: "vram".into(),
            label: "VRAM".into(),
            value: format_memory(vram),
            detail: format!("of {}", format_memory(max_vram)),
            ratio: clamped_ratio(vram, max_vram),
            tone: Some(memory_tone(vram, max_vram)),
            accent: (0, 0, 0),
        },
        MetricView {
            id: "power".into(),
            label: "POWER".into(),
            value: format!("{power:.0} W"),
            detail: if usage.is_some_and(|value| value.power_available) {
                format!("max {max_power:.0} W")
            } else {
                "unavailable".into()
            },
            ratio: clamped_ratio(power, max_power),
            tone: Some(memory_tone(power, max_power)),
            accent: (0, 0, 0),
        },
    ];

    let mut all_rows: Vec<&ProcessRow> = state.processes.values().collect();
    all_rows.sort_by(|left, right| {
        metric(right, "memory")
            .partial_cmp(&metric(left, "memory"))
            .unwrap_or(Ordering::Equal)
            .then_with(|| left.process_id.cmp(&right.process_id))
    });
    let pinned = state
        .pinned_rows()
        .into_iter()
        .map(|row| project_process(row, max_memory, max_vram))
        .collect();
    let processes = if state.panel.is_details_visible() {
        all_rows
            .into_iter()
            .map(|row| project_process(row, max_memory, max_vram))
            .collect()
    } else {
        state
            .pinned_rows()
            .into_iter()
            .map(|row| project_process(row, max_memory, max_vram))
            .collect()
    };
    let disks = state
        .disks()
        .iter()
        .map(|disk| DiskView {
            name: disk.name.clone(),
            read_speed: disk.read_speed,
            write_speed: disk.write_speed,
        })
        .collect();

    FloatingProjection {
        connected: state.connected,
        details_visible: state.panel.is_details_visible(),
        panel_locked: state.panel == PanelState::Locked,
        metrics,
        pinned,
        processes,
        disks,
    }
}

pub fn apply_projection(app: &Shell, projection: FloatingProjection) {
    app.set_connected(projection.connected);
    app.set_details_visible(projection.details_visible);
    app.set_panel_locked(projection.panel_locked);
    app.set_metrics(ModelRc::new(VecModel::from(
        projection
            .metrics
            .into_iter()
            .map(to_metric_data)
            .collect::<Vec<_>>(),
    )));
    app.set_pinned_processes(ModelRc::new(VecModel::from(
        projection
            .pinned
            .into_iter()
            .map(to_process_data)
            .collect::<Vec<_>>(),
    )));
    app.set_processes(ModelRc::new(VecModel::from(
        projection
            .processes
            .into_iter()
            .map(to_process_data)
            .collect::<Vec<_>>(),
    )));
    app.set_disks(ModelRc::new(VecModel::from(
        projection
            .disks
            .into_iter()
            .map(|disk| DiskData {
                name: disk.name.into(),
                read_text: format_speed("R", disk.read_speed).into(),
                write_text: format_speed("W", disk.write_speed).into(),
            })
            .collect::<Vec<_>>(),
    )));
}

pub fn dispatch_projection(
    app: &slint::Weak<Shell>,
    projection: FloatingProjection,
) -> Result<(), slint::EventLoopError> {
    app.upgrade_in_event_loop(move |app| apply_projection(&app, projection))
}

pub fn smoke_state(kill_pid: i32) -> DesktopState {
    use std::collections::BTreeMap;

    use chrono::Local;
    use xhm_core::wire::{DiskUsagePayload, HardwareLimitsPayload, SystemUsagePayload};

    let kill_pid = kill_pid.max(1);
    let mut processes = BTreeMap::new();
    for index in 0..36 {
        let pid = if index == 0 { kill_pid } else { 8_000 + index };
        let cpu = ((index * 13 + 17) % 100) as f64;
        let gpu = ((index * 19 + 9) % 100) as f64;
        let memory = if index == 0 {
            26_000.0
        } else {
            384.0 + index as f64 * 173.0
        };
        processes.insert(
            pid,
            ProcessRow {
                process_id: pid,
                process_name: if index == 0 {
                    "g3-kill-smoke".into()
                } else {
                    format!("fixture-process-{:02}", index + 1)
                },
                command_line: None,
                display_name: Some(if index == 0 {
                    "G3 Kill Smoke Target".into()
                } else {
                    format!("Fixture Process {:02}", index + 1)
                }),
                metrics: BTreeMap::from([
                    ("cpu".into(), cpu),
                    ("memory".into(), memory),
                    ("gpu".into(), gpu),
                    ("vram".into(), memory * 0.4),
                ]),
                has_meta: true,
                is_pinned: index < 2,
            },
        );
    }

    DesktopState {
        limits: Some(HardwareLimitsPayload {
            timestamp: Local::now(),
            max_memory: 32_768.0,
            max_vram: 16_384.0,
        }),
        usage: Some(SystemUsagePayload {
            timestamp: Local::now(),
            total_cpu: 37.0,
            total_gpu: 86.0,
            cpu_temperature: Some(62.0),
            gpu_temperature: Some(71.0),
            total_memory: 22_016.0,
            total_vram: 9_420.8,
            upload_speed: 18.2,
            download_speed: 74.6,
            max_memory: 32_768.0,
            max_vram: 16_384.0,
            disks: vec![
                DiskUsagePayload {
                    name: "C: NVMe".into(),
                    total_bytes: None,
                    used_bytes: None,
                    read_speed: Some(842.4),
                    write_speed: Some(128.7),
                },
                DiskUsagePayload {
                    name: "D: Data".into(),
                    total_bytes: None,
                    used_bytes: None,
                    read_speed: Some(72.1),
                    write_speed: Some(44.9),
                },
                DiskUsagePayload {
                    name: "E: Archive".into(),
                    total_bytes: None,
                    used_bytes: None,
                    read_speed: Some(2.4),
                    write_speed: Some(0.8),
                },
            ],
            power_available: true,
            total_power: 118.0,
            max_power: 160.0,
            power_scheme_index: Some(1),
        }),
        panel: PanelState::Locked,
        connected: true,
        processes,
        pinned: vec![kill_pid, 8_001],
    }
}

pub fn smoke_projection(kill_pid: i32) -> FloatingProjection {
    project_state(&smoke_state(kill_pid))
}

fn project_process(row: &ProcessRow, max_memory: f64, max_vram: f64) -> ProcessView {
    ProcessView {
        pid: row.process_id,
        name: row
            .display_name
            .as_deref()
            .filter(|value| !value.is_empty())
            .unwrap_or(&row.process_name)
            .to_owned(),
        cpu: metric(row, "cpu"),
        memory: metric(row, "memory"),
        gpu: metric(row, "gpu"),
        vram: metric(row, "vram"),
        max_memory,
        max_vram,
        pinned: row.is_pinned,
    }
}

fn metric(row: &ProcessRow, key: &str) -> f64 {
    row.metrics.get(key).copied().unwrap_or(0.0)
}

fn to_metric_data(metric: MetricView) -> MetricData {
    MetricData {
        id: metric.id.into(),
        label: metric.label.into(),
        value: metric.value.into(),
        detail: metric.detail.into(),
        ratio: metric.ratio.clamp(0.0, 1.0),
        accent: metric
            .tone
            .map(tone_color)
            .unwrap_or_else(|| Color::from_rgb_u8(metric.accent.0, metric.accent.1, metric.accent.2)),
    }
}

fn to_process_data(process: ProcessView) -> ProcessData {
    ProcessData {
        pid: process.pid,
        name: SharedString::from(process.name),
        cpu_text: format!("{:.0}%", process.cpu).into(),
        cpu_ratio: clamped_ratio(process.cpu, 100.0),
        cpu_color: tone_color(threshold_tone(process.cpu)),
        memory_text: format_memory(process.memory).into(),
        memory_ratio: clamped_ratio(process.memory, process.max_memory),
        memory_color: tone_color(memory_tone(process.memory, process.max_memory)),
        gpu_text: format!("{:.0}%", process.gpu).into(),
        gpu_ratio: clamped_ratio(process.gpu, 100.0),
        gpu_color: tone_color(threshold_tone(process.gpu)),
        vram_text: format_memory(process.vram).into(),
        vram_ratio: clamped_ratio(process.vram, process.max_vram),
        vram_color: tone_color(memory_tone(process.vram, process.max_vram)),
        pinned: process.pinned,
    }
}

fn tone_color(tone: ThresholdTone) -> Color {
    match tone {
        ThresholdTone::Green => Color::from_rgb_u8(0x4a, 0xde, 0x80),
        ThresholdTone::Yellow => Color::from_rgb_u8(0xfa, 0xcc, 0x15),
        ThresholdTone::Red => Color::from_rgb_u8(0xf8, 0x71, 0x71),
    }
}

fn format_memory(megabytes: f64) -> String {
    if !megabytes.is_finite() || megabytes <= 0.0 {
        "0 MB".into()
    } else if megabytes >= 1024.0 {
        format!("{:.1} GB", megabytes / 1024.0)
    } else {
        format!("{megabytes:.0} MB")
    }
}

fn format_speed(prefix: &str, speed: Option<f64>) -> String {
    match speed.filter(|value| value.is_finite()) {
        Some(value) => format!("{prefix} {value:.1} MB/s"),
        None => format!("{prefix} -- MB/s"),
    }
}


#[cfg(windows)]
enum AsyncUiResult {
    KillFinished {
        pid: u32,
        name: String,
        outcome: crate::win32::KillOutcome,
    },
    PowerFinished(Result<String, String>),
}

#[cfg(windows)]
struct FloatingRuntimeState {
    clock: super::floating_interactions::SystemClock,
    pointer: super::floating_interactions::PointerMachine,
    kill: super::floating_interactions::KillMachine,
    drag_anchor: Option<crate::win32::PhysicalPoint>,
    desktop_state: std::sync::Arc<std::sync::Mutex<DesktopState>>,
    subscription_tx:
        Option<tokio::sync::mpsc::UnboundedSender<crate::service_client::SseSubscription>>,
    async_tx: std::sync::mpsc::Sender<AsyncUiResult>,
    async_rx: std::sync::mpsc::Receiver<AsyncUiResult>,
    toast_until_ms: Option<u64>,
    power_inflight: bool,
}

#[cfg(windows)]
pub struct FloatingUiRuntime {
    _timer: slint::Timer,
}

#[cfg(windows)]
pub fn install_runtime(
    app: &Shell,
    handle: crate::win32::WindowHandle,
    desktop_state: std::sync::Arc<std::sync::Mutex<DesktopState>>,
    subscription_tx: Option<
        tokio::sync::mpsc::UnboundedSender<crate::service_client::SseSubscription>,
    >,
) -> FloatingUiRuntime {
    use std::cell::RefCell;
    use std::rc::Rc;

    use slint::ComponentHandle;

    use super::floating_interactions::{
        KillDecision, MonotonicClock, PointerAction, PointerMachine, SystemClock,
    };

    let (async_tx, async_rx) = std::sync::mpsc::channel();
    let runtime = Rc::new(RefCell::new(FloatingRuntimeState {
        clock: SystemClock::new(),
        pointer: PointerMachine::default(),
        kill: super::floating_interactions::KillMachine::default(),
        drag_anchor: None,
        desktop_state,
        subscription_tx,
        async_tx,
        async_rx,
        toast_until_ms: None,
        power_inflight: false,
    }));
    let weak = app.as_weak();

    {
        let runtime = Rc::clone(&runtime);
        let weak = weak.clone();
        app.on_pointer_down(move |logical_x, logical_y, metric| {
            let Some((point, anchor)) =
                native_pointer_point(handle, logical_x + 12.0, logical_y + 12.0)
            else {
                tracing::warn!("floating pointer-down could not resolve physical coordinates");
                return;
            };
            let mut runtime = runtime.borrow_mut();
            let now = runtime.clock.now_ms();
            runtime.pointer.press(point, metric.to_string(), now);
            runtime.drag_anchor = Some(anchor);
            if let Some(app) = weak.upgrade() {
                app.set_active_metric_id(metric);
                app.set_active_metric_scale(1.0);
            }
        });
    }

    {
        let runtime = Rc::clone(&runtime);
        let weak = weak.clone();
        app.on_pointer_move(move |logical_x, logical_y| {
            let Some((point, _)) =
                native_pointer_point(handle, logical_x + 12.0, logical_y + 12.0)
            else {
                return;
            };
            let action = {
                let mut runtime = runtime.borrow_mut();
                let now = runtime.clock.now_ms();
                runtime.pointer.move_pointer(point, now)
            };
            if matches!(action, Some(PointerAction::BeginDrag | PointerAction::Drag)) {
                move_dragged_window(handle, point, runtime.borrow().drag_anchor);
                if let Some(app) = weak.upgrade() {
                    app.set_organic_state("dragging".into());
                    app.set_active_metric_id("".into());
                    app.set_active_metric_scale(1.0);
                }
            }
        });
    }

    {
        let runtime = Rc::clone(&runtime);
        let weak = weak.clone();
        app.on_pointer_up(move |_logical_x, _logical_y| {
            let action = {
                let mut runtime = runtime.borrow_mut();
                let now = runtime.clock.now_ms();
                runtime.pointer.release(now)
            };
            handle_pointer_release(action, &runtime, &weak, handle);
        });
    }

    {
        let runtime = Rc::clone(&runtime);
        let weak = weak.clone();
        app.on_pointer_cancel(move || {
            let action = {
                let mut runtime = runtime.borrow_mut();
                let now = runtime.clock.now_ms();
                runtime.pointer.cancel(now)
            };
            if action == Some(PointerAction::EndDrag) {
                finish_drag(handle, &weak);
            }
            runtime.borrow_mut().drag_anchor = None;
        });
    }

    {
        let runtime = Rc::clone(&runtime);
        let weak = weak.clone();
        app.on_hover_changed(move |inside| {
            let projection = {
                let runtime = runtime.borrow();
                let mut state = runtime
                    .desktop_state
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner);
                state.panel = panel_after_hover(state.panel, inside);
                notify_subscription(&runtime, &state);
                project_state(&state)
            };
            if let Some(app) = weak.upgrade() {
                apply_projection(&app, projection);
            }
        });
    }

    {
        let runtime = Rc::clone(&runtime);
        let weak = weak.clone();
        app.on_toggle_pin(move |pid| {
            let projection = {
                let runtime = runtime.borrow();
                let mut state = runtime
                    .desktop_state
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner);
                if state.pinned.contains(&pid) {
                    state.unpin(pid);
                } else {
                    state.pin(pid);
                }
                notify_subscription(&runtime, &state);
                project_state(&state)
            };
            if let Some(app) = weak.upgrade() {
                apply_projection(&app, projection);
            }
        });
    }

    {
        let runtime = Rc::clone(&runtime);
        let weak = weak.clone();
        app.on_kill_process(move |pid, name| {
            let decision = {
                let mut runtime = runtime.borrow_mut();
                let now = runtime.clock.now_ms();
                runtime.kill.click(pid.max(0) as u32, name.to_string(), now)
            };
            match decision {
                Some(KillDecision::Confirm { pid, name }) => {
                    tracing::info!(pid, process = name, timeout_ms = 1_000, arc = 43.98, "G3 kill confirm started");
                    if let Some(app) = weak.upgrade() {
                        app.set_kill_confirm_pid(pid as i32);
                        app.set_kill_confirm_progress(0.0);
                        show_toast(&app, &runtime, format!("Click again to close {name}"));
                    }
                }
                Some(KillDecision::Execute { pid, name }) => {
                    tracing::info!(pid, process = name, "G3 kill execute dispatched exactly once");
                    spawn_kill(runtime.borrow().async_tx.clone(), pid, name);
                    if let Some(app) = weak.upgrade() {
                        app.set_toast_message("Closing process...".into());
                        app.set_toast_visible(true);
                    }
                }
                None => {}
            }
        });
    }

    let timer = slint::Timer::default();
    {
        let runtime = Rc::clone(&runtime);
        let weak = weak.clone();
        timer.start(
            slint::TimerMode::Repeated,
            std::time::Duration::from_millis(16),
            move || {
                let now = runtime.borrow().clock.now_ms();
                let long_press_action = runtime.borrow_mut().pointer.tick(now);
                if let Some(PointerAction::LongPress(metric)) = long_press_action {
                    if let Some(app) = weak.upgrade() {
                        show_toast(&app, &runtime, format!("{metric} long press"));
                    }
                    tracing::info!(metric, elapsed_ms = 2_000, "G3 long-press triggered");
                }

                let visual = runtime.borrow_mut().pointer.visual(now);
                if let Some(app) = weak.upgrade() {
                    match visual {
                        Some(visual) => {
                            app.set_active_metric_id(visual.metric.into());
                            app.set_active_metric_scale(visual.scale);
                        }
                        None => {
                            app.set_active_metric_id("".into());
                            app.set_active_metric_scale(1.0);
                        }
                    }

                    runtime.borrow_mut().kill.tick(now);
                    if let Some((pid, progress)) = runtime.borrow().kill.confirming(now) {
                        app.set_kill_confirm_pid(pid as i32);
                        app.set_kill_confirm_progress(progress);
                    } else {
                        app.set_kill_confirm_pid(-1);
                        app.set_kill_confirm_progress(0.0);
                    }

                    loop {
                        let result = { runtime.borrow().async_rx.try_recv() };
                        let Ok(result) = result else {
                            break;
                        };
                        apply_async_result(&app, &runtime, result);
                    }
                    let toast_expired = {
                        runtime
                            .borrow()
                            .toast_until_ms
                            .is_some_and(|deadline| now >= deadline)
                    };
                    if toast_expired {
                        runtime.borrow_mut().toast_until_ms = None;
                        app.set_toast_visible(false);
                    }
                }
            },
        );
    }

    FloatingUiRuntime { _timer: timer }
}

#[cfg(windows)]
fn handle_pointer_release(
    action: Option<super::floating_interactions::PointerAction>,
    runtime: &std::rc::Rc<std::cell::RefCell<FloatingRuntimeState>>,
    weak: &slint::Weak<Shell>,
    handle: crate::win32::WindowHandle,
) {
    use super::floating_interactions::PointerAction;

    match action {
        Some(PointerAction::EndDrag) => finish_drag(handle, weak),
        Some(PointerAction::Click(metric)) => {
            let projection = {
                let runtime_ref = runtime.borrow();
                let mut state = runtime_ref
                    .desktop_state
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner);
                state.panel = panel_after_click(state.panel);
                notify_subscription(&runtime_ref, &state);
                project_state(&state)
            };
            if let Some(app) = weak.upgrade() {
                apply_projection(&app, projection);
            }
            if metric == "power" {
                let mut runtime_ref = runtime.borrow_mut();
                if !runtime_ref.power_inflight {
                    runtime_ref.power_inflight = true;
                    spawn_power(runtime_ref.async_tx.clone());
                }
            }
            tracing::info!(metric, down_ms = 50, recover_ms = 150, "G3 click feedback");
        }
        Some(PointerAction::LongPress(metric)) => {
            if let Some(app) = weak.upgrade() {
                show_toast(&app, runtime, format!("{metric} long press"));
            }
        }
        _ => {}
    }
    runtime.borrow_mut().drag_anchor = None;
}

#[cfg(windows)]
fn native_pointer_point(
    handle: crate::win32::WindowHandle,
    logical_x: f32,
    logical_y: f32,
) -> Option<(crate::win32::PhysicalPoint, crate::win32::PhysicalPoint)> {
    use crate::win32::dpi::native::NativeDpiQuery;
    use crate::win32::taskbar::native::NativeWindowPositionOps;
    use crate::win32::WindowPositionOps;

    let positioner = NativeWindowPositionOps;
    let rect = positioner.window_rect(handle)?;
    let dpi = crate::win32::dpi::dpi_for_window(Some(&NativeDpiQuery), handle);
    let point = super::floating_interactions::logical_pointer_to_physical(
        rect, logical_x, logical_y, dpi,
    );
    let anchor = crate::win32::PhysicalPoint::new(point.x - rect.left, point.y - rect.top);
    Some((point, anchor))
}

#[cfg(windows)]
fn move_dragged_window(
    handle: crate::win32::WindowHandle,
    cursor: crate::win32::PhysicalPoint,
    anchor: Option<crate::win32::PhysicalPoint>,
) {
    use crate::win32::taskbar::native::NativeWindowPositionOps;
    use crate::win32::WindowPositionOps;

    let Some(anchor) = anchor else {
        return;
    };
    let origin = super::floating_interactions::drag_origin(cursor, anchor);
    let positioner = NativeWindowPositionOps;
    if !positioner.move_topmost(handle, origin.x, origin.y) {
        tracing::warn!(x = origin.x, y = origin.y, "SetWindowPos failed during floating drag");
    }
}

#[cfg(windows)]
fn finish_drag(handle: crate::win32::WindowHandle, weak: &slint::Weak<Shell>) {
    use crate::win32::taskbar::native::NativeWindowPositionOps;
    use crate::win32::WindowPositionOps;

    let positioner = NativeWindowPositionOps;
    let Some(current) = positioner.window_rect(handle) else {
        return;
    };
    let monitor = super::floating_interactions::native::monitor_geometry(handle);
    let snapped = monitor
        .and_then(|m| super::floating_interactions::snapped_release_rect(current, m));
    if let Some((rect, edge)) = snapped {
        let moved = positioner.move_topmost(handle, rect.left, rect.top);
        if let Some(app) = weak.upgrade() {
            app.set_organic_state(
                match edge {
                    crate::win32::TaskbarEdge::Left => "dock-left",
                    crate::win32::TaskbarEdge::Right => "dock-right",
                    crate::win32::TaskbarEdge::Top => "dock-top",
                    crate::win32::TaskbarEdge::Bottom => "dock-bottom",
                }
                .into(),
            );
        }
        tracing::info!(
            ?edge,
            x = rect.left,
            y = rect.top,
            snap_distance_px = 24,
            set_window_pos = moved,
            "G3 drag release snapped through SetWindowPos"
        );
    } else {
        // 未吸附：clamp 到显示器工作区，越界时通过 SetWindowPos 拉回。
        if let Some(monitor) = monitor {
            let clamped = super::floating_interactions::clamp_rect_to_work_area(
                current,
                monitor.work_area,
            );
            if clamped.left != current.left || clamped.top != current.top {
                let moved = positioner.move_topmost(handle, clamped.left, clamped.top);
                tracing::info!(
                    x = clamped.left,
                    y = clamped.top,
                    prev_x = current.left,
                    prev_y = current.top,
                    set_window_pos = moved,
                    "G3 drag release clamped to work area"
                );
            }
        }
        if let Some(app) = weak.upgrade() {
            app.set_organic_state("floating".into());
        }
        tracing::info!(
            x = current.left,
            y = current.top,
            snap_distance_px = 24,
            "G3 drag release outside snap boundary"
        );
    }
}

#[cfg(windows)]
fn notify_subscription(runtime: &FloatingRuntimeState, state: &DesktopState) {
    if let Some(sender) = &runtime.subscription_tx {
        let _ = sender.send(crate::service_client::SseSubscription::new(
            state.panel.subscription_mode(),
            state.normalized_pinned(),
        ));
    }
}

#[cfg(windows)]
fn show_toast(
    app: &Shell,
    runtime: &std::rc::Rc<std::cell::RefCell<FloatingRuntimeState>>,
    message: String,
) {
    use super::floating_interactions::MonotonicClock;

    let now = runtime.borrow().clock.now_ms();
    runtime.borrow_mut().toast_until_ms = Some(now.saturating_add(3_000));
    app.set_toast_message(message.into());
    app.set_toast_visible(true);
}

#[cfg(windows)]
fn spawn_kill(
    sender: std::sync::mpsc::Sender<AsyncUiResult>,
    pid: u32,
    name: String,
) {
    std::thread::spawn(move || {
        use crate::win32::process::native::WindowsProcessManager;

        let guard = crate::win32::KillOnce::new(WindowsProcessManager);
        let outcome = guard.kill_process_tree(pid);
        let _ = sender.send(AsyncUiResult::KillFinished { pid, name, outcome });
    });
}

#[cfg(windows)]
fn spawn_power(sender: std::sync::mpsc::Sender<AsyncUiResult>) {
    std::thread::spawn(move || {
        let result = (|| -> Result<String, String> {
            let runtime = tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
                .map_err(|error| error.to_string())?;
            runtime.block_on(async {
                let config = crate::config::Config::load().await;
                let client = crate::service_client::rest::RestClient::new(&config)
                    .map_err(|error| error.to_string())?;
                client.warmup().await.map_err(|error| error.to_string())?;
                let switched = client
                    .power_next_scheme()
                    .await
                    .map_err(|error| error.to_string())?;
                Ok(format!(
                    "Power scheme {}: {}",
                    switched.new_scheme_index, switched.message
                ))
            })
        })();
        let _ = sender.send(AsyncUiResult::PowerFinished(result));
    });
}

#[cfg(windows)]
fn apply_async_result(
    app: &Shell,
    runtime: &std::rc::Rc<std::cell::RefCell<FloatingRuntimeState>>,
    result: AsyncUiResult,
) {
    match result {
        AsyncUiResult::KillFinished { pid, name, outcome } => {
            runtime.borrow_mut().kill.complete(pid);
            tracing::info!(pid, process = name, ?outcome, "G3 kill completed");
            let remove_row = matches!(
                outcome,
                crate::win32::KillOutcome::Success
                    | crate::win32::KillOutcome::NotFound
                    | crate::win32::KillOutcome::AlreadyExited
            );
            let message = match outcome {
                crate::win32::KillOutcome::Success => format!("Closed {name}"),
                crate::win32::KillOutcome::NotFound => format!("{name} no longer exists"),
                crate::win32::KillOutcome::AlreadyExited => format!("{name} already exited"),
                crate::win32::KillOutcome::AccessDenied => format!("Access denied closing {name}"),
                crate::win32::KillOutcome::Other(error) => format!("Failed to close {name}: {error}"),
            };
            if remove_row {
                let projection = {
                    let runtime_ref = runtime.borrow();
                    let mut state = runtime_ref
                        .desktop_state
                        .lock()
                        .unwrap_or_else(std::sync::PoisonError::into_inner);
                    state.processes.remove(&(pid as i32));
                    state.pinned.retain(|candidate| *candidate != pid as i32);
                    project_state(&state)
                };
                apply_projection(app, projection);
            }
            show_toast(app, runtime, message);
        }
        AsyncUiResult::PowerFinished(result) => {
            runtime.borrow_mut().power_inflight = false;
            show_toast(
                app,
                runtime,
                result.unwrap_or_else(|error| format!("Power switch failed: {error}")),
            );
        }
    }
}
#[cfg(test)]
mod tests {
    use std::collections::BTreeMap;

    use chrono::Local;
    use xhm_core::wire::{
        ProcessMetadataPayload, ProcessMetadataSnapshot, ProcessMetricSnapshot,
        ProcessSnapshotPayload, PushEvent,
    };

    use super::*;

    fn metric_snapshot(pid: i32, name: &str, memory: f64) -> ProcessMetricSnapshot {
        ProcessMetricSnapshot {
            process_id: pid,
            process_name: name.into(),
            has_meta: false,
            command_line: None,
            display_name: None,
            metrics: BTreeMap::from([
                ("cpu".into(), 50.0),
                ("memory".into(), memory),
                ("gpu".into(), 80.0),
                ("vram".into(), memory / 2.0),
            ]),
        }
    }

    #[test]
    fn threshold_boundaries_and_progress_clamp_are_exact() {
        assert_eq!(threshold_tone(49.999), ThresholdTone::Green);
        assert_eq!(threshold_tone(50.0), ThresholdTone::Yellow);
        assert_eq!(threshold_tone(79.999), ThresholdTone::Yellow);
        assert_eq!(threshold_tone(80.0), ThresholdTone::Red);
        assert_eq!(memory_tone(9_999.0, 0.0), ThresholdTone::Green);
        assert_eq!(clamped_ratio(-1.0, 100.0), 0.0);
        assert_eq!(clamped_ratio(25.0, 100.0), 0.25);
        assert_eq!(clamped_ratio(150.0, 100.0), 1.0);
    }

    #[test]
    fn collapsed_projection_contains_only_pinned_but_full_is_virtualizable() {
        let mut state = DesktopState::new();
        state.apply_event(&PushEvent::ProcessMetrics(ProcessSnapshotPayload {
            timestamp: Local::now(),
            process_count: 3,
            processes: vec![
                metric_snapshot(1, "one", 100.0),
                metric_snapshot(2, "two", 300.0),
                metric_snapshot(3, "three", 200.0),
            ],
        }));
        state.pin(1);
        let collapsed = project_state(&state);
        assert_eq!(collapsed.processes.len(), 1);
        assert_eq!(collapsed.pinned.len(), 1);

        state.panel = PanelState::Expanded;
        let full = project_state(&state);
        assert_eq!(full.processes.len(), 3);
        assert_eq!(full.processes[0].pid, 2);
        assert_eq!(panel_after_click(state.panel), PanelState::Locked);
        assert_eq!(panel_after_hover(PanelState::Expanded, false), PanelState::Collapsed);
    }

    #[test]
    fn late_metadata_merges_and_pid_restart_does_not_rebind_pin() {
        let mut state = DesktopState::new();
        state.apply_event(&PushEvent::ProcessMetrics(ProcessSnapshotPayload {
            timestamp: Local::now(),
            process_count: 1,
            processes: vec![metric_snapshot(44, "old", 100.0)],
        }));
        state.pin(44);
        state.apply_event(&PushEvent::ProcessMetadata(ProcessMetadataPayload {
            timestamp: Local::now(),
            process_count: 1,
            processes: vec![ProcessMetadataSnapshot {
                process_id: 44,
                process_name: "old".into(),
                command_line: "old.exe --serve".into(),
                display_name: "Old Display".into(),
            }],
        }));
        assert_eq!(state.processes[&44].display_name.as_deref(), Some("Old Display"));

        state.apply_event(&PushEvent::ProcessMetrics(ProcessSnapshotPayload {
            timestamp: Local::now(),
            process_count: 0,
            processes: vec![],
        }));
        assert!(state.pinned.is_empty());
        state.apply_event(&PushEvent::ProcessMetrics(ProcessSnapshotPayload {
            timestamp: Local::now(),
            process_count: 1,
            processes: vec![metric_snapshot(44, "new", 200.0)],
        }));
        assert!(!state.processes[&44].is_pinned);
    }

    #[test]
    fn smoke_fixture_exercises_long_list_disks_and_all_threshold_colors() {
        let projection = smoke_projection(1234);
        assert_eq!(projection.processes.len(), 36);
        assert_eq!(projection.pinned.len(), 2);
        assert_eq!(projection.disks.len(), 3);
        assert!(projection.metrics.iter().any(|metric| metric.tone == Some(ThresholdTone::Green)));
        assert!(projection.metrics.iter().any(|metric| metric.tone == Some(ThresholdTone::Yellow)));
        assert!(projection.metrics.iter().any(|metric| metric.tone == Some(ThresholdTone::Red)));
    }

    #[test]
    fn panel_after_click_parity_matches_csharp_locked_expanded_toggle() {
        // C# FloatingWindowViewModel.cs:258-270：Expanded↔Locked 互切，
        // Collapsed 不响应点击，Clickthrough 透传。
        assert_eq!(panel_after_click(PanelState::Expanded), PanelState::Locked);
        assert_eq!(panel_after_click(PanelState::Locked), PanelState::Expanded);
        assert_eq!(panel_after_click(PanelState::Collapsed), PanelState::Collapsed);
        assert_eq!(panel_after_click(PanelState::Clickthrough), PanelState::Clickthrough);
    }
}
