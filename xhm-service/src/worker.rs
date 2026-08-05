use std::{fmt, time::Duration};

use sysinfo::{ProcessRefreshKind, ProcessesToUpdate, System, UpdateKind};
use tokio::{
    sync::watch,
    task::JoinHandle,
    time::{sleep_until, Instant},
};
use xhm_core::{
    models::{LhmSnapshot, MetricValue, MetricValueMap, NewProcessMetricRecord, PowerStatus},
    wire::{
        select_lite_subset, DiskUsagePayload, HardwareLimitsPayload, ProcessMetadataPayload,
        ProcessMetadataSnapshot, ProcessMetricSnapshot, ProcessSnapshotPayload, PushEvent,
        SubscriptionMode, SystemUsagePayload,
    },
};

use crate::state::{AppState, PushTarget, RoutedPushEvent};

const BYTES_PER_MB: f64 = 1024.0 * 1024.0;
const HARDWARE_REFRESH_INTERVAL: Duration = Duration::from_secs(60 * 60);

/// Owns the service's system and process collection task.
pub struct ServiceWorker {
    shutdown_tx: watch::Sender<bool>,
    task: Option<JoinHandle<()>>,
}

impl fmt::Debug for ServiceWorker {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let task_finished = self
            .task
            .as_ref()
            .map(JoinHandle::is_finished)
            .unwrap_or(true);

        formatter
            .debug_struct("ServiceWorker")
            .field("shutdown_requested", &*self.shutdown_tx.borrow())
            .field("task_finished", &task_finished)
            .finish()
    }
}

impl ServiceWorker {
    /// Starts collection immediately on the current Tokio runtime.
    pub fn start(state: AppState) -> ServiceWorker {
        let (shutdown_tx, shutdown_rx) = watch::channel(false);
        let task = tokio::spawn(run_worker(state, shutdown_rx));

        ServiceWorker {
            shutdown_tx,
            task: Some(task),
        }
    }

    /// Requests shutdown and waits for the collection task to finish.
    ///
    /// Calling this method more than once is harmless.
    pub async fn shutdown(&mut self) {
        let _ = self.shutdown_tx.send(true);
        let Some(task) = self.task.take() else {
            return;
        };

        if let Err(error) = task.await {
            tracing::error!(%error, "service worker task failed during shutdown");
        }
    }
}

impl Drop for ServiceWorker {
    fn drop(&mut self) {
        let _ = self.shutdown_tx.send(true);
    }
}

async fn run_worker(state: AppState, mut shutdown_rx: watch::Receiver<bool>) {
    let mut system = System::new();
    let mut max_memory = 0.0;
    let mut max_vram = 0.0;
    let mut next_hardware_refresh = Instant::now();
    let mut next_collection = Instant::now();

    loop {
        if *shutdown_rx.borrow() {
            break;
        }

        let now = Instant::now();
        if now >= next_hardware_refresh {
            system.refresh_memory();
            max_memory = round_one(bytes_to_mb(system.total_memory()));

            // VRAM capacity from LHM bridge snapshot (if available)
            if let Some(snapshot) = state.lhm.snapshot().as_ref() {
                if let Some(total) = snapshot.gpu_memory_total_mb {
                    if total.is_finite() && total > 0.0 {
                        max_vram = round_one(total);
                    }
                }
            }

            send_event(
                &state,
                PushTarget::All,
                PushEvent::HardwareLimits(HardwareLimitsPayload {
                    timestamp: state.clock.now_local(),
                    max_memory,
                    max_vram,
                }),
            );
            next_hardware_refresh = Instant::now() + HARDWARE_REFRESH_INTERVAL;
        }

        if *shutdown_rx.borrow() {
            break;
        }

        if Instant::now() >= next_collection {
            let (interval_seconds, keywords) = {
                let runtime = state.runtime.read().await;
                (
                    runtime.interval_seconds.max(1),
                    normalize_keywords(&runtime.process_keywords),
                )
            };

            collect_cycle(&state, &mut system, max_memory, &keywords).await;
            next_collection = Instant::now() + Duration::from_secs(interval_seconds);
        }

        let deadline = next_hardware_refresh.min(next_collection);
        tokio::select! {
            changed = shutdown_rx.changed() => {
                match changed {
                    Ok(()) if *shutdown_rx.borrow() => break,
                    Ok(()) => {}
                    Err(_) => break,
                }
            }
            _ = sleep_until(deadline) => {}
        }
    }
}

