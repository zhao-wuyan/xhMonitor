//! xhm-service 可执行入口：组装路由、初始化生产组件、在 35179 并行运行。
//!
//! 对齐 C# `Program.cs`：路径基于 `current_exe().parent()`、graceful shutdown、
//! 管理员检测，以及采集 worker 与 LHM bridge 的确定性回收。

use std::{net::SocketAddr, sync::Arc};

use tokio::net::TcpListener;
use tokio_util::sync::CancellationToken;
use tracing_subscriber::EnvFilter;
use xhm_core::traits::{Clock, LhmReader, MetricStore, RyzenAdjClient, SystemClock};
use xhm_service::{
    db::SqliteMetricStore,
    lhm::LhmBridgeManager,
    power::{is_supported_power_platform, ProductionRyzenAdjClient},
    state::{RuntimeConfig, ServicePaths},
    web::{web_app, SecurityConfig, DEFAULT_WEB_PORT},
    AppState, ServiceWorker,
};

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(EnvFilter::from_default_env())
        .init();

    let paths = ServicePaths::new()?;
    tracing::info!(db = %paths.db_path.display(), "opening database");
    let store: Arc<dyn MetricStore> = Arc::new(SqliteMetricStore::open(&paths.db_path)?);
    let power_platform_supported = is_supported_power_platform();
    if !power_platform_supported {
        tracing::warn!("power control disabled: AMD GPU + Ryzen AI Max 395 platform gate failed");
    }
    let ryzenadj: Arc<dyn RyzenAdjClient> = Arc::new(ProductionRyzenAdjClient::new(
        &paths,
        power_platform_supported,
    ));

    // LHM bridge 是可选的：路径不存在或非管理员时降级运行。
    let mut bridge_manager: Option<LhmBridgeManager> = None;
    let lhm: Arc<dyn LhmReader> = match LhmBridgeManager::start(&paths.lhm_bridge_path) {
        Ok((reader, manager)) => {
            tracing::info!("lhm-bridge started");
            bridge_manager = Some(manager);
            reader
        }
        Err(error) => {
            tracing::warn!(%error, "lhm-bridge unavailable; running without hardware sensors");
            Arc::new(xhm_core::traits::MockLhmReader::default())
        }
    };

    let clock: Arc<dyn Clock> = Arc::new(SystemClock);
    let runtime = RuntimeConfig {
        process_keywords: load_process_keywords(store.as_ref()),
        ..RuntimeConfig::default()
    };
    let port = runtime.port;
    let security_config = match SecurityConfig::load(store.as_ref()) {
        Ok(config) => config,
        Err(error) => {
            tracing::error!(%error, "security settings unavailable; web listener restricted to loopback");
            SecurityConfig::default()
        }
    };

    let state = AppState::new(store, clock, lhm, ryzenadj, paths, runtime);
    let mut worker = ServiceWorker::start(state.clone());
    let internal_app = xhm_service::app(state.clone());
    let requested_lan = security_config.enable_lan_access;
    let (web_app, enable_lan) = match web_app(
        state.clone(),
        security_config,
        &state.paths.wwwroot_path,
    ) {
        Ok(app) => (app, requested_lan),
        Err(error) => {
            tracing::error!(%error, "unsafe web security configuration; web listener restricted to loopback");
            (
                web_app(
                    state.clone(),
                    SecurityConfig::default(),
                    &state.paths.wwwroot_path,
                )
                .expect("default loopback security configuration must be valid"),
                false,
            )
        }
    };
    if !state.paths.wwwroot_path.join("index.html").is_file() {
        tracing::warn!(
            path = %state.paths.wwwroot_path.display(),
            "web assets unavailable; API and hub remain available on the web listener"
        );
    }

    let internal_addr = listen_addr(port);
    let internal_listener = TcpListener::bind(internal_addr).await?;
    tracing::info!("internal service listening on {internal_addr}");

    let web_addr = web_listen_addr(DEFAULT_WEB_PORT, enable_lan);
    let web_listener = match TcpListener::bind(web_addr).await {
        Ok(listener) => {
            tracing::info!(lan = enable_lan, "web gateway listening on {web_addr}");
            Some(listener)
        }
        Err(error) => {
            tracing::error!(%error, "web gateway could not bind {web_addr}");
            None
        }
    };

    let cancellation = CancellationToken::new();
    let internal_cancellation = cancellation.clone();
    let mut internal_task = tokio::spawn(async move {
        axum::serve(
            internal_listener,
            internal_app.into_make_service_with_connect_info::<SocketAddr>(),
        )
        .with_graceful_shutdown(internal_cancellation.cancelled_owned())
        .await
    });
    let web_cancellation = cancellation.clone();
    let mut web_task = tokio::spawn(async move {
        if let Some(listener) = web_listener {
            axum::serve(
                listener,
                web_app.into_make_service_with_connect_info::<SocketAddr>(),
            )
            .with_graceful_shutdown(web_cancellation.cancelled_owned())
            .await
        } else {
            web_cancellation.cancelled_owned().await;
            Ok(())
        }
    });

    let mut server_result = tokio::select! {
        _ = shutdown_signal() => Ok(()),
        result = &mut internal_task => flatten_server_result(result),
        result = &mut web_task => flatten_server_result(result),
    };
    cancellation.cancel();

    if !internal_task.is_finished() {
        let result = flatten_server_result(internal_task.await);
        if server_result.is_ok() {
            server_result = result;
        }
    }
    if !web_task.is_finished() {
        let result = flatten_server_result(web_task.await);
        if server_result.is_ok() {
            server_result = result;
        }
    }

    worker.shutdown().await;
    if let Some(manager) = bridge_manager.as_mut() {
        manager.shutdown().await;
    }
    server_result?;

    tracing::info!("service stopped");
    Ok(())
}

