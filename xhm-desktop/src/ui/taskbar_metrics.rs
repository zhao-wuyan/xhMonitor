//! Taskbar metrics projection, presentation normalization, and pointer bridge.
//!
//! This module owns G4's second-window rendering contract. Native placement and
//! snap calculations remain in the G2 Win32 boundary; the UI only forwards intent.

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex};

use slint::{Color, ComponentHandle, Model, ModelRc, SharedString, VecModel};

use crate::desktop_state::DesktopState;
use crate::win32::{PhysicalSize, TaskbarEdge};
use crate::{TaskbarMetricData, TaskbarWindow};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TaskbarVisualStyle {
    Text,
    Bar,
}

impl TaskbarVisualStyle {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Text => "Text",
            Self::Bar => "Bar",
        }
    }

    pub fn parse(value: &str) -> Self {
        if value.trim().eq_ignore_ascii_case("Text") {
            Self::Text
        } else {
            Self::Bar
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TaskbarOrientation {
    Horizontal,
    Vertical,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TaskbarPresentation {
    TextHorizontal,
    TextVertical,
    BarHorizontal,
    BarVertical,
}

impl TaskbarPresentation {
    pub const ALL: [Self; 4] = [
        Self::TextHorizontal,
        Self::TextVertical,
        Self::BarHorizontal,
        Self::BarVertical,
    ];

    pub const fn new(style: TaskbarVisualStyle, orientation: TaskbarOrientation) -> Self {
        match (style, orientation) {
            (TaskbarVisualStyle::Text, TaskbarOrientation::Horizontal) => Self::TextHorizontal,
            (TaskbarVisualStyle::Text, TaskbarOrientation::Vertical) => Self::TextVertical,
            (TaskbarVisualStyle::Bar, TaskbarOrientation::Horizontal) => Self::BarHorizontal,
            (TaskbarVisualStyle::Bar, TaskbarOrientation::Vertical) => Self::BarVertical,
        }
    }

    pub const fn style(self) -> TaskbarVisualStyle {
        match self {
            Self::TextHorizontal | Self::TextVertical => TaskbarVisualStyle::Text,
            Self::BarHorizontal | Self::BarVertical => TaskbarVisualStyle::Bar,
        }
    }

    pub const fn orientation(self) -> TaskbarOrientation {
        match self {
            Self::TextHorizontal | Self::BarHorizontal => TaskbarOrientation::Horizontal,
            Self::TextVertical | Self::BarVertical => TaskbarOrientation::Vertical,
        }
    }
}

pub const fn orientation_for_edge(edge: TaskbarEdge) -> TaskbarOrientation {
    match edge {
        TaskbarEdge::Top | TaskbarEdge::Bottom => TaskbarOrientation::Horizontal,
        TaskbarEdge::Left | TaskbarEdge::Right => TaskbarOrientation::Vertical,
    }
}

pub const fn normalize_presentation(
    edge: TaskbarEdge,
    style: TaskbarVisualStyle,
) -> TaskbarPresentation {
    TaskbarPresentation::new(style, orientation_for_edge(edge))
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TaskbarRenderGeometry {
    pub width: i32,
    pub height: i32,
}

impl TaskbarRenderGeometry {
    pub const fn physical_size(self) -> PhysicalSize {
        PhysicalSize::new(self.width, self.height)
    }
}

pub fn render_geometry(
    presentation: TaskbarPresentation,
    column_count: usize,
    gap: u8,
) -> TaskbarRenderGeometry {
    let count = column_count.max(1) as i32;
    let gap = i32::from(gap.min(24));
    match presentation.orientation() {
        TaskbarOrientation::Horizontal => {
            let column = match presentation.style() {
                TaskbarVisualStyle::Text => 58,
                TaskbarVisualStyle::Bar => 62,
            };
            TaskbarRenderGeometry {
                width: (count * column + (count - 1) * gap + 12).max(88),
                height: 38,
            }
        }
        TaskbarOrientation::Vertical => {
            let row = match presentation.style() {
                TaskbarVisualStyle::Text => 40,
                TaskbarVisualStyle::Bar => 46,
            };
            TaskbarRenderGeometry {
                width: 40,
                height: (count * row + (count - 1) * gap + 12).max(72),
            }
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TaskbarSettings {
    pub opacity_percent: u8,
    pub process_keywords: String,
    pub monitor_cpu: bool,
    pub monitor_memory: bool,
    pub monitor_gpu: bool,
    pub monitor_vram: bool,
    pub monitor_power: bool,
    pub monitor_network: bool,
    pub enable_floating_mode: bool,
    pub enable_edge_dock_mode: bool,
    pub dock_cpu_label: String,
    pub dock_memory_label: String,
    pub dock_gpu_label: String,
    pub dock_vram_label: String,
    pub dock_power_label: String,
    pub dock_upload_label: String,
    pub dock_download_label: String,
    pub dock_column_gap: u8,
    pub dock_visual_style: TaskbarVisualStyle,
}

impl Default for TaskbarSettings {
    fn default() -> Self {
        Self {
            opacity_percent: 90,
            process_keywords: "[]".into(),
            monitor_cpu: true,
            monitor_memory: true,
            monitor_gpu: true,
            monitor_vram: true,
            monitor_power: true,
            monitor_network: true,
            enable_floating_mode: true,
            enable_edge_dock_mode: true,
            dock_cpu_label: "CPU".into(),
            dock_memory_label: "RAM".into(),
            dock_gpu_label: "GPU".into(),
            dock_vram_label: "VRAM".into(),
            dock_power_label: "PWR".into(),
            dock_upload_label: "UP".into(),
            dock_download_label: "DN".into(),
            dock_column_gap: 0,
            dock_visual_style: TaskbarVisualStyle::Bar,
        }
    }
}

impl TaskbarSettings {
    pub fn normalized(mut self) -> Self {
        self.opacity_percent = self.opacity_percent.clamp(20, 100);
        self.dock_column_gap = self.dock_column_gap.min(24);
        self.dock_cpu_label = normalized_label(&self.dock_cpu_label, "CPU");
        self.dock_memory_label = normalized_label(&self.dock_memory_label, "RAM");
        self.dock_gpu_label = normalized_label(&self.dock_gpu_label, "GPU");
        self.dock_vram_label = normalized_label(&self.dock_vram_label, "VRAM");
        self.dock_power_label = normalized_label(&self.dock_power_label, "PWR");
        self.dock_upload_label = normalized_label(&self.dock_upload_label, "UP");
        self.dock_download_label = normalized_label(&self.dock_download_label, "DN");
        self
    }

    pub fn apply_allowed_groups(&mut self, groups: &BTreeMap<String, BTreeMap<String, String>>) {
        let appearance = groups.get("Appearance");
        let data_collection = groups.get("DataCollection");
        let monitoring = groups.get("Monitoring");

        if let Some(value) = appearance.and_then(|group| group.get("Opacity")) {
            self.opacity_percent = parse_u8_clamped(value, 20, 100, self.opacity_percent);
        }
        if let Some(value) = data_collection.and_then(|group| group.get("ProcessKeywords")) {
            self.process_keywords = value.clone();
        }
        if let Some(group) = monitoring {
            self.monitor_cpu = parse_bool(group.get("MonitorCpu"), self.monitor_cpu);
            self.monitor_memory = parse_bool(group.get("MonitorMemory"), self.monitor_memory);
            self.monitor_gpu = parse_bool(group.get("MonitorGpu"), self.monitor_gpu);
            self.monitor_vram = parse_bool(group.get("MonitorVram"), self.monitor_vram);
            self.monitor_power = parse_bool(group.get("MonitorPower"), self.monitor_power);
            self.monitor_network = parse_bool(group.get("MonitorNetwork"), self.monitor_network);
            self.enable_floating_mode =
                parse_bool(group.get("EnableFloatingMode"), self.enable_floating_mode);
            self.enable_edge_dock_mode =
                parse_bool(group.get("EnableEdgeDockMode"), self.enable_edge_dock_mode);
            replace_string(&mut self.dock_cpu_label, group.get("DockCpuLabel"));
            replace_string(&mut self.dock_memory_label, group.get("DockMemoryLabel"));
            replace_string(&mut self.dock_gpu_label, group.get("DockGpuLabel"));
            replace_string(&mut self.dock_vram_label, group.get("DockVramLabel"));
            replace_string(&mut self.dock_power_label, group.get("DockPowerLabel"));
            replace_string(&mut self.dock_upload_label, group.get("DockUploadLabel"));
            replace_string(&mut self.dock_download_label, group.get("DockDownloadLabel"));
            if let Some(value) = group.get("DockColumnGap") {
                self.dock_column_gap = parse_u8_clamped(value, 0, 24, self.dock_column_gap);
            }
            if let Some(value) = group.get("DockVisualStyle") {
                self.dock_visual_style = TaskbarVisualStyle::parse(value);
            }
        }
        *self = self.clone().normalized();
    }
}

fn replace_string(target: &mut String, value: Option<&String>) {
    if let Some(value) = value {
        *target = value.clone();
    }
}

fn parse_bool(value: Option<&String>, fallback: bool) -> bool {
    match value.map(|value| value.trim()) {
        Some(value) if value.eq_ignore_ascii_case("true") || value == "1" => true,
        Some(value) if value.eq_ignore_ascii_case("false") || value == "0" => false,
        _ => fallback,
    }
}

fn parse_u8_clamped(value: &str, min: u8, max: u8, fallback: u8) -> u8 {
    value
        .trim()
        .parse::<u8>()
        .map(|value| value.clamp(min, max))
        .unwrap_or(fallback)
}

fn normalized_label(value: &str, fallback: &str) -> String {
    let value = value.trim();
    if value.is_empty() {
        fallback.to_owned()
    } else {
        value.to_owned()
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TaskbarDisplayState {
    pub edge: TaskbarEdge,
    pub docked: bool,
    pub dragging: bool,
}

impl Default for TaskbarDisplayState {
    fn default() -> Self {
        Self {
            edge: TaskbarEdge::Bottom,
            docked: true,
            dragging: false,
        }
    }
}

pub type SharedTaskbarSettings = Arc<Mutex<TaskbarSettings>>;
pub type SharedTaskbarDisplay = Arc<Mutex<TaskbarDisplayState>>;

pub fn shared_settings() -> SharedTaskbarSettings {
    Arc::new(Mutex::new(TaskbarSettings::default()))
}

pub fn shared_display_state() -> SharedTaskbarDisplay {
    Arc::new(Mutex::new(TaskbarDisplayState::default()))
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct TaskbarColumnToken {
    id: String,
    label: String,
    is_bar_metric: bool,
    unit: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct TaskbarLayoutToken {
    presentation: TaskbarPresentation,
    gap: u8,
    columns: Vec<TaskbarColumnToken>,
}

impl TaskbarLayoutToken {
    fn fingerprint(&self) -> String {
        let presentation = match self.presentation {
            TaskbarPresentation::TextHorizontal => "text-horizontal",
            TaskbarPresentation::TextVertical => "text-vertical",
            TaskbarPresentation::BarHorizontal => "bar-horizontal",
            TaskbarPresentation::BarVertical => "bar-vertical",
        };
        let columns = self
            .columns
            .iter()
            .map(|column| {
                format!(
                    "{}:{}:{}:{}",
                    column.id, column.label, column.is_bar_metric, column.unit
                )
            })
            .collect::<Vec<_>>()
            .join("|");
        format!("{presentation};{};{columns}", self.gap)
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct TaskbarMetricView {
    pub id: String,
    pub label: String,
    pub value: String,
    pub detail: String,
    pub ratio: f32,
    pub accent: (u8, u8, u8),
    pub is_bar_metric: bool,
    unit: String,
}

#[derive(Debug, Clone, PartialEq)]
pub struct TaskbarProjection {
    pub presentation: TaskbarPresentation,
    pub gap: u8,
    pub docked: bool,
    pub dragging: bool,
    pub edge: TaskbarEdge,
    pub connected: bool,
    pub metrics: Vec<TaskbarMetricView>,
    layout: TaskbarLayoutToken,
}

impl TaskbarProjection {
    fn fingerprint(&self) -> String {
        self.layout.fingerprint()
    }

    pub fn geometry(&self) -> TaskbarRenderGeometry {
        render_geometry(self.presentation, self.metrics.len(), self.gap)
    }
}

pub fn project_state(
    state: &DesktopState,
    settings: &TaskbarSettings,
    display: TaskbarDisplayState,
) -> TaskbarProjection {
    let settings = settings.clone().normalized();
    let presentation = if display.docked {
        normalize_presentation(display.edge, settings.dock_visual_style)
    } else {
        TaskbarPresentation::new(settings.dock_visual_style, TaskbarOrientation::Horizontal)
    };
    let usage = state.usage.as_ref();
    let max_memory = usage
        .map(|usage| usage.max_memory)
        .or_else(|| state.limits.as_ref().map(|limits| limits.max_memory))
        .unwrap_or(0.0);
    let max_vram = usage
        .map(|usage| usage.max_vram)
        .or_else(|| state.limits.as_ref().map(|limits| limits.max_vram))
        .unwrap_or(0.0);
    let max_power = usage.map(|usage| usage.max_power).unwrap_or(0.0);

    let mut metrics = Vec::with_capacity(7);
    if settings.monitor_network {
        let upload = usage.map(|usage| usage.upload_speed).unwrap_or(0.0);
        let download = usage.map(|usage| usage.download_speed).unwrap_or(0.0);
        metrics.push(metric(
            "upload",
            &settings.dock_upload_label,
            compact_speed(upload),
            "network",
            0.0,
            (0x22, 0xd3, 0xee),
            false,
        ));
        metrics.push(metric(
            "download",
            &settings.dock_download_label,
            compact_speed(download),
            "network",
            0.0,
            (0x22, 0xd3, 0xee),
            false,
        ));
    }
    if settings.monitor_cpu {
        let value = usage.map(|usage| usage.total_cpu).unwrap_or(0.0);
        let temperature = usage
            .and_then(|usage| usage.cpu_temperature)
            .filter(|value| value.is_finite())
            .map(|value| format!("{value:.0} C"))
            .unwrap_or_default();
        metrics.push(metric(
            "cpu",
            &settings.dock_cpu_label,
            format!("{value:.0}%"),
            &temperature,
            ratio(value, 100.0),
            threshold_color(value),
            true,
        ));
    }
    if settings.monitor_memory {
        let value = usage.map(|usage| usage.total_memory).unwrap_or(0.0);
        metrics.push(metric(
            "memory",
            &settings.dock_memory_label,
            compact_memory(value),
            &format!("/ {}", compact_memory(max_memory)),
            ratio(value, max_memory),
            (0x4a, 0xde, 0x80),
            true,
        ));
    }
    if settings.monitor_gpu {
        let value = usage.map(|usage| usage.total_gpu).unwrap_or(0.0);
        let temperature = usage
            .and_then(|usage| usage.gpu_temperature)
            .filter(|value| value.is_finite())
            .map(|value| format!("{value:.0} C"))
            .unwrap_or_default();
        metrics.push(metric(
            "gpu",
            &settings.dock_gpu_label,
            format!("{value:.0}%"),
            &temperature,
            ratio(value, 100.0),
            threshold_color(value),
            true,
        ));
    }
    if settings.monitor_vram {
        let value = usage.map(|usage| usage.total_vram).unwrap_or(0.0);
        metrics.push(metric(
            "vram",
            &settings.dock_vram_label,
            compact_memory(value),
            &format!("/ {}", compact_memory(max_vram)),
            ratio(value, max_vram),
            (0xf5, 0x9e, 0x0b),
            true,
        ));
    }
    if settings.monitor_power && usage.is_some_and(|usage| usage.power_available) {
        let value = usage.map(|usage| usage.total_power).unwrap_or(0.0);
        metrics.push(metric(
            "power",
            &settings.dock_power_label,
            format!("{value:.0}W"),
            &format!("/ {max_power:.0}W"),
            ratio(value, max_power),
            (0xf8, 0x71, 0x71),
            true,
        ));
    }
    if metrics.is_empty() {
        metrics.push(metric(
            "empty",
            "X",
            "--".into(),
            "",
            0.0,
            (0xff, 0xff, 0xff),
            false,
        ));
    }

    let layout = TaskbarLayoutToken {
        presentation,
        gap: settings.dock_column_gap,
        columns: metrics
            .iter()
            .map(|metric| TaskbarColumnToken {
                id: metric.id.clone(),
                label: metric.label.clone(),
                is_bar_metric: metric.is_bar_metric,
                unit: metric.unit.clone(),
            })
            .collect(),
    };

    TaskbarProjection {
        presentation,
        gap: settings.dock_column_gap,
        docked: display.docked,
        dragging: display.dragging,
        edge: display.edge,
        connected: state.connected,
        metrics,
        layout,
    }
}

fn metric(
    id: &str,
    label: &str,
    value: String,
    detail: &str,
    ratio: f32,
    accent: (u8, u8, u8),
    is_bar_metric: bool,
) -> TaskbarMetricView {
    let unit = value
        .chars()
        .skip_while(|character| character.is_ascii_digit() || *character == '.' || *character == '-')
        .collect();
    TaskbarMetricView {
        id: id.into(),
        label: label.into(),
        value,
        detail: detail.into(),
        ratio,
        accent,
        is_bar_metric,
        unit,
    }
}

fn ratio(value: f64, maximum: f64) -> f32 {
    if !value.is_finite() || !maximum.is_finite() || maximum <= 0.0 {
        0.0
    } else {
        (value / maximum).clamp(0.0, 1.0) as f32
    }
}

fn threshold_color(value: f64) -> (u8, u8, u8) {
    if value >= 90.0 {
        (0xf8, 0x71, 0x71)
    } else if value >= 70.0 {
        (0xfa, 0xcc, 0x15)
    } else {
        (0x4a, 0xde, 0x80)
    }
}

fn compact_memory(value: f64) -> String {
    if !value.is_finite() || value <= 0.0 {
        "0M".into()
    } else if value >= 1024.0 {
        format!("{:.1}G", value / 1024.0)
    } else {
        format!("{value:.0}M")
    }
}

fn compact_speed(value: f64) -> String {
    if !value.is_finite() || value <= 0.0 {
        "0K/s".into()
    } else if value >= 1.0 {
        format!("{value:.1}M/s")
    } else {
        format!("{:.0}K/s", value * 1024.0)
    }
}

fn to_metric_data(metric: &TaskbarMetricView) -> TaskbarMetricData {
    TaskbarMetricData {
        id: SharedString::from(&metric.id),
        label: SharedString::from(&metric.label),
        value: SharedString::from(&metric.value),
        detail: SharedString::from(&metric.detail),
        ratio: metric.ratio,
        accent: Color::from_rgb_u8(metric.accent.0, metric.accent.1, metric.accent.2),
        is_bar_metric: metric.is_bar_metric,
    }
}

fn edge_state(edge: TaskbarEdge) -> SharedString {
    match edge {
        TaskbarEdge::Left => "dock-left",
        TaskbarEdge::Top => "dock-top",
        TaskbarEdge::Right => "dock-right",
        TaskbarEdge::Bottom => "dock-bottom",
    }
    .into()
}

pub fn apply_projection(app: &TaskbarWindow, projection: TaskbarProjection) {
    let fingerprint = projection.fingerprint();
    let values = projection
        .metrics
        .iter()
        .map(to_metric_data)
        .collect::<Vec<_>>();
    let rebuild = app.get_layout_token().as_str() != fingerprint;
    if rebuild {
        app.set_metrics(ModelRc::new(VecModel::from(values)));
        app.set_layout_token(fingerprint.into());
    } else {
        let model = app.get_metrics();
        if model.row_count() == values.len() {
            for (index, value) in values.into_iter().enumerate() {
                model.set_row_data(index, value);
            }
        } else {
            app.set_metrics(ModelRc::new(VecModel::from(values)));
        }
    }
    app.set_connected(projection.connected);
    app.set_vertical(matches!(
        projection.presentation.orientation(),
        TaskbarOrientation::Vertical
    ));
    app.set_bar_visual(matches!(projection.presentation.style(), TaskbarVisualStyle::Bar));
    app.set_gap(i32::from(projection.gap));
    app.set_docked(projection.docked);
    app.set_dragging(projection.dragging);
    app.set_edge_state(edge_state(projection.edge));
    app.set_status_text(
        if projection.connected {
            "taskbar connected"
        } else {
            "taskbar reconnecting"
        }
        .into(),
    );
}

pub fn dispatch_projection(
    app: &slint::Weak<TaskbarWindow>,
    projection: TaskbarProjection,
) -> Result<(), slint::EventLoopError> {
    app.upgrade_in_event_loop(move |app| apply_projection(&app, projection))
}

#[derive(Debug, Default)]
pub struct TaskbarRenderState {
    last_layout: Option<TaskbarLayoutToken>,
    rebuild_count: usize,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RenderUpdate {
    Rebuilt,
    ValuesUpdated,
}

impl TaskbarRenderState {
    pub fn apply(&mut self, projection: &TaskbarProjection) -> RenderUpdate {
        if self.last_layout.as_ref() == Some(&projection.layout) {
            RenderUpdate::ValuesUpdated
        } else {
            self.last_layout = Some(projection.layout.clone());
            self.rebuild_count += 1;
            RenderUpdate::Rebuilt
        }
    }

    pub const fn rebuild_count(&self) -> usize {
        self.rebuild_count
    }
}

#[cfg(windows)]
struct TaskbarRuntimeState {
    drag_anchor: Option<crate::win32::DragAnchor>,
    display: SharedTaskbarDisplay,
    desktop_state: Arc<Mutex<DesktopState>>,
    settings: SharedTaskbarSettings,
}

#[cfg(windows)]
pub struct TaskbarUiRuntime {
    _timer: slint::Timer,
    cancellation: tokio_util::sync::CancellationToken,
}

#[cfg(windows)]
impl Drop for TaskbarUiRuntime {
    fn drop(&mut self) {
        self.cancellation.cancel();
    }
}

#[cfg(windows)]
pub fn install_runtime(
    app: &TaskbarWindow,
    handle: crate::win32::WindowHandle,
    desktop_state: Arc<Mutex<DesktopState>>,
    settings: SharedTaskbarSettings,
    display: SharedTaskbarDisplay,
    cancellation: tokio_util::sync::CancellationToken,
) -> TaskbarUiRuntime {
    use std::cell::RefCell;
    use std::rc::Rc;

    let runtime = Rc::new(RefCell::new(TaskbarRuntimeState {
        drag_anchor: None,
        display,
        desktop_state,
        settings,
    }));
    let weak = app.as_weak();

    {
        let runtime = Rc::clone(&runtime);
        let weak = weak.clone();
        app.on_pointer_down(move |logical_x, logical_y| {
            let Some((cursor, anchor)) = native_pointer(handle, logical_x, logical_y) else {
                tracing::warn!("taskbar pointer-down could not resolve physical coordinates");
                return;
            };
            let (projection, origin) = {
                let runtime = runtime.borrow_mut();
                let mut display = runtime
                    .display
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner);
                if display.docked {
                    display.docked = false;
                }
                display.dragging = true;
                let settings = runtime
                    .settings
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner)
                    .clone();
                let state = runtime
                    .desktop_state
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner)
                    .clone();
                let projection = project_state(&state, &settings, *display);
                let origin = crate::win32::origin_for_anchor(cursor, projection.geometry().physical_size(), anchor);
                (projection, origin)
            };
            runtime.borrow_mut().drag_anchor = Some(anchor);
            if let Some(app) = weak.upgrade() {
                apply_projection(&app, projection);
            }
            if let Some(origin) = origin {
                move_to(handle, origin.x, origin.y);
            }
        });
    }

    {
        let runtime = Rc::clone(&runtime);
        app.on_pointer_move(move |logical_x, logical_y| {
            let anchor = runtime.borrow().drag_anchor;
            let Some(anchor) = anchor else {
                return;
            };
            let Some((cursor, _)) = native_pointer(handle, logical_x, logical_y) else {
                return;
            };
            let positioner = crate::win32::taskbar::native::NativeWindowPositionOps;
            use crate::win32::WindowPositionOps;
            let Some(rect) = positioner.window_rect(handle) else {
                return;
            };
            if let Some(origin) = crate::win32::origin_for_anchor(cursor, rect.size(), anchor) {
                move_to(handle, origin.x, origin.y);
            }
        });
    }

    {
        let runtime = Rc::clone(&runtime);
        let weak = weak.clone();
        app.on_pointer_up(move || finish_drag(&runtime, &weak, handle));
    }

    {
        let runtime = Rc::clone(&runtime);
        let weak = weak.clone();
        app.on_pointer_cancel(move || finish_drag(&runtime, &weak, handle));
    }

    let timer = slint::Timer::default();
    {
        let runtime = Rc::clone(&runtime);
        let weak = weak.clone();
        timer.start(
            slint::TimerMode::Repeated,
            std::time::Duration::from_millis(250),
            move || {
                let projection = {
                    let runtime = runtime.borrow();
                    let state = runtime
                        .desktop_state
                        .lock()
                        .unwrap_or_else(std::sync::PoisonError::into_inner)
                        .clone();
                    let settings = runtime
                        .settings
                        .lock()
                        .unwrap_or_else(std::sync::PoisonError::into_inner)
                        .clone();
                    let display = *runtime
                        .display
                        .lock()
                        .unwrap_or_else(std::sync::PoisonError::into_inner);
                    project_state(&state, &settings, display)
                };
                if let Some(app) = weak.upgrade() {
                    apply_projection(&app, projection);
                }
            },
        );
    }

    TaskbarUiRuntime {
        _timer: timer,
        cancellation,
    }
}

#[cfg(windows)]
fn native_pointer(
    handle: crate::win32::WindowHandle,
    logical_x: f32,
    logical_y: f32,
) -> Option<(crate::win32::PhysicalPoint, crate::win32::DragAnchor)> {
    use crate::win32::dpi::native::NativeDpiQuery;
    use crate::win32::taskbar::native::NativeWindowPositionOps;
    use crate::win32::WindowPositionOps;

    let positioner = NativeWindowPositionOps;
    let rect = positioner.window_rect(handle)?;
    let dpi = crate::win32::dpi::dpi_for_window(Some(&NativeDpiQuery), handle);
    let cursor = super::floating_interactions::logical_pointer_to_physical(rect, logical_x, logical_y, dpi);
    let anchor = crate::win32::drag_anchor(cursor, rect)?;
    Some((cursor, anchor))
}

#[cfg(windows)]
fn move_to(handle: crate::win32::WindowHandle, x: i32, y: i32) {
    use crate::win32::taskbar::native::NativeWindowPositionOps;
    use crate::win32::WindowPositionOps;

    let positioner = NativeWindowPositionOps;
    if !positioner.move_topmost(handle, x, y) {
        tracing::warn!(x, y, "SetWindowPos failed during taskbar drag");
    }
}

#[cfg(windows)]
fn finish_drag(
    runtime: &std::rc::Rc<std::cell::RefCell<TaskbarRuntimeState>>,
    weak: &slint::Weak<TaskbarWindow>,
    handle: crate::win32::WindowHandle,
) {
    use crate::win32::taskbar::native::NativeWindowPositionOps;
    use crate::win32::WindowPositionOps;

    let positioner = NativeWindowPositionOps;
    let Some(rect) = positioner.window_rect(handle) else {
        runtime.borrow_mut().drag_anchor = None;
        return;
    };
    let snapped = super::floating_interactions::native::monitor_geometry(handle)
        .and_then(|monitor| crate::win32::snap_taskbar_window(rect, monitor.bounds));
    let projection = {
        let runtime = runtime.borrow_mut();
        let mut display = runtime
            .display
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        display.dragging = false;
        if let Some((_, edge)) = snapped {
            display.edge = edge;
            display.docked = true;
        } else {
            display.docked = false;
        }
        let settings = runtime
            .settings
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .clone();
        let state = runtime
            .desktop_state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .clone();
        project_state(&state, &settings, *display)
    };
    if let Some((snap_rect, _)) = snapped {
        move_to(handle, snap_rect.left, snap_rect.top);
        tracing::info!(x = snap_rect.left, y = snap_rect.top, "taskbar drag snapped through G2 geometry");
    } else {
        tracing::info!(x = rect.left, y = rect.top, "taskbar drag released as floating");
    }
    if let Some(app) = weak.upgrade() {
        apply_projection(&app, projection);
    }
    runtime.borrow_mut().drag_anchor = None;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sixteen_presentation_edge_cells_have_geometry() {
        for presentation in TaskbarPresentation::ALL {
            for edge in [
                TaskbarEdge::Left,
                TaskbarEdge::Top,
                TaskbarEdge::Right,
                TaskbarEdge::Bottom,
            ] {
                let geometry = render_geometry(presentation, 7, 4);
                assert!(geometry.width > 0, "{presentation:?} {edge:?}");
                assert!(geometry.height > 0, "{presentation:?} {edge:?}");
            }
        }
    }

    #[test]
    fn edge_normalization_uses_only_production_orientations() {
        for style in [TaskbarVisualStyle::Text, TaskbarVisualStyle::Bar] {
            assert_eq!(
                normalize_presentation(TaskbarEdge::Top, style).orientation(),
                TaskbarOrientation::Horizontal
            );
            assert_eq!(
                normalize_presentation(TaskbarEdge::Bottom, style).orientation(),
                TaskbarOrientation::Horizontal
            );
            assert_eq!(
                normalize_presentation(TaskbarEdge::Left, style).orientation(),
                TaskbarOrientation::Vertical
            );
            assert_eq!(
                normalize_presentation(TaskbarEdge::Right, style).orientation(),
                TaskbarOrientation::Vertical
            );
        }
    }

    #[test]
    fn values_update_without_layout_rebuild() {
        let metric = metric("cpu", "CPU", "20%".into(), "", 0.2, (1, 2, 3), true);
        let layout = TaskbarLayoutToken {
            presentation: TaskbarPresentation::BarHorizontal,
            gap: 4,
            columns: vec![TaskbarColumnToken {
                id: "cpu".into(),
                label: "CPU".into(),
                is_bar_metric: true,
                unit: "%".into(),
            }],
        };
        let first = TaskbarProjection {
            presentation: TaskbarPresentation::BarHorizontal,
            gap: 4,
            docked: true,
            dragging: false,
            edge: TaskbarEdge::Bottom,
            connected: true,
            metrics: vec![metric.clone()],
            layout: layout.clone(),
        };
        let second = TaskbarProjection {
            metrics: vec![TaskbarMetricView {
                value: "21%".into(),
                ratio: 0.21,
                ..metric
            }],
            ..first.clone()
        };
        let mut render = TaskbarRenderState::default();
        assert_eq!(render.apply(&first), RenderUpdate::Rebuilt);
        assert_eq!(render.apply(&second), RenderUpdate::ValuesUpdated);
        assert_eq!(render.rebuild_count(), 1);

        let label_changed = TaskbarProjection {
            layout: TaskbarLayoutToken {
                columns: vec![TaskbarColumnToken {
                    label: "CPU Total".into(),
                    ..layout.columns[0].clone()
                }],
                ..layout
            },
            ..second
        };
        assert_eq!(render.apply(&label_changed), RenderUpdate::Rebuilt);
        assert_eq!(render.rebuild_count(), 2);
    }

    #[test]
    fn settings_normalization_clamps_only_allowed_values() {
        let settings = TaskbarSettings {
            opacity_percent: 0,
            dock_column_gap: 255,
            dock_cpu_label: " ".into(),
            ..TaskbarSettings::default()
        }
        .normalized();
        assert_eq!(settings.opacity_percent, 20);
        assert_eq!(settings.dock_column_gap, 24);
        assert_eq!(settings.dock_cpu_label, "CPU");
    }
}
