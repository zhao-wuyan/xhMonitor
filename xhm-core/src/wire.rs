//! 实时推送载荷（SignalR 与 SSE 共用）。
//!
//! 这些结构体逐字段对齐 `Worker.cs` 里推给 `IMetricsClient` 的匿名类型。
//! 事件**方法名**保持 PascalCase（`PropertyNamingPolicy` 只影响属性，
//! 不影响 hub invocation 的 `target`），而**属性名**是 camelCase。
//!
//! 时间戳用本地时间 + UTC 偏移（`DateTime.Now`），不是 `Z`——见 `crate::time`。

use std::cmp::Ordering;
use std::collections::{BTreeMap, HashMap, HashSet};

use chrono::{DateTime, Local};
use serde::{Deserialize, Serialize};

/// 服务端 → 客户端的 5 个事件名。这些字符串被前端
/// `useMetricsHub.ts:93,94,96,110` 逐字注册，改动即断连。
pub mod events {
    pub const RECEIVE_HARDWARE_LIMITS: &str = "ReceiveHardwareLimits";
    pub const RECEIVE_SYSTEM_USAGE: &str = "ReceiveSystemUsage";
    pub const RECEIVE_PROCESS_METRICS: &str = "ReceiveProcessMetrics";
    pub const RECEIVE_PROCESS_METRICS_LITE: &str = "ReceiveProcessMetricsLite";
    pub const RECEIVE_PROCESS_METADATA: &str = "ReceiveProcessMetadata";
}

/// 客户端 → 服务端唯一可调用的 hub 方法。
pub const SET_PROCESS_METRICS_SUBSCRIPTION: &str = "SetProcessMetricsSubscription";

/// SignalR 组名。
pub mod groups {
    pub const PROCESS_METRICS_FULL: &str = "metrics.processes.full";
    /// C# 侧维护但从不作为推送目标——lite 是逐连接下发的。
    /// 保留常量以便行为对齐审计，不用于路由。
    pub const PROCESS_METRICS_LITE: &str = "metrics.processes.lite";
}

/// 进程指标订阅模式。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum SubscriptionMode {
    /// 新连接的默认值（`MetricsHub.OnConnectedAsync` → `RegisterConnection` → Full）。
    #[default]
    Full,
    Lite,
}

impl SubscriptionMode {
    /// 对齐 `MetricsHub.cs:42-43`：`Trim()` 后**大小写不敏感**等于 `"lite"`
    /// 才是 Lite；其余一切（含 null、空串、`"full"`、乱码）都是 Full。
    pub fn parse(raw: Option<&str>) -> Self {
        match raw.map(str::trim) {
            Some(mode) if mode.eq_ignore_ascii_case("lite") => SubscriptionMode::Lite,
            _ => SubscriptionMode::Full,
        }
    }
}

/// 归一化 pinned PID 列表：过滤 `> 0` → 去重 → 升序
/// （`ProcessMetricsSubscriptionStore.cs:139-150`）。
/// Full 模式会丢弃 pinned，调用方负责。
pub fn normalize_pinned_ids(raw: Option<&[i32]>) -> Vec<i32> {
    let Some(ids) = raw else {
        return Vec::new();
    };
    let mut out: Vec<i32> = ids.iter().copied().filter(|id| *id > 0).collect();
    out.sort_unstable();
    out.dedup();
    out
}

// ─────────────────────────────────────────────────────────────────────────────
// 事件载荷
// ─────────────────────────────────────────────────────────────────────────────

/// `ReceiveHardwareLimits`。启动时一次 + 每小时一次，推给所有连接。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HardwareLimitsPayload {
    #[serde(with = "crate::time::serde_wire_local")]
    pub timestamp: DateTime<Local>,
    /// MB
    pub max_memory: f64,
    /// MB
    pub max_vram: f64,
}

/// `ReceiveSystemUsage` 中的单块磁盘。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiskUsagePayload {
    pub name: String,
    pub total_bytes: Option<i64>,
    pub used_bytes: Option<i64>,
    /// MB/s
    pub read_speed: Option<f64>,
    /// MB/s
    pub write_speed: Option<f64>,
}

