//! Feature-local projection from the shared DesktopState into generated Slint models.

use std::cmp::Ordering;

use slint::{Color, Model, ModelRc, SharedString, VecModel};

use crate::desktop_state::{DesktopState, PanelState, ProcessRow};
use crate::{MetricData, ProcessData, Shell};

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
    /// Value-text color. `None` renders as C# ColorTextMain (white).
    pub value_tone: Option<ThresholdTone>,
    /// Progress-bar fill tone. `None` hides the bar (NET column has no bar).
    pub bar_tone: Option<ThresholdTone>,
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
pub struct FloatingProjection {
    pub connected: bool,
    pub details_visible: bool,
    pub panel_locked: bool,
    pub metrics: Vec<MetricView>,
    pub pinned: Vec<ProcessView>,
    pub processes: Vec<ProcessView>,
    /// C# details header shows `RAM (63.6 G)` / `VRAM (64.0 G)`.
    pub max_memory_text: String,
    pub max_vram_text: String,
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

    let mut metrics = vec![
        // NET: value = upload line, detail = download line (C# stacks two
        // arrow-suffixed speed lines; no bar). Text stays white.
        MetricView {
            id: "net".into(),
            label: "NET".into(),
            value: format_speed(upload, "\u{2191}"),
            detail: format_speed(download, "\u{2193}"),
            ratio: 0.0,
            value_tone: None,
            bar_tone: None,
        },
        // CPU: value white (F0, no %), bar tone-colored, detail = temp.
        MetricView {
            id: "cpu".into(),
            label: "CPU".into(),
            value: format!("{cpu:.0}"),
            detail: format_temperature(usage.and_then(|value| value.cpu_temperature)),
            ratio: clamped_ratio(cpu, 100.0),
            value_tone: None,
            bar_tone: Some(threshold_tone(cpu)),
        },
        // RAM: value white, bar colored by usage ratio.
        MetricView {
            id: "ram".into(),
            label: "RAM".into(),
            value: format_memory(memory),
            detail: String::new(),
            ratio: clamped_ratio(memory, max_memory),
            value_tone: None,
            bar_tone: Some(memory_tone(memory, max_memory)),
        },
        MetricView {
            id: "gpu".into(),
            label: "GPU".into(),
            value: format!("{gpu:.0}"),
            detail: format_temperature(usage.and_then(|value| value.gpu_temperature)),
            ratio: clamped_ratio(gpu, 100.0),
            value_tone: None,
            bar_tone: Some(threshold_tone(gpu)),
        },
        MetricView {
            id: "vram".into(),
            label: "VRAM".into(),
            value: format_memory(vram),
            detail: String::new(),
            ratio: clamped_ratio(vram, max_vram),
            value_tone: None,
            bar_tone: Some(memory_tone(vram, max_vram)),
        },
    ];
    // POWER: only when the backend reports power available (C# IsPowerVisible).
    if let Some(usage) = usage.filter(|value| value.power_available) {
        let power = usage.total_power;
        let max_power = usage.max_power;
        metrics.push(MetricView {
            id: "power".into(),
            label: "POWER".into(),
            value: format_power(power),
            detail: format_power(max_power),
            ratio: clamped_ratio(power, max_power),
            value_tone: None,
            bar_tone: Some(memory_tone(power, max_power)),
        });
    }

