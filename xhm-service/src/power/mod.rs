use std::{
    collections::HashMap,
    ffi::{c_void, OsStr},
    fmt, fs,
    path::{Path, PathBuf},
    process::{Command, Output, Stdio},
    sync::{Arc, Mutex, MutexGuard},
    thread,
    time::{Duration, Instant},
};

use libloading::Library;
use serde::Deserialize;
use xhm_core::{
    models::{PowerScheme, PowerSchemeSwitchResult, PowerStatus, PowerWarmupStatus},
    traits::RyzenAdjClient,
    CoreError, Result,
};

use crate::state::ServicePaths;

const CLI_TIMEOUT: Duration = Duration::from_secs(3);
const MAX_CONSECUTIVE_CLI_FAILURES: u8 = 3;
const DEVICE_NOT_SUPPORTED: &str = "当前设备不支持此功能";
const SCHEME_PROFILE_NOT_CONFIGURED: &str = "功耗切换方案未配置";

#[derive(Debug, Clone, Copy, PartialEq)]
struct RyzenAdjSnapshot {
    stapm_limit: f64,
    stapm_value: f64,
    fast_limit: f64,
    fast_value: f64,
    slow_limit: f64,
    slow_value: f64,
}

trait RyzenAdjBackend: Send + Sync {
    fn is_available(&self) -> bool;
    fn read_snapshot(&self) -> Result<RyzenAdjSnapshot>;
    fn apply_scheme(&self, scheme: PowerScheme) -> Result<()>;
}

impl<T> RyzenAdjBackend for Arc<T>
where
    T: RyzenAdjBackend + ?Sized,
{
    fn is_available(&self) -> bool {
        (**self).is_available()
    }

    fn read_snapshot(&self) -> Result<RyzenAdjSnapshot> {
        (**self).read_snapshot()
    }

    fn apply_scheme(&self, scheme: PowerScheme) -> Result<()> {
        (**self).apply_scheme(scheme)
    }
}

struct NativeBackend {
    library_path: PathBuf,
    #[cfg(windows)]
    library_directory: Option<Vec<u16>>,
    session: Mutex<Option<NativeSession>>,
}

impl NativeBackend {
    fn new(library_path: PathBuf) -> Self {
        #[cfg(windows)]
        let library_directory = library_path.parent().map(path_to_wide_null);

        Self {
            library_path,
            #[cfg(windows)]
            library_directory,
            session: Mutex::new(None),
        }
    }

    #[cfg(windows)]
    fn library_directory(&self) -> Option<&[u16]> {
        self.library_directory.as_deref()
    }

    #[cfg(not(windows))]
    fn library_directory(&self) -> Option<&[u16]> {
        None
    }

    fn with_session<T>(
        &self,
        operation: impl FnOnce(&NativeSession, Option<&[u16]>) -> Result<T>,
    ) -> Result<T> {
        let mut session = lock_ryzen(&self.session, "native session")?;
        if session.is_none() {
            *session = Some(NativeSession::load(
                &self.library_path,
                self.library_directory(),
            )?);
        }

        operation(
            session.as_ref().expect("native session initialized"),
            self.library_directory(),
        )
    }
}

impl fmt::Debug for NativeBackend {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("NativeBackend")
            .field("library_path", &self.library_path)
            .finish_non_exhaustive()
    }
}

impl RyzenAdjBackend for NativeBackend {
    fn is_available(&self) -> bool {
        cfg!(windows) && self.library_path.is_file()
    }

    fn read_snapshot(&self) -> Result<RyzenAdjSnapshot> {
        self.with_session(NativeSession::read_snapshot)
    }

    fn apply_scheme(&self, scheme: PowerScheme) -> Result<()> {
        self.with_session(|session, directory| session.apply_scheme(directory, scheme))
    }
}

type InitFn = unsafe extern "system" fn() -> *mut c_void;
type CleanupFn = unsafe extern "system" fn(*mut c_void);
type RefreshFn = unsafe extern "system" fn(*mut c_void) -> i32;
type ReadValueFn = unsafe extern "system" fn(*mut c_void) -> f32;
type SetLimitFn = unsafe extern "system" fn(*mut c_void, u32) -> i32;

struct NativeSession {
    // The library must stay loaded until cleanup has run. Drop executes cleanup
    // before fields are released, so copied function pointers never outlive it.
    library: Library,
    handle: usize,
    cleanup: CleanupFn,
    refresh: RefreshFn,
    get_stapm_limit: ReadValueFn,
    get_stapm_value: ReadValueFn,
    get_fast_limit: ReadValueFn,
    get_fast_value: ReadValueFn,
    get_slow_limit: ReadValueFn,
    get_slow_value: ReadValueFn,
    set_stapm_limit: SetLimitFn,
    set_fast_limit: SetLimitFn,
    set_slow_limit: SetLimitFn,
}

impl NativeSession {
    fn load(library_path: &Path, library_directory: Option<&[u16]>) -> Result<Self> {
        if !cfg!(windows) {
            return Err(CoreError::Unsupported(
                "native RyzenAdj is only available on Windows".to_owned(),
            ));
        }

        let _directory_scope = NativeLibraryDirectoryScope::enter(library_directory)?;
        // SAFETY: Loading a DLL executes its loader hooks. The path is the configured
        // libryzenadj.dll path and the resulting Library is retained by NativeSession.
        let library = unsafe { Library::new(library_path) }
            .map_err(|error| ryzen_error(format!("load {}: {error}", library_path.display())))?;

        // SAFETY: These signatures match libryzenadj's exported stdcall API. Symbols
        // are copied only while `library` is alive and are stored beside that library.
        let init = unsafe { load_symbol::<InitFn>(&library, b"init_ryzenadj\0")? };
        let cleanup = unsafe { load_symbol::<CleanupFn>(&library, b"cleanup_ryzenadj\0")? };
        let refresh = unsafe { load_symbol::<RefreshFn>(&library, b"refresh_table\0")? };
        let get_stapm_limit =
            unsafe { load_symbol::<ReadValueFn>(&library, b"get_stapm_limit\0")? };
        let get_stapm_value =
            unsafe { load_symbol::<ReadValueFn>(&library, b"get_stapm_value\0")? };
        let get_fast_limit = unsafe { load_symbol::<ReadValueFn>(&library, b"get_fast_limit\0")? };
        let get_fast_value = unsafe { load_symbol::<ReadValueFn>(&library, b"get_fast_value\0")? };
        let get_slow_limit = unsafe { load_symbol::<ReadValueFn>(&library, b"get_slow_limit\0")? };
        let get_slow_value = unsafe { load_symbol::<ReadValueFn>(&library, b"get_slow_value\0")? };
        let set_stapm_limit = unsafe { load_symbol::<SetLimitFn>(&library, b"set_stapm_limit\0")? };
        let set_fast_limit = unsafe { load_symbol::<SetLimitFn>(&library, b"set_fast_limit\0")? };
        let set_slow_limit = unsafe { load_symbol::<SetLimitFn>(&library, b"set_slow_limit\0")? };

        // SAFETY: `init` is the resolved libryzenadj initializer and takes no arguments.
        let handle = unsafe { init() };
        if handle.is_null() {
            return Err(ryzen_error(
                "init_ryzenadj returned null; run as Administrator and keep the RyzenAdj driver files beside the DLL",
            ));
        }

        Ok(Self {
            library,
            handle: handle as usize,
            cleanup,
            refresh,
            get_stapm_limit,
            get_stapm_value,
            get_fast_limit,
            get_fast_value,
            get_slow_limit,
            get_slow_value,
            set_stapm_limit,
            set_fast_limit,
            set_slow_limit,
        })
    }

