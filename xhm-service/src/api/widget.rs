use std::path::Path;

use axum::{
    extract::{rejection::JsonRejection, Path as AxumPath, State},
    http::StatusCode,
    response::{IntoResponse, Response},
    routing::{get, post},
    Json, Router,
};
use serde_json::{json, Value};
use tokio::io::AsyncWriteExt;
use uuid::Uuid;
use xhm_core::{
    models::{widget_disk, MetricClickConfig, WidgetSettings},
    CoreError,
};

use crate::state::AppState;

pub fn router() -> Router<AppState> {
    Router::new()
        .route(
            "/api/v1/widgetconfig",
            get(get_settings).post(update_settings),
        )
        .route(
            "/api/v1/widgetconfig/:metric_id",
            post(update_metric_config),
        )
}

fn bad_json(error: JsonRejection) -> Response {
    (
        StatusCode::BAD_REQUEST,
        Json(json!({ "error": error.to_string() })),
    )
        .into_response()
}

fn bad_request(message: impl std::fmt::Display) -> Response {
    (
        StatusCode::BAD_REQUEST,
        Json(json!({ "error": message.to_string() })),
    )
        .into_response()
}

fn write_error(error: CoreError) -> Response {
    tracing::error!(error = %error, "failed to save widget settings");
    (
        StatusCode::INTERNAL_SERVER_ERROR,
        Json(json!({
            "success": false,
            "error": error.to_string(),
        })),
    )
        .into_response()
}

async fn get_settings(State(state): State<AppState>) -> Json<WidgetSettings> {
    Json(load_settings(&state.paths.widget_config_path).await)
}

async fn update_settings(
    State(state): State<AppState>,
    payload: Result<Json<Value>, JsonRejection>,
) -> Response {
    let Json(value) = match payload {
        Ok(payload) => payload,
        Err(error) => return bad_json(error),
    };
    if !value.is_object() {
        return bad_request("请求体必须是 JSON 对象");
    }
    let settings: WidgetSettings = match serde_json::from_value(value) {
        Ok(settings) => settings,
        Err(error) => return bad_request(error),
    };

    match persist_settings(&state.paths.widget_config_path, &settings).await {
        Ok(()) => Json(json!({ "success": true })).into_response(),
        Err(error) => write_error(error),
    }
}

async fn update_metric_config(
    State(state): State<AppState>,
    AxumPath(metric_id): AxumPath<String>,
    payload: Result<Json<Value>, JsonRejection>,
) -> Response {
    let Json(value) = match payload {
        Ok(payload) => payload,
        Err(error) => return bad_json(error),
    };
    if !value.is_object() {
        return bad_request("请求体必须是 JSON 对象");
    }
    let config: MetricClickConfig = match serde_json::from_value(value) {
        Ok(config) => config,
        Err(error) => return bad_request(error),
    };

    let mut settings = load_settings(&state.paths.widget_config_path).await;
    settings.metric_click_actions.insert(metric_id, config);

    match persist_settings(&state.paths.widget_config_path, &settings).await {
        Ok(()) => Json(json!({ "success": true })).into_response(),
        Err(error) => write_error(error),
    }
}

async fn load_settings(path: &Path) -> WidgetSettings {
    let bytes = match tokio::fs::read(path).await {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return WidgetSettings::default();
        }
        Err(error) => {
            tracing::error!(path = %path.display(), error = %error, "failed to load widget settings");
            return WidgetSettings::default();
        }
    };

    match serde_json::from_slice::<widget_disk::WidgetSettings>(&bytes) {
        Ok(settings) => settings.into(),
        Err(error) => {
            tracing::error!(path = %path.display(), error = %error, "failed to parse widget settings");
            WidgetSettings::default()
        }
    }
}

async fn persist_settings(path: &Path, settings: &WidgetSettings) -> xhm_core::Result<()> {
    let parent = path.parent().ok_or_else(|| {
        CoreError::Configuration(format!(
            "widget settings path has no parent: {}",
            path.display()
        ))
    })?;
    tokio::fs::create_dir_all(parent).await?;

    let disk_settings: widget_disk::WidgetSettings = settings.clone().into();
    let bytes = serde_json::to_vec_pretty(&disk_settings)?;
    let file_name = path
        .file_name()
        .map(|name| name.to_string_lossy())
        .unwrap_or_else(|| "widget-settings.json".into());
    let temporary_path = parent.join(format!(".{file_name}.{}.tmp", Uuid::new_v4()));

    let write_result: std::io::Result<()> = async {
        let mut temporary_file = tokio::fs::OpenOptions::new()
            .create_new(true)
            .write(true)
            .open(&temporary_path)
            .await?;
        temporary_file.write_all(&bytes).await?;
        temporary_file.sync_all().await?;
        drop(temporary_file);
        tokio::fs::rename(&temporary_path, path).await?;
        Ok(())
    }
    .await;

    match write_result {
        Ok(()) => Ok(()),
        Err(error) => {
            cleanup_temporary_file(&temporary_path).await;
            Err(error.into())
        }
    }
}

