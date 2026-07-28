//! Supervised `lhm-bridge` subprocess and synchronous sensor reader.

use std::{
    ffi::OsString,
    fmt, fs, io,
    path::{Path, PathBuf},
    process::{ExitStatus, Stdio},
    sync::{Arc, RwLock},
    time::Duration,
};

use tokio::{
    io::{AsyncBufReadExt, BufReader},
    process::{Child, ChildStderr, ChildStdout, Command},
    sync::watch,
    task::JoinHandle,
    time,
};
use xhm_core::{
    models::{LhmBridgeBanner, LhmSnapshot},
    traits::LhmReader,
    CoreError, Result,
};

const BRIDGE_COMPONENT: &str = "lhm-bridge";
const MAX_RESTARTS: usize = 5;
const INITIAL_RESTART_DELAY: Duration = Duration::from_secs(1);
const GRACEFUL_SHUTDOWN_TIMEOUT: Duration = Duration::from_millis(500);

#[derive(Debug, Default)]
struct ReaderView {
    snapshot: Option<LhmSnapshot>,
    sensor_elevated: bool,
    running: bool,
}

#[derive(Debug, Default)]
struct ReaderState {
    view: RwLock<ReaderView>,
}

impl ReaderState {
    fn child_started(&self) {
        let mut view = self
            .view
            .write()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        *view = ReaderView {
            running: true,
            ..ReaderView::default()
        };
    }

    fn child_stopped(&self) {
        let mut view = self
            .view
            .write()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        *view = ReaderView::default();
    }

    fn set_sensor_elevated(&self, elevated: bool) {
        let mut view = self
            .view
            .write()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if view.running {
            view.sensor_elevated = elevated;
        }
    }

    fn set_snapshot(&self, snapshot: LhmSnapshot) {
        let mut view = self
            .view
            .write()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if view.running {
            view.snapshot = Some(snapshot);
        }
    }

    fn snapshot(&self) -> Option<LhmSnapshot> {
        let view = self
            .view
            .read()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if view.running {
            view.snapshot.clone()
        } else {
            None
        }
    }

    fn is_sensor_elevated(&self) -> bool {
        let view = self
            .view
            .read()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        view.running && view.sensor_elevated
    }
}

#[derive(Debug)]
struct ManagedLhmReader {
    state: Arc<ReaderState>,
}

impl LhmReader for ManagedLhmReader {
    fn snapshot(&self) -> Option<LhmSnapshot> {
        self.state.snapshot()
    }

    fn is_sensor_elevated(&self) -> bool {
        self.state.is_sensor_elevated()
    }
}

/// Owns the supervisor task for one `lhm-bridge` reader.
///
/// Keep this handle alive for as long as its returned reader is in use. Dropping
/// it requests asynchronous cleanup; call [`Self::shutdown`] when the caller can
/// await deterministic child termination.
pub struct LhmBridgeManager {
    shutdown_tx: watch::Sender<bool>,
    task: Option<JoinHandle<()>>,
}

impl fmt::Debug for LhmBridgeManager {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let task_finished = self
            .task
            .as_ref()
            .map(JoinHandle::is_finished)
            .unwrap_or(true);

        formatter
            .debug_struct("LhmBridgeManager")
            .field("shutdown_requested", &*self.shutdown_tx.borrow())
            .field("task_finished", &task_finished)
            .finish()
    }
}

impl LhmBridgeManager {
    /// Starts a supervised bridge from the supplied absolute executable path.
    ///
    /// The returned trait object can be placed directly in `AppState`; the
    /// manager is the independent lifecycle handle that must be retained by
    /// `main`.
    pub fn start(bridge_path: impl AsRef<Path>) -> Result<(Arc<dyn LhmReader>, LhmBridgeManager)> {
        Self::start_with_options(
            LaunchSpec::production(bridge_path.as_ref().to_path_buf()),
            SupervisorConfig::production(),
        )
    }

