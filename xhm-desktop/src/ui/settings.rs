//! Settings page model, REST integration, and Windows system controls.
//!
//! Taskbar and System settings share one PUT so the Service restarts against a
//! complete configuration snapshot.

use std::{collections::BTreeMap, net::IpAddr};

use crate::service_client::rest::{RestClient, RestError};
use crate::ui::taskbar_metrics::{SharedTaskbarSettings, TaskbarSettings, TaskbarVisualStyle};
use crate::SettingsWindow;

const APPEARANCE: &str = "Appearance";
const DATA_COLLECTION: &str = "DataCollection";
const MONITORING: &str = "Monitoring";
const SYSTEM: &str = "System";

#[derive(Debug, thiserror::Error)]
pub enum SettingsError {
    #[error("process keywords must be a JSON array")]
    InvalidProcessKeywords,
    #[error("{field} must be a number")]
    InvalidNumber { field: &'static str },
    #[error(transparent)]
    Rest(#[from] RestError),
    #[error("LAN access requires a valid IP whitelist or an enabled access key")]
    UnsafeLanConfiguration,
    #[error("access key is enabled but no key was provided")]
    MissingAccessKey,
    #[error("invalid IP whitelist rule: {rule}")]
    InvalidIpWhitelist { rule: String },
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct SystemSettings {
    pub admin_mode: bool,
    pub start_with_windows: bool,
    pub enable_lan_access: bool,
    pub enable_access_key: bool,
    pub access_key: String,
    pub ip_whitelist: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SettingsDocument {
    pub taskbar: TaskbarSettings,
    pub system: SystemSettings,
}

pub fn settings_document_from_groups(
    groups: &BTreeMap<String, BTreeMap<String, String>>,
) -> SettingsDocument {
    SettingsDocument {
        taskbar: settings_from_groups(groups),
        system: SystemSettings {
            admin_mode: group_bool(groups, MONITORING, "AdminMode"),
            start_with_windows: group_bool(groups, SYSTEM, "StartWithWindows"),
            enable_lan_access: group_bool(groups, SYSTEM, "EnableLanAccess"),
            enable_access_key: group_bool(groups, SYSTEM, "EnableAccessKey"),
            access_key: group_value(groups, SYSTEM, "AccessKey"),
            ip_whitelist: group_value(groups, SYSTEM, "IpWhitelist"),
        },
    }
}

fn group_bool(
    groups: &BTreeMap<String, BTreeMap<String, String>>,
    category: &str,
    key: &str,
) -> bool {
    group_value(groups, category, key)
        .trim()
        .eq_ignore_ascii_case("true")
}

fn group_value(
    groups: &BTreeMap<String, BTreeMap<String, String>>,
    category: &str,
    key: &str,
) -> String {
    groups
        .get(category)
        .and_then(|group| group.get(key))
        .cloned()
        .unwrap_or_default()
}

pub fn settings_from_groups(
    groups: &BTreeMap<String, BTreeMap<String, String>>,
) -> TaskbarSettings {
    let mut settings = TaskbarSettings::default();
    settings.apply_allowed_groups(groups);
    settings
}

pub fn allowed_subset(
    settings: &TaskbarSettings,
) -> Result<BTreeMap<String, BTreeMap<String, String>>, SettingsError> {
    let settings = settings.clone().normalized();
    validate_process_keywords(&settings.process_keywords)?;

    let mut appearance = BTreeMap::new();
    appearance.insert("Opacity".into(), settings.opacity_percent.to_string());

    let mut data_collection = BTreeMap::new();
    data_collection.insert("ProcessKeywords".into(), settings.process_keywords);
    data_collection.insert(
        "TopProcessCount".into(),
        settings.top_process_count.to_string(),
    );
    data_collection.insert(
        "DataRetentionDays".into(),
        settings.data_retention_days.to_string(),
    );

    let mut monitoring = BTreeMap::new();
    monitoring.insert("MonitorCpu".into(), settings.monitor_cpu.to_string());
    monitoring.insert("MonitorMemory".into(), settings.monitor_memory.to_string());
    monitoring.insert("MonitorGpu".into(), settings.monitor_gpu.to_string());
    monitoring.insert("MonitorVram".into(), settings.monitor_vram.to_string());
    monitoring.insert("MonitorPower".into(), settings.monitor_power.to_string());
    monitoring.insert(
        "MonitorNetwork".into(),
        settings.monitor_network.to_string(),
    );
    monitoring.insert(
        "EnableFloatingMode".into(),
        settings.enable_floating_mode.to_string(),
    );
    monitoring.insert(
        "EnableEdgeDockMode".into(),
        settings.enable_edge_dock_mode.to_string(),
    );
    monitoring.insert("DockCpuLabel".into(), settings.dock_cpu_label);
    monitoring.insert("DockMemoryLabel".into(), settings.dock_memory_label);
    monitoring.insert("DockGpuLabel".into(), settings.dock_gpu_label);
    monitoring.insert("DockVramLabel".into(), settings.dock_vram_label);
    monitoring.insert("DockPowerLabel".into(), settings.dock_power_label);
    monitoring.insert("DockUploadLabel".into(), settings.dock_upload_label);
    monitoring.insert("DockDownloadLabel".into(), settings.dock_download_label);
    monitoring.insert("DockColumnGap".into(), settings.dock_column_gap.to_string());
    monitoring.insert(
        "DockVisualStyle".into(),
        settings.dock_visual_style.as_str().into(),
    );

    let mut result = BTreeMap::new();
    result.insert(APPEARANCE.into(), appearance);
    result.insert(DATA_COLLECTION.into(), data_collection);
    result.insert(MONITORING.into(), monitoring);
    Ok(result)
}

pub fn document_subset(
    document: &SettingsDocument,
) -> Result<BTreeMap<String, BTreeMap<String, String>>, SettingsError> {
    validate_system_settings(&document.system)?;
    let mut body = allowed_subset(&document.taskbar)?;
    body.entry(MONITORING.to_string()).or_default().insert(
        "AdminMode".to_string(),
        document.system.admin_mode.to_string(),
    );

    let mut system = BTreeMap::new();
    system.insert(
        "StartWithWindows".to_string(),
        document.system.start_with_windows.to_string(),
    );
    system.insert(
        "EnableLanAccess".to_string(),
        document.system.enable_lan_access.to_string(),
    );
    system.insert(
        "EnableAccessKey".to_string(),
        document.system.enable_access_key.to_string(),
    );
    system.insert("AccessKey".to_string(), document.system.access_key.clone());
    system.insert(
        "IpWhitelist".to_string(),
        document.system.ip_whitelist.clone(),
    );
    body.insert(SYSTEM.to_string(), system);
    Ok(body)
}

pub async fn load_settings(client: &RestClient) -> Result<SettingsDocument, SettingsError> {
    let groups = client.get_settings().await?;
    Ok(settings_document_from_groups(&groups))
}

pub async fn save_settings(
    client: &RestClient,
    document: &SettingsDocument,
) -> Result<(), SettingsError> {
    let body = document_subset(document)?;
    client.put_settings(&body).await?;
    Ok(())
}

fn validate_process_keywords(value: &str) -> Result<(), SettingsError> {
    let value: serde_json::Value =
        serde_json::from_str(value).map_err(|_| SettingsError::InvalidProcessKeywords)?;
    if value.is_array() {
        Ok(())
    } else {
        Err(SettingsError::InvalidProcessKeywords)
    }
}
fn validate_system_settings(settings: &SystemSettings) -> Result<(), SettingsError> {
    if settings.enable_access_key && settings.access_key.trim().is_empty() {
        return Err(SettingsError::MissingAccessKey);
    }
    validate_ip_whitelist(&settings.ip_whitelist)?;
    if settings.enable_lan_access
        && settings.ip_whitelist.trim().is_empty()
        && !settings.enable_access_key
    {
        return Err(SettingsError::UnsafeLanConfiguration);
    }
    Ok(())
}

fn validate_ip_whitelist(raw: &str) -> Result<(), SettingsError> {
    for rule in raw
        .split([',', '\n', '\r'])
        .map(str::trim)
        .filter(|rule| !rule.is_empty())
    {
        let (address, prefix) = match rule.split_once('/') {
            Some((address, prefix)) => {
                let prefix =
                    prefix
                        .parse::<u8>()
                        .map_err(|_| SettingsError::InvalidIpWhitelist {
                            rule: rule.to_string(),
                        })?;
                (address, Some(prefix))
            }
            None => (rule, None),
        };
        let address = address
            .parse::<IpAddr>()
            .map_err(|_| SettingsError::InvalidIpWhitelist {
                rule: rule.to_string(),
            })?;
        let max_prefix = if address.is_ipv4() { 32 } else { 128 };
        if prefix.is_some_and(|prefix| prefix > max_prefix) {
            return Err(SettingsError::InvalidIpWhitelist {
                rule: rule.to_string(),
            });
        }
    }
    Ok(())
}

fn parse_input(value: &str, field: &'static str, min: u8, max: u8) -> Result<u8, SettingsError> {
    value
        .trim()
        .parse::<u8>()
        .map(|value| value.clamp(min, max))
        .map_err(|_| SettingsError::InvalidNumber { field })
}

fn parse_u32_input(
    value: &str,
    field: &'static str,
    min: u32,
    max: u32,
) -> Result<u32, SettingsError> {
    value
        .trim()
        .parse::<u32>()
        .map(|value| value.clamp(min, max))
        .map_err(|_| SettingsError::InvalidNumber { field })
}

pub fn collect_from_window(app: &SettingsWindow) -> Result<TaskbarSettings, SettingsError> {
    let mut settings = TaskbarSettings {
        opacity_percent: parse_input(app.get_opacity_text().as_str(), "Opacity", 20, 100)?,
        process_keywords: app.get_process_keywords().to_string(),
        top_process_count: parse_u32_input(
            app.get_top_process_count().as_str(),
            "TopProcessCount",
            1,
            100,
        )?,
        data_retention_days: parse_u32_input(
            app.get_data_retention_days().as_str(),
            "DataRetentionDays",
            1,
            365,
        )?,
        monitor_cpu: app.get_monitor_cpu(),
        monitor_memory: app.get_monitor_memory(),
        monitor_gpu: app.get_monitor_gpu(),
        monitor_vram: app.get_monitor_vram(),
        monitor_power: app.get_monitor_power(),
        monitor_network: app.get_monitor_network(),
        enable_floating_mode: app.get_enable_floating_mode(),
        enable_edge_dock_mode: app.get_enable_edge_dock_mode(),
        dock_cpu_label: app.get_dock_cpu_label().to_string(),
        dock_memory_label: app.get_dock_memory_label().to_string(),
        dock_gpu_label: app.get_dock_gpu_label().to_string(),
        dock_vram_label: app.get_dock_vram_label().to_string(),
        dock_power_label: app.get_dock_power_label().to_string(),
        dock_upload_label: app.get_dock_upload_label().to_string(),
        dock_download_label: app.get_dock_download_label().to_string(),
        dock_column_gap: parse_input(app.get_dock_column_gap().as_str(), "DockColumnGap", 0, 24)?,
        dock_visual_style: if app.get_bar_visual() {
            TaskbarVisualStyle::Bar
        } else {
            TaskbarVisualStyle::Text
        },
    }
    .normalized();
    validate_process_keywords(&settings.process_keywords)?;
    settings.process_keywords = settings.process_keywords.trim().to_owned();
    Ok(settings)
}
pub fn collect_document_from_window(
    app: &SettingsWindow,
) -> Result<SettingsDocument, SettingsError> {
    let document = SettingsDocument {
        taskbar: collect_from_window(app)?,
        system: SystemSettings {
            admin_mode: app.get_admin_mode(),
            start_with_windows: app.get_start_with_windows(),
            enable_lan_access: app.get_enable_lan_access(),
            enable_access_key: app.get_enable_access_key(),
            access_key: app.get_access_key().to_string(),
            ip_whitelist: app.get_ip_whitelist().to_string(),
        },
    };
    validate_system_settings(&document.system)?;
    Ok(document)
}

pub fn apply_to_window(app: &SettingsWindow, settings: &TaskbarSettings) {
    let settings = settings.clone().normalized();
    app.set_opacity_text(settings.opacity_percent.to_string().into());
    app.set_top_process_count(settings.top_process_count.to_string().into());
    app.set_data_retention_days(settings.data_retention_days.to_string().into());
    app.set_process_keywords(settings.process_keywords.into());
    app.set_monitor_cpu(settings.monitor_cpu);
    app.set_monitor_memory(settings.monitor_memory);
    app.set_monitor_gpu(settings.monitor_gpu);
    app.set_monitor_vram(settings.monitor_vram);
    app.set_monitor_power(settings.monitor_power);
    app.set_monitor_network(settings.monitor_network);
    app.set_enable_floating_mode(settings.enable_floating_mode);
    app.set_enable_edge_dock_mode(settings.enable_edge_dock_mode);
    app.set_dock_cpu_label(settings.dock_cpu_label.into());
    app.set_dock_memory_label(settings.dock_memory_label.into());
    app.set_dock_gpu_label(settings.dock_gpu_label.into());
    app.set_dock_vram_label(settings.dock_vram_label.into());
    app.set_dock_power_label(settings.dock_power_label.into());
    app.set_dock_upload_label(settings.dock_upload_label.into());
    app.set_dock_download_label(settings.dock_download_label.into());
    app.set_dock_column_gap(settings.dock_column_gap.to_string().into());
    app.set_bar_visual(matches!(
        settings.dock_visual_style,
        TaskbarVisualStyle::Bar
    ));
}
pub fn apply_system_to_window(app: &SettingsWindow, settings: &SystemSettings) {
    app.set_admin_mode(settings.admin_mode);
    app.set_start_with_windows(settings.start_with_windows);
    app.set_enable_lan_access(settings.enable_lan_access);
    app.set_enable_access_key(settings.enable_access_key);
    app.set_access_key(settings.access_key.clone().into());
    app.set_ip_whitelist(settings.ip_whitelist.clone().into());
}

#[cfg(windows)]
enum SettingsAsyncResult {
    Loaded(Result<SettingsDocument, String>),
    Saved(Result<SettingsDocument, String>),
}

#[cfg(windows)]
pub struct SettingsUiRuntime {
    _timer: slint::Timer,
}

#[cfg(windows)]
pub fn install_runtime(
    app: &SettingsWindow,
    taskbar_settings: SharedTaskbarSettings,
) -> SettingsUiRuntime {
    use std::cell::RefCell;
    use std::rc::Rc;

    use slint::ComponentHandle;

    let (sender, receiver) = std::sync::mpsc::channel();
    let receiver = Rc::new(RefCell::new(receiver));
    let current_system = Rc::new(RefCell::new(SystemSettings::default()));
    let weak = app.as_weak();

    {
        let sender = sender.clone();
        let weak = weak.clone();
        app.on_load_settings(move || {
            if let Some(app) = weak.upgrade() {
                app.set_status_text("Loading settings...".into());
            }
            spawn_settings_request(sender.clone(), SettingsRequest::Load);
        });
    }

    {
        let sender = sender.clone();
        let weak = weak.clone();
        let current_system = Rc::clone(&current_system);
        app.on_save_settings(move || {
            let Some(app) = weak.upgrade() else {
                return;
            };
            match collect_document_from_window(&app) {
                Ok(document) => {
                    app.set_status_text("Saving settings...".into());
                    spawn_settings_request(
                        sender.clone(),
                        SettingsRequest::Save {
                            document: Box::new(document),
                            previous_system: current_system.borrow().clone(),
                        },
                    );
                }
                Err(error) => app.set_status_text(format!("Save rejected: {error}").into()),
            }
        });
    }

    {
        let weak = weak.clone();
        app.on_close_window(move || {
            if let Some(app) = weak.upgrade() {
                let _ = app.hide();
            }
        });
    }

    let timer = slint::Timer::default();
    {
        let receiver = Rc::clone(&receiver);
        let current_system = Rc::clone(&current_system);
        let weak = weak.clone();
        timer.start(
            slint::TimerMode::Repeated,
            std::time::Duration::from_millis(100),
            move || loop {
                let Ok(result) = receiver.borrow().try_recv() else {
                    break;
                };
                let Some(app) = weak.upgrade() else {
                    continue;
                };
                match result {
                    SettingsAsyncResult::Loaded(Ok(mut document)) => {
                        document.system.admin_mode =
                            crate::system_controls::is_admin_mode_enabled();
                        document.system.start_with_windows =
                            crate::system_controls::is_startup_enabled();
                        apply_to_window(&app, &document.taskbar);
                        apply_system_to_window(&app, &document.system);
                        app.set_local_ip(get_local_ip().into());
                        *current_system.borrow_mut() = document.system;
                        app.set_status_text("Settings loaded.".into());
                    }
                    SettingsAsyncResult::Loaded(Err(error)) => {
                        app.set_status_text(format!("Settings load failed: {error}").into());
                    }
                    SettingsAsyncResult::Saved(Ok(document)) => {
                        *taskbar_settings
                            .lock()
                            .unwrap_or_else(std::sync::PoisonError::into_inner) =
                            document.taskbar.clone();
                        *current_system.borrow_mut() = document.system;
                        app.set_status_text("Settings saved.".into());
                    }
                    SettingsAsyncResult::Saved(Err(error)) => {
                        app.set_status_text(format!("Settings save failed: {error}").into());
                    }
                }
            },
        );
    }

    spawn_settings_request(sender, SettingsRequest::Load);
    SettingsUiRuntime { _timer: timer }
}

#[cfg(windows)]
fn apply_system_controls(
    previous: &SystemSettings,
    current: &SystemSettings,
) -> Result<(), String> {
    let admin_changed = crate::system_controls::is_admin_mode_enabled() != current.admin_mode;
    if admin_changed {
        crate::system_controls::apply_admin_mode(current.admin_mode)
            .map_err(|error| error.to_string())?;
    }

    if crate::system_controls::is_startup_enabled() != current.start_with_windows {
        crate::system_controls::set_startup(current.start_with_windows)
            .map_err(|error| error.to_string())?;
    }

    let network_changed = previous.enable_lan_access != current.enable_lan_access
        || previous.enable_access_key != current.enable_access_key
        || previous.access_key != current.access_key
        || previous.ip_whitelist != current.ip_whitelist;
    if network_changed && !admin_changed {
        crate::system_controls::restart_service_for_settings()
            .map_err(|error| error.to_string())?;
    }

    if network_changed {
        if current.enable_lan_access {
            crate::system_controls::wait_for_web_gateway().map_err(|error| error.to_string())?;
        }
        crate::system_controls::configure_firewall(current.enable_lan_access, 35_180)
            .map_err(|error| error.to_string())?;
    }
    Ok(())
}

#[cfg(windows)]
fn get_local_ip() -> String {
    use std::process::Command;
    Command::new("powershell")
        .args([
            "-NoProfile", "-NonInteractive", "-Command",
            "(Get-NetIPAddress -AddressFamily IPv4 -Type Unicast | Where-Object { $_.PrefixOrigin -eq 'Dhcp' -or $_.PrefixOrigin -eq 'Manual' } | Where-Object { $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -ne '127.0.0.1' } | Select-Object -First 1).IPAddress",
        ])
        .output()
        .ok()
        .and_then(|o| {
            if o.status.success() {
                let ip = String::from_utf8_lossy(&o.stdout).trim().to_string();
                if ip.is_empty() { None } else { Some(ip) }
            } else { None }
        })
        .unwrap_or_else(|| "unavailable".to_string())
}

#[cfg(windows)]
enum SettingsRequest {
    Load,
    Save {
        document: Box<SettingsDocument>,
        previous_system: SystemSettings,
    },
}

#[cfg(windows)]
enum SettingsRequestKind {
    Load,
    Save,
}

#[cfg(windows)]
fn request_result_kind(request: &SettingsRequest) -> SettingsRequestKind {
    match request {
        SettingsRequest::Load => SettingsRequestKind::Load,
        SettingsRequest::Save { .. } => SettingsRequestKind::Save,
    }
}

#[cfg(windows)]
fn spawn_settings_request(
    sender: std::sync::mpsc::Sender<SettingsAsyncResult>,
    request: SettingsRequest,
) {
    let _ = std::thread::Builder::new()
        .name("xhm-settings".into())
        .spawn(move || {
            let fallback = request_result_kind(&request);
            let result = tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
                .map_err(|error| error.to_string())
                .and_then(|runtime| {
                    runtime.block_on(async move {
                        let config = crate::config::Config::load().await;
                        let client = RestClient::new(&config)
                            .map_err(|error: RestError| error.to_string())?;
                        match request {
                            SettingsRequest::Load => load_settings(&client)
                                .await
                                .map(|settings| SettingsAsyncResult::Loaded(Ok(settings)))
                                .map_err(|error| error.to_string()),
                            SettingsRequest::Save {
                                document,
                                previous_system,
                            } => {
                                save_settings(&client, &document)
                                    .await
                                    .map_err(|error| error.to_string())?;
                                apply_system_controls(&previous_system, &document.system)?;
                                Ok(SettingsAsyncResult::Saved(Ok(*document)))
                            }
                        }
                    })
                });
            let result = match (fallback, result) {
                (_, Ok(result)) => result,
                (SettingsRequestKind::Load, Err(error)) => SettingsAsyncResult::Loaded(Err(error)),
                (SettingsRequestKind::Save, Err(error)) => SettingsAsyncResult::Saved(Err(error)),
            };
            let _ = sender.send(result);
        });
}

#[cfg(test)]
mod tests {
    use super::*;
    use wiremock::matchers::{body_partial_json, method, path};
    use wiremock::{Mock, MockServer, ResponseTemplate};

    fn client_for(server: &MockServer) -> RestClient {
        RestClient::with_client(server.uri(), reqwest::Client::new())
    }

    #[test]
    fn taskbar_subset_does_not_invent_system_keys() {
        let body = allowed_subset(&TaskbarSettings::default()).unwrap();
        assert!(!body.contains_key(SYSTEM));
        assert!(!body
            .get(MONITORING)
            .is_some_and(|group| group.contains_key("AdminMode")));
    }

    #[test]
    fn invalid_keywords_are_rejected_before_any_put() {
        let settings = TaskbarSettings {
            process_keywords: "not-an-array".into(),
            ..TaskbarSettings::default()
        };
        assert!(matches!(
            allowed_subset(&settings),
            Err(SettingsError::InvalidProcessKeywords)
        ));
    }

    #[test]
    fn unsafe_lan_and_invalid_whitelist_are_rejected() {
        let unsafe_document = SettingsDocument {
            taskbar: TaskbarSettings::default(),
            system: SystemSettings {
                enable_lan_access: true,
                ..SystemSettings::default()
            },
        };
        assert!(matches!(
            document_subset(&unsafe_document),
            Err(SettingsError::UnsafeLanConfiguration)
        ));

        let invalid_document = SettingsDocument {
            taskbar: TaskbarSettings::default(),
            system: SystemSettings {
                ip_whitelist: "192.168.1.0/40".to_string(),
                ..SystemSettings::default()
            },
        };
        assert!(matches!(
            document_subset(&invalid_document),
            Err(SettingsError::InvalidIpWhitelist { .. })
        ));
    }

    #[tokio::test]
    async fn settings_get_and_put_include_validated_system_controls() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path("/api/v1/config/settings"))
            .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
                "Appearance": {"Opacity": "105", "Ignored": "yes"},
                "DataCollection": {
                    "ProcessKeywords": "[\"gpu\"]",
                    "TopProcessCount": "150",
                    "DataRetentionDays": "400"
                },
                "Monitoring": {"DockVisualStyle": "Text", "DockColumnGap": "29", "MonitorCpu": "false", "AdminMode": "true"},
                "System": {
                    "StartWithWindows": "true",
                    "EnableLanAccess": "true",
                    "EnableAccessKey": "true",
                    "AccessKey": "secret",
                    "IpWhitelist": ""
                }
            })))
            .mount(&server)
            .await;
        Mock::given(method("PUT"))
            .and(path("/api/v1/config/settings"))
            .and(body_partial_json(serde_json::json!({
                "Appearance": {"Opacity": "100"},
                "DataCollection": {
                    "ProcessKeywords": "[\"gpu\"]",
                    "TopProcessCount": "100",
                    "DataRetentionDays": "365"
                },
                "Monitoring": {
                    "DockVisualStyle": "Text",
                    "DockColumnGap": "24",
                    "MonitorCpu": "false",
                    "AdminMode": "true"
                },
                "System": {
                    "StartWithWindows": "true",
                    "EnableLanAccess": "true",
                    "EnableAccessKey": "true",
                    "AccessKey": "secret",
                    "IpWhitelist": ""
                }
            })))
            .respond_with(
                ResponseTemplate::new(200).set_body_json(serde_json::json!({"updatedCount": 1})),
            )
            .mount(&server)
            .await;

        let client = client_for(&server);
        let document = load_settings(&client).await.unwrap();
        assert_eq!(document.taskbar.opacity_percent, 100);
        assert_eq!(document.taskbar.top_process_count, 100);
        assert_eq!(document.taskbar.data_retention_days, 365);
        assert_eq!(document.taskbar.dock_column_gap, 24);
        assert_eq!(document.taskbar.dock_visual_style, TaskbarVisualStyle::Text);
        assert!(!document.taskbar.monitor_cpu);
        assert!(document.system.admin_mode);
        assert!(document.system.start_with_windows);
        assert!(document.system.enable_lan_access);
        assert!(document.system.enable_access_key);
        assert_eq!(document.system.access_key, "secret");
        save_settings(&client, &document).await.unwrap();
    }
}
