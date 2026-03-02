# XhMonitor.Service

## Purpose and Role

XhMonitor.Service is the core backend service of the XhMonitor system. It runs as a Windows background process (WinExe in Release mode), responsible for:

- Collecting process-level performance metrics (CPU, memory, GPU, VRAM) at configurable intervals
- Collecting real-time system-level metrics (total CPU, GPU, memory, network, disk, power)
- Persisting all metrics to a local SQLite database via Entity Framework Core
- Pushing real-time data to connected clients via SignalR
- Providing HTTP REST API endpoints for Desktop and Web front ends to query historical data and manage configuration
- Running background aggregation tasks (raw -> minute -> hour -> day) and database cleanup

Single-instance enforcement is implemented via a named Mutex at startup.

## Technology Stack

| Component | Technology |
|-----------|-----------|
| Runtime | .NET 8 (net8.0), ASP.NET Core Web host |
| Output type | WinExe (Release), Console (Debug) |
| Database | SQLite via EF Core 8 + Microsoft.Data.Sqlite 10 |
| Real-time push | ASP.NET Core SignalR |
| Logging | Serilog (Console + Debug + File sinks) |
| Windows service | Microsoft.Extensions.Hosting.WindowsServices |
| Hardware monitoring | LibreHardwareMonitor (via XhMonitor.Core) |
| Power control | RyzenAdj CLI wrapper (AMD GPU only) |

## Directory Structure

```
XhMonitor.Service/
├── Program.cs                    -- Entry point, DI setup, Kestrel/CORS/SignalR configuration
├── Worker.cs                     -- Main background service: process & system metric collection loop
├── appsettings.json              -- Default configuration
├── appsettings.Development.json  -- Development overrides
├── Configuration/                -- Strongly-typed settings classes
│   ├── AggregationSettings.cs    -- Aggregation batch size
│   ├── ConfigurationValidator.cs -- Startup config validation
│   ├── DatabaseSettings.cs       -- Retention days, cleanup interval
│   └── MonitorSettings.cs        -- Collection intervals
├── Controllers/                  -- HTTP REST API controllers
│   ├── ConfigController.cs       -- Settings, alerts, metric metadata, health, admin status
│   ├── MetricsController.cs      -- Query latest/history/processes/aggregations
│   ├── PowerController.cs        -- Power scheme status and switching (AMD only)
│   └── WidgetConfigController.cs -- Desktop widget per-metric click configuration
├── Core/                         -- Service-layer domain logic
│   ├── BuiltInMetricProviderFactory.cs          -- Factory for built-in PerformanceCounter providers
│   ├── LibreHardwareMonitorProviderFactory.cs   -- Factory for LHM-based providers
│   ├── MetricProviderRegistry.cs                -- Discovers and loads metric provider plugins
│   ├── PerformanceMonitor.cs                    -- Orchestrates parallel per-process metric collection
│   ├── ProcessMetadataStore.cs                  -- In-memory cache for process display names / command lines
│   └── ProcessScanner.cs                        -- Scans running processes, filters by keywords
├── Data/                         -- EF Core data layer
│   ├── MonitorDbContext.cs                    -- DbContext with WAL mode, seed data, UTC converters
│   ├── MonitorDbContextDesignTimeFactory.cs   -- EF design-time factory for migrations
│   ├── Repositories/                          -- Repository implementations
│   └── SeedDataIds.cs                         -- Fixed IDs for seed data to avoid migration drift
├── Hubs/
│   └── MetricsHub.cs             -- SignalR hub: sends process metadata snapshot on client connect
├── Models/
│   ├── MetricMetadata.cs         -- DTO for metric display info (name, unit, color, icon)
│   └── SettingsDto.cs            -- DTO for settings update requests
└── Workers/
    ├── AggregationWorker.cs      -- Periodic aggregation: raw->minute, minute->hour, hour->day
    └── DatabaseCleanupWorker.cs  -- Periodic deletion of records older than RetentionDays + VACUUM
```

## Key Classes

### Worker (`Worker.cs`)

The main `BackgroundService` that drives the entire monitoring lifecycle.

Startup phases (sequential):
1. Hardware limit detection (MaxMemory, MaxVram) -- pushes `ReceiveHardwareLimits` to all clients
2. Performance counter warmup -- calls `ISystemMetricProvider.WarmupAsync()`
3. Parallel background tasks launch: VRAM re-check loop (hourly), system usage loop, process push loop
4. First process data collection

