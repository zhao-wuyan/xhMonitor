//! 持久化实体与 REST 线上契约。
//!
//! 这些类型是**逐字段对齐** C# 版本的线上格式，任何重命名都会静默破坏
//! 未修改的 React 前端。三条易错规则（来自 `Program.cs:310` 未配置
//! `AddJsonOptions`，因此 MVC 使用 `JsonSerializerDefaults.Web`）：
//!
//! 1. **属性名 camelCase，但 Map 的 key 逐字保留**（`DictionaryKeyPolicy` 未设置）。
//!    `/api/v1/config/settings` 的 `"Appearance"` / `"ThemeColor"` 必须保持 PascalCase。
//! 2. **枚举是整数**（未注册 `JsonStringEnumConverter`）：`aggregationLevel: 1|2|3`。
//! 3. **null 必须写出，不能省略**（`DefaultIgnoreCondition = Never`）。
//!    只有 SignalR 的 `ProcessMetricSnapshot` 三个字段是显式 `[JsonIgnore]` 的例外。

use std::collections::BTreeMap;

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

use crate::error::CoreError;

// ─────────────────────────────────────────────────────────────────────────────
// 枚举
// ─────────────────────────────────────────────────────────────────────────────

/// 聚合粒度。存储为 INTEGER，线上也序列化为整数（无 0 值）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(into = "i32", try_from = "i32")]
pub enum AggregationLevel {
    Minute = 1,
    Hour = 2,
    Day = 3,
}

impl AggregationLevel {
    /// 按 C# `MetricsController` 的语义解析 `aggregation` 查询参数。
    ///
    /// 关键行为：**任何无法识别的值都退化为 `Minute`，而不是报错**
    /// （`MetricsController.cs:98`、`:179`）。`"raw"` 在 `/aggregations`
    /// 上同样退化为 `Minute`。改成返回 400 属于行为变更。
    pub fn from_query_lossy(raw: &str) -> Self {
        match raw.to_lowercase().as_str() {
            "hour" => AggregationLevel::Hour,
            "day" => AggregationLevel::Day,
            _ => AggregationLevel::Minute,
        }
    }
}

impl From<AggregationLevel> for i32 {
    fn from(level: AggregationLevel) -> i32 {
        level as i32
    }
}

impl TryFrom<i32> for AggregationLevel {
    type Error = CoreError;

    fn try_from(value: i32) -> Result<Self, Self::Error> {
        match value {
            1 => Ok(AggregationLevel::Minute),
            2 => Ok(AggregationLevel::Hour),
            3 => Ok(AggregationLevel::Day),
            other => Err(CoreError::invalid(format!(
                "unknown AggregationLevel: {other}"
            ))),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// `MetricsJson` 内层载荷
// ─────────────────────────────────────────────────────────────────────────────

/// 原始采样中单个指标的值。
///
/// 由 `MetricRepository`（`WhenWritingNull`）写入，因此 `unit` 为 null 时**省略**。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MetricValue {
    pub value: f64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub unit: Option<String>,
}

/// 聚合桶中单个指标的统计量。
///
/// 由 `AggregationWorker` 写入，该处**未**配置 `WhenWritingNull`，
/// 因此 `unit` 即使为空也要写出（C# 侧首次合并时置为 `""`）。
/// 字段顺序对齐 C# 声明序 `Min, Max, Avg, Sum, Count, Unit`。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MetricAggregation {
    pub min: f64,
    pub max: f64,
    pub avg: f64,
    pub sum: f64,
    pub count: i64,
    pub unit: String,
}

impl MetricAggregation {
    /// 以 C# 的哨兵值起始：`Min = double.MaxValue`、`Max = double.MinValue`
    /// （`AggregationWorker.cs:458-466`）。没有样本合并进来时这些哨兵会被
    /// 原样序列化——这是既有行为，刻意保留以保持数据可比。
    pub fn empty(unit: String) -> Self {
        MetricAggregation {
            min: f64::MAX,
            max: f64::MIN,
            avg: 0.0,
            sum: 0.0,
            count: 0,
            unit,
        }
    }

    /// 合并一个原始采样点。
    pub fn merge_value(&mut self, value: f64) {
        if value < self.min {
            self.min = value;
        }
        if value > self.max {
            self.max = value;
        }
        self.sum += value;
        self.count += 1;
    }

