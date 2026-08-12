use chrono::{DateTime, Duration as ChronoDuration, Utc};
use regex::Regex;
use std::{collections::BTreeMap, fmt, time::Duration};

use sysinfo::{ProcessRefreshKind, ProcessesToUpdate, System, UpdateKind};
use tokio::{
    sync::watch,
    task::JoinHandle,
    time::{sleep_until, Instant},
};
use xhm_core::{
    models::{
        AggregationLevel, ApplicationSetting, LhmSnapshot, MetricAggregation, MetricAggregationMap,
        MetricValue, MetricValueMap, NewAggregatedMetricRecord, NewProcessMetricRecord,
        PowerStatus, PurgeCursor, PurgeWindow, RollupCoverage, WalCheckpointResult,
    },
    wire::{
        select_lite_subset, DiskUsagePayload, HardwareLimitsPayload, ProcessMetadataPayload,
        ProcessMetadataSnapshot, ProcessMetricSnapshot, ProcessSnapshotPayload, PushEvent,
        SubscriptionMode, SystemUsagePayload,
    },
    CoreError, Result as CoreResult,
};

use crate::state::{AppState, ProcessNameRule, PushTarget, RoutedPushEvent};

const BYTES_PER_MB: f64 = 1024.0 * 1024.0;
const HARDWARE_REFRESH_INTERVAL: Duration = Duration::from_secs(60 * 60);
const LIFECYCLE_INTERVAL: Duration = Duration::from_secs(5 * 60);
struct ProcessNameResolver {
    rules: Vec<CompiledProcessNameRule>,
}

struct CompiledProcessNameRule {
    process_name: String,
    keywords: Vec<String>,
    action: ProcessNameAction,
}

enum ProcessNameAction {
    Direct(Option<String>),
    Regex {
        regex: Option<Regex>,
        group: usize,
        format: Option<String>,
    },
    Fallback,
}
fn process_name_matches(rule_name: &str, process_name: &str) -> bool {
    if rule_name.eq_ignore_ascii_case(process_name) {
        return true;
    }
    let suffix_start = process_name.len().saturating_sub(4);
    let has_exe_suffix = process_name
        .as_bytes()
        .get(suffix_start..)
        .is_some_and(|suffix| suffix.eq_ignore_ascii_case(b".exe"));
    has_exe_suffix
        && process_name
            .get(..suffix_start)
            .is_some_and(|base_name| rule_name.eq_ignore_ascii_case(base_name))
}

impl ProcessNameResolver {
    fn new(rules: &[ProcessNameRule]) -> Self {
        let rules = rules
            .iter()
            .map(|rule| {
                let action = match rule.rule_type.to_ascii_lowercase().as_str() {
                    "direct" => ProcessNameAction::Direct(rule.display_name.clone()),
                    "regex" => {
                        let regex = rule.pattern.as_deref().and_then(|pattern| {
                            Regex::new(pattern)
                                .map_err(|error| {
                                    tracing::warn!(
                                        process_name = rule.process_name,
                                        pattern,
                                        %error,
                                        "invalid process name regex"
                                    );
                                    error
                                })
                                .ok()
                        });
                        ProcessNameAction::Regex {
                            regex,
                            group: rule.group.unwrap_or(1),
                            format: rule.format.clone(),
                        }
                    }
                    _ => ProcessNameAction::Fallback,
                };
                CompiledProcessNameRule {
                    process_name: rule.process_name.clone(),
                    keywords: rule
                        .keywords
                        .iter()
                        .map(|keyword| keyword.to_lowercase())
                        .collect(),
                    action,
                }
            })
            .collect();
        Self { rules }
    }

    fn resolve(&self, process_name: &str, command_line: &str) -> String {
        let command_line_lower = command_line.to_lowercase();
        let mut matching_rules = self
            .rules
            .iter()
            .filter(|rule| process_name_matches(&rule.process_name, process_name));
        let rule = matching_rules
            .clone()
            .find(|rule| {
                !rule.keywords.is_empty()
                    && rule
                        .keywords
                        .iter()
                        .any(|keyword| command_line_lower.contains(keyword))
            })
            .or_else(|| matching_rules.find(|rule| rule.keywords.is_empty()));
        let Some(rule) = rule else {
            return process_name.to_owned();
        };

        let resolved = match &rule.action {
            ProcessNameAction::Direct(display_name) => display_name.clone(),
            ProcessNameAction::Regex {
                regex,
                group,
                format,
            } => regex
                .as_ref()
                .and_then(|regex| regex.captures(command_line))
                .and_then(|captures| captures.get(*group))
                .map(|capture| {
                    format
                        .as_deref()
                        .map(|template| template.replace("{0}", capture.as_str()))
                        .unwrap_or_else(|| capture.as_str().to_owned())
                }),
            ProcessNameAction::Fallback => None,
        };
        resolved
            .filter(|name| !name.is_empty())
            .unwrap_or_else(|| process_name.to_owned())
    }
}

/// Owns the service's collection and metric lifecycle tasks.
pub struct ServiceWorker {
    shutdown_tx: watch::Sender<bool>,
    collection_task: Option<JoinHandle<()>>,
    lifecycle_task: Option<JoinHandle<()>>,
}