async fn collect_cycle(
    state: &AppState,
    system: &mut System,
    max_memory: f64,
    keywords: &[String],
) {
    system.refresh_cpu_usage();
    system.refresh_memory();

    let local_timestamp = state.clock.now_local();
    let lhm = state.lhm.snapshot();
    let power = if state.ryzenadj.is_supported() {
        state.ryzenadj.read_status()
    } else {
        None
    };
    let usage = build_system_usage(
        local_timestamp,
        f64::from(system.global_cpu_usage()),
        system.used_memory(),
        max_memory,
        lhm.as_ref(),
        power,
    );
    send_event(state, PushTarget::All, PushEvent::SystemUsage(usage));

    if keywords.is_empty() {
        return;
    }

    system.refresh_processes_specifics(
        ProcessesToUpdate::All,
        true,
        ProcessRefreshKind::new()
            .with_cpu()
            .with_memory()
            .with_cmd(UpdateKind::OnlyIfNotSet),
    );

    let timestamp = state.clock.now_utc();
    let wire_timestamp = timestamp.with_timezone(&chrono::Local);
    let mut records = Vec::new();
    let mut snapshots = Vec::new();
    let mut metadata = Vec::new();

    for process in system.processes().values() {
        let process_name = process.name().to_string_lossy().into_owned();
        let command_line = command_line(process.cmd());
        if !matches_keywords(&process_name, &command_line, keywords) {
            continue;
        }

        let Ok(process_id) = i32::try_from(process.pid().as_u32()) else {
            tracing::warn!(
                pid = process.pid().as_u32(),
                "process PID exceeds i32 range"
            );
            continue;
        };
        let vram_mb = lhm
            .as_ref()
            .and_then(|snapshot| snapshot.process_vram_mb.get(&process_id))
            .copied()
            .unwrap_or(0.0);

        match build_process_sample(
            process_id,
            process_name,
            command_line,
            f64::from(process.cpu_usage()),
            process.memory(),
            vram_mb,
            timestamp,
        ) {
            Ok(sample) => {
                records.push(sample.record);
                snapshots.push(sample.snapshot);
                metadata.push(sample.metadata);
            }
            Err(error) => {
                tracing::error!(process_id, %error, "failed to serialize process metrics");
            }
        }
    }

    if records.is_empty() {
        return;
    }

    snapshots.sort_unstable_by_key(|process| process.process_id);
    metadata.sort_unstable_by_key(|process| process.process_id);
    records.sort_unstable_by_key(|record| record.process_id);

    let expected_count = records.len();
    let store = state.store.clone();
    match tokio::task::spawn_blocking(move || store.save_process_metrics(&records)).await {
        Ok(Ok(saved)) if saved != expected_count => {
            tracing::warn!(
                saved,
                expected_count,
                "process metric store saved a partial frame"
            );
        }
        Ok(Ok(_)) => {}
        Ok(Err(error)) => {
            tracing::error!(%error, "failed to save process metric frame");
        }
        Err(error) => {
            tracing::error!(%error, "process metric store task failed");
        }
    }

    route_process_events(state, wire_timestamp, snapshots, metadata).await;
}

fn normalize_keywords(keywords: &[String]) -> Vec<String> {
    keywords
        .iter()
        .map(|keyword| keyword.trim())
        .filter(|keyword| !keyword.is_empty())
        .map(str::to_lowercase)
        .collect()
}

fn matches_keywords(process_name: &str, command_line: &str, keywords: &[String]) -> bool {
    if keywords.is_empty() {
        return false;
    }

    let process_name = process_name.to_lowercase();
    let command_line = command_line.to_lowercase();
    keywords.iter().any(|keyword| {
        process_name.contains(keyword.as_str()) || command_line.contains(keyword.as_str())
    })
}