    /// 合并一个下层聚合桶（minute→hour、hour→day）。
    /// `sum`/`count` 累加保证 `avg` 始终是跨层的真加权平均。
    pub fn merge_aggregation(&mut self, other: &MetricAggregation) {
        if other.min < self.min {
            self.min = other.min;
        }
        if other.max > self.max {
            self.max = other.max;
        }
        self.sum += other.sum;
        self.count += other.count;
    }

    /// 落库前定稿 `avg`（`AggregationWorker.cs:414`）。
    pub fn finalize(&mut self) {
        self.avg = if self.count > 0 {
            self.sum / self.count as f64
        } else {
            0.0
        };
    }
}

/// 原始 `MetricsJson` 的反序列化目标。
pub type MetricValueMap = BTreeMap<String, MetricValue>;

/// 聚合 `MetricsJson` 的反序列化目标。
pub type MetricAggregationMap = BTreeMap<String, MetricAggregation>;

// ─────────────────────────────────────────────────────────────────────────────
// 持久化实体（同时是 REST 响应体）
// ─────────────────────────────────────────────────────────────────────────────

/// `ProcessMetricRecords` 一行。`/metrics/latest` 与 `/metrics/history?aggregation=raw` 直接返回。
///
/// `metrics_json` 是**双重编码的 JSON 字符串**，不是嵌套对象——前端需要
/// 自行 `JSON.parse`。压平它会破坏兼容性。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProcessMetricRecord {
    pub id: i64,
    pub process_id: i32,
    pub process_name: String,
    pub command_line: Option<String>,
    pub display_name: Option<String>,
    #[serde(with = "crate::time::serde_wire_utc")]
    pub timestamp: DateTime<Utc>,
    pub metrics_json: String,
}

/// 待写入的原始采样行（尚未分配 `Id`）。
#[derive(Debug, Clone, PartialEq)]
pub struct NewProcessMetricRecord {
    pub process_id: i32,
    pub process_name: String,
    pub command_line: Option<String>,
    pub display_name: Option<String>,
    pub timestamp: DateTime<Utc>,
    pub metrics_json: String,
}

/// `AggregatedMetricRecords` 一行。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AggregatedMetricRecord {
    pub id: i64,
    pub process_id: i32,
    pub process_name: String,
    pub aggregation_level: AggregationLevel,
    #[serde(with = "crate::time::serde_wire_utc")]
    pub timestamp: DateTime<Utc>,
    pub metrics_json: String,
}

/// 待写入的聚合行。
#[derive(Debug, Clone, PartialEq)]
pub struct NewAggregatedMetricRecord {
    pub process_id: i32,
    pub process_name: String,
    pub aggregation_level: AggregationLevel,
    pub timestamp: DateTime<Utc>,
    pub metrics_json: String,
}

/// `AlertConfigurations` 一行；同时是 `POST /api/v1/config/alerts` 的请求体。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AlertConfiguration {
    #[serde(default)]
    pub id: i32,
    pub metric_id: String,
    #[serde(default)]
    pub threshold: f64,
    #[serde(default = "default_true")]
    pub is_enabled: bool,
    #[serde(default = "epoch", with = "crate::time::serde_wire_utc")]
    pub created_at: DateTime<Utc>,
    #[serde(default = "epoch", with = "crate::time::serde_wire_utc")]
    pub updated_at: DateTime<Utc>,
}

/// `ApplicationSettings` 一行。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ApplicationSetting {
    pub id: i32,
    pub category: String,
    pub key: String,
    pub value: String,
    #[serde(with = "crate::time::serde_wire_utc")]
    pub created_at: DateTime<Utc>,
    #[serde(with = "crate::time::serde_wire_utc")]
    pub updated_at: DateTime<Utc>,
}

fn default_true() -> bool {
    true
}

/// C# `default(DateTime)` 对应 `0001-01-01T00:00:00`。
/// `POST /config/alerts` 在 update 分支会把客户端请求体原样回显，
/// 未提供 `createdAt` 时正是这个值——保持一致。
fn epoch() -> DateTime<Utc> {
    DateTime::from_timestamp(-62_135_596_800, 0).expect("0001-01-01T00:00:00Z is representable")
}