    fn read_snapshot(&self, library_directory: Option<&[u16]>) -> Result<RyzenAdjSnapshot> {
        let _directory_scope = NativeLibraryDirectoryScope::enter(library_directory)?;
        let handle = self.handle as *mut c_void;

        // SAFETY: The handle was returned by init_ryzenadj and is serialized by the
        // NativeBackend mutex. Function pointers remain valid while `self.library` lives.
        let refresh_result = unsafe { (self.refresh)(handle) };
        check_native_result("refresh_table", refresh_result)?;

        // SAFETY: Same initialized handle and live library as above. Getter calls do not
        // transfer ownership and are made only after a successful table refresh.
        let snapshot = unsafe {
            RyzenAdjSnapshot {
                stapm_limit: f64::from((self.get_stapm_limit)(handle)),
                stapm_value: f64::from((self.get_stapm_value)(handle)),
                fast_limit: f64::from((self.get_fast_limit)(handle)),
                fast_value: f64::from((self.get_fast_value)(handle)),
                slow_limit: f64::from((self.get_slow_limit)(handle)),
                slow_value: f64::from((self.get_slow_value)(handle)),
            }
        };
        Ok(snapshot)
    }

    fn apply_scheme(&self, library_directory: Option<&[u16]>, scheme: PowerScheme) -> Result<()> {
        let _directory_scope = NativeLibraryDirectoryScope::enter(library_directory)?;
        let handle = self.handle as *mut c_void;

        // SAFETY: The initialized handle and each setter are owned by this live session;
        // NativeBackend serializes the three writes as one operation.
        unsafe {
            check_native_result(
                "set_stapm_limit",
                (self.set_stapm_limit)(handle, watts_to_milliwatts(scheme.stapm_watts)),
            )?;
            check_native_result(
                "set_fast_limit",
                (self.set_fast_limit)(handle, watts_to_milliwatts(scheme.fast_watts)),
            )?;
            check_native_result(
                "set_slow_limit",
                (self.set_slow_limit)(handle, watts_to_milliwatts(scheme.slow_watts)),
            )?;
        }
        Ok(())
    }
}

impl fmt::Debug for NativeSession {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("NativeSession")
            .field("library", &self.library)
            .field("handle", &self.handle)
            .finish_non_exhaustive()
    }
}

impl Drop for NativeSession {
    fn drop(&mut self) {
        if self.handle == 0 {
            return;
        }

        // SAFETY: cleanup belongs to the still-live Library field and this is the sole
        // owner of the non-zero handle. The handle is cleared to make double cleanup impossible.
        unsafe { (self.cleanup)(self.handle as *mut c_void) };
        self.handle = 0;
    }
}

unsafe fn load_symbol<T>(library: &Library, name: &[u8]) -> Result<T>
where
    T: Copy,
{
    // SAFETY: The caller supplies the ABI/signature matching the requested export.
    let symbol = unsafe { library.get::<T>(name) }
        .map_err(|error| ryzen_error(format!("missing native symbol: {error}")))?;
    Ok(*symbol)
}

fn check_native_result(function: &str, result: i32) -> Result<()> {
    if result == 0 {
        Ok(())
    } else {
        Err(ryzen_error(format!("{function} failed with code {result}")))
    }
}

#[cfg(windows)]
static DLL_DIRECTORY_LOCK: Mutex<()> = Mutex::new(());

#[cfg(windows)]
struct NativeLibraryDirectoryScope {
    _guard: MutexGuard<'static, ()>,
    previous: Option<Vec<u16>>,
}

#[cfg(windows)]
impl NativeLibraryDirectoryScope {
    fn enter(directory: Option<&[u16]>) -> Result<Self> {
        use std::ptr::null_mut;
        use windows_sys::Win32::System::LibraryLoader::{GetDllDirectoryW, SetDllDirectoryW};

        let directory =
            directory.ok_or_else(|| ryzen_error("libryzenadj.dll path has no parent directory"))?;
        let guard = lock_ryzen(&DLL_DIRECTORY_LOCK, "DLL directory")?;

        // SAFETY: A zero-length query with a null buffer asks Windows for the required size.
        let required = unsafe { GetDllDirectoryW(0, null_mut()) };
        let previous = if required == 0 {
            None
        } else {
            let mut buffer = vec![0_u16; required as usize + 1];
            // SAFETY: `buffer` is writable for the supplied length and remains alive in scope.
            let written = unsafe {
                GetDllDirectoryW(
                    u32::try_from(buffer.len()).unwrap_or(u32::MAX),
                    buffer.as_mut_ptr(),
                )
            };
            if written == 0 {
                None
            } else {
                buffer.truncate(written as usize + 1);
                Some(buffer)
            }
        };

        // SAFETY: `directory` is a NUL-terminated UTF-16 string retained by NativeBackend.
        if unsafe { SetDllDirectoryW(directory.as_ptr()) } == 0 {
            return Err(CoreError::Io(std::io::Error::last_os_error()));
        }

        Ok(Self {
            _guard: guard,
            previous,
        })
    }
}

#[cfg(windows)]
impl Drop for NativeLibraryDirectoryScope {
    fn drop(&mut self) {
        use std::ptr::null;
        use windows_sys::Win32::System::LibraryLoader::SetDllDirectoryW;

        let previous = self
            .previous
            .as_ref()
            .map_or_else(null, |directory| directory.as_ptr());
        // SAFETY: The previous buffer is NUL terminated and lives through this call. A null
        // pointer restores the default DLL search directory when none was previously set.
        unsafe {
            SetDllDirectoryW(previous);
        }
    }
}

#[cfg(not(windows))]
struct NativeLibraryDirectoryScope;

#[cfg(not(windows))]
impl NativeLibraryDirectoryScope {
    fn enter(_directory: Option<&[u16]>) -> Result<Self> {
        Ok(Self)
    }
}

#[cfg(windows)]
fn path_to_wide_null(path: &Path) -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;

    path.as_os_str().encode_wide().chain(Some(0)).collect()
}

#[derive(Debug)]
struct CliBackend {
    executable_path: PathBuf,
}

impl CliBackend {
    fn new(executable_path: PathBuf) -> Self {
        Self { executable_path }
    }