fn command_line(arguments: &[std::ffi::OsString]) -> String {
    let mut output = String::new();
    for argument in arguments {
        if !output.is_empty() {
            output.push(' ');
        }
        output.push_str(&argument.to_string_lossy());
    }
    output
}

struct ProcessSample {
    record: NewProcessMetricRecord,
    snapshot: ProcessMetricSnapshot,
    metadata: ProcessMetadataSnapshot,
}

fn build_process_sample(
    process_id: i32,
    process_name: String,
    command_line: String,
    cpu_usage: f64,
    memory_bytes: u64,
    vram_mb: f64,
    timestamp: chrono::DateTime<chrono::Utc>,
) -> Result<ProcessSample, serde_json::Error> {
    let cpu_usage = round_one(nonnegative(cpu_usage));
    let memory_mb = round_one(bytes_to_mb(memory_bytes));
    let mut metrics = MetricValueMap::new();
    metrics.insert(
        "cpu".to_owned(),
        MetricValue {
            value: cpu_usage,
            unit: Some("%".to_owned()),
        },
    );
    metrics.insert(
        "memory".to_owned(),
        MetricValue {
            value: memory_mb,
            unit: Some("MB".to_owned()),
        },
    );
    metrics.insert(
        "vram".to_owned(),
        MetricValue {
            value: round_one(nonnegative(vram_mb)),
            unit: Some("MB".to_owned()),
        },
    );

    let metrics_json = serde_json::to_string(&metrics)?;
    let wire_metrics = metrics
        .into_iter()
        .map(|(metric, value)| (metric, value.value))
        .collect();
    let stored_command_line = if command_line.is_empty() {
        None
    } else {
        Some(command_line.clone())
    };
    let display_name = process_name.clone();

    Ok(ProcessSample {
        record: NewProcessMetricRecord {
            process_id,
            process_name: process_name.clone(),
            command_line: stored_command_line,
            display_name: Some(display_name.clone()),
            timestamp,
            metrics_json,
        },
        snapshot: ProcessMetricSnapshot {
            process_id,
            process_name: process_name.clone(),
            has_meta: true,
            command_line: Some(command_line.clone()),
            display_name: Some(display_name.clone()),
            metrics: wire_metrics,
        },
        metadata: ProcessMetadataSnapshot {
            process_id,
            process_name,
            command_line,
            display_name,
        },
    })
}

fn build_system_usage(
    timestamp: chrono::DateTime<chrono::Local>,
    total_cpu: f64,
    used_memory_bytes: u64,
    max_memory: f64,
    lhm: Option<&LhmSnapshot>,
    power: Option<PowerStatus>,
) -> SystemUsagePayload {
    let (
        total_gpu,
        cpu_temperature,
        gpu_temperature,
        upload_speed,
        download_speed,
        disks,
        vram_used,
        vram_total,
    ) = match lhm {
        Some(snapshot) => {
            let disks = snapshot
                .disks
                .iter()
                .map(|disk| DiskUsagePayload {
                    name: disk.name.clone(),
                    total_bytes: disk.total_bytes,
                    used_bytes: disk.used_bytes,
                    read_speed: optional_nonnegative(disk.read_mbps),
                    write_speed: optional_nonnegative(disk.write_mbps),
                })
                .collect();

            (
                round_one(nonnegative(snapshot.gpu_load.unwrap_or(0.0))),
                finite(snapshot.cpu_temp),
                finite(snapshot.gpu_temp),
                nonnegative(snapshot.net_up_mbps),
                nonnegative(snapshot.net_down_mbps),
                disks,
                finite(snapshot.gpu_memory_used_mb).unwrap_or(0.0),
                finite(snapshot.gpu_memory_total_mb).unwrap_or(0.0),
            )
        }
        None => (0.0, None, None, 0.0, 0.0, Vec::new(), 0.0, 0.0),
    };
    let total_vram = round_one(nonnegative(vram_used));
    let max_vram = round_one(nonnegative(vram_total));

    let power_available = power.is_some();
    let (total_power, max_power, power_scheme_index) = match power {
        Some(status) => (
            round_one(nonnegative(status.current_watts)),
            round_one(nonnegative(status.limit_watts)),
            status.scheme_index,
        ),
        None => (0.0, 0.0, None),
    };

    SystemUsagePayload {
        timestamp,
        total_cpu: round_one(nonnegative(total_cpu)),
        total_gpu,
        cpu_temperature: cpu_temperature.map(round_one),
        gpu_temperature: gpu_temperature.map(round_one),
        total_memory: round_one(bytes_to_mb(used_memory_bytes)),
        total_vram,
        upload_speed,
        download_speed,
        max_memory,
        max_vram,
        disks,
        power_available,
        total_power,
        max_power,
        power_scheme_index,
    }
}

