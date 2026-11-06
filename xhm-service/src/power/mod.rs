use std::{
    collections::HashMap,
    fmt, fs,
    sync::{Arc, Mutex, MutexGuard},
    time::{Duration, Instant},
};

#[cfg(windows)]
use std::ffi::OsStr;

use serde::Deserialize;
use xhm_core::{
    models::{PowerScheme, PowerSchemeSwitchResult, PowerStatus, PowerWarmupStatus},
    traits::RyzenAdjClient,
    CoreError, Result,
};

use crate::state::{strip_json_comments, ServicePaths};

mod pawnio;
use pawnio::PawnIoBackend;

const STATUS_CACHE_TTL: Duration = Duration::from_secs(3);
const MAX_CONSECUTIVE_FAILURES: u8 = 3;
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

#[derive(Default)]
struct BackendState {
    cached: Option<(Instant, Option<RyzenAdjSnapshot>)>,
    consecutive_failures: u8,
}

struct CachedBackend<B> {
    platform_supported: bool,
    backend: B,
    state: Mutex<BackendState>,
}

impl<B: RyzenAdjBackend> CachedBackend<B> {
    fn new(platform_supported: bool, backend: B) -> Self {
        Self {
            platform_supported,
            backend,
            state: Mutex::new(BackendState::default()),
        }
    }

    fn available(&self, state: &BackendState) -> bool {
        self.platform_supported
            && state.consecutive_failures < MAX_CONSECUTIVE_FAILURES
            && self.backend.is_available()
    }

    fn is_available(&self) -> bool {
        self.state.lock().is_ok_and(|state| self.available(&state))
    }

    fn read_snapshot(&self) -> Option<RyzenAdjSnapshot> {
        self.read_snapshot_with_clock(Instant::now)
    }

    fn read_snapshot_with_clock(&self, now: impl FnOnce() -> Instant) -> Option<RyzenAdjSnapshot> {
        // 缓存检查与硬件采样使用同一把锁，避免 API 和监控线程同时重复采样。
        let mut state = self.state.lock().ok()?;
        if !self.available(&state) {
            return None;
        }
        let now = now();
        if let Some((sampled_at, snapshot)) = state.cached {
            if now.saturating_duration_since(sampled_at) < STATUS_CACHE_TTL {
                return snapshot;
            }
        }

        let result = self.backend.read_snapshot();
        Self::record_result(&mut state, result.is_ok());
        let snapshot = match result {
            Ok(snapshot) => Some(snapshot),
            Err(error) => {
                tracing::warn!(%error, "PawnIO power status unavailable");
                None
            }
        };
        // 暂时失败同样节流；命中失败缓存不会再次计数或输出日志。
        state.cached = Some((now, snapshot));
        snapshot
    }

    fn apply_scheme(&self, scheme: PowerScheme) -> Result<()> {
        let mut state = lock_ryzen(&self.state, "PawnIO power state")?;
        if !self.available(&state) {
            return Err(CoreError::Unsupported(
                "PawnIO power control unavailable".to_owned(),
            ));
        }
        // 写入可能部分成功，即使返回失败也必须允许后续读取验证实际限制。
        state.cached = None;
        let result = self.backend.apply_scheme(scheme);
        Self::record_result(&mut state, result.is_ok());
        result
    }

    fn record_result(state: &mut BackendState, succeeded: bool) {
        if succeeded {
            state.consecutive_failures = 0;
        } else {
            state.consecutive_failures = state.consecutive_failures.saturating_add(1);
            if state.consecutive_failures == MAX_CONSECUTIVE_FAILURES {
                tracing::warn!("disabled PawnIO power control after 3 consecutive failures");
            }
        }
    }
}

/// 通过 PawnIO 读取和切换功耗；首次访问时才加载官方签名模块。
pub struct ProductionRyzenAdjClient {
    backend: CachedBackend<PawnIoBackend>,
    schemes: Box<[PowerScheme]>,
}

impl ProductionRyzenAdjClient {
    pub fn new(paths: &ServicePaths, platform_supported: bool) -> Self {
        Self {
            backend: CachedBackend::new(platform_supported, PawnIoBackend::new()),
            schemes: load_verified_schemes(paths).into_boxed_slice(),
        }
    }
}

impl fmt::Debug for ProductionRyzenAdjClient {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("ProductionRyzenAdjClient")
            .field("platform_supported", &self.backend.platform_supported)
            .field("schemes", &self.schemes)
            .finish_non_exhaustive()
    }
}