    fn start_with_options(
        launch: LaunchSpec,
        config: SupervisorConfig,
    ) -> Result<(Arc<dyn LhmReader>, LhmBridgeManager)> {
        validate_bridge_path(&launch.executable)?;
        let runtime = tokio::runtime::Handle::try_current().map_err(|error| {
            CoreError::LhmBridge(format!(
                "a Tokio runtime is required to start {}: {error}",
                launch.executable.display()
            ))
        })?;

        let state = Arc::new(ReaderState::default());
        let reader: Arc<dyn LhmReader> = Arc::new(ManagedLhmReader {
            state: Arc::clone(&state),
        });
        let (shutdown_tx, shutdown_rx) = watch::channel(false);
        let task = runtime.spawn(supervise(launch, config, state, shutdown_rx));

        Ok((
            reader,
            LhmBridgeManager {
                shutdown_tx,
                task: Some(task),
            },
        ))
    }

    /// Requests shutdown and waits until the supervisor has reaped its child.
    ///
    /// Calling this more than once is harmless.
    pub async fn shutdown(&mut self) {
        let _ = self.shutdown_tx.send(true);
        let Some(task) = self.task.take() else {
            return;
        };

        if let Err(error) = task.await {
            tracing::error!(%error, "lhm-bridge supervisor task failed during shutdown");
        }
    }

    /// Reports whether shutdown was requested or the supervisor already ended.
    pub fn is_shutdown(&self) -> bool {
        if *self.shutdown_tx.borrow() {
            return true;
        }

        self.task
            .as_ref()
            .map(JoinHandle::is_finished)
            .unwrap_or(true)
    }
}

impl Drop for LhmBridgeManager {
    fn drop(&mut self) {
        let _ = self.shutdown_tx.send(true);
        // Dropping JoinHandle detaches instead of aborting. The supervisor can
        // therefore finish its 500 ms graceful-stop path; kill_on_drop remains
        // the final guard if the Tokio runtime itself is torn down.
    }
}

#[derive(Debug, Clone)]
struct LaunchSpec {
    executable: PathBuf,
    arguments: Vec<OsString>,
    environment: Vec<(OsString, OsString)>,
}

impl LaunchSpec {
    fn production(executable: PathBuf) -> Self {
        Self {
            executable,
            arguments: Vec::new(),
            environment: vec![
                (
                    OsString::from("DOTNET_GCConserveMemory"),
                    OsString::from("9"),
                ),
                (OsString::from("DOTNET_gcConcurrent"), OsString::from("0")),
                (OsString::from("DOTNET_GCRetainVM"), OsString::from("0")),
            ],
        }
    }
}

#[derive(Debug, Clone, Copy)]
struct SupervisorConfig {
    max_restarts: usize,
    initial_restart_delay: Duration,
}

impl SupervisorConfig {
    const fn production() -> Self {
        Self {
            max_restarts: MAX_RESTARTS,
            initial_restart_delay: INITIAL_RESTART_DELAY,
        }
    }
}

#[derive(Debug)]
struct ExponentialBackoff {
    initial: Duration,
    max_retries: usize,
    retries_used: usize,
}

impl ExponentialBackoff {
    fn new(initial: Duration, max_retries: usize) -> Self {
        Self {
            initial,
            max_retries,
            retries_used: 0,
        }
    }

    fn next_delay(&mut self) -> Option<Duration> {
        if self.retries_used >= self.max_retries {
            return None;
        }

        let factor = 1_u32
            .checked_shl(self.retries_used.try_into().unwrap_or(u32::MAX))
            .unwrap_or(u32::MAX);
        self.retries_used += 1;
        Some(self.initial.checked_mul(factor).unwrap_or(Duration::MAX))
    }

    fn retries_used(&self) -> usize {
        self.retries_used
    }
}

#[derive(Debug)]
struct SpawnedBridge {
    child: Child,
    stdout: ChildStdout,
    stderr: ChildStderr,
}

#[derive(Debug)]
enum ChildOutcome {
    Shutdown,
    Exited(io::Result<ExitStatus>),
    StdoutClosed(io::Result<()>),
}

