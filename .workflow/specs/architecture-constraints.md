---
title: "Architecture Constraints"
dimension: specs
category: architecture
keywords:
  - architecture
  - desktop
  - service
  - web
  - boundaries
readMode: required
priority: high
---

# Architecture Constraints

## Runtime Boundaries

- `XhMonitor.Core` owns shared models, configuration defaults, and core abstractions.
- `XhMonitor.Service` owns hosted service, HTTP API, persistence, hardware/process metrics, and background workers.
- `XhMonitor.Desktop` owns WPF shell, tray integration, local web server/proxy, desktop settings UI, and desktop-only OS integration.
- `xhmonitor-web` owns the browser UI and must communicate through configured API/SignalR endpoints instead of hardcoded service URLs.

## Configuration

- Shared defaults live in `ConfigurationDefaults` where practical.
- Runtime user settings are persisted through the config API/database path, not by editing appsettings from UI.
- Infrastructure-level ports and service endpoints remain in configuration/discovery services, not in user-facing settings unless explicitly designed.

## Integration

- Desktop UI should use services (`IServiceDiscovery`, `IBackendServerService`, `IWindowManagementService`, etc.) rather than constructing process/network behavior inline.
- Backend API changes require checking frontend and desktop consumers before modifying response shapes.
