use std::{
    collections::{BTreeMap, HashSet},
    sync::LazyLock,
};

use axum::{
    extract::{rejection::JsonRejection, Path, State},
    http::StatusCode,
    response::{IntoResponse, Response},
    routing::{delete, get, put},
    Json, Router,
};
use serde::Deserialize;
use serde_json::json;
use xhm_core::models::{AlertConfiguration, MetricMetadata, SettingsUpsertCounts};

use crate::state::AppState;

type SettingsPayload = Json<BTreeMap<String, BTreeMap<String, String>>>;

static METRIC_METADATA: LazyLock<[MetricMetadata; 4]> = LazyLock::new(|| {
    [
        MetricMetadata {
            metric_id: "cpu".to_owned(),
            display_name: "CPU Usage".to_owned(),
            unit: "%".to_owned(),
            metric_type: "Percentage".to_owned(),
            category: Some("Percentage".to_owned()),
            color: Some("#3b82f6".to_owned()),
            icon: Some("Cpu".to_owned()),
        },
        MetricMetadata {
            metric_id: "memory".to_owned(),
            display_name: "Memory Usage".to_owned(),
            unit: "MB".to_owned(),
            metric_type: "Size".to_owned(),
            category: Some("Size".to_owned()),
            color: Some("#10b981".to_owned()),
            icon: Some("MemoryStick".to_owned()),
        },
        MetricMetadata {
            metric_id: "gpu".to_owned(),
            display_name: "GPU Usage".to_owned(),
            unit: "%".to_owned(),
            metric_type: "Percentage".to_owned(),
            category: Some("Percentage".to_owned()),
            color: Some("#8b5cf6".to_owned()),
            icon: Some("Gpu".to_owned()),
        },
        MetricMetadata {
            metric_id: "vram".to_owned(),
            display_name: "VRAM Usage".to_owned(),
            unit: "MB".to_owned(),
            metric_type: "Size".to_owned(),
            category: Some("Size".to_owned()),
            color: Some("#f59e0b".to_owned()),
            icon: Some("HardDrive".to_owned()),
        },
    ]
});

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct UpdateSettingRequest {
    #[serde(default)]
    value: String,
}

pub fn router() -> Router<AppState> {
    Router::new()
        .route("/api/v1/config", get(get_config))
        .route("/api/v1/config/alerts", get(get_alerts).post(update_alert))
        .route("/api/v1/config/alerts/:id", delete(delete_alert))
        .route("/api/v1/config/metrics", get(get_metrics))
        .route("/api/v1/config/health", get(get_health))
        .route(
            "/api/v1/config/settings",
            get(get_settings).put(update_settings),
        )
        .route(
            "/api/v1/config/settings/:category/:key",
            put(update_setting),
        )
        .route("/api/v1/config/admin-status", get(get_admin_status))
}

async fn run_store<T, F>(operation: F) -> Result<T, String>
where
    T: Send + 'static,
    F: FnOnce() -> xhm_core::Result<T> + Send + 'static,
{
    match tokio::task::spawn_blocking(operation).await {
        Ok(Ok(value)) => Ok(value),
        Ok(Err(error)) => Err(error.to_string()),
        Err(error) => Err(format!("blocking store task failed: {error}")),
    }
}

fn bad_json(error: JsonRejection) -> Response {
    (
        StatusCode::BAD_REQUEST,
        Json(json!({ "error": error.to_string() })),
    )
        .into_response()
}

fn internal_error(error: String) -> Response {
    tracing::error!(error = %error, "configuration request failed");
    (
        StatusCode::INTERNAL_SERVER_ERROR,
        Json(json!({ "error": error })),
    )
        .into_response()
}

async fn get_config(State(state): State<AppState>) -> Json<serde_json::Value> {
    let runtime = state.runtime.read().await;

    Json(json!({
        "monitor": {
            "intervalSeconds": runtime.interval_seconds,
            "systemUsageIntervalSeconds": 1,
            "keywords": runtime.process_keywords.clone(),
        },
        "metricProviders": {
            "pluginDirectory": runtime.plugin_directory.to_string_lossy().into_owned(),
        },
    }))
}