Main loops after startup:

| Loop | Interval | Action |
|------|----------|--------|
| Process collection | `Monitor:IntervalSeconds` (default 5s) | `PerformanceMonitor.CollectAllAsync()`, save to DB, push via SignalR |
| System usage | `Monitor:SystemUsageIntervalSeconds` (default 1s) | `ISystemMetricProvider.GetSystemUsageAsync()`, push via SignalR |
| VRAM limit check | 1 hour | Re-query `GetHardwareLimitsAsync()`, push updated limits |
| Process push | Channel-based | Dequeues `ProcessSnapshot` and pushes `ReceiveProcessMetrics` to all clients |

A bounded `Channel<ProcessSnapshot>` (capacity 1) decouples collection from push. Old snapshots are dropped when the channel is full.

### PerformanceMonitor (`Core/PerformanceMonitor.cs`)

Orchestrates parallel metric collection across all matching processes:

- Calls `ProcessScanner.ScanProcesses()` to get filtered process list
- Runs `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = 4`
- For each process, calls all registered `IMetricProvider` instances (max 8 concurrent via semaphore)
- Each provider call has a 2-second timeout; errors are silently skipped
- Returns `List<ProcessMetrics>`

### MetricProviderRegistry (`Core/MetricProviderRegistry.cs`)

Discovers and manages `IMetricProvider` instances. Supports plugin loading from the configured `plugins/` directory. Falls back to built-in providers (BuiltIn or LibreHardwareMonitor based on `MetricProviders:PreferLibreHardwareMonitor`).

### ProcessScanner (`Core/ProcessScanner.cs`)

Scans running Windows processes and filters by keyword list. Keywords are loaded from the database (`DataCollection.ProcessKeywords` setting). Supports hot-reload via `ReloadKeywordsAsync()` called by `ConfigController` when settings are saved.

### ProcessMetadataStore (`Core/ProcessMetadataStore.cs`)

In-memory store for process metadata (display name, command line). Tracks changes and provides delta updates. On new SignalR client connect, the full snapshot is sent immediately.

### AggregationWorker (`Workers/AggregationWorker.cs`)

Runs every 1 minute. Performs three sequential aggregation passes using a watermark-based incremental strategy:

```
Raw records  -->  Minute aggregations  -->  Hour aggregations  -->  Day aggregations
```

Each metric value is aggregated into `{min, max, avg, sum, count, unit}` and stored as JSON. Batch size is configurable (default 2000, range 100-50000).

### DatabaseCleanupWorker (`Workers/DatabaseCleanupWorker.cs`)

Runs at configurable intervals (default every 24 hours). Deletes records from both `ProcessMetricRecords` and `AggregatedMetricRecords` older than `RetentionDays`. Executes `VACUUM` after deletion to reclaim space.

## HTTP API Endpoints

All routes are prefixed with `/api/v1/`.

### MetricsController -- `GET /api/v1/metrics`

| Method | Path | Query Params | Description |
|--------|------|-------------|-------------|
| GET | `/metrics/latest` | `processId?`, `processName?`, `keyword?` | Latest snapshot records, optionally filtered |
| GET | `/metrics/history` | `processId`, `from?`, `to?`, `aggregation=raw\|minute\|hour\|day` | Per-process time-series, raw or aggregated |
| GET | `/metrics/processes` | `from?`, `to?`, `keyword?` | List distinct processes seen in time range with record counts |
| GET | `/metrics/aggregations` | `from`, `to`, `aggregation=minute\|hour\|day` | All process aggregations in time range |

### ConfigController -- `GET /api/v1/config`

| Method | Path | Body | Description |
|--------|------|------|-------------|
| GET | `/config` | -- | Returns current Monitor section config (intervals, keywords, plugin dir) |
| GET | `/config/health` | -- | Database connectivity health check |
| GET | `/config/metrics` | -- | List all registered metric providers with display metadata |
| GET | `/config/alerts` | -- | List all alert configurations |
| POST | `/config/alerts` | `AlertConfiguration` | Create or update an alert configuration |
| DELETE | `/config/alerts/{id}` | -- | Delete an alert configuration |
| GET | `/config/settings` | -- | All application settings grouped by category |
| PUT | `/config/settings/{category}/{key}` | `{value}` | Update a single setting |
| PUT | `/config/settings` | `Dictionary<string, Dictionary<string, string>>` | Batch update settings; triggers keyword reload if `DataCollection.ProcessKeywords` changes |
| GET | `/config/admin-status` | -- | Returns whether the service is running as Windows Administrator |

