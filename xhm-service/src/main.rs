//! xhm-service 可执行入口：组装路由、初始化生产组件、在 35179 并行运行。
//!
//! 对齐 C# `Program.cs`：路径基于 `current_exe().parent()`、graceful shutdown、
//! 管理员检测，以及采集 worker 与 LHM bridge 的确定性回收。

use std::{net::SocketAddr, sync::Arc};

use tokio::net::TcpListener;
use tracing_subscriber::EnvFilter;
use xhm_core::traits::{Clock, LhmReader, MetricStore, RyzenAdjClient, SystemClock};
use xhm_service::{
    db::SqliteMetricStore,
    lhm::LhmBridgeManager,
    power::{is_supported_power_platform, ProductionRyzenAdjClient},
    state::{RuntimeConfig, ServicePaths},
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

    let state = AppState::new(store, clock, lhm, ryzenadj, paths, runtime);
    let mut worker = ServiceWorker::start(state.clone());
    let app = xhm_service::app(state);

    let addr = listen_addr(port);
    tracing::info!("listening on {addr}");
    let server_result = match TcpListener::bind(addr).await {
        Ok(listener) => {
            axum::serve(listener, app)
                .with_graceful_shutdown(shutdown_signal())
                .await
        }
        Err(error) => Err(error),
    };

    worker.shutdown().await;
    // 优雅回收 bridge 子进程：等待 500ms 后强杀（由 manager.shutdown 内部处理）。
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
    use xhm_service::DEFAULT_SERVICE_PORT;

    #[test]
    fn service_listener_is_loopback_only_on_fixed_port() {
        let address = listen_addr(DEFAULT_SERVICE_PORT);

        assert!(address.ip().is_loopback());
        assert_eq!(address.ip().to_string(), "127.0.0.1");
        assert_eq!(address.port(), 35_179);
    }
}