/// `ReceiveSystemUsage`。字段顺序对齐 `Worker.cs:274-299` 的匿名类型声明序。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SystemUsagePayload {
    #[serde(with = "crate::time::serde_wire_local")]
    pub timestamp: DateTime<Local>,
    pub total_cpu: f64,
    pub total_gpu: f64,
    pub cpu_temperature: Option<f64>,
    pub gpu_temperature: Option<f64>,
    pub total_memory: f64,
    pub total_vram: f64,
    /// MB/s，已 `max(0.0, v)`
    pub upload_speed: f64,
    /// MB/s，已 `max(0.0, v)`
    pub download_speed: f64,
    pub max_memory: f64,
    pub max_vram: f64,
    pub disks: Vec<DiskUsagePayload>,
    pub power_available: bool,
    pub total_power: f64,
    pub max_power: f64,
    pub power_scheme_index: Option<i32>,
}

/// `ReceiveProcessMetrics` / `ReceiveProcessMetricsLite` 中的单个进程。
///
/// 三个字段是**条件序列化**的（`Worker.cs:1111-1116`），这是整份契约里
/// 唯一违反"null 必写"规则的地方：
/// - `hasMeta` 为 `false` 时省略（`JsonIgnore(WhenWritingDefault)`）
/// - `commandLine` / `displayName` 为 null 时省略（`JsonIgnore(WhenWritingNull)`）
///
/// 因此同一条消息里不同元素的形状可以不同——不能用固定形状结构体。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProcessMetricSnapshot {
    pub process_id: i32,
    pub process_name: String,
    #[serde(default, skip_serializing_if = "is_false")]
    pub has_meta: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub command_line: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub display_name: Option<String>,
    /// key 逐字保留（`cpu` / `memory` / `gpu` / `vram` / `llama_*`）。
    pub metrics: BTreeMap<String, f64>,
}

fn is_false(value: &bool) -> bool {
    !*value
}

/// `ReceiveProcessMetrics` / `ReceiveProcessMetricsLite` 的信封。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProcessSnapshotPayload {
    #[serde(with = "crate::time::serde_wire_local")]
    pub timestamp: DateTime<Local>,
    /// 等于 `processes.len()`；lite 模式下是**裁剪后**的数量，不是全机进程数。
    pub process_count: i32,
    pub processes: Vec<ProcessMetricSnapshot>,
}

/// `ReceiveProcessMetadata` 中的单个进程。这里三个字符串字段都**非空**
/// （源侧 `?? string.Empty`），与 `ProcessMetricSnapshot` 的可选字段不同。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProcessMetadataSnapshot {
    pub process_id: i32,
    pub process_name: String,
    pub command_line: String,
    pub display_name: String,
}

/// `ReceiveProcessMetadata` 的信封。
///
/// 两种语义共用这一形状：
/// - 广播（`Worker.cs:355-360`）：`process_count` 是**增量**条数
/// - 连接时回放（`MetricsHub.cs:66-74`）：`process_count` 是 store 全量条数
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProcessMetadataPayload {
    #[serde(with = "crate::time::serde_wire_local")]
    pub timestamp: DateTime<Local>,
    pub process_count: i32,
    pub processes: Vec<ProcessMetadataSnapshot>,
}

// ─────────────────────────────────────────────────────────────────────────────
// 传输无关的推送总线消息
// ─────────────────────────────────────────────────────────────────────────────

/// 一条待下发的推送。采集侧只产生这个枚举；
/// SignalR 适配器与 SSE 适配器各自决定如何编码与路由。
#[derive(Debug, Clone, PartialEq)]
pub enum PushEvent {
    HardwareLimits(HardwareLimitsPayload),
    SystemUsage(SystemUsagePayload),
    /// 推给 `metrics.processes.full` 组。
    ProcessMetrics(ProcessSnapshotPayload),
    /// 逐连接下发（`Clients.Client(connId)`，不是组广播），因为每个 lite
    /// 订阅者的 pinned 集合不同、载荷也不同。变体存在的意义是让编码与
    /// 路由共用同一条 `event_name()`/`to_json()` 路径。
    ProcessMetricsLite(ProcessSnapshotPayload),
    /// 广播的元数据增量。
    ProcessMetadata(ProcessMetadataPayload),
}