fn validate_bridge_path(path: &Path) -> Result<()> {
    if !path.is_absolute() {
        return Err(CoreError::invalid(format!(
            "lhm-bridge path must be absolute: {}",
            path.display()
        )));
    }

    let metadata = fs::metadata(path)?;
    if !metadata.is_file() {
        return Err(CoreError::LhmBridge(format!(
            "lhm-bridge path is not a file: {}",
            path.display()
        )));
    }

    Ok(())
}

async fn supervise(
    launch: LaunchSpec,
    config: SupervisorConfig,
    state: Arc<ReaderState>,
    mut shutdown_rx: watch::Receiver<bool>,
) {
    let mut backoff = ExponentialBackoff::new(config.initial_restart_delay, config.max_restarts);

    loop {
        if shutdown_requested(&shutdown_rx) {
            break;
        }

        tracing::info!(
            path = %launch.executable.display(),
            "starting lhm-bridge child"
        );

        match spawn_bridge(&launch) {
            Ok(bridge) => {
                let should_restart =
                    monitor_bridge(bridge, Arc::clone(&state), &mut shutdown_rx).await;
                if !should_restart {
                    break;
                }
            }
            Err(error) => {
                state.child_stopped();
                tracing::error!(
                    path = %launch.executable.display(),
                    %error,
                    "failed to start lhm-bridge child"
                );
            }
        }

        let Some(delay) = backoff.next_delay() else {
            tracing::error!(
                path = %launch.executable.display(),
                max_restarts = config.max_restarts,
                "lhm-bridge restart budget exhausted"
            );
            break;
        };

        tracing::warn!(
            path = %launch.executable.display(),
            retry = backoff.retries_used(),
            max_retries = config.max_restarts,
            ?delay,
            "lhm-bridge stopped unexpectedly; scheduling restart"
        );

        if !wait_for_retry(delay, &mut shutdown_rx).await {
            break;
        }
    }

    state.child_stopped();
}

fn spawn_bridge(launch: &LaunchSpec) -> io::Result<SpawnedBridge> {
    let mut command = Command::new(&launch.executable);
    command
        .args(&launch.arguments)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true);

    for (key, value) in &launch.environment {
        command.env(key, value);
    }

    configure_child_process(&mut command);

    let mut child = command.spawn()?;
    let stdout = child
        .stdout
        .take()
        .expect("stdout is piped before lhm-bridge is spawned");
    let stderr = child
        .stderr
        .take()
        .expect("stderr is piped before lhm-bridge is spawned");

    Ok(SpawnedBridge {
        child,
        stdout,
        stderr,
    })
}

async fn monitor_bridge(
    mut bridge: SpawnedBridge,
    state: Arc<ReaderState>,
    shutdown_rx: &mut watch::Receiver<bool>,
) -> bool {
    let pid = bridge.child.id();
    state.child_started();
    tracing::info!(?pid, "lhm-bridge child started");

    let stderr_state = Arc::clone(&state);
    let stderr_task = tokio::spawn(async move { pump_stderr(bridge.stderr, stderr_state).await });
    let stdout_pump = pump_stdout(bridge.stdout, Arc::clone(&state));
    tokio::pin!(stdout_pump);

    let (outcome, stdout_completed) = loop {
        tokio::select! {
            biased;
            changed = shutdown_rx.changed() => {
                if changed.is_err() || shutdown_requested(shutdown_rx) {
                    break (ChildOutcome::Shutdown, false);
                }
            }
            result = &mut stdout_pump => {
                break (ChildOutcome::StdoutClosed(result), true);
            }
            status = bridge.child.wait() => {
                break (ChildOutcome::Exited(status), false);
            }
        }
    };

    // Make the trait view unavailable as soon as child liveness is lost. Any
    // buffered final line drained below is ignored while running=false.
    state.child_stopped();

    let should_restart = match outcome {
        ChildOutcome::Shutdown => {
            stop_child(&mut bridge.child).await;
            false
        }
        ChildOutcome::Exited(Ok(status)) => {
            tracing::warn!(?pid, %status, "lhm-bridge child exited unexpectedly");
            true
        }
        ChildOutcome::Exited(Err(error)) => {
            tracing::error!(?pid, %error, "failed while waiting for lhm-bridge child");
            stop_child(&mut bridge.child).await;
            true
        }
        ChildOutcome::StdoutClosed(Ok(())) => {
            tracing::warn!(?pid, "lhm-bridge stdout closed unexpectedly");
            stop_child(&mut bridge.child).await;
            true
        }
        ChildOutcome::StdoutClosed(Err(error)) => {
            tracing::error!(?pid, %error, "failed reading lhm-bridge stdout");
            stop_child(&mut bridge.child).await;
            true
        }
    };

    if !stdout_completed {
        if let Err(error) = stdout_pump.await {
            tracing::warn!(?pid, %error, "failed draining lhm-bridge stdout");
        }
    }

    match stderr_task.await {
        Ok(Ok(())) => {}
        Ok(Err(error)) => {
            tracing::warn!(?pid, %error, "failed reading lhm-bridge stderr");
        }
        Err(error) => {
            tracing::error!(?pid, %error, "lhm-bridge stderr task failed");
        }
    }

    should_restart
}

