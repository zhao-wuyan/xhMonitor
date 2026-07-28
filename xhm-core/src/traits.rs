//! 外部边界抽象与测试替身。
//!
//! 依据 `test-conventions-002`：LHM bridge、RyzenAdj、SQLite、时钟都是外部边界，
//! 必须可隔离。这里给出 trait + 内存 mock，使 `cargo test --workspace` 能在
//! **无管理员权限、无真实硬件**的 CI 上跑通。
//!
//! 存储 trait 刻意是**同步**的：rusqlite 本身同步，调用侧用
//! `tokio::task::spawn_blocking` 包一层即可，避免引入 `async-trait`
//! 的装箱开销和 dyn 兼容性问题。

use std::sync::Mutex;

use chrono::{DateTime, Local, Utc};

use crate::error::{CoreError, Result};
use crate::models::{
    AggregatedMetricRecord, AggregationLevel, AlertConfiguration, ApplicationSetting, LhmSnapshot,
    MetricFilter, NewAggregatedMetricRecord, NewProcessMetricRecord, PowerScheme, PowerStatus,
    ProcessMetricRecord, ProcessSummary, SettingsUpsertCounts,
};

// ─────────────────────────────────────────────────────────────────────────────
// 时钟
// ─────────────────────────────────────────────────────────────────────────────

/// 可注入时钟。采样循环、聚合水位、保留期裁剪都依赖它，
/// 注入后这些逻辑才能在测试里确定性地推进。
pub trait Clock: Send + Sync {
    fn now_utc(&self) -> DateTime<Utc>;

    /// SignalR 载荷使用本地时间；默认由 UTC 换算，mock 无需单独实现。
    fn now_local(&self) -> DateTime<Local> {
        self.now_utc().with_timezone(&Local)
    }
}

/// 生产实现。
#[derive(Debug, Clone, Copy, Default)]
pub struct SystemClock;

impl Clock for SystemClock {
    fn now_utc(&self) -> DateTime<Utc> {
        Utc::now()
    }
}

/// 测试用固定时钟，可手动推进。
#[derive(Debug)]
pub struct MockClock {
    now: Mutex<DateTime<Utc>>,
}

impl MockClock {
    pub fn new(start: DateTime<Utc>) -> Self {
        MockClock {
            now: Mutex::new(start),
        }
    }

    /// 推进虚拟时间。
    pub fn advance(&self, delta: chrono::Duration) {
        let mut guard = self.now.lock().expect("MockClock poisoned");
        *guard += delta;
    }

    pub fn set(&self, instant: DateTime<Utc>) {
        *self.now.lock().expect("MockClock poisoned") = instant;
    }
}

