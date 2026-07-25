// Rust Service POC — src/main.rs
// 验证目标：
// 1. LHM bridge subprocess IPC（系统级指标唯一来源）
// 2. axum SSE 端点推送 LHM 快照
// 3. windows crate 进程级指标采集（per-process，独立于 LHM）
// 4. rusqlite 持久化写入
// 5. 打印本进程 Private Bytes

use std::path::PathBuf;
use axum::{
    extract::State,
    response::sse::{Event, KeepAlive, Sse},
    routing::get,
    Router,
};
use tokio::sync::broadcast;
use tokio_stream::{wrappers::BroadcastStream, StreamExt};
use tracing_subscriber::fmt;

mod lhm_bridge;
mod process_metrics;

const SSE_PORT: u16 = 35199; // 避免与生产 Service 冲突

#[derive(Clone)]
struct AppState {
    tx: broadcast::Sender<lhm_bridge::LhmSnapshot>,
}

#[tokio::main]
async fn main() {
    fmt::init();

    // LHM bridge 路径：优先 LHM_BRIDGE_EXE 环境变量，否则找相邻目录
    let bridge_exe = std::env::var("LHM_BRIDGE_EXE").unwrap_or_else(|_| {
        // 尝试相对路径（从 poc/rust-service/target 运行时）
        let candidates = [
            PathBuf::from("../../lhm-bridge/bin/Release/net8.0/win-x64/publish/lhm-bridge.exe"),
            PathBuf::from("lhm-bridge.exe"),
        ];
        candidates
            .iter()
            .find(|p| p.exists())
            .map(|p| p.to_string_lossy().into_owned())
            .unwrap_or_else(|| {
                eprintln!("[poc] WARNING: lhm-bridge.exe not found; set LHM_BRIDGE_EXE env var");
                eprintln!("[poc] Build with: cd poc/lhm-bridge && dotnet publish -c Release");
                "lhm-bridge.exe".to_string()
            })
    });

    tracing::info!("[poc] lhm-bridge path: {bridge_exe}");

    let (tx, _rx) = broadcast::channel::<lhm_bridge::LhmSnapshot>(32);
    let state = AppState { tx: tx.clone() };

    // ── 1. LHM bridge task（系统级指标）────────────────────────────────────
    {
        let tx = tx.clone();
        let exe = bridge_exe.clone();
        tokio::spawn(async move {
            lhm_bridge::run(&exe, tx).await;
        });
    }

    // ── 2. 进程级指标采集（每 3s，独立于 LHM）──────────────────────────────
    tokio::spawn(async move {
        loop {
            let procs = tokio::task::spawn_blocking(process_metrics::collect)
                .await
                .unwrap_or_default();
            let top5: Vec<_> = {
                let mut v = procs;
                v.sort_by(|a, b| b.working_set_mb.partial_cmp(&a.working_set_mb).unwrap());
                v.into_iter().take(5).collect()
            };
            tracing::info!("[process_metrics] top-5 by working set:");
            for p in &top5 {
                tracing::info!("  {:>6}  {:.1} MiB  {}", p.pid, p.working_set_mb, p.name);
            }
            tokio::time::sleep(std::time::Duration::from_secs(3)).await;
        }
    });

    // ── 3. rusqlite 验证写入────────────────────────────────────────────────
    {
        let tx_db = tx.clone();
        tokio::spawn(async move {
            let db_path = std::env::temp_dir().join("xhm-poc-service.db");
            tracing::info!("[sqlite] db path: {}", db_path.display());
            let db = rusqlite::Connection::open(&db_path).expect("open db");
            db.execute_batch(
                "CREATE TABLE IF NOT EXISTS lhm_snapshot (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts         TEXT,
                    cpu_temp   REAL,
                    gpu_temp   REAL,
                    gpu_load   REAL
                );"
            ).expect("create table");
            tracing::info!("[sqlite] table ready");

            let mut rx = tx_db.subscribe();
            loop {
                if let Ok(snap) = rx.recv().await {
                    db.execute(
                        "INSERT INTO lhm_snapshot (ts, cpu_temp, gpu_temp, gpu_load) VALUES (?1,?2,?3,?4)",
                        rusqlite::params![snap.ts, snap.cpu_temp, snap.gpu_temp, snap.gpu_load],
                    ).ok();
                }
            }
        });
    }

    // ── 4. 本进程内存定期上报──────────────────────────────────────────────
    tokio::spawn(async move {
        loop {
            print_memory();
            tokio::time::sleep(std::time::Duration::from_secs(10)).await;
        }
    });

    // ── 5. axum SSE server────────────────────────────────────────────────
    let app = Router::new()
        .route("/hubs/metrics/system",  get(sse_system_handler))
        .with_state(state);

    let listener = tokio::net::TcpListener::bind(format!("127.0.0.1:{SSE_PORT}"))
        .await
        .expect("bind");
    tracing::info!("[poc] SSE server on http://127.0.0.1:{SSE_PORT}/hubs/metrics/system");
    tracing::info!("[poc] curl -N http://127.0.0.1:{SSE_PORT}/hubs/metrics/system");

    axum::serve(listener, app).await.expect("serve");
}

async fn sse_system_handler(
    State(state): State<AppState>,
) -> Sse<impl futures_core::Stream<Item = Result<Event, std::convert::Infallible>>> {
    let rx = state.tx.subscribe();
    let stream = BroadcastStream::new(rx).filter_map(|msg| {
        msg.ok().map(|snap| {
            let data = serde_json::to_string(&snap).unwrap_or_default();
            Ok(Event::default().event("system-usage").data(data))
        })
    });
    Sse::new(stream).keep_alive(KeepAlive::default())
}

fn print_memory() {
    #[cfg(windows)]
    unsafe {
        use windows::Win32::System::{
            ProcessStatus::{GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS},
            Threading::GetCurrentProcess,
        };
        let mut pmc = PROCESS_MEMORY_COUNTERS::default();
        pmc.cb = std::mem::size_of::<PROCESS_MEMORY_COUNTERS>() as u32;
        if GetProcessMemoryInfo(GetCurrentProcess(), &mut pmc, pmc.cb).is_ok() {
            tracing::info!(
                "[memory] WorkingSet={:.1} MiB  PrivateBytes={:.1} MiB",
                pmc.WorkingSetSize as f64 / 1_048_576.0,
                pmc.PagefileUsage  as f64 / 1_048_576.0,
            );
        }
    }
}
