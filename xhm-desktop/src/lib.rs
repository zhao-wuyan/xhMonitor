//! `xhm-desktop` — xhMonitor 的 Slint 桌面壳（Rust 迁移 P2）。
//!
//! G1 范围：crate 装配、服务发现、SSE+REST 客户端、UI-neutral 状态结构与
//! 确定性测试。Win32 壳、主悬浮窗交互、任务栏、设置/关于/Toast 由后续
//! goal（G2-G4）交付，不在本阶段实现。
//!
//! 本 crate 是 G2-G4 的稳定装配点：`lib.rs` 是模块与生成 Slint component 的
//! 唯一根；`main.rs` 只调用 [`bootstrap`]，不承载客户端、Win32 或 UI 状态逻辑。

#![deny(rust_2018_idioms)]
slint::include_modules!();
#[cfg(windows)]
use slint::ComponentHandle;

pub mod config;
pub mod desktop_state;
#[cfg(windows)]
pub mod persistence;
pub mod service_client;
#[cfg(windows)]
pub mod shell;
#[cfg(windows)]
pub mod system_controls;
pub mod tray;
pub mod ui;
pub mod win32;

/// 进程事件 channel 深度（SSE 后台任务 → UI / 测试）。
pub const EVENT_CHANNEL_CAPACITY: usize = 256;