async fn get_alerts(State(state): State<AppState>) -> Response {
    let store = state.store.clone();
    match run_store(move || store.list_alerts()).await {
        Ok(mut alerts) => {
            alerts.sort_by(|left, right| left.metric_id.cmp(&right.metric_id));
            Json(alerts).into_response()
        }
        Err(error) => internal_error(error),
    }
}

async fn update_alert(
    State(state): State<AppState>,
    payload: Result<Json<AlertConfiguration>, JsonRejection>,
) -> Response {
    let Json(mut alert) = match payload {
        Ok(payload) => payload,
        Err(error) => return bad_json(error),
    };
    let store = state.store.clone();
    let now = state.clock.now_utc();

    let result = run_store(move || {
        let existing_ids: HashSet<i32> = store
            .list_alerts()?
            .into_iter()
            .map(|existing| existing.id)
            .collect();
        let inserting = !existing_ids.contains(&alert.id);

        if inserting {
            alert.created_at = now;
            alert.updated_at = now;
        }

        store.upsert_alert(&alert, now)?;

        if inserting && alert.id == 0 {
            let inserted = store
                .list_alerts()?
                .into_iter()
                .filter(|saved| !existing_ids.contains(&saved.id))
                .filter(|saved| {
                    saved.metric_id == alert.metric_id
                        && saved.threshold == alert.threshold
                        && saved.is_enabled == alert.is_enabled
                })
                .max_by_key(|saved| saved.id);

            if let Some(inserted) = inserted {
                alert.id = inserted.id;
            }
        }

        Ok(alert)
    })
    .await;

    match result {
        Ok(alert) => Json(alert).into_response(),
        Err(error) => internal_error(error),
    }
}

async fn delete_alert(State(state): State<AppState>, Path(id): Path<i32>) -> Response {
    let store = state.store.clone();
    match run_store(move || store.delete_alert(id)).await {
        Ok(true) => StatusCode::NO_CONTENT.into_response(),
        Ok(false) => StatusCode::NOT_FOUND.into_response(),
        Err(error) => internal_error(error),
    }
}

async fn get_metrics() -> Json<&'static [MetricMetadata; 4]> {
    Json(&METRIC_METADATA)
}

async fn get_health(State(state): State<AppState>) -> Response {
    let store = state.store.clone();
    let result = run_store(move || store.health_check()).await;
    let timestamp = xhm_core::time::to_wire_utc(&state.clock.now_utc());

    match result {
        Ok(()) => Json(json!({
            "status": "Healthy",
            "timestamp": timestamp,
            "database": "Connected",
        }))
        .into_response(),
        Err(error) => {
            tracing::error!(error = %error, "health check failed");
            (
                StatusCode::SERVICE_UNAVAILABLE,
                Json(json!({
                    "status": "Unhealthy",
                    "timestamp": timestamp,
                    "error": error,
                })),
            )
                .into_response()
        }
    }
}

async fn get_settings(State(state): State<AppState>) -> Response {
    let store = state.store.clone();
    let settings = match run_store(move || store.list_settings()).await {
        Ok(settings) => settings,
        Err(error) => return internal_error(error),
    };

    let mut grouped = BTreeMap::<String, BTreeMap<String, String>>::new();
    for setting in settings {
        grouped
            .entry(setting.category)
            .or_default()
            .insert(setting.key, setting.value);
    }

    Json(grouped).into_response()
}

async fn update_setting(
    State(state): State<AppState>,
    Path((category, key)): Path<(String, String)>,
    payload: Result<Json<UpdateSettingRequest>, JsonRejection>,
) -> Response {
    let Json(request) = match payload {
        Ok(payload) => payload,
        Err(error) => return bad_json(error),
    };
    let store = state.store.clone();
    let now = state.clock.now_utc();
    let value = request.value;

    let result = run_store(move || {
        let found = store.update_setting(&category, &key, &value, now)?;
        Ok((found, category, key, value))
    })
    .await;

    match result {
        Ok((true, category, key, value)) => Json(json!({
            "message": "配置已更新",
            "category": category,
            "key": key,
            "value": value,
        }))
        .into_response(),
        Ok((false, category, key, _)) => (
            StatusCode::NOT_FOUND,
            Json(json!({
                "message": format!("配置项 {category}.{key} 不存在"),
            })),
        )
            .into_response(),
        Err(error) => internal_error(error),
    }
}

