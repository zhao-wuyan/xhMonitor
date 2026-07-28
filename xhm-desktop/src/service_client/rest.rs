//! 既有 REST 客户端（TASK-003）。
//!
//! 仅绑定 xhm-service 现有路由（精确 path + 服务端大小写 + shape），不创建新 API：
//!
//! | 方法 | 路由 | xhm-service 源 |
//! |------|------|----------------|
//! | GET  | `/api/v1/config/health` | `api/config.rs:208-233` |
//! | GET / PUT | `/api/v1/config/settings` | `api/config.rs:235-331` |
//! | GET  | `/api/v1/config/admin-status` | `api/config.rs:355-370` |
//! | GET  | `/api/v1/power/warmup` | `api/power.rs:66-82` |
//! | POST | `/api/v1/power/scheme/next` | `api/power.rs:89-181` |
//! | GET / POST | `/api/v1/widgetconfig` | `api/widget.rs:60-110` |
//!
//! - settings 使用 `BTreeMap<String, BTreeMap<String, String>>`（对齐
//!   config.rs:19 的 `SettingsPayload` 与 get_settings 的 grouped shape）。
//! - WidgetSettings / MetricClickConfig 直接复用 xhm-core 的 serde 表示。
//! - non-2xx 保留 status 与 body；不修改 xhm-service。

use std::collections::BTreeMap;
use std::time::Duration;

use serde::{de::DeserializeOwned, Serialize};
use xhm_core::models::{MetricClickConfig, PowerScheme, PowerWarmupStatus, WidgetSettings};

use crate::config::Config;

/// health 端点 body（config.rs:213-231：200 Healthy 或 503 Unhealthy）。
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HealthStatus {
    pub status: String,
    #[serde(default)]
    pub timestamp: Option<String>,
    #[serde(default)]
    pub database: Option<String>,
    #[serde(default)]
    pub error: Option<String>,
}

/// `/api/v1/power/scheme/next` 200 响应（power.rs:174-181 SwitchResponse，camelCase）。
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PowerSwitchResponse {
    pub message: String,
    #[serde(default)]
    pub previous_scheme_index: Option<i32>,
    pub new_scheme_index: i32,
    pub scheme: PowerScheme,
}

/// `/api/v1/config/admin-status` body（config.rs:359-370）。
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AdminStatus {
    pub is_admin: bool,
    pub message: String,
}

/// REST 调用错误：transport 错误或 non-2xx 响应（保留 status + body 用于诊断）。
#[derive(Debug, thiserror::Error)]
pub enum RestError {
    #[error("transport: {0}")]
    Transport(#[from] reqwest::Error),
    #[error("body decode: {0}")]
    Decode(#[from] serde_json::Error),
    #[error("{status} {url}: {body}")]
    Status {
        status: u16,
        url: String,
        body: String,
    },
}

impl RestError {
    /// non-2xx 的 HTTP status code（transport / decode 错误返回 None）。
    pub fn status_code(&self) -> Option<u16> {
        match self {
            RestError::Status { status, .. } => Some(*status),
            _ => None,
        }
    }
}

/// 既有 REST 客户端。
#[derive(Clone)]
pub struct RestClient {
    api_base: String,
    client: reqwest::Client,
}

impl std::fmt::Debug for RestClient {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("RestClient")
            .field("api_base", &self.api_base)
            .finish_non_exhaustive()
    }
}

const DEFAULT_REQUEST_TIMEOUT: Duration = Duration::from_secs(5);

impl RestClient {
    /// 从 [`Config`] 与默认 timeout 构造。
    pub fn new(config: &Config) -> Result<Self, RestError> {
        let client = reqwest::Client::builder()
            .timeout(DEFAULT_REQUEST_TIMEOUT)
            .build()?;
        Ok(Self {
            api_base: config.api_base.clone(),
            client,
        })
    }

    /// 测试 / 受控入口：显式 base + client。
    pub fn with_client(api_base: impl Into<String>, client: reqwest::Client) -> Self {
        Self {
            api_base: api_base.into(),
            client,
        }
    }

    fn url(&self, path: &str) -> String {
        format!("{}{path}", self.api_base)
    }

    async fn get_json<T: DeserializeOwned>(&self, path: &str) -> Result<T, RestError> {
        let response = self.client.get(self.url(path)).send().await?;
        let status = response.status();
        if status.is_success() {
            Ok(response.json().await?)
        } else {
            Err(status_error(response, status, self.url(path)).await)
        }
    }