/// 双窗口启动入口：装配 Slint 软件渲染壳、两个唯一 title 的 Slint Window、
/// ShellController（HWND 解析、placement、topmost、click-through、persistence）
/// 与 tray 生命周期，最后运行事件循环。
///
/// 仅在 `SLINT_BACKEND` 未显式设置时把渲染后端固定为 `winit-software`，
/// 显式环境值仅用于诊断——不会作为自动 fallback 切换到其他 renderer
/// （locked_decisions：Slint backend 固定 winit-software）。
///
/// 顺序（current-plan TASK-008）：Rust mutex → winit-software → 两个 Slint
/// Window → 延迟 HWND → topmost/DPI/placement → 唯一 tray → Slint loop。
pub fn bootstrap() -> anyhow::Result<()> {
    ensure_software_backend();
    let filter = tracing_subscriber::EnvFilter::try_from_default_env()
        .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("info"));
    let _ = tracing_subscriber::fmt().with_env_filter(filter).try_init();
    tracing::info!(backend = %resolved_backend(), "xhm-desktop bootstrap");

    #[cfg(windows)]
    let single_instance_guard = acquire_single_instance()?;
    #[cfg(windows)]
    {
        match crate::system_controls::ensure_service_running() {
            Ok(true) => tracing::info!("xhm-service started by desktop"),
            Ok(false) => {}
            Err(error) => {
                tracing::warn!(%error, "desktop could not ensure xhm-service is running")
            }
        }
    }

    // Create all P2 windows before the active loop. Settings and About remain
    // hidden until tray commands request them.
    let floating = Shell::new()?;
    #[cfg(windows)]
    let taskbar = TaskbarWindow::new()?;
    #[cfg(windows)]
    let settings = SettingsWindow::new()?;
    #[cfg(windows)]
    let about = AboutWindow::new()?;
    #[cfg(not(windows))]
    let _taskbar = ();

    #[cfg(windows)]
    let controller_cell: std::rc::Rc<std::cell::RefCell<Option<shell::ShellController>>> =
        std::rc::Rc::new(std::cell::RefCell::new(None));
    #[cfg(windows)]
    let guard_cell = std::rc::Rc::new(std::cell::RefCell::new(Some(single_instance_guard)));
    #[cfg(windows)]
    let controller_timer = slint::Timer::default();
    #[cfg(windows)]
    let runtime_resources = std::rc::Rc::new(std::cell::RefCell::new(RuntimeResources::default()));
    #[cfg(windows)]
    let taskbar_settings = ui::taskbar_metrics::shared_settings();
    #[cfg(windows)]
    let taskbar_display = ui::taskbar_metrics::shared_display_state();
    #[cfg(windows)]
    let settings_weak = settings.as_weak();
    #[cfg(windows)]
    let about_weak = about.as_weak();
    #[cfg(windows)]
    {
        let settings_ui =
            ui::settings::install_runtime(&settings, std::sync::Arc::clone(&taskbar_settings));
        let about_ui = ui::about::install_runtime(&about);
        let mut resources = runtime_resources.borrow_mut();
        resources.settings_ui = Some(settings_ui);
        resources.about_ui = Some(about_ui);
        resources.settings_window = Some(settings);
        resources.about_window = Some(about);
    }

    #[cfg(windows)]
    {
        use slint::ComponentHandle;
        // HWND 解析使用 bounded retry：20x50ms = 最多 1s。
        // 超时后保持 mutex guard（在 guard_cell 中），但壳未接线——
        // 明确记录错误但不退出（让 tray exit 仍可用）。
        let weak_floating = floating.as_weak();
        let weak_taskbar = taskbar.as_weak();
        let controller_cell_clone = std::rc::Rc::clone(&controller_cell);
        let guard_cell_clone = std::rc::Rc::clone(&guard_cell);
        let runtime_resources_clone = std::rc::Rc::clone(&runtime_resources);
        let taskbar_settings_clone = std::sync::Arc::clone(&taskbar_settings);
        let taskbar_display_clone = std::sync::Arc::clone(&taskbar_display);
        let attempt = std::rc::Rc::new(std::cell::Cell::new(0u32));
        let attempt_clone = std::rc::Rc::clone(&attempt);
        const MAX_HWND_RETRIES: u32 = 20;
        const RETRY_INTERVAL_MS: u64 = 50;
        let wired = std::rc::Rc::new(std::cell::Cell::new(false));
        let wired_clone = std::rc::Rc::clone(&wired);
        controller_timer.start(
            slint::TimerMode::Repeated,
            std::time::Duration::from_millis(RETRY_INTERVAL_MS),
            move || {
                if wired_clone.get() {
                    return;
                }
                let n = attempt_clone.get();
                if n >= MAX_HWND_RETRIES {
                    tracing::error!(
                        attempts = n,
                        "HWND resolution timed out after {} retries; shell not wired (guard retained, tray exit available)",
                        MAX_HWND_RETRIES
                    );
                    wired_clone.set(true);
                    return;
                }
                attempt_clone.set(n + 1);
                match wire_dual_window(
                    &weak_floating,
                    &weak_taskbar,
                    &guard_cell_clone,
                    &runtime_resources_clone,
                    std::sync::Arc::clone(&taskbar_settings_clone),
                    std::sync::Arc::clone(&taskbar_display_clone),
                ) {
                    Ok(controller) => {
                        *controller_cell_clone.borrow_mut() = Some(controller);
                        wired_clone.set(true);
                        tracing::info!(attempts = n + 1, "dual-window wired after retries");
                    }
                    Err(error) => {
                        tracing::warn!(attempt = n + 1, %error, "HWND retry pending");
                    }
                }
            },
        );
        // 保持 timer + guard 活跃到 run() 结束——由 TrayRuntime 持有引用。
        // tray-exit 时 controller/guard/timer 有序 drop，并释放 mutex。

        // active_mode 初始 dispatch：G2 默认 Floating 模式，
        // taskbar 窗在 EdgeDock 模式才显示（当前固定 show 供 smoke 观察）。
        taskbar.show()?;
    }

    #[cfg(windows)]
    let _tray_runtime = install_tray_runtime(
        &floating,
        &settings_weak,
        &about_weak,
        &controller_cell,
        &guard_cell,
        controller_timer,
        &runtime_resources,
    )?;
    #[cfg(windows)]
    if std::env::var_os("XHM_DESKTOP_G4_SMOKE").is_some() {
        if let Some(settings) = settings_weak.upgrade() {
            settings.show()?;
        }
        if let Some(about) = about_weak.upgrade() {
            about.show()?;
        }
        tracing::info!("G4 smoke windows opened (taskbar + settings + about)");
    }
    floating.run()?;
    // _tray_runtime drop 在 run() 返回后：controller/guard/timers 有序释放。
    Ok(())
}

#[cfg(windows)]
fn acquire_single_instance() -> anyhow::Result<win32::SingleInstanceGuard> {
    let guard = win32::SingleInstanceGuard::acquire(win32::MUTEX_NAME)
        .map_err(|error| anyhow::anyhow!("single-instance acquisition failed: {error}"))?;
    tracing::info!(
        mutex = win32::normalize_mutex_name(win32::MUTEX_NAME),
        "single-instance acquired"
    );
    Ok(guard)
}

