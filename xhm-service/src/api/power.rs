use std::sync::Arc;

use axum::{
    extract::State,
    http::StatusCode,
    response::{IntoResponse, Response},
    routing::{get, post},
    Extension, Json, Router,
};
use serde::Serialize;
use xhm_core::models::{PowerScheme, PowerSchemeSwitchResult, PowerStatus, PowerWarmupStatus};

use crate::{
    power::{switch_to_next_scheme, DeviceVerifier, ProductionDeviceVerifier},
    state::AppState,
};

const DEVICE_NOT_SUPPORTED: &str = "当前设备不支持此功能";

pub fn router() -> Router<AppState> {
    router_with_verifier(Arc::new(ProductionDeviceVerifier::default()))
}

pub fn router_with_verifier(verifier: Arc<dyn DeviceVerifier>) -> Router<AppState> {
    Router::new()
        .route("/api/v1/power/status", get(get_status))
        .route("/api/v1/power/warmup", get(warmup))
        .route("/api/v1/power/scheme/next", post(switch_to_next))
        .layer(Extension(verifier))
}

enum StatusOutcome {
    Unsupported,
    Unavailable,
    Available(PowerStatus),
}

async fn get_status(State(state): State<AppState>) -> Response {
    let client = Arc::clone(&state.ryzenadj);
    let outcome = tokio::task::spawn_blocking(move || {
        if !client.is_supported() {
            StatusOutcome::Unsupported
        } else {
            client
                .read_status()
                .map_or(StatusOutcome::Unavailable, StatusOutcome::Available)
        }
    })
    .await;

    match outcome {
        Ok(StatusOutcome::Unsupported) => {
            message_response(StatusCode::NOT_FOUND, "Power provider not supported")
        }
        Ok(StatusOutcome::Unavailable) => {
            message_response(StatusCode::SERVICE_UNAVAILABLE, "Power status unavailable")
        }
        Ok(StatusOutcome::Available(status)) => Json(status).into_response(),
        Err(error) => {
            tracing::error!(%error, "power status task failed");
            message_response(StatusCode::SERVICE_UNAVAILABLE, "Power status unavailable")
        }
    }
}

async fn warmup(Extension(verifier): Extension<Arc<dyn DeviceVerifier>>) -> Response {
    let verification =
        tokio::task::spawn_blocking(move || verifier.verification_status(true)).await;
    let status = match verification {
        Ok(status) => status,
        Err(error) => {
            tracing::error!(%error, "device verification task failed");
            PowerWarmupStatus {
                enabled: false,
                device_name: None,
                reason: Some(DEVICE_NOT_SUPPORTED.to_owned()),
            }
        }
    };

    Json(status).into_response()
}

enum SwitchOutcome {
    Unsupported,
    Completed(PowerSchemeSwitchResult),
}

async fn switch_to_next(
    State(state): State<AppState>,
    Extension(verifier): Extension<Arc<dyn DeviceVerifier>>,
) -> Response {
    let verifier_for_check = Arc::clone(&verifier);
    let verification =
        tokio::task::spawn_blocking(move || verifier_for_check.verification_status(false)).await;
    let verification = match verification {
        Ok(verification) => verification,
        Err(error) => {
            tracing::error!(%error, "device verification task failed");
            return message_response(StatusCode::FORBIDDEN, DEVICE_NOT_SUPPORTED);
        }
    };
    if !verification.enabled {
        return message_response(
            StatusCode::FORBIDDEN,
            verification
                .reason
                .unwrap_or_else(|| DEVICE_NOT_SUPPORTED.to_owned()),
        );
    }

    let Some(device_name) = verification.device_name else {
        return message_response(StatusCode::FORBIDDEN, DEVICE_NOT_SUPPORTED);
    };
    let schemes = verifier.schemes_for_device(&device_name).to_vec();
    if schemes.is_empty() {
        return message_response(StatusCode::FORBIDDEN, "功耗切换方案未配置");
    }

    let client = Arc::clone(&state.ryzenadj);
    let outcome = tokio::task::spawn_blocking(move || {
        if !client.is_supported() {
            SwitchOutcome::Unsupported
        } else {
            SwitchOutcome::Completed(switch_to_next_scheme(client.as_ref(), &schemes))
        }
    })
    .await;

    match outcome {
        Ok(SwitchOutcome::Unsupported) => {
            message_response(StatusCode::NOT_FOUND, "Power provider not supported")
        }
        Ok(SwitchOutcome::Completed(result)) => switch_result_response(result),
        Err(error) => {
            tracing::error!(%error, "power scheme task failed");
            message_response(StatusCode::SERVICE_UNAVAILABLE, "Power status unavailable")
        }
    }
}