    // C# ApplyPendingProcessRefresh: order by (Memory + Vram) desc; pinned order
    // follows pin sequence, not metrics.
    let mut all_rows: Vec<&ProcessRow> = state.processes.values().collect();
    all_rows.sort_by(|left, right| {
        (metric(right, "memory") + metric(right, "vram"))
            .partial_cmp(&(metric(left, "memory") + metric(left, "vram")))
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

    FloatingProjection {
        connected: state.connected,
        details_visible: state.panel.is_details_visible(),
        panel_locked: state.panel == PanelState::Locked,
        metrics,
        pinned,
        processes,
        max_memory_text: format!("({})", format_memory(max_memory)),
        max_vram_text: format!("({})", format_memory(max_vram)),
    }
}

/// Reconcile an existing Slint model in place instead of swapping the whole
/// `ModelRc`. Swapping forces the Repeater to destroy and rebuild every row
/// (full re-layout + re-raster), which is expensive under the software
/// renderer and fires on *every* SSE frame. Here we diff by index against the
/// live `VecModel`: unchanged rows are untouched, only changed rows repaint,
/// and the row count is adjusted by push/remove. Returns `Some(model)` only on
/// first use (or if the backing model isn't a `VecModel`), when the caller must
/// install a fresh `VecModel`.
fn reconcile_model<T>(model: &ModelRc<T>, next: Vec<T>) -> Option<ModelRc<T>>
where
    T: Clone + PartialEq + 'static,
{
    let Some(vec_model) = model.as_any().downcast_ref::<VecModel<T>>() else {
        return Some(ModelRc::new(VecModel::from(next)));
    };
    let old_len = vec_model.row_count();
    let new_len = next.len();
    for (index, item) in next.iter().enumerate().take(old_len.min(new_len)) {
        if vec_model.row_data(index).as_ref() != Some(item) {
            vec_model.set_row_data(index, item.clone());
        }
    }
    for item in next.iter().skip(old_len) {
        vec_model.push(item.clone());
    }
    for index in (new_len..old_len).rev() {
        vec_model.remove(index);
    }
    None
}

pub fn apply_projection(app: &Shell, projection: FloatingProjection) {
    app.set_connected(projection.connected);
    app.set_details_visible(projection.details_visible);
    app.set_panel_locked(projection.panel_locked);
    let metrics = projection
        .metrics
        .into_iter()
        .map(to_metric_data)
        .collect::<Vec<_>>();
    if let Some(model) = reconcile_model(&app.get_metrics(), metrics) {
        app.set_metrics(model);
    }
    let pinned = projection
        .pinned
        .into_iter()
        .map(to_process_data)
        .collect::<Vec<_>>();
    if let Some(model) = reconcile_model(&app.get_pinned_processes(), pinned) {
        app.set_pinned_processes(model);
    }
    let processes = projection
        .processes
        .into_iter()
        .map(to_process_data)
        .collect::<Vec<_>>();
    if let Some(model) = reconcile_model(&app.get_processes(), processes) {
        app.set_processes(model);
    }
    app.set_max_memory_text(projection.max_memory_text.into());
    app.set_max_vram_text(projection.max_vram_text.into());
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
        value_color: value_color(metric.value_tone),
        bar_color: metric.bar_tone.map(tone_color).unwrap_or(WHITE),
        has_bar: metric.bar_tone.is_some(),
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

const WHITE: Color = Color::from_rgb_u8(0xff, 0xff, 0xff);

fn value_color(tone: Option<ThresholdTone>) -> Color {
    tone.map(tone_color).unwrap_or(WHITE)
}

fn tone_color(tone: ThresholdTone) -> Color {
    match tone {
        ThresholdTone::Green => Color::from_rgb_u8(0x4a, 0xde, 0x80),
        ThresholdTone::Yellow => Color::from_rgb_u8(0xfa, 0xcc, 0x15),
        ThresholdTone::Red => Color::from_rgb_u8(0xf8, 0x71, 0x71),
    }
}

/// C# MemoryUnitConverter parity: MB in, ` M` under 1000, else `val/1024 :.1 G`.
/// Note the 1000 (not 1024) switch threshold and single-letter suffix.
fn format_memory(megabytes: f64) -> String {
    if !megabytes.is_finite() || megabytes <= 0.0 {
        "0 M".into()
    } else if megabytes >= 1000.0 {
        format!("{:.1} G", megabytes / 1024.0)
    } else {
        format!("{megabytes:.0} M")
    }
}

/// C# NetworkSpeedConverter parity: MB/s in. Under 1 MB/s → integer `K/s`+arrow,
/// else `0.0M/s`+arrow. No space before the unit; arrow suffix (↑/↓).
fn format_speed(mb_per_second: f64, arrow: &str) -> String {
    let value = if mb_per_second.is_finite() && mb_per_second > 0.0 {
        mb_per_second
    } else {
        0.0
    };
    if value < 1.0 {
        let kb = (value * 1024.0).round() as i64;
        format!("{kb}K/s{arrow}")
    } else {
        format!("{value:.1}M/s{arrow}")
    }
}

/// C# FormatTemperatureText parity: null/≤0/non-finite → `-°C`, else rounded `N°C`.
fn format_temperature(celsius: Option<f64>) -> String {
    match celsius.filter(|value| value.is_finite() && *value > 0.0) {
        Some(value) => format!("{:.0}\u{00b0}C", value.round()),
        None => "-\u{00b0}C".into(),
    }
}

/// C# PowerValueConverter parity: ≤0/non-finite → `--`, else integer watts (no unit).
fn format_power(watts: f64) -> String {
    if watts.is_finite() && watts > 0.0 {
        format!("{watts:.0}")
    } else {
        "--".into()
    }
}
fn is_power_metric(metric: &str) -> bool {
    metric.eq_ignore_ascii_case("power")
}

fn should_switch_power(action: Option<&super::floating_interactions::PointerAction>) -> bool {
    matches!(
        action,
        Some(super::floating_interactions::PointerAction::LongPress(metric))
            if is_power_metric(metric)
    )
}

#[cfg(windows)]
enum AsyncUiResult {
    Kill {
        pid: u32,
        name: String,
        outcome: crate::win32::KillOutcome,
    },
    PowerWarmup(Result<(), String>),
    PowerSwitch(Result<String, String>),
}

#[cfg(windows)]
struct FloatingRuntimeState {
    clock: super::floating_interactions::SystemClock,
    pointer: super::floating_interactions::PointerMachine,
    kill: super::floating_interactions::KillMachine,
    desktop_state: std::sync::Arc<std::sync::Mutex<DesktopState>>,
    subscription_tx:
        Option<tokio::sync::mpsc::UnboundedSender<crate::service_client::SseSubscription>>,
    async_tx: std::sync::mpsc::Sender<AsyncUiResult>,
    async_rx: std::sync::mpsc::Receiver<AsyncUiResult>,
    toast_until_ms: Option<u64>,
    power_warmup_inflight: bool,
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
        desktop_state,
        subscription_tx,
        async_tx,
        async_rx,
        toast_until_ms: None,
        power_warmup_inflight: false,
        power_inflight: false,
    }));
    let weak = app.as_weak();