impl RyzenAdjClient for ProductionRyzenAdjClient {
    fn is_supported(&self) -> bool {
        self.backend.is_available()
    }

    fn read_status(&self) -> Option<PowerStatus> {
        self.backend
            .read_snapshot()
            .map(|snapshot| snapshot_to_status(snapshot, &self.schemes))
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

    struct ScriptedBackend {
        outcomes: Mutex<std::collections::VecDeque<bool>>,
        current: Mutex<RyzenAdjSnapshot>,
        reads: AtomicUsize,
        writes: AtomicUsize,
        available: std::sync::atomic::AtomicBool,
        fail_write: std::sync::atomic::AtomicBool,
    }

    impl ScriptedBackend {
        fn new(outcomes: impl IntoIterator<Item = bool>) -> Self {
            Self {
                outcomes: Mutex::new(outcomes.into_iter().collect()),
                current: Mutex::new(snapshot()),
                reads: AtomicUsize::new(0),
                writes: AtomicUsize::new(0),
                available: std::sync::atomic::AtomicBool::new(true),
                fail_write: std::sync::atomic::AtomicBool::new(false),
            }
        }
    }

    impl RyzenAdjBackend for ScriptedBackend {
        fn is_available(&self) -> bool {
            self.available.load(Ordering::SeqCst)
        }

        fn read_snapshot(&self) -> Result<RyzenAdjSnapshot> {
            self.reads.fetch_add(1, Ordering::SeqCst);
            if self.outcomes.lock().unwrap().pop_front().unwrap_or(true) {
                Ok(*self.current.lock().unwrap())
            } else {
                Err(ryzen_error("sample failed"))
            }
        }

        fn apply_scheme(&self, scheme: PowerScheme) -> Result<()> {
            self.writes.fetch_add(1, Ordering::SeqCst);
            let mut current = self.current.lock().unwrap();
            current.stapm_limit = f64::from(scheme.stapm_watts);
            current.fast_limit = f64::from(scheme.fast_watts);
            current.slow_limit = f64::from(scheme.slow_watts);
            if self.fail_write.load(Ordering::SeqCst) {
                Err(ryzen_error("partial write failed"))
            } else {
                Ok(())
            }
        }
    }

    #[test]
    fn successful_cache_expires_at_three_seconds() {
        let backend = Arc::new(ScriptedBackend::new([]));
        let client = CachedBackend::new(true, backend.clone());
        let now = Instant::now();
        assert_eq!(client.read_snapshot_with_clock(|| now), Some(snapshot()));
        assert_eq!(
            client.read_snapshot_with_clock(|| now + STATUS_CACHE_TTL - Duration::from_nanos(1)),
            Some(snapshot())
        );
        assert_eq!(backend.reads.load(Ordering::SeqCst), 1);
        assert_eq!(
            client.read_snapshot_with_clock(|| now + STATUS_CACHE_TTL),
            Some(snapshot())
        );
        assert_eq!(backend.reads.load(Ordering::SeqCst), 2);
    }

    #[test]
    fn concurrent_callers_share_one_sample() {
        let backend = Arc::new(ScriptedBackend::new([]));
        let client = Arc::new(CachedBackend::new(true, backend.clone()));
        let barrier = Arc::new(std::sync::Barrier::new(8));
        let now = Instant::now();
        let workers: Vec<_> = (0..8)
            .map(|_| {
                let client = client.clone();
                let barrier = barrier.clone();
                std::thread::spawn(move || {
                    barrier.wait();
                    client.read_snapshot_with_clock(|| now)
                })
            })
            .collect();
        for worker in workers {
            assert_eq!(worker.join().unwrap(), Some(snapshot()));
        }
        assert_eq!(backend.reads.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn throttled_failures_stop_all_io_after_three_real_attempts() {
        let backend = Arc::new(ScriptedBackend::new([false, false, false]));
        let client = CachedBackend::new(true, backend.clone());
        let now = Instant::now();
        for index in 0..3 {
            let at = now + STATUS_CACHE_TTL * index;
            assert!(client.read_snapshot_with_clock(|| at).is_none());
            assert!(client
                .read_snapshot_with_clock(|| at + Duration::from_secs(1))
                .is_none());
            assert_eq!(backend.reads.load(Ordering::SeqCst), index as usize + 1);
        }
        assert!(!client.is_available());
        assert!(client
            .read_snapshot_with_clock(|| now + STATUS_CACHE_TTL * 10)
            .is_none());
        assert!(client.apply_scheme(TEST_SCHEMES[0]).is_err());
        assert_eq!(backend.reads.load(Ordering::SeqCst), 3);
        assert_eq!(backend.writes.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn successful_sample_resets_failure_count() {
        let backend = Arc::new(ScriptedBackend::new([
            false, false, true, false, false, false,
        ]));
        let client = CachedBackend::new(true, backend.clone());
        let now = Instant::now();
        for index in 0..6 {
            assert!(client.is_available());
            assert_eq!(
                client
                    .read_snapshot_with_clock(|| now + STATUS_CACHE_TTL * index)
                    .is_some(),
                index == 2
            );
        }
        assert!(!client.is_available());
        assert_eq!(backend.reads.load(Ordering::SeqCst), 6);
    }

    #[test]
    fn successful_write_resets_failures_and_invalidates_failure_cache() {
        let backend = Arc::new(ScriptedBackend::new([false, false, true]));
        let client = CachedBackend::new(true, backend.clone());
        let now = Instant::now();
        assert!(client.read_snapshot_with_clock(|| now).is_none());
        assert!(client
            .read_snapshot_with_clock(|| now + STATUS_CACHE_TTL)
            .is_none());
        client.apply_scheme(TEST_SCHEMES[1]).unwrap();
        assert_eq!(client.state.lock().unwrap().consecutive_failures, 0);
        let verified = client
            .read_snapshot_with_clock(|| now + STATUS_CACHE_TTL)
            .unwrap();
        assert_eq!(
            snapshot_to_status(verified, &TEST_SCHEMES).limits,
            TEST_SCHEMES[1]
        );
        assert_eq!(backend.reads.load(Ordering::SeqCst), 3);
    }

    #[test]
    fn writes_invalidate_success_cache_even_after_partial_failure() {
        for fail_write in [false, true] {
            let backend = Arc::new(ScriptedBackend::new([]));
            let client = CachedBackend::new(true, backend.clone());
            let now = Instant::now();
            assert_eq!(client.read_snapshot_with_clock(|| now), Some(snapshot()));
            backend.fail_write.store(fail_write, Ordering::SeqCst);
            assert_eq!(client.apply_scheme(TEST_SCHEMES[1]).is_err(), fail_write);
            let verified = client.read_snapshot_with_clock(|| now).unwrap();
            assert_eq!(
                snapshot_to_status(verified, &TEST_SCHEMES).limits,
                TEST_SCHEMES[1]
            );
            assert_eq!(backend.reads.load(Ordering::SeqCst), 2);
        }
    }

    #[test]
    fn unsupported_platform_never_touches_backend() {
        let backend = Arc::new(ScriptedBackend::new([]));
        let client = CachedBackend::new(false, backend.clone());
        assert!(!client.is_available());
        assert!(client.read_snapshot().is_none());
        assert!(matches!(
            client.apply_scheme(TEST_SCHEMES[0]),
            Err(CoreError::Unsupported(_))
        ));
        assert_eq!(backend.reads.load(Ordering::SeqCst), 0);
        assert_eq!(backend.writes.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn permanent_backend_disable_overrides_success_cache() {
        let backend = Arc::new(ScriptedBackend::new([]));
        let client = CachedBackend::new(true, backend.clone());
        assert!(client.read_snapshot().is_some());
        backend.available.store(false, Ordering::SeqCst);
        assert!(!client.is_available());
        assert!(client.read_snapshot().is_none());
        assert!(client.apply_scheme(TEST_SCHEMES[0]).is_err());
        assert_eq!(backend.reads.load(Ordering::SeqCst), 1);
        assert_eq!(backend.writes.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn three_write_failures_stop_subsequent_reads_and_writes() {
        let backend = Arc::new(ScriptedBackend::new([]));
        let client = CachedBackend::new(true, backend.clone());
        backend.fail_write.store(true, Ordering::SeqCst);
        for _ in 0..MAX_CONSECUTIVE_FAILURES {
            assert!(client.apply_scheme(TEST_SCHEMES[1]).is_err());
        }
        assert!(!client.is_available());
        assert!(client.apply_scheme(TEST_SCHEMES[1]).is_err());
        assert!(client.read_snapshot().is_none());
        assert_eq!(backend.writes.load(Ordering::SeqCst), 3);
        assert_eq!(backend.reads.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn switch_rejects_empty_schemes_without_writing() {
        let client = MockRyzenAdjClient::supported(PowerStatus {
            current_watts: 42.0,
            limit_watts: 55.0,
            scheme_index: Some(0),
            limits: TEST_SCHEMES[0],
        });
        assert!(!switch_to_next_scheme(&client, &[]).success);
        assert!(client.applied_schemes().is_empty());
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