// ─────────────────────────────────────────────────────────────────────────────
// 查询参数与投影
// ─────────────────────────────────────────────────────────────────────────────

/// `/metrics/latest` 与 `/metrics/processes` 共用的过滤条件。
///
/// `process_name` / `keyword` 在 C# 侧翻译成 SQLite `instr()`，
/// 是**大小写敏感**的子串匹配。用 `LIKE '%x%'` 会变成 ASCII 不敏感，属行为变更。
#[derive(Debug, Clone, Default, PartialEq)]
pub struct MetricFilter {
    pub process_id: Option<i32>,
    pub process_name: Option<String>,
    pub keyword: Option<String>,
    pub from: Option<DateTime<Utc>>,
    pub to: Option<DateTime<Utc>>,
}

/// `/api/v1/metrics/processes` 的匿名投影。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProcessSummary {
    pub process_id: i32,
    pub process_name: String,
    #[serde(with = "crate::time::serde_wire_utc")]
    pub last_seen: DateTime<Utc>,
    pub record_count: i64,
}

/// `PUT /api/v1/config/settings` 批量 upsert 的计数结果。
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct SettingsUpsertCounts {
    pub updated: usize,
    pub inserted: usize,
    /// `DataCollection.ProcessKeywords` 是否被触碰——决定是否热重载扫描关键词。
    pub process_keywords_touched: bool,
}

// ─────────────────────────────────────────────────────────────────────────────
// 功耗
// ─────────────────────────────────────────────────────────────────────────────

/// TDP 档位（瓦）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PowerScheme {
    pub stapm_watts: i32,
    pub fast_watts: i32,
    pub slow_watts: i32,
}

/// `GET /api/v1/power/status` 的 200 响应。
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PowerStatus {
    pub current_watts: f64,
    pub limit_watts: f64,
    /// 可为 null，且**必须写出 null**。
    pub scheme_index: Option<i32>,
    pub limits: PowerScheme,
}

/// `POST /api/v1/power/scheme/next` 的领域结果。
#[derive(Debug, Clone, PartialEq)]
pub struct PowerSchemeSwitchResult {
    pub success: bool,
    pub message: String,
    pub previous_scheme_index: Option<i32>,
    pub new_scheme_index: i32,
    pub new_scheme: Option<PowerScheme>,
}

impl PowerSchemeSwitchResult {
    /// 失败结果；`new_scheme_index` 固定为 `-1`（对齐 C# `Fail`）。
    pub fn fail(message: impl Into<String>) -> Self {
        PowerSchemeSwitchResult {
            success: false,
            message: message.into(),
            previous_scheme_index: None,
            new_scheme_index: -1,
            new_scheme: None,
        }
    }
}

/// `GET /api/v1/power/warmup` 的 200 响应。该端点永不失败。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PowerWarmupStatus {
    pub enabled: bool,
    pub device_name: Option<String>,
    pub reason: Option<String>,
}

// ─────────────────────────────────────────────────────────────────────────────
// 指标元数据
// ─────────────────────────────────────────────────────────────────────────────

/// `GET /api/v1/config/metrics` 的数组元素。
///
/// 注意 `type` / `category` 在这里是**字符串**
/// （`"Percentage" | "Numeric" | "Size" | "Text"`），
/// 与 `aggregationLevel` 的整数枚举相反。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MetricMetadata {
    pub metric_id: String,
    pub display_name: String,
    pub unit: String,
    #[serde(rename = "type")]
    pub metric_type: String,
    pub category: Option<String>,
    pub color: Option<String>,
    pub icon: Option<String>,
}

// ─────────────────────────────────────────────────────────────────────────────
// Widget 配置
// ─────────────────────────────────────────────────────────────────────────────

/// 单个指标的点击行为。
///
/// `parameters` 为 null 时**写出 `null`**，不能省略。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MetricClickConfig {
    #[serde(default)]
    pub enabled: bool,
    #[serde(default = "default_action")]
    pub action: String,
    #[serde(default)]
    pub parameters: Option<BTreeMap<String, String>>,
}

fn default_action() -> String {
    "none".to_string()
}