async fn route_process_events(
    state: &AppState,
    timestamp: chrono::DateTime<chrono::Local>,
    processes: Vec<ProcessMetricSnapshot>,
    metadata: Vec<ProcessMetadataSnapshot>,
) {
    send_event(
        state,
        PushTarget::Full,
        PushEvent::ProcessMetrics(ProcessSnapshotPayload {
            timestamp,
            process_count: process_count(processes.len()),
            processes: processes.clone(),
        }),
    );

    let mut subscriptions = state.realtime.read().await.subscriptions_snapshot();
    subscriptions.sort_unstable_by(|left, right| left.0.cmp(&right.0));
    for (connection_id, subscription) in subscriptions {
        if subscription.mode != SubscriptionMode::Lite {
            continue;
        }

        let selected = select_lite_subset(&processes, "cpu", 5, &subscription.pinned_process_ids);
        if selected.is_empty() {
            continue;
        }

        send_event(
            state,
            PushTarget::Connection(connection_id),
            PushEvent::ProcessMetricsLite(ProcessSnapshotPayload {
                timestamp,
                process_count: process_count(selected.len()),
                processes: selected,
            }),
        );
    }

    send_event(
        state,
        PushTarget::All,
        PushEvent::ProcessMetadata(ProcessMetadataPayload {
            timestamp,
            process_count: process_count(metadata.len()),
            processes: metadata,
        }),
    );
}

fn send_event(state: &AppState, target: PushTarget, event: PushEvent) {
    let _ = state.push_tx.send(RoutedPushEvent { target, event });
}

fn process_count(count: usize) -> i32 {
    i32::try_from(count).unwrap_or(i32::MAX)
}

fn bytes_to_mb(bytes: u64) -> f64 {
    bytes as f64 / BYTES_PER_MB
}

fn finite(value: Option<f64>) -> Option<f64> {
    value.filter(|value| value.is_finite())
}

fn optional_nonnegative(value: Option<f64>) -> Option<f64> {
    value.and_then(|value| {
        if value.is_finite() {
            Some(value.max(0.0))
        } else {
            None
        }
    })
}

fn nonnegative(value: f64) -> f64 {
    if value.is_finite() && value > 0.0 {
        value
    } else {
        0.0
    }
}

fn round_one(value: f64) -> f64 {
    (value * 10.0).round() / 10.0
}

#[cfg(test)]
mod tests {
    use std::sync::Arc;

    use chrono::{Local, TimeZone, Utc};
    use tokio::sync::broadcast::error::TryRecvError;
    use xhm_core::traits::{MockClock, MockLhmReader, MockRyzenAdjClient};

    use super::*;
    use crate::{
        db::SqliteMetricStore,
        state::{RuntimeConfig, ServicePaths},
    };

    fn fixed_time() -> chrono::DateTime<Utc> {
        Utc.with_ymd_and_hms(2026, 7, 26, 10, 30, 0).unwrap()
    }

    fn test_state(runtime: RuntimeConfig) -> AppState {
        AppState::new(
            Arc::new(SqliteMetricStore::open_in_memory().unwrap()),
            Arc::new(MockClock::new(fixed_time())),
            Arc::new(MockLhmReader::new(None, false)),
            Arc::new(MockRyzenAdjClient::unsupported()),
            ServicePaths::for_exe_dir(std::env::temp_dir().join("xhm-worker-tests")),
            runtime,
        )
    }

