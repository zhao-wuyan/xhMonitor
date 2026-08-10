use axum::{
    extract::{Query, State},
    http::StatusCode,
    response::{IntoResponse, Response},
    routing::get,
    Json, Router,
};
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use xhm_core::{
    models::{AggregationLevel, MetricFilter},
    time::from_sqlite_text,
    CoreError,
};

use crate::state::AppState;

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct LatestQuery {
    process_id: Option<String>,
    process_name: Option<String>,
    keyword: Option<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct HistoryQuery {
    process_id: Option<String>,
    from: Option<String>,
    to: Option<String>,
    aggregation: Option<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ProcessesQuery {
    from: Option<String>,
    to: Option<String>,
    keyword: Option<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct AggregationsQuery {
    from: Option<String>,
    to: Option<String>,
    aggregation: Option<String>,
}

#[derive(Debug)]
struct ApiError {
    status: StatusCode,
    message: String,
}

#[derive(Debug, Serialize)]
struct ErrorResponse {
    error: String,
}

impl ApiError {
    fn bad_request(message: impl Into<String>) -> Self {
        Self {
            status: StatusCode::BAD_REQUEST,
            message: message.into(),
        }
    }

    fn from_core(error: CoreError) -> Self {
        match error {
            CoreError::InvalidArgument(message) => Self::bad_request(message),
            CoreError::NotFound(message) => Self {
                status: StatusCode::NOT_FOUND,
                message,
            },
            other => {
                tracing::error!(error = %other, "metrics store operation failed");
                Self {
                    status: StatusCode::INTERNAL_SERVER_ERROR,
                    message: "metrics store operation failed".to_owned(),
                }
            }
        }
    }
}

impl IntoResponse for ApiError {
    fn into_response(self) -> Response {
        (
            self.status,
            Json(ErrorResponse {
                error: self.message,
            }),
        )
            .into_response()
    }
}

pub fn router() -> Router<AppState> {
    Router::new()
        .route("/api/v1/metrics/latest", get(latest))
        .route("/api/v1/metrics/history", get(history))
        .route("/api/v1/metrics/processes", get(processes))
        .route("/api/v1/metrics/aggregations", get(aggregations))
}

async fn latest(
    State(state): State<AppState>,
    Query(query): Query<LatestQuery>,
) -> Result<Response, ApiError> {
    let filter = MetricFilter {
        process_id: parse_optional_i32(query.process_id, "processId")?,
        process_name: non_whitespace(query.process_name),
        keyword: non_whitespace(query.keyword),
        from: None,
        to: None,
    };
    let store = state.store.clone();
    let records = run_store(move || store.latest_process_metrics(&filter)).await?;

    Ok(Json(records).into_response())
}

async fn history(
    State(state): State<AppState>,
    Query(query): Query<HistoryQuery>,
) -> Result<Response, ApiError> {
    let process_id = parse_required_i32(query.process_id, "processId")?;
    let from = parse_optional_timestamp(query.from, "from")?;
    let to = parse_optional_timestamp(query.to, "to")?;
    let aggregation = query.aggregation.as_deref().unwrap_or("raw");
    let store = state.store.clone();

    if aggregation.eq_ignore_ascii_case("raw") {
        let records = run_store(move || store.history_raw(process_id, from, to)).await?;
        return Ok(Json(records).into_response());
    }

    let level = AggregationLevel::from_query_lossy(aggregation);
    let records = run_store(move || store.history_aggregated(process_id, level, from, to)).await?;

    Ok(Json(records).into_response())
}

async fn processes(
    State(state): State<AppState>,
    Query(query): Query<ProcessesQuery>,
) -> Result<Response, ApiError> {
    let filter = MetricFilter {
        process_id: None,
        process_name: None,
        keyword: non_whitespace(query.keyword),
        from: parse_optional_timestamp(query.from, "from")?,
        to: parse_optional_timestamp(query.to, "to")?,
    };
    let store = state.store.clone();
    let records = run_store(move || store.process_summaries(&filter)).await?;

    Ok(Json(records).into_response())
}

async fn aggregations(
    State(state): State<AppState>,
    Query(query): Query<AggregationsQuery>,
) -> Result<Response, ApiError> {
    let from = parse_required_timestamp(query.from, "from")?;
    let to = parse_required_timestamp(query.to, "to")?;
    let level =
        AggregationLevel::from_query_lossy(query.aggregation.as_deref().unwrap_or("minute"));
    let store = state.store.clone();
    let records = run_store(move || store.aggregations(level, from, to)).await?;

    Ok(Json(records).into_response())
}

async fn run_store<T>(
    operation: impl FnOnce() -> xhm_core::Result<T> + Send + 'static,
) -> Result<T, ApiError>
where
    T: Send + 'static,
{
    tokio::task::spawn_blocking(operation)
        .await
        .map_err(|error| {
            tracing::error!(error = %error, "metrics store task failed");
            ApiError {
                status: StatusCode::INTERNAL_SERVER_ERROR,
                message: "metrics store task failed".to_owned(),
            }
        })?
        .map_err(ApiError::from_core)
}

fn non_whitespace(value: Option<String>) -> Option<String> {
    value.filter(|text| !text.trim().is_empty())
}

fn parse_optional_i32(value: Option<String>, field: &str) -> Result<Option<i32>, ApiError> {
    value
        .map(|raw| {
            raw.trim()
                .parse::<i32>()
                .map_err(|_| ApiError::bad_request(format!("{field} must be a 32-bit integer")))
        })
        .transpose()
}

fn parse_required_i32(value: Option<String>, field: &str) -> Result<i32, ApiError> {
    let raw = value.ok_or_else(|| ApiError::bad_request(format!("{field} is required")))?;
    raw.trim()
        .parse::<i32>()
        .map_err(|_| ApiError::bad_request(format!("{field} must be a 32-bit integer")))
}

fn parse_optional_timestamp(
    value: Option<String>,
    field: &str,
) -> Result<Option<DateTime<Utc>>, ApiError> {
    value
        .map(|raw| {
            from_sqlite_text(&raw)
                .map_err(|_| ApiError::bad_request(format!("{field} must be a valid timestamp")))
        })
        .transpose()
}

fn parse_required_timestamp(value: Option<String>, field: &str) -> Result<DateTime<Utc>, ApiError> {
    let raw = value.ok_or_else(|| ApiError::bad_request(format!("{field} is required")))?;
    from_sqlite_text(&raw)
        .map_err(|_| ApiError::bad_request(format!("{field} must be a valid timestamp")))
}

#[cfg(test)]
mod tests {
    use std::sync::Arc;

    use axum::{
        body::{to_bytes, Body},
        http::Request,
    };
    use chrono::TimeZone;
    use serde_json::{json, Value};
    use tower::ServiceExt;
    use xhm_core::{
        models::{NewAggregatedMetricRecord, NewProcessMetricRecord},
        traits::{MetricStore, MockClock, MockLhmReader, MockRyzenAdjClient},
    };

    use super::*;
    use crate::{
        db::SqliteMetricStore,
        state::{RuntimeConfig, ServicePaths},
    };

    fn timestamp(hour: u32, minute: u32, second: u32) -> DateTime<Utc> {
        Utc.with_ymd_and_hms(2026, 7, 26, hour, minute, second)
            .unwrap()
    }

    fn raw_record(
        process_id: i32,
        process_name: &str,
        command_line: Option<&str>,
        timestamp: DateTime<Utc>,
        metrics_json: &str,
    ) -> NewProcessMetricRecord {
        NewProcessMetricRecord {
            process_id,
            process_name: process_name.to_owned(),
            command_line: command_line.map(str::to_owned),
            display_name: None,
            timestamp,
            metrics_json: metrics_json.to_owned(),
        }
    }

    fn aggregate_record(
        process_id: i32,
        process_name: &str,
        aggregation_level: AggregationLevel,
        timestamp: DateTime<Utc>,
        metrics_json: &str,
    ) -> NewAggregatedMetricRecord {
        NewAggregatedMetricRecord {
            process_id,
            process_name: process_name.to_owned(),
            aggregation_level,
            timestamp,
            metrics_json: metrics_json.to_owned(),
        }
    }

    fn save_aggregate_fixtures(
        store: &SqliteMetricStore,
        records: &[NewAggregatedMetricRecord],
    ) -> usize {
        for level in [
            AggregationLevel::Minute,
            AggregationLevel::Hour,
            AggregationLevel::Day,
        ] {
            let bucket_seconds = match level {
                AggregationLevel::Minute => 60,
                AggregationLevel::Hour => 60 * 60,
                AggregationLevel::Day => 24 * 60 * 60,
            };
            let level_records = records
                .iter()
                .filter(|record| record.aggregation_level == level)
                .collect::<Vec<_>>();
            let Some(first_timestamp) = level_records.iter().map(|record| record.timestamp).min()
            else {
                continue;
            };
            let last_timestamp = level_records
                .iter()
                .map(|record| record.timestamp)
                .max()
                .unwrap();
            let covered_from = DateTime::from_timestamp(
                first_timestamp.timestamp().div_euclid(bucket_seconds) * bucket_seconds,
                0,
            )
            .unwrap();
            let final_bucket = DateTime::from_timestamp(
                last_timestamp.timestamp().div_euclid(bucket_seconds) * bucket_seconds,
                0,
            )
            .unwrap();
            let step = chrono::Duration::seconds(bucket_seconds);
            let mut bucket_start = covered_from;
            while bucket_start <= final_bucket {
                let bucket_end = bucket_start + step;
                let bucket_records = level_records
                    .iter()
                    .filter(|record| {
                        record.timestamp >= bucket_start && record.timestamp < bucket_end
                    })
                    .map(|record| (*record).clone())
                    .collect::<Vec<_>>();
                store
                    .commit_rollup(
                        level,
                        covered_from,
                        bucket_start,
                        bucket_end,
                        &bucket_records,
                    )
                    .unwrap();
                bucket_start = bucket_end;
            }
        }
        records.len()
    }

    fn test_app(store: Arc<SqliteMetricStore>) -> Router {
        let state = AppState::new(
            store,
            Arc::new(MockClock::new(timestamp(0, 0, 0))),
            Arc::new(MockLhmReader::default()),
            Arc::new(MockRyzenAdjClient::unsupported()),
            ServicePaths::for_exe_dir("metrics-api-tests"),
            RuntimeConfig::default(),
        );

        router().with_state(state)
    }

    async fn get(app: Router, uri: &str) -> (StatusCode, Value) {
        let response = app
            .oneshot(Request::builder().uri(uri).body(Body::empty()).unwrap())
            .await
            .unwrap();
        let status = response.status();
        let body = to_bytes(response.into_body(), usize::MAX).await.unwrap();
        let json = serde_json::from_slice(&body).unwrap();

        (status, json)
    }

    #[tokio::test]
    async fn latest_filters_before_selecting_the_latest_frame_and_preserves_wire_json() {
        let store = Arc::new(SqliteMetricStore::open_in_memory().unwrap());
        let older = timestamp(10, 0, 0);
        let matching_frame = timestamp(10, 1, 0);
        let newer_non_matching_frame = timestamp(10, 2, 0);
        let records = [
            raw_record(
                7,
                "AlphaWorker",
                Some("--needle"),
                older,
                r#"{"cpu":{"value":1}}"#,
            ),
            raw_record(
                7,
                "AlphaWorker",
                Some("--needle"),
                matching_frame,
                r#"{"cpu":{"value":2}}"#,
            ),
            raw_record(
                8,
                "AlphaAgent",
                Some("--needle"),
                matching_frame,
                r#"{"cpu":{"value":3}}"#,
            ),
            raw_record(
                9,
                "alphaNewest",
                Some("--Needle"),
                newer_non_matching_frame,
                r#"{"cpu":{"value":4}}"#,
            ),
        ];
        assert_eq!(store.save_process_metrics(&records).unwrap(), records.len());
        let app = test_app(store);

        let (status, body) = get(app.clone(), "/api/v1/metrics/latest?processName=Alpha").await;
        assert_eq!(status, StatusCode::OK);
        assert_eq!(
            body,
            json!([
                {
                    "id": 3,
                    "processId": 8,
                    "processName": "AlphaAgent",
                    "commandLine": "--needle",
                    "displayName": null,
                    "timestamp": "2026-07-26T10:01:00Z",
                    "metricsJson": "{\"cpu\":{\"value\":3}}"
                },
                {
                    "id": 2,
                    "processId": 7,
                    "processName": "AlphaWorker",
                    "commandLine": "--needle",
                    "displayName": null,
                    "timestamp": "2026-07-26T10:01:00Z",
                    "metricsJson": "{\"cpu\":{\"value\":2}}"
                }
            ])
        );

        let (status, body) = get(app.clone(), "/api/v1/metrics/latest?processId=7").await;
        assert_eq!(status, StatusCode::OK);
        assert_eq!(body.as_array().unwrap().len(), 1);
        assert_eq!(body[0]["id"], 2);

        let (status, body) = get(
            app.clone(),
            "/api/v1/metrics/latest?processId=7&processName=AlphaAgent",
        )
        .await;
        assert_eq!(status, StatusCode::OK);
        assert_eq!(body, json!([]));

        let (status, body) = get(app.clone(), "/api/v1/metrics/latest?keyword=Needle").await;
        assert_eq!(status, StatusCode::OK);
        assert_eq!(body.as_array().unwrap().len(), 1);
        assert_eq!(body[0]["processId"], 9);

        let (status, body) = get(app, "/api/v1/metrics/latest?keyword=needle").await;
        assert_eq!(status, StatusCode::OK);
        assert_eq!(body.as_array().unwrap().len(), 2);
        assert_eq!(body[0]["processName"], "AlphaAgent");
        assert_eq!(body[1]["processName"], "AlphaWorker");
    }

    #[tokio::test]
    async fn history_selects_raw_or_aggregate_rows_with_inclusive_time_boundaries() {
        let store = Arc::new(SqliteMetricStore::open_in_memory().unwrap());
        let before = timestamp(9, 59, 59);
        let from = timestamp(10, 0, 0);
        let middle = timestamp(10, 1, 0);
        let to = timestamp(10, 2, 0);
        let after = timestamp(10, 2, 1);
        let raw_records = [
            raw_record(42, "Target", None, before, r#"{"sample":1}"#),
            raw_record(42, "Target", None, from, r#"{"sample":2}"#),
            raw_record(42, "Target", None, middle, r#"{"sample":3}"#),
            raw_record(42, "Target", None, to, r#"{"sample":4}"#),
            raw_record(42, "Target", None, after, r#"{"sample":5}"#),
            raw_record(99, "Other", None, middle, r#"{"sample":6}"#),
        ];
        assert_eq!(
            store.save_process_metrics(&raw_records).unwrap(),
            raw_records.len()
        );
        let aggregates = [
            aggregate_record(
                42,
                "Target",
                AggregationLevel::Minute,
                before,
                r#"{"sample":1}"#,
            ),
            aggregate_record(
                42,
                "Target",
                AggregationLevel::Minute,
                from,
                r#"{"sample":2}"#,
            ),
            aggregate_record(
                42,
                "Target",
                AggregationLevel::Minute,
                to,
                r#"{"sample":3}"#,
            ),
            aggregate_record(
                42,
                "Target",
                AggregationLevel::Minute,
                after,
                r#"{"sample":4}"#,
            ),
            aggregate_record(
                42,
                "Target",
                AggregationLevel::Hour,
                middle,
                r#"{"sample":5}"#,
            ),
        ];
        assert_eq!(
            save_aggregate_fixtures(&store, &aggregates),
            aggregates.len()
        );
        let app = test_app(store);

        let uri = "/api/v1/metrics/history?processId=42&from=2026-07-26T10:00:00Z&to=2026-07-26T10:02:00Z";
        let (status, body) = get(app.clone(), uri).await;
        assert_eq!(status, StatusCode::OK);
        assert_eq!(
            body.as_array()
                .unwrap()
                .iter()
                .map(|record| record["id"].as_i64().unwrap())
                .collect::<Vec<_>>(),
            [2, 3, 4]
        );
        assert!(body.as_array().unwrap().iter().all(|record| {
            record["processId"] == 42 && record.get("aggregationLevel").is_none()
        }));

        let uri = "/api/v1/metrics/history?processId=42&from=2026-07-26T10:00:00Z&to=2026-07-26T10:02:00Z&aggregation=MiNuTe";
        let (status, body) = get(app.clone(), uri).await;
        assert_eq!(status, StatusCode::OK);
        assert_eq!(
            body,
            json!([
                {
                    "id": 2,
                    "processId": 42,
                    "processName": "Target",
                    "aggregationLevel": 1,
                    "timestamp": "2026-07-26T10:00:00Z",
                    "metricsJson": "{\"sample\":2}"
                },
                {
                    "id": 3,
                    "processId": 42,
                    "processName": "Target",
                    "aggregationLevel": 1,
                    "timestamp": "2026-07-26T10:02:00Z",
                    "metricsJson": "{\"sample\":3}"
                }
            ])
        );

        let uri = "/api/v1/metrics/history?processId=42&from=2026-07-26T10:00:00Z&to=2026-07-26T10:02:00Z&aggregation=unknown";
        let (status, body) = get(app, uri).await;
        assert_eq!(status, StatusCode::OK);
        assert_eq!(
            body.as_array()
                .unwrap()
                .iter()
                .map(|record| record["id"].as_i64().unwrap())
                .collect::<Vec<_>>(),
            [2, 3]
        );
    }

    #[tokio::test]
    async fn processes_applies_inclusive_range_and_case_sensitive_keyword_filtering() {
        let store = Arc::new(SqliteMetricStore::open_in_memory().unwrap());
        let records = [
            raw_record(1, "Alpha", Some("--needle"), timestamp(10, 0, 0), "{}"),
            raw_record(1, "Alpha", Some("--needle"), timestamp(10, 1, 0), "{}"),
            raw_record(2, "Beta", Some("--Needle"), timestamp(10, 2, 0), "{}"),
            raw_record(3, "Outside", Some("--needle"), timestamp(10, 3, 0), "{}"),
        ];
        assert_eq!(store.save_process_metrics(&records).unwrap(), records.len());
        let app = test_app(store);
        let range = "from=2026-07-26T10:00:00Z&to=2026-07-26T10:02:00Z";

        let (status, body) = get(app.clone(), &format!("/api/v1/metrics/processes?{range}")).await;
        assert_eq!(status, StatusCode::OK);
        assert_eq!(
            body,
            json!([
                {
                    "processId": 2,
                    "processName": "Beta",
                    "lastSeen": "2026-07-26T10:02:00Z",
                    "recordCount": 1
                },
                {
                    "processId": 1,
                    "processName": "Alpha",
                    "lastSeen": "2026-07-26T10:01:00Z",
                    "recordCount": 2
                }
            ])
        );

        let (status, body) = get(
            app.clone(),
            &format!("/api/v1/metrics/processes?{range}&keyword=needle"),
        )
        .await;
        assert_eq!(status, StatusCode::OK);
        assert_eq!(body.as_array().unwrap().len(), 1);
        assert_eq!(body[0]["processName"], "Alpha");

        let (status, body) = get(
            app,
            &format!("/api/v1/metrics/processes?{range}&keyword=Needle"),
        )
        .await;
        assert_eq!(status, StatusCode::OK);
        assert_eq!(body.as_array().unwrap().len(), 1);
        assert_eq!(body[0]["processName"], "Beta");
    }

    #[tokio::test]
    async fn aggregations_requires_the_range_and_sorts_by_timestamp_then_process_name() {
        let store = Arc::new(SqliteMetricStore::open_in_memory().unwrap());
        let aggregates = [
            aggregate_record(
                1,
                "Before",
                AggregationLevel::Minute,
                timestamp(9, 59, 59),
                "{}",
            ),
            aggregate_record(
                2,
                "Zeta",
                AggregationLevel::Minute,
                timestamp(10, 0, 0),
                "{}",
            ),
            aggregate_record(
                3,
                "Alpha",
                AggregationLevel::Minute,
                timestamp(10, 0, 0),
                "{}",
            ),
            aggregate_record(
                4,
                "Beta",
                AggregationLevel::Minute,
                timestamp(10, 2, 0),
                "{}",
            ),
            aggregate_record(
                5,
                "After",
                AggregationLevel::Minute,
                timestamp(10, 2, 1),
                "{}",
            ),
        ];
        assert_eq!(
            save_aggregate_fixtures(&store, &aggregates),
            aggregates.len()
        );
        let app = test_app(store);
        let range = "from=2026-07-26T10:00:00Z&to=2026-07-26T10:02:00Z";

        let (status, body) = get(
            app.clone(),
            &format!("/api/v1/metrics/aggregations?{range}"),
        )
        .await;
        assert_eq!(status, StatusCode::OK);
        assert_eq!(
            body.as_array()
                .unwrap()
                .iter()
                .map(|record| record["id"].as_i64().unwrap())
                .collect::<Vec<_>>(),
            [3, 2, 4]
        );

        let (status, body) = get(
            app,
            &format!("/api/v1/metrics/aggregations?{range}&aggregation=raw"),
        )
        .await;
        assert_eq!(status, StatusCode::OK);
        assert_eq!(
            body.as_array()
                .unwrap()
                .iter()
                .map(|record| record["id"].as_i64().unwrap())
                .collect::<Vec<_>>(),
            [3, 2, 4]
        );
    }

    #[tokio::test]
    async fn required_and_malformed_query_values_return_json_bad_requests() {
        let store = Arc::new(SqliteMetricStore::open_in_memory().unwrap());
        let app = test_app(store);

        let (status, body) = get(app.clone(), "/api/v1/metrics/history").await;
        assert_eq!(status, StatusCode::BAD_REQUEST);
        assert_eq!(body, json!({ "error": "processId is required" }));

        let (status, body) = get(
            app.clone(),
            "/api/v1/metrics/history?processId=not-an-integer",
        )
        .await;
        assert_eq!(status, StatusCode::BAD_REQUEST);
        assert_eq!(
            body,
            json!({ "error": "processId must be a 32-bit integer" })
        );

        let (status, body) = get(
            app.clone(),
            "/api/v1/metrics/aggregations?to=2026-07-26T10:00:00Z",
        )
        .await;
        assert_eq!(status, StatusCode::BAD_REQUEST);
        assert_eq!(body, json!({ "error": "from is required" }));

        let (status, body) = get(
            app.clone(),
            "/api/v1/metrics/aggregations?from=2026-07-26T10:00:00Z",
        )
        .await;
        assert_eq!(status, StatusCode::BAD_REQUEST);
        assert_eq!(body, json!({ "error": "to is required" }));

        let (status, body) = get(app, "/api/v1/metrics/processes?from=not-a-timestamp").await;
        assert_eq!(status, StatusCode::BAD_REQUEST);
        assert_eq!(body, json!({ "error": "from must be a valid timestamp" }));
    }
}
