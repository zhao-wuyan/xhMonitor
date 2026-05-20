# Project: xhMonitor

## What This Is

xhMonitor is a Windows resource monitoring system for tracking process and device metrics in real time. It combines a .NET backend service, WPF desktop client, and React web UI to collect, aggregate, and visualize CPU, memory, GPU, VRAM, disk, power, and network data.

## Core Value

Provide reliable, low-latency Windows resource visibility across desktop and web clients, with extensible metric collection and safe controls for platform-specific power features.

## Requirements

### Validated

<!-- Shipped and confirmed valuable. -->

- Real-time multi-dimensional metric collection for CPU, memory, GPU, VRAM, disk, power, and network data.
- SignalR-based live updates for web and desktop clients.
- Layered aggregation for minute, hour, and day level analysis.
- Configurable process filtering by monitored keywords.
- WPF floating desktop monitor with tray integration.
- React web visualization with chart-based metric exploration.
- Access key authentication, IP whitelist, and LAN access controls.

### Active

<!-- Current scope being built toward. These are hypotheses until shipped. -->

- [ ] Keep service, desktop, and web clients aligned around shared metric contracts.
- [ ] Preserve extensibility through `IMetricProvider` implementations and configuration-driven metric definitions.
- [ ] Maintain safe degradation when privileged hardware or power APIs are unavailable.

### Out of Scope

<!-- Explicit boundaries. Include reasoning to prevent re-adding. -->

- Cross-platform desktop monitoring — current implementation is Windows-oriented and depends on Windows APIs, WPF, PerformanceCounter, and platform-specific hardware integrations.

## Context

The repository already contains existing workflow assets under `.workflow/`, including specs, issue records, analysis sessions, and project technology metadata. This file was restored non-destructively because `.workflow/project.md`, `.workflow/state.json`, and `.workflow/config.json` were missing while prior workflow artifacts existed.

## Constraints

- **Platform**: Windows 10/11 target environment — required by WPF desktop UI, Windows process monitoring, and privileged power-management integrations.
- **Runtime**: .NET 8 and Node.js 18+ — required by the backend, desktop, and web development setup.
- **Safety**: Power-management and LAN access features must preserve authentication, whitelist, and graceful degradation behavior.
- **Compatibility**: Existing `.workflow/` assets are intentional and must not be overwritten during initialization recovery.

## Tech Stack

- **Language**: C#, TypeScript, XAML
- **Framework**: .NET 8, ASP.NET Core, WPF, Entity Framework Core 8, React 19, SignalR, Vite 7, TailwindCSS v4
- **Database**: SQLite via Entity Framework Core

## Key Decisions

<!-- Decisions that constrain future work. Add throughout project lifecycle. -->

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Use layered architecture with client-server separation | Keeps core metrics, service APIs, desktop UI, and web UI independently evolvable | Active |
| Use `IMetricProvider` for metric collection extensibility | Enables new metric sources without rewriting service or UI contracts | Active |
| Use SignalR for real-time delivery | Supports low-latency updates to web and desktop clients | Active |
| Keep initialization recovery non-destructive | Existing `.workflow/` history and specs are project assets | Active |

## Stakeholders

- Windows users who need process-level resource and hardware monitoring.
- Developers maintaining metric providers, service APIs, desktop client, and web visualization.

---
*Last updated: 2026-05-20 after non-destructive initialization recovery*