async fn update_settings(
    State(state): State<AppState>,
    payload: Result<SettingsPayload, JsonRejection>,
) -> Response {
    let Json(settings) = match payload {
        Ok(payload) => payload,
        Err(error) => return bad_json(error),
    };

    let entry_count = settings.values().map(BTreeMap::len).sum();
    let mut entries = Vec::with_capacity(entry_count);
    let mut process_keywords = None;

    for (category, group) in settings {
        for (key, value) in group {
            if category == "DataCollection" && key == "ProcessKeywords" {
                process_keywords = Some(value.clone());
            }
            entries.push((category.clone(), key, value));
        }
    }

    let store = state.store.clone();
    let now = state.clock.now_utc();
    let counts = match run_store(move || store.upsert_settings(&entries, now)).await {
        Ok(counts) => counts,
        Err(error) => return internal_error(error),
    };

    reload_process_keywords(&state, counts, process_keywords).await;

    Json(json!({
        "message": format!(
            "成功更新 {} 个配置项，新增 {} 个配置项",
            counts.updated, counts.inserted
        ),
        "updatedCount": counts.updated,
        "insertedCount": counts.inserted,
    }))
    .into_response()
}

async fn reload_process_keywords(
    state: &AppState,
    counts: SettingsUpsertCounts,
    serialized: Option<String>,
) {
    if !counts.process_keywords_touched {
        return;
    }
    let Some(serialized) = serialized else {
        return;
    };

    match serde_json::from_str::<Option<Vec<String>>>(&serialized) {
        Ok(keywords) => {
            state.runtime.write().await.process_keywords = keywords.unwrap_or_default();
        }
        Err(error) => {
            tracing::error!(error = %error, "failed to reload process keywords");
        }
    }
}

async fn get_admin_status() -> Json<serde_json::Value> {
    admin_status_response(crate::power::is_administrator())
}

fn admin_status_response(is_admin: bool) -> Json<serde_json::Value> {
    let message = if is_admin {
        "Service 正在以管理员权限运行"
    } else {
        "Service 未以管理员权限运行"
    };

    Json(json!({
        "isAdmin": is_admin,
        "message": message,
    }))
}

#[cfg(test)]
mod tests {
    use std::{path::PathBuf, sync::Arc};

    use axum::{
        body::Body,
        http::{header::CONTENT_TYPE, Method, Request},
    };
    use chrono::{TimeZone, Utc};
    use http_body_util::BodyExt;
    use serde_json::Value;
    use tower::ServiceExt;
    use uuid::Uuid;
    use xhm_core::traits::{MockClock, MockLhmReader, MockRyzenAdjClient};

    use super::*;
    use crate::{
        db::SqliteMetricStore,
        state::{RuntimeConfig, ServicePaths},
    };

    fn fixed_time() -> chrono::DateTime<Utc> {
        Utc.with_ymd_and_hms(2026, 7, 26, 12, 34, 56)
            .single()
            .expect("valid fixed timestamp")
    }

    fn test_state(bridge_elevated: bool) -> AppState {
        let store = Arc::new(
            SqliteMetricStore::open_in_memory().expect("in-memory configuration store must open"),
        );
        let paths = ServicePaths::for_exe_dir(
            std::env::temp_dir().join(format!("xhm-config-test-{}", Uuid::new_v4())),
        );
        let runtime = RuntimeConfig {
            interval_seconds: 3,
            process_keywords: vec!["alpha".to_owned(), "beta".to_owned()],
            plugin_directory: PathBuf::from("custom-plugins"),
            ..RuntimeConfig::default()
        };

        AppState::new(
            store,
            Arc::new(MockClock::new(fixed_time())),
            Arc::new(MockLhmReader::new(None, bridge_elevated)),
            Arc::new(MockRyzenAdjClient::unsupported()),
            paths,
            runtime,
        )
    }

    fn request(method: Method, uri: &str, body: Body) -> Request<Body> {
        Request::builder()
            .method(method)
            .uri(uri)
            .body(body)
            .expect("valid request")
    }

    fn json_request(method: Method, uri: &str, body: impl Into<Body>) -> Request<Body> {
        Request::builder()
            .method(method)
            .uri(uri)
            .header(CONTENT_TYPE, "application/json")
            .body(body.into())
            .expect("valid JSON request")
    }