### PowerController -- `GET /api/v1/power`

Available only when AMD GPU is detected and RyzenAdj is present.

| Method | Path | Description |
|--------|------|-------------|
| GET | `/power/status` | Current power status: watts, limit, scheme index, STAPM/Fast/Slow limits |
| GET | `/power/warmup` | Trigger device verification retry; returns enabled status and device name |
| POST | `/power/scheme/next` | Switch to next power scheme (requires device verification pass) |

### WidgetConfigController -- `GET /api/v1/widgetconfig`

| Method | Path | Body | Description |
|--------|------|------|-------------|
| GET | `/widgetconfig` | -- | Load widget settings from `data/widget-settings.json` |
| POST | `/widgetconfig` | `WidgetSettings` | Save full widget settings |
| POST | `/widgetconfig/{metricId}` | `MetricClickConfig` | Update click action for a single metric |

## SignalR Hub

**Hub class**: `MetricsHub`
**Default path**: `/hubs/metrics` (configurable via `Server:HubPath`)
**Interface**: `IMetricsClient` (typed hub)

### Server -> Client Events

| Event | Payload fields | When sent |
|-------|---------------|-----------|
| `ReceiveHardwareLimits` | `Timestamp`, `MaxMemory` (MB), `MaxVram` (MB) | On startup, then hourly for VRAM updates |
| `ReceiveSystemUsage` | `Timestamp`, `TotalCpu`, `TotalGpu`, `TotalMemory`, `TotalVram`, `UploadSpeed`, `DownloadSpeed`, `MaxMemory`, `MaxVram`, `Disks[]`, `PowerAvailable`, `TotalPower`, `MaxPower`, `PowerSchemeIndex` | Every `SystemUsageIntervalSeconds` (default 1s) |
| `ReceiveProcessMetrics` | `Timestamp`, `ProcessCount`, `Processes[{ProcessId, ProcessName, Metrics{}}]` | Every `IntervalSeconds` (default 5s) when processes are found |
| `ReceiveProcessMetadata` | `Timestamp`, `ProcessCount`, `Processes[{ProcessId, ProcessName, CommandLine, DisplayName}]` | On connect (full snapshot), then on any metadata change |

### Connection Lifecycle

On `OnConnectedAsync`: immediately sends full `ProcessMetadataStore` snapshot to the new client via `ReceiveProcessMetadata`.

## Background Workers

| Worker | Trigger | Purpose |
|--------|---------|---------|
| `Worker` | Continuous | Main metric collection and SignalR push |
| `AggregationWorker` | Every 1 minute | Incremental aggregation Raw->Minute->Hour->Day |
| `DatabaseCleanupWorker` | Every `CleanupIntervalHours` (default 24h) | Delete old records, VACUUM |

## Configuration Options

### appsettings.json sections

#### ConnectionStrings
```json
{
  "ConnectionStrings": {
    "DatabaseConnection": "Data Source=monitor.db"
  }
}
```

#### Server
| Key | Default | Description |
|-----|---------|-------------|
| `Server:Host` | `localhost` | Kestrel bind host |
| `Server:Port` | `35179` | Kestrel bind port |
| `Server:HubPath` | `/hubs/metrics` | SignalR hub route |

#### Monitor (`MonitorSettings`)
| Key | Default | Range | Description |
|-----|---------|-------|-------------|
| `Monitor:IntervalSeconds` | `5` | 1-3600 | Process metric collection interval (seconds) |
| `Monitor:SystemUsageIntervalSeconds` | `1` | 1-3600 | System usage collection interval (seconds) |
| `Monitor:Keywords` | `[]` | -- | Initial process filter keywords (overridden by DB at runtime) |

#### Database (`DatabaseSettings`)
| Key | Default | Range | Description |
|-----|---------|-------|-------------|
| `Database:RetentionDays` | `30` | 1-365 | Days to retain raw and aggregated records |
| `Database:CleanupIntervalHours` | `24` | 1-168 | Hours between cleanup runs |

#### Aggregation (`AggregationSettings`)
| Key | Default | Range | Description |
|-----|---------|-------|-------------|
| `Aggregation:BatchSize` | `2000` | 100-50000 | Records per batch during aggregation |