impl fmt::Debug for ServiceWorker {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let collection_finished = self
            .collection_task
            .as_ref()
            .map(JoinHandle::is_finished)
            .unwrap_or(true);
        let lifecycle_finished = self
            .lifecycle_task
            .as_ref()
            .map(JoinHandle::is_finished)
            .unwrap_or(true);

        formatter
            .debug_struct("ServiceWorker")
            .field("shutdown_requested", &*self.shutdown_tx.borrow())
            .field("collection_finished", &collection_finished)
            .field("lifecycle_finished", &lifecycle_finished)
            .finish()
    }
}

impl ServiceWorker {
    /// Starts collection and lifecycle processing immediately on the current Tokio runtime.
    pub fn start(state: AppState) -> ServiceWorker {
        let (shutdown_tx, shutdown_rx) = watch::channel(false);
        let collection_task = tokio::spawn(run_worker(state.clone(), shutdown_rx.clone()));
        let lifecycle_task = tokio::spawn(run_lifecycle_worker(state, shutdown_rx));

        ServiceWorker {
            shutdown_tx,
            collection_task: Some(collection_task),
            lifecycle_task: Some(lifecycle_task),
        }
    }

    /// Requests shutdown and waits for both tasks to finish. Repeated calls are harmless.
    pub async fn shutdown(&mut self) {
        let _ = self.shutdown_tx.send(true);
        if let Some(task) = self.collection_task.take() {
            if let Err(error) = task.await {
                tracing::error!(%error, "collection task failed during shutdown");
            }
        }
        if let Some(task) = self.lifecycle_task.take() {
            if let Err(error) = task.await {
                tracing::error!(%error, "lifecycle task failed during shutdown");
            }
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
    let process_name_resolver = {
        let runtime = state.runtime.read().await;
        ProcessNameResolver::new(&runtime.process_name_rules)
    };

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

            collect_cycle(
                &state,
                &mut system,
                max_memory,
                &keywords,
                &process_name_resolver,
            )
            .await;
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

#[derive(Debug, Clone, PartialEq, Eq)]
struct RetentionPolicy {
    configured_days: i64,
    raw: ChronoDuration,
    minute: ChronoDuration,
    hour: ChronoDuration,
    day: ChronoDuration,
}

impl RetentionPolicy {
    fn from_settings(settings: &[ApplicationSetting]) -> CoreResult<Self> {
        let value = settings
            .iter()
            .find(|setting| {
                setting.category == "DataCollection" && setting.key == "DataRetentionDays"
            })
            .ok_or_else(|| CoreError::invalid("missing DataCollection.DataRetentionDays"))?;
        let configured_days = value
            .value
            .trim()
            .parse::<i64>()
            .map_err(|_| CoreError::invalid("malformed DataCollection.DataRetentionDays"))?;
        Self::from_configured_days(configured_days)
    }

    fn from_configured_days(configured_days: i64) -> CoreResult<Self> {
        if configured_days <= 0 {
            return Err(CoreError::invalid(
                "DataCollection.DataRetentionDays must be positive",
            ));
        }
        let minute_days = if configured_days <= 1 {
            configured_days
        } else {
            (configured_days / 2).max(1)
        };
        let days = |value| {
            ChronoDuration::try_days(value)
                .ok_or_else(|| CoreError::invalid("DataRetentionDays exceeds chrono range"))
        };
        Ok(Self {
            configured_days,
            raw: ChronoDuration::hours(12),
            minute: days(minute_days)?,
            hour: days(configured_days)?,
            day: days(configured_days)?,
        })
    }
}

#[derive(Debug, Clone, Copy)]
struct LifecycleLimits {
    max_buckets_per_tier: usize,
    read_batch_size: usize,
    delete_batch_size: usize,
    max_delete_batches: usize,
    max_purge_time: Duration,
}

impl Default for LifecycleLimits {
    fn default() -> Self {
        Self {
            max_buckets_per_tier: 60,
            read_batch_size: 512,
            delete_batch_size: 256,
            max_delete_batches: 16,
            max_purge_time: Duration::from_millis(250),
        }
    }
}

#[derive(Debug)]
struct TierCycleResult {
    buckets: usize,
    rows_read: usize,
    inserted: usize,
    replaced: usize,
    verified: usize,
    coverage_before: Option<RollupCoverage>,
    coverage_after: Option<RollupCoverage>,
}

#[derive(Debug)]
struct LifecycleCycleResult {
    configured_days: Option<i64>,
    retention_windows: Option<[ChronoDuration; 4]>,
    cutoffs: Option<[DateTime<Utc>; 4]>,
    tiers: Vec<TierCycleResult>,
    deleted: [u64; 4],
    wal: Option<WalCheckpointResult>,
    elapsed: Duration,
    failure: Option<String>,
    purge_skipped: Option<String>,
}

fn bucket_seconds(level: AggregationLevel) -> i64 {
    match level {
        AggregationLevel::Minute => 60,
        AggregationLevel::Hour => 60 * 60,
        AggregationLevel::Day => 24 * 60 * 60,
    }
}

fn floor_bucket(value: DateTime<Utc>, level: AggregationLevel) -> CoreResult<DateTime<Utc>> {
    let seconds = bucket_seconds(level);
    DateTime::from_timestamp(value.timestamp().div_euclid(seconds) * seconds, 0)
        .ok_or_else(|| CoreError::invalid("bucket boundary is outside chrono range"))
}

fn ceil_bucket(value: DateTime<Utc>, level: AggregationLevel) -> CoreResult<DateTime<Utc>> {
    let floor = floor_bucket(value, level)?;
    if floor == value {
        Ok(floor)
    } else {
        floor
            .checked_add_signed(ChronoDuration::seconds(bucket_seconds(level)))
            .ok_or_else(|| CoreError::invalid("bucket boundary overflow"))
    }
}

fn checked_cutoff(
    now: DateTime<Utc>,
    retention: ChronoDuration,
    level: Option<AggregationLevel>,
) -> CoreResult<DateTime<Utc>> {
    let cutoff = now
        .checked_sub_signed(retention)
        .ok_or_else(|| CoreError::invalid("retention cutoff is outside chrono range"))?;
    match level {
        Some(level) => floor_bucket(cutoff, level),
        None => Ok(cutoff),
    }
}

fn retention_cutoffs(
    now: DateTime<Utc>,
    policy: &RetentionPolicy,
) -> CoreResult<[DateTime<Utc>; 4]> {
    Ok([
        checked_cutoff(now, policy.raw, None)?,
        checked_cutoff(now, policy.minute, Some(AggregationLevel::Minute))?,
        checked_cutoff(now, policy.hour, Some(AggregationLevel::Hour))?,
        checked_cutoff(now, policy.day, Some(AggregationLevel::Day))?,
    ])
}

fn finalize_groups(
    target: AggregationLevel,
    timestamp: DateTime<Utc>,
    groups: BTreeMap<(i32, String), MetricAggregationMap>,
) -> CoreResult<Vec<NewAggregatedMetricRecord>> {
    let mut records = Vec::with_capacity(groups.len());
    for ((process_id, process_name), mut metrics) in groups {
        for metric in metrics.values_mut() {
            metric.finalize();
        }
        let metrics_json = serde_json::to_string(&metrics)
            .map_err(|error| CoreError::invalid(format!("serializing rollup metrics: {error}")))?;
        records.push(NewAggregatedMetricRecord {
            process_id,
            process_name,
            aggregation_level: target,
            timestamp,
            metrics_json,
        });
    }
    Ok(records)
}

fn aggregate_raw_bucket(
    state: &AppState,
    target: AggregationLevel,
    from: DateTime<Utc>,
    to: DateTime<Utc>,
    batch_size: usize,
) -> CoreResult<(Vec<NewAggregatedMetricRecord>, usize)> {
    let mut groups = BTreeMap::<(i32, String), MetricAggregationMap>::new();
    let mut after_id = 0;
    let mut rows_read = 0;
    loop {
        let rows = state
            .store
            .raw_batch_for_aggregation(from, to, after_id, batch_size)?;
        let row_count = rows.len();
        for row in rows {
            after_id = row.id;
            let values: MetricValueMap =
                serde_json::from_str(&row.metrics_json).map_err(|error| {
                    CoreError::invalid(format!(
                        "parsing raw MetricsJson for row {}: {error}",
                        row.id
                    ))
                })?;
            let metrics = groups
                .entry((row.process_id, row.process_name))
                .or_default();
            for (name, value) in values {
                let unit = value.unit.unwrap_or_default();
                metrics
                    .entry(name)
                    .or_insert_with(|| MetricAggregation::empty(unit))
                    .merge_value(value.value);
            }
        }
        rows_read += row_count;
        if row_count < batch_size {
            break;
        }
    }
    Ok((finalize_groups(target, from, groups)?, rows_read))
}

fn aggregate_rollup_bucket(
    state: &AppState,
    source: AggregationLevel,
    target: AggregationLevel,
    from: DateTime<Utc>,
    to: DateTime<Utc>,
    batch_size: usize,
) -> CoreResult<(Vec<NewAggregatedMetricRecord>, usize)> {
    let mut groups = BTreeMap::<(i32, String), MetricAggregationMap>::new();
    let mut after_id = 0;
    let mut rows_read = 0;
    loop {
        let rows = state
            .store
            .aggregate_batch_for_rollup(source, from, to, after_id, batch_size)?;
        let row_count = rows.len();
        for row in rows {
            after_id = row.id;
            let values: MetricAggregationMap =
                serde_json::from_str(&row.metrics_json).map_err(|error| {
                    CoreError::invalid(format!(
                        "parsing aggregate MetricsJson for row {}: {error}",
                        row.id
                    ))
                })?;
            let metrics = groups
                .entry((row.process_id, row.process_name))
                .or_default();
            for (name, value) in values {
                let unit = value.unit.clone();
                metrics
                    .entry(name)
                    .or_insert_with(|| MetricAggregation::empty(unit))
                    .merge_aggregation(&value);
            }
        }
        rows_read += row_count;
        if row_count < batch_size {
            break;
        }
    }
    Ok((finalize_groups(target, from, groups)?, rows_read))
}

fn run_rollup_tier(
    state: &AppState,
    source: Option<AggregationLevel>,
    target: AggregationLevel,
    now: DateTime<Utc>,
    limits: LifecycleLimits,
) -> CoreResult<TierCycleResult> {
    let coverage_before = state.store.rollup_coverage(target)?;
    let source_coverage = source
        .map(|level| state.store.rollup_coverage(level))
        .transpose()?
        .flatten();
    let (covered_from, mut next_bucket) = if let Some(coverage) = &coverage_before {
        (coverage.covered_from, coverage.completed_through)
    } else if source.is_none() {
        let Some(earliest) = state.store.earliest_raw_timestamp()? else {
            return Ok(TierCycleResult {
                buckets: 0,
                rows_read: 0,
                inserted: 0,
                replaced: 0,
                verified: 0,
                coverage_before: None,
                coverage_after: None,
            });
        };
        let start = floor_bucket(earliest, target)?;
        (start, start)
    } else {
        let Some(source_coverage) = &source_coverage else {
            return Ok(TierCycleResult {
                buckets: 0,
                rows_read: 0,
                inserted: 0,
                replaced: 0,
                verified: 0,
                coverage_before: None,
                coverage_after: None,
            });
        };
        let start = ceil_bucket(source_coverage.covered_from, target)?;
        (start, start)
    };
    let mut final_boundary = floor_bucket(now, target)?;
    if let Some(source_coverage) = &source_coverage {
        final_boundary = final_boundary.min(source_coverage.completed_through);
    }

    let mut result = TierCycleResult {
        buckets: 0,
        rows_read: 0,
        inserted: 0,
        replaced: 0,
        verified: 0,
        coverage_before,
        coverage_after: None,
    };
    let step = ChronoDuration::seconds(bucket_seconds(target));
    while next_bucket < final_boundary && result.buckets < limits.max_buckets_per_tier {
        let bucket_end = next_bucket
            .checked_add_signed(step)
            .ok_or_else(|| CoreError::invalid("rollup bucket end overflow"))?;
        if bucket_end > final_boundary {
            break;
        }
        if let Some(source_coverage) = &source_coverage {
            if next_bucket < source_coverage.covered_from
                || bucket_end > source_coverage.completed_through
            {
                return Err(CoreError::invalid(
                    "source rollup coverage does not contain destination bucket",
                ));
            }
        }
        let (records, rows_read) = match source {
            None => aggregate_raw_bucket(
                state,
                target,
                next_bucket,
                bucket_end,
                limits.read_batch_size,
            )?,
            Some(source) => aggregate_rollup_bucket(
                state,
                source,
                target,
                next_bucket,
                bucket_end,
                limits.read_batch_size,
            )?,
        };
        let commit =
            state
                .store
                .commit_rollup(target, covered_from, next_bucket, bucket_end, &records)?;
        result.buckets += 1;
        result.rows_read += rows_read;
        result.inserted += commit.inserted;
        result.replaced += commit.replaced;
        result.verified += commit.verified;
        result.coverage_after = Some(commit.coverage);
        next_bucket = bucket_end;
    }
    if result.coverage_after.is_none() {
        result.coverage_after = result.coverage_before.clone();
    }
    Ok(result)
}

fn covered_purge_window(
    state: &AppState,
    destination: AggregationLevel,
    policy_cutoff: DateTime<Utc>,
) -> CoreResult<Option<PurgeWindow>> {
    let Some(coverage) = state.store.rollup_coverage(destination)? else {
        return Ok(None);
    };
    let cutoff = policy_cutoff.min(coverage.completed_through);
    if cutoff <= coverage.covered_from {
        return Ok(None);
    }
    Ok(Some(PurgeWindow::covered(coverage.covered_from, cutoff)))
}

fn purge_tier(
    state: &AppState,
    level: Option<AggregationLevel>,
    window: &PurgeWindow,
    limits: LifecycleLimits,
    batches: &mut usize,
    deadline: Instant,
) -> CoreResult<u64> {
    let mut cursor: Option<PurgeCursor> = None;
    let mut deleted = 0u64;
    while *batches < limits.max_delete_batches && Instant::now() < deadline {
        let result = match level {
            Some(level) => state.store.purge_aggregate_batch(
                level,
                window,
                cursor.as_ref(),
                limits.delete_batch_size,
            )?,
            None => {
                state
                    .store
                    .purge_raw_batch(window, cursor.as_ref(), limits.delete_batch_size)?
            }
        };
        *batches += 1;
        deleted = deleted.saturating_add(result.deleted);
        if result.exhausted {
            break;
        }
        let Some(next_cursor) = result.next_cursor else {
            return Err(CoreError::storage(
                "non-exhausted purge batch returned no continuation cursor",
            ));
        };
        cursor = Some(next_cursor);
    }
    Ok(deleted)
}

fn run_lifecycle_cycle(state: &AppState, limits: LifecycleLimits) -> LifecycleCycleResult {
    let started = Instant::now();
    let now = state.clock.now_utc();
    let policy_result = state
        .store
        .list_settings()
        .and_then(|settings| RetentionPolicy::from_settings(&settings));
    let mut result = LifecycleCycleResult {
        configured_days: policy_result
            .as_ref()
            .ok()
            .map(|policy| policy.configured_days),
        retention_windows: policy_result
            .as_ref()
            .ok()
            .map(|policy| [policy.raw, policy.minute, policy.hour, policy.day]),
        cutoffs: None,
        tiers: Vec::with_capacity(3),
        deleted: [0; 4],
        wal: None,
        elapsed: Duration::ZERO,
        failure: None,
        purge_skipped: None,
    };

    for (source, target) in [
        (None, AggregationLevel::Minute),
        (Some(AggregationLevel::Minute), AggregationLevel::Hour),
        (Some(AggregationLevel::Hour), AggregationLevel::Day),
    ] {
        match run_rollup_tier(state, source, target, now, limits) {
            Ok(tier) => result.tiers.push(tier),
            Err(error) => {
                result.failure = Some(format!("{target:?} rollup failed: {error}"));
                result.purge_skipped = Some("aggregation failure".to_owned());
                result.elapsed = started.elapsed();
                return result;
            }
        }
    }

    let policy = match policy_result {
        Ok(policy) => policy,
        Err(error) => {
            tracing::warn!(%error, "metric purge disabled by invalid retention setting");
            result.purge_skipped = Some(error.to_string());
            result.elapsed = started.elapsed();
            return result;
        }
    };
    let cutoffs = match retention_cutoffs(now, &policy) {
        Ok(cutoffs) => cutoffs,
        Err(error) => {
            result.failure = Some(error.to_string());
            result.purge_skipped = Some("retention cutoff failure".to_owned());
            result.elapsed = started.elapsed();
            return result;
        }
    };
    result.cutoffs = Some(cutoffs);
    let deadline = Instant::now() + limits.max_purge_time;
    let mut batches = 0usize;
    let purge_result = (|| -> CoreResult<()> {
        if let Some(window) = covered_purge_window(state, AggregationLevel::Minute, cutoffs[0])? {
            result.deleted[0] = purge_tier(state, None, &window, limits, &mut batches, deadline)?;
        }
        if let Some(window) = covered_purge_window(state, AggregationLevel::Hour, cutoffs[1])? {
            result.deleted[1] = purge_tier(
                state,
                Some(AggregationLevel::Minute),
                &window,
                limits,
                &mut batches,
                deadline,
            )?;
        }
        if let Some(window) = covered_purge_window(state, AggregationLevel::Day, cutoffs[2])? {
            result.deleted[2] = purge_tier(
                state,
                Some(AggregationLevel::Hour),
                &window,
                limits,
                &mut batches,
                deadline,
            )?;
        }
        let day_window = PurgeWindow::terminal(cutoffs[3]);
        result.deleted[3] = purge_tier(
            state,
            Some(AggregationLevel::Day),
            &day_window,
            limits,
            &mut batches,
            deadline,
        )?;
        Ok(())
    })();
    if let Err(error) = purge_result {
        result.failure = Some(format!("purge failed: {error}"));
    }
    if result.deleted.iter().any(|deleted| *deleted > 0) {
        match state.store.checkpoint_wal() {
            Ok(wal) => {
                if wal.busy > 0 {
                    tracing::warn!(busy = wal.busy, "SQLite WAL checkpoint remained busy");
                }
                result.wal = Some(wal);
            }
            Err(error) => result.failure = Some(format!("WAL checkpoint failed: {error}")),
        }
    }
    result.elapsed = started.elapsed();
    result
}

async fn run_lifecycle_worker(state: AppState, mut shutdown_rx: watch::Receiver<bool>) {
    let limits = LifecycleLimits::default();
    loop {
        if *shutdown_rx.borrow() {
            break;
        }
        if state.runtime.read().await.record_metrics {
            let cycle_state = state.clone();
            match tokio::task::spawn_blocking(move || run_lifecycle_cycle(&cycle_state, limits))
                .await
            {
                Ok(result) => tracing::info!(
                    configured_days = ?result.configured_days,
                    retention_windows = ?result.retention_windows,
                    cutoffs = ?result.cutoffs,
                    tiers = ?result.tiers,
                    wal = ?result.wal,
                    deleted_raw = result.deleted[0],
                    deleted_minute = result.deleted[1],
                    deleted_hour = result.deleted[2],
                    deleted_day = result.deleted[3],
                    failure = ?result.failure,
                    purge_skipped = ?result.purge_skipped,
                    elapsed_ms = result.elapsed.as_millis(),
                    "metric lifecycle cycle completed"
                ),
                Err(error) => tracing::error!(%error, "metric lifecycle blocking task failed"),
            }
        }

        let deadline = Instant::now() + LIFECYCLE_INTERVAL;
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
    process_name_resolver: &ProcessNameResolver,
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
        let gpu_usage = lhm
            .as_ref()
            .and_then(|snapshot| snapshot.process_gpu_usage.get(&process_id))
            .copied()
            .unwrap_or(0.0);
        let vram_mb = lhm
            .as_ref()
            .and_then(|snapshot| snapshot.process_vram_mb.get(&process_id))
            .copied()
            .unwrap_or(0.0);
        let display_name = process_name_resolver.resolve(&process_name, &command_line);

        match build_process_sample(ProcessSampleInput {
            process_id,
            process_name,
            command_line,
            display_name,
            cpu_usage: f64::from(process.cpu_usage()),
            memory_bytes: process.memory(),
            gpu_usage,
            vram_mb,
            timestamp,
        }) {
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

    persist_process_records(state, records).await;

    route_process_events(state, wire_timestamp, snapshots, metadata).await;
}

async fn persist_process_records(state: &AppState, records: Vec<NewProcessMetricRecord>) {
    if !state.runtime.read().await.record_metrics {
        return;
    }

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

struct ProcessSampleInput {
    process_id: i32,
    process_name: String,
    command_line: String,
    display_name: String,
    cpu_usage: f64,
    memory_bytes: u64,
    gpu_usage: f64,
    vram_mb: f64,
    timestamp: chrono::DateTime<chrono::Utc>,
}

fn build_process_sample(input: ProcessSampleInput) -> Result<ProcessSample, serde_json::Error> {
    let ProcessSampleInput {
        process_id,
        process_name,
        command_line,
        display_name,
        cpu_usage,
        memory_bytes,
        gpu_usage,
        vram_mb,
        timestamp,
    } = input;
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
        "gpu".to_owned(),
        MetricValue {
            value: round_one(nonnegative(gpu_usage)),
            unit: Some("%".to_owned()),
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
    use xhm_core::{
        time::to_sqlite_text,
        traits::{MetricStore, MockClock, MockLhmReader, MockRyzenAdjClient},
    };

    use super::*;
    use crate::{
        db::SqliteMetricStore,
        state::{RuntimeConfig, ServicePaths},
    };

    fn fixed_time() -> chrono::DateTime<Utc> {
        Utc.with_ymd_and_hms(2026, 7, 26, 10, 30, 0).unwrap()
    }

    fn test_state(runtime: RuntimeConfig) -> AppState {
        test_state_with_store(runtime).0
    }

    fn test_state_with_store(runtime: RuntimeConfig) -> (AppState, Arc<SqliteMetricStore>) {
        let store = Arc::new(SqliteMetricStore::open_in_memory().unwrap());
        let state = AppState::new(
            store.clone(),
            Arc::new(MockClock::new(fixed_time())),
            Arc::new(MockLhmReader::new(None, false)),
            Arc::new(MockRyzenAdjClient::unsupported()),
            ServicePaths::for_exe_dir(std::env::temp_dir().join("xhm-worker-tests")),
            runtime,
        );
        (state, store)
    }

    fn retention_setting(value: &str) -> ApplicationSetting {
        ApplicationSetting {
            id: 7,
            category: "DataCollection".to_owned(),
            key: "DataRetentionDays".to_owned(),
            value: value.to_owned(),
            created_at: fixed_time(),
            updated_at: fixed_time(),
        }
    }

    fn raw_metric(
        process_id: i32,
        process_name: &str,
        timestamp: DateTime<Utc>,
        metrics_json: &str,
    ) -> NewProcessMetricRecord {
        NewProcessMetricRecord {
            process_id,
            process_name: process_name.to_owned(),
            command_line: None,
            display_name: None,
            timestamp,
            metrics_json: metrics_json.to_owned(),
        }
    }

    #[test]
    fn retention_policy_pins_default_short_and_odd_day_rules() {
        let one = RetentionPolicy::from_configured_days(1).unwrap();
        let two = RetentionPolicy::from_configured_days(2).unwrap();
        let three = RetentionPolicy::from_configured_days(3).unwrap();
        let thirty = RetentionPolicy::from_configured_days(30).unwrap();
        let thirty_one = RetentionPolicy::from_configured_days(31).unwrap();

        assert_eq!(one.minute, ChronoDuration::days(1));
        assert_eq!(two.minute, ChronoDuration::days(1));
        assert_eq!(three.minute, ChronoDuration::days(1));
        assert_eq!(thirty.raw, ChronoDuration::hours(12));
        assert_eq!(thirty.minute, ChronoDuration::days(15));
        assert_eq!(thirty.hour, ChronoDuration::days(30));
        assert_eq!(thirty.day, ChronoDuration::days(30));
        assert_eq!(thirty_one.minute, ChronoDuration::days(15));
        assert!(RetentionPolicy::from_configured_days(0).is_err());
        assert!(RetentionPolicy::from_configured_days(-1).is_err());
        assert!(RetentionPolicy::from_settings(&[]).is_err());
        assert!(RetentionPolicy::from_settings(&[retention_setting("not-a-number")]).is_err());
    }

    #[test]
    fn lifecycle_creates_only_closed_utc_buckets_and_is_idempotent() {
        let state = test_state(RuntimeConfig::default());
        let at = |minute, second| {
            Utc.with_ymd_and_hms(2026, 7, 26, 10, minute, second)
                .unwrap()
        };
        state
            .store
            .save_process_metrics(&[
                raw_metric(7, "worker", at(28, 59), r#"{"cpu":{"value":1.0}}"#),
                raw_metric(7, "worker", at(29, 0), r#"{"cpu":{"value":2.0}}"#),
                raw_metric(7, "worker", at(29, 59), r#"{"cpu":{"value":4.0}}"#),
                raw_metric(7, "worker", at(30, 0), r#"{"cpu":{"value":8.0}}"#),
            ])
            .unwrap();
        let limits = LifecycleLimits {
            max_buckets_per_tier: 120,
            max_delete_batches: 0,
            ..LifecycleLimits::default()
        };

        let first = run_lifecycle_cycle(&state, limits);
        assert!(first.failure.is_none(), "{:?}", first.failure);
        let rows = state
            .store
            .history_aggregated(7, AggregationLevel::Minute, None, None)
            .unwrap();
        assert_eq!(
            rows.iter().map(|row| row.timestamp).collect::<Vec<_>>(),
            [at(28, 0), at(29, 0)]
        );
        let second = run_lifecycle_cycle(&state, limits);
        assert!(second.failure.is_none(), "{:?}", second.failure);
        assert_eq!(
            state
                .store
                .history_aggregated(7, AggregationLevel::Minute, None, None)
                .unwrap()
                .len(),
            2
        );
    }

    #[test]
    fn lifecycle_purges_covered_minute_and_hour_rows_without_deleting_partial_fringe() {
        let (state, store) = test_state_with_store(RuntimeConfig::default());
        let now = fixed_time();
        let covered_from =
            floor_bucket(now - ChronoDuration::days(45), AggregationLevel::Day).unwrap();
        let minute_fringe = covered_from - ChronoDuration::minutes(1);
        let minute_eligible = covered_from + ChronoDuration::seconds(30);
        let hour_fringe = covered_from - ChronoDuration::hours(1);
        let hour_eligible = covered_from + ChronoDuration::minutes(30);
        let aggregate = |level, timestamp| NewAggregatedMetricRecord {
            process_id: 77,
            process_name: "fringe-test".to_owned(),
            aggregation_level: level,
            timestamp,
            metrics_json: "{}".to_owned(),
        };

        {
            let connection = store.test_connection().unwrap();
            for (level, timestamp) in [
                (AggregationLevel::Minute, minute_fringe),
                (AggregationLevel::Hour, hour_fringe),
            ] {
                connection
                    .execute(
                        "INSERT INTO \"AggregatedMetricRecords\"
                             (\"ProcessId\", \"ProcessName\", \"AggregationLevel\", \"Timestamp\", \"MetricsJson\")
                         VALUES (77, 'fringe-test', ?1, ?2, '{}')",
                        rusqlite::params![i32::from(level), to_sqlite_text(&timestamp)],
                    )
                    .unwrap();
            }
        }
        state
            .store
            .commit_rollup(
                AggregationLevel::Minute,
                covered_from,
                covered_from,
                covered_from + ChronoDuration::minutes(1),
                &[aggregate(AggregationLevel::Minute, minute_eligible)],
            )
            .unwrap();
        state
            .store
            .commit_rollup(
                AggregationLevel::Hour,
                covered_from,
                covered_from,
                covered_from + ChronoDuration::hours(1),
                &[aggregate(AggregationLevel::Hour, hour_eligible)],
            )
            .unwrap();
        state
            .store
            .commit_rollup(
                AggregationLevel::Day,
                covered_from,
                covered_from,
                covered_from + ChronoDuration::days(1),
                &[],
            )
            .unwrap();

        let result = run_lifecycle_cycle(&state, LifecycleLimits::default());
        assert!(result.failure.is_none(), "{:?}", result.failure);
        assert_eq!(result.deleted, [0, 1, 1, 0]);
        let minute_rows = state
            .store
            .history_aggregated(77, AggregationLevel::Minute, None, None)
            .unwrap();
        assert_eq!(minute_rows.len(), 1);
        assert_eq!(minute_rows[0].timestamp, minute_fringe);
        let hour_rows = state
            .store
            .history_aggregated(77, AggregationLevel::Hour, None, None)
            .unwrap();
        assert_eq!(hour_rows.len(), 1);
        assert_eq!(hour_rows[0].timestamp, hour_fringe);
    }

    #[test]
    fn aggregate_rollup_uses_weighted_sum_and_count() {
        let state = test_state(RuntimeConfig::default());
        let from = Utc.with_ymd_and_hms(2026, 7, 26, 10, 0, 0).unwrap();
        let second = from + ChronoDuration::seconds(30);
        let metrics = |min: f64, max: f64, sum: f64, count: i64| {
            serde_json::to_string(&BTreeMap::from([(
                "cpu".to_owned(),
                MetricAggregation {
                    min,
                    max,
                    avg: sum / count as f64,
                    sum,
                    count,
                    unit: "%".to_owned(),
                },
            )]))
            .unwrap()
        };
        state
            .store
            .commit_rollup(
                AggregationLevel::Minute,
                from,
                from,
                from + ChronoDuration::minutes(1),
                &[
                    NewAggregatedMetricRecord {
                        process_id: 7,
                        process_name: "worker".to_owned(),
                        aggregation_level: AggregationLevel::Minute,
                        timestamp: from,
                        metrics_json: metrics(1.0, 3.0, 4.0, 2),
                    },
                    NewAggregatedMetricRecord {
                        process_id: 7,
                        process_name: "worker".to_owned(),
                        aggregation_level: AggregationLevel::Minute,
                        timestamp: second,
                        metrics_json: metrics(8.0, 8.0, 8.0, 1),
                    },
                ],
            )
            .unwrap();

        let (records, rows_read) = aggregate_rollup_bucket(
            &state,
            AggregationLevel::Minute,
            AggregationLevel::Hour,
            from,
            from + ChronoDuration::hours(1),
            1,
        )
        .unwrap();
        assert_eq!(rows_read, 2);
        let rolled: MetricAggregationMap = serde_json::from_str(&records[0].metrics_json).unwrap();
        let cpu = &rolled["cpu"];
        assert_eq!(
            (cpu.min, cpu.max, cpu.sum, cpu.count, cpu.avg),
            (1.0, 8.0, 12.0, 3, 4.0)
        );
    }

    #[test]
    fn lifecycle_hot_reloads_policy_and_invalid_value_disables_purge() {
        let state = test_state(RuntimeConfig::default());
        let limits = LifecycleLimits {
            max_delete_batches: 0,
            ..LifecycleLimits::default()
        };
        let first = run_lifecycle_cycle(&state, limits);
        assert_eq!(first.configured_days, Some(30));
        assert_eq!(
            first.retention_windows.unwrap(),
            [
                ChronoDuration::hours(12),
                ChronoDuration::days(15),
                ChronoDuration::days(30),
                ChronoDuration::days(30),
            ]
        );
        state
            .store
            .update_setting("DataCollection", "DataRetentionDays", "10", fixed_time())
            .unwrap();
        let second = run_lifecycle_cycle(&state, limits);
        assert_eq!(second.configured_days, Some(10));
        assert_eq!(
            second.retention_windows.unwrap()[1],
            ChronoDuration::days(5)
        );
        let old = fixed_time() - ChronoDuration::days(40);
        state
            .store
            .save_process_metrics(&[raw_metric(9, "retained", old, r#"{"cpu":{"value":1.0}}"#)])
            .unwrap();

        state
            .store
            .update_setting(
                "DataCollection",
                "DataRetentionDays",
                "invalid",
                fixed_time(),
            )
            .unwrap();
        let invalid = run_lifecycle_cycle(&state, LifecycleLimits::default());
        assert!(invalid.configured_days.is_none());
        assert!(invalid.purge_skipped.is_some());
        assert_eq!(invalid.deleted, [0; 4]);
        assert_eq!(state.store.history_raw(9, None, None).unwrap().len(), 1);
    }

    #[test]
    fn aggregation_failure_prevents_every_purge() {
        let state = test_state(RuntimeConfig::default());
        let old = fixed_time() - ChronoDuration::days(40);
        state
            .store
            .save_process_metrics(&[raw_metric(7, "broken", old, r#"{"cpu":"bad"}"#)])
            .unwrap();
        let result = run_lifecycle_cycle(&state, LifecycleLimits::default());
        assert!(result.failure.is_some());
        assert_eq!(result.deleted, [0; 4]);
        assert_eq!(state.store.history_raw(7, None, None).unwrap().len(), 1);
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
        let sample = build_process_sample(ProcessSampleInput {
            process_id: 42,
            process_name: "alpha".to_owned(),
            command_line: "alpha.exe --serve".to_owned(),
            display_name: "Alpha Service".to_owned(),
            cpu_usage: 12.34,
            memory_bytes: 10 * 1024 * 1024,
            gpu_usage: 72.34,
            vram_mb: 512.34,
            timestamp: fixed_time(),
        })
        .unwrap();
        assert_eq!(sample.record.display_name.as_deref(), Some("Alpha Service"));
        assert_eq!(
            sample.snapshot.display_name.as_deref(),
            Some("Alpha Service")
        );
        assert_eq!(sample.metadata.display_name, "Alpha Service");
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
            metrics.get("gpu"),
            Some(&MetricValue {
                value: 72.3,
                unit: Some("%".to_owned()),
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
        assert_eq!(sample.snapshot.metrics.get("gpu"), Some(&72.3));
        assert_eq!(sample.snapshot.metrics.get("vram"), Some(&512.3));
        assert_eq!(sample.record.timestamp, fixed_time());
    }

    #[test]
    fn process_name_resolver_matches_csharp_rule_precedence_and_fallbacks() {
        let resolver = ProcessNameResolver::new(&[
            ProcessNameRule {
                process_name: "python".to_owned(),
                keywords: vec!["comfyui".to_owned()],
                rule_type: "Direct".to_owned(),
                pattern: None,
                group: None,
                format: None,
                display_name: Some("Python: ComfyUI".to_owned()),
            },
            ProcessNameRule {
                process_name: "python".to_owned(),
                keywords: Vec::new(),
                rule_type: "Regex".to_owned(),
                pattern: Some(r"([^\\/\s]+\.py)".to_owned()),
                group: Some(1),
                format: Some("Python: {0}".to_owned()),
                display_name: None,
            },
        ]);

        assert_eq!(
            resolver.resolve("PYTHON.EXE", r"python.exe main.py --comfyui"),
            "Python: ComfyUI"
        );
        assert_eq!(
            resolver.resolve("python.exe", r"python.exe worker.py"),
            "Python: worker.py"
        );
        assert_eq!(resolver.resolve("node", "node server.js"), "node");
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
            process_gpu_usage: Default::default(),
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
            let sample = build_process_sample(ProcessSampleInput {
                process_id,
                process_name: format!("process-{process_id}"),
                command_line: format!("process-{process_id}.exe"),
                display_name: format!("Process {process_id}"),
                cpu_usage: f64::from(100 - process_id),
                memory_bytes: process_id as u64 * 1024 * 1024,
                gpu_usage: 0.0,
                vram_mb: 0.0,
                timestamp: fixed_time(),
            })
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
        assert_eq!(metadata.processes[0].display_name, "Process 1");
        assert!(matches!(receiver.try_recv(), Err(TryRecvError::Empty)));
    }

    #[tokio::test]
    async fn metric_recording_defaults_off_and_hot_enables_persistence() {
        let (state, store) = test_state_with_store(RuntimeConfig::default());
        let record = raw_metric(7, "worker", fixed_time(), r#"{"cpu":{"value":1.0}}"#);

        persist_process_records(&state, vec![record.clone()]).await;
        assert!(store.history_raw(7, None, None).unwrap().is_empty());

        state.runtime.write().await.record_metrics = true;
        persist_process_records(&state, vec![record]).await;
        assert_eq!(store.history_raw(7, None, None).unwrap().len(), 1);
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
        assert!(worker.collection_task.is_none());
        assert!(worker.lifecycle_task.is_none());
    }
}