    {
        let runtime = Rc::clone(&runtime);
        let weak = weak.clone();
        app.on_pointer_down(move |logical_x, logical_y, metric| {
            let Some(point) = native_pointer_point(handle, logical_x + 12.0, logical_y + 12.0)
            else {
                tracing::warn!("floating pointer-down could not resolve physical coordinates");
                return;
            };
            let metric_id = metric.to_string();
            let warmup_sender = {
                let mut runtime = runtime.borrow_mut();
                let now = runtime.clock.now_ms();
                let should_warmup = is_power_metric(&metric_id) && !runtime.power_warmup_inflight;
                runtime.pointer.press(point, metric_id, now);
                if should_warmup {
                    runtime.power_warmup_inflight = true;
                    Some(runtime.async_tx.clone())
                } else {
                    None
                }
            };
            if let Some(sender) = warmup_sender {
                spawn_power_warmup(sender);
            }
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
            let Some(point) = native_pointer_point(handle, logical_x + 12.0, logical_y + 12.0)
            else {
                return;
            };
            let action = {
                let mut runtime = runtime.borrow_mut();
                let now = runtime.clock.now_ms();
                runtime.pointer.move_pointer(point, now)
            };
            // 越过 5px 阈值：交给原生 OS 模态移动循环（winit drag_window == WPF
            // DragMove）。移交后 OS 全权驱动窗口移动，Slint pointer-move 直到释放
            // 前不再触发；软件渲染下由此获得与 C# 一致的丝滑拖动。
            if action == Some(PointerAction::BeginDrag) {
                if let Some(app) = weak.upgrade() {
                    app.set_organic_state("dragging".into());
                    app.set_active_metric_id("".into());
                    app.set_active_metric_scale(1.0);
                    begin_native_drag(&app);
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
        });
    }

    {
        let runtime = Rc::clone(&runtime);
        let weak = weak.clone();
        app.on_hover_changed(move |inside| {
            let projection = {
                let runtime = runtime.borrow();
                // Drag hands the window to the OS move loop, which drags the
                // window under the cursor and makes `has-hover` flap. Ignore
                // hover-driven panel toggles while a gesture owns the bar so the
                // panel (and therefore the SSE Lite/Full subscription) can't
                // thrash mid-drag. C# parity: dragging never toggles the panel.
                if runtime.pointer.is_active() {
                    return;
                }
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
                    tracing::info!(
                        pid,
                        process = name,
                        timeout_ms = 1_000,
                        arc = 43.98,
                        "G3 kill confirm started"
                    );
                    if let Some(app) = weak.upgrade() {
                        app.set_kill_confirm_pid(pid as i32);
                        app.set_kill_confirm_progress(0.0);
                        show_toast(&app, &runtime, format!("Click again to close {name}"));
                    }
                }
                Some(KillDecision::Execute { pid, name }) => {
                    tracing::info!(
                        pid,
                        process = name,
                        "G3 kill execute dispatched exactly once"
                    );
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
                if should_switch_power(long_press_action.as_ref()) {
                    dispatch_power_switch(&runtime);
                }
                if let Some(PointerAction::LongPress(metric)) = long_press_action {
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
    if should_switch_power(action.as_ref()) {
        dispatch_power_switch(runtime);
    }

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
            // C# parity: short clicks only toggle panel state; Power changes require 2s hold.
            tracing::info!(metric, down_ms = 50, recover_ms = 150, "G3 click feedback");
        }
        Some(PointerAction::LongPress(metric)) => {
            tracing::info!(metric, elapsed_ms = 2_000, "G3 long-press released");
        }
        _ => {}
    }
}
#[cfg(windows)]
fn dispatch_power_switch(runtime: &std::rc::Rc<std::cell::RefCell<FloatingRuntimeState>>) {
    let sender = {
        let mut runtime = runtime.borrow_mut();
        if runtime.power_inflight {
            None
        } else {
            runtime.power_inflight = true;
            Some(runtime.async_tx.clone())
        }
    };
    if let Some(sender) = sender {
        tracing::info!("Power scheme switch dispatched after 2s long press");
        spawn_power(sender);
    }
}

#[cfg(windows)]
fn native_pointer_point(
    handle: crate::win32::WindowHandle,
    logical_x: f32,
    logical_y: f32,
) -> Option<crate::win32::PhysicalPoint> {
    use crate::win32::dpi::native::NativeDpiQuery;
    use crate::win32::taskbar::native::NativeWindowPositionOps;
    use crate::win32::WindowPositionOps;

    let positioner = NativeWindowPositionOps;
    let rect = positioner.window_rect(handle)?;
    let dpi = crate::win32::dpi::dpi_for_window(Some(&NativeDpiQuery), handle);
    Some(super::floating_interactions::logical_pointer_to_physical(
        rect, logical_x, logical_y, dpi,
    ))
}

/// 将当前指针拖动移交给原生 OS 模态移动循环（winit `drag_window()`，等价于
/// WPF `DragMove()`）。使用 winit 方法而非裸 FFI，可让 winit 维护内部
/// `dragging` 标志，从而在 `WM_EXITSIZEMOVE` 时合成 pointer-up，保证 Slint
/// 指针状态机正常结束拖动。替代逐帧 `SetWindowPos`，软件渲染下与 C# 拖动等效。
#[cfg(windows)]
fn begin_native_drag(app: &Shell) {
    use slint::winit_030::WinitWindowAccessor;
    use slint::ComponentHandle;

    let started = app
        .window()
        .with_winit_window(|window| window.drag_window().is_ok())
        .unwrap_or(false);
    if !started {
        tracing::warn!("native drag_window handoff failed; window did not enter OS move loop");
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
    let snapped =
        monitor.and_then(|m| super::floating_interactions::snapped_release_rect(current, m));
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
            let clamped =
                super::floating_interactions::clamp_rect_to_work_area(current, monitor.work_area);
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
        // A-fix: the floating panel holds a constant Full subscription for its
        // entire lifetime. Panel hover/click toggles Collapsed<->Expanded no
        // longer change the SSE mode, so they can't tear down and re-establish
        // the HTTP stream (SSE encodes mode in the URL query, so any mode flip
        // forces a reconnect). Only pin-set changes travel here now, which are
        // rare and user-driven. Localhost Full is cheap; collapsed rendering is
        // unaffected since the projection still hides the process list.
        let _ = sender.send(crate::service_client::SseSubscription::new(
            xhm_core::wire::SubscriptionMode::Full,
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
fn spawn_kill(sender: std::sync::mpsc::Sender<AsyncUiResult>, pid: u32, name: String) {
    std::thread::spawn(move || {
        use crate::win32::process::native::WindowsProcessManager;

        let guard = crate::win32::KillOnce::new(WindowsProcessManager);
        let outcome = guard.kill_process_tree(pid);
        let _ = sender.send(AsyncUiResult::Kill { pid, name, outcome });
    });
}

#[cfg(windows)]
fn spawn_power_warmup(sender: std::sync::mpsc::Sender<AsyncUiResult>) {
    std::thread::spawn(move || {
        let result = (|| -> Result<(), String> {
            let runtime = tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
                .map_err(|error| error.to_string())?;
            runtime.block_on(async {
                let config = crate::config::Config::load().await;
                let client = crate::service_client::rest::RestClient::new(&config)
                    .map_err(|error| error.to_string())?;
                client
                    .warmup()
                    .await
                    .map(|_| ())
                    .map_err(|error| error.to_string())
            })
        })();
        let _ = sender.send(AsyncUiResult::PowerWarmup(result));
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
        let _ = sender.send(AsyncUiResult::PowerSwitch(result));
    });
}

#[cfg(windows)]
fn apply_async_result(
    app: &Shell,
    runtime: &std::rc::Rc<std::cell::RefCell<FloatingRuntimeState>>,
    result: AsyncUiResult,
) {
    match result {
        AsyncUiResult::Kill { pid, name, outcome } => {
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
                crate::win32::KillOutcome::Other(error) => {
                    format!("Failed to close {name}: {error}")
                }
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
        AsyncUiResult::PowerWarmup(result) => {
            runtime.borrow_mut().power_warmup_inflight = false;
            match result {
                Ok(()) => tracing::debug!("Power device verification warmup completed"),
                Err(error) => tracing::warn!(%error, "Power device verification warmup failed"),
            }
        }
        AsyncUiResult::PowerSwitch(result) => {
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
    fn power_metric_visibility_follows_service_availability() {
        let mut state = smoke_state(1_234);
        state.usage.as_mut().unwrap().power_available = false;
        let unavailable = project_state(&state);
        assert_eq!(
            unavailable
                .metrics
                .iter()
                .map(|metric| metric.id.as_str())
                .collect::<Vec<_>>(),
            ["net", "cpu", "ram", "gpu", "vram"]
        );

        state.usage.as_mut().unwrap().power_available = true;
        let available = project_state(&state);
        assert_eq!(available.metrics.last().unwrap().id, "power");
        assert_eq!(available.metrics.len(), 6);
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
        assert_eq!(
            panel_after_hover(PanelState::Expanded, false),
            PanelState::Collapsed
        );
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
        assert_eq!(
            state.processes[&44].display_name.as_deref(),
            Some("Old Display")
        );

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
    fn smoke_fixture_exercises_long_list_and_all_threshold_colors() {
        let projection = smoke_projection(1234);
        assert_eq!(projection.processes.len(), 36);
        assert_eq!(projection.pinned.len(), 2);
        assert!(projection
            .metrics
            .iter()
            .any(|metric| metric.bar_tone == Some(ThresholdTone::Green)));
        assert!(projection
            .metrics
            .iter()
            .any(|metric| metric.bar_tone == Some(ThresholdTone::Yellow)));
        assert!(projection
            .metrics
            .iter()
            .any(|metric| metric.bar_tone == Some(ThresholdTone::Red)));
    }

    #[test]
    fn only_power_long_press_requests_scheme_switch() {
        use super::super::floating_interactions::PointerAction;

        assert!(!should_switch_power(Some(&PointerAction::Click(
            "power".into()
        ))));
        assert!(should_switch_power(Some(&PointerAction::LongPress(
            "power".into()
        ))));
        assert!(!should_switch_power(Some(&PointerAction::LongPress(
            "cpu".into()
        ))));
        assert!(!should_switch_power(None));
    }

    #[test]
    fn panel_after_click_parity_matches_csharp_locked_expanded_toggle() {
        // C# FloatingWindowViewModel.cs:258-270：Expanded↔Locked 互切，
        // Collapsed 不响应点击，Clickthrough 透传。
        assert_eq!(panel_after_click(PanelState::Expanded), PanelState::Locked);
        assert_eq!(panel_after_click(PanelState::Locked), PanelState::Expanded);
        assert_eq!(
            panel_after_click(PanelState::Collapsed),
            PanelState::Collapsed
        );
        assert_eq!(
            panel_after_click(PanelState::Clickthrough),
            PanelState::Clickthrough
        );
    }

    #[test]
    fn reconcile_model_updates_in_place_without_swapping() {
        let model: ModelRc<i32> = ModelRc::new(VecModel::from(vec![1, 2, 3]));
        let backing_ptr =
            model.as_any().downcast_ref::<VecModel<i32>>().unwrap() as *const VecModel<i32>;

        // Same length, one changed row: reconciles in place (None), keeps the
        // same backing VecModel, updates only the changed index.
        assert!(reconcile_model(&model, vec![1, 9, 3]).is_none());
        assert_eq!(
            model.as_any().downcast_ref::<VecModel<i32>>().unwrap() as *const VecModel<i32>,
            backing_ptr,
            "reconcile must not swap the backing model"
        );
        assert_eq!(model.row_data(1), Some(9));
        assert_eq!(model.row_count(), 3);

        // Grow adds the new tail rows.
        assert!(reconcile_model(&model, vec![1, 9, 3, 4, 5]).is_none());
        assert_eq!(model.row_count(), 5);
        assert_eq!(model.row_data(4), Some(5));

        // Shrink drops the surplus rows.
        assert!(reconcile_model(&model, vec![7]).is_none());
        assert_eq!(model.row_count(), 1);
        assert_eq!(model.row_data(0), Some(7));
    }
}
