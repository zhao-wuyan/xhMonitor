use std::{
    fmt,
    path::Path,
    sync::{Mutex, MutexGuard},
};

use chrono::{DateTime, Utc};
use rusqlite::{params, types::Type, Connection, Params, Row};
use xhm_core::{
    models::{
        AggregatedMetricRecord, AggregationLevel, AlertConfiguration, ApplicationSetting,
        MetricFilter, NewAggregatedMetricRecord, NewProcessMetricRecord, ProcessMetricRecord,
        ProcessSummary, SettingsUpsertCounts,
    },
    time::{from_sqlite_text, to_sqlite_text},
    traits::MetricStore,
    CoreError, Result,
};

const SCHEMA_SQL: &str = r#"
CREATE TABLE IF NOT EXISTS "AggregatedMetricRecords" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_AggregatedMetricRecords" PRIMARY KEY AUTOINCREMENT,
    "ProcessId" INTEGER NOT NULL,
    "ProcessName" TEXT NOT NULL,
    "AggregationLevel" INTEGER NOT NULL,
    "Timestamp" TEXT NOT NULL,
    "MetricsJson" TEXT NOT NULL,
    CONSTRAINT "CK_AggregatedMetricRecords_MetricsJson_Valid" CHECK (json_valid(MetricsJson))
);

CREATE TABLE IF NOT EXISTS "AlertConfigurations" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_AlertConfigurations" PRIMARY KEY AUTOINCREMENT,
    "MetricId" TEXT NOT NULL,
    "Threshold" REAL NOT NULL,
    "IsEnabled" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS "ProcessMetricRecords" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ProcessMetricRecords" PRIMARY KEY AUTOINCREMENT,
    "ProcessId" INTEGER NOT NULL,
    "ProcessName" TEXT NOT NULL,
    "CommandLine" TEXT NULL,
    "DisplayName" TEXT NULL,
    "Timestamp" TEXT NOT NULL,
    "MetricsJson" TEXT NOT NULL,
    CONSTRAINT "CK_ProcessMetricRecords_MetricsJson_Valid" CHECK (json_valid(MetricsJson))
);

CREATE TABLE IF NOT EXISTS "ApplicationSettings" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ApplicationSettings" PRIMARY KEY AUTOINCREMENT,
    "Category" TEXT NOT NULL,
    "Key" TEXT NOT NULL,
    "Value" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_AggregatedMetricRecords_AggregationLevel_Timestamp"
    ON "AggregatedMetricRecords" ("AggregationLevel", "Timestamp");
CREATE INDEX IF NOT EXISTS "IX_AggregatedMetricRecords_ProcessId_AggregationLevel_Timestamp"
    ON "AggregatedMetricRecords" ("ProcessId", "AggregationLevel", "Timestamp");
CREATE INDEX IF NOT EXISTS "IX_AggregatedMetricRecords_ProcessId_Timestamp"
    ON "AggregatedMetricRecords" ("ProcessId", "Timestamp");
CREATE INDEX IF NOT EXISTS "IX_ProcessMetricRecords_ProcessId_Timestamp"
    ON "ProcessMetricRecords" ("ProcessId", "Timestamp");
CREATE INDEX IF NOT EXISTS "IX_ProcessMetricRecords_Timestamp"
    ON "ProcessMetricRecords" ("Timestamp");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ApplicationSettings_Category_Key"
    ON "ApplicationSettings" ("Category", "Key");
"#;

const INITIAL_ALERT_SEED_SQL: &str = r#"
INSERT OR IGNORE INTO "AlertConfigurations"
    ("Id", "MetricId", "Threshold", "IsEnabled", "CreatedAt", "UpdatedAt")