    fn command(&self) -> Command {
        let mut command = Command::new(&self.executable_path);
        if let Some(directory) = self.executable_path.parent() {
            command.current_dir(directory);
        }
        command
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());
        command
    }

    fn run(&self, mut command: Command) -> Result<Output> {
        let mut child = command.spawn()?;
        let started = Instant::now();

        loop {
            match child.try_wait()? {
                Some(_) => break,
                None if started.elapsed() < CLI_TIMEOUT => {
                    thread::sleep(Duration::from_millis(10));
                }
                None => {
                    let _ = child.kill();
                    let _ = child.wait();
                    return Err(ryzen_error(format!(
                        "{} timed out after {:.1}s",
                        self.executable_path.display(),
                        CLI_TIMEOUT.as_secs_f64()
                    )));
                }
            }
        }

        let output = child.wait_with_output()?;
        if output.status.success() {
            return Ok(output);
        }

        let code = output.status.code().unwrap_or(-1);
        let stderr = String::from_utf8_lossy(&output.stderr);
        let stdout = String::from_utf8_lossy(&output.stdout);
        Err(ryzen_error(format!(
            "{} exited with code {code}. StdErr: {}. StdOut: {}",
            self.executable_path.display(),
            truncate(&stderr, 800),
            truncate(&stdout, 800)
        )))
    }
}

impl RyzenAdjBackend for CliBackend {
    fn is_available(&self) -> bool {
        cfg!(windows) && self.executable_path.is_file()
    }

    fn read_snapshot(&self) -> Result<RyzenAdjSnapshot> {
        let mut command = self.command();
        command.arg("-i");
        let output = self.run(command)?;
        let stdout = String::from_utf8_lossy(&output.stdout);
        parse_info_output(&stdout).map_err(ryzen_error)
    }

    fn apply_scheme(&self, scheme: PowerScheme) -> Result<()> {
        let arguments = limit_arguments(scheme);
        let mut command = self.command();
        command.args(arguments.iter().map(String::as_str));
        self.run(command).map(|_| ())
    }
}

fn limit_arguments(scheme: PowerScheme) -> [String; 3] {
    [
        format!("--stapm-limit={}", watts_to_milliwatts(scheme.stapm_watts)),
        format!("--fast-limit={}", watts_to_milliwatts(scheme.fast_watts)),
        format!("--slow-limit={}", watts_to_milliwatts(scheme.slow_watts)),
    ]
}

fn watts_to_milliwatts(watts: i32) -> u32 {
    u32::try_from(watts).unwrap_or(0).saturating_mul(1_000)
}

fn parse_info_output(output: &str) -> std::result::Result<RyzenAdjSnapshot, String> {
    if output.trim().is_empty() {
        return Err("Empty output".to_owned());
    }

    let mut stapm_limit = None;
    let mut stapm_value = None;
    let mut fast_limit = None;
    let mut fast_value = None;
    let mut slow_limit = None;
    let mut slow_value = None;

    for line in output.lines() {
        let line = line.trim();
        if !line.starts_with('|') || line.starts_with("|---") {
            continue;
        }

        let mut columns = line
            .split('|')
            .map(str::trim)
            .filter(|part| !part.is_empty());
        let Some(name) = columns.next() else {
            continue;
        };
        let Some(value_text) = columns.next() else {
            continue;
        };
        let Ok(value) = value_text.parse::<f64>() else {
            continue;
        };
        if value.is_nan() {
            continue;
        }

        if name.eq_ignore_ascii_case("STAPM LIMIT") {
            stapm_limit = Some(value);
        } else if name.eq_ignore_ascii_case("STAPM VALUE") {
            stapm_value = Some(value);
        } else if name.eq_ignore_ascii_case("PPT LIMIT FAST") {
            fast_limit = Some(value);
        } else if name.eq_ignore_ascii_case("PPT VALUE FAST") {
            fast_value = Some(value);
        } else if name.eq_ignore_ascii_case("PPT LIMIT SLOW") {
            slow_limit = Some(value);
        } else if name.eq_ignore_ascii_case("PPT VALUE SLOW") {
            slow_value = Some(value);
        }
    }

    match (
        stapm_limit,
        stapm_value,
        fast_limit,
        fast_value,
        slow_limit,
        slow_value,
    ) {
        (
            Some(stapm_limit),
            Some(stapm_value),
            Some(fast_limit),
            Some(fast_value),
            Some(slow_limit),
            Some(slow_value),
        ) => Ok(RyzenAdjSnapshot {
            stapm_limit,
            stapm_value,
            fast_limit,
            fast_value,
            slow_limit,
            slow_value,
        }),
        _ => Err("Missing required keys in output table".to_owned()),
    }
}

fn truncate(value: &str, max_chars: usize) -> &str {
    if value.len() <= max_chars {
        value
    } else {
        let mut boundary = max_chars;
        while !value.is_char_boundary(boundary) {
            boundary -= 1;
        }
        &value[..boundary]
    }
}

#[derive(Debug, Default)]
struct FallbackState {
    primary_disabled: bool,
    consecutive_cli_failures: u8,
    disabled: bool,
}

struct FallbackBackend<P, F> {
    primary: P,
    fallback: F,
    state: Mutex<FallbackState>,
}

impl<P, F> FallbackBackend<P, F>
where
    P: RyzenAdjBackend,
    F: RyzenAdjBackend,
{
    fn new(primary: P, fallback: F) -> Self {
        Self {
            primary,
            fallback,
            state: Mutex::new(FallbackState::default()),
        }
    }

    fn is_available(&self) -> bool {
        let state = self
            .state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        !state.disabled
            && ((!state.primary_disabled && self.primary.is_available())
                || self.fallback.is_available())
    }

    fn read_snapshot(&self) -> Result<RyzenAdjSnapshot> {
        let mut state = lock_ryzen(&self.state, "RyzenAdj fallback")?;
        if state.disabled {
            return Err(cli_circuit_open_error());
        }
        if !state.primary_disabled {
            if self.primary.is_available() {
                match self.primary.read_snapshot() {
                    Ok(snapshot) => return Ok(snapshot),
                    Err(error) => {
                        tracing::warn!(%error, "native RyzenAdj failed; permanently switching to CLI");
                        state.primary_disabled = true;
                    }
                }
            } else {
                state.primary_disabled = true;
            }
        }

        let result = self.fallback.read_snapshot();
        self.record_cli_result(&mut state, result.is_ok());
        result
    }

    fn apply_scheme(&self, scheme: PowerScheme) -> Result<()> {
        let mut state = lock_ryzen(&self.state, "RyzenAdj fallback")?;
        if state.disabled {
            return Err(cli_circuit_open_error());
        }
        if !state.primary_disabled {
            if self.primary.is_available() {
                match self.primary.apply_scheme(scheme) {
                    Ok(()) => return Ok(()),
                    Err(error) => {
                        tracing::warn!(%error, "native RyzenAdj failed; permanently switching to CLI");
                        state.primary_disabled = true;
                    }
                }
            } else {
                state.primary_disabled = true;
            }
        }

        let result = self.fallback.apply_scheme(scheme);
        self.record_cli_result(&mut state, result.is_ok());
        result
    }

    fn record_cli_result(&self, state: &mut FallbackState, succeeded: bool) {
        if succeeded {
            state.consecutive_cli_failures = 0;
            return;
        }

        state.consecutive_cli_failures = state.consecutive_cli_failures.saturating_add(1);
        if state.consecutive_cli_failures >= MAX_CONSECUTIVE_CLI_FAILURES {
            state.disabled = true;
            tracing::warn!(
                failure_count = state.consecutive_cli_failures,
                "disabled RyzenAdj after consecutive CLI failures"
            );
        }
    }
}