    async fn put_json<B: Serialize, T: DeserializeOwned>(
        &self,
        path: &str,
        body: &B,
    ) -> Result<T, RestError> {
        let response = self.client.put(self.url(path)).json(body).send().await?;
        let status = response.status();
        if status.is_success() {
            Ok(response.json().await?)
        } else {
            Err(status_error(response, status, self.url(path)).await)
        }
    }

    async fn post_json_response<B: Serialize, T: DeserializeOwned>(
        &self,
        path: &str,
        body: &B,
    ) -> Result<T, RestError> {
        let response = self.client.post(self.url(path)).json(body).send().await?;
        let status = response.status();
        if status.is_success() {
            Ok(response.json().await?)
        } else {
            Err(status_error(response, status, self.url(path)).await)
        }
    }

    /// `GET /api/v1/config/health`。
    pub async fn health(&self) -> Result<HealthStatus, RestError> {
        self.get_json("/api/v1/config/health").await
    }

    /// `GET /api/v1/config/settings` → `BTreeMap<category, BTreeMap<key, value>>`。
    pub async fn get_settings(
        &self,
    ) -> Result<BTreeMap<String, BTreeMap<String, String>>, RestError> {
        self.get_json("/api/v1/config/settings").await
    }

    /// `PUT /api/v1/config/settings`，body 即 grouped BTreeMap（config.rs:19 SettingsPayload）。
    pub async fn put_settings(
        &self,
        settings: &BTreeMap<String, BTreeMap<String, String>>,
    ) -> Result<serde_json::Value, RestError> {
        self.put_json("/api/v1/config/settings", settings).await
    }

    /// `GET /api/v1/config/admin-status`。
    pub async fn admin_status(&self) -> Result<AdminStatus, RestError> {
        self.get_json("/api/v1/config/admin-status").await
    }

    /// `GET /api/v1/power/warmup`。
    pub async fn warmup(&self) -> Result<PowerWarmupStatus, RestError> {
        self.get_json("/api/v1/power/warmup").await
    }

    /// `POST /api/v1/power/scheme/next`。
    pub async fn power_next_scheme(&self) -> Result<PowerSwitchResponse, RestError> {
        // 空对象 body：对齐 C# 客户端无参调用；axum 接受空 body（api/power.rs:89-181 不读 body）。
        self.post_json_response("/api/v1/power/scheme/next", &serde_json::Value::Null)
            .await
    }

    /// `GET /api/v1/widgetconfig`。
    pub async fn get_widget_config(&self) -> Result<WidgetSettings, RestError> {
        self.get_json("/api/v1/widgetconfig").await
    }

    /// `POST /api/v1/widgetconfig`。
    pub async fn put_widget_config(
        &self,
        settings: &WidgetSettings,
    ) -> Result<serde_json::Value, RestError> {
        self.post_json_response("/api/v1/widgetconfig", settings)
            .await
    }