/// `widget-settings.json` 的内容；同时是 `/api/v1/widgetconfig` 的响应体。
///
/// `metric_click_actions` 的 key 逐字保留（`cpu` / `memory` / `gpu` / `vram` / `power`）。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WidgetSettings {
    #[serde(default)]
    pub enable_metric_click: bool,
    #[serde(default)]
    pub metric_click_actions: BTreeMap<String, MetricClickConfig>,
}

impl Default for WidgetSettings {
    /// 对齐 C# `WidgetConfigController.GetDefaultSettings()`（`:95-116`）。
    fn default() -> Self {
        let plain = || MetricClickConfig {
            enabled: false,
            action: "none".to_string(),
            parameters: None,
        };

        let mut actions = BTreeMap::new();
        actions.insert("cpu".to_string(), plain());
        actions.insert("memory".to_string(), plain());
        actions.insert("gpu".to_string(), plain());
        actions.insert("vram".to_string(), plain());
        actions.insert(
            "power".to_string(),
            MetricClickConfig {
                enabled: false,
                action: "togglePowerMode".to_string(),
                parameters: Some(BTreeMap::from([(
                    "modes".to_string(),
                    "balanced,performance,powersaver".to_string(),
                )])),
            },
        );

        WidgetSettings {
            enable_metric_click: false,
            metric_click_actions: actions,
        }
    }
}

/// `widget-settings.json` 的**磁盘**表示。
///
/// C# 用默认 `JsonSerializerOptions` 读写该文件，因此磁盘上是 **PascalCase**，
/// 而 HTTP 响应经 MVC 是 camelCase。两种命名必须并存，否则读不了既有文件。
pub mod widget_disk {
    use std::collections::BTreeMap;

    use serde::{Deserialize, Serialize};

    #[derive(Debug, Clone, Serialize, Deserialize)]
    #[serde(rename_all = "PascalCase")]
    pub struct MetricClickConfig {
        #[serde(default)]
        pub enabled: bool,
        #[serde(default)]
        pub action: String,
        #[serde(default)]
        pub parameters: Option<BTreeMap<String, String>>,
    }

    #[derive(Debug, Clone, Serialize, Deserialize)]
    #[serde(rename_all = "PascalCase")]
    pub struct WidgetSettings {
        #[serde(default)]
        pub enable_metric_click: bool,
        #[serde(default)]
        pub metric_click_actions: BTreeMap<String, MetricClickConfig>,
    }

    impl From<super::WidgetSettings> for WidgetSettings {
        fn from(value: super::WidgetSettings) -> Self {
            WidgetSettings {
                enable_metric_click: value.enable_metric_click,
                metric_click_actions: value
                    .metric_click_actions
                    .into_iter()
                    .map(|(k, v)| {
                        (
                            k,
                            MetricClickConfig {
                                enabled: v.enabled,
                                action: v.action,
                                parameters: v.parameters,
                            },
                        )
                    })
                    .collect(),
            }
        }
    }

    impl From<WidgetSettings> for super::WidgetSettings {
        fn from(value: WidgetSettings) -> Self {
            super::WidgetSettings {
                enable_metric_click: value.enable_metric_click,
                metric_click_actions: value
                    .metric_click_actions
                    .into_iter()
                    .map(|(k, v)| {
                        (
                            k,
                            super::MetricClickConfig {
                                enabled: v.enabled,
                                action: if v.action.is_empty() {
                                    "none".to_string()
                                } else {
                                    v.action
                                },
                                parameters: v.parameters,
                            },
                        )
                    })
                    .collect(),
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// LHM bridge IPC
// ─────────────────────────────────────────────────────────────────────────────

/// `lhm-bridge` 中单个 LHM Storage hardware 的逐盘快照。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub struct LhmDiskSnapshot {
    pub name: String,
    #[serde(default)]
    pub total_bytes: Option<i64>,
    #[serde(default)]
    pub used_bytes: Option<i64>,
    #[serde(default)]
    pub read_mbps: Option<f64>,
    #[serde(default)]
    pub write_mbps: Option<f64>,
}

/// `lhm-bridge` stdout 每行一个的快照（snake_case，与 C# 侧 `[JsonPropertyName]` 对齐）。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct LhmSnapshot {
    pub ts: DateTime<Utc>,
    #[serde(default)]
    pub cpu_temp: Option<f64>,
    #[serde(default)]
    pub cpu_temp_label: Option<String>,
    #[serde(default)]
    pub gpu_temp: Option<f64>,
    #[serde(default)]
    pub gpu_load: Option<f64>,
    #[serde(default)]
    pub net_up_mbps: f64,
    #[serde(default)]
    pub net_down_mbps: f64,
    #[serde(default)]
    pub disk_read_mbps: f64,
    #[serde(default)]
    pub disk_write_mbps: f64,
    #[serde(default)]
    pub disks: Vec<LhmDiskSnapshot>,
}

/// `lhm-bridge` 启动时写到 stderr 首行的 banner，用于探测提权状态。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct LhmBridgeBanner {
    pub component: String,
    pub is_admin: bool,
    pub interval_ms: i32,
    pub pid: i32,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn aggregation_level_serializes_as_an_integer_not_a_name() {
        // 前端按 1|2|3 判定；序列化成 "Minute" 会静默破坏图表。
        let json = serde_json::to_string(&AggregationLevel::Hour).unwrap();
        assert_eq!(json, "2");
    }