impl<P, F> RyzenAdjBackend for FallbackBackend<P, F>
where
    P: RyzenAdjBackend,
    F: RyzenAdjBackend,
{
    fn is_available(&self) -> bool {
        FallbackBackend::is_available(self)
    }

    fn read_snapshot(&self) -> Result<RyzenAdjSnapshot> {
        FallbackBackend::read_snapshot(self)
    }

    fn apply_scheme(&self, scheme: PowerScheme) -> Result<()> {
        FallbackBackend::apply_scheme(self, scheme)
    }
}

fn cli_circuit_open_error() -> CoreError {
    ryzen_error("RyzenAdj disabled after 3 consecutive CLI failures")
}

struct PlatformGatedBackend<B> {
    platform_supported: bool,
    backend: B,
}

impl<B> PlatformGatedBackend<B> {
    fn new(platform_supported: bool, backend: B) -> Self {
        Self {
            platform_supported,
            backend,
        }
    }
}

impl<B> RyzenAdjBackend for PlatformGatedBackend<B>
where
    B: RyzenAdjBackend,
{
    fn is_available(&self) -> bool {
        self.platform_supported && self.backend.is_available()
    }

    fn read_snapshot(&self) -> Result<RyzenAdjSnapshot> {
        if !self.platform_supported {
            return Err(CoreError::Unsupported(
                "RyzenAdj requires an AMD GPU and AMD Ryzen AI Max 395 processor".to_owned(),
            ));
        }
        self.backend.read_snapshot()
    }

    fn apply_scheme(&self, scheme: PowerScheme) -> Result<()> {
        if !self.platform_supported {
            return Err(CoreError::Unsupported(
                "RyzenAdj requires an AMD GPU and AMD Ryzen AI Max 395 processor".to_owned(),
            ));
        }
        self.backend.apply_scheme(scheme)
    }
}

/// Production RyzenAdj implementation. Construction only records paths; DLL loading,
/// native initialization, and CLI process creation are deferred until the first read/write.
pub struct ProductionRyzenAdjClient {
    backend: PlatformGatedBackend<FallbackBackend<NativeBackend, CliBackend>>,
    schemes: Box<[PowerScheme]>,
}

impl ProductionRyzenAdjClient {
    pub fn new(paths: &ServicePaths, platform_supported: bool) -> Self {
        let schemes = load_verified_schemes(paths);
        Self::from_paths(
            paths.ryzenadj_dll_path.clone(),
            paths.ryzenadj_exe_path.clone(),
            platform_supported,
            schemes,
        )
    }

    pub fn from_paths(
        library_path: PathBuf,
        executable_path: PathBuf,
        platform_supported: bool,
        schemes: Vec<PowerScheme>,
    ) -> Self {
        Self {
            backend: PlatformGatedBackend::new(
                platform_supported,
                FallbackBackend::new(
                    NativeBackend::new(library_path),
                    CliBackend::new(executable_path),
                ),
            ),
            schemes: schemes.into_boxed_slice(),
        }
    }
}

impl fmt::Debug for ProductionRyzenAdjClient {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("ProductionRyzenAdjClient")
            .field("platform_supported", &self.backend.platform_supported)
            .field("native", &self.backend.backend.primary)
            .field("cli", &self.backend.backend.fallback)
            .field("schemes", &self.schemes)
            .finish_non_exhaustive()
    }
}

impl RyzenAdjClient for ProductionRyzenAdjClient {
    fn is_supported(&self) -> bool {
        self.backend.is_available()
    }

    fn read_status(&self) -> Option<PowerStatus> {
        match self.backend.read_snapshot() {
            Ok(snapshot) => Some(snapshot_to_status(snapshot, &self.schemes)),
            Err(error) => {
                tracing::warn!(%error, "RyzenAdj status unavailable");
                None
            }
        }
    }

    fn apply_scheme(&self, scheme: PowerScheme) -> Result<()> {
        self.backend.apply_scheme(scheme)
    }
}

pub fn switch_to_next_scheme(
    client: &dyn RyzenAdjClient,
    schemes: &[PowerScheme],
) -> PowerSchemeSwitchResult {
    if !client.is_supported() {
        return PowerSchemeSwitchResult::fail("RyzenAdj not available");
    }
    if schemes.is_empty() {
        return PowerSchemeSwitchResult::fail(
            "Power switching disabled: no power schemes configured",
        );
    }

    let Some(status) = client.read_status() else {
        return PowerSchemeSwitchResult::fail("Power status unavailable");
    };
    let previous_index = match_scheme_index(status.limits, schemes);
    let next_index = previous_index.map_or(0, |index| (index + 1) % schemes.len());
    let next = schemes[next_index];

    if let Err(error) = client.apply_scheme(next) {
        if let Some(verified) = client.read_status() {
            if scheme_matches(verified.limits, next) {
                let verified_index =
                    match_scheme_index(verified.limits, schemes).unwrap_or(next_index);
                return PowerSchemeSwitchResult {
                    success: true,
                    message: "OK".to_owned(),
                    previous_scheme_index: previous_index.map(|index| index as i32),
                    new_scheme_index: verified_index as i32,
                    new_scheme: Some(verified.limits),
                };
            }
        }
        return PowerSchemeSwitchResult::fail(error.to_string());
    }

    PowerSchemeSwitchResult {
        success: true,
        message: "OK".to_owned(),
        previous_scheme_index: previous_index.map(|index| index as i32),
        new_scheme_index: next_index as i32,
        new_scheme: Some(next),
    }
}

fn snapshot_to_status(snapshot: RyzenAdjSnapshot, schemes: &[PowerScheme]) -> PowerStatus {
    let limits = PowerScheme {
        stapm_watts: to_watts(snapshot.stapm_limit).round() as i32,
        fast_watts: to_watts(snapshot.fast_limit).round() as i32,
        slow_watts: to_watts(snapshot.slow_limit).round() as i32,
    };
    PowerStatus {
        current_watts: to_watts(snapshot.stapm_value),
        limit_watts: to_watts(snapshot.stapm_limit),
        scheme_index: match_scheme_index(limits, schemes).map(|index| index as i32),
        limits,
    }
}

fn to_watts(value: f64) -> f64 {
    if value.is_nan() || value <= 0.0 {
        0.0
    } else if value > 1_000.0 {
        value / 1_000.0
    } else {
        value
    }
}

fn match_scheme_index(limits: PowerScheme, schemes: &[PowerScheme]) -> Option<usize> {
    schemes
        .iter()
        .position(|scheme| scheme_matches(limits, *scheme))
}

fn scheme_matches(left: PowerScheme, right: PowerScheme) -> bool {
    left.stapm_watts.abs_diff(right.stapm_watts) <= 1
        && left.fast_watts.abs_diff(right.fast_watts) <= 1
        && left.slow_watts.abs_diff(right.slow_watts) <= 1
}