#[cfg(windows)]
fn wire_dual_window(
    floating: &slint::Weak<Shell>,
    taskbar: &slint::Weak<TaskbarWindow>,
    guard_cell: &std::rc::Rc<std::cell::RefCell<Option<win32::SingleInstanceGuard>>>,
    resources: &std::rc::Rc<std::cell::RefCell<RuntimeResources>>,
    taskbar_settings: ui::taskbar_metrics::SharedTaskbarSettings,
    taskbar_display: ui::taskbar_metrics::SharedTaskbarDisplay,
) -> anyhow::Result<shell::ShellController> {
    use crate::shell::{ShellController, FLOATING_TITLE, TASKBAR_TITLE};
    use crate::win32::dpi::native::NativeDpiQuery;
    use crate::win32::hwnd::native::NativeWindowEnumerator;
    use crate::win32::taskbar::native::{NativeTaskbarQuery, NativeWindowPositionOps};
    use crate::win32::taskbar::{TaskbarQuery, WindowPositionOps};
    use crate::win32::window::native::NativeWindowOps;
    use windows_sys::Win32::System::Threading::GetCurrentProcessId;

    let pid = unsafe { GetCurrentProcessId() };
    let enumerator = NativeWindowEnumerator;

    let Some(floating_handle) = win32::find_own_hwnd(&enumerator, FLOATING_TITLE, pid) else {
        tracing::error!(title = FLOATING_TITLE, "floating HWND not resolved");
        anyhow::bail!("floating window HWND not found after 300ms delay");
    };
    let Some(taskbar_handle) = win32::find_own_hwnd(&enumerator, TASKBAR_TITLE, pid) else {
        tracing::error!(title = TASKBAR_TITLE, "taskbar HWND not resolved");
        anyhow::bail!("taskbar window HWND not found after 300ms delay");
    };

    tracing::info!(
        pid,
        floating_hwnd = floating_handle.raw(),
        taskbar_hwnd = taskbar_handle.raw(),
        "dual-window HWNDs resolved"
    );

    // 只在两个 HWND 都成功解析后才 take guard，失败路径 guard 留在 cell 中。
    let guard = guard_cell
        .borrow_mut()
        .take()
        .ok_or_else(|| anyhow::anyhow!("single-instance guard already consumed"))?;
    let Some(mut controller) = ShellController::new(pid, &enumerator, guard) else {
        anyhow::bail!("ShellController construction failed (HWND resolution)");
    };

    // 验证两个 HWND 不同（core P1 requirement）。
    assert_ne!(
        controller.floating.handle.raw(),
        controller.taskbar.handle.raw(),
        "floating and taskbar HWNDs must be distinct"
    );

    // DPI 采样（真机 smoke 记录 physical/logical 换算基线）。
    let dpi_query = NativeDpiQuery;
    let floating_dpi = win32::dpi_for_window(Some(&dpi_query), controller.floating.handle);
    let taskbar_dpi = win32::dpi_for_window(Some(&dpi_query), controller.taskbar.handle);
    tracing::info!(floating_dpi, taskbar_dpi, "dual-window DPI sampled");

    // 四边 placement：taskbar 窗口定位到 Shell_TrayWnd 旁。
    let taskbar_query = NativeTaskbarQuery;
    let positioner = NativeWindowPositionOps;
    if let Some(placement) = controller.place_taskbar_window(&taskbar_query, &positioner) {
        {
            let mut display = taskbar_display
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            display.edge = placement.edge;
            display.docked = true;
        }
        tracing::info!(
            edge = ?placement.edge,
            origin_x = placement.origin.x,
            origin_y = placement.origin.y,
            "taskbar placement applied"
        );
        // 记录 placement 的物理 RECT 供 smoke 证据。
        if let Some(rect) = positioner.window_rect(controller.taskbar.handle) {
            tracing::info!(
                taskbar_rect = ?rect,
                "taskbar window physical RECT after placement"
            );
        }
        if let Some(app) = taskbar.upgrade() {
            app.set_status_text(
                format!(
                    "edge:{:?} {}/{}",
                    placement.edge, placement.origin.x, placement.origin.y
                )
                .into(),
            );
        }
    } else {
        tracing::warn!("taskbar placement returned None (no Shell_TrayWnd?)");
    }

    // Topmost reassert（两个窗口都置顶）。
    let topmost_ops = NativeWindowOps;
    controller.reassert_topmost(&topmost_ops);
    tracing::info!("topmost reasserted on both windows");

    // 位置持久化：加载上次位置并 clamp 到当前 virtual screen。
    if let Ok(store) = persistence::WindowPositionStore::from_environment() {
        if let Some(taskbar_snap) = taskbar_query.snapshot() {
            if let Some(saved) = store.load(taskbar_snap.virtual_screen) {
                let positioner = NativeWindowPositionOps;
                if positioner.move_topmost(controller.floating.handle, saved.left, saved.top) {
                    tracing::info!(
                        saved_x = saved.left,
                        saved_y = saved.top,
                        "floating window restored from persistence"
                    );
                }
            }
        }
    }

    // G3 UI state and callback bridge are installed on the Slint thread. The
    // async SSE worker only reduces shared state and dispatches immutable projections.
    let smoke_mode = std::env::var_os("XHM_DESKTOP_UI_SMOKE").is_some();
    if smoke_mode {
        if let Some(child) = spawn_smoke_process() {
            let smoke_pid = child.id() as i32;
            let smoke_state = ui::floating_window::smoke_state(smoke_pid);
            *controller
                .floating
                .state
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner) = smoke_state.clone();
            *controller
                .taskbar
                .state
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner) = smoke_state;
            tracing::info!(smoke_pid, "G3 UI smoke fixture loaded");
            resources.borrow_mut().smoke_process = Some(child);
        } else {
            tracing::error!("G3 UI smoke process failed to start");
        }
    }

    let initial_subscription = {
        let state = controller
            .floating
            .state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        service_client::SseSubscription::new(
            state.panel.subscription_mode(),
            state.normalized_pinned(),
        )
    };
    let (floating_subscription_tx, mut floating_subscription_rx) =
        tokio::sync::mpsc::unbounded_channel();
    if let Some(app) = floating.upgrade() {
        let projection = {
            let state = controller
                .floating
                .state
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            ui::floating_window::project_state(&state)
        };
        ui::floating_window::apply_projection(&app, projection);
        resources.borrow_mut().floating_ui = Some(ui::floating_window::install_runtime(
            &app,
            controller.floating.handle,
            std::sync::Arc::clone(&controller.floating.state),
            Some(floating_subscription_tx),
        ));
    }
    if let Some(app) = taskbar.upgrade() {
        let projection = {
            let state = controller
                .taskbar
                .state
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let settings = taskbar_settings
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let display = taskbar_display
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            ui::taskbar_metrics::project_state(&state, &settings, *display)
        };
        ui::taskbar_metrics::apply_projection(&app, projection);
        resources.borrow_mut().taskbar_ui = Some(ui::taskbar_metrics::install_runtime(
            &app,
            controller.taskbar.handle,
            std::sync::Arc::clone(&controller.taskbar.state),
            std::sync::Arc::clone(&taskbar_settings),
            std::sync::Arc::clone(&taskbar_display),
            controller.taskbar.cancel.clone(),
        ));
    }

    // 双 SSE 生产连接：floating 和 taskbar 各自 SseStreamBuilder::spawn，
    // 独立 cancel/control。专用线程持有 runtime，controller shutdown 后有序退出。
    let floating_cancel_token = controller.floating.cancel.clone();
    let taskbar_cancel_token = controller.taskbar.cancel.clone();
    let floating_state = std::sync::Arc::clone(&controller.floating.state);
    let taskbar_state = std::sync::Arc::clone(&controller.taskbar.state);
    let floating_ui_weak = floating.clone();
    let taskbar_ui_weak = taskbar.clone();
    let taskbar_settings_for_sse = std::sync::Arc::clone(&taskbar_settings);
    let taskbar_display_for_sse = std::sync::Arc::clone(&taskbar_display);
    match std::thread::Builder::new()
        .name("xhm-desktop-sse".into())
        .spawn(move || {
            let runtime = tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build();
            let Ok(runtime) = runtime else {
                tracing::error!("failed to create tokio runtime for dual SSE");
                return;
            };

            let config = runtime.block_on(config::Config::load());
            let api_base = config.api_base.clone();
            tracing::info!(api_base = %api_base, "dual SSE: config loaded");

            let floating_builder = match service_client::SseStreamBuilder::new(&api_base) {
                Ok(builder) => builder
                    .mode(initial_subscription.mode)
                    .pinned(initial_subscription.pinned.clone()),
                Err(error) => {
                    tracing::error!(%error, "floating SSE builder failed");
                    return;
                }
            };
            let taskbar_builder = match service_client::SseStreamBuilder::new(&api_base) {
                Ok(builder) => builder,
                Err(error) => {
                    tracing::error!(%error, "taskbar SSE builder failed");
                    return;
                }
            };

            runtime.block_on(async move {
                use service_client::SseMessage;

                let http = reqwest::Client::new();
                let (mut floating_rx, floating_ctrl) = floating_builder.build().spawn(http.clone());
                let (mut taskbar_rx, taskbar_ctrl) = taskbar_builder.build().spawn(http);

                let floating_receiver = tokio::spawn(async move {
                    while let Some(message) = floating_rx.recv().await {
                        if !smoke_mode {
                            let projection = {
                                let mut state = floating_state
                                    .lock()
                                    .unwrap_or_else(std::sync::PoisonError::into_inner);
                                state.apply_message(&message);
                                ui::floating_window::project_state(&state)
                            };
                            if let Err(error) =
                                ui::floating_window::dispatch_projection(&floating_ui_weak, projection)
                            {
                                tracing::warn!(%error, "floating UI dispatch failed");
                            }
                        }
                        match message {
                            SseMessage::Connected => tracing::info!("floating SSE: connected"),
                            SseMessage::Disconnected => {
                                tracing::info!("floating SSE: disconnected")
                            }
                            _ => {}
                        }
                    }
                    tracing::info!("floating SSE: receiver closed");
                });
                let taskbar_receiver = tokio::spawn(async move {
                    while let Some(message) = taskbar_rx.recv().await {
                        if !smoke_mode {
                            let projection = {
                                let mut state = taskbar_state
                                    .lock()
                                    .unwrap_or_else(std::sync::PoisonError::into_inner);
                                state.apply_message(&message);
                                let settings = taskbar_settings_for_sse
                                    .lock()
                                    .unwrap_or_else(std::sync::PoisonError::into_inner)
                                    .clone();
                                let display = *taskbar_display_for_sse
                                    .lock()
                                    .unwrap_or_else(std::sync::PoisonError::into_inner);
                                ui::taskbar_metrics::project_state(&state, &settings, display)
                            };
                            if let Err(error) =
                                ui::taskbar_metrics::dispatch_projection(&taskbar_ui_weak, projection)
                            {
                                tracing::warn!(%error, "taskbar UI dispatch failed");
                            }
                        }
                        match message {
                            SseMessage::Connected => tracing::info!("taskbar SSE: connected"),
                            SseMessage::Disconnected => {
                                tracing::info!("taskbar SSE: disconnected")
                            }
                            _ => {}
                        }
                    }
                    tracing::info!("taskbar SSE: receiver closed");
                });

                tracing::info!("dual SSE streams spawned (floating + taskbar independent)");
                loop {
                    tokio::select! {
                        _ = floating_cancel_token.cancelled() => break,
                        _ = taskbar_cancel_token.cancelled() => break,
                        subscription = floating_subscription_rx.recv() => {
                            let Some(subscription) = subscription else {
                                break;
                            };
                            if floating_ctrl.resubscribe(subscription.mode, subscription.pinned) {
                                tracing::info!("floating SSE subscription updated from panel state");
                            }
                        }
                    }
                }
                floating_ctrl.cancel();
                taskbar_ctrl.cancel();
                floating_receiver.abort();
                taskbar_receiver.abort();
                tracing::info!("dual SSE runtime stopped");
            });
        }) {
        Ok(thread) => resources.borrow_mut().sse_thread = Some(thread),
        Err(error) => tracing::error!(%error, "failed to spawn dual SSE thread"),
    }

    // 周期性位置保存（5s 间隔），由 TrayRuntime 持有并在退出时 drop。
    let weak_floating = floating.clone();
    let save_timer = slint::Timer::default();
    let floating_handle_save = controller.floating.handle;
    save_timer.start(
        slint::TimerMode::Repeated,
        std::time::Duration::from_secs(5),
        move || {
            let positioner = NativeWindowPositionOps;
            if let Some(rect) = positioner.window_rect(floating_handle_save) {
                if let Ok(store) = persistence::WindowPositionStore::from_environment() {
                    match store.save(rect) {
                        Ok(()) => {
                            tracing::trace!(
                                x = rect.left,
                                y = rect.top,
                                "floating position auto-saved"
                            );
                        }
                        Err(error) => {
                            tracing::warn!(%error, "floating position auto-save failed");
                        }
                    }
                }
            }
            let _ = &weak_floating;
        },
    );
    resources.borrow_mut().save_timer = Some(save_timer);

    // Click-through 默认关闭（floating 窗可交互）。
    let click_ops = NativeWindowOps;
    controller.click_through_enabled = true; // toggle 会翻转回 false
    controller.toggle_click_through(&click_ops);
    tracing::info!(
        enabled = controller.click_through_enabled,
        "click-through initialized"
    );

    // 更新 floating 窗状态文案。
    if let Some(app) = floating.upgrade() {
        app.set_status_text(
            format!(
                "HWND {}/{} pid {}",
                floating_handle.raw(),
                taskbar_handle.raw(),
                pid
            )
            .into(),
        );
    }

    tracing::info!("dual-window shell controller wired");
    Ok(controller)
}