async fn pump_stdout(stdout: ChildStdout, state: Arc<ReaderState>) -> io::Result<()> {
    let mut lines = BufReader::new(stdout).lines();
    while let Some(line) = lines.next_line().await? {
        match serde_json::from_str::<LhmSnapshot>(&line) {
            Ok(snapshot) => state.set_snapshot(snapshot),
            Err(error) => {
                tracing::warn!(
                    %error,
                    line = %line,
                    "skipping malformed lhm-bridge stdout line"
                );
            }
        }
    }

    Ok(())
}

async fn pump_stderr(stderr: ChildStderr, state: Arc<ReaderState>) -> io::Result<()> {
    let mut lines = BufReader::new(stderr).lines();
    match lines.next_line().await? {
        Some(line) => match parse_banner(&line) {
            Ok(banner) => {
                state.set_sensor_elevated(banner.is_admin);
                tracing::info!(
                    elevated = banner.is_admin,
                    interval_ms = banner.interval_ms,
                    pid = banner.pid,
                    "received lhm-bridge banner"
                );
            }
            Err(error) => {
                tracing::warn!(
                    %error,
                    line = %line,
                    "invalid lhm-bridge stderr banner"
                );
            }
        },
        None => {
            tracing::warn!("lhm-bridge stderr closed before its banner");
            return Ok(());
        }
    }

    while let Some(line) = lines.next_line().await? {
        tracing::warn!(line = %line, "lhm-bridge stderr");
    }

    Ok(())
}

fn parse_banner(line: &str) -> Result<LhmBridgeBanner> {
    let banner: LhmBridgeBanner = serde_json::from_str(line)?;
    if banner.component != BRIDGE_COMPONENT {
        return Err(CoreError::LhmBridge(format!(
            "unexpected bridge component {:?}",
            banner.component
        )));
    }
    if banner.interval_ms <= 0 {
        return Err(CoreError::LhmBridge(format!(
            "invalid bridge interval {}",
            banner.interval_ms
        )));
    }
    if banner.pid <= 0 {
        return Err(CoreError::LhmBridge(format!(
            "invalid bridge pid {}",
            banner.pid
        )));
    }

    Ok(banner)
}

async fn wait_for_retry(delay: Duration, shutdown_rx: &mut watch::Receiver<bool>) -> bool {
    if shutdown_requested(shutdown_rx) {
        return false;
    }

    tokio::select! {
        _ = time::sleep(delay) => true,
        changed = shutdown_rx.changed() => {
            changed.is_ok() && !shutdown_requested(shutdown_rx)
        }
    }
}

fn shutdown_requested(shutdown_rx: &watch::Receiver<bool>) -> bool {
    *shutdown_rx.borrow()
}