fn switch_result_response(result: PowerSchemeSwitchResult) -> Response {
    if !result.success {
        return message_response(StatusCode::SERVICE_UNAVAILABLE, result.message);
    }
    let Some(scheme) = result.new_scheme else {
        return message_response(StatusCode::SERVICE_UNAVAILABLE, result.message);
    };

    Json(SwitchResponse {
        message: "OK",
        previous_scheme_index: result.previous_scheme_index,
        new_scheme_index: result.new_scheme_index,
        scheme,
    })
    .into_response()
}

#[derive(Debug, Serialize)]
struct MessageResponse {
    message: String,
}

fn message_response(status: StatusCode, message: impl Into<String>) -> Response {
    (
        status,
        Json(MessageResponse {
            message: message.into(),
        }),
    )
        .into_response()
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct SwitchResponse {
    message: &'static str,
    previous_scheme_index: Option<i32>,
    new_scheme_index: i32,
    scheme: PowerScheme,
}

#[cfg(test)]
mod tests {
    use std::sync::Arc;

    use axum::{
        body::Body,
        http::{Method, Request},
    };
    use http_body_util::BodyExt;
    use serde_json::{json, Value};
    use tower::ServiceExt;
    use xhm_core::{
        models::{PowerScheme, PowerStatus, PowerWarmupStatus},
        traits::{MockLhmReader, MockRyzenAdjClient, RyzenAdjClient, SystemClock},
    };

    use crate::{
        db::SqliteMetricStore,
        state::{AppState, RuntimeConfig, ServicePaths},
    };

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

    #[derive(Debug)]
    struct FixedDeviceVerifier {
        status: PowerWarmupStatus,
        schemes: Vec<PowerScheme>,
    }

    impl FixedDeviceVerifier {
        fn verified() -> Self {
            Self {
                status: PowerWarmupStatus {
                    enabled: true,
                    device_name: Some("SixUnitedAXB35-02".to_owned()),
                    reason: None,
                },
                schemes: TEST_SCHEMES.to_vec(),
            }
        }

        fn verified_without_schemes() -> Self {
            Self {
                status: PowerWarmupStatus {
                    enabled: true,
                    device_name: Some("SixUnitedAXB35-02".to_owned()),
                    reason: None,
                },
                schemes: Vec::new(),
            }
        }

        fn denied(reason: &str) -> Self {
            Self {
                status: PowerWarmupStatus {
                    enabled: false,
                    device_name: None,
                    reason: Some(reason.to_owned()),
                },
                schemes: Vec::new(),
            }
        }
    }

    impl DeviceVerifier for FixedDeviceVerifier {
        fn verification_status(&self, _retry: bool) -> PowerWarmupStatus {
            self.status.clone()
        }

        fn schemes_for_device(&self, _device_name: &str) -> &[PowerScheme] {
            &self.schemes
        }
    }

    fn app(client: Arc<dyn RyzenAdjClient>, verifier: Arc<dyn DeviceVerifier>) -> Router {
        let state = AppState::new(
            Arc::new(SqliteMetricStore::open_in_memory().unwrap()),
            Arc::new(SystemClock),
            Arc::new(MockLhmReader::new(None, false)),
            client,
            ServicePaths::for_exe_dir("power-api-test"),
            RuntimeConfig::default(),
        );
        router_with_verifier(verifier).with_state(state)
    }

    async fn request(app: Router, method: Method, uri: &str) -> (StatusCode, Value) {
        let response = app
            .oneshot(
                Request::builder()
                    .method(method)
                    .uri(uri)
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        let status = response.status();
        let body = response.into_body().collect().await.unwrap().to_bytes();
        (status, serde_json::from_slice(&body).unwrap())
    }

    fn status() -> PowerStatus {
        PowerStatus {
            current_watts: 42.0,
            limit_watts: 55.0,
            scheme_index: Some(0),
            limits: TEST_SCHEMES[0],
        }
    }

    #[tokio::test]
    async fn status_returns_exact_200_payload() {
        let (status_code, body) = request(
            app(
                Arc::new(MockRyzenAdjClient::supported(status())),
                Arc::new(FixedDeviceVerifier::verified()),
            ),
            Method::GET,
            "/api/v1/power/status",
        )
        .await;

        assert_eq!(status_code, StatusCode::OK);
        assert_eq!(
            body,
            json!({
                "currentWatts": 42.0,
                "limitWatts": 55.0,
                "schemeIndex": 0,
                "limits": {
                    "stapmWatts": 55,
                    "fastWatts": 100,
                    "slowWatts": 55
                }
            })
        );
    }

    #[tokio::test]
    async fn status_maps_unsupported_to_404() {
        let (status_code, body) = request(
            app(
                Arc::new(MockRyzenAdjClient::unsupported()),
                Arc::new(FixedDeviceVerifier::verified()),
            ),
            Method::GET,
            "/api/v1/power/status",
        )
        .await;

        assert_eq!(status_code, StatusCode::NOT_FOUND);
        assert_eq!(body, json!({ "message": "Power provider not supported" }));
    }

    #[tokio::test]
    async fn status_maps_missing_snapshot_to_503() {
        let (status_code, body) = request(
            app(
                Arc::new(MockRyzenAdjClient::supported_but_unavailable()),
                Arc::new(FixedDeviceVerifier::verified()),
            ),
            Method::GET,
            "/api/v1/power/status",
        )
        .await;

        assert_eq!(status_code, StatusCode::SERVICE_UNAVAILABLE);
        assert_eq!(body, json!({ "message": "Power status unavailable" }));
    }

    #[tokio::test]
    async fn warmup_returns_injected_device_verification() {
        let (status_code, body) = request(
            app(
                Arc::new(MockRyzenAdjClient::unsupported()),
                Arc::new(FixedDeviceVerifier::denied("功耗切换方案未配置")),
            ),
            Method::GET,
            "/api/v1/power/warmup",
        )
        .await;

        assert_eq!(status_code, StatusCode::OK);
        assert_eq!(
            body,
            json!({
                "enabled": false,
                "deviceName": null,
                "reason": "功耗切换方案未配置"
            })
        );
    }

    #[tokio::test]
    async fn scheme_next_maps_device_denial_to_403_before_backend() {
        let client = Arc::new(MockRyzenAdjClient::supported(status()));
        let (status_code, body) = request(
            app(
                client.clone(),
                Arc::new(FixedDeviceVerifier::denied("当前设备不支持此功能")),
            ),
            Method::POST,
            "/api/v1/power/scheme/next",
        )
        .await;

        assert_eq!(status_code, StatusCode::FORBIDDEN);
        assert_eq!(body, json!({ "message": "当前设备不支持此功能" }));
        assert!(client.applied_schemes().is_empty());
    }

    #[tokio::test]
    async fn scheme_next_maps_missing_configured_profile_to_403_before_backend() {
        let client = Arc::new(MockRyzenAdjClient::supported(status()));
        let (status_code, body) = request(
            app(
                client.clone(),
                Arc::new(FixedDeviceVerifier::verified_without_schemes()),
            ),
            Method::POST,
            "/api/v1/power/scheme/next",
        )
        .await;

        assert_eq!(status_code, StatusCode::FORBIDDEN);
        assert_eq!(body, json!({ "message": "功耗切换方案未配置" }));
        assert!(client.applied_schemes().is_empty());
    }

    #[tokio::test]
    async fn scheme_next_maps_unsupported_backend_to_404() {
        let (status_code, body) = request(
            app(
                Arc::new(MockRyzenAdjClient::unsupported()),
                Arc::new(FixedDeviceVerifier::verified()),
            ),
            Method::POST,
            "/api/v1/power/scheme/next",
        )
        .await;

        assert_eq!(status_code, StatusCode::NOT_FOUND);
        assert_eq!(body, json!({ "message": "Power provider not supported" }));
    }

    #[tokio::test]
    async fn scheme_next_maps_apply_failure_to_503() {
        let (status_code, body) = request(
            app(
                Arc::new(MockRyzenAdjClient::failing_apply(status())),
                Arc::new(FixedDeviceVerifier::verified()),
            ),
            Method::POST,
            "/api/v1/power/scheme/next",
        )
        .await;

        assert_eq!(status_code, StatusCode::SERVICE_UNAVAILABLE);
        assert_eq!(body["message"], "ryzenadj: mock apply failure");
    }

    #[tokio::test]
    async fn scheme_next_returns_exact_success_payload() {
        let client = Arc::new(MockRyzenAdjClient::supported(status()));
        let (status_code, body) = request(
            app(client.clone(), Arc::new(FixedDeviceVerifier::verified())),
            Method::POST,
            "/api/v1/power/scheme/next",
        )
        .await;

        assert_eq!(status_code, StatusCode::OK);
        assert_eq!(
            body,
            json!({
                "message": "OK",
                "previousSchemeIndex": 0,
                "newSchemeIndex": 1,
                "scheme": {
                    "stapmWatts": 85,
                    "fastWatts": 120,
                    "slowWatts": 85
                }
            })
        );
        assert_eq!(
            client.applied_schemes(),
            [PowerScheme {
                stapm_watts: 85,
                fast_watts: 120,
                slow_watts: 85,
            }]
        );
    }
}