    #[test]
    fn keyword_matching_checks_name_and_command_line_case_insensitively() {
        let keywords = normalize_keywords(&[
            "  ALPHA  ".to_owned(),
            "model.gguf".to_owned(),
            " ".to_owned(),
        ]);

        assert!(matches_keywords("alpha-service.exe", "", &keywords));
        assert!(matches_keywords(
            "runner.exe",
            "runner.exe --model C:/Models/MODEL.GGUF",
            &keywords
        ));
        assert!(!matches_keywords(
            "runner.exe",
            "runner.exe --idle",
            &keywords
        ));
        assert!(!matches_keywords("alpha-service.exe", "", &[]));
    }

    #[test]
    fn process_sample_uses_metric_value_json_and_online_units() {
        let sample = build_process_sample(
            42,
            "alpha".to_owned(),
            "alpha.exe --serve".to_owned(),
            12.34,
            10 * 1024 * 1024,
            512.34,
            fixed_time(),
        )
        .unwrap();
        let metrics: MetricValueMap = serde_json::from_str(&sample.record.metrics_json).unwrap();

        assert_eq!(
            metrics.get("cpu"),
            Some(&MetricValue {
                value: 12.3,
                unit: Some("%".to_owned()),
            })
        );
        assert_eq!(
            metrics.get("memory"),
            Some(&MetricValue {
                value: 10.0,
                unit: Some("MB".to_owned()),
            })
        );
        assert_eq!(
            metrics.get("vram"),
            Some(&MetricValue {
                value: 512.3,
                unit: Some("MB".to_owned()),
            })
        );
        assert_eq!(sample.snapshot.metrics.get("cpu"), Some(&12.3));
        assert_eq!(sample.snapshot.metrics.get("memory"), Some(&10.0));
        assert_eq!(sample.snapshot.metrics.get("vram"), Some(&512.3));
        assert_eq!(sample.record.timestamp, fixed_time());
    }

    #[test]
    fn system_usage_without_bridge_or_power_uses_safe_values() {
        let timestamp = fixed_time().with_timezone(&Local);
        let usage = build_system_usage(timestamp, f64::NAN, 512 * 1024 * 1024, 1024.0, None, None);

        assert_eq!(usage.total_cpu, 0.0);
        assert_eq!(usage.total_gpu, 0.0);
        assert_eq!(usage.cpu_temperature, None);
        assert_eq!(usage.gpu_temperature, None);
        assert_eq!(usage.total_memory, 512.0);
        assert_eq!(usage.total_vram, 0.0);
        assert_eq!(usage.upload_speed, 0.0);
        assert_eq!(usage.download_speed, 0.0);
        assert_eq!(usage.max_memory, 1024.0);
        assert_eq!(usage.max_vram, 0.0);
        assert!(usage.disks.is_empty());
        assert!(!usage.power_available);
        assert_eq!(usage.total_power, 0.0);
        assert_eq!(usage.max_power, 0.0);
        assert_eq!(usage.power_scheme_index, None);
    }

    #[test]
    fn system_usage_maps_only_per_disk_bridge_snapshots() {
        let lhm = LhmSnapshot {
            ts: fixed_time(),
            cpu_temp: Some(60.04),
            cpu_temp_label: Some("CPU Package".to_owned()),
            gpu_temp: Some(50.06),
            gpu_memory_used_mb: None,
            gpu_memory_total_mb: None,
            gpu_load: Some(25.04),
            process_vram_mb: Default::default(),
            net_up_mbps: 1.25,
            net_down_mbps: 2.5,
            disk_read_mbps: 99.0,
            disk_write_mbps: 88.0,
            disks: vec![xhm_core::models::LhmDiskSnapshot {
                name: "NVMe 0".to_owned(),
                total_bytes: Some(1_000),
                used_bytes: Some(400),
                read_mbps: Some(3.5),
                write_mbps: None,
            }],
        };

        let usage = build_system_usage(
            fixed_time().with_timezone(&Local),
            10.0,
            512 * 1024 * 1024,
            1024.0,
            Some(&lhm),
            None,
        );

        assert_eq!(usage.disks.len(), 1);
        assert_eq!(usage.disks[0].name, "NVMe 0");
        assert_eq!(usage.disks[0].total_bytes, Some(1_000));
        assert_eq!(usage.disks[0].used_bytes, Some(400));
        assert_eq!(usage.disks[0].read_speed, Some(3.5));
        assert_eq!(usage.disks[0].write_speed, None);
    }