async fn stop_child(child: &mut Child) {
    let pid = child.id();
    if let Some(pid) = pid {
        if let Err(error) = request_graceful_shutdown(pid) {
            tracing::warn!(pid, %error, "failed to request graceful lhm-bridge shutdown");
        }
    }

    match time::timeout(GRACEFUL_SHUTDOWN_TIMEOUT, child.wait()).await {
        Ok(Ok(status)) => {
            tracing::info!(?pid, %status, "lhm-bridge stopped during grace period");
            return;
        }
        Ok(Err(error)) => {
            tracing::warn!(?pid, %error, "failed waiting for graceful lhm-bridge shutdown");
        }
        Err(_) => {
            tracing::warn!(?pid, "lhm-bridge grace period expired; forcing termination");
        }
    }

    if let Err(error) = child.start_kill() {
        tracing::warn!(?pid, %error, "failed to issue forced lhm-bridge termination");
    }
    if let Err(error) = child.wait().await {
        tracing::error!(?pid, %error, "failed to reap lhm-bridge child");
    }
}

#[cfg(windows)]
fn configure_child_process(command: &mut Command) {
    use std::os::windows::process::CommandExt;

    const CREATE_NEW_PROCESS_GROUP: u32 = 0x0000_0200;
    command
        .as_std_mut()
        .creation_flags(CREATE_NEW_PROCESS_GROUP);
}

#[cfg(not(windows))]
fn configure_child_process(_command: &mut Command) {}

#[cfg(windows)]
fn request_graceful_shutdown(pid: u32) -> io::Result<()> {
    const CTRL_BREAK_EVENT: u32 = 1;

    #[link(name = "Kernel32")]
    extern "system" {
        fn GenerateConsoleCtrlEvent(ctrl_event: u32, process_group_id: u32) -> i32;
    }

    // The child is created as its own process group, so CTRL_BREAK is scoped to
    // that bridge and reaches its Console.CancelKeyPress handler.
    let generated = unsafe { GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, pid) };
    if generated == 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

#[cfg(unix)]
fn request_graceful_shutdown(pid: u32) -> io::Result<()> {
    const SIGTERM: i32 = 15;

    extern "C" {
        fn kill(pid: i32, signal: i32) -> i32;
    }

    let pid = i32::try_from(pid)
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "child pid exceeds i32"))?;
    let result = unsafe { kill(pid, SIGTERM) };
    if result == 0 {
        Ok(())
    } else {
        Err(io::Error::last_os_error())
    }
}

#[cfg(not(any(windows, unix)))]
fn request_graceful_shutdown(_pid: u32) -> io::Result<()> {
    Err(io::Error::new(
        io::ErrorKind::Unsupported,
        "graceful child signaling is unsupported on this platform",
    ))
}

#[cfg(test)]
mod tests {
    use std::{
        env,
        fs::{self, OpenOptions},
        io::{self, Write},
        path::Path,
        process,
        time::{Duration, SystemTime, UNIX_EPOCH},
    };

    use chrono::{TimeZone, Utc};

    use super::*;

    const FIXTURE_MODE_ENV: &str = "XHM_LHM_FIXTURE_MODE";
    const FIXTURE_MARKER_ENV: &str = "XHM_LHM_FIXTURE_MARKER";
    const FIXTURE_TEST_NAME: &str = "lhm::tests::bridge_fixture_process";

    fn sample_snapshot() -> LhmSnapshot {
        LhmSnapshot {
            ts: Utc.with_ymd_and_hms(2026, 7, 26, 6, 33, 34).unwrap(),
            cpu_temp: Some(61.5),
            cpu_temp_label: Some("Core Max".to_owned()),
            gpu_temp: Some(52.0),
            gpu_load: Some(43.0),
            net_up_mbps: 0.106,
            net_down_mbps: 0.091,
            disk_read_mbps: 0.0,
            disk_write_mbps: 0.0,
            disks: Vec::new(),
        }
    }