impl PushEvent {
    /// 对应的 SignalR 事件名 / SSE `event:` 字段。
    pub fn event_name(&self) -> &'static str {
        match self {
            PushEvent::HardwareLimits(_) => events::RECEIVE_HARDWARE_LIMITS,
            PushEvent::SystemUsage(_) => events::RECEIVE_SYSTEM_USAGE,
            PushEvent::ProcessMetrics(_) => events::RECEIVE_PROCESS_METRICS,
            PushEvent::ProcessMetricsLite(_) => events::RECEIVE_PROCESS_METRICS_LITE,
            PushEvent::ProcessMetadata(_) => events::RECEIVE_PROCESS_METADATA,
        }
    }

    /// 编码为单个 JSON 参数值，供两种传输复用。
    pub fn to_json(&self) -> Result<serde_json::Value, serde_json::Error> {
        match self {
            PushEvent::HardwareLimits(p) => serde_json::to_value(p),
            PushEvent::SystemUsage(p) => serde_json::to_value(p),
            PushEvent::ProcessMetrics(p) => serde_json::to_value(p),
            PushEvent::ProcessMetricsLite(p) => serde_json::to_value(p),
            PushEvent::ProcessMetadata(p) => serde_json::to_value(p),
        }
    }
}

fn compare_lite_candidates(
    left: &(&ProcessMetricSnapshot, f64),
    right: &(&ProcessMetricSnapshot, f64),
) -> Ordering {
    let value_order = match (left.1.is_nan(), right.1.is_nan()) {
        (true, true) => Ordering::Equal,
        (true, false) => Ordering::Greater,
        (false, true) => Ordering::Less,
        (false, false) if left.1 > right.1 => Ordering::Less,
        (false, false) if left.1 < right.1 => Ordering::Greater,
        (false, false) => Ordering::Equal,
    };

    value_order.then_with(|| left.0.process_id.cmp(&right.0.process_id))
}

/// 从全量快照中挑出 lite 子集：按 `metric` 取 Top-N，并集 pinned PID。
///
/// 对齐 `Worker.cs:936, 949-969, 998-1037`：
/// - Top-N 按指标降序，**并列时 PID 小的优先**（`:1008-1017`）
/// - 缺失该指标的进程按 `0.0` 参与 Top-N
/// - Top-N 保持排名顺序；随后按传入顺序追加仍存在的 pinned PID
/// - Top-N 与 pinned 之间按 PID 去重
pub fn select_lite_subset(
    processes: &[ProcessMetricSnapshot],
    metric: &str,
    top_n: usize,
    pinned: &[i32],
) -> Vec<ProcessMetricSnapshot> {
    let mut ranked = Vec::with_capacity(processes.len().min(top_n));
    if top_n > 0 {
        for process in processes {
            let value = process.metrics.get(metric).copied().unwrap_or(0.0);
            ranked.push((process, value));
            ranked.sort_unstable_by(compare_lite_candidates);

            if ranked.len() > top_n {
                ranked.pop();
            }
        }
    }

    let capacity = ranked.len().saturating_add(pinned.len());
    let mut selected = Vec::with_capacity(capacity);
    let mut included = HashSet::with_capacity(capacity);

    for (process, _) in ranked {
        if included.insert(process.process_id) {
            selected.push(process.clone());
        }
    }

    if !pinned.is_empty() {
        let mut process_index = HashMap::with_capacity(processes.len());
        for process in processes {
            assert!(
                process_index.insert(process.process_id, process).is_none(),
                "duplicate process PID: {}",
                process.process_id
            );
        }

        for pid in pinned {
            if included.insert(*pid) {
                if let Some(process) = process_index.get(pid) {
                    selected.push((**process).clone());
                }
            }
        }
    }

    selected
}

#[cfg(test)]
mod tests {
    use super::*;

    fn snapshot(pid: i32, cpu: Option<f64>) -> ProcessMetricSnapshot {
        let mut metrics = BTreeMap::new();
        if let Some(value) = cpu {
            metrics.insert("cpu".to_string(), value);
        }
        ProcessMetricSnapshot {
            process_id: pid,
            process_name: format!("p{pid}"),
            has_meta: false,
            command_line: None,
            display_name: None,
            metrics,
        }
    }

    #[test]
    fn subscription_mode_defaults_to_full_for_anything_but_lite() {
        assert_eq!(SubscriptionMode::parse(None), SubscriptionMode::Full);
        assert_eq!(SubscriptionMode::parse(Some("")), SubscriptionMode::Full);
        assert_eq!(
            SubscriptionMode::parse(Some("full")),
            SubscriptionMode::Full
        );
        assert_eq!(
            SubscriptionMode::parse(Some("garbage")),
            SubscriptionMode::Full
        );
    }

