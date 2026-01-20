# Configuration Boundaries (appsettings.json vs Database)

This project uses two configuration sources with different responsibilities:

- `appsettings.json`: infrastructure, deployment, and system-wide settings (typically require restart)
- Database (`ApplicationSettings`): user-configurable runtime preferences (can change while the app is running)

## What Belongs in `appsettings.json`

Use `appsettings.json` for settings that are part of how the service is hosted and deployed, or that should be consistent for all users/machines.

### Infrastructure Settings

- `Server.Host`
- `Server.Port`
- `Server.HubPath`

### Deployment / Environment Settings

- `Database.ConnectionString`
- `MetricProviders.PluginDirectory` (or similar plugin/assembly path settings)

### System Intervals / Timers

- `Monitor.IntervalSeconds`
- `Monitor.SystemUsageIntervalSeconds`

### Static Application Configuration

- `Logging`
- CORS/allowed origins, authentication provider wiring, and other host-level static configuration

## What Belongs in the Database (`ApplicationSettings`)

Use the database for settings that are user-configurable during runtime and typically driven by the UI.

### Appearance / UI Preferences

- `ThemeColor`
- `Opacity`

### Data Collection Preferences

- `ProcessKeywords`
- `TopProcessCount`
- `DataRetentionDays`

### User System Preferences

- `StartWithWindows`

## Decision Tree

Answer these questions to decide the correct location:

1. Is it infrastructure/deployment (ports, paths, connection strings) or needs to be the same for all users?  
   → Put it in `appsettings.json`
2. Does changing it require restarting the service/app?  
   → Put it in `appsettings.json`
3. Is it user-configurable from the UI and should take effect during runtime?  
   → Put it in the database (`ApplicationSettings`)
4. Can it safely vary per user or per machine without affecting deployment correctness?  
   → Put it in the database (`ApplicationSettings`)

## Examples

### `appsettings.json` Examples

- `Server.Port`: deployment/infrastructure; conflicts can break hosting; restart required
- `Database.ConnectionString`: deployment secret/host; should not be editable from UI
- `Monitor.IntervalSeconds`: system-wide monitoring cadence; changing it impacts server scheduling

### Database (`ApplicationSettings`) Examples

- `ThemeColor` / `Opacity`: UI preference; safe to change at runtime
- `ProcessKeywords`: user-driven filtering; should update without restart
- `TopProcessCount`: user preference for the UI and data display

## Migration Guidance (Overlapping Settings)

When a setting exists in both sources:

1. Choose the authoritative source using the decision tree.
2. Remove the setting from the non-authoritative source:
   - Infrastructure/system settings: remove from `ApplicationSettings` and UI, keep only in `appsettings.json`
   - Runtime preferences: remove from `appsettings.json` (if present), keep only in `ApplicationSettings`
3. Add startup validation to detect duplicates and prevent drift.
4. If a UI previously wrote a setting that moved to `appsettings.json`, update the UI to display it as read-only or remove it entirely.