#[cfg(windows)]
fn spawn_smoke_process() -> Option<std::process::Child> {
    std::process::Command::new("cmd.exe")
        .args(["/D", "/C", "ping", "-n", "120", "127.0.0.1"])
        .stdin(std::process::Stdio::null())
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .spawn()
        .ok()
}

#[cfg(windows)]
#[derive(Default)]
struct RuntimeResources {
    save_timer: Option<slint::Timer>,
    sse_thread: Option<std::thread::JoinHandle<()>>,
    floating_ui: Option<ui::floating_window::FloatingUiRuntime>,
    taskbar_ui: Option<ui::taskbar_metrics::TaskbarUiRuntime>,
    settings_ui: Option<ui::settings::SettingsUiRuntime>,
    about_ui: Option<ui::about::AboutUiRuntime>,
    settings_window: Option<SettingsWindow>,
    about_window: Option<AboutWindow>,
    smoke_process: Option<std::process::Child>,
}

#[cfg(windows)]
struct TrayRuntime {
    _timer: slint::Timer,
    _tray: std::rc::Rc<std::cell::RefCell<tray::TrayHandle>>,
    _controller_cell: std::rc::Rc<std::cell::RefCell<Option<shell::ShellController>>>,
    _guard_cell: std::rc::Rc<std::cell::RefCell<Option<win32::SingleInstanceGuard>>>,
    _wire_timer: slint::Timer,
    _resources: std::rc::Rc<std::cell::RefCell<RuntimeResources>>,
}

