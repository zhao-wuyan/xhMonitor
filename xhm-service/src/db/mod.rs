use std::{
    collections::HashSet,
    fmt, fs,
    path::{Path, PathBuf},
    sync::{Mutex, MutexGuard},
};

use chrono::{DateTime, Utc};
use rusqlite::{params, types::Type, Connection, OptionalExtension, Params, Row};
use xhm_core::{
    models::{
        AggregatedMetricRecord, AggregationLevel, AlertConfiguration, ApplicationSetting,
        MetricFilter, NewAggregatedMetricRecord, NewProcessMetricRecord, ProcessMetricRecord,
        ProcessSummary, PurgeBatchResult, PurgeCursor, PurgeWindow, RollupCommitResult,
        RollupCoverage, SettingsUpsertCounts, WalCheckpointResult,
    },
    time::{from_sqlite_text, to_sqlite_text},
    traits::MetricStore,
    CoreError, Result,
};

const LIFECYCLE_SCHEMA_MIGRATION_ID: &str = "20260810000000_AddMetricLifecycleStorage";
const LEGACY_BACKUP_FILE_NAME: &str = ".xhmonitor-legacy-delete-pending.db";
const REBUILD_FILE_NAME: &str = ".xhmonitor-rebuild-pending.db";

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

CREATE TABLE IF NOT EXISTS "MetricLifecycleCheckpoints" (
    "TargetLevel" INTEGER NOT NULL PRIMARY KEY,
    "CoveredFrom" TEXT NOT NULL,
    "CompletedThrough" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
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
CREATE INDEX IF NOT EXISTS "IX_ProcessMetricRecords_Timestamp_Id"
    ON "ProcessMetricRecords" ("Timestamp", "Id");
CREATE INDEX IF NOT EXISTS "IX_AggregatedMetricRecords_AggregationLevel_Timestamp_Id"
    ON "AggregatedMetricRecords" ("AggregationLevel", "Timestamp", "Id");
CREATE INDEX IF NOT EXISTS "IX_AggregatedMetricRecords_FullBucket"
    ON "AggregatedMetricRecords"
       ("ProcessId", "ProcessName", "AggregationLevel", "Timestamp");
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

const METRIC_RECORDING_SETTINGS_SEED_SQL: &str = r#"
INSERT OR IGNORE INTO "ApplicationSettings"
    ("Category", "Key", "Value", "CreatedAt", "UpdatedAt")
VALUES
    ('DataCollection', 'RecordMetrics', 'false', '2024-01-01 00:00:00', '2024-01-01 00:00:00');
"#;

#[derive(Debug)]
pub struct LegacyDatabaseRebuild {
    backup_path: PathBuf,
    pub settings_copied: usize,
    pub alerts_copied: usize,
}

#[derive(Debug)]
pub enum LegacyDatabasePreparation {
    NotRequired,
    Deferred,
    Rebuilt(LegacyDatabaseRebuild),
}

#[derive(Debug, PartialEq)]
struct LegacySetting {
    category: String,
    key: String,
    value: String,
    created_at: String,
    updated_at: String,
}

#[derive(Debug, PartialEq)]
struct LegacyAlert {
    id: i64,
    metric_id: String,
    threshold: f64,
    is_enabled: bool,
    created_at: String,
    updated_at: String,
}

pub fn prepare_legacy_database(path: impl AsRef<Path>) -> Result<LegacyDatabasePreparation> {
    let path = path.as_ref();
    let parent = path.parent().ok_or_else(|| {
        CoreError::storage(format!("database path has no parent: {}", path.display()))
    })?;
    let backup_path = parent.join(LEGACY_BACKUP_FILE_NAME);
    let rebuild_path = parent.join(REBUILD_FILE_NAME);

    if !path.exists() && backup_path.exists() {
        remove_database_files(&rebuild_path)?;
        fs::rename(&backup_path, path).map_err(|error| {
            CoreError::storage(format!(
                "failed to restore interrupted legacy database {}: {error}",
                path.display()
            ))
        })?;
    } else if path.exists() {
        remove_database_files(&rebuild_path)?;
    }

    if !path.is_file() {
        return Ok(LegacyDatabasePreparation::NotRequired);
    }

    let legacy = Connection::open(path).map_err(|error| {
        CoreError::storage(format!(
            "failed to inspect SQLite database {}: {error}",
            path.display()
        ))
    })?;
    legacy
        .busy_timeout(std::time::Duration::from_secs(1))
        .map_err(|error| sql_error("configuring legacy database busy timeout", error))?;

    let has_lifecycle_marker = migration_exists(&legacy, LIFECYCLE_SCHEMA_MIGRATION_ID)?;
    let has_lifecycle_table = table_exists(&legacy, "MetricLifecycleCheckpoints")?;
    let has_metric_table = table_exists(&legacy, "ProcessMetricRecords")?;
    tracing::info!(
        path = %path.display(),
        has_lifecycle_marker,
        has_lifecycle_table,
        has_metric_table,
        "inspected database for lifecycle rebuild"
    );
    if has_lifecycle_marker || !has_metric_table {
        tracing::info!(
            path = %path.display(),
            has_lifecycle_marker,
            has_lifecycle_table,
            has_metric_table,
            "lifecycle database rebuild skipped"
        );
        drop(legacy);
        return if backup_path.exists() {
            Ok(LegacyDatabasePreparation::Rebuilt(LegacyDatabaseRebuild {
                backup_path,
                settings_copied: 0,
                alerts_copied: 0,
            }))
        } else {
            Ok(LegacyDatabasePreparation::NotRequired)
        };
    }
    if backup_path.exists() {
        return Err(CoreError::storage(format!(
            "cannot rebuild legacy database while pending backup exists: {}",
            backup_path.display()
        )));
    }
    tracing::info!(
        path = %path.display(),
        "legacy metric database detected; rebuilding without metric history"
    );

    if checkpoint_and_use_delete_journal(&legacy)? == CheckpointOutcome::Deferred {
        tracing::warn!(
            path = %path.display(),
            "legacy database rebuild deferred because the database is in use"
        );
        return Ok(LegacyDatabasePreparation::Deferred);
    }
    let settings = load_legacy_settings(&legacy)?;
    let alerts = load_legacy_alerts(&legacy)?;
    drop(legacy);

    let rebuild_result = build_replacement_database(&rebuild_path, &settings, &alerts);
    if let Err(error) = rebuild_result {
        let _ = remove_database_files(&rebuild_path);
        return Err(error);
    }

    fs::rename(path, &backup_path).map_err(|error| {
        CoreError::storage(format!(
            "failed to preserve legacy database {}: {error}",
            path.display()
        ))
    })?;
    if let Err(error) = fs::rename(&rebuild_path, path) {
        let rollback = fs::rename(&backup_path, path);
        return Err(CoreError::storage(format!(
            "failed to activate rebuilt database {}: {error}; rollback: {}",
            path.display(),
            rollback
                .map(|()| "restored legacy database".to_owned())
                .unwrap_or_else(|rollback_error| rollback_error.to_string())
        )));
    }
    tracing::info!(
        path = %path.display(),
        backup = %backup_path.display(),
        settings = settings.len(),
        alerts = alerts.len(),
        "rebuilt lifecycle database activated"
    );

    Ok(LegacyDatabasePreparation::Rebuilt(LegacyDatabaseRebuild {
        backup_path,
        settings_copied: settings.len(),
        alerts_copied: alerts.len(),
    }))
}

pub fn finalize_legacy_database_rebuild(rebuild: LegacyDatabaseRebuild) -> Result<()> {
    remove_database_files(&rebuild.backup_path)
}

fn migration_exists(connection: &Connection, migration_id: &str) -> Result<bool> {
    if !table_exists(connection, "__EFMigrationsHistory")? {
        return Ok(false);
    }
    connection
        .query_row(
            "SELECT EXISTS(
                 SELECT 1 FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = ?1
             )",
            [migration_id],
            |row| row.get(0),
        )
        .map_err(|error| sql_error("checking lifecycle schema migration", error))
}