impl Clock for MockClock {
    fn now_utc(&self) -> DateTime<Utc> {
        *self.now.lock().expect("MockClock poisoned")
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// LHM bridge
// ─────────────────────────────────────────────────────────────────────────────

/// 系统级传感器读取。唯一数据源是 `lhm-bridge` 子进程；
/// 不从 PDH/DXGI 重新采集，避免出现第二套真相。
pub trait LhmReader: Send + Sync {
    /// 最近一次快照。子进程尚未产出或已死时返回 `None`。
    fn snapshot(&self) -> Option<LhmSnapshot>;

    /// 子进程是否以管理员身份运行。该信号只描述 LHM 传感器能力，
    /// 不能替代 Service 自身 Windows token 的管理员状态。
    fn is_sensor_elevated(&self) -> bool;
}

/// 内存替身：返回固定快照。
#[derive(Debug, Default)]
pub struct MockLhmReader {
    snapshot: Mutex<Option<LhmSnapshot>>,
    bridge_elevated: bool,
}

impl MockLhmReader {
    pub fn new(snapshot: Option<LhmSnapshot>, bridge_elevated: bool) -> Self {
        MockLhmReader {
            snapshot: Mutex::new(snapshot),
            bridge_elevated,
        }
    }

    /// 模拟子进程产出新一帧。
    pub fn push(&self, snapshot: LhmSnapshot) {
        *self.snapshot.lock().expect("MockLhmReader poisoned") = Some(snapshot);
    }

    /// 模拟子进程死亡。
    pub fn clear(&self) {
        *self.snapshot.lock().expect("MockLhmReader poisoned") = None;
    }
}

impl LhmReader for MockLhmReader {
    fn snapshot(&self) -> Option<LhmSnapshot> {
        self.snapshot
            .lock()
            .expect("MockLhmReader poisoned")
            .clone()
    }

    fn is_sensor_elevated(&self) -> bool {
        self.bridge_elevated
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// RyzenAdj
// ─────────────────────────────────────────────────────────────────────────────

/// TDP 读写后端。生产实现是 native DLL 主路径 + CLI 备用路径的装饰器。
pub trait RyzenAdjClient: Send + Sync {
    /// 当前机器是否支持功耗控制。不支持时 `/api/v1/power/status` 返回 404。
    fn is_supported(&self) -> bool;

    /// 读取当前功耗与限制。不可用时返回 `None`（映射为 503）。
    fn read_status(&self) -> Option<PowerStatus>;

    /// 应用一个 TDP 档位。
    fn apply_scheme(&self, scheme: PowerScheme) -> Result<()>;
}

/// 内存替身：可配置支持性、读数与写入失败。
#[derive(Debug)]
pub struct MockRyzenAdjClient {
    supported: bool,
    status: Mutex<Option<PowerStatus>>,
    applied: Mutex<Vec<PowerScheme>>,
    fail_apply: bool,
}

impl MockRyzenAdjClient {
    pub fn supported(status: PowerStatus) -> Self {
        MockRyzenAdjClient {
            supported: true,
            status: Mutex::new(Some(status)),
            applied: Mutex::new(Vec::new()),
            fail_apply: false,
        }
    }

    pub fn unsupported() -> Self {
        MockRyzenAdjClient {
            supported: false,
            status: Mutex::new(None),
            applied: Mutex::new(Vec::new()),
            fail_apply: false,
        }
    }

    /// 支持但读不到状态——对应 503 分支。
    pub fn supported_but_unavailable() -> Self {
        MockRyzenAdjClient {
            supported: true,
            status: Mutex::new(None),
            applied: Mutex::new(Vec::new()),
            fail_apply: false,
        }
    }

    pub fn failing_apply(status: PowerStatus) -> Self {
        MockRyzenAdjClient {
            supported: true,
            status: Mutex::new(Some(status)),
            applied: Mutex::new(Vec::new()),
            fail_apply: true,
        }
    }

    /// 已成功写入的档位序列，供断言。
    pub fn applied_schemes(&self) -> Vec<PowerScheme> {
        self.applied
            .lock()
            .expect("MockRyzenAdjClient poisoned")
            .clone()
    }
}

impl RyzenAdjClient for MockRyzenAdjClient {
    fn is_supported(&self) -> bool {
        self.supported
    }

    fn read_status(&self) -> Option<PowerStatus> {
        *self.status.lock().expect("MockRyzenAdjClient poisoned")
    }

    fn apply_scheme(&self, scheme: PowerScheme) -> Result<()> {
        if self.fail_apply {
            return Err(CoreError::RyzenAdj("mock apply failure".to_string()));
        }
        self.applied
            .lock()
            .expect("MockRyzenAdjClient poisoned")
            .push(scheme);
        if let Some(status) = self.status.lock().expect("poisoned").as_mut() {
            status.limits = scheme;
            status.limit_watts = f64::from(scheme.stapm_watts);
        }
        Ok(())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 存储
// ─────────────────────────────────────────────────────────────────────────────

/// SQLite 数据访问边界。
///
/// 覆盖 20 个 REST 端点需要的全部读写路径。C# 侧没有读仓储
/// （查询是 controller 内联 LINQ），所以这个 trait 是新设计的边界，
/// 而不是 `IProcessMetricRepository` 的直译——后者只有一个写方法。
pub trait MetricStore: Send + Sync {
    // ── 原始采样 ────────────────────────────────────────────────────────────
    /// 批量写入一个采集周期的行。所有行共享同一 `timestamp`，
    /// 这是 `/latest` 用 `MAX(Timestamp)` 取整帧的前提。
    ///
    /// **时间基准（与 C# 的既有缺陷分道扬镳）**：本 trait 只接受
    /// `DateTime<Utc>`。C# 侧 `MetricRepository.MapToEntity:79` 用
    /// `DateTime.SpecifyKind(cycleTimestamp, DateTimeKind.Utc)` —— 只贴标签
    /// 不做换算，而 `Worker.cs:455-461` 的 llama 采样传的是本地时间
    /// `DateTime.Now`，`PerformanceMonitor.cs:32` 传的是 `DateTime.UtcNow`。
    /// 结果是 `ProcessMetricRecords.Timestamp` 里 UTC 与本地时间混存，
    /// llama 历史行整体偏移一个时区（东八区为 +8h），而聚合水位与保留期
    /// 裁剪又一律按 UTC 比较。
    ///
    /// Rust 侧的取舍：**不复刻这个假转换**，写入一律是真 UTC；
    /// 同时**不对历史行做读取端补偿**——补偿需要猜测每行的来源провider，
    /// 会把一个可见的数据事实变成不可见的启发式。既有 llama 行的偏移
    /// 作为已知数据缺陷保留。
    fn save_process_metrics(&self, records: &[NewProcessMetricRecord]) -> Result<usize>;

    /// `/api/v1/metrics/latest`
    fn latest_process_metrics(&self, filter: &MetricFilter) -> Result<Vec<ProcessMetricRecord>>;

    /// `/api/v1/metrics/history?aggregation=raw`
    fn history_raw(
        &self,
        process_id: i32,
        from: Option<DateTime<Utc>>,
        to: Option<DateTime<Utc>>,
    ) -> Result<Vec<ProcessMetricRecord>>;

    /// `/api/v1/metrics/processes`
    fn process_summaries(&self, filter: &MetricFilter) -> Result<Vec<ProcessSummary>>;

    // ── 聚合 ────────────────────────────────────────────────────────────────
    /// `/api/v1/metrics/history?aggregation=minute|hour|day`
    fn history_aggregated(
        &self,
        process_id: i32,
        level: AggregationLevel,
        from: Option<DateTime<Utc>>,
        to: Option<DateTime<Utc>>,
    ) -> Result<Vec<AggregatedMetricRecord>>;

    /// `/api/v1/metrics/aggregations`（无 process 过滤，from/to 必填）
    fn aggregations(
        &self,
        level: AggregationLevel,
        from: DateTime<Utc>,
        to: DateTime<Utc>,
    ) -> Result<Vec<AggregatedMetricRecord>>;

    fn save_aggregates(&self, records: &[NewAggregatedMetricRecord]) -> Result<usize>;

    /// 某聚合层级已产出的最新桶时间；冷启动时为 `None`。
    fn aggregate_watermark(&self, level: AggregationLevel) -> Result<Option<DateTime<Utc>>>;

    /// 原始表最早一行的时间，用于冷启动定位 `windowStart`。
    fn earliest_raw_timestamp(&self) -> Result<Option<DateTime<Utc>>>;

    /// 某聚合层级最早一行的时间，用于上层冷启动。
    fn earliest_aggregate_timestamp(
        &self,
        level: AggregationLevel,
    ) -> Result<Option<DateTime<Utc>>>;

    /// 分页读取待聚合的原始行（keyset 分页，`id > after_id`）。
    /// 窗口边界是**严格**开区间 `(from, to)`，与 C# 一致。
    fn raw_batch_for_aggregation(
        &self,
        from: DateTime<Utc>,
        to: DateTime<Utc>,
        after_id: i64,
        limit: usize,
    ) -> Result<Vec<ProcessMetricRecord>>;

    /// 分页读取待上卷的聚合行。
    fn aggregate_batch_for_rollup(
        &self,
        level: AggregationLevel,
        from: DateTime<Utc>,
        to: DateTime<Utc>,
        after_id: i64,
        limit: usize,
    ) -> Result<Vec<AggregatedMetricRecord>>;

    // ── 保留期 ──────────────────────────────────────────────────────────────
    /// 删除 `timestamp < cutoff` 的原始行与聚合行，返回删除总数。
    fn purge_before(&self, cutoff: DateTime<Utc>) -> Result<u64>;

    /// `VACUUM`。仅在确有删除时调用。
    fn vacuum(&self) -> Result<()>;

    // ── 告警配置 ────────────────────────────────────────────────────────────
    fn list_alerts(&self) -> Result<Vec<AlertConfiguration>>;

    /// 按 `id` upsert。insert 分支写入 `created_at`/`updated_at`；
    /// update 分支**只**改 `threshold`/`is_enabled`/`updated_at`。
    fn upsert_alert(&self, alert: &AlertConfiguration, now: DateTime<Utc>) -> Result<()>;

    /// 返回 `false` 表示记录不存在（映射为 404）。
    fn delete_alert(&self, id: i32) -> Result<bool>;

    // ── 应用设置 ────────────────────────────────────────────────────────────
    fn list_settings(&self) -> Result<Vec<ApplicationSetting>>;

    /// 返回 `false` 表示 `(category, key)` 不存在（映射为 404）。
    fn update_setting(
        &self,
        category: &str,
        key: &str,
        value: &str,
        now: DateTime<Utc>,
    ) -> Result<bool>;

    /// 批量 upsert。
    fn upsert_settings(
        &self,
        entries: &[(String, String, String)],
        now: DateTime<Utc>,
    ) -> Result<SettingsUpsertCounts>;

    // ── 健康 ────────────────────────────────────────────────────────────────
    /// 连通性探针，支撑 `/api/v1/config/health`。
    fn health_check(&self) -> Result<()>;
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::TimeZone;

    #[test]
    fn mock_clock_is_deterministic_and_advances() {
        let start = Utc.with_ymd_and_hms(2026, 7, 26, 0, 0, 0).unwrap();
        let clock = MockClock::new(start);
        assert_eq!(clock.now_utc(), start);
        assert_eq!(clock.now_utc(), start, "must not drift between reads");

        clock.advance(chrono::Duration::seconds(90));
        assert_eq!(clock.now_utc(), start + chrono::Duration::seconds(90));
    }

    #[test]
    fn mock_clock_local_tracks_utc() {
        let start = Utc.with_ymd_and_hms(2026, 7, 26, 4, 0, 0).unwrap();
        let clock = MockClock::new(start);
        assert_eq!(clock.now_local().with_timezone(&Utc), start);
    }

    #[test]
    fn mock_lhm_reader_reports_absence_when_the_bridge_is_down() {
        let reader = MockLhmReader::new(None, false);
        assert!(reader.snapshot().is_none());
        assert!(!reader.is_sensor_elevated());
    }

    #[test]
    fn mock_lhm_reader_serves_the_last_pushed_frame() {
        let reader = MockLhmReader::new(None, true);
        let frame = LhmSnapshot {
            ts: Utc.with_ymd_and_hms(2026, 7, 26, 0, 0, 0).unwrap(),
            cpu_temp: Some(61.5),
            cpu_temp_label: Some("Core Max".to_string()),
            gpu_temp: Some(52.0),
            gpu_load: Some(43.0),
            net_up_mbps: 1.0,
            net_down_mbps: 2.0,
            disk_read_mbps: 3.0,
            disk_write_mbps: 4.0,
            disks: Vec::new(),
        };
        reader.push(frame.clone());
        assert_eq!(reader.snapshot(), Some(frame));
        assert!(reader.is_sensor_elevated());

        reader.clear();
        assert!(reader.snapshot().is_none(), "clear must model bridge death");
    }

    fn status() -> PowerStatus {
        PowerStatus {
            current_watts: 20.0,
            limit_watts: 55.0,
            scheme_index: Some(0),
            limits: PowerScheme {
                stapm_watts: 55,
                fast_watts: 100,
                slow_watts: 55,
            },
        }
    }

    #[test]
    fn unsupported_ryzenadj_reports_no_status() {
        let client = MockRyzenAdjClient::unsupported();
        assert!(!client.is_supported());
        assert!(client.read_status().is_none());
    }

    #[test]
    fn supported_but_unavailable_is_distinct_from_unsupported() {
        // 这两种状态映射到不同 HTTP 码（404 vs 503），不能合并。
        let client = MockRyzenAdjClient::supported_but_unavailable();
        assert!(client.is_supported());
        assert!(client.read_status().is_none());
    }

    #[test]
    fn applying_a_scheme_updates_the_reported_limits() {
        let client = MockRyzenAdjClient::supported(status());
        let next = PowerScheme {
            stapm_watts: 85,
            fast_watts: 120,
            slow_watts: 85,
        };
        client.apply_scheme(next).unwrap();

        assert_eq!(client.applied_schemes(), vec![next]);
        let after = client.read_status().unwrap();
        assert_eq!(after.limits, next);
        assert_eq!(after.limit_watts, 85.0);
    }

    #[test]
    fn a_failing_apply_does_not_record_the_scheme() {
        let client = MockRyzenAdjClient::failing_apply(status());
        let err = client
            .apply_scheme(PowerScheme {
                stapm_watts: 85,
                fast_watts: 120,
                slow_watts: 85,
            })
            .unwrap_err();
        assert!(matches!(err, CoreError::RyzenAdj(_)));
        assert!(client.applied_schemes().is_empty());
        assert_eq!(client.read_status().unwrap().limits.stapm_watts, 55);
    }
}