/// Device authorization is independent from process elevation. It represents the
/// model whitelist used by the C# DeviceVerifier and controls only scheme changes.
pub trait DeviceVerifier: Send + Sync {
    fn verification_status(&self, retry: bool) -> PowerWarmupStatus;
    fn schemes_for_device(&self, device_name: &str) -> &[PowerScheme];
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DeviceVerificationRule {
    pub name: String,
    pub scheme_key: String,
    pub manufacturer_contains: Vec<String>,
    pub model_contains: Vec<String>,
    pub schemes: Vec<PowerScheme>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct DeviceVerificationOptions {
    pub devices: Vec<DeviceVerificationRule>,
}

impl DeviceVerificationOptions {
    pub fn load(paths: &ServicePaths) -> anyhow::Result<Self> {
        let contents = fs::read_to_string(paths.exe_dir.join("appsettings.json"))?;
        Self::from_json(&contents)
    }

    fn from_json(contents: &str) -> anyhow::Result<Self> {
        let settings: AppSettings = serde_json::from_str(&strip_json_comments(contents))?;
        let verification = settings.power.device_verification;
        let devices = verification
            .devices
            .into_iter()
            .map(|device| {
                let schemes = verification
                    .scheme_profiles
                    .iter()
                    .find(|(key, _)| key.eq_ignore_ascii_case(&device.scheme_key))
                    .map(|(_, schemes)| {
                        schemes
                            .iter()
                            .copied()
                            .map(PowerSchemeConfig::into_power_scheme)
                            .collect()
                    })
                    .unwrap_or_default();
                DeviceVerificationRule {
                    name: device.name,
                    scheme_key: device.scheme_key,
                    manufacturer_contains: device.hardware_manufacturer_contains,
                    model_contains: device.hardware_model_contains,
                    schemes,
                }
            })
            .collect();
        Ok(Self { devices })
    }

    fn matching_rule(&self, facts: &DeviceFacts) -> Option<&DeviceVerificationRule> {
        self.devices.iter().find(|rule| rule_matches(rule, facts))
    }
}

#[derive(Debug, Default, Deserialize)]
struct AppSettings {
    #[serde(rename = "Power", default)]
    power: PowerSettings,
}

#[derive(Debug, Default, Deserialize)]
struct PowerSettings {
    #[serde(rename = "DeviceVerification", default)]
    device_verification: DeviceVerificationSettings,
}

#[derive(Debug, Default, Deserialize)]
struct DeviceVerificationSettings {
    #[serde(rename = "SchemeProfiles", default)]
    scheme_profiles: HashMap<String, Vec<PowerSchemeConfig>>,
    #[serde(rename = "Devices", default)]
    devices: Vec<DeviceVerificationRuleConfig>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct DeviceVerificationRuleConfig {
    #[serde(default)]
    name: String,
    #[serde(default)]
    scheme_key: String,
    #[serde(default)]
    hardware_manufacturer_contains: Vec<String>,
    #[serde(default)]
    hardware_model_contains: Vec<String>,
}

#[derive(Debug, Clone, Copy, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct PowerSchemeConfig {
    stapm_watts: i32,
    fast_watts: i32,
    slow_watts: i32,
}

impl PowerSchemeConfig {
    fn into_power_scheme(self) -> PowerScheme {
        PowerScheme {
            stapm_watts: self.stapm_watts,
            fast_watts: self.fast_watts,
            slow_watts: self.slow_watts,
        }
    }
}

fn strip_json_comments(contents: &str) -> String {
    let mut output = String::with_capacity(contents.len());
    let mut chars = contents.chars().peekable();
    let mut in_string = false;
    let mut escaped = false;

    while let Some(character) = chars.next() {
        if in_string {
            output.push(character);
            if escaped {
                escaped = false;
            } else if character == '\\' {
                escaped = true;
            } else if character == '"' {
                in_string = false;
            }
            continue;
        }

        if character == '"' {
            in_string = true;
            output.push(character);
            continue;
        }

        if character != '/' {
            output.push(character);
            continue;
        }

        match chars.peek().copied() {
            Some('/') => {
                output.push(' ');
                output.push(' ');
                chars.next();
                for comment_character in chars.by_ref() {
                    if comment_character == '\n' {
                        output.push('\n');
                        break;
                    }
                    output.push(' ');
                }
            }
            Some('*') => {
                output.push(' ');
                output.push(' ');
                chars.next();
                let mut previous_was_asterisk = false;
                for comment_character in chars.by_ref() {
                    if comment_character == '\n' {
                        output.push('\n');
                    } else {
                        output.push(' ');
                    }
                    if previous_was_asterisk && comment_character == '/' {
                        break;
                    }
                    previous_was_asterisk = comment_character == '*';
                }
            }
            _ => output.push(character),
        }
    }

    output
}

fn load_verified_schemes(paths: &ServicePaths) -> Vec<PowerScheme> {
    let options = match DeviceVerificationOptions::load(paths) {
        Ok(options) => options,
        Err(error) => {
            tracing::warn!(
                %error,
                path = %paths.exe_dir.join("appsettings.json").display(),
                "failed to load power device verification configuration"
            );
            return Vec::new();
        }
    };
    let Some(facts) = read_device_facts() else {
        return Vec::new();
    };
    options
        .matching_rule(&facts)
        .map(|rule| rule.schemes.clone())
        .unwrap_or_default()
}

#[derive(Debug)]
pub struct ProductionDeviceVerifier {
    options: DeviceVerificationOptions,
    cached: Mutex<Option<PowerWarmupStatus>>,
}

impl ProductionDeviceVerifier {
    pub fn new(options: DeviceVerificationOptions) -> Self {
        Self {
            options,
            cached: Mutex::new(None),
        }
    }

    pub fn from_paths(paths: &ServicePaths) -> Self {
        match DeviceVerificationOptions::load(paths) {
            Ok(options) => Self::new(options),
            Err(error) => {
                tracing::warn!(
                    %error,
                    path = %paths.exe_dir.join("appsettings.json").display(),
                    "failed to load power device verification configuration"
                );
                Self::new(DeviceVerificationOptions::default())
            }
        }
    }

    fn probe(&self) -> PowerWarmupStatus {
        let Some(facts) = read_device_facts() else {
            return disabled_verification();
        };
        verification_for_facts(&self.options, &facts)
    }
}

impl Default for ProductionDeviceVerifier {
    fn default() -> Self {
        match ServicePaths::new() {
            Ok(paths) => Self::from_paths(&paths),
            Err(error) => {
                tracing::warn!(%error, "failed to resolve service paths for power configuration");
                Self::new(DeviceVerificationOptions::default())
            }
        }
    }
}

impl DeviceVerifier for ProductionDeviceVerifier {
    fn verification_status(&self, retry: bool) -> PowerWarmupStatus {
        let mut cached = self
            .cached
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if let Some(status) = cached.as_ref() {
            if status.device_name.is_some() || !retry {
                return status.clone();
            }
        }

        let status = self.probe();
        *cached = Some(status.clone());
        status
    }

    fn schemes_for_device(&self, device_name: &str) -> &[PowerScheme] {
        self.options
            .devices
            .iter()
            .find(|rule| rule.name.eq_ignore_ascii_case(device_name))
            .map(|rule| rule.schemes.as_slice())
            .unwrap_or_default()
    }
}

fn verification_for_facts(
    options: &DeviceVerificationOptions,
    facts: &DeviceFacts,
) -> PowerWarmupStatus {
    let Some(rule) = options.matching_rule(facts) else {
        return disabled_verification();
    };
    if rule.schemes.is_empty() {
        return PowerWarmupStatus {
            enabled: false,
            device_name: Some(rule.name.clone()),
            reason: Some(SCHEME_PROFILE_NOT_CONFIGURED.to_owned()),
        };
    }

    PowerWarmupStatus {
        enabled: true,
        device_name: Some(rule.name.clone()),
        reason: None,
    }
}

fn disabled_verification() -> PowerWarmupStatus {
    PowerWarmupStatus {
        enabled: false,
        device_name: None,
        reason: Some(DEVICE_NOT_SUPPORTED.to_owned()),
    }
}

#[derive(Debug, Default)]
struct DeviceFacts {
    manufacturers: Vec<String>,
    models: Vec<String>,
    processor_name: Option<String>,
}

fn rule_matches(rule: &DeviceVerificationRule, facts: &DeviceFacts) -> bool {
    contains_any(&facts.manufacturers, &rule.manufacturer_contains)
        && contains_any(&facts.models, &rule.model_contains)
}

fn contains_any(values: &[String], candidates: &[String]) -> bool {
    candidates.is_empty()
        || candidates.iter().any(|candidate| {
            !candidate.is_empty()
                && values
                    .iter()
                    .any(|value| contains_ignore_ascii_case(value, candidate))
        })
}

fn contains_ignore_ascii_case(value: &str, candidate: &str) -> bool {
    let candidate = candidate.as_bytes();
    !candidate.is_empty()
        && value
            .as_bytes()
            .windows(candidate.len())
            .any(|window| window.eq_ignore_ascii_case(candidate))
}

fn power_platform_supported(gpu_descriptions: &[String], processor_name: Option<&str>) -> bool {
    let has_amd_gpu = gpu_descriptions.iter().any(|description| {
        contains_ignore_ascii_case(description, "advanced micro devices")
            || contains_ignore_ascii_case(description, "amd")
            || contains_ignore_ascii_case(description, "radeon")
    });
    let processor_name = processor_name.unwrap_or_default();
    has_amd_gpu
        && contains_ignore_ascii_case(processor_name, "AMD Ryzen AI Max")
        && contains_ignore_ascii_case(processor_name, "395")
}

#[cfg(windows)]
pub fn is_supported_power_platform() -> bool {
    let Some(facts) = read_device_facts() else {
        return false;
    };
    power_platform_supported(&read_gpu_descriptions(), facts.processor_name.as_deref())
}

#[cfg(not(windows))]
pub fn is_supported_power_platform() -> bool {
    false
}

#[cfg(windows)]
fn read_device_facts() -> Option<DeviceFacts> {
    const BIOS_KEY: &str = r"HARDWARE\DESCRIPTION\System\BIOS";
    const PROCESSOR_KEY: &str = r"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    let manufacturers = ["SystemManufacturer", "BaseBoardManufacturer"]
        .into_iter()
        .filter_map(|name| read_registry_string(BIOS_KEY, name))
        .collect();
    let models = ["SystemProductName", "SystemFamily", "BaseBoardProduct"]
        .into_iter()
        .filter_map(|name| read_registry_string(BIOS_KEY, name))
        .collect();

    Some(DeviceFacts {
        manufacturers,
        models,
        processor_name: read_registry_string(PROCESSOR_KEY, "ProcessorNameString"),
    })
}

#[cfg(not(windows))]
fn read_device_facts() -> Option<DeviceFacts> {
    None
}

#[cfg(windows)]
fn read_gpu_descriptions() -> Vec<String> {
    const VIDEO_KEY: &str = r"SYSTEM\CurrentControlSet\Control\Video";
    const DESCRIPTION_VALUES: [&str; 3] = [
        "DriverDesc",
        "ProviderName",
        "HardwareInformation.AdapterString",
    ];

    registry_subkeys(VIDEO_KEY)
        .into_iter()
        .flat_map(|adapter| {
            let adapter_path = format!(r"{VIDEO_KEY}\{adapter}");
            registry_subkeys(&adapter_path)
                .into_iter()
                .flat_map(move |instance| {
                    let instance_path = format!(r"{adapter_path}\{instance}");
                    DESCRIPTION_VALUES
                        .into_iter()
                        .filter_map(move |name| read_registry_string(&instance_path, name))
                })
        })
        .collect()
}

#[cfg(windows)]
fn registry_subkeys(subkey: &str) -> Vec<String> {
    use std::{
        os::windows::ffi::OsStrExt,
        ptr::{null, null_mut},
    };
    use windows_sys::Win32::{
        Foundation::ERROR_SUCCESS,
        System::Registry::{
            RegCloseKey, RegEnumKeyExW, RegOpenKeyExW, HKEY_LOCAL_MACHINE, KEY_READ,
        },
    };

    let subkey: Vec<u16> = OsStr::new(subkey).encode_wide().chain(Some(0)).collect();
    let mut key = null_mut();
    // SAFETY: HKEY_LOCAL_MACHINE is predefined, the path is NUL-terminated, and `key`
    // points to writable storage for the opened handle.
    if unsafe { RegOpenKeyExW(HKEY_LOCAL_MACHINE, subkey.as_ptr(), 0, KEY_READ, &mut key) }
        != ERROR_SUCCESS
    {
        return Vec::new();
    }

    let mut names = Vec::new();
    let mut index = 0;
    loop {
        let mut name = [0_u16; 256];
        let mut name_len = name.len() as u32;
        // SAFETY: `key` is open and `name`/`name_len` describe a writable UTF-16 buffer.
        let status = unsafe {
            RegEnumKeyExW(
                key,
                index,
                name.as_mut_ptr(),
                &mut name_len,
                null(),
                null_mut(),
                null_mut(),
                null_mut(),
            )
        };
        if status != ERROR_SUCCESS {
            break;
        }
        if let Ok(name) = String::from_utf16(&name[..name_len as usize]) {
            names.push(name);
        }
        index += 1;
    }

    // SAFETY: `key` was returned by RegOpenKeyExW and is closed exactly once.
    unsafe {
        RegCloseKey(key);
    }
    names
}

#[cfg(windows)]
fn read_registry_string(subkey: &str, value_name: &str) -> Option<String> {
    use std::{os::windows::ffi::OsStrExt, ptr::null_mut};
    use windows_sys::Win32::{
        Foundation::ERROR_SUCCESS,
        System::Registry::{RegGetValueW, HKEY_LOCAL_MACHINE, RRF_RT_REG_SZ},
    };

    let subkey: Vec<u16> = OsStr::new(subkey).encode_wide().chain(Some(0)).collect();
    let value_name: Vec<u16> = OsStr::new(value_name)
        .encode_wide()
        .chain(Some(0))
        .collect();
    let mut byte_len = 0_u32;

    // SAFETY: Predefined HKEY is valid and both names are NUL-terminated. Null data asks
    // RegGetValueW for the required buffer size.
    let status = unsafe {
        RegGetValueW(
            HKEY_LOCAL_MACHINE,
            subkey.as_ptr(),
            value_name.as_ptr(),
            RRF_RT_REG_SZ,
            null_mut(),
            null_mut(),
            &mut byte_len,
        )
    };
    if status != ERROR_SUCCESS || byte_len < 2 {
        return None;
    }

    let mut value = vec![0_u16; byte_len.div_ceil(2) as usize];
    // SAFETY: `value` is sized from the first call and writable for `byte_len` bytes.
    let status = unsafe {
        RegGetValueW(
            HKEY_LOCAL_MACHINE,
            subkey.as_ptr(),
            value_name.as_ptr(),
            RRF_RT_REG_SZ,
            null_mut(),
            value.as_mut_ptr().cast(),
            &mut byte_len,
        )
    };
    if status != ERROR_SUCCESS {
        return None;
    }

    let used = value
        .iter()
        .position(|unit| *unit == 0)
        .unwrap_or(value.len());
    String::from_utf16(&value[..used])
        .ok()
        .filter(|value| !value.trim().is_empty())
}

/// Returns whether the current Windows process token belongs to the built-in
/// Administrators group. Other platforms explicitly return false.
#[cfg(windows)]
pub fn is_administrator() -> bool {
    use std::ptr::null_mut;
    use windows_sys::Win32::Security::{
        CheckTokenMembership, CreateWellKnownSid, WinBuiltinAdministratorsSid,
        SECURITY_MAX_SID_SIZE,
    };

    let mut sid = [0_u8; SECURITY_MAX_SID_SIZE as usize];
    let mut sid_len = SECURITY_MAX_SID_SIZE;
    // SAFETY: `sid` has SECURITY_MAX_SID_SIZE bytes and is writable for the call.
    if unsafe {
        CreateWellKnownSid(
            WinBuiltinAdministratorsSid,
            null_mut(),
            sid.as_mut_ptr().cast(),
            &mut sid_len,
        )
    } == 0
    {
        return false;
    }

    let mut is_member = 0;
    // SAFETY: A null token asks Windows to use the effective thread/process token;
    // `sid` contains the valid well-known Administrators SID from the prior call.
    unsafe {
        CheckTokenMembership(null_mut(), sid.as_mut_ptr().cast(), &mut is_member) != 0
            && is_member != 0
    }
}

#[cfg(not(windows))]
pub fn is_administrator() -> bool {
    false
}

fn lock_ryzen<'a, T>(mutex: &'a Mutex<T>, name: &str) -> Result<MutexGuard<'a, T>> {
    mutex
        .lock()
        .map_err(|_| ryzen_error(format!("{name} lock poisoned")))
}