    #[test]
    fn subscription_mode_matches_lite_case_insensitively_after_trim() {
        assert_eq!(
            SubscriptionMode::parse(Some("lite")),
            SubscriptionMode::Lite
        );
        assert_eq!(
            SubscriptionMode::parse(Some("  LiTe  ")),
            SubscriptionMode::Lite
        );
    }

    #[test]
    fn pinned_ids_are_filtered_deduped_and_sorted() {
        assert_eq!(
            normalize_pinned_ids(Some(&[5, -1, 0, 5, 3])),
            vec![3, 5],
            "must drop <= 0, dedupe, and sort ascending"
        );
        assert!(normalize_pinned_ids(None).is_empty());
        assert!(normalize_pinned_ids(Some(&[])).is_empty());
    }

    #[test]
    fn process_metric_snapshot_omits_has_meta_when_false() {
        let json = serde_json::to_value(snapshot(1, Some(10.0))).unwrap();
        assert!(
            json.get("hasMeta").is_none(),
            "hasMeta must be omitted when false"
        );
        assert!(json.get("commandLine").is_none());
        assert!(json.get("displayName").is_none());
    }

    #[test]
    fn process_metric_snapshot_includes_metadata_when_present() {
        let mut snap = snapshot(1, Some(10.0));
        snap.has_meta = true;
        snap.command_line = Some("python app.py".to_string());
        snap.display_name = Some("App".to_string());
        let json = serde_json::to_value(&snap).unwrap();
        assert_eq!(json["hasMeta"], true);
        assert_eq!(json["commandLine"], "python app.py");
        assert_eq!(json["displayName"], "App");
    }

    #[test]
    fn process_metric_snapshot_keeps_metric_keys_verbatim() {
        let mut snap = snapshot(1, Some(10.0));
        snap.metrics.insert("llama_gen_tps_avg".to_string(), 42.0);
        let json = serde_json::to_value(&snap).unwrap();
        assert_eq!(json["metrics"]["llama_gen_tps_avg"], 42.0);
        assert!(json["metrics"].get("llamaGenTpsAvg").is_none());
    }

    #[test]
    fn lite_subset_takes_top_n_by_metric() {
        let all = vec![
            snapshot(3, Some(50.0)),
            snapshot(2, Some(90.0)),
            snapshot(1, Some(5.0)),
            snapshot(4, Some(1.0)),
        ];
        let picked = select_lite_subset(&all, "cpu", 2, &[]);
        let pids: Vec<i32> = picked.iter().map(|p| p.process_id).collect();
        assert_eq!(pids, vec![2, 3]);
    }

    #[test]
    fn lite_subset_breaks_ties_by_lower_pid() {
        let all = vec![
            snapshot(9, Some(50.0)),
            snapshot(2, Some(50.0)),
            snapshot(7, Some(50.0)),
        ];
        let picked = select_lite_subset(&all, "cpu", 2, &[]);
        let pids: Vec<i32> = picked.iter().map(|p| p.process_id).collect();
        assert_eq!(pids, vec![2, 7], "ties must prefer the lower PID");
    }

    #[test]
    fn lite_subset_appends_existing_pinned_processes_in_input_order_without_duplicates() {
        let all = vec![
            snapshot(1, Some(50.0)),
            snapshot(2, Some(90.0)),
            snapshot(3, Some(1.0)),
            snapshot(4, Some(5.0)),
        ];
        let picked = select_lite_subset(&all, "cpu", 2, &[4, 2, 3, 4, 999]);
        let pids: Vec<i32> = picked.iter().map(|p| p.process_id).collect();
        assert_eq!(pids, vec![2, 1, 4, 3]);
    }

    #[test]
    fn lite_subset_ignores_pinned_pids_that_no_longer_exist() {
        let all = vec![snapshot(1, Some(5.0))];
        let picked = select_lite_subset(&all, "cpu", 1, &[999]);
        let pids: Vec<i32> = picked.iter().map(|p| p.process_id).collect();
        assert_eq!(pids, vec![1]);
    }

    #[test]
    fn lite_subset_ranks_missing_metric_as_zero_ahead_of_negative_values() {
        let all = vec![
            snapshot(9, Some(-1.0)),
            snapshot(5, None),
            snapshot(3, Some(0.0)),
            snapshot(1, Some(-10.0)),
        ];
        let picked = select_lite_subset(&all, "cpu", 3, &[]);
        let pids: Vec<i32> = picked.iter().map(|p| p.process_id).collect();
        assert_eq!(pids, vec![3, 5, 9]);
    }