fn table_exists(connection: &Connection, table: &str) -> Result<bool> {
    connection
        .query_row(
            "SELECT EXISTS(
                 SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = ?1
             )",
            [table],
            |row| row.get(0),
        )
        .map_err(|error| sql_error("checking SQLite table", error))
}

fn require_table_columns(
    connection: &Connection,
    table: &str,
    required_columns: &[&str],
) -> Result<()> {
    let mut statement = connection
        .prepare("SELECT name FROM pragma_table_info(?1)")
        .map_err(|error| sql_error("preparing SQLite column inspection", error))?;
    let columns = statement
        .query_map([table], |row| row.get::<_, String>(0))
        .map_err(|error| sql_error("querying SQLite columns", error))?
        .collect::<rusqlite::Result<HashSet<_>>>()
        .map_err(|error| sql_error("reading SQLite columns", error))?;
    let missing = required_columns
        .iter()
        .copied()
        .filter(|column| !columns.contains(*column))
        .collect::<Vec<_>>();
    if missing.is_empty() {
        Ok(())
    } else {
        Err(CoreError::storage(format!(
            "legacy table {table} is missing required columns: {}",
            missing.join(", ")
        )))
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum CheckpointOutcome {
    Ready,
    Deferred,
}

fn checkpoint_and_use_delete_journal(connection: &Connection) -> Result<CheckpointOutcome> {
    let busy = match connection.query_row("PRAGMA wal_checkpoint(TRUNCATE)", [], |row| {
        row.get::<_, i64>(0)
    }) {
        Ok(busy) => busy,
        Err(error) if is_database_in_use(&error) => return Ok(CheckpointOutcome::Deferred),
        Err(error) => return Err(sql_error("checkpointing legacy SQLite WAL", error)),
    };
    if busy != 0 {
        return Ok(CheckpointOutcome::Deferred);
    }
    let mode = match connection.query_row("PRAGMA journal_mode=DELETE", [], |row| {
        row.get::<_, String>(0)
    }) {
        Ok(mode) => mode,
        Err(error) if is_database_in_use(&error) => return Ok(CheckpointOutcome::Deferred),
        Err(error) => {
            return Err(sql_error(
                "switching SQLite journal mode for rebuild",
                error,
            ))
        }
    };
    if !mode.eq_ignore_ascii_case("delete") {
        return Ok(CheckpointOutcome::Deferred);
    }
    Ok(CheckpointOutcome::Ready)
}

fn is_database_in_use(error: &rusqlite::Error) -> bool {
    matches!(
        error,
        rusqlite::Error::SqliteFailure(code, _)
            if matches!(
                code.code,
                rusqlite::ffi::ErrorCode::DatabaseBusy
                    | rusqlite::ffi::ErrorCode::DatabaseLocked
            )
    )
}

fn load_legacy_settings(connection: &Connection) -> Result<Vec<LegacySetting>> {
    if !table_exists(connection, "ApplicationSettings")? {
        return Ok(Vec::new());
    }
    require_table_columns(
        connection,
        "ApplicationSettings",
        &["Category", "Key", "Value", "CreatedAt", "UpdatedAt"],
    )?;
    let mut statement = connection
        .prepare(
            "SELECT \"Category\", \"Key\", \"Value\", \"CreatedAt\", \"UpdatedAt\"
             FROM \"ApplicationSettings\"
             WHERE NOT (
                 (\"Category\" = 'System' AND \"Key\" IN ('SignalRPort', 'WebPort'))
                 OR
                 (\"Category\" = 'DataCollection' AND \"Key\" IN ('SystemInterval', 'ProcessInterval'))
             )
             ORDER BY \"Category\", \"Key\"",
        )
        .map_err(|error| sql_error("preparing legacy settings query", error))?;
    let rows = statement
        .query_map([], |row| {
            Ok(LegacySetting {
                category: row.get(0)?,
                key: row.get(1)?,
                value: row.get(2)?,
                created_at: row.get(3)?,
                updated_at: row.get(4)?,
            })
        })
        .map_err(|error| sql_error("querying legacy settings", error))?;
    rows.collect::<rusqlite::Result<Vec<_>>>()
        .map_err(|error| sql_error("reading legacy settings", error))
}

fn load_legacy_alerts(connection: &Connection) -> Result<Vec<LegacyAlert>> {
    if !table_exists(connection, "AlertConfigurations")? {
        return Ok(Vec::new());
    }
    require_table_columns(
        connection,
        "AlertConfigurations",
        &[
            "Id",
            "MetricId",
            "Threshold",
            "IsEnabled",
            "CreatedAt",
            "UpdatedAt",
        ],
    )?;
    let mut statement = connection
        .prepare(
            "SELECT \"Id\", \"MetricId\", \"Threshold\", \"IsEnabled\", \"CreatedAt\", \"UpdatedAt\"
             FROM \"AlertConfigurations\"
             ORDER BY \"Id\"",
        )
        .map_err(|error| sql_error("preparing legacy alerts query", error))?;
    let rows = statement
        .query_map([], |row| {
            Ok(LegacyAlert {
                id: row.get(0)?,
                metric_id: row.get(1)?,
                threshold: row.get(2)?,
                is_enabled: row.get(3)?,
                created_at: row.get(4)?,
                updated_at: row.get(5)?,
            })
        })
        .map_err(|error| sql_error("querying legacy alerts", error))?;
    rows.collect::<rusqlite::Result<Vec<_>>>()
        .map_err(|error| sql_error("reading legacy alerts", error))
}

fn build_replacement_database(
    path: &Path,
    settings: &[LegacySetting],
    alerts: &[LegacyAlert],
) -> Result<()> {
    let mut connection = Connection::open(path).map_err(|error| {
        CoreError::storage(format!(
            "failed to create rebuilt SQLite database {}: {error}",
            path.display()
        ))
    })?;
    initialize_schema(&mut connection, true)?;
    let transaction = connection
        .transaction()
        .map_err(|error| sql_error("starting legacy configuration copy", error))?;

    for setting in settings {
        transaction
            .execute(
                "INSERT INTO \"ApplicationSettings\"
                     (\"Category\", \"Key\", \"Value\", \"CreatedAt\", \"UpdatedAt\")
                 VALUES (?1, ?2, ?3, ?4, ?5)
                 ON CONFLICT(\"Category\", \"Key\") DO UPDATE SET
                     \"Value\" = excluded.\"Value\",
                     \"CreatedAt\" = excluded.\"CreatedAt\",
                     \"UpdatedAt\" = excluded.\"UpdatedAt\"",
                params![
                    setting.category,
                    setting.key,
                    setting.value,
                    setting.created_at,
                    setting.updated_at,
                ],
            )
            .map_err(|error| sql_error("copying legacy setting", error))?;
    }
    for alert in alerts {
        transaction
            .execute(
                "INSERT INTO \"AlertConfigurations\"
                     (\"Id\", \"MetricId\", \"Threshold\", \"IsEnabled\", \"CreatedAt\", \"UpdatedAt\")
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6)
                 ON CONFLICT(\"Id\") DO UPDATE SET
                     \"MetricId\" = excluded.\"MetricId\",
                     \"Threshold\" = excluded.\"Threshold\",
                     \"IsEnabled\" = excluded.\"IsEnabled\",
                     \"CreatedAt\" = excluded.\"CreatedAt\",
                     \"UpdatedAt\" = excluded.\"UpdatedAt\"",
                params![
                    alert.id,
                    alert.metric_id,
                    alert.threshold,
                    alert.is_enabled,
                    alert.created_at,
                    alert.updated_at,
                ],
            )
            .map_err(|error| sql_error("copying legacy alert", error))?;
    }
    transaction
        .commit()
        .map_err(|error| sql_error("committing legacy configuration copy", error))?;

    verify_copied_configuration(&connection, settings, alerts)?;
    let quick_check = connection
        .query_row("PRAGMA quick_check", [], |row| row.get::<_, String>(0))
        .map_err(|error| sql_error("checking rebuilt SQLite database", error))?;
    if quick_check != "ok" {
        return Err(CoreError::storage(format!(
            "rebuilt SQLite database failed quick_check: {quick_check}"
        )));
    }
    match checkpoint_and_use_delete_journal(&connection)? {
        CheckpointOutcome::Ready => Ok(()),
        CheckpointOutcome::Deferred => Err(CoreError::storage(
            "rebuilt SQLite database unexpectedly remained busy",
        )),
    }
}

fn verify_copied_configuration(
    connection: &Connection,
    settings: &[LegacySetting],
    alerts: &[LegacyAlert],
) -> Result<()> {
    for setting in settings {
        let copied = connection
            .query_row(
                "SELECT EXISTS(
                     SELECT 1 FROM \"ApplicationSettings\"
                     WHERE \"Category\" = ?1 AND \"Key\" = ?2 AND \"Value\" = ?3
                       AND \"CreatedAt\" = ?4 AND \"UpdatedAt\" = ?5
                 )",
                params![
                    setting.category,
                    setting.key,
                    setting.value,
                    setting.created_at,
                    setting.updated_at,
                ],
                |row| row.get::<_, bool>(0),
            )
            .map_err(|error| sql_error("verifying copied legacy setting", error))?;
        if !copied {
            return Err(CoreError::storage(format!(
                "rebuilt database did not preserve setting {}/{}",
                setting.category, setting.key
            )));
        }
    }
    for alert in alerts {
        let copied = connection
            .query_row(
                "SELECT EXISTS(
                     SELECT 1 FROM \"AlertConfigurations\"
                     WHERE \"Id\" = ?1 AND \"MetricId\" = ?2 AND \"Threshold\" = ?3
                       AND \"IsEnabled\" = ?4 AND \"CreatedAt\" = ?5 AND \"UpdatedAt\" = ?6
                 )",
                params![
                    alert.id,
                    alert.metric_id,
                    alert.threshold,
                    alert.is_enabled,
                    alert.created_at,
                    alert.updated_at,
                ],
                |row| row.get::<_, bool>(0),
            )
            .map_err(|error| sql_error("verifying copied legacy alert", error))?;
        if !copied {
            return Err(CoreError::storage(format!(
                "rebuilt database did not preserve alert {}",
                alert.id
            )));
        }
    }
    Ok(())
}

fn remove_database_files(path: &Path) -> Result<()> {
    for candidate in [
        path.to_path_buf(),
        sqlite_sidecar_path(path, "-wal"),
        sqlite_sidecar_path(path, "-shm"),
    ] {
        match fs::remove_file(&candidate) {
            Ok(()) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => {
                return Err(CoreError::storage(format!(
                    "failed to remove database file {}: {error}",
                    candidate.display()
                )));
            }
        }
    }
    Ok(())
}

fn sqlite_sidecar_path(path: &Path, suffix: &str) -> PathBuf {
    let mut sidecar = path.as_os_str().to_os_string();
    sidecar.push(suffix);
    PathBuf::from(sidecar)
}
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
        Self::from_connection(connection, true)
    }

    pub fn open_deferred_legacy(path: impl AsRef<Path>) -> Result<Self> {
        let path = path.as_ref();
        let connection = Connection::open(path).map_err(|error| {
            CoreError::storage(format!(
                "failed to open deferred legacy SQLite database {}: {error}",
                path.display()
            ))
        })?;
        Self::from_connection(connection, false)
    }

    pub fn open_in_memory() -> Result<Self> {
        let connection = Connection::open_in_memory().map_err(|error| {
            CoreError::storage(format!("failed to open in-memory SQLite: {error}"))
        })?;
        Self::from_connection(connection, true)
    }

    fn from_connection(
        mut connection: Connection,
        record_lifecycle_migration: bool,
    ) -> Result<Self> {
        initialize_schema(&mut connection, record_lifecycle_migration)?;
        Ok(Self {
            connection: Mutex::new(connection),
        })
    }

    fn connection(&self) -> Result<MutexGuard<'_, Connection>> {
        self.connection
            .lock()
            .map_err(|_| CoreError::storage("SQLite connection mutex poisoned"))
    }

    #[cfg(test)]
    pub(crate) fn test_connection(&self) -> Result<MutexGuard<'_, Connection>> {
        self.connection()
    }
}

fn initialize_schema(connection: &mut Connection, record_lifecycle_migration: bool) -> Result<()> {
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
    apply_migration(
        &transaction,
        "20260810000000_AddMetricRecordingSetting",
        env!("CARGO_PKG_VERSION"),
        METRIC_RECORDING_SETTINGS_SEED_SQL,
    )?;
    if record_lifecycle_migration {
        apply_migration(
            &transaction,
            LIFECYCLE_SCHEMA_MIGRATION_ID,
            env!("CARGO_PKG_VERSION"),
            "",
        )?;
    }
    let has_legacy_conflicts = transaction
        .query_row(
            "SELECT EXISTS(
                 SELECT 1
                 FROM \"AggregatedMetricRecords\"
                 GROUP BY \"ProcessId\", \"ProcessName\", \"AggregationLevel\", \"Timestamp\"
                 HAVING COUNT(*) > 1
                 LIMIT 1
             )",
            [],
            |row| row.get::<_, bool>(0),
        )
        .map_err(|error| sql_error("checking legacy aggregate conflicts", error))?;
    if has_legacy_conflicts {
        tracing::warn!("legacy aggregate bucket conflicts remain for covered reconstruction");
    }
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

    fn rollup_coverage(&self, target: AggregationLevel) -> Result<Option<RollupCoverage>> {
        let connection = self.connection()?;
        let raw = connection
            .query_row(
                "SELECT \"CoveredFrom\", \"CompletedThrough\"
                 FROM \"MetricLifecycleCheckpoints\"
                 WHERE \"TargetLevel\" = ?1",
                [i32::from(target)],
                |row| Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?)),
            )
            .optional()
            .map_err(|error| sql_error("selecting rollup coverage", error))?;
        raw.map(|(covered_from, completed_through)| {
            Ok(RollupCoverage {
                covered_from: from_sqlite_text(&covered_from).map_err(|error| {
                    CoreError::storage(format!("invalid rollup covered_from: {error}"))
                })?,
                completed_through: from_sqlite_text(&completed_through).map_err(|error| {
                    CoreError::storage(format!("invalid rollup completed_through: {error}"))
                })?,
            })
        })
        .transpose()
    }

    fn commit_rollup(
        &self,
        target: AggregationLevel,
        covered_from: DateTime<Utc>,
        bucket_start: DateTime<Utc>,
        bucket_end: DateTime<Utc>,
        records: &[NewAggregatedMetricRecord],
    ) -> Result<RollupCommitResult> {
        let bucket_seconds = match target {
            AggregationLevel::Minute => 60,
            AggregationLevel::Hour => 60 * 60,
            AggregationLevel::Day => 24 * 60 * 60,
        };
        if bucket_start.timestamp_subsec_nanos() != 0
            || bucket_start.timestamp().rem_euclid(bucket_seconds) != 0
        {
            return Err(CoreError::invalid(
                "rollup bucket start is not aligned to the target level",
            ));
        }
        let expected_end = bucket_start
            .checked_add_signed(chrono::Duration::seconds(bucket_seconds))
            .ok_or_else(|| CoreError::invalid("rollup bucket end overflow"))?;
        if bucket_end != expected_end {
            return Err(CoreError::invalid(
                "rollup commit must cover exactly one target-level bucket",
            ));
        }
        if covered_from > bucket_start {
            return Err(CoreError::invalid(
                "rollup coverage start is after the committed bucket",
            ));
        }

        let mut keys = HashSet::with_capacity(records.len());
        for record in records {
            if record.aggregation_level != target {
                return Err(CoreError::invalid("rollup record target level mismatch"));
            }
            if record.timestamp < bucket_start || record.timestamp >= bucket_end {
                return Err(CoreError::invalid(
                    "rollup record timestamp is outside committed bucket",
                ));
            }
            if !keys.insert((
                record.process_id,
                record.process_name.as_str(),
                &record.timestamp,
            )) {
                return Err(CoreError::invalid("duplicate rollup input bucket key"));
            }
        }

        let mut connection = self.connection()?;
        let transaction = connection
            .transaction()
            .map_err(|error| sql_error("starting rollup transaction", error))?;
        let existing = transaction
            .query_row(
                "SELECT \"CoveredFrom\", \"CompletedThrough\"
                 FROM \"MetricLifecycleCheckpoints\"
                 WHERE \"TargetLevel\" = ?1",
                [i32::from(target)],
                |row| Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?)),
            )
            .optional()
            .map_err(|error| sql_error("reading current rollup coverage", error))?
            .map(|(from, through)| -> Result<RollupCoverage> {
                Ok(RollupCoverage {
                    covered_from: from_sqlite_text(&from).map_err(|error| {
                        CoreError::storage(format!("invalid current covered_from: {error}"))
                    })?,
                    completed_through: from_sqlite_text(&through).map_err(|error| {
                        CoreError::storage(format!("invalid current completed_through: {error}"))
                    })?,
                })
            })
            .transpose()?;
        if let Some(existing) = &existing {
            if existing.covered_from != covered_from {
                return Err(CoreError::invalid(
                    "rollup coverage start cannot change after checkpoint creation",
                ));
            }
            let replay_start = existing
                .completed_through
                .checked_sub_signed(chrono::Duration::seconds(bucket_seconds))
                .ok_or_else(|| CoreError::invalid("rollup replay boundary overflow"))?;
            let is_contiguous_extension = bucket_start == existing.completed_through;
            let is_last_bucket_replay =
                bucket_start == replay_start && bucket_end == existing.completed_through;
            if !is_contiguous_extension && !is_last_bucket_replay {
                return Err(CoreError::invalid(
                    "rollup bucket is not contiguous with current coverage",
                ));
            }
        } else if covered_from != bucket_start {
            return Err(CoreError::invalid(
                "initial rollup commit must start at the committed bucket",
            ));
        }

        let bucket_start_text = to_sqlite_text(&bucket_start);
        let bucket_end_text = to_sqlite_text(&bucket_end);
        let replaced = transaction
            .execute(
                "DELETE FROM \"AggregatedMetricRecords\"
                 WHERE \"AggregationLevel\" = ?1
                   AND \"Timestamp\" >= ?2
                   AND \"Timestamp\" < ?3",
                params![i32::from(target), bucket_start_text, bucket_end_text],
            )
            .map_err(|error| sql_error("replacing complete aggregate bucket", error))?;

        for record in records {
            transaction
                .execute(
                    "INSERT INTO \"AggregatedMetricRecords\"
                         (\"ProcessId\", \"ProcessName\", \"AggregationLevel\", \"Timestamp\", \"MetricsJson\")
                     VALUES (?1, ?2, ?3, ?4, ?5)",
                    params![
                        record.process_id,
                        record.process_name,
                        i32::from(target),
                        to_sqlite_text(&record.timestamp),
                        record.metrics_json
                    ],
                )
                .map_err(|error| sql_error("inserting rebuilt aggregate bucket", error))?;
        }

        let bucket_row_count = transaction
            .query_row(
                "SELECT COUNT(*)
                 FROM \"AggregatedMetricRecords\"
                 WHERE \"AggregationLevel\" = ?1
                   AND \"Timestamp\" >= ?2
                   AND \"Timestamp\" < ?3",
                params![i32::from(target), bucket_start_text, bucket_end_text],
                |row| row.get::<_, i64>(0),
            )
            .map_err(|error| sql_error("verifying complete aggregate bucket", error))?;
        if bucket_row_count != records.len() as i64 {
            return Err(CoreError::storage(
                "rebuilt aggregate bucket row count verification failed",
            ));
        }

        let mut verified = 0usize;
        for record in records {
            let timestamp = to_sqlite_text(&record.timestamp);
            let (count, metrics_json) = transaction
                .query_row(
                    "SELECT COUNT(*), MIN(\"MetricsJson\")
                     FROM \"AggregatedMetricRecords\"
                     WHERE \"ProcessId\" = ?1
                       AND \"ProcessName\" = ?2
                       AND \"AggregationLevel\" = ?3
                       AND \"Timestamp\" = ?4",
                    params![
                        record.process_id,
                        record.process_name,
                        i32::from(target),
                        timestamp
                    ],
                    |row| Ok((row.get::<_, i64>(0)?, row.get::<_, Option<String>>(1)?)),
                )
                .map_err(|error| sql_error("verifying rebuilt aggregate bucket", error))?;
            if count != 1 || metrics_json.as_deref() != Some(record.metrics_json.as_str()) {
                return Err(CoreError::storage(
                    "rebuilt aggregate bucket verification failed",
                ));
            }
            verified += 1;
        }

        transaction
            .execute(
                "INSERT INTO \"MetricLifecycleCheckpoints\"
                     (\"TargetLevel\", \"CoveredFrom\", \"CompletedThrough\", \"UpdatedAt\")
                 VALUES (?1, ?2, ?3, ?4)
                 ON CONFLICT(\"TargetLevel\") DO UPDATE SET
                     \"CompletedThrough\" = excluded.\"CompletedThrough\",
                     \"UpdatedAt\" = excluded.\"UpdatedAt\"",
                params![
                    i32::from(target),
                    to_sqlite_text(&covered_from),
                    to_sqlite_text(&bucket_end),
                    to_sqlite_text(&Utc::now())
                ],
            )
            .map_err(|error| sql_error("updating rollup coverage", error))?;
        transaction
            .commit()
            .map_err(|error| sql_error("committing rollup transaction", error))?;

        Ok(RollupCommitResult {
            inserted: records.len(),
            replaced,
            verified,
            coverage: RollupCoverage {
                covered_from,
                completed_through: bucket_end,
            },
        })
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
             WHERE \"Timestamp\" >= ?1
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
               AND \"Timestamp\" >= ?2
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

    fn purge_raw_batch(
        &self,
        window: &PurgeWindow,
        cursor: Option<&PurgeCursor>,
        limit: usize,
    ) -> Result<PurgeBatchResult> {
        if limit == 0 {
            return Err(CoreError::invalid("raw purge batch limit must be positive"));
        }
        let limit = i64::try_from(limit)
            .map_err(|_| CoreError::invalid("raw purge batch limit exceeds i64"))?;
        let covered_from = window.covered_from().map(to_sqlite_text);
        let cutoff = to_sqlite_text(window.cutoff());
        let cursor_timestamp = cursor.map(|cursor| to_sqlite_text(&cursor.timestamp));
        let cursor_id = cursor.map(|cursor| cursor.id);
        let mut connection = self.connection()?;
        let transaction = connection
            .transaction()
            .map_err(|error| sql_error("starting raw purge transaction", error))?;
        let candidates = {
            let mut statement = transaction
                .prepare(
                    "SELECT \"Timestamp\", \"Id\"
                     FROM \"ProcessMetricRecords\"
                     WHERE (?1 IS NULL OR \"Timestamp\" >= ?1)
                       AND \"Timestamp\" < ?2
                       AND (
                           ?3 IS NULL
                           OR \"Timestamp\" > ?3
                           OR (\"Timestamp\" = ?3 AND \"Id\" > ?4)
                       )
                     ORDER BY \"Timestamp\", \"Id\"
                     LIMIT ?5",
                )
                .map_err(|error| sql_error("preparing raw purge candidates", error))?;
            let rows = statement
                .query_map(
                    params![covered_from, cutoff, cursor_timestamp, cursor_id, limit],
                    |row| {
                        Ok(PurgeCursor {
                            timestamp: timestamp_column(row, 0)?,
                            id: row.get(1)?,
                        })
                    },
                )
                .map_err(|error| sql_error("querying raw purge candidates", error))?;
            let mut candidates = Vec::new();
            for row in rows {
                candidates
                    .push(row.map_err(|error| sql_error("reading raw purge candidate", error))?);
            }
            candidates
        };
        let mut deleted = 0usize;
        for candidate in &candidates {
            deleted += transaction
                .execute(
                    "DELETE FROM \"ProcessMetricRecords\" WHERE \"Id\" = ?1",
                    [candidate.id],
                )
                .map_err(|error| sql_error("deleting raw purge candidate", error))?;
        }
        transaction
            .commit()
            .map_err(|error| sql_error("committing raw purge transaction", error))?;
        let exhausted = candidates.len() < usize::try_from(limit).unwrap_or(usize::MAX);
        Ok(PurgeBatchResult {
            deleted: u64::try_from(deleted)
                .map_err(|_| CoreError::storage("raw purge count exceeds u64"))?,
            next_cursor: candidates.last().cloned(),
            exhausted,
        })
    }

    fn purge_aggregate_batch(
        &self,
        level: AggregationLevel,
        window: &PurgeWindow,
        cursor: Option<&PurgeCursor>,
        limit: usize,
    ) -> Result<PurgeBatchResult> {
        if limit == 0 {
            return Err(CoreError::invalid(
                "aggregate purge batch limit must be positive",
            ));
        }
        let limit = i64::try_from(limit)
            .map_err(|_| CoreError::invalid("aggregate purge batch limit exceeds i64"))?;
        let covered_from = window.covered_from().map(to_sqlite_text);
        let cutoff = to_sqlite_text(window.cutoff());
        let cursor_timestamp = cursor.map(|cursor| to_sqlite_text(&cursor.timestamp));
        let cursor_id = cursor.map(|cursor| cursor.id);
        let mut connection = self.connection()?;
        let transaction = connection
            .transaction()
            .map_err(|error| sql_error("starting aggregate purge transaction", error))?;
        let candidates = {
            let mut statement = transaction
                .prepare(
                    "SELECT \"Timestamp\", \"Id\"
                     FROM \"AggregatedMetricRecords\"
                     WHERE \"AggregationLevel\" = ?1
                       AND (?2 IS NULL OR \"Timestamp\" >= ?2)
                       AND \"Timestamp\" < ?3
                       AND (
                           ?4 IS NULL
                           OR \"Timestamp\" > ?4
                           OR (\"Timestamp\" = ?4 AND \"Id\" > ?5)
                       )
                     ORDER BY \"Timestamp\", \"Id\"
                     LIMIT ?6",
                )
                .map_err(|error| sql_error("preparing aggregate purge candidates", error))?;
            let rows = statement
                .query_map(
                    params![
                        i32::from(level),
                        covered_from,
                        cutoff,
                        cursor_timestamp,
                        cursor_id,
                        limit
                    ],
                    |row| {
                        Ok(PurgeCursor {
                            timestamp: timestamp_column(row, 0)?,
                            id: row.get(1)?,
                        })
                    },
                )
                .map_err(|error| sql_error("querying aggregate purge candidates", error))?;
            let mut candidates = Vec::new();
            for row in rows {
                candidates.push(
                    row.map_err(|error| sql_error("reading aggregate purge candidate", error))?,
                );
            }
            candidates
        };
        let mut deleted = 0usize;
        for candidate in &candidates {
            deleted += transaction
                .execute(
                    "DELETE FROM \"AggregatedMetricRecords\" WHERE \"Id\" = ?1",
                    [candidate.id],
                )
                .map_err(|error| sql_error("deleting aggregate purge candidate", error))?;
        }
        transaction
            .commit()
            .map_err(|error| sql_error("committing aggregate purge transaction", error))?;
        let exhausted = candidates.len() < usize::try_from(limit).unwrap_or(usize::MAX);
        Ok(PurgeBatchResult {
            deleted: u64::try_from(deleted)
                .map_err(|_| CoreError::storage("aggregate purge count exceeds u64"))?,
            next_cursor: candidates.last().cloned(),
            exhausted,
        })
    }

    fn checkpoint_wal(&self) -> Result<WalCheckpointResult> {
        let connection = self.connection()?;
        let (busy, log_frames, checkpointed_frames) = connection
            .query_row("PRAGMA wal_checkpoint(TRUNCATE)", [], |row| {
                Ok((
                    row.get::<_, i64>(0)?,
                    row.get::<_, i64>(1)?,
                    row.get::<_, i64>(2)?,
                ))
            })
            .map_err(|error| sql_error("checkpointing SQLite WAL", error))?;
        Ok(WalCheckpointResult {
            busy: u32::try_from(busy).map_err(|_| CoreError::storage("invalid WAL busy result"))?,
            log_frames: u32::try_from(log_frames.max(0))
                .map_err(|_| CoreError::storage("invalid WAL log frame count"))?,
            checkpointed_frames: u32::try_from(checkpointed_frames.max(0))
                .map_err(|_| CoreError::storage("invalid WAL checkpointed frame count"))?,
        })
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

    fn after(seconds: i64) -> DateTime<Utc> {
        at(0) + chrono::Duration::seconds(seconds)
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
            store.commit_rollup(
                AggregationLevel::Minute,
                at(0),
                at(0),
                after(60),
                &aggregates,
            ),
            Err(CoreError::Storage(_))
        ));
        assert_eq!(
            store.rollup_coverage(AggregationLevel::Minute).unwrap(),
            None
        );
        assert!(store
            .history_aggregated(1, AggregationLevel::Minute, None, None)
            .unwrap()
            .is_empty());
    }

    #[test]
    fn aggregation_queries_preserve_history_and_half_open_worker_windows() {
        let store = SqliteMetricStore::open_in_memory().unwrap();
        store
            .save_process_metrics(&[
                raw_record(1, "raw", None, at(0), "{}"),
                raw_record(1, "raw", None, at(1), "{}"),
                raw_record(1, "raw", None, at(2), "{}"),
                raw_record(1, "raw", None, at(3), "{}"),
            ])
            .unwrap();

        let minute = [
            aggregate_record(1, "raw", AggregationLevel::Minute, at(0), "{}"),
            aggregate_record(2, "Zulu", AggregationLevel::Minute, at(1), "{}"),
            aggregate_record(1, "Alpha", AggregationLevel::Minute, at(1), "{}"),
            aggregate_record(1, "raw", AggregationLevel::Minute, at(2), "{}"),
            aggregate_record(1, "raw", AggregationLevel::Minute, at(3), "{}"),
        ];
        store
            .commit_rollup(AggregationLevel::Minute, at(0), at(0), after(60), &minute)
            .unwrap();
        store
            .commit_rollup(
                AggregationLevel::Hour,
                at(0),
                at(0),
                after(60 * 60),
                &[aggregate_record(
                    1,
                    "raw",
                    AggregationLevel::Hour,
                    at(2),
                    "{}",
                )],
            )
            .unwrap();

        assert_eq!(store.earliest_raw_timestamp().unwrap(), Some(at(0)));
        assert_eq!(
            store
                .earliest_aggregate_timestamp(AggregationLevel::Minute)
                .unwrap(),
            Some(at(0))
        );
        assert_eq!(
            store.rollup_coverage(AggregationLevel::Minute).unwrap(),
            Some(RollupCoverage {
                covered_from: at(0),
                completed_through: after(60),
            })
        );
        assert_eq!(store.rollup_coverage(AggregationLevel::Day).unwrap(), None);

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
        assert_eq!(first_raw_batch[0].timestamp, at(0));
        let second_raw_batch = store
            .raw_batch_for_aggregation(at(0), at(3), first_raw_batch[0].id, 10)
            .unwrap();
        assert_eq!(
            second_raw_batch
                .iter()
                .map(|record| record.timestamp)
                .collect::<Vec<_>>(),
            [at(1), at(2)]
        );

        let first_rollup_batch = store
            .aggregate_batch_for_rollup(AggregationLevel::Minute, at(0), at(3), 0, 2)
            .unwrap();
        assert_eq!(
            first_rollup_batch
                .iter()
                .map(|record| record.process_name.as_str())
                .collect::<Vec<_>>(),
            ["raw", "Zulu"]
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
        assert_eq!(second_rollup_batch.len(), 2);
        assert_eq!(second_rollup_batch[0].process_name, "Alpha");
        assert_eq!(second_rollup_batch[1].timestamp, at(2));
    }

    #[test]
    fn bounded_tier_purges_enforce_coverage_floor_cutoff_and_level() {
        let store = SqliteMetricStore::open_in_memory().unwrap();
        store
            .save_process_metrics(&[
                raw_record(1, "fringe", None, at(0), "{}"),
                raw_record(1, "eligible", None, at(1), "{}"),
                raw_record(1, "cutoff", None, at(2), "{}"),
            ])
            .unwrap();
        store
            .commit_rollup(
                AggregationLevel::Minute,
                at(0),
                at(0),
                after(60),
                &[
                    aggregate_record(1, "fringe", AggregationLevel::Minute, at(0), "{}"),
                    aggregate_record(1, "eligible", AggregationLevel::Minute, at(1), "{}"),
                    aggregate_record(1, "cutoff", AggregationLevel::Minute, at(2), "{}"),
                ],
            )
            .unwrap();
        store
            .commit_rollup(
                AggregationLevel::Hour,
                at(0),
                at(0),
                after(60 * 60),
                &[aggregate_record(
                    1,
                    "other-level",
                    AggregationLevel::Hour,
                    at(0),
                    "{}",
                )],
            )
            .unwrap();
        let window = PurgeWindow::covered(at(1), at(2));

        let raw = store.purge_raw_batch(&window, None, 1).unwrap();
        assert_eq!(raw.deleted, 1);
        assert!(!raw.exhausted);
        let raw_done = store
            .purge_raw_batch(&window, raw.next_cursor.as_ref(), 1)
            .unwrap();
        assert_eq!(raw_done.deleted, 0);
        assert!(raw_done.exhausted);

        let minute = store
            .purge_aggregate_batch(AggregationLevel::Minute, &window, None, 1)
            .unwrap();
        assert_eq!(minute.deleted, 1);
        let raw_rows = store.history_raw(1, None, None).unwrap();
        assert_eq!(raw_rows.len(), 2);
        assert!(raw_rows.iter().any(|row| row.timestamp == at(0)));
        assert!(raw_rows.iter().any(|row| row.timestamp == at(2)));
        let minute_rows = store
            .history_aggregated(1, AggregationLevel::Minute, None, None)
            .unwrap();
        assert_eq!(minute_rows.len(), 2);
        assert!(minute_rows.iter().any(|row| row.timestamp == at(0)));
        assert!(minute_rows.iter().any(|row| row.timestamp == at(2)));
        assert_eq!(
            store
                .history_aggregated(1, AggregationLevel::Hour, None, None)
                .unwrap()
                .len(),
            1
        );
        let _ = store.checkpoint_wal().unwrap();
        store.health_check().unwrap();
    }

    #[test]
    fn exact_bucket_replacement_is_idempotent_and_rebuilds_conflicts() {
        let store = SqliteMetricStore::open_in_memory().unwrap();
        {
            let connection = store.connection().unwrap();
            for metrics_json in [r#"{"old":1}"#, r#"{"old":2}"#] {
                connection
                    .execute(
                        "INSERT INTO \"AggregatedMetricRecords\"
                             (\"ProcessId\", \"ProcessName\", \"AggregationLevel\", \"Timestamp\", \"MetricsJson\")
                         VALUES (?1, ?2, ?3, ?4, ?5)",
                        params![
                            7,
                            "same-name",
                            i32::from(AggregationLevel::Minute),
                            to_sqlite_text(&at(0)),
                            metrics_json
                        ],
                    )
                    .unwrap();
            }
        }
        let rebuilt = aggregate_record(
            7,
            "same-name",
            AggregationLevel::Minute,
            at(0),
            r#"{"new":3}"#,
        );
        let first = store
            .commit_rollup(
                AggregationLevel::Minute,
                at(0),
                at(0),
                after(60),
                std::slice::from_ref(&rebuilt),
            )
            .unwrap();
        assert_eq!(first.replaced, 2);
        assert_eq!(first.verified, 1);
        let second = store
            .commit_rollup(
                AggregationLevel::Minute,
                at(0),
                at(0),
                after(60),
                std::slice::from_ref(&rebuilt),
            )
            .unwrap();
        assert_eq!(second.replaced, 1);
        let rows = store
            .history_aggregated(7, AggregationLevel::Minute, None, None)
            .unwrap();
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].metrics_json, r#"{"new":3}"#);
    }

    #[test]
    fn empty_rollup_replaces_complete_legacy_bucket_before_advancing_coverage() {
        let store = SqliteMetricStore::open_in_memory().unwrap();
        {
            let connection = store.connection().unwrap();
            for metrics_json in [r#"{"old":1}"#, r#"{"old":2}"#] {
                connection
                    .execute(
                        "INSERT INTO \"AggregatedMetricRecords\"
                             (\"ProcessId\", \"ProcessName\", \"AggregationLevel\", \"Timestamp\", \"MetricsJson\")
                         VALUES (7, 'legacy', 1, ?1, ?2)",
                        params![to_sqlite_text(&at(0)), metrics_json],
                    )
                    .unwrap();
            }
            connection
                .execute(
                    "INSERT INTO \"AggregatedMetricRecords\"
                         (\"ProcessId\", \"ProcessName\", \"AggregationLevel\", \"Timestamp\", \"MetricsJson\")
                     VALUES (8, 'outside', 1, ?1, '{}')",
                    [to_sqlite_text(&after(60))],
                )
                .unwrap();
        }

        let commit = store
            .commit_rollup(AggregationLevel::Minute, at(0), at(0), after(60), &[])
            .unwrap();

        assert_eq!(commit.replaced, 2);
        assert_eq!(commit.inserted, 0);
        assert_eq!(commit.verified, 0);
        assert_eq!(
            store.rollup_coverage(AggregationLevel::Minute).unwrap(),
            Some(RollupCoverage {
                covered_from: at(0),
                completed_through: after(60),
            })
        );
        assert!(store
            .aggregations(AggregationLevel::Minute, at(0), after(59))
            .unwrap()
            .is_empty());
        assert_eq!(
            store
                .history_aggregated(8, AggregationLevel::Minute, None, None)
                .unwrap()
                .len(),
            1
        );
    }

    #[test]
    fn forward_gap_rollup_preserves_destination_rows_and_coverage() {
        let store = SqliteMetricStore::open_in_memory().unwrap();
        store
            .commit_rollup(
                AggregationLevel::Minute,
                at(0),
                at(0),
                after(60),
                &[aggregate_record(
                    1,
                    "first",
                    AggregationLevel::Minute,
                    at(0),
                    "{}",
                )],
            )
            .unwrap();
        {
            let connection = store.connection().unwrap();
            connection
                .execute(
                    "INSERT INTO \"AggregatedMetricRecords\"
                         (\"ProcessId\", \"ProcessName\", \"AggregationLevel\", \"Timestamp\", \"MetricsJson\")
                     VALUES (9, 'gap-target', 1, ?1, '{\"legacy\":true}')",
                    [to_sqlite_text(&after(120))],
                )
                .unwrap();
        }
        let coverage_before = store.rollup_coverage(AggregationLevel::Minute).unwrap();

        assert!(matches!(
            store.commit_rollup(AggregationLevel::Minute, at(0), after(120), after(180), &[],),
            Err(CoreError::InvalidArgument(_))
        ));
        assert_eq!(
            store.rollup_coverage(AggregationLevel::Minute).unwrap(),
            coverage_before
        );
        let rows = store
            .history_aggregated(9, AggregationLevel::Minute, None, None)
            .unwrap();
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].metrics_json, r#"{"legacy":true}"#);
    }

    #[test]
    fn database_open_preserves_untouched_legacy_conflicts() {
        let path = std::env::temp_dir().join(format!("xhm-conflicts-{}.db", Uuid::new_v4()));
        {
            let store = SqliteMetricStore::open(&path).unwrap();
            let connection = store.connection().unwrap();
            for metrics_json in [r#"{"old":1}"#, r#"{"old":2}"#] {
                connection
                    .execute(
                        "INSERT INTO \"AggregatedMetricRecords\"
                             (\"ProcessId\", \"ProcessName\", \"AggregationLevel\", \"Timestamp\", \"MetricsJson\")
                         VALUES (7, 'legacy', 1, ?1, ?2)",
                        params![to_sqlite_text(&at(0)), metrics_json],
                    )
                    .unwrap();
            }
        }
        {
            let reopened = SqliteMetricStore::open(&path).unwrap();
            assert_eq!(
                reopened
                    .history_aggregated(7, AggregationLevel::Minute, None, None)
                    .unwrap()
                    .len(),
                2
            );
        }
        let _ = fs::remove_file(&path);
        let _ = fs::remove_file(format!("{}-wal", path.display()));
        let _ = fs::remove_file(format!("{}-shm", path.display()));
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
        assert_eq!(initial_settings.len(), 18);
        assert!(initial_settings.windows(2).all(|pair| {
            (&pair[0].category, &pair[0].key) <= (&pair[1].category, &pair[1].key)
        }));
        assert!(initial_settings.iter().any(|setting| {
            setting.category == "DataCollection"
                && setting.key == "RecordMetrics"
                && setting.value == "false"
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
            assert_eq!(settings.len(), 18);
            assert!(!settings.iter().any(|setting| setting.key == "WebPort"));
            assert!(settings.iter().any(|setting| {
                setting.category == "Monitoring" && setting.key == "MonitorCpu"
            }));
            assert!(settings.iter().any(|setting| {
                setting.category == "DataCollection"
                    && setting.key == "RecordMetrics"
                    && setting.value == "false"
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
                    "IX_AggregatedMetricRecords_AggregationLevel_Timestamp_Id",
                    "IX_AggregatedMetricRecords_FullBucket",
                    "IX_AggregatedMetricRecords_ProcessId_AggregationLevel_Timestamp",
                    "IX_AggregatedMetricRecords_ProcessId_Timestamp",
                    "IX_ApplicationSettings_Category_Key",
                    "IX_ProcessMetricRecords_ProcessId_Timestamp",
                    "IX_ProcessMetricRecords_Timestamp",
                    "IX_ProcessMetricRecords_Timestamp_Id",
                ]
            );

            let migration_count = connection
                .query_row(
                    "SELECT COUNT(*) FROM \"__EFMigrationsHistory\"",
                    [],
                    |row| row.get::<_, i64>(0),
                )
                .unwrap();
            assert_eq!(migration_count, 10);
            let lifecycle_product_version = connection
                .query_row(
                    "SELECT \"ProductVersion\" FROM \"__EFMigrationsHistory\"
                     WHERE \"MigrationId\" = ?1",
                    [LIFECYCLE_SCHEMA_MIGRATION_ID],
                    |row| row.get::<_, String>(0),
                )
                .unwrap();
            assert_eq!(lifecycle_product_version, env!("CARGO_PKG_VERSION"));
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
    fn fresh_install_records_lifecycle_marker_and_skips_future_rebuilds() {
        let root = std::env::temp_dir().join(format!("xhm-fresh-install-{}", Uuid::new_v4()));
        fs::create_dir_all(&root).unwrap();
        let path = root.join("xhmonitor.db");

        assert!(matches!(
            prepare_legacy_database(&path).unwrap(),
            LegacyDatabasePreparation::NotRequired
        ));
        {
            let store = SqliteMetricStore::open(&path).unwrap();
            let connection = store.test_connection().unwrap();
            let product_version = connection
                .query_row(
                    "SELECT \"ProductVersion\" FROM \"__EFMigrationsHistory\"
                     WHERE \"MigrationId\" = ?1",
                    [LIFECYCLE_SCHEMA_MIGRATION_ID],
                    |row| row.get::<_, String>(0),
                )
                .unwrap();
            assert_eq!(product_version, env!("CARGO_PKG_VERSION"));
        }
        assert!(matches!(
            prepare_legacy_database(&path).unwrap(),
            LegacyDatabasePreparation::NotRequired
        ));

        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn legacy_database_is_rebuilt_once_without_copying_metric_history() {
        let root = std::env::temp_dir().join(format!("xhm-legacy-rebuild-{}", Uuid::new_v4()));
        fs::create_dir_all(&root).unwrap();
        let path = root.join("xhmonitor.db");
        {
            let connection = Connection::open(&path).unwrap();
            connection
                .execute_batch(
                    r#"
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE "ProcessMetricRecords" (
                        "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                        "ProcessId" INTEGER NOT NULL,
                        "ProcessName" TEXT NOT NULL,
                        "CommandLine" TEXT NULL,
                        "Timestamp" TEXT NOT NULL,
                        "MetricsJson" TEXT NOT NULL
                    );
                    INSERT INTO "ProcessMetricRecords"
                        ("ProcessId", "ProcessName", "Timestamp", "MetricsJson")
                    VALUES (7, 'legacy', '2026-07-26 12:00:00', '{}');

                    CREATE TABLE "ApplicationSettings" (
                        "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                        "Category" TEXT NOT NULL,
                        "Key" TEXT NOT NULL,
                        "Value" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL
                    );
                    CREATE UNIQUE INDEX "IX_ApplicationSettings_Category_Key"
                        ON "ApplicationSettings" ("Category", "Key");
                    INSERT INTO "ApplicationSettings"
                        ("Category", "Key", "Value", "CreatedAt", "UpdatedAt")
                    VALUES
                        ('Appearance', 'Opacity', '73', '2025-01-01 00:00:00', '2026-01-01 00:00:00'),
                        ('Custom', 'Feature', 'enabled', '2025-02-01 00:00:00', '2026-02-01 00:00:00'),
                        ('System', 'WebPort', '9999', '2025-03-01 00:00:00', '2026-03-01 00:00:00');

                    CREATE TABLE "AlertConfigurations" (
                        "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                        "MetricId" TEXT NOT NULL,
                        "Threshold" REAL NOT NULL,
                        "IsEnabled" INTEGER NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL
                    );
                    INSERT INTO "AlertConfigurations"
                        ("Id", "MetricId", "Threshold", "IsEnabled", "CreatedAt", "UpdatedAt")
                    VALUES (1, 'cpu', 77.5, 0, '2025-04-01 00:00:00', '2026-04-01 00:00:00');

                    CREATE TABLE "__EFMigrationsHistory" (
                        "MigrationId" TEXT PRIMARY KEY,
                        "ProductVersion" TEXT NOT NULL
                    );
                    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20251221075010_InitialCreate', '8.0.22');
                    "#,
                )
                .unwrap();
        }

        let LegacyDatabasePreparation::Rebuilt(rebuild) = prepare_legacy_database(&path).unwrap()
        else {
            panic!("legacy database should be rebuilt");
        };
        assert_eq!(rebuild.settings_copied, 2);
        assert_eq!(rebuild.alerts_copied, 1);
        assert!(rebuild.backup_path.is_file());
        {
            let store = SqliteMetricStore::open(&path).unwrap();
            assert_eq!(store.earliest_raw_timestamp().unwrap(), None);
            let settings = store.list_settings().unwrap();
            assert!(settings.iter().any(|setting| {
                setting.category == "Appearance"
                    && setting.key == "Opacity"
                    && setting.value == "73"
            }));
            assert!(settings.iter().any(|setting| {
                setting.category == "Custom"
                    && setting.key == "Feature"
                    && setting.value == "enabled"
            }));
            assert!(!settings.iter().any(|setting| setting.key == "WebPort"));
            let cpu = store
                .list_alerts()
                .unwrap()
                .into_iter()
                .find(|alert| alert.id == 1)
                .unwrap();
            assert_eq!(cpu.threshold, 77.5);
            assert!(!cpu.is_enabled);
        }
        finalize_legacy_database_rebuild(rebuild).unwrap();

        assert!(matches!(
            prepare_legacy_database(&path).unwrap(),
            LegacyDatabasePreparation::NotRequired
        ));
        let connection = Connection::open(&path).unwrap();
        assert!(migration_exists(&connection, LIFECYCLE_SCHEMA_MIGRATION_ID).unwrap());
        drop(connection);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn busy_legacy_database_starts_without_marker_and_rebuilds_after_release() {
        let root = std::env::temp_dir().join(format!("xhm-legacy-rebuild-busy-{}", Uuid::new_v4()));
        fs::create_dir_all(&root).unwrap();
        let path = root.join("xhmonitor.db");
        let reader = Connection::open(&path).unwrap();
        reader
            .execute_batch(
                r#"
                PRAGMA journal_mode=WAL;
                CREATE TABLE "ProcessMetricRecords" (
                    "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                    "ProcessId" INTEGER NOT NULL,
                    "ProcessName" TEXT NOT NULL,
                    "CommandLine" TEXT NULL,
                    "Timestamp" TEXT NOT NULL,
                    "MetricsJson" TEXT NOT NULL
                );
                INSERT INTO "ProcessMetricRecords"
                    ("ProcessId", "ProcessName", "Timestamp", "MetricsJson")
                VALUES (7, 'legacy', '2026-07-26 12:00:00', '{}');
                CREATE TABLE "MetricLifecycleCheckpoints" (
                    "TargetLevel" INTEGER NOT NULL PRIMARY KEY,
                    "CoveredFrom" TEXT NOT NULL,
                    "CompletedThrough" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('20251221075010_InitialCreate', '8.0.22');
                BEGIN;
                SELECT COUNT(*) FROM "ProcessMetricRecords";
                "#,
            )
            .unwrap();
        {
            let writer = Connection::open(&path).unwrap();
            writer
                .execute(
                    "INSERT INTO \"ProcessMetricRecords\"
                         (\"ProcessId\", \"ProcessName\", \"Timestamp\", \"MetricsJson\")
                     VALUES (8, 'wal-frame', '2026-07-26 12:01:00', '{}')",
                    [],
                )
                .unwrap();
        }

        assert!(matches!(
            prepare_legacy_database(&path).unwrap(),
            LegacyDatabasePreparation::Deferred
        ));
        {
            let store = SqliteMetricStore::open_deferred_legacy(&path).unwrap();
            let connection = store.test_connection().unwrap();
            assert!(!migration_exists(&connection, LIFECYCLE_SCHEMA_MIGRATION_ID).unwrap());
            assert!(table_exists(&connection, "MetricLifecycleCheckpoints").unwrap());
            let raw_count = connection
                .query_row("SELECT COUNT(*) FROM \"ProcessMetricRecords\"", [], |row| {
                    row.get::<_, i64>(0)
                })
                .unwrap();
            assert_eq!(raw_count, 2);
        }

        reader.execute_batch("COMMIT").unwrap();
        drop(reader);
        let LegacyDatabasePreparation::Rebuilt(rebuild) = prepare_legacy_database(&path).unwrap()
        else {
            panic!("released legacy database should be rebuilt");
        };
        {
            let store = SqliteMetricStore::open(&path).unwrap();
            assert_eq!(store.earliest_raw_timestamp().unwrap(), None);
        }
        finalize_legacy_database_rebuild(rebuild).unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn legacy_rebuild_failure_leaves_original_database_in_place() {
        let root =
            std::env::temp_dir().join(format!("xhm-legacy-rebuild-failure-{}", Uuid::new_v4()));
        fs::create_dir_all(&root).unwrap();
        let path = root.join("xhmonitor.db");
        {
            let connection = Connection::open(&path).unwrap();
            connection
                .execute_batch(
                    r#"
                    CREATE TABLE "ProcessMetricRecords" ("Id" INTEGER PRIMARY KEY);
                    CREATE TABLE "ApplicationSettings" (
                        "Category" TEXT NOT NULL,
                        "Key" TEXT NOT NULL,
                        "Value" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL
                    );
                    INSERT INTO "ApplicationSettings"
                        ("Category", "Key", "Value", "CreatedAt")
                    VALUES ('Custom', 'Broken', 'value', '2025-01-01 00:00:00');
                    "#,
                )
                .unwrap();
        }

        let error = prepare_legacy_database(&path).unwrap_err();
        assert!(error.to_string().contains("UpdatedAt"));
        assert!(path.is_file());
        assert!(!root.join(LEGACY_BACKUP_FILE_NAME).exists());
        assert!(!root.join(REBUILD_FILE_NAME).exists());
        let connection = Connection::open(&path).unwrap();
        assert!(table_exists(&connection, "ProcessMetricRecords").unwrap());
        drop(connection);
        fs::remove_dir_all(root).unwrap();
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
