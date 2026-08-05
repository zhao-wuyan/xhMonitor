//! Shared state and fixed module boundaries for the xhMonitor service.

#![deny(rust_2018_idioms)]
#![warn(missing_debug_implementations)]

use std::collections::HashSet;

use axum::Router;
use tower_http::cors::{AllowHeaders, AllowMethods, AllowOrigin, CorsLayer};

pub mod api;
pub mod db;
pub mod lhm;
pub mod power;
pub mod realtime;
pub mod state;
pub mod web;
pub mod worker;

pub use state::{
    AppState, PushTarget, RoutedPushEvent, RuntimeConfig, ServicePaths, DEFAULT_ALLOWED_ORIGINS,
    DEFAULT_HUB_PATH, DEFAULT_SERVICE_PORT, DEFAULT_SSE_PATH,
};
pub use worker::ServiceWorker;

/// 组装全部路由 + CORS，返回可 `axum::serve` 的 `Router`。
///
/// 从 `state.runtime` 的 `try_read` 读取 hub/sse path 和 allowed origins——
/// 仅在启动时调用，不会 contended。
pub fn app(state: AppState) -> Router {
    let allowed_origins = state
        .runtime
        .try_read()
        .expect("runtime lock not contended at startup")
        .allowed_origins
        .clone();
    routes(state).layer(build_cors(&allowed_origins))
}

pub(crate) fn routes(state: AppState) -> Router {
    let runtime = state
        .runtime
        .try_read()
        .expect("runtime lock not contended at startup");
    let hub_path = runtime.hub_path.clone();
    let sse_path = runtime.sse_path.clone();
    drop(runtime);

    api::config::router()
        .merge(api::metrics::router())
        .merge(api::power::router())
        .merge(api::widget::router())
        .merge(realtime::router(&hub_path, &sse_path))
        .with_state(state)
}

fn build_cors(origins: &[String]) -> CorsLayer {
    let allowed: HashSet<String> = origins.iter().cloned().collect();
    CorsLayer::new()
        .allow_origin(AllowOrigin::predicate(move |origin, _| {
            origin
                .to_str()
                .map(|o| allowed.contains(o))
                .unwrap_or(false)
        }))
        .allow_methods(AllowMethods::mirror_request())
        .allow_headers(AllowHeaders::mirror_request())
        .allow_credentials(true)
}