    #[test]
    fn lite_subset_ranks_nan_after_all_numeric_values() {
        let all = vec![
            snapshot(9, Some(f64::NAN)),
            snapshot(2, Some(f64::NEG_INFINITY)),
            snapshot(4, Some(f64::NAN)),
            snapshot(3, Some(10.0)),
            snapshot(5, Some(f64::INFINITY)),
        ];
        let picked = select_lite_subset(&all, "cpu", all.len(), &[]);
        let pids: Vec<i32> = picked.iter().map(|p| p.process_id).collect();
        assert_eq!(pids, vec![5, 3, 2, 4, 9]);
    }

    #[test]
    fn lite_subset_with_zero_top_n_still_returns_pinned_processes() {
        let all = vec![
            snapshot(1, Some(50.0)),
            snapshot(2, Some(90.0)),
            snapshot(3, Some(1.0)),
        ];
        let picked = select_lite_subset(&all, "cpu", 0, &[3, 1, 3, 999]);
        let pids: Vec<i32> = picked.iter().map(|p| p.process_id).collect();
        assert_eq!(pids, vec![3, 1]);
    }

    #[test]
    #[should_panic(expected = "duplicate process PID")]
    fn lite_subset_panics_on_duplicate_process_pid_when_pinned_lookup_is_needed() {
        let all = vec![snapshot(1, Some(50.0)), snapshot(1, Some(90.0))];
        let _ = select_lite_subset(&all, "cpu", 0, &[999]);
    }

    #[test]
    fn push_event_names_match_the_frontend_handlers() {
        // 前端逐字注册这些字符串；任何改动都会让事件静默丢失。
        let snapshot = ProcessSnapshotPayload {
            timestamp: Local::now(),
            process_count: 0,
            processes: Vec::new(),
        };
        assert_eq!(
            PushEvent::ProcessMetrics(snapshot.clone()).event_name(),
            "ReceiveProcessMetrics"
        );
        assert_eq!(
            PushEvent::ProcessMetricsLite(snapshot).event_name(),
            "ReceiveProcessMetricsLite"
        );
        assert_eq!(
            PushEvent::HardwareLimits(HardwareLimitsPayload {
                timestamp: Local::now(),
                max_memory: 0.0,
                max_vram: 0.0,
            })
            .event_name(),
            "ReceiveHardwareLimits"
        );
        assert_eq!(
            PushEvent::ProcessMetadata(ProcessMetadataPayload {
                timestamp: Local::now(),
                process_count: 0,
                processes: Vec::new(),
            })
            .event_name(),
            "ReceiveProcessMetadata"
        );
    }

    #[test]
    fn lite_and_full_share_one_payload_shape() {
        // 两个事件的载荷 schema 必须完全一致，只有路由与内容选择不同。
        let snapshot = ProcessSnapshotPayload {
            timestamp: Local::now(),
            process_count: 1,
            processes: vec![snapshot(1, Some(10.0))],
        };
        let full = PushEvent::ProcessMetrics(snapshot.clone())
            .to_json()
            .unwrap();
        let lite = PushEvent::ProcessMetricsLite(snapshot).to_json().unwrap();
        assert_eq!(full, lite);
    }

    #[test]
    fn system_usage_emits_nulls_for_absent_temperatures() {
        let payload = SystemUsagePayload {
            timestamp: Local::now(),
            total_cpu: 10.0,
            total_gpu: 20.0,
            cpu_temperature: None,
            gpu_temperature: None,
            total_memory: 8192.0,
            total_vram: 2048.0,
            upload_speed: 0.0,
            download_speed: 0.0,
            max_memory: 32768.0,
            max_vram: 8192.0,
            disks: Vec::new(),
            power_available: false,
            total_power: 0.0,
            max_power: 0.0,
            power_scheme_index: None,
        };
        let json = serde_json::to_value(&payload).unwrap();
        assert_eq!(json["cpuTemperature"], serde_json::Value::Null);
        assert_eq!(json["powerSchemeIndex"], serde_json::Value::Null);
        assert!(json["disks"].is_array());
        // 时间戳必须带偏移而不是 Z
        assert!(!json["timestamp"].as_str().unwrap().ends_with('Z'));
    }
}