    /// `POST /api/v1/widgetconfig/{metric_id}`。
    pub async fn put_metric_config(
        &self,
        metric_id: &str,
        config: &MetricClickConfig,
    ) -> Result<serde_json::Value, RestError> {
        self.post_json_response(&format!("/api/v1/widgetconfig/{metric_id}"), config)
            .await
    }
}

async fn status_error(
    response: reqwest::Response,
    status: reqwest::StatusCode,
    url: String,
) -> RestError {
    let body = response.text().await.unwrap_or_default();
    RestError::Status {
        status: status.as_u16(),
        url,
        body,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use wiremock::matchers::{body_partial_json, body_string, header, method, path};
    use wiremock::{Mock, MockServer, ResponseTemplate};

    fn client_for(server: &MockServer) -> RestClient {
        let client = reqwest::Client::builder()
            .timeout(Duration::from_secs(2))
            .build()
            .unwrap();
        RestClient::with_client(server.uri(), client)
    }

    #[tokio::test]
    async fn health_returns_healthy_body() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path("/api/v1/config/health"))
            .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
                "status": "Healthy",
                "timestamp": "2026-07-27T00:00:00Z",
                "database": "Connected"
            })))
            .mount(&server)
            .await;

        let rest = client_for(&server);
        let health = rest.health().await.unwrap();
        assert_eq!(health.status, "Healthy");
        assert_eq!(health.database.as_deref(), Some("Connected"));
    }

    #[tokio::test]
    async fn health_503_preserves_status_and_body() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path("/api/v1/config/health"))
            .respond_with(
                ResponseTemplate::new(503).set_body_json(
                    serde_json::json!({"status": "Unhealthy", "error": "db locked"}),
                ),
            )
            .mount(&server)
            .await;

        let rest = client_for(&server);
        let error = rest.health().await.unwrap_err();
        assert_eq!(error.status_code(), Some(503));
        assert!(format!("{error}").contains("Unhealthy"));
    }

    #[tokio::test]
    async fn put_settings_sends_grouped_btreemap_body() {
        let server = MockServer::start().await;
        let mut expected = BTreeMap::new();
        let mut appear = BTreeMap::new();
        appear.insert("CpuVisible".to_string(), "true".to_string());
        expected.insert("Appearance".to_string(), appear);

        Mock::given(method("PUT"))
            .and(path("/api/v1/config/settings"))
            .and(body_partial_json(serde_json::json!({
                "Appearance": {"CpuVisible": "true"}
            })))
            .and(header("content-type", "application/json"))
            .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
                "message": "ok", "updatedCount": 1, "insertedCount": 0
            })))
            .mount(&server)
            .await;

        let rest = client_for(&server);
        let result = rest.put_settings(&expected).await.unwrap();
        assert_eq!(result["updatedCount"], 1);
    }

    #[tokio::test]
    async fn get_admin_status_matches_service_shape() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path("/api/v1/config/admin-status"))
            .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
                "isAdmin": false,
                "message": "Service 未以管理员权限运行"
            })))
            .mount(&server)
            .await;

        let rest = client_for(&server);
        let status = rest.admin_status().await.unwrap();
        assert!(!status.is_admin);
        assert!(status.message.contains("管理员"));
    }

    #[tokio::test]
    async fn power_warmup_matches_camel_case_shape() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path("/api/v1/power/warmup"))
            .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
                "enabled": true,
                "deviceName": "AMD Ryzen AI MAX+ 395",
                "reason": null
            })))
            .mount(&server)
            .await;

        let rest = client_for(&server);
        let warmup = rest.warmup().await.unwrap();
        assert!(warmup.enabled);
        assert_eq!(warmup.device_name.as_deref(), Some("AMD Ryzen AI MAX+ 395"));
    }

    #[tokio::test]
    async fn power_next_scheme_posts_and_parses_switch_response() {
        let server = MockServer::start().await;
        Mock::given(method("POST"))
            .and(path("/api/v1/power/scheme/next"))
            .and(body_string("null"))
            .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
                "message": "OK",
                "previousSchemeIndex": 0,
                "newSchemeIndex": 1,
                "scheme": {"stapmWatts": 54, "fastWatts": 45, "slowWatts": 25}
            })))
            .mount(&server)
            .await;

        let rest = client_for(&server);
        let switch = rest.power_next_scheme().await.unwrap();
        assert_eq!(switch.new_scheme_index, 1);
        assert_eq!(switch.scheme.stapm_watts, 54);
    }

    #[tokio::test]
    async fn widgetconfig_get_uses_widget_settings_shape() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path("/api/v1/widgetconfig"))
            .respond_with(ResponseTemplate::new(200).set_body_json(serde_json::json!({
                "enableMetricClick": true,
                "metricClickActions": {
                    "cpu": {"enabled": true, "action": "none", "parameters": null}
                }
            })))
            .mount(&server)
            .await;

        let rest = client_for(&server);
        let widget = rest.get_widget_config().await.unwrap();
        assert!(widget.enable_metric_click);
        assert!(widget.metric_click_actions.get("cpu").unwrap().enabled);
    }

    #[tokio::test]
    async fn put_metric_config_posts_camel_case_body_to_metric_path() {
        let server = MockServer::start().await;
        Mock::given(method("POST"))
            .and(path("/api/v1/widgetconfig/cpu"))
            .and(body_partial_json(serde_json::json!({
                "enabled": true, "action": "togglePowerMode"
            })))
            .respond_with(
                ResponseTemplate::new(200).set_body_json(serde_json::json!({"success": true})),
            )
            .mount(&server)
            .await;

        let rest = client_for(&server);
        let config = MetricClickConfig {
            enabled: true,
            action: "togglePowerMode".to_string(),
            parameters: None,
        };
        let value = rest.put_metric_config("cpu", &config).await.unwrap();
        assert_eq!(value["success"], true);
    }

    #[tokio::test]
    async fn transport_failure_maps_to_rest_error() {
        // 指向未监听端口 → 连接拒绝。
        let client = reqwest::Client::builder()
            .timeout(Duration::from_millis(200))
            .build()
            .unwrap();
        let rest = RestClient::with_client("http://127.0.0.1:1", client);
        let error = rest.health().await.unwrap_err();
        assert!(matches!(error, RestError::Transport(_)));
    }
}