    fn fixture_launch(mode: &str, marker: Option<&Path>) -> LaunchSpec {
        let mut environment = vec![(OsString::from(FIXTURE_MODE_ENV), OsString::from(mode))];
        if let Some(marker) = marker {
            environment.push((
                OsString::from(FIXTURE_MARKER_ENV),
                marker.as_os_str().to_os_string(),
            ));
        }

        LaunchSpec {
            executable: env::current_exe().expect("test executable path"),
            arguments: vec![
                OsString::from("--nocapture"),
                OsString::from("--exact"),
                OsString::from(FIXTURE_TEST_NAME),
            ],
            environment,
        }
    }

    fn fixture_config(max_restarts: usize) -> SupervisorConfig {
        SupervisorConfig {
            max_restarts,
            initial_restart_delay: Duration::from_millis(10),
        }
    }

    async fn wait_until(predicate: impl Fn() -> bool) {
        time::timeout(Duration::from_secs(5), async {
            while !predicate() {
                time::sleep(Duration::from_millis(10)).await;
            }
        })
        .await
        .expect("condition timed out");
    }

    #[test]
    fn production_launch_uses_gc_tuning_without_a_hard_heap_limit() {
        let launch = LaunchSpec::production(env::current_exe().expect("test executable path"));

        assert_eq!(
            launch.environment,
            vec![
                (
                    OsString::from("DOTNET_GCConserveMemory"),
                    OsString::from("9"),
                ),
                (OsString::from("DOTNET_gcConcurrent"), OsString::from("0"),),
                (OsString::from("DOTNET_GCRetainVM"), OsString::from("0"),),
            ]
        );
    }

    #[test]
    fn parses_and_validates_banner() {
        let banner = parse_banner(
            r#"{"component":"lhm-bridge","is_admin":true,"interval_ms":1000,"pid":42}"#,
        )
        .unwrap();
        assert!(banner.is_admin);
        assert_eq!(banner.interval_ms, 1000);

        assert!(parse_banner(
            r#"{"component":"other","is_admin":true,"interval_ms":1000,"pid":42}"#
        )
        .is_err());
        assert!(parse_banner("not json").is_err());
    }

    #[test]
    fn reader_state_exposes_only_live_child_data() {
        let state = Arc::new(ReaderState::default());
        let reader = ManagedLhmReader {
            state: Arc::clone(&state),
        };

        assert_eq!(reader.snapshot(), None);
        assert!(!reader.is_sensor_elevated());

        state.child_started();
        state.set_sensor_elevated(true);
        state.set_snapshot(sample_snapshot());
        assert_eq!(reader.snapshot(), Some(sample_snapshot()));
        assert!(reader.is_sensor_elevated());

        state.child_stopped();
        assert_eq!(reader.snapshot(), None);
        assert!(!reader.is_sensor_elevated());
    }

    #[test]
    fn backoff_is_exponential_and_stops_after_five_retries() {
        let mut backoff = ExponentialBackoff::new(Duration::from_millis(25), MAX_RESTARTS);
        let delays: Vec<_> = (0..MAX_RESTARTS)
            .map(|_| backoff.next_delay().unwrap())
            .collect();

        assert_eq!(
            delays,
            [
                Duration::from_millis(25),
                Duration::from_millis(50),
                Duration::from_millis(100),
                Duration::from_millis(200),
                Duration::from_millis(400),
            ]
        );
        assert_eq!(backoff.next_delay(), None);
    }

    #[test]
    fn rejects_relative_bridge_path() {
        match LhmBridgeManager::start("lhm-bridge.exe") {
            Err(CoreError::InvalidArgument(message)) => {
                assert!(message.contains("absolute"));
            }
            Err(error) => panic!("unexpected error: {error}"),
            Ok(_) => panic!("relative bridge path was accepted"),
        }
    }