    #[test]
    fn aggregation_level_round_trips_through_its_integer_form() {
        for level in [
            AggregationLevel::Minute,
            AggregationLevel::Hour,
            AggregationLevel::Day,
        ] {
            let json = serde_json::to_string(&level).unwrap();
            assert_eq!(
                serde_json::from_str::<AggregationLevel>(&json).unwrap(),
                level
            );
        }
    }

    #[test]
    fn aggregation_level_rejects_zero_and_out_of_range() {
        // C# 枚举没有 0 值；接受它会写出无法被 C# 读回的行。
        assert!(serde_json::from_str::<AggregationLevel>("0").is_err());
        assert!(serde_json::from_str::<AggregationLevel>("4").is_err());
    }

    #[test]
    fn unknown_aggregation_query_degrades_to_minute() {
        // MetricsController.cs:98 —— 不认识的值不报错，退化为 minute。
        assert_eq!(
            AggregationLevel::from_query_lossy("garbage"),
            AggregationLevel::Minute
        );
        assert_eq!(
            AggregationLevel::from_query_lossy("raw"),
            AggregationLevel::Minute
        );
        assert_eq!(
            AggregationLevel::from_query_lossy("HOUR"),
            AggregationLevel::Hour
        );
        assert_eq!(
            AggregationLevel::from_query_lossy("Day"),
            AggregationLevel::Day
        );
    }

    #[test]
    fn process_metric_record_emits_nulls_rather_than_omitting_them() {
        // DefaultIgnoreCondition = Never：前端读 data.commandLine，缺键与 null 不等价。
        let record = ProcessMetricRecord {
            id: 1,
            process_id: 42,
            process_name: "python".to_string(),
            command_line: None,
            display_name: None,
            timestamp: DateTime::from_timestamp(1_800_000_000, 0).unwrap(),
            metrics_json: "{}".to_string(),
        };
        let json = serde_json::to_value(&record).unwrap();
        assert_eq!(json["commandLine"], serde_json::Value::Null);
        assert_eq!(json["displayName"], serde_json::Value::Null);
        assert!(json.get("commandLine").is_some());
    }

    #[test]
    fn process_metric_record_uses_camel_case_keys() {
        let record = ProcessMetricRecord {
            id: 7,
            process_id: 42,
            process_name: "python".to_string(),
            command_line: Some("python app.py".to_string()),
            display_name: Some("App".to_string()),
            timestamp: DateTime::from_timestamp(1_800_000_000, 0).unwrap(),
            metrics_json: r#"{"cpu":{"value":12.5}}"#.to_string(),
        };
        let json = serde_json::to_value(&record).unwrap();
        for key in [
            "id",
            "processId",
            "processName",
            "commandLine",
            "displayName",
            "timestamp",
            "metricsJson",
        ] {
            assert!(json.get(key).is_some(), "missing key {key}");
        }
        // metricsJson 必须保持字符串（双重编码），不能被压平成对象。
        assert!(json["metricsJson"].is_string());
    }