VALUES
    (1, 'cpu', 90.0, 1, '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (2, 'memory', 90.0, 1, '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (3, 'gpu', 90.0, 1, '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (4, 'vram', 90.0, 1, '2024-01-01 00:00:00', '2024-01-01 00:00:00');
"#;

const APPLICATION_SETTINGS_SEED_SQL: &str = r#"
INSERT OR IGNORE INTO "ApplicationSettings"
    ("Id", "Category", "Key", "Value", "CreatedAt", "UpdatedAt")
VALUES
    (1, 'Appearance', 'ThemeColor', '"Dark"', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (2, 'Appearance', 'Opacity', '90', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (3, 'DataCollection', 'ProcessKeywords', '["python","llama-server"]', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (4, 'DataCollection', 'SystemInterval', '1000', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (5, 'DataCollection', 'ProcessInterval', '5000', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (6, 'DataCollection', 'TopProcessCount', '10', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (7, 'DataCollection', 'DataRetentionDays', '30', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (8, 'System', 'StartWithWindows', 'false', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (9, 'System', 'SignalRPort', '35179', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (10, 'System', 'WebPort', '35180', '2024-01-01 00:00:00', '2024-01-01 00:00:00');
"#;

const REMOVE_PORTS_SQL: &str = "DELETE FROM \"ApplicationSettings\" WHERE \"Id\" IN (9, 10)";
const REMOVE_INTERVALS_SQL: &str = "DELETE FROM \"ApplicationSettings\" WHERE \"Id\" IN (4, 5)";

const MONITORING_SETTINGS_SEED_SQL: &str = r#"
INSERT OR IGNORE INTO "ApplicationSettings"
    ("Id", "Category", "Key", "Value", "CreatedAt", "UpdatedAt")
VALUES
    (9, 'Monitoring', 'MonitorCpu', 'true', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (10, 'Monitoring', 'MonitorMemory', 'true', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (11, 'Monitoring', 'MonitorGpu', 'true', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (12, 'Monitoring', 'MonitorVram', 'true', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (13, 'Monitoring', 'MonitorPower', 'true', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (14, 'Monitoring', 'MonitorNetwork', 'true', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    (15, 'Monitoring', 'AdminMode', 'false', '2024-01-01 00:00:00', '2024-01-01 00:00:00');
"#;
const SECURITY_SETTINGS_SEED_SQL: &str = r#"
INSERT OR IGNORE INTO "ApplicationSettings"
    ("Category", "Key", "Value", "CreatedAt", "UpdatedAt")
VALUES
    ('System', 'EnableLanAccess', 'false', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    ('System', 'EnableAccessKey', 'false', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    ('System', 'AccessKey', '', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
    ('System', 'IpWhitelist', '', '2024-01-01 00:00:00', '2024-01-01 00:00:00');
"#;

/// SQLite-backed implementation of the synchronous persistence boundary.
pub struct SqliteMetricStore {
    connection: Mutex<Connection>,
}

impl fmt::Debug for SqliteMetricStore {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("SqliteMetricStore")
            .field("connection", &"<rusqlite::Connection>")
            .finish()
    }
}

impl SqliteMetricStore {
    pub fn open(path: impl AsRef<Path>) -> Result<Self> {
        let path = path.as_ref();
        let connection = Connection::open(path).map_err(|error| {
            CoreError::storage(format!(
                "failed to open SQLite database {}: {error}",
                path.display()
            ))
        })?;
        Self::from_connection(connection)
    }

    pub fn open_in_memory() -> Result<Self> {
        let connection = Connection::open_in_memory().map_err(|error| {
            CoreError::storage(format!("failed to open in-memory SQLite: {error}"))
        })?;
        Self::from_connection(connection)
    }

    fn from_connection(mut connection: Connection) -> Result<Self> {
        initialize_schema(&mut connection)?;
        Ok(Self {
            connection: Mutex::new(connection),
        })
    }

    fn connection(&self) -> Result<MutexGuard<'_, Connection>> {
        self.connection
            .lock()
            .map_err(|_| CoreError::storage("SQLite connection mutex poisoned"))
    }
}

fn initialize_schema(connection: &mut Connection) -> Result<()> {
    connection
        .pragma_update(None, "journal_mode", "WAL")
        .map_err(|error| sql_error("enabling SQLite WAL mode", error))?;

    let transaction = connection
        .transaction()
        .map_err(|error| sql_error("starting schema transaction", error))?;
    transaction
        .execute_batch(SCHEMA_SQL)
        .map_err(|error| sql_error("initializing SQLite schema", error))?;

    let has_display_name = transaction
        .query_row(
            "SELECT EXISTS(
                 SELECT 1
                 FROM pragma_table_info('ProcessMetricRecords')
                 WHERE name = 'DisplayName'
             )",
            [],
            |row| row.get::<_, bool>(0),
        )
        .map_err(|error| sql_error("inspecting ProcessMetricRecords schema", error))?;

    if !has_display_name {
        transaction
            .execute(
                "ALTER TABLE \"ProcessMetricRecords\" ADD COLUMN \"DisplayName\" TEXT NULL",
                [],
            )
            .map_err(|error| sql_error("adding ProcessMetricRecords.DisplayName", error))?;
    }

    apply_migration(
        &transaction,
        "20251221075010_InitialCreate",
        "8.0.22",
        INITIAL_ALERT_SEED_SQL,
    )?;
    apply_migration(
        &transaction,
        "20251229152043_AddApplicationSettings",
        "8.0.22",
        APPLICATION_SETTINGS_SEED_SQL,
    )?;
    apply_migration(
        &transaction,
        "20260114000000_AddDisplayNameToProcessMetricRecord",
        "8.0.23",
        "",
    )?;
    apply_migration(
        &transaction,
        "20260120084302_UpdateSeedDataToUseDefaults",
        "8.0.23",
        "",
    )?;
    apply_migration(
        &transaction,
        "20260120084843_RemovePortsFromDatabase",
        "8.0.23",
        REMOVE_PORTS_SQL,
    )?;
    apply_migration(
        &transaction,
        "20260120085153_RemoveIntervalsFromDatabase",
        "8.0.23",
        REMOVE_INTERVALS_SQL,
    )?;
    apply_migration(
        &transaction,
        "20260126161829_AddMonitoringSettings",
        "8.0.23",
        MONITORING_SETTINGS_SEED_SQL,
    )?;
    apply_migration(
        &transaction,
        "20260804000000_AddSecuritySettings",
        "8.0.23",
        SECURITY_SETTINGS_SEED_SQL,
    )?;
    transaction
        .commit()
        .map_err(|error| sql_error("committing schema transaction", error))
}

fn apply_migration(
    transaction: &rusqlite::Transaction<'_>,
    migration_id: &str,
    product_version: &str,
    sql: &str,
) -> Result<()> {
    let applied = transaction
        .query_row(
            "SELECT EXISTS(
                 SELECT 1 FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = ?1
             )",
            [migration_id],
            |row| row.get::<_, bool>(0),
        )
        .map_err(|error| {
            CoreError::storage(format!("checking migration {migration_id}: {error}"))
        })?;
    if applied {
        return Ok(());
    }

    if !sql.is_empty() {
        transaction.execute_batch(sql).map_err(|error| {
            CoreError::storage(format!("applying migration {migration_id}: {error}"))
        })?;
    }
    transaction
        .execute(
            "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\")
             VALUES (?1, ?2)",
            params![migration_id, product_version],
        )
        .map_err(|error| {
            CoreError::storage(format!("recording migration {migration_id}: {error}"))
        })?;
    Ok(())
}

fn sql_error(context: &str, error: rusqlite::Error) -> CoreError {
    CoreError::storage(format!("{context}: {error}"))
}

fn active_filter(value: Option<&str>) -> Option<&str> {
    match value {
        Some(value) if !value.trim().is_empty() => Some(value),
        _ => None,
    }
}

fn timestamp_column(row: &Row<'_>, index: usize) -> rusqlite::Result<DateTime<Utc>> {
    let raw: String = row.get(index)?;
    from_sqlite_text(&raw).map_err(|error| {
        rusqlite::Error::FromSqlConversionFailure(index, Type::Text, Box::new(error))
    })
}

fn aggregation_level_column(row: &Row<'_>, index: usize) -> rusqlite::Result<AggregationLevel> {
    let raw: i32 = row.get(index)?;
    AggregationLevel::try_from(raw).map_err(|error| {
        rusqlite::Error::FromSqlConversionFailure(index, Type::Integer, Box::new(error))
    })
}

fn process_record_from_row(row: &Row<'_>) -> rusqlite::Result<ProcessMetricRecord> {
    Ok(ProcessMetricRecord {
        id: row.get(0)?,
        process_id: row.get(1)?,
        process_name: row.get(2)?,
        command_line: row.get(3)?,
        display_name: row.get(4)?,
        timestamp: timestamp_column(row, 5)?,
        metrics_json: row.get(6)?,
    })
}

fn aggregate_record_from_row(row: &Row<'_>) -> rusqlite::Result<AggregatedMetricRecord> {
    Ok(AggregatedMetricRecord {
        id: row.get(0)?,
        process_id: row.get(1)?,
        process_name: row.get(2)?,
        aggregation_level: aggregation_level_column(row, 3)?,
        timestamp: timestamp_column(row, 4)?,
        metrics_json: row.get(5)?,
    })
}

fn query_process_records<P: Params>(
    connection: &Connection,
    sql: &str,
    parameters: P,
) -> Result<Vec<ProcessMetricRecord>> {
    let mut statement = connection
        .prepare(sql)
        .map_err(|error| sql_error("preparing process metric query", error))?;
    let rows = statement
        .query_map(parameters, process_record_from_row)
        .map_err(|error| sql_error("querying process metrics", error))?;
    let mut records = Vec::new();
    for row in rows {
        records.push(row.map_err(|error| sql_error("reading process metric row", error))?);
    }
    Ok(records)
}

fn query_aggregate_records<P: Params>(
    connection: &Connection,
    sql: &str,
    parameters: P,
) -> Result<Vec<AggregatedMetricRecord>> {
    let mut statement = connection
        .prepare(sql)
        .map_err(|error| sql_error("preparing aggregate metric query", error))?;
    let rows = statement
        .query_map(parameters, aggregate_record_from_row)
        .map_err(|error| sql_error("querying aggregate metrics", error))?;
    let mut records = Vec::new();
    for row in rows {
        records.push(row.map_err(|error| sql_error("reading aggregate metric row", error))?);
    }
    Ok(records)
}

fn optional_timestamp(raw: Option<String>, context: &str) -> Result<Option<DateTime<Utc>>> {
    raw.map(|value| {
        from_sqlite_text(&value).map_err(|error| CoreError::storage(format!("{context}: {error}")))
    })
    .transpose()
}

impl MetricStore for SqliteMetricStore {
    fn save_process_metrics(&self, records: &[NewProcessMetricRecord]) -> Result<usize> {
        if records.is_empty() {
            return Ok(0);
        }

        let mut connection = self.connection()?;
        let transaction = connection
            .transaction()
            .map_err(|error| sql_error("starting process metric transaction", error))?;
        {
            let mut statement = transaction
                .prepare(
                    "INSERT INTO \"ProcessMetricRecords\"
                         (\"ProcessId\", \"ProcessName\", \"CommandLine\", \"DisplayName\", \"Timestamp\", \"MetricsJson\")
                     VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
                )
                .map_err(|error| sql_error("preparing process metric insert", error))?;

            for record in records {
                let timestamp = to_sqlite_text(&record.timestamp);
                statement
                    .execute(params![
                        record.process_id,
                        record.process_name,
                        record.command_line,
                        record.display_name,
                        timestamp,
                        record.metrics_json,
                    ])
                    .map_err(|error| sql_error("inserting process metric", error))?;
            }
        }
        transaction
            .commit()
            .map_err(|error| sql_error("committing process metrics", error))?;
        Ok(records.len())
    }

    fn latest_process_metrics(&self, filter: &MetricFilter) -> Result<Vec<ProcessMetricRecord>> {
        let connection = self.connection()?;
        let process_name = active_filter(filter.process_name.as_deref());
        let keyword = active_filter(filter.keyword.as_deref());
        let latest_timestamp = connection
            .query_row(
                "SELECT MAX(\"Timestamp\")
                 FROM \"ProcessMetricRecords\"
                 WHERE (?1 IS NULL OR \"ProcessId\" = ?1)
                   AND (?2 IS NULL OR instr(\"ProcessName\", ?2) > 0)
                   AND (
                       ?3 IS NULL
                       OR instr(\"ProcessName\", ?3) > 0
                       OR (\"CommandLine\" IS NOT NULL AND instr(\"CommandLine\", ?3) > 0)
                   )",
                params![filter.process_id, process_name, keyword],
                |row| row.get::<_, Option<String>>(0),
            )
            .map_err(|error| sql_error("selecting latest process metric timestamp", error))?;

        let Some(latest_timestamp) = latest_timestamp else {
            return Ok(Vec::new());
        };

        query_process_records(
            &connection,
            "SELECT \"Id\", \"ProcessId\", \"ProcessName\", \"CommandLine\", \"DisplayName\",
                    \"Timestamp\", \"MetricsJson\"
             FROM \"ProcessMetricRecords\"
             WHERE (?1 IS NULL OR \"ProcessId\" = ?1)
               AND (?2 IS NULL OR instr(\"ProcessName\", ?2) > 0)
               AND (
                   ?3 IS NULL
                   OR instr(\"ProcessName\", ?3) > 0
                   OR (\"CommandLine\" IS NOT NULL AND instr(\"CommandLine\", ?3) > 0)
               )
               AND \"Timestamp\" = ?4
             ORDER BY \"ProcessName\"",
            params![filter.process_id, process_name, keyword, latest_timestamp],
        )
    }

    fn history_raw(
        &self,
        process_id: i32,
        from: Option<DateTime<Utc>>,
        to: Option<DateTime<Utc>>,
    ) -> Result<Vec<ProcessMetricRecord>> {
        let connection = self.connection()?;
        let from = from.as_ref().map(to_sqlite_text);
        let to = to.as_ref().map(to_sqlite_text);
        query_process_records(
            &connection,
            "SELECT \"Id\", \"ProcessId\", \"ProcessName\", \"CommandLine\", \"DisplayName\",
                    \"Timestamp\", \"MetricsJson\"
             FROM \"ProcessMetricRecords\"
             WHERE \"ProcessId\" = ?1
               AND (?2 IS NULL OR \"Timestamp\" >= ?2)
               AND (?3 IS NULL OR \"Timestamp\" <= ?3)
             ORDER BY \"Timestamp\"",
            params![process_id, from, to],
        )
    }

    fn process_summaries(&self, filter: &MetricFilter) -> Result<Vec<ProcessSummary>> {
        let connection = self.connection()?;
        let keyword = active_filter(filter.keyword.as_deref());
        let from = filter.from.as_ref().map(to_sqlite_text);
        let to = filter.to.as_ref().map(to_sqlite_text);
        let mut statement = connection
            .prepare(
                "SELECT \"ProcessId\", \"ProcessName\", MAX(\"Timestamp\"), COUNT(*)
                 FROM \"ProcessMetricRecords\"
                 WHERE (?1 IS NULL OR \"Timestamp\" >= ?1)
                   AND (?2 IS NULL OR \"Timestamp\" <= ?2)
                   AND (
                       ?3 IS NULL
                       OR instr(\"ProcessName\", ?3) > 0
                       OR (\"CommandLine\" IS NOT NULL AND instr(\"CommandLine\", ?3) > 0)
                   )
                 GROUP BY \"ProcessId\", \"ProcessName\"
                 ORDER BY MAX(\"Timestamp\") DESC",
            )
            .map_err(|error| sql_error("preparing process summary query", error))?;
        let rows = statement
            .query_map(params![from, to, keyword], |row| {
                Ok(ProcessSummary {
                    process_id: row.get(0)?,
                    process_name: row.get(1)?,
                    last_seen: timestamp_column(row, 2)?,
                    record_count: row.get(3)?,
                })
            })
            .map_err(|error| sql_error("querying process summaries", error))?;
        let mut summaries = Vec::new();
        for row in rows {
            summaries.push(row.map_err(|error| sql_error("reading process summary row", error))?);
        }
        Ok(summaries)
    }

    fn history_aggregated(
        &self,
        process_id: i32,
        level: AggregationLevel,
        from: Option<DateTime<Utc>>,
        to: Option<DateTime<Utc>>,
    ) -> Result<Vec<AggregatedMetricRecord>> {
        let connection = self.connection()?;
        let from = from.as_ref().map(to_sqlite_text);
        let to = to.as_ref().map(to_sqlite_text);
        query_aggregate_records(
            &connection,
            "SELECT \"Id\", \"ProcessId\", \"ProcessName\", \"AggregationLevel\", \"Timestamp\",
                    \"MetricsJson\"
             FROM \"AggregatedMetricRecords\"
             WHERE \"ProcessId\" = ?1
               AND \"AggregationLevel\" = ?2
               AND (?3 IS NULL OR \"Timestamp\" >= ?3)
               AND (?4 IS NULL OR \"Timestamp\" <= ?4)
             ORDER BY \"Timestamp\"",
            params![process_id, i32::from(level), from, to],
        )
    }

    fn aggregations(
        &self,
        level: AggregationLevel,
        from: DateTime<Utc>,
        to: DateTime<Utc>,
    ) -> Result<Vec<AggregatedMetricRecord>> {
        let connection = self.connection()?;
        let from = to_sqlite_text(&from);
        let to = to_sqlite_text(&to);
        query_aggregate_records(
            &connection,
            "SELECT \"Id\", \"ProcessId\", \"ProcessName\", \"AggregationLevel\", \"Timestamp\",
                    \"MetricsJson\"
             FROM \"AggregatedMetricRecords\"
             WHERE \"AggregationLevel\" = ?1
               AND \"Timestamp\" >= ?2
               AND \"Timestamp\" <= ?3
             ORDER BY \"Timestamp\", \"ProcessName\"",
            params![i32::from(level), from, to],
        )
    }

    fn save_aggregates(&self, records: &[NewAggregatedMetricRecord]) -> Result<usize> {
        if records.is_empty() {
            return Ok(0);
        }

        let mut connection = self.connection()?;
        let transaction = connection
            .transaction()
            .map_err(|error| sql_error("starting aggregate metric transaction", error))?;
        {
            let mut statement = transaction
                .prepare(
                    "INSERT INTO \"AggregatedMetricRecords\"
                         (\"ProcessId\", \"ProcessName\", \"AggregationLevel\", \"Timestamp\", \"MetricsJson\")
                     VALUES (?1, ?2, ?3, ?4, ?5)",
                )
                .map_err(|error| sql_error("preparing aggregate metric insert", error))?;

            for record in records {
                let timestamp = to_sqlite_text(&record.timestamp);
                statement
                    .execute(params![
                        record.process_id,
                        record.process_name,
                        i32::from(record.aggregation_level),
                        timestamp,
                        record.metrics_json,
                    ])
                    .map_err(|error| sql_error("inserting aggregate metric", error))?;
            }
        }
        transaction
            .commit()
            .map_err(|error| sql_error("committing aggregate metrics", error))?;
        Ok(records.len())
    }

    fn aggregate_watermark(&self, level: AggregationLevel) -> Result<Option<DateTime<Utc>>> {
        let connection = self.connection()?;
        let raw = connection
            .query_row(
                "SELECT MAX(\"Timestamp\")
                 FROM \"AggregatedMetricRecords\"
                 WHERE \"AggregationLevel\" = ?1",
                [i32::from(level)],
                |row| row.get::<_, Option<String>>(0),
            )
            .map_err(|error| sql_error("selecting aggregate watermark", error))?;
        optional_timestamp(raw, "invalid aggregate watermark")
    }

    fn earliest_raw_timestamp(&self) -> Result<Option<DateTime<Utc>>> {
        let connection = self.connection()?;
        let raw = connection
            .query_row(
                "SELECT MIN(\"Timestamp\") FROM \"ProcessMetricRecords\"",
                [],
                |row| row.get::<_, Option<String>>(0),
            )
            .map_err(|error| sql_error("selecting earliest raw timestamp", error))?;
        optional_timestamp(raw, "invalid earliest raw timestamp")
    }

    fn earliest_aggregate_timestamp(
        &self,
        level: AggregationLevel,
    ) -> Result<Option<DateTime<Utc>>> {
        let connection = self.connection()?;
        let raw = connection
            .query_row(
                "SELECT MIN(\"Timestamp\")
                 FROM \"AggregatedMetricRecords\"
                 WHERE \"AggregationLevel\" = ?1",
                [i32::from(level)],
                |row| row.get::<_, Option<String>>(0),
            )
            .map_err(|error| sql_error("selecting earliest aggregate timestamp", error))?;
        optional_timestamp(raw, "invalid earliest aggregate timestamp")
    }

    fn raw_batch_for_aggregation(
        &self,
        from: DateTime<Utc>,
        to: DateTime<Utc>,
        after_id: i64,
        limit: usize,
    ) -> Result<Vec<ProcessMetricRecord>> {
        let limit = i64::try_from(limit)
            .map_err(|_| CoreError::invalid("aggregation batch limit exceeds i64"))?;
        let connection = self.connection()?;
        query_process_records(
            &connection,
            "SELECT \"Id\", \"ProcessId\", \"ProcessName\", \"CommandLine\", \"DisplayName\",
                    \"Timestamp\", \"MetricsJson\"
             FROM \"ProcessMetricRecords\"
             WHERE \"Timestamp\" > ?1
               AND \"Timestamp\" < ?2
               AND \"Id\" > ?3
             ORDER BY \"Id\"
             LIMIT ?4",
            params![to_sqlite_text(&from), to_sqlite_text(&to), after_id, limit],
        )
    }

    fn aggregate_batch_for_rollup(
        &self,
        level: AggregationLevel,
        from: DateTime<Utc>,
        to: DateTime<Utc>,
        after_id: i64,
        limit: usize,
    ) -> Result<Vec<AggregatedMetricRecord>> {
        let limit = i64::try_from(limit)
            .map_err(|_| CoreError::invalid("rollup batch limit exceeds i64"))?;
        let connection = self.connection()?;
        query_aggregate_records(
            &connection,
            "SELECT \"Id\", \"ProcessId\", \"ProcessName\", \"AggregationLevel\", \"Timestamp\",
                    \"MetricsJson\"
             FROM \"AggregatedMetricRecords\"
             WHERE \"AggregationLevel\" = ?1
               AND \"Timestamp\" > ?2
               AND \"Timestamp\" < ?3
               AND \"Id\" > ?4
             ORDER BY \"Id\"
             LIMIT ?5",
            params![
                i32::from(level),
                to_sqlite_text(&from),
                to_sqlite_text(&to),
                after_id,
                limit
            ],
        )
    }

    fn purge_before(&self, cutoff: DateTime<Utc>) -> Result<u64> {
        let cutoff = to_sqlite_text(&cutoff);
        let mut connection = self.connection()?;
        let transaction = connection
            .transaction()
            .map_err(|error| sql_error("starting retention transaction", error))?;
        let raw = transaction
            .execute(
                "DELETE FROM \"ProcessMetricRecords\" WHERE \"Timestamp\" < ?1",
                [&cutoff],
            )
            .map_err(|error| sql_error("purging raw metrics", error))?;
        let aggregated = transaction
            .execute(
                "DELETE FROM \"AggregatedMetricRecords\" WHERE \"Timestamp\" < ?1",
                [&cutoff],
            )
            .map_err(|error| sql_error("purging aggregate metrics", error))?;
        transaction
            .commit()
            .map_err(|error| sql_error("committing retention transaction", error))?;
        Ok(raw as u64 + aggregated as u64)
    }

    fn vacuum(&self) -> Result<()> {
        let connection = self.connection()?;
        connection
            .execute_batch("VACUUM")
            .map_err(|error| sql_error("vacuuming SQLite database", error))
    }

    fn list_alerts(&self) -> Result<Vec<AlertConfiguration>> {
        let connection = self.connection()?;
        let mut statement = connection
            .prepare(
                "SELECT \"Id\", \"MetricId\", \"Threshold\", \"IsEnabled\", \"CreatedAt\", \"UpdatedAt\"
                 FROM \"AlertConfigurations\"
                 ORDER BY \"MetricId\"",
            )
            .map_err(|error| sql_error("preparing alert query", error))?;
        let rows = statement
            .query_map([], |row| {
                Ok(AlertConfiguration {
                    id: row.get(0)?,
                    metric_id: row.get(1)?,
                    threshold: row.get(2)?,
                    is_enabled: row.get(3)?,
                    created_at: timestamp_column(row, 4)?,
                    updated_at: timestamp_column(row, 5)?,
                })
            })
            .map_err(|error| sql_error("querying alerts", error))?;
        let mut alerts = Vec::new();
        for row in rows {
            alerts.push(row.map_err(|error| sql_error("reading alert row", error))?);
        }
        Ok(alerts)
    }

    fn upsert_alert(&self, alert: &AlertConfiguration, now: DateTime<Utc>) -> Result<()> {
        let now = to_sqlite_text(&now);
        let mut connection = self.connection()?;
        let transaction = connection
            .transaction()
            .map_err(|error| sql_error("starting alert transaction", error))?;

        if alert.id == 0 {
            let updated = transaction
                .execute(
                    "UPDATE \"AlertConfigurations\"
                     SET \"Threshold\" = ?1, \"IsEnabled\" = ?2, \"UpdatedAt\" = ?3
                     WHERE \"Id\" = 0",
                    params![alert.threshold, alert.is_enabled, now],
                )
                .map_err(|error| sql_error("updating zero-id alert", error))?;
            if updated == 0 {
                transaction
                    .execute(
                        "INSERT INTO \"AlertConfigurations\"
                             (\"MetricId\", \"Threshold\", \"IsEnabled\", \"CreatedAt\", \"UpdatedAt\")
                         VALUES (?1, ?2, ?3, ?4, ?4)",
                        params![alert.metric_id, alert.threshold, alert.is_enabled, now],
                    )
                    .map_err(|error| sql_error("inserting generated-id alert", error))?;
            }
        } else {
            transaction
                .execute(
                    "INSERT INTO \"AlertConfigurations\"
                         (\"Id\", \"MetricId\", \"Threshold\", \"IsEnabled\", \"CreatedAt\", \"UpdatedAt\")
                     VALUES (?1, ?2, ?3, ?4, ?5, ?5)
                     ON CONFLICT(\"Id\") DO UPDATE SET
                         \"Threshold\" = excluded.\"Threshold\",
                         \"IsEnabled\" = excluded.\"IsEnabled\",
                         \"UpdatedAt\" = excluded.\"UpdatedAt\"",
                    params![
                        alert.id,
                        alert.metric_id,
                        alert.threshold,
                        alert.is_enabled,
                        now,
                    ],
                )
                .map_err(|error| sql_error("upserting alert", error))?;
        }

        transaction
            .commit()
            .map_err(|error| sql_error("committing alert transaction", error))
    }

    fn delete_alert(&self, id: i32) -> Result<bool> {
        let connection = self.connection()?;
        connection
            .execute(
                "DELETE FROM \"AlertConfigurations\" WHERE \"Id\" = ?1",
                [id],
            )
            .map(|count| count != 0)
            .map_err(|error| sql_error("deleting alert", error))
    }

    fn list_settings(&self) -> Result<Vec<ApplicationSetting>> {
        let connection = self.connection()?;
        let mut statement = connection
            .prepare(
                "SELECT \"Id\", \"Category\", \"Key\", \"Value\", \"CreatedAt\", \"UpdatedAt\"
                 FROM \"ApplicationSettings\"
                 ORDER BY \"Category\", \"Key\"",
            )
            .map_err(|error| sql_error("preparing settings query", error))?;
        let rows = statement
            .query_map([], |row| {
                Ok(ApplicationSetting {
                    id: row.get(0)?,
                    category: row.get(1)?,
                    key: row.get(2)?,
                    value: row.get(3)?,
                    created_at: timestamp_column(row, 4)?,
                    updated_at: timestamp_column(row, 5)?,
                })
            })
            .map_err(|error| sql_error("querying settings", error))?;
        let mut settings = Vec::new();
        for row in rows {
            settings.push(row.map_err(|error| sql_error("reading setting row", error))?);
        }
        Ok(settings)
    }

    fn update_setting(
        &self,
        category: &str,
        key: &str,
        value: &str,
        now: DateTime<Utc>,
    ) -> Result<bool> {
        let connection = self.connection()?;
        connection
            .execute(
                "UPDATE \"ApplicationSettings\"
                 SET \"Value\" = ?3, \"UpdatedAt\" = ?4
                 WHERE \"Category\" = ?1 AND \"Key\" = ?2",
                params![category, key, value, to_sqlite_text(&now)],
            )
            .map(|count| count != 0)
            .map_err(|error| sql_error("updating setting", error))
    }

    fn upsert_settings(
        &self,
        entries: &[(String, String, String)],
        now: DateTime<Utc>,
    ) -> Result<SettingsUpsertCounts> {
        if entries.is_empty() {
            return Ok(SettingsUpsertCounts::default());
        }

        let now = to_sqlite_text(&now);
        let mut connection = self.connection()?;
        let transaction = connection
            .transaction()
            .map_err(|error| sql_error("starting settings transaction", error))?;
        let mut counts = SettingsUpsertCounts::default();
        {
            let mut exists_statement = transaction
                .prepare(
                    "SELECT EXISTS(
                         SELECT 1 FROM \"ApplicationSettings\"
                         WHERE \"Category\" = ?1 AND \"Key\" = ?2
                     )",
                )
                .map_err(|error| sql_error("preparing setting existence query", error))?;
            let mut update_statement = transaction
                .prepare(
                    "UPDATE \"ApplicationSettings\"
                     SET \"Value\" = ?3, \"UpdatedAt\" = ?4
                     WHERE \"Category\" = ?1 AND \"Key\" = ?2",
                )
                .map_err(|error| sql_error("preparing setting update", error))?;
            let mut insert_statement = transaction
                .prepare(
                    "INSERT INTO \"ApplicationSettings\"
                         (\"Category\", \"Key\", \"Value\", \"CreatedAt\", \"UpdatedAt\")
                     VALUES (?1, ?2, ?3, ?4, ?4)",
                )
                .map_err(|error| sql_error("preparing setting insert", error))?;

            for (category, key, value) in entries {
                let exists = exists_statement
                    .query_row(params![category, key], |row| row.get::<_, bool>(0))
                    .map_err(|error| sql_error("checking setting existence", error))?;
                if exists {
                    update_statement
                        .execute(params![category, key, value, now])
                        .map_err(|error| sql_error("updating setting in batch", error))?;
                    counts.updated += 1;
                } else {
                    insert_statement
                        .execute(params![category, key, value, now])
                        .map_err(|error| sql_error("inserting setting in batch", error))?;
                    counts.inserted += 1;
                }

                if category == "DataCollection" && key == "ProcessKeywords" {
                    counts.process_keywords_touched = true;
                }
            }
        }
        transaction
            .commit()
            .map_err(|error| sql_error("committing settings transaction", error))?;
        Ok(counts)
    }

    fn health_check(&self) -> Result<()> {
        let connection = self.connection()?;
        connection
            .query_row("SELECT 1", [], |row| row.get::<_, i32>(0))
            .map(|_| ())
            .map_err(|error| sql_error("checking SQLite health", error))
    }
}

#[cfg(test)]
mod tests {
    use std::{
        fs,
        panic::{catch_unwind, AssertUnwindSafe},
    };

    use chrono::TimeZone;
    use uuid::Uuid;

    use super::*;

    fn at(second: u32) -> DateTime<Utc> {
        Utc.with_ymd_and_hms(2026, 7, 26, 12, 0, second)
            .single()
            .unwrap()
    }

    fn raw_record(
        process_id: i32,
        process_name: &str,
        command_line: Option<&str>,
        timestamp: DateTime<Utc>,
        metrics_json: &str,
    ) -> NewProcessMetricRecord {
        NewProcessMetricRecord {
            process_id,
            process_name: process_name.to_owned(),
            command_line: command_line.map(str::to_owned),
            display_name: None,
            timestamp,
            metrics_json: metrics_json.to_owned(),
        }
    }

    fn aggregate_record(
        process_id: i32,
        process_name: &str,
        level: AggregationLevel,
        timestamp: DateTime<Utc>,
        metrics_json: &str,
    ) -> NewAggregatedMetricRecord {
        NewAggregatedMetricRecord {
            process_id,
            process_name: process_name.to_owned(),
            aggregation_level: level,
            timestamp,
            metrics_json: metrics_json.to_owned(),
        }
    }

    #[test]
    fn raw_queries_match_filter_boundary_and_ordering_semantics() {
        let store = SqliteMetricStore::open_in_memory().unwrap();
        let records = [
            raw_record(1, "Alpha", Some("worker-token first"), at(0), "{}"),
            raw_record(1, "Alpha", Some("worker-token second"), at(1), "{}"),
            raw_record(2, "Zulu", Some("other"), at(2), "{}"),
            raw_record(3, "Beta", None, at(2), "{}"),
        ];
        assert_eq!(store.save_process_metrics(&records).unwrap(), 4);

        let latest = store
            .latest_process_metrics(&MetricFilter::default())
            .unwrap();
        assert_eq!(
            latest
                .iter()
                .map(|record| record.process_name.as_str())
                .collect::<Vec<_>>(),
            ["Beta", "Zulu"]
        );

        let latest_for_process = store
            .latest_process_metrics(&MetricFilter {
                process_id: Some(1),
                ..MetricFilter::default()
            })
            .unwrap();
        assert_eq!(latest_for_process.len(), 1);
        assert_eq!(latest_for_process[0].timestamp, at(1));

        let latest_for_keyword = store
            .latest_process_metrics(&MetricFilter {
                keyword: Some("worker-token".to_owned()),
                ..MetricFilter::default()
            })
            .unwrap();
        assert_eq!(latest_for_keyword.len(), 1);
        assert_eq!(latest_for_keyword[0].timestamp, at(1));

        let case_mismatch = store
            .latest_process_metrics(&MetricFilter {
                process_name: Some("alpha".to_owned()),
                ..MetricFilter::default()
            })
            .unwrap();
        assert!(case_mismatch.is_empty());

        let whitespace_is_ignored = store
            .latest_process_metrics(&MetricFilter {
                process_name: Some(" \t ".to_owned()),
                ..MetricFilter::default()
            })
            .unwrap();
        assert_eq!(whitespace_is_ignored.len(), 2);

        let history = store.history_raw(1, Some(at(0)), Some(at(1))).unwrap();
        assert_eq!(
            history
                .iter()
                .map(|record| record.timestamp)
                .collect::<Vec<_>>(),
            [at(0), at(1)]
        );

        let summaries = store
            .process_summaries(&MetricFilter {
                keyword: Some("worker-token".to_owned()),
                from: Some(at(0)),
                to: Some(at(1)),
                ..MetricFilter::default()
            })
            .unwrap();
        assert_eq!(summaries.len(), 1);
        assert_eq!(summaries[0].process_id, 1);
        assert_eq!(summaries[0].last_seen, at(1));
        assert_eq!(summaries[0].record_count, 2);

        let summary_case_mismatch = store
            .process_summaries(&MetricFilter {
                keyword: Some("WORKER-TOKEN".to_owned()),
                ..MetricFilter::default()
            })
            .unwrap();
        assert!(summary_case_mismatch.is_empty());
    }

    #[test]
    fn batch_writes_are_atomic_when_json_constraints_fail() {
        let store = SqliteMetricStore::open_in_memory().unwrap();
        let raw = [
            raw_record(1, "first", None, at(0), "{}"),
            raw_record(1, "second", None, at(1), "not-json"),
        ];
        assert!(matches!(
            store.save_process_metrics(&raw),
            Err(CoreError::Storage(_))
        ));
        assert!(store.history_raw(1, None, None).unwrap().is_empty());

        let aggregates = [
            aggregate_record(1, "first", AggregationLevel::Minute, at(0), "{}"),
            aggregate_record(1, "second", AggregationLevel::Minute, at(1), "not-json"),
        ];
        assert!(matches!(
            store.save_aggregates(&aggregates),
            Err(CoreError::Storage(_))
        ));
        assert!(store
            .history_aggregated(1, AggregationLevel::Minute, None, None)
            .unwrap()
            .is_empty());
    }

    #[test]
    fn aggregation_queries_preserve_inclusive_history_and_open_worker_windows() {
        let store = SqliteMetricStore::open_in_memory().unwrap();
        store
            .save_process_metrics(&[
                raw_record(1, "raw", None, at(0), "{}"),
                raw_record(1, "raw", None, at(1), "{}"),
                raw_record(1, "raw", None, at(2), "{}"),
                raw_record(1, "raw", None, at(3), "{}"),
            ])
            .unwrap();

        store
            .save_aggregates(&[
                aggregate_record(1, "raw", AggregationLevel::Minute, at(0), "{}"),
                aggregate_record(2, "Zulu", AggregationLevel::Minute, at(1), "{}"),
                aggregate_record(1, "Alpha", AggregationLevel::Minute, at(1), "{}"),
                aggregate_record(1, "raw", AggregationLevel::Minute, at(2), "{}"),
                aggregate_record(1, "raw", AggregationLevel::Minute, at(3), "{}"),
                aggregate_record(1, "raw", AggregationLevel::Hour, at(2), "{}"),
            ])
            .unwrap();

        assert_eq!(store.earliest_raw_timestamp().unwrap(), Some(at(0)));
        assert_eq!(
            store
                .earliest_aggregate_timestamp(AggregationLevel::Minute)
                .unwrap(),
            Some(at(0))
        );
        assert_eq!(
            store.aggregate_watermark(AggregationLevel::Minute).unwrap(),
            Some(at(3))
        );
        assert_eq!(
            store.aggregate_watermark(AggregationLevel::Day).unwrap(),
            None
        );

        let history = store
            .history_aggregated(1, AggregationLevel::Minute, Some(at(1)), Some(at(2)))
            .unwrap();
        assert_eq!(
            history
                .iter()
                .map(|record| record.timestamp)
                .collect::<Vec<_>>(),
            [at(1), at(2)]
        );

        let at_one = store
            .aggregations(AggregationLevel::Minute, at(1), at(1))
            .unwrap();
        assert_eq!(
            at_one
                .iter()
                .map(|record| record.process_name.as_str())
                .collect::<Vec<_>>(),
            ["Alpha", "Zulu"]
        );

        let first_raw_batch = store.raw_batch_for_aggregation(at(0), at(3), 0, 1).unwrap();
        assert_eq!(first_raw_batch.len(), 1);
        assert_eq!(first_raw_batch[0].timestamp, at(1));
        let second_raw_batch = store
            .raw_batch_for_aggregation(at(0), at(3), first_raw_batch[0].id, 10)
            .unwrap();
        assert_eq!(
            second_raw_batch
                .iter()
                .map(|record| record.timestamp)
                .collect::<Vec<_>>(),
            [at(2)]
        );

        let first_rollup_batch = store
            .aggregate_batch_for_rollup(AggregationLevel::Minute, at(0), at(3), 0, 2)
            .unwrap();
        assert_eq!(
            first_rollup_batch
                .iter()
                .map(|record| record.process_name.as_str())
                .collect::<Vec<_>>(),
            ["Zulu", "Alpha"]
        );
        let second_rollup_batch = store
            .aggregate_batch_for_rollup(
                AggregationLevel::Minute,
                at(0),
                at(3),
                first_rollup_batch[1].id,
                10,
            )
            .unwrap();
        assert_eq!(second_rollup_batch.len(), 1);
        assert_eq!(second_rollup_batch[0].timestamp, at(2));
    }

    #[test]
    fn retention_health_and_vacuum_keep_rows_at_the_cutoff() {
        let store = SqliteMetricStore::open_in_memory().unwrap();
        store
            .save_process_metrics(&[
                raw_record(1, "old", None, at(0), "{}"),
                raw_record(1, "cutoff", None, at(1), "{}"),
            ])
            .unwrap();
        store
            .save_aggregates(&[
                aggregate_record(1, "old", AggregationLevel::Minute, at(0), "{}"),
                aggregate_record(1, "cutoff", AggregationLevel::Minute, at(1), "{}"),
            ])
            .unwrap();

        assert_eq!(store.purge_before(at(1)).unwrap(), 2);
        assert_eq!(store.history_raw(1, None, None).unwrap().len(), 1);
        assert_eq!(
            store
                .history_aggregated(1, AggregationLevel::Minute, None, None)
                .unwrap()
                .len(),
            1
        );
        store.vacuum().unwrap();
        store.health_check().unwrap();
    }

    #[test]
    fn alert_and_setting_upserts_match_csharp_update_rules() {
        let store = SqliteMetricStore::open_in_memory().unwrap();
        let initial_alerts = store.list_alerts().unwrap();
        assert_eq!(
            initial_alerts
                .iter()
                .map(|alert| alert.metric_id.as_str())
                .collect::<Vec<_>>(),
            ["cpu", "gpu", "memory", "vram"]
        );
        let original_cpu = initial_alerts
            .iter()
            .find(|alert| alert.id == 1)
            .unwrap()
            .clone();
        store
            .upsert_alert(
                &AlertConfiguration {
                    id: 1,
                    metric_id: "renamed".to_owned(),
                    threshold: 75.0,
                    is_enabled: false,
                    created_at: at(8),
                    updated_at: at(8),
                },
                at(9),
            )
            .unwrap();
        let cpu = store
            .list_alerts()
            .unwrap()
            .into_iter()
            .find(|alert| alert.id == 1)
            .unwrap();
        assert_eq!(cpu.metric_id, "cpu");
        assert_eq!(cpu.created_at, original_cpu.created_at);
        assert_eq!(cpu.threshold, 75.0);
        assert!(!cpu.is_enabled);
        assert_eq!(cpu.updated_at, at(9));

        store
            .upsert_alert(
                &AlertConfiguration {
                    id: 99,
                    metric_id: "disk".to_owned(),
                    threshold: 80.0,
                    is_enabled: true,
                    created_at: at(0),
                    updated_at: at(0),
                },
                at(10),
            )
            .unwrap();
        let disk = store
            .list_alerts()
            .unwrap()
            .into_iter()
            .find(|alert| alert.id == 99)
            .unwrap();
        assert_eq!(disk.created_at, at(10));
        assert_eq!(disk.updated_at, at(10));
        assert!(store.delete_alert(99).unwrap());
        assert!(!store.delete_alert(99).unwrap());

        store
            .upsert_alert(
                &AlertConfiguration {
                    id: 0,
                    metric_id: "network".to_owned(),
                    threshold: 70.0,
                    is_enabled: true,
                    created_at: at(0),
                    updated_at: at(0),
                },
                at(10),
            )
            .unwrap();
        let generated = store
            .list_alerts()
            .unwrap()
            .into_iter()
            .find(|alert| alert.metric_id == "network")
            .unwrap();
        assert!(generated.id > 0);
        assert_eq!(generated.created_at, at(10));
        assert_eq!(generated.updated_at, at(10));

        let initial_settings = store.list_settings().unwrap();
        assert_eq!(initial_settings.len(), 17);
        assert!(initial_settings.windows(2).all(|pair| {
            (&pair[0].category, &pair[0].key) <= (&pair[1].category, &pair[1].key)
        }));
        let original_opacity = initial_settings
            .iter()
            .find(|setting| setting.category == "Appearance" && setting.key == "Opacity")
            .unwrap()
            .clone();
        assert!(store
            .update_setting("Appearance", "Opacity", "75", at(11))
            .unwrap());
        assert!(!store
            .update_setting("Missing", "Setting", "value", at(11))
            .unwrap());

        let counts = store
            .upsert_settings(
                &[
                    (
                        "DataCollection".to_owned(),
                        "ProcessKeywords".to_owned(),
                        "[\"rust\"]".to_owned(),
                    ),
                    (
                        "Custom".to_owned(),
                        "Feature".to_owned(),
                        "enabled".to_owned(),
                    ),
                ],
                at(12),
            )
            .unwrap();
        assert_eq!(
            counts,
            SettingsUpsertCounts {
                updated: 1,
                inserted: 1,
                process_keywords_touched: true,
            }
        );

        let settings = store.list_settings().unwrap();
        let opacity = settings
            .iter()
            .find(|setting| setting.category == "Appearance" && setting.key == "Opacity")
            .unwrap();
        assert_eq!(opacity.value, "75");
        assert_eq!(opacity.created_at, original_opacity.created_at);
        assert_eq!(opacity.updated_at, at(11));
        let custom = settings
            .iter()
            .find(|setting| setting.category == "Custom" && setting.key == "Feature")
            .unwrap();
        assert_eq!(custom.created_at, at(12));
        assert_eq!(custom.updated_at, at(12));
    }

    #[test]
    fn opening_an_existing_migration_schema_adds_display_name_and_current_seed_data() {
        let path = std::env::temp_dir().join(format!("xhm-store-{}.db", Uuid::new_v4()));
        {
            let connection = Connection::open(&path).unwrap();
            connection
                .execute_batch(
                    r#"
                    CREATE TABLE "AlertConfigurations" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_AlertConfigurations" PRIMARY KEY AUTOINCREMENT,
                        "MetricId" TEXT NOT NULL,
                        "Threshold" REAL NOT NULL,
                        "IsEnabled" INTEGER NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL
                    );
                    INSERT INTO "AlertConfigurations"
                        ("Id", "MetricId", "Threshold", "IsEnabled", "CreatedAt", "UpdatedAt")
                    VALUES
                        (1, 'cpu', 90.0, 1, '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
                        (2, 'memory', 90.0, 1, '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
                        (3, 'gpu', 90.0, 1, '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
                        (4, 'vram', 90.0, 1, '2024-01-01 00:00:00', '2024-01-01 00:00:00');

                    CREATE TABLE "ProcessMetricRecords" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_ProcessMetricRecords" PRIMARY KEY AUTOINCREMENT,
                        "ProcessId" INTEGER NOT NULL,
                        "ProcessName" TEXT NOT NULL,
                        "CommandLine" TEXT NULL,
                        "Timestamp" TEXT NOT NULL,
                        "MetricsJson" TEXT NOT NULL,
                        CONSTRAINT "CK_ProcessMetricRecords_MetricsJson_Valid" CHECK (json_valid(MetricsJson))
                    );
                    CREATE INDEX "IX_ProcessMetricRecords_ProcessId_Timestamp"
                        ON "ProcessMetricRecords" ("ProcessId", "Timestamp");
                    CREATE INDEX "IX_ProcessMetricRecords_Timestamp"
                        ON "ProcessMetricRecords" ("Timestamp");
                    INSERT INTO "ProcessMetricRecords"
                        ("ProcessId", "ProcessName", "CommandLine", "Timestamp", "MetricsJson")
                    VALUES (7, 'legacy', NULL, '2026-07-26 12:00:00', '{}');

                    CREATE TABLE "ApplicationSettings" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_ApplicationSettings" PRIMARY KEY AUTOINCREMENT,
                        "Category" TEXT NOT NULL,
                        "Key" TEXT NOT NULL,
                        "Value" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL
                    );
                    CREATE UNIQUE INDEX "IX_ApplicationSettings_Category_Key"
                        ON "ApplicationSettings" ("Category", "Key");
                    INSERT INTO "ApplicationSettings"
                        ("Id", "Category", "Key", "Value", "CreatedAt", "UpdatedAt")
                    VALUES
                        (9, 'System', 'SignalRPort', '35179', '2024-01-01 00:00:00', '2024-01-01 00:00:00'),
                        (10, 'System', 'WebPort', '35180', '2024-01-01 00:00:00', '2024-01-01 00:00:00');

                    CREATE TABLE "__EFMigrationsHistory" (
                        "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                        "ProductVersion" TEXT NOT NULL
                    );
                    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20251221075010_InitialCreate', '8.0.22');
                    "#,
                )
                .unwrap();
        }

        {
            let store = SqliteMetricStore::open(&path).unwrap();
            let legacy = store.history_raw(7, None, None).unwrap();
            assert_eq!(legacy.len(), 1);
            assert_eq!(legacy[0].display_name, None);

            let settings = store.list_settings().unwrap();
            assert_eq!(settings.len(), 17);
            assert!(!settings.iter().any(|setting| setting.key == "WebPort"));
            assert!(settings.iter().any(|setting| {
                setting.category == "Monitoring" && setting.key == "MonitorCpu"
            }));
            for key in [
                "EnableLanAccess",
                "EnableAccessKey",
                "AccessKey",
                "IpWhitelist",
            ] {
                assert!(settings
                    .iter()
                    .any(|setting| setting.category == "System" && setting.key == key));
            }

            let connection = store.connection().unwrap();
            let has_display_name = connection
                .query_row(
                    "SELECT EXISTS(
                         SELECT 1 FROM pragma_table_info('ProcessMetricRecords')
                         WHERE name = 'DisplayName'
                     )",
                    [],
                    |row| row.get::<_, bool>(0),
                )
                .unwrap();
            assert!(has_display_name);

            let mut index_statement = connection
                .prepare(
                    "SELECT name FROM sqlite_master
                     WHERE type = 'index' AND name LIKE 'IX_%'
                     ORDER BY name",
                )
                .unwrap();
            let indexes = index_statement
                .query_map([], |row| row.get::<_, String>(0))
                .unwrap()
                .collect::<rusqlite::Result<Vec<_>>>()
                .unwrap();
            assert_eq!(
                indexes,
                [
                    "IX_AggregatedMetricRecords_AggregationLevel_Timestamp",
                    "IX_AggregatedMetricRecords_ProcessId_AggregationLevel_Timestamp",
                    "IX_AggregatedMetricRecords_ProcessId_Timestamp",
                    "IX_ApplicationSettings_Category_Key",
                    "IX_ProcessMetricRecords_ProcessId_Timestamp",
                    "IX_ProcessMetricRecords_Timestamp",
                ]
            );

            let migration_count = connection
                .query_row(
                    "SELECT COUNT(*) FROM \"__EFMigrationsHistory\"",
                    [],
                    |row| row.get::<_, i64>(0),
                )
                .unwrap();
            assert_eq!(migration_count, 8);
            drop(index_statement);
            drop(connection);
            assert!(store.delete_alert(1).unwrap());
        }

        {
            let reopened = SqliteMetricStore::open(&path).unwrap();
            assert!(!reopened
                .list_alerts()
                .unwrap()
                .iter()
                .any(|alert| alert.id == 1));
        }

        let _ = fs::remove_file(&path);
        let _ = fs::remove_file(format!("{}-wal", path.display()));
        let _ = fs::remove_file(format!("{}-shm", path.display()));
    }

    #[test]
    fn a_poisoned_connection_lock_is_reported_as_a_storage_error() {
        let store = SqliteMetricStore::open_in_memory().unwrap();
        let panic = catch_unwind(AssertUnwindSafe(|| {
            let _connection = store.connection.lock().unwrap();
            panic!("poison the SQLite mutex");
        }));
        assert!(panic.is_err());

        assert!(matches!(
            store.health_check(),
            Err(CoreError::Storage(message)) if message.contains("poisoned")
        ));
    }
}
