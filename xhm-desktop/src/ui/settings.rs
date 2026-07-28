//! Settings page model and allowed-subset REST integration.
//!
//! The desktop only writes the explicitly permitted configuration keys. Deferred
//! system controls are intentionally absent from both the model and request body.

use std::collections::BTreeMap;

use crate::service_client::rest::{RestClient, RestError};
use crate::ui::taskbar_metrics::{SharedTaskbarSettings, TaskbarSettings, TaskbarVisualStyle};
use crate::SettingsWindow;

const APPEARANCE: &str = "Appearance";
const DATA_COLLECTION: &str = "DataCollection";
const MONITORING: &str = "Monitoring";

#[derive(Debug, thiserror::Error)]
pub enum SettingsError {
    #[error("process keywords must be a JSON array")]
    InvalidProcessKeywords,
    #[error("{field} must be a number")]
    InvalidNumber { field: &'static str },
    #[error(transparent)]
    Rest(#[from] RestError),
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

pub async fn load_settings(client: &RestClient) -> Result<TaskbarSettings, SettingsError> {
    let groups = client.get_settings().await?;
    Ok(settings_from_groups(&groups))
}

pub async fn save_settings(
    client: &RestClient,
    settings: &TaskbarSettings,
) -> Result<(), SettingsError> {
    let body = allowed_subset(settings)?;
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

fn parse_input(value: &str, field: &'static str, min: u8, max: u8) -> Result<u8, SettingsError> {
    value
        .trim()
        .parse::<u8>()
        .map(|value| value.clamp(min, max))
        .map_err(|_| SettingsError::InvalidNumber { field })
}

pub fn collect_from_window(app: &SettingsWindow) -> Result<TaskbarSettings, SettingsError> {
    let mut settings = TaskbarSettings {
        opacity_percent: parse_input(app.get_opacity_text().as_str(), "Opacity", 20, 100)?,
        process_keywords: app.get_process_keywords().to_string(),
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

pub fn apply_to_window(app: &SettingsWindow, settings: &TaskbarSettings) {
    let settings = settings.clone().normalized();
    app.set_opacity_text(settings.opacity_percent.to_string().into());
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

#[cfg(windows)]
enum SettingsAsyncResult {
    Loaded(Result<TaskbarSettings, String>),
    Saved(Result<TaskbarSettings, String>),
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
        app.on_save_settings(move || {
            let Some(app) = weak.upgrade() else {
                return;
            };
            match collect_from_window(&app) {
                Ok(settings) => {
                    app.set_status_text("Saving allowed settings...".into());
                    spawn_settings_request(
                        sender.clone(),
                        SettingsRequest::Save(Box::new(settings)),
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
        let weak = weak.clone();
        timer.start(
            slint::TimerMode::Repeated,
            std::time::Duration::from_millis(100),
            move || loop {
                let result = receiver.borrow().try_recv();
                let Ok(result) = result else {
                    break;
                };
                let Some(app) = weak.upgrade() else {
                    continue;
                };
                match result {
                    SettingsAsyncResult::Loaded(Ok(settings)) => {
                        apply_to_window(&app, &settings);
                        app.set_status_text(
                            "Settings loaded. System controls remain P3-only.".into(),
                        );
                        *taskbar_settings
                            .lock()
                            .unwrap_or_else(std::sync::PoisonError::into_inner) = settings;
                    }
                    SettingsAsyncResult::Loaded(Err(error)) => {
                        app.set_status_text(format!("Settings load failed: {error}").into());
                    }
                    SettingsAsyncResult::Saved(Ok(settings)) => {
                        *taskbar_settings
                            .lock()
                            .unwrap_or_else(std::sync::PoisonError::into_inner) = settings;
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
enum SettingsRequest {
    Load,
    Save(Box<TaskbarSettings>),
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
        SettingsRequest::Save(_) => SettingsRequestKind::Save,
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
                            SettingsRequest::Save(settings) => save_settings(&client, &settings)
                                .await
                                .map(|()| SettingsAsyncResult::Saved(Ok(*settings)))
                                .map_err(|error| error.to_string()),
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
    fn allowed_subset_excludes_deferred_system_controls() {
        let body = allowed_subset(&TaskbarSettings::default()).unwrap();
        let all_keys = body
            .values()
            .flat_map(|group| group.keys())
            .collect::<Vec<_>>();
        for forbidden in ["AdminMode", "Startup", "LAN", "Firewall", "System"] {
            assert!(!all_keys.iter().any(|key| key.as_str() == forbidden));
        }
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

    #[tokio::test]
    async fn settings_get_and_put_use_only_allowed_subset() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path("/api/v1/config/settings"))
            .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
                "Appearance": {"Opacity": "105", "Ignored": "yes"},
                "DataCollection": {"ProcessKeywords": "[\"gpu\"]"},
                "Monitoring": {"DockVisualStyle": "Text", "DockColumnGap": "29", "MonitorCpu": "false", "AdminMode": "true"},
                "System": {"Firewall": "true"}
            })))
            .mount(&server)
            .await;
        Mock::given(method("PUT"))
            .and(path("/api/v1/config/settings"))
            .and(body_partial_json(serde_json::json!({
                "Appearance": {"Opacity": "100"},
                "DataCollection": {"ProcessKeywords": "[\"gpu\"]"},
                "Monitoring": {"DockVisualStyle": "Text", "DockColumnGap": "24", "MonitorCpu": "false"}
            })))
            .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({"updatedCount": 1})))
            .mount(&server)
            .await;

        let client = client_for(&server);
        let settings = load_settings(&client).await.unwrap();
        assert_eq!(settings.opacity_percent, 100);
        assert_eq!(settings.dock_column_gap, 24);
        assert_eq!(settings.dock_visual_style, TaskbarVisualStyle::Text);
        assert!(!settings.monitor_cpu);
        save_settings(&client, &settings).await.unwrap();
    }
}