    #[test]
    fn metric_value_omits_unit_when_absent() {
        // MetricRepository 使用 WhenWritingNull。
        let value = MetricValue {
            value: 12.5,
            unit: None,
        };
        assert_eq!(serde_json::to_string(&value).unwrap(), r#"{"value":12.5}"#);
    }

    #[test]
    fn metric_aggregation_always_writes_unit() {
        // AggregationWorker 未设置 WhenWritingNull。
        let mut agg = MetricAggregation::empty(String::new());
        agg.merge_value(10.0);
        agg.merge_value(20.0);
        agg.finalize();
        let json = serde_json::to_value(&agg).unwrap();
        assert_eq!(json["unit"], "");
        assert_eq!(json["min"], 10.0);
        assert_eq!(json["max"], 20.0);
        assert_eq!(json["avg"], 15.0);
        assert_eq!(json["sum"], 30.0);
        assert_eq!(json["count"], 2);
    }

    #[test]
    fn merging_aggregations_keeps_avg_a_weighted_mean() {
        // minute→hour 必须累加 sum/count，否则 avg 变成"平均的平均"。
        let mut left = MetricAggregation::empty("%".to_string());
        for v in [10.0, 20.0, 30.0] {
            left.merge_value(v);
        }
        let mut right = MetricAggregation::empty("%".to_string());
        right.merge_value(100.0);

        let mut merged = MetricAggregation::empty("%".to_string());
        merged.merge_aggregation(&left);
        merged.merge_aggregation(&right);
        merged.finalize();

        assert_eq!(merged.count, 4);
        assert_eq!(merged.sum, 160.0);
        assert_eq!(merged.avg, 40.0);
        assert_eq!(merged.min, 10.0);
        assert_eq!(merged.max, 100.0);
    }

    #[test]
    fn empty_aggregation_finalizes_avg_to_zero_not_nan() {
        let mut agg = MetricAggregation::empty("%".to_string());
        agg.finalize();
        assert_eq!(agg.avg, 0.0);
    }

    #[test]
    fn power_status_writes_null_scheme_index() {
        let status = PowerStatus {
            current_watts: 42.5,
            limit_watts: 55.0,
            scheme_index: None,
            limits: PowerScheme {
                stapm_watts: 55,
                fast_watts: 100,
                slow_watts: 55,
            },
        };
        let json = serde_json::to_value(status).unwrap();
        assert_eq!(json["schemeIndex"], serde_json::Value::Null);
        assert_eq!(json["limits"]["stapmWatts"], 55);
    }

    #[test]
    fn metric_metadata_renames_type_and_keeps_nulls() {
        let meta = MetricMetadata {
            metric_id: "cpu".to_string(),
            display_name: "CPU Usage".to_string(),
            unit: "%".to_string(),
            metric_type: "Percentage".to_string(),
            category: None,
            color: Some("#3b82f6".to_string()),
            icon: None,
        };
        let json = serde_json::to_value(&meta).unwrap();
        assert_eq!(json["type"], "Percentage");
        assert!(json.get("metricType").is_none());
        assert_eq!(json["category"], serde_json::Value::Null);
        assert_eq!(json["icon"], serde_json::Value::Null);
    }

    #[test]
    fn widget_settings_wire_form_is_camel_case_with_verbatim_map_keys() {
        let settings = WidgetSettings::default();
        let json = serde_json::to_value(&settings).unwrap();
        assert_eq!(json["enableMetricClick"], false);
        // map key 逐字保留，不做 camelCase 转换
        assert!(json["metricClickActions"]["cpu"].is_object());
        assert_eq!(json["metricClickActions"]["cpu"]["action"], "none");
        assert_eq!(
            json["metricClickActions"]["cpu"]["parameters"],
            serde_json::Value::Null
        );
        assert_eq!(
            json["metricClickActions"]["power"]["parameters"]["modes"],
            "balanced,performance,powersaver"
        );
    }

    #[test]
    fn widget_settings_disk_form_is_pascal_case() {
        // 既有 widget-settings.json 是 PascalCase；用 camelCase 写会让 C# 读不回。
        let disk: widget_disk::WidgetSettings = WidgetSettings::default().into();
        let json = serde_json::to_value(&disk).unwrap();
        assert!(json.get("EnableMetricClick").is_some());
        assert!(json.get("enableMetricClick").is_none());
        assert!(json["MetricClickActions"]["power"]["Action"].is_string());
    }

    #[test]
    fn widget_settings_round_trip_through_disk_form_preserves_content() {
        let original = WidgetSettings::default();
        let disk: widget_disk::WidgetSettings = original.clone().into();
        let text = serde_json::to_string(&disk).unwrap();
        let parsed: widget_disk::WidgetSettings = serde_json::from_str(&text).unwrap();
        let restored: WidgetSettings = parsed.into();
        assert_eq!(restored, original);
    }

    #[test]
    fn lhm_snapshot_parses_a_bridge_line_missing_optional_sensors() {
        // 非提权运行时 cpu_temp / cpu_temp_label 整个键都不存在。
        let line = r#"{"ts":"2026-07-26T06:33:34.3146799Z","gpu_temp":52,"gpu_load":43,"net_up_mbps":0.106,"net_down_mbps":0.091,"disk_read_mbps":0,"disk_write_mbps":0}"#;
        let snap: LhmSnapshot = serde_json::from_str(line).unwrap();
        assert_eq!(snap.cpu_temp, None);
        assert_eq!(snap.cpu_temp_label, None);
        assert_eq!(snap.gpu_temp, Some(52.0));
        assert_eq!(snap.net_up_mbps, 0.106);
        assert!(snap.disks.is_empty());
    }

    #[test]
    fn lhm_snapshot_parses_an_elevated_bridge_line() {
        let line = r#"{"ts":"2026-07-26T06:33:34.3146799Z","cpu_temp":61.5,"cpu_temp_label":"Core Max","gpu_temp":52,"gpu_load":43,"net_up_mbps":0,"net_down_mbps":0,"disk_read_mbps":12.5,"disk_write_mbps":3.5,"disks":[{"name":"Samsung SSD 990 PRO 1TB","total_bytes":1099511627776,"used_bytes":687194767360,"read_mbps":12.5,"write_mbps":null},{"name":"WDC WD40EZAX","total_bytes":null,"used_bytes":null,"read_mbps":null,"write_mbps":3.5}]}"#;
        let snap: LhmSnapshot = serde_json::from_str(line).unwrap();
        assert_eq!(snap.cpu_temp, Some(61.5));
        assert_eq!(snap.cpu_temp_label.as_deref(), Some("Core Max"));
        assert_eq!(snap.disk_read_mbps, 12.5);
        assert_eq!(snap.disk_write_mbps, 3.5);
        assert_eq!(snap.disks.len(), 2);

        let first = &snap.disks[0];
        assert_eq!(first.name, "Samsung SSD 990 PRO 1TB");
        assert_eq!(first.total_bytes, Some(1_099_511_627_776));
        assert_eq!(first.used_bytes, Some(687_194_767_360));
        assert_eq!(first.read_mbps, Some(12.5));
        assert_eq!(first.write_mbps, None);

        let second = &snap.disks[1];
        assert_eq!(second.name, "WDC WD40EZAX");
        assert_eq!(second.total_bytes, None);
        assert_eq!(second.used_bytes, None);
        assert_eq!(second.read_mbps, None);
        assert_eq!(second.write_mbps, Some(3.5));
    }

    #[test]
    fn lhm_banner_parses() {
        let line = r#"{"component":"lhm-bridge","is_admin":false,"interval_ms":1000,"pid":1234}"#;
        let banner: LhmBridgeBanner = serde_json::from_str(line).unwrap();
        assert_eq!(banner.component, "lhm-bridge");
        assert!(!banner.is_admin);
    }

    #[test]
    fn alert_configuration_defaults_match_the_csharp_request_binding() {
        // 客户端只发 metricId 时，C# required 之外的成员取默认值。
        let alert: AlertConfiguration = serde_json::from_str(r#"{"metricId":"cpu"}"#).unwrap();
        assert_eq!(alert.id, 0);
        assert_eq!(alert.metric_id, "cpu");
        assert!(alert.is_enabled);
        // default(DateTime) == 0001-01-01T00:00:00
        assert_eq!(
            crate::time::to_wire_utc(&alert.created_at),
            "0001-01-01T00:00:00Z"
        );
    }
}