#### MetricProviders
| Key | Default | Description |
|-----|---------|-------------|
| `MetricProviders:PreferLibreHardwareMonitor` | `true` | Use LHM over PerformanceCounter |
| `MetricProviders:PluginDirectory` | `{ContentRoot}/plugins` | Path to external metric provider plugins |

#### Power
| Key | Default | Description |
|-----|---------|-------------|
| `Power:RyzenAdjPath` | `tools/RyzenAdj/ryzenadj.exe` | Path to RyzenAdj executable |
| `Power:PollingIntervalSeconds` | `3` | Power status polling interval |
| `Power:DeviceVerification:*` | -- | Device verification endpoint settings |

#### SignalR (optional)
| Key | Description |
|-----|-------------|
| `SignalR:MaximumReceiveMessageSize` | Hub receive size limit (bytes) |
| `SignalR:ApplicationMaxBufferSize` | Hub application buffer (bytes) |
| `SignalR:TransportMaxBufferSize` | Hub transport buffer (bytes) |

## Database Schema Overview

SQLite database with WAL mode enabled. All `DateTime` columns are stored and read as UTC.

### Tables

| Table | Key columns | Description |
|-------|------------|-------------|
| `ProcessMetricRecords` | `Id` (long PK), `ProcessId`, `ProcessName`, `Timestamp`, `MetricsJson` (JSON), `CommandLine` | Raw per-process metric samples |
| `AggregatedMetricRecords` | `Id` (long PK), `ProcessId`, `ProcessName`, `AggregationLevel` (enum), `Timestamp`, `MetricsJson` (JSON) | Aggregated metrics at Minute/Hour/Day resolution |
| `AlertConfigurations` | `Id`, `MetricId`, `Threshold`, `IsEnabled`, `CreatedAt`, `UpdatedAt` | Alert thresholds per metric; seed data: cpu/memory/gpu/vram at 90% |
| `ApplicationSettings` | `Id`, `Category`, `Key` (unique with Category), `Value`, `CreatedAt`, `UpdatedAt` | Key-value settings store; groups: Appearance, DataCollection, Monitoring, System |

`MetricsJson` in `ProcessMetricRecords` is a JSON object `{ "cpu": { "value": 12.5, "unit": "%" }, ... }`.
`MetricsJson` in `AggregatedMetricRecords` is a JSON object `{ "cpu": { "min": 1.0, "max": 80.0, "avg": 25.3, "sum": 1520, "count": 60, "unit": "%" }, ... }`.

Both columns have a SQLite `json_valid()` check constraint.

Migrations are applied automatically on startup via `dbContext.Database.Migrate()`.

## Integration Points

### With XhMonitor.Desktop (WPF)

- Desktop connects to SignalR hub at `http://localhost:35179/hubs/metrics`
- Desktop calls REST API for settings (GET/PUT `/api/v1/config/settings`)
- Desktop calls `/api/v1/power/scheme/next` for power mode toggle from taskbar widget
- Desktop reads widget config via `/api/v1/widgetconfig`
- CORS allows origin `app://.` (Electron-style) and `http://localhost:35180`

### With XhMonitor.Web (Frontend)

- Web front end connects to SignalR hub at the same endpoint
- Web queries historical data via `/api/v1/metrics/history` and `/api/v1/metrics/processes`
- Web manages alerts via `/api/v1/config/alerts`
- CORS allows `http://localhost:3000`, `http://localhost:5173`, `http://localhost:35180`

### With XhMonitor.Core

- All metric provider interfaces (`IMetricProvider`, `ISystemMetricProvider`, `ILibreHardwareManager`, etc.) are defined in `XhMonitor.Core`
- Entity models (`ProcessMetricRecord`, `AggregatedMetricRecord`, `AlertConfiguration`, `ApplicationSettings`) are defined in `XhMonitor.Core.Entities`
- `LibreHardwareManager`, `SystemMetricProvider`, `RyzenAdjPowerProvider`, `ProcessNameResolver`, `WmiGpuVendorDetector` are implemented in `XhMonitor.Core`

## Development Notes

- Run with `dotnet run` from the `XhMonitor.Service` directory; `appsettings.Development.json` is loaded automatically
- EF Core migrations: `dotnet ef migrations add <Name> --project XhMonitor.Service`
- The service enforces single-instance via a named Mutex; kill any existing instance before debugging a second one
- `ServerGarbageCollection` is disabled in the project file to reduce GC pauses on a monitoring workload
- RyzenAdj binaries are published under `tools/RyzenAdj/` alongside the executable and can be replaced without rebuilding