fn ryzen_error(message: impl Into<String>) -> CoreError {
    CoreError::RyzenAdj(message.into())
}

#[cfg(test)]
mod tests {
    use std::sync::{
        atomic::{AtomicUsize, Ordering},
        Arc,
    };

    use xhm_core::traits::MockRyzenAdjClient;

    use super::*;
    const TEST_SCHEMES: [PowerScheme; 3] = [
        PowerScheme {
            stapm_watts: 55,
            fast_watts: 100,
            slow_watts: 55,
        },
        PowerScheme {
            stapm_watts: 85,
            fast_watts: 120,
            slow_watts: 85,
        },
        PowerScheme {
            stapm_watts: 120,
            fast_watts: 140,
            slow_watts: 120,
        },
    ];
    const DEVICE_CONFIG: &str = r#"{
        // Existing C# appsettings.json uses JSON comments.
        "Power": {
            "DeviceVerification": {
                "Endpoint": "http://127.0.0.1:5050/device_info",
                "SchemeProfiles": {
                    "AXB35-02": [
                        { "StapmWatts": 55, "FastWatts": 100, "SlowWatts": 55 },
                        { "StapmWatts": 85, "FastWatts": 120, "SlowWatts": 85 },
                        { "StapmWatts": 120, "FastWatts": 140, "SlowWatts": 120 }
                    ]
                },
                "Devices": [{
                    "Name": "SixUnitedAXB35-02",
                    "SchemeKey": "$SCHEME_KEY",
                    "HardwareManufacturerContains": ["Six United", "Sixunited"],
                    "HardwareModelContains": ["AXB35-02"]
                }]
            }
        }
    }"#;

    fn configured_options(scheme_key: &str) -> DeviceVerificationOptions {
        DeviceVerificationOptions::from_json(&DEVICE_CONFIG.replace("$SCHEME_KEY", scheme_key))
            .unwrap()
    }

    fn matching_facts() -> DeviceFacts {
        DeviceFacts {
            manufacturers: vec!["Six United Intelligent Tech. CO.,Ltd.".to_owned()],
            models: vec!["AXB35-02".to_owned()],
            processor_name: Some("AMD Ryzen AI Max+ PRO 395".to_owned()),
        }
    }

    fn snapshot() -> RyzenAdjSnapshot {
        RyzenAdjSnapshot {
            stapm_limit: 55_000.0,
            stapm_value: 42_000.0,
            fast_limit: 100_000.0,
            fast_value: 80_000.0,
            slow_limit: 55_000.0,
            slow_value: 45_000.0,
        }
    }

    #[test]
    fn cli_parser_reads_all_required_power_fields() {
        let output = r#"
CPU Family: Renoir
|        Name         |   Value   |     Parameter      |
|---------------------|-----------|--------------------|
| STAPM LIMIT         | 55000.000 | stapm-limit        |
| STAPM VALUE         | 42000.000 |                    |
| PPT LIMIT FAST      | 100000.000 | fast-limit        |
| PPT VALUE FAST      | 80000.000 |                    |
| PPT LIMIT SLOW      | 55000.000 | slow-limit         |
| PPT VALUE SLOW      | 45000.000 |                    |
"#;

        assert_eq!(parse_info_output(output), Ok(snapshot()));
    }

    #[test]
    fn cli_parser_rejects_incomplete_status_table() {
        let error = parse_info_output("| STAPM LIMIT | 55000.000 |").unwrap_err();

        assert_eq!(error, "Missing required keys in output table");
    }

    #[test]
    fn cli_limit_arguments_convert_watts_to_milliwatts() {
        assert_eq!(
            limit_arguments(TEST_SCHEMES[0]),
            [
                "--stapm-limit=55000",
                "--fast-limit=100000",
                "--slow-limit=55000",
            ]
        );
    }

    #[derive(Debug)]
    struct TestBackend {
        fail_read: bool,
        reads: AtomicUsize,
    }

    impl TestBackend {
        fn succeeding() -> Self {
            Self {
                fail_read: false,
                reads: AtomicUsize::new(0),
            }
        }

        fn failing() -> Self {
            Self {
                fail_read: true,
                reads: AtomicUsize::new(0),
            }
        }
    }

    impl RyzenAdjBackend for TestBackend {
        fn is_available(&self) -> bool {
            true
        }

        fn read_snapshot(&self) -> Result<RyzenAdjSnapshot> {
            self.reads.fetch_add(1, Ordering::SeqCst);
            if self.fail_read {
                Err(ryzen_error("native failed"))
            } else {
                Ok(snapshot())
            }
        }

        fn apply_scheme(&self, _scheme: PowerScheme) -> Result<()> {
            Ok(())
        }
    }

    #[derive(Debug)]
    struct ScriptedBackend {
        outcomes: Mutex<std::collections::VecDeque<bool>>,
        reads: AtomicUsize,
    }

    impl ScriptedBackend {
        fn new(outcomes: impl IntoIterator<Item = bool>) -> Self {
            Self {
                outcomes: Mutex::new(outcomes.into_iter().collect()),
                reads: AtomicUsize::new(0),
            }
        }
    }

    impl RyzenAdjBackend for ScriptedBackend {
        fn is_available(&self) -> bool {
            true
        }

        fn read_snapshot(&self) -> Result<RyzenAdjSnapshot> {
            self.reads.fetch_add(1, Ordering::SeqCst);
            if self.outcomes.lock().unwrap().pop_front().unwrap_or(false) {
                Ok(snapshot())
            } else {
                Err(ryzen_error("CLI failed"))
            }
        }

        fn apply_scheme(&self, _scheme: PowerScheme) -> Result<()> {
            Ok(())
        }
    }

    #[test]
    fn fallback_disables_native_exactly_once_across_threads() {
        let primary = Arc::new(TestBackend::failing());
        let fallback = Arc::new(TestBackend::succeeding());
        let client = Arc::new(FallbackBackend::new(primary.clone(), fallback.clone()));

        let workers: Vec<_> = (0..8)
            .map(|_| {
                let client = Arc::clone(&client);
                thread::spawn(move || client.read_snapshot().unwrap())
            })
            .collect();
        for worker in workers {
            assert_eq!(worker.join().unwrap(), snapshot());
        }

        assert_eq!(primary.reads.load(Ordering::SeqCst), 1);
        assert_eq!(fallback.reads.load(Ordering::SeqCst), 8);
    }

    #[test]
    fn three_consecutive_cli_failures_open_circuit_and_stop_execution() {
        let primary = Arc::new(TestBackend::failing());
        let fallback = Arc::new(TestBackend::failing());
        let client = FallbackBackend::new(primary, fallback.clone());

        for _ in 0..MAX_CONSECUTIVE_CLI_FAILURES {
            assert!(client.read_snapshot().is_err());
        }

        assert!(!client.is_available());
        assert!(client.read_snapshot().is_err());
        assert_eq!(
            fallback.reads.load(Ordering::SeqCst),
            usize::from(MAX_CONSECUTIVE_CLI_FAILURES)
        );
    }

    #[test]
    fn successful_cli_execution_resets_consecutive_failure_count() {
        let primary = Arc::new(TestBackend::failing());
        let fallback = Arc::new(ScriptedBackend::new([
            false, false, true, false, false, false,
        ]));
        let client = FallbackBackend::new(primary, fallback.clone());

        assert!(client.read_snapshot().is_err());
        assert!(client.read_snapshot().is_err());
        assert!(client.read_snapshot().is_ok());
        assert!(client.read_snapshot().is_err());
        assert!(client.read_snapshot().is_err());
        assert!(client.is_available());
        assert!(client.read_snapshot().is_err());

        assert!(!client.is_available());
        assert_eq!(fallback.reads.load(Ordering::SeqCst), 6);
    }

    #[test]
    fn unsupported_platform_gate_never_touches_backend() {
        let backend = Arc::new(TestBackend::succeeding());
        let client = PlatformGatedBackend::new(false, backend.clone());

        assert!(!client.is_available());
        assert!(matches!(
            client.read_snapshot(),
            Err(CoreError::Unsupported(_))
        ));
        assert_eq!(backend.reads.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn scheme_next_wraps_from_last_scheme_to_first() {
        let client = MockRyzenAdjClient::supported(PowerStatus {
            current_watts: 91.0,
            limit_watts: 120.0,
            scheme_index: Some(2),
            limits: TEST_SCHEMES[2],
        });

        let result = switch_to_next_scheme(&client, &TEST_SCHEMES);

        assert!(result.success);
        assert_eq!(result.previous_scheme_index, Some(2));
        assert_eq!(result.new_scheme_index, 0);
        assert_eq!(result.new_scheme, Some(TEST_SCHEMES[0]));
        assert_eq!(client.applied_schemes(), [TEST_SCHEMES[0]]);
    }

    #[test]
    fn unknown_limits_start_at_first_scheme() {
        let client = MockRyzenAdjClient::supported(PowerStatus {
            current_watts: 42.0,
            limit_watts: 60.0,
            scheme_index: None,
            limits: PowerScheme {
                stapm_watts: 60,
                fast_watts: 90,
                slow_watts: 60,
            },
        });

        let result = switch_to_next_scheme(&client, &TEST_SCHEMES);

        assert!(result.success);
        assert_eq!(result.previous_scheme_index, None);
        assert_eq!(result.new_scheme_index, 0);
    }

    #[test]
    fn configured_device_rule_matches_csharp_smbios_values() {
        let options = configured_options("AXB35-02");
        let facts = matching_facts();

        assert!(rule_matches(&options.devices[0], &facts));
        assert!(verification_for_facts(&options, &facts).enabled);
    }

    #[test]
    fn jsonc_configuration_resolves_scheme_profile_case_insensitively() {
        let options = configured_options("axb35-02");

        assert_eq!(options.devices.len(), 1);
        assert_eq!(options.devices[0].scheme_key, "axb35-02");
        assert_eq!(options.devices[0].schemes, TEST_SCHEMES);
    }

    #[test]
    fn verified_device_with_missing_profile_disables_switching() {
        let options = configured_options("missing-profile");
        let status = verification_for_facts(&options, &matching_facts());

        assert!(!status.enabled);
        assert_eq!(status.device_name.as_deref(), Some("SixUnitedAXB35-02"));
        assert_eq!(
            status.reason.as_deref(),
            Some(SCHEME_PROFILE_NOT_CONFIGURED)
        );
    }

    #[test]
    fn platform_gate_requires_amd_gpu_and_ryzen_ai_max_395() {
        let supported_gpu = ["AMD Radeon 8060S Graphics".to_owned()];
        let unsupported_gpu = ["NVIDIA GeForce RTX 5090".to_owned()];

        assert!(power_platform_supported(
            &supported_gpu,
            Some("AMD Ryzen AI Max+ PRO 395")
        ));
        assert!(!power_platform_supported(
            &unsupported_gpu,
            Some("AMD Ryzen AI Max+ PRO 395")
        ));
        assert!(!power_platform_supported(
            &supported_gpu,
            Some("AMD Ryzen 9 9950X")
        ));
    }

    #[cfg(not(windows))]
    #[test]
    fn administrator_detection_is_explicitly_false_off_windows() {
        assert!(!is_administrator());
    }
}