    async fn json_body(response: Response) -> Value {
        let bytes = response
            .into_body()
            .collect()
            .await
            .expect("response body must collect")
            .to_bytes();
        serde_json::from_slice(&bytes).expect("response must be JSON")
    }

    #[tokio::test]
    async fn get_config_returns_runtime_values_with_csharp_shape() {
        let response = router()
            .with_state(test_state(false))
            .oneshot(request(Method::GET, "/api/v1/config", Body::empty()))
            .await
            .expect("request succeeds");

        assert_eq!(response.status(), StatusCode::OK);
        let body = json_body(response).await;
        assert_eq!(body["monitor"]["intervalSeconds"], 3);
        assert_eq!(body["monitor"]["systemUsageIntervalSeconds"], 1);
        assert_eq!(body["monitor"]["keywords"], json!(["alpha", "beta"]));
        assert_eq!(body["metricProviders"]["pluginDirectory"], "custom-plugins");
    }

    #[tokio::test]
    async fn get_alerts_returns_metric_id_sorted_seed_rows() {
        let response = router()
            .with_state(test_state(false))
            .oneshot(request(Method::GET, "/api/v1/config/alerts", Body::empty()))
            .await
            .expect("request succeeds");

        assert_eq!(response.status(), StatusCode::OK);
        let alerts = json_body(response).await;
        let metric_ids: Vec<_> = alerts
            .as_array()
            .expect("alerts array")
            .iter()
            .map(|alert| alert["metricId"].as_str().expect("metric id"))
            .collect();
        assert_eq!(metric_ids, ["cpu", "gpu", "memory", "vram"]);
    }

    #[tokio::test]
    async fn post_alert_upserts_and_rejects_malformed_json() {
        let app = router().with_state(test_state(false));
        let response = app
            .clone()
            .oneshot(json_request(
                Method::POST,
                "/api/v1/config/alerts",
                r#"{"metricId":"temperature","threshold":80.5,"isEnabled":true}"#,
            ))
            .await
            .expect("request succeeds");

        assert_eq!(response.status(), StatusCode::OK);
        let body = json_body(response).await;
        assert_eq!(body["metricId"], "temperature");
        assert_eq!(body["createdAt"], "2026-07-26T12:34:56Z");
        assert!(body["id"].as_i64().expect("generated alert id") > 4);

        let malformed = app
            .oneshot(json_request(
                Method::POST,
                "/api/v1/config/alerts",
                "{not-json",
            ))
            .await
            .expect("request succeeds");
        assert_eq!(malformed.status(), StatusCode::BAD_REQUEST);
    }

    #[tokio::test]
    async fn delete_alert_returns_no_content_then_not_found() {
        let app = router().with_state(test_state(false));
        let deleted = app
            .clone()
            .oneshot(request(
                Method::DELETE,
                "/api/v1/config/alerts/1",
                Body::empty(),
            ))
            .await
            .expect("request succeeds");
        assert_eq!(deleted.status(), StatusCode::NO_CONTENT);

        let missing = app
            .oneshot(request(
                Method::DELETE,
                "/api/v1/config/alerts/1",
                Body::empty(),
            ))
            .await
            .expect("request succeeds");
        assert_eq!(missing.status(), StatusCode::NOT_FOUND);
    }

    #[tokio::test]
    async fn get_metrics_returns_exact_builtin_registry_metadata() {
        let response = router()
            .with_state(test_state(false))
            .oneshot(request(
                Method::GET,
                "/api/v1/config/metrics",
                Body::empty(),
            ))
            .await
            .expect("request succeeds");

        assert_eq!(response.status(), StatusCode::OK);
        let body = json_body(response).await;
        assert_eq!(body.as_array().expect("metadata array").len(), 4);
        assert_eq!(body[0]["metricId"], "cpu");
        assert_eq!(body[0]["type"], "Percentage");
        assert_eq!(body[1]["metricId"], "memory");
        assert_eq!(body[1]["unit"], "MB");
        assert_eq!(body[2]["color"], "#8b5cf6");
        assert_eq!(body[3]["icon"], "HardDrive");
    }