    #[tokio::test]
    async fn process_routing_emits_full_then_connection_specific_lite_then_metadata() {
        let state = test_state(RuntimeConfig::default());
        {
            let mut registry = state.realtime.write().await;
            registry.register_direct("lite-a".to_owned());
            registry.register_direct("lite-b".to_owned());
            assert!(registry.set_subscription("lite-a", SubscriptionMode::Lite, Some(&[6])));
            assert!(registry.set_subscription("lite-b", SubscriptionMode::Lite, Some(&[7])));
        }
        let mut receiver = state.push_tx.subscribe();
        let mut processes = Vec::new();
        let mut metadata = Vec::new();
        for process_id in 1..=7 {
            let sample = build_process_sample(
                process_id,
                format!("process-{process_id}"),
                format!("process-{process_id}.exe"),
                f64::from(100 - process_id),
                process_id as u64 * 1024 * 1024,
                0.0,
                fixed_time(),
            )
            .unwrap();
            processes.push(sample.snapshot);
            metadata.push(sample.metadata);
        }

        route_process_events(
            &state,
            fixed_time().with_timezone(&Local),
            processes,
            metadata,
        )
        .await;

        let full = receiver.recv().await.unwrap();
        assert_eq!(full.target, PushTarget::Full);
        let PushEvent::ProcessMetrics(full) = full.event else {
            panic!("first event must be the full process snapshot");
        };
        assert_eq!(full.process_count, 7);

        let lite_a = receiver.recv().await.unwrap();
        assert_eq!(lite_a.target, PushTarget::Connection("lite-a".to_owned()));
        let PushEvent::ProcessMetricsLite(lite_a) = lite_a.event else {
            panic!("second event must be lite-a's snapshot");
        };
        assert_eq!(
            lite_a
                .processes
                .iter()
                .map(|process| process.process_id)
                .collect::<Vec<_>>(),
            [1, 2, 3, 4, 5, 6]
        );

        let lite_b = receiver.recv().await.unwrap();
        assert_eq!(lite_b.target, PushTarget::Connection("lite-b".to_owned()));
        let PushEvent::ProcessMetricsLite(lite_b) = lite_b.event else {
            panic!("third event must be lite-b's snapshot");
        };
        assert_eq!(
            lite_b
                .processes
                .iter()
                .map(|process| process.process_id)
                .collect::<Vec<_>>(),
            [1, 2, 3, 4, 5, 7]
        );

        let metadata = receiver.recv().await.unwrap();
        assert_eq!(metadata.target, PushTarget::All);
        let PushEvent::ProcessMetadata(metadata) = metadata.event else {
            panic!("fourth event must be process metadata");
        };
        assert_eq!(metadata.process_count, 7);
        assert_eq!(metadata.processes[0].command_line, "process-1.exe");
        assert_eq!(metadata.processes[0].display_name, "process-1");
        assert!(matches!(receiver.try_recv(), Err(TryRecvError::Empty)));
    }

    #[tokio::test]
    async fn service_worker_starts_immediately_and_shutdown_is_idempotent() {
        let state = test_state(RuntimeConfig {
            interval_seconds: 3_600,
            ..RuntimeConfig::default()
        });
        let mut receiver = state.push_tx.subscribe();
        let mut worker = ServiceWorker::start(state);

        let hardware = tokio::time::timeout(Duration::from_secs(1), receiver.recv())
            .await
            .expect("hardware limits must be emitted within one second")
            .unwrap();
        assert!(matches!(hardware.event, PushEvent::HardwareLimits(_)));

        let usage = tokio::time::timeout(Duration::from_secs(1), receiver.recv())
            .await
            .expect("system usage must be emitted within one second")
            .unwrap();
        assert!(matches!(usage.event, PushEvent::SystemUsage(_)));

        worker.shutdown().await;
        worker.shutdown().await;
        assert!(*worker.shutdown_tx.borrow());
        assert!(worker.task.is_none());
    }
}
