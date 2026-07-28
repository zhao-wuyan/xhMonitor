//! .NET 兼容的时间序列化。
//!
//! 迁移中存在三种**互不相同**的时间表示，混用会静默破坏兼容性：
//!
//! | 场景 | 格式 | 来源 |
//! |------|------|------|
//! | SQLite `TEXT` 列 | `2026-07-26 12:34:56.7891234` | EF Core `SqliteDateTimeTypeMapping` |
//! | REST 响应 | `2026-07-26T12:34:56.7891234Z` | STJ + `MonitorDbContext` 全局 UTC ValueConverter |
//! | SignalR 推送 | `2026-07-26T12:34:56.7891234+08:00` | `DateTime.Now`（Local）经 STJ |
//!
//! 三者共用 .NET 的小数秒规则：**最多 7 位（tick = 100ns），去掉尾随零；
//! 全零时连小数点一并省略**。这一点对 SQLite 尤其关键——`Timestamp` 是
//! `TEXT` 列，范围查询是字典序比较，多写或少写一位都会让历史库查错数据。

use chrono::{DateTime, Local, NaiveDateTime, TimeZone, Utc};

use crate::error::CoreError;

/// .NET tick = 100 纳秒。
const NANOS_PER_TICK: u32 = 100;

/// 生成 .NET 风格的小数秒后缀（含前导 `.`）；无小数时返回空串。
fn fraction_suffix(subsec_nanos: u32) -> String {
    // 闰秒时 chrono 会返回 >= 1e9，钳制到有效范围以免越界。
    let nanos = subsec_nanos.min(999_999_999);
    let ticks = nanos / NANOS_PER_TICK;
    if ticks == 0 {
        return String::new();
    }
    let mut buf = format!(".{ticks:07}");
    while buf.ends_with('0') {
        buf.pop();
    }
    buf
}

/// 序列化为 EF Core 写入 SQLite `TEXT` 列的格式。
pub fn to_sqlite_text(dt: &DateTime<Utc>) -> String {
    let mut out = dt.format("%Y-%m-%d %H:%M:%S").to_string();
    out.push_str(&fraction_suffix(dt.timestamp_subsec_nanos()));
    out
}

/// 序列化为 REST 响应使用的 UTC ISO-8601（`Z` 后缀）。
pub fn to_wire_utc(dt: &DateTime<Utc>) -> String {
    let mut out = dt.format("%Y-%m-%dT%H:%M:%S").to_string();
    out.push_str(&fraction_suffix(dt.timestamp_subsec_nanos()));
    out.push('Z');
    out
}

/// 序列化为 SignalR 推送使用的本地时间（带 UTC 偏移，非 `Z`）。
pub fn to_wire_local(dt: &DateTime<Local>) -> String {
    let mut out = dt.format("%Y-%m-%dT%H:%M:%S").to_string();
    out.push_str(&fraction_suffix(dt.timestamp_subsec_nanos()));
    out.push_str(&dt.format("%:z").to_string());
    out
}

/// 解析 SQLite `TEXT` 时间戳。
///
/// 主格式是 EF Core 的 `yyyy-MM-dd HH:mm:ss[.fffffff]`（无时区，语义为 UTC）。
/// 同时容忍 `T` 分隔符与带 `Z`/偏移的 RFC-3339，因为历史库可能被其他工具写过。
pub fn from_sqlite_text(raw: &str) -> Result<DateTime<Utc>, CoreError> {
    let text = raw.trim();
    if text.is_empty() {
        return Err(CoreError::invalid("empty timestamp"));
    }

    // 带时区偏移或 Z 的情况优先按 RFC-3339 解析，避免把偏移当成小数秒。
    if let Ok(parsed) = DateTime::parse_from_rfc3339(text) {
        return Ok(parsed.with_timezone(&Utc));
    }

    for format in ["%Y-%m-%d %H:%M:%S%.f", "%Y-%m-%dT%H:%M:%S%.f"] {
        if let Ok(naive) = NaiveDateTime::parse_from_str(text, format) {
            return Ok(Utc.from_utc_datetime(&naive));
        }
    }

    Err(CoreError::invalid(format!(
        "unrecognized timestamp format: {text}"
    )))
}

/// `#[serde(with = "...")]` 适配器：REST 实体上的 UTC 时间戳。
pub mod serde_wire_utc {
    use chrono::{DateTime, Utc};
    use serde::{Deserialize, Deserializer, Serializer};