    #[tokio::test]
    async fn get_health_reports_connected_database_and_wire_timestamp() {
        let response = router()
            .with_state(test_state(false))
            .oneshot(request(Method::GET, "/api/v1/config/health", Body::empty()))
            .await
            .expect("request succeeds");

        assert_eq!(response.status(), StatusCode::OK);
        let body = json_body(response).await;
        assert_eq!(body["status"], "Healthy");
        assert_eq!(body["database"], "Connected");
        assert_eq!(body["timestamp"], "2026-07-26T12:34:56Z");
    }

    #[tokio::test]
    async fn get_settings_groups_seed_rows_in_sorted_maps() {
        let response = router()
            .with_state(test_state(false))
            .oneshot(request(
                Method::GET,
                "/api/v1/config/settings",
                Body::empty(),
            ))
            .await
            .expect("request succeeds");

        assert_eq!(response.status(), StatusCode::OK);
        let body = json_body(response).await;
        assert!(body["Appearance"]["ThemeColor"].is_string());
        assert!(body["DataCollection"]["ProcessKeywords"].is_string());
        assert_eq!(body["Monitoring"]["MonitorCpu"], "true");
        assert_eq!(body["System"]["StartWithWindows"], "false");
    }

    #[tokio::test]
    async fn put_setting_updates_existing_and_returns_not_found_for_missing() {
        let app = router().with_state(test_state(false));
        let updated = app
            .clone()
            .oneshot(json_request(
                Method::PUT,
                "/api/v1/config/settings/Appearance/Opacity",
                r#"{"value":"77"}"#,
            ))
            .await
            .expect("request succeeds");

        assert_eq!(updated.status(), StatusCode::OK);
        let body = json_body(updated).await;
        assert_eq!(body["message"], "配置已更新");
        assert_eq!(body["category"], "Appearance");
        assert_eq!(body["key"], "Opacity");
        assert_eq!(body["value"], "77");

        let missing = app
            .oneshot(json_request(
                Method::PUT,
                "/api/v1/config/settings/Missing/Key",
                r#"{"value":"1"}"#,
            ))
            .await
            .expect("request succeeds");
        assert_eq!(missing.status(), StatusCode::NOT_FOUND);
        assert_eq!(
            json_body(missing).await["message"],
            "配置项 Missing.Key 不存在"
        );
    }

    #[tokio::test]
    async fn put_settings_upserts_counts_and_hot_reloads_process_keywords() {
        let state = test_state(false);
        let app = router().with_state(state.clone());
        let response = app
            .oneshot(json_request(
                Method::PUT,
                "/api/v1/config/settings",
                r#"{"DataCollection":{"ProcessKeywords":"[\"python\",\"new-worker\"]"},"NewCategory":{"NewKey":"new-value"}}"#,
            ))
            .await
            .expect("request succeeds");

        assert_eq!(response.status(), StatusCode::OK);
        let body = json_body(response).await;
        assert_eq!(body["updatedCount"], 1);
        assert_eq!(body["insertedCount"], 1);
        assert_eq!(body["message"], "成功更新 1 个配置项，新增 1 个配置项");
        assert_eq!(
            state.runtime.read().await.process_keywords,
            ["python", "new-worker"]
        );
    }

    #[tokio::test]
    async fn get_admin_status_uses_service_token_instead_of_bridge_elevation() {
        let expected = crate::power::is_administrator();
        let elevated_bridge = router()
            .with_state(test_state(true))
            .oneshot(request(
                Method::GET,
                "/api/v1/config/admin-status",
                Body::empty(),
            ))
            .await
            .expect("request succeeds");
        let unelevated_bridge = router()
            .with_state(test_state(false))
            .oneshot(request(
                Method::GET,
                "/api/v1/config/admin-status",
                Body::empty(),
            ))
            .await
            .expect("request succeeds");

        assert_eq!(elevated_bridge.status(), StatusCode::OK);
        assert_eq!(unelevated_bridge.status(), StatusCode::OK);
        let elevated_body = json_body(elevated_bridge).await;
        let unelevated_body = json_body(unelevated_bridge).await;
        assert_eq!(elevated_body["isAdmin"], expected);
        assert_eq!(unelevated_body["isAdmin"], expected);
        assert_eq!(
            elevated_body["message"],
            if expected {
                "Service 正在以管理员权限运行"
            } else {
                "Service 未以管理员权限运行"
            }
        );
    }
}