async fn cleanup_temporary_file(path: &Path) {
    match tokio::fs::remove_file(path).await {
        Ok(()) => {}
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => {
            tracing::warn!(path = %path.display(), error = %error, "failed to clean widget settings temporary file");
        }
    }
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
    use xhm_core::traits::{MockClock, MockLhmReader, MockRyzenAdjClient};

    use super::*;
    use crate::{
        db::SqliteMetricStore,
        state::{RuntimeConfig, ServicePaths},
    };

    fn unique_root() -> PathBuf {
        std::env::temp_dir().join(format!("xhm-widget-test-{}", Uuid::new_v4()))
    }

    fn test_state(root: &Path) -> AppState {
        let store = Arc::new(
            SqliteMetricStore::open_in_memory().expect("in-memory widget test store must open"),
        );
        let now = Utc
            .with_ymd_and_hms(2026, 7, 26, 12, 34, 56)
            .single()
            .expect("valid fixed timestamp");

        AppState::new(
            store,
            Arc::new(MockClock::new(now)),
            Arc::new(MockLhmReader::default()),
            Arc::new(MockRyzenAdjClient::unsupported()),
            ServicePaths::for_exe_dir(root),
            RuntimeConfig::default(),
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
    async fn get_widget_config_returns_core_default_when_file_is_missing() {
        let root = unique_root();
        tokio::fs::create_dir_all(&root)
            .await
            .expect("test root must be created");
        let response = router()
            .with_state(test_state(&root))
            .oneshot(request(Method::GET, "/api/v1/widgetconfig", Body::empty()))
            .await
            .expect("request succeeds");

        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(
            json_body(response).await,
            serde_json::to_value(WidgetSettings::default()).expect("default serializes")
        );
        tokio::fs::remove_dir_all(root)
            .await
            .expect("test root must be removed");
    }

    #[tokio::test]
    async fn post_widget_config_writes_pascal_case_atomically_and_rejects_bad_json() {
        let root = unique_root();
        tokio::fs::create_dir_all(&root)
            .await
            .expect("test root must be created");
        let state = test_state(&root);
        let config_path = state.paths.widget_config_path.clone();
        let app = router().with_state(state);
        let response = app
            .clone()
            .oneshot(json_request(
                Method::POST,
                "/api/v1/widgetconfig",
                r#"{"enableMetricClick":true,"metricClickActions":{"cpu":{"enabled":true,"action":"launch","parameters":null}}}"#,
            ))
            .await
            .expect("request succeeds");

        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(json_body(response).await["success"], true);
        let disk_text = tokio::fs::read_to_string(&*config_path)
            .await
            .expect("widget settings must be written");
        let disk: Value = serde_json::from_str(&disk_text).expect("disk JSON must parse");
        assert_eq!(disk["EnableMetricClick"], true);
        assert!(disk.get("enableMetricClick").is_none());
        assert_eq!(disk["MetricClickActions"]["cpu"]["Action"], "launch");

        let malformed = app
            .oneshot(json_request(
                Method::POST,
                "/api/v1/widgetconfig",
                "{not-json",
            ))
            .await
            .expect("request succeeds");
        assert_eq!(malformed.status(), StatusCode::BAD_REQUEST);

        let mut entries = tokio::fs::read_dir(config_path.parent().expect("config parent"))
            .await
            .expect("config directory must exist");
        let mut file_names = Vec::new();
        while let Some(entry) = entries
            .next_entry()
            .await
            .expect("directory entry must read")
        {
            file_names.push(entry.file_name());
        }
        assert_eq!(
            file_names,
            [std::ffi::OsString::from("widget-settings.json")]
        );

        tokio::fs::remove_dir_all(root)
            .await
            .expect("test root must be removed");
    }

    #[tokio::test]
    async fn post_metric_config_preserves_existing_settings_and_updates_one_metric() {
        let root = unique_root();
        let data_dir = root.join("data");
        tokio::fs::create_dir_all(&data_dir)
            .await
            .expect("data directory must be created");
        let initial = WidgetSettings {
            enable_metric_click: true,
            ..WidgetSettings::default()
        };
        let disk: widget_disk::WidgetSettings = initial.into();
        tokio::fs::write(
            data_dir.join("widget-settings.json"),
            serde_json::to_vec_pretty(&disk).expect("disk settings serialize"),
        )
        .await
        .expect("initial settings must be written");

        let state = test_state(&root);
        let config_path = state.paths.widget_config_path.clone();
        let app = router().with_state(state);
        let response = app
            .clone()
            .oneshot(json_request(
                Method::POST,
                "/api/v1/widgetconfig/cpu",
                r#"{"enabled":true,"action":"openTaskManager","parameters":null}"#,
            ))
            .await
            .expect("request succeeds");

        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(json_body(response).await["success"], true);
        let saved_disk: widget_disk::WidgetSettings = serde_json::from_slice(
            &tokio::fs::read(&*config_path)
                .await
                .expect("saved settings must read"),
        )
        .expect("saved disk settings must parse");
        let saved: WidgetSettings = saved_disk.into();
        assert!(saved.enable_metric_click);
        assert!(saved.metric_click_actions["cpu"].enabled);
        assert_eq!(saved.metric_click_actions["cpu"].action, "openTaskManager");
        assert_eq!(
            saved.metric_click_actions["power"].action,
            "togglePowerMode"
        );

        // 非 object body（如 JSON 数组）对齐 C# [FromBody] 严格拒绝语义，返回 400。
        let malformed = app
            .oneshot(json_request(Method::POST, "/api/v1/widgetconfig/cpu", "[]"))
            .await
            .expect("request succeeds");
        assert_eq!(malformed.status(), StatusCode::BAD_REQUEST);

        tokio::fs::remove_dir_all(root)
            .await
            .expect("test root must be removed");
    }
}