    pub fn serialize<S: Serializer>(dt: &DateTime<Utc>, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(&super::to_wire_utc(dt))
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<DateTime<Utc>, D::Error> {
        let raw = String::deserialize(d)?;
        super::from_sqlite_text(&raw).map_err(serde::de::Error::custom)
    }
}

/// `#[serde(with = "...")]` 适配器：SignalR 推送上的本地时间戳。
pub mod serde_wire_local {
    use chrono::{DateTime, Local};
    use serde::{Deserialize, Deserializer, Serializer};

    pub fn serialize<S: Serializer>(dt: &DateTime<Local>, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(&super::to_wire_local(dt))
    }

    /// 仅测试与 Desktop 侧消费需要；服务端只序列化。
    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<DateTime<Local>, D::Error> {
        let raw = String::deserialize(d)?;
        super::from_sqlite_text(&raw)
            .map(|utc| utc.with_timezone(&Local))
            .map_err(serde::de::Error::custom)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::TimeZone;

    fn utc(y: i32, mo: u32, d: u32, h: u32, mi: u32, s: u32, nanos: u32) -> DateTime<Utc> {
        Utc.with_ymd_and_hms(y, mo, d, h, mi, s)
            .unwrap()
            .with_nanosecond(nanos)
            .unwrap()
    }

    use chrono::Timelike;

    #[test]
    fn sqlite_text_omits_the_dot_when_there_is_no_fraction() {
        let dt = utc(2026, 7, 26, 12, 34, 56, 0);
        assert_eq!(to_sqlite_text(&dt), "2026-07-26 12:34:56");
    }

    #[test]
    fn sqlite_text_trims_trailing_zeros_to_dotnet_shape() {
        // 500ms -> 5_000_000 ticks -> ".5000000" -> ".5"
        let dt = utc(2026, 7, 26, 12, 34, 56, 500_000_000);
        assert_eq!(to_sqlite_text(&dt), "2026-07-26 12:34:56.5");
    }

    #[test]
    fn sqlite_text_keeps_full_tick_precision() {
        // 789_123_400ns = 7_891_234 ticks -> ".7891234"
        let dt = utc(2026, 7, 26, 12, 34, 56, 789_123_400);
        assert_eq!(to_sqlite_text(&dt), "2026-07-26 12:34:56.7891234");
    }

    #[test]
    fn sqlite_text_truncates_below_tick_resolution() {
        // .NET 只有 100ns 分辨率；99ns 应被丢弃而不是四舍五入进位。
        let dt = utc(2026, 7, 26, 12, 34, 56, 99);
        assert_eq!(to_sqlite_text(&dt), "2026-07-26 12:34:56");
    }

    #[test]
    fn sqlite_text_ordering_matches_chronological_ordering() {
        // TEXT 列上的范围查询是字典序；这是该格式唯一真正的正确性要求。
        let earlier = to_sqlite_text(&utc(2026, 7, 26, 12, 34, 56, 100_000_000));
        let later = to_sqlite_text(&utc(2026, 7, 26, 12, 34, 56, 900_000_000));
        let next_second = to_sqlite_text(&utc(2026, 7, 26, 12, 34, 57, 0));
        assert!(earlier < later, "{earlier} !< {later}");
        assert!(later < next_second, "{later} !< {next_second}");
    }

    #[test]
    fn wire_utc_appends_z() {
        let dt = utc(2026, 7, 26, 12, 34, 56, 789_123_400);
        assert_eq!(to_wire_utc(&dt), "2026-07-26T12:34:56.7891234Z");
    }

    #[test]
    fn round_trips_through_sqlite_text() {
        let dt = utc(2026, 7, 26, 12, 34, 56, 789_123_400);
        let parsed = from_sqlite_text(&to_sqlite_text(&dt)).unwrap();
        assert_eq!(parsed, dt);
    }

    #[test]
    fn parses_rfc3339_written_by_other_tools() {
        let parsed = from_sqlite_text("2026-07-26T12:34:56.5Z").unwrap();
        assert_eq!(parsed, utc(2026, 7, 26, 12, 34, 56, 500_000_000));
    }

    #[test]
    fn parses_offset_timestamps_into_utc() {
        let parsed = from_sqlite_text("2026-07-26T20:34:56+08:00").unwrap();
        assert_eq!(parsed, utc(2026, 7, 26, 12, 34, 56, 0));
    }

    #[test]
    fn rejects_garbage() {
        assert!(from_sqlite_text("not a timestamp").is_err());
        assert!(from_sqlite_text("   ").is_err());
    }

    #[test]
    fn wire_local_carries_an_offset_not_a_z() {
        let dt = Local.timestamp_opt(1_800_000_000, 0).unwrap();
        let text = to_wire_local(&dt);
        assert!(!text.ends_with('Z'), "{text} must not use Z");
        assert!(
            text.contains('+') || text.matches('-').count() > 2,
            "{text} must carry a UTC offset"
        );
    }
}