#[cfg(windows)]
impl Drop for TrayRuntime {
    fn drop(&mut self) {
        if let Some(controller) = self._controller_cell.borrow_mut().as_mut() {
            controller.shutdown();
        }
        self._resources.borrow_mut().save_timer.take();
        self._resources.borrow_mut().floating_ui.take();
        self._resources.borrow_mut().taskbar_ui.take();
        self._resources.borrow_mut().settings_ui.take();
        self._resources.borrow_mut().about_ui.take();
        self._resources.borrow_mut().settings_window.take();
        self._resources.borrow_mut().about_window.take();
        let sse_thread = self._resources.borrow_mut().sse_thread.take();
        if let Some(thread) = sse_thread {
            if thread.join().is_err() {
                tracing::error!("dual SSE thread panicked during shutdown");
            }
        }
        if let Some(mut child) = self._resources.borrow_mut().smoke_process.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}

#[cfg(windows)]
fn install_tray_runtime(
    app: &Shell,
    settings: &slint::Weak<SettingsWindow>,
    about: &slint::Weak<AboutWindow>,
    controller_cell: &std::rc::Rc<std::cell::RefCell<Option<shell::ShellController>>>,
    guard_cell: &std::rc::Rc<std::cell::RefCell<Option<win32::SingleInstanceGuard>>>,
    wire_timer: slint::Timer,
    resources: &std::rc::Rc<std::cell::RefCell<RuntimeResources>>,
) -> anyhow::Result<TrayRuntime> {
    use crate::win32::taskbar::native::NativeWindowPositionOps;
    use crate::win32::taskbar::WindowPositionOps;
    use crate::win32::window::native::NativeWindowOps;

    let (sender, receiver) = tray::channel();
    let icon_path = tray_icon_path()?;
    let tray = std::rc::Rc::new(std::cell::RefCell::new(
        tray::build_tray("XhMonitor", &icon_path, sender)
            .map_err(|error| anyhow::anyhow!("tray fallback initialization failed: {error}"))?,
    ));

    let timer = slint::Timer::default();
    let weak = app.as_weak();
    let callback_tray = std::rc::Rc::clone(&tray);
    let controller_cell_clone = std::rc::Rc::clone(controller_cell);
    let settings_weak = settings.clone();
    let about_weak = about.clone();
    let primary_visible = std::rc::Rc::new(std::cell::Cell::new(true));
    let matrix_mode = std::env::var_os("XHM_DESKTOP_TRAY_MATRIX").is_some();
    let mut tick = 0_u64;
    let mut notification_sent = false;
    let mut last_command = String::from("tray-ready");
    let primary_visible_callback = std::rc::Rc::clone(&primary_visible);
    timer.start(
        slint::TimerMode::Repeated,
        std::time::Duration::from_millis(100),
        move || {
            tick += 1;
            if matrix_mode && !notification_sent && tick >= 10 {
                notification_sent = true;
                match callback_tray.borrow().show_notification(
                    "XhMonitor tray spike",
                    "Notification click opens About in the active Slint loop.",
                ) {
                    Ok(()) => tracing::info!("TRAY_MATRIX notification-requested"),
                    Err(error) => tracing::error!(%error, "TRAY_MATRIX notification-failed"),
                }
            }

            while let Ok(command) = receiver.try_recv() {
                last_command = command.id().to_owned();
                match command {
                    tray::TrayCommand::ShowHide => {
                        let next_visible = !primary_visible_callback.get();
                        primary_visible_callback.set(next_visible);
                        if let Some(app) = weak.upgrade() {
                            if next_visible {
                                let _ = app.show();
                            } else {
                                let _ = app.hide();
                            }
                        }
                        if let Some(controller) = controller_cell_clone.borrow_mut().as_mut() {
                            let topmost_ops = NativeWindowOps;
                            if next_visible {
                                controller.reassert_topmost(&topmost_ops);
                                tracing::info!("TRAY_MATRIX show: Slint show + topmost reasserted");
                            } else {
                                let positioner = NativeWindowPositionOps;
                                if let Some(rect) =
                                    positioner.window_rect(controller.floating.handle)
                                {
                                    if let Ok(store) =
                                        persistence::WindowPositionStore::from_environment()
                                    {
                                        let _ = store.save(rect);
                                        tracing::info!(
                                            saved_x = rect.left,
                                            saved_y = rect.top,
                                            "TRAY_MATRIX hide: Slint hide + position saved"
                                        );
                                    }
                                }
                            }
                        }
                        tracing::info!(
                            visible = next_visible,
                            "TRAY_MATRIX show-hide-applied (real Slint show/hide)"
                        );
                    }
                    tray::TrayCommand::ClickThrough => {
                        let checked = !callback_tray.borrow().is_click_through_checked();
                        match callback_tray.borrow().set_click_through_checked(checked) {
                            Ok(()) => {
                                // 调用真实 Win32 click-through toggle via ShellController。
                                if let Some(controller) =
                                    controller_cell_clone.borrow_mut().as_mut()
                                {
                                    let click_ops = NativeWindowOps;
                                    controller.toggle_click_through(&click_ops);
                                    tracing::info!(
                                        enabled = controller.click_through_enabled,
                                        "TRAY_MATRIX click-through toggled via Win32"
                                    );
                                }
                                tracing::info!(checked, "TRAY_MATRIX checked-state-applied")
                            }
                            Err(error) => {
                                tracing::error!(%error, "TRAY_MATRIX checked-state-failed")
                            }
                        }
                    }
                    tray::TrayCommand::Exit => {
                        tracing::info!(tick, "TRAY_MATRIX exit-applied");
                        // 保存位置 + shutdown controller 后退出。
                        if let Some(controller) = controller_cell_clone.borrow_mut().as_mut() {
                            let positioner = NativeWindowPositionOps;
                            if let Some(rect) = positioner.window_rect(controller.floating.handle) {
                                if let Ok(store) =
                                    persistence::WindowPositionStore::from_environment()
                                {
                                    let _ = store.save(rect);
                                    tracing::info!("TRAY_MATRIX exit: position saved");
                                }
                            }
                            controller.shutdown();
                        }
                        if let Err(error) = slint::quit_event_loop() {
                            tracing::error!(%error, "TRAY_MATRIX exit-failed");
                        }
                        return;
                    }
                    tray::TrayCommand::OpenWeb => match open_web_dashboard() {
                        Ok(()) => tracing::info!(
                            endpoint = "http://127.0.0.1:35180",
                            "tray Web command opened existing dashboard"
                        ),
                        Err(error) => tracing::error!(%error, "tray Web command failed"),
                    },
                    tray::TrayCommand::AdminMode => {
                        #[cfg(windows)]
                        {
                            let enabled = !crate::system_controls::is_admin_mode_enabled();
                            std::thread::spawn(move || {
                                match crate::system_controls::apply_admin_mode(enabled) {
                                    Ok(()) => tracing::info!(enabled, "tray Admin Mode applied"),
                                    Err(error) => {
                                        tracing::error!(%error, enabled, "tray Admin Mode failed")
                                    }
                                }
                            });
                        }
                        #[cfg(not(windows))]
                        {
                            tracing::info!("tray Admin Mode is Windows-only");
                        }
                    }
                    tray::TrayCommand::Settings => {
                        if let Some(settings) = settings_weak.upgrade() {
                            let _ = settings.show();
                        }
                        tracing::info!("tray Settings window opened");
                    }
                    tray::TrayCommand::About => {
                        if let Some(about) = about_weak.upgrade() {
                            let _ = about.show();
                        }
                        tracing::info!("tray About window opened");
                    }
                }
            }

            if tick % 10 == 0 {
                tracing::info!(tick, "TRAY_MATRIX active-loop");
            }
            if let Some(app) = weak.upgrade() {
                app.set_status_text(format!("{last_command} #{tick}").into());
            }
        },
    );

    Ok(TrayRuntime {
        _timer: timer,
        _tray: tray,
        _controller_cell: std::rc::Rc::clone(controller_cell),
        _guard_cell: std::rc::Rc::clone(guard_cell),
        _wire_timer: wire_timer,
        _resources: std::rc::Rc::clone(resources),
    })
}

#[cfg(windows)]
fn open_web_dashboard() -> anyhow::Result<()> {
    use windows_sys::Win32::UI::Shell::ShellExecuteW;
    use windows_sys::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

    let operation = "open\0".encode_utf16().collect::<Vec<_>>();
    let target = "http://127.0.0.1:35180\0"
        .encode_utf16()
        .collect::<Vec<_>>();
    let result = unsafe {
        ShellExecuteW(
            std::ptr::null_mut(),
            operation.as_ptr(),
            target.as_ptr(),
            std::ptr::null(),
            std::ptr::null(),
            SW_SHOWNORMAL,
        )
    };
    if result as isize > 32 {
        Ok(())
    } else {
        anyhow::bail!("ShellExecuteW returned {}", result as isize)
    }
}

#[cfg(windows)]
fn tray_icon_path() -> anyhow::Result<std::path::PathBuf> {
    let installed = std::env::current_exe()
        .ok()
        .and_then(|path| path.parent().map(|dir| dir.join("Assets").join("icon.ico")));
    let source = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("XhMonitor.Desktop")
        .join("Assets")
        .join("icon.ico");
    installed
        .filter(|path| path.is_file())
        .or_else(|| source.is_file().then_some(source))
        .ok_or_else(|| anyhow::anyhow!("XhMonitor tray icon asset not found"))
}

/// 若调用方未显式设置 `SLINT_BACKEND`，则固定为软件渲染。
///
/// Slint 通过 `SLINT_BACKEND` 选择 backend+renderer 组合；`winit-software`
/// 表示 winit 窗口后端 + 软件光栅化 renderer（POC 已验证的路径）。
fn ensure_software_backend() {
    if std::env::var_os("SLINT_BACKEND").is_none() {
        // SAFETY: 进程启动早期、单线程下设置环境变量；后续所有 Slint 初始化读取此值。
        // 不把已显式设置的值覆盖——显式值仅用于诊断。
        std::env::set_var("SLINT_BACKEND", "winit-software");
    }
}

/// 返回当前生效（或即将生效）的 Slint backend 名。
fn resolved_backend() -> String {
    std::env::var("SLINT_BACKEND").unwrap_or_else(|_| "winit-software".to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    use std::ffi::OsString;
    use std::sync::{LazyLock, Mutex, MutexGuard};

    static BACKEND_ENV_LOCK: LazyLock<Mutex<()>> = LazyLock::new(|| Mutex::new(()));

    struct BackendEnvGuard {
        original: Option<OsString>,
    }

    impl BackendEnvGuard {
        fn lock() -> (MutexGuard<'static, ()>, Self) {
            let lock = BACKEND_ENV_LOCK
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            let guard = Self {
                original: std::env::var_os("SLINT_BACKEND"),
            };
            (lock, guard)
        }
    }

    impl Drop for BackendEnvGuard {
        fn drop(&mut self) {
            match self.original.take() {
                Some(value) => std::env::set_var("SLINT_BACKEND", value),
                None => std::env::remove_var("SLINT_BACKEND"),
            }
        }
    }

    #[test]
    fn resolved_backend_defaults_to_winit_software() {
        let (_lock, _restore) = BackendEnvGuard::lock();
        // 未显式设置时回到默认软件渲染（不依赖 ensure_software_backend 已运行）。
        std::env::remove_var("SLINT_BACKEND");
        assert_eq!(resolved_backend(), "winit-software");
    }

    #[test]
    fn resolved_backend_respects_explicit_value() {
        let (_lock, _restore) = BackendEnvGuard::lock();
        std::env::set_var("SLINT_BACKEND", "winit-skia");
        assert_eq!(resolved_backend(), "winit-skia");
    }

    #[test]
    fn ensure_software_backend_sets_default_only_when_unset() {
        let (_lock, _restore) = BackendEnvGuard::lock();
        std::env::remove_var("SLINT_BACKEND");
        ensure_software_backend();
        assert_eq!(std::env::var("SLINT_BACKEND").unwrap(), "winit-software");

        // 已显式设置时不覆盖。
        std::env::set_var("SLINT_BACKEND", "winit-femtovg");
        ensure_software_backend();
        assert_eq!(std::env::var("SLINT_BACKEND").unwrap(), "winit-femtovg");
    }
}