fn load_process_keywords(store: &dyn MetricStore) -> Vec<String> {
    let settings = match store.list_settings() {
        Ok(settings) => settings,
        Err(error) => {
            tracing::warn!(%error, "failed to load process keywords; using empty defaults");
            return Vec::new();
        }
    };
    let Some(serialized) = settings
        .iter()
        .find(|setting| setting.category == "DataCollection" && setting.key == "ProcessKeywords")
        .map(|setting| setting.value.as_str())
    else {
        return Vec::new();
    };

    match serde_json::from_str::<Option<Vec<String>>>(serialized) {
        Ok(keywords) => keywords.unwrap_or_default(),
        Err(error) => {
            tracing::warn!(%error, "failed to parse process keywords; using empty defaults");
            Vec::new()
        }
    }
}

fn listen_addr(port: u16) -> SocketAddr {
    SocketAddr::from(([127, 0, 0, 1], port))
}
fn web_listen_addr(port: u16, enable_lan: bool) -> SocketAddr {
    if enable_lan {
        SocketAddr::from(([0, 0, 0, 0], port))
    } else {
        SocketAddr::from(([127, 0, 0, 1], port))
    }
}

fn flatten_server_result(
    result: Result<std::io::Result<()>, tokio::task::JoinError>,
) -> anyhow::Result<()> {
    result??;
    Ok(())
}

async fn shutdown_signal() {
    let ctrl_c = async {
        tokio::signal::ctrl_c()
            .await
            .expect("failed to install Ctrl+C handler");
    };

    #[cfg(unix)]
    let terminate = async {
        tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())
            .expect("failed to install SIGTERM handler")
            .recv()
            .await;
    };

    #[cfg(not(unix))]
    let terminate = std::future::pending::<()>();

    tokio::select! {
        _ = ctrl_c => {},
        _ = terminate => {},
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use xhm_service::{web::DEFAULT_WEB_PORT, DEFAULT_SERVICE_PORT};

    #[test]
    fn service_listener_is_loopback_only_on_fixed_port() {
        let address = listen_addr(DEFAULT_SERVICE_PORT);

        assert!(address.ip().is_loopback());
        assert_eq!(address.ip().to_string(), "127.0.0.1");
        assert_eq!(address.port(), 35_179);
    }

    #[test]
    fn web_listener_switches_between_loopback_and_all_interfaces() {
        let local = web_listen_addr(DEFAULT_WEB_PORT, false);
        let lan = web_listen_addr(DEFAULT_WEB_PORT, true);

        assert!(local.ip().is_loopback());
        assert_eq!(local.port(), 35_180);
        assert!(lan.ip().is_unspecified());
        assert_eq!(lan.port(), 35_180);
    }
}
