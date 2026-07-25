// Rust Service POC — src/lhm_bridge.rs
// 验证目标：spawn LHM bridge subprocess，读取 JSON Lines，解析为 LhmSnapshot
// 架构边界：系统级指标全部来自此 bridge，不从 PDH/DXGI 重新采集

use std::process::Stdio;
use tokio::process::Command;
use tokio::io::{AsyncBufReadExt, BufReader};
use tokio::sync::broadcast;
use serde::Deserialize;

#[derive(Debug, Clone, Deserialize, serde::Serialize)]
pub struct LhmSnapshot {
    pub ts:              String,
    pub cpu_temp:        Option<f64>,
    pub gpu_temp:        Option<f64>,
    pub gpu_load:        Option<f64>,
    pub net_up_mbps:     f64,
    pub net_down_mbps:   f64,
    pub disk_read_mbps:  f64,
    pub disk_write_mbps: f64,
}

/// Spawn the LHM bridge executable and pump JSON Lines into `tx`.
/// `bridge_exe`: path to the published lhm-bridge.exe
pub async fn run(bridge_exe: &str, tx: broadcast::Sender<LhmSnapshot>) {
    loop {
        tracing::info!("[lhm-bridge] spawning {bridge_exe}");

        let mut child = match Command::new(bridge_exe)
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .kill_on_drop(true)
            .spawn()
        {
            Ok(c) => c,
            Err(e) => {
                tracing::error!("[lhm-bridge] spawn failed: {e}; retry in 5s");
                tokio::time::sleep(std::time::Duration::from_secs(5)).await;
                continue;
            }
        };

        let stdout = child.stdout.take().expect("no stdout");
        let stderr = child.stderr.take().expect("no stderr");

        // Log stderr in background
        tokio::spawn(async move {
            let mut lines = BufReader::new(stderr).lines();
            while let Ok(Some(line)) = lines.next_line().await {
                tracing::warn!("[lhm-bridge stderr] {line}");
            }
        });

        let mut lines = BufReader::new(stdout).lines();
        loop {
            match lines.next_line().await {
                Ok(Some(line)) => {
                    match serde_json::from_str::<LhmSnapshot>(&line) {
                        Ok(snap) => {
                            tracing::debug!(
                                "[lhm-bridge] cpu={:?}°C gpu={:?}°C load={:?}%",
                                snap.cpu_temp, snap.gpu_temp, snap.gpu_load
                            );
                            let _ = tx.send(snap);
                        }
                        Err(e) => tracing::warn!("[lhm-bridge] parse error: {e}  line={line}"),
                    }
                }
                Ok(None) => {
                    tracing::warn!("[lhm-bridge] stdout closed; restarting in 3s");
                    break;
                }
                Err(e) => {
                    tracing::error!("[lhm-bridge] read error: {e}; restarting in 3s");
                    break;
                }
            }
        }

        let _ = child.wait().await;
        tokio::time::sleep(std::time::Duration::from_secs(3)).await;
    }
}