    #[tokio::test]
    async fn fixture_skips_bad_json_and_shutdown_is_repeatable() {
        let (reader, mut manager) = LhmBridgeManager::start_with_options(
            fixture_launch("bad-json", None),
            fixture_config(0),
        )
        .unwrap();

        wait_until(|| reader.snapshot().is_some() && reader.is_sensor_elevated()).await;
        let snapshot = reader.snapshot().unwrap();
        assert_eq!(snapshot.cpu_temp, Some(61.5));
        assert_eq!(snapshot.cpu_temp_label.as_deref(), Some("Core Max"));

        manager.shutdown().await;
        assert!(manager.is_shutdown());
        assert_eq!(reader.snapshot(), None);
        assert!(!reader.is_sensor_elevated());

        manager.shutdown().await;
        assert!(manager.is_shutdown());
    }

    #[tokio::test]
    async fn fixture_snapshot_then_exit_clears_the_stale_reader_view() {
        let (reader, mut manager) = LhmBridgeManager::start_with_options(
            fixture_launch("snapshot-then-exit", None),
            fixture_config(0),
        )
        .unwrap();

        wait_until(|| reader.snapshot().is_some()).await;
        wait_until(|| {
            manager
                .task
                .as_ref()
                .map(JoinHandle::is_finished)
                .unwrap_or(true)
        })
        .await;

        assert_eq!(reader.snapshot(), None);
        assert!(!reader.is_sensor_elevated());
        manager.shutdown().await;
    }

    #[tokio::test]
    async fn fixture_exit_restarts_only_within_budget() {
        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let marker = env::temp_dir().join(format!(
            "xhm-lhm-bridge-fixture-{}-{unique}.txt",
            process::id()
        ));
        let _ = fs::remove_file(&marker);

        let (reader, mut manager) = LhmBridgeManager::start_with_options(
            fixture_launch("exit", Some(&marker)),
            fixture_config(2),
        )
        .unwrap();

        wait_until(|| {
            manager
                .task
                .as_ref()
                .map(JoinHandle::is_finished)
                .unwrap_or(true)
        })
        .await;

        let launches = fs::read_to_string(&marker)
            .expect("fixture invocation marker")
            .lines()
            .count();
        assert_eq!(launches, 3);
        assert_eq!(reader.snapshot(), None);
        assert!(!reader.is_sensor_elevated());

        manager.shutdown().await;
        manager.shutdown().await;
        let _ = fs::remove_file(marker);
    }

    #[test]
    fn bridge_fixture_process() {
        let Ok(mode) = env::var(FIXTURE_MODE_ENV) else {
            return;
        };

        if let Ok(marker) = env::var(FIXTURE_MARKER_ENV) {
            let mut marker = OpenOptions::new()
                .create(true)
                .append(true)
                .open(marker)
                .expect("open fixture marker");
            writeln!(marker, "{}", process::id()).expect("write fixture marker");
            marker.flush().expect("flush fixture marker");
        }

        let banner = LhmBridgeBanner {
            component: BRIDGE_COMPONENT.to_owned(),
            is_admin: mode == "bad-json",
            interval_ms: 1000,
            pid: i32::try_from(process::id()).expect("fixture pid fits i32"),
        };
        let mut stderr = io::stderr().lock();
        writeln!(stderr, "{}", serde_json::to_string(&banner).unwrap()).unwrap();
        stderr.flush().unwrap();
        drop(stderr);

        match mode.as_str() {
            "bad-json" => {
                let mut stdout = io::stdout().lock();
                writeln!(stdout, "{{ definitely not json").unwrap();
                writeln!(
                    stdout,
                    "{}",
                    serde_json::to_string(&sample_snapshot()).unwrap()
                )
                .unwrap();
                stdout.flush().unwrap();
                drop(stdout);
                std::thread::sleep(Duration::from_secs(30));
            }
            "snapshot-then-exit" => {
                let mut stdout = io::stdout().lock();
                writeln!(
                    stdout,
                    "{}",
                    serde_json::to_string(&sample_snapshot()).unwrap()
                )
                .unwrap();
                stdout.flush().unwrap();
                drop(stdout);
                std::thread::sleep(Duration::from_millis(100));
                process::exit(23);
            }
            "exit" => {
                process::exit(23);
            }
            other => panic!("unknown fixture mode: {other}"),
        }
    }
}
