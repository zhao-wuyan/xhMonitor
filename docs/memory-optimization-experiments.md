# Memory Optimization Experiments

Date: 2026-05-27

Branch: `perf/memory-optimization-switches`

## Baseline From Dump Analysis

The analyzed dumps are not OOM crash dumps. The current footprint is mostly normal resident overhead for a WPF desktop process plus an ASP.NET Core service plus LibreHardwareMonitor.

| Process | Working Set | Private Usage | GC Allocated Heap | Main Contributors |
| --- | ---: | ---: | ---: | --- |
| Desktop | 250.78 MiB | 165.79 MiB | 23.3 MiB | WPF/WinForms UI stack, DirectWrite fonts, AutomationPeer, Dispatcher, CLR loader/JIT |
| Service | 210.93 MiB | 103.55 MiB | 53.5 MiB | ASP.NET Core, SignalR, LibreHardwareMonitor, EF Core, process/performance counters |

## Experiment Rules

- Keep every optimization behind configuration where practical.
- Preserve current defaults unless this branch explicitly marks an experiment value.
- Measure with the same scenario before and after each change.
- If UX or metrics quality regresses, revert the config first before reverting code.

Suggested measurement fields:

| Field | How to compare |
| --- | --- |
| Private bytes / private usage | Primary memory signal |
| Working set | Secondary signal; can fluctuate due to OS trimming |
| GC heap size | Detect managed heap growth |
| `System.String` / `System.Byte[]` | Detect payload/log/JSON pressure |
| `LibreHardwareMonitor.Hardware.SensorValue[]` | Detect LHM sensor cache footprint |
| Active SignalR requests/connections | Detect stale HTTP state |

## Optimization Matrix

| ID | Item | Status | Switch / Revert Path | Expected Effect | Risk |
| --- | --- | --- | --- | --- | --- |
| M01 | Keep system UI metrics at 1 second | Recorded, not changed | `Monitor:SystemUsageIntervalSeconds=1` | Preserves mainstream monitor refresh behavior | Low |
| M02 | Slow down hidden/collapsed refresh | Recorded, not changed | Existing collapsed mode already uses Lite subscription and skips full process collection sync | Potential allocation reduction when collapsed | Medium UX risk |
| M03 | Default Desktop to Lite/Pinned only | Recorded, not doing now | Existing `FloatingWindowViewModel.SyncProcessMetricsSubscription()` already switches Full only when details visible | Limited extra benefit expected | Medium feature risk |
| M04 | WPF process list virtualization / visual tree reduction | Recorded, not doing in first pass | Would require Desktop XAML changes; previous attempts affected list display | Possible Desktop memory reduction | Medium/high UX risk |
| M05 | LibreHardwareMonitor hardware category switches | Implemented | `MetricProviders:LibreHardwareMonitor:*` | Reduce LHM native/sensor objects when Network or Storage are disabled | Medium metrics coverage risk |
| M06 | llama-server `/metrics` failure backoff | Implemented | `Monitor:LlamaMetricsFailureBackoffThreshold`, `Monitor:LlamaMetricsFailureBackoffSeconds`; code default is `0`, this branch config enables `3/60s` | Avoid repeated failed HTTP calls and temporary allocations when endpoint returns `501` | Low |
| M07 | Disable llama realtime metrics entirely | Existing switch | `Monitor:LlamaMetricsIntervalSeconds=0` | Removes llama metrics HTTP loop and cache updates | Feature loss for llama metrics |
| M08 | Aggregation batch tuning | Existing switch | `Aggregation:BatchSize` | Lower aggregation memory peak if DB backlog is large | More DB cycles if too low |
| M09 | Disable LibreHardwareMonitor globally | Existing switch | `MetricProviders:PreferLibreHardwareMonitor=false` | Largest LHM-related reduction; falls back to traditional providers | Loses some system sensors |
| M10 | Service GC mode evaluation | Recorded, not implemented | Runtime config / publish setting experiment only | May reduce service resident heap segments | Needs benchmark |

## First-Pass Switches

### M05: LibreHardwareMonitor Hardware Categories

Current branch adds per-category switches under:

```json
"MetricProviders": {
  "LibreHardwareMonitor": {
    "EnableCpu": true,
    "EnableGpu": true,
    "EnableMemory": true,
    "EnableMotherboard": false,
    "EnableController": false,
    "EnableNetwork": true,
    "EnableStorage": true
  }
}
```

Suggested trials:

| Trial | Config | What to watch |
| --- | --- | --- |
| M05-A | Disable `EnableStorage` only | Disk throughput/SMART metrics disappear or degrade; check Service private usage |
| M05-B | Disable `EnableNetwork` only | Network metrics may fall back or disappear; check Service private usage |
| M05-C | Disable both `EnableStorage` and `EnableNetwork` | Best LHM footprint reduction candidate |

Rollback: set both values back to `true`.

### M06: llama Metrics Failure Backoff

Current branch adds:

```json
"Monitor": {
  "LlamaMetricsFailureBackoffThreshold": 3,
  "LlamaMetricsFailureBackoffSeconds": 60
}
```

Behavior: after a port fails 3 consecutive `/metrics` requests, the enricher skips that port for 60 seconds. Port display is still kept, but failed HTTP requests stop during the backoff window.

Rollback: set `LlamaMetricsFailureBackoffThreshold` to `0`.

## Current Notes

- The project already has collapsed state: `FloatingWindowViewModel.PanelState.Collapsed`.
- Collapsed state already avoids full process collection sync and uses Lite process metrics subscription.
- Keep `SystemUsageIntervalSeconds=1` unless measurement proves it is a problem. One-second system refresh is expected for a monitor UI.
- Do not use forced `GC.Collect()` or working-set trimming as a real optimization; it only changes short-term task-manager numbers and can hurt responsiveness.

## Results Log

| Date | Trial | Config | Result | Decision |
| --- | --- | --- | --- | --- |
| 2026-05-27 | Baseline dump | Current production-like config | Desktop private 165.79 MiB; Service private 103.55 MiB | Baseline |
| 2026-05-27 | M05/M06 code switches | Code defaults preserve old behavior; branch config enables llama backoff at 3 failures/60s | `dotnet build XhMonitor.Service/XhMonitor.Service.csproj --no-restore` passed; targeted tests passed 20/20 | Ready for runtime measurement |
| 2026-05-27 | New dump comparison | `EnableNetwork=true`, `EnableStorage=true`, llama failure backoff enabled at 3 failures/60s | Service committed private 95.69 -> 89.22 MiB; Service GC heap 53.51 -> 43.29 MiB. Desktop committed private 154.11 -> 149.65 MiB; Desktop GC heap 23.29 -> 12.99 MiB | Improvement observed, but not a strict publish-to-publish A/B because new dump was from `bin\\Debug` path |

## 2026-05-27 New Dump Comparison

Compared files:

| Process | Old dump | New dump |
| --- | --- | --- |
| Service | `C:\Users\xinghe_zwy\Downloads\xhMonitorDMP\XhMonitor.Service.old.DMP` | `C:\Users\xinghe_zwy\Downloads\xhMonitorDMP\XhMonitor.Service (2).DMP` |
| Desktop | `C:\Users\xinghe_zwy\Downloads\xhMonitorDMP\XhMonitor.Desktop.old.DMP` | `C:\Users\xinghe_zwy\Downloads\xhMonitorDMP\XhMonitor.Desktop (2).DMP` |

Important caveat: the old dumps were from `C:\my_program\XhMonitor\...`, while the new dumps were from `bin\Debug\net8.0...`. Treat this as a directional memory check, not a strict release-package A/B result.

| Process | Metric | Old | New | Delta |
| --- | --- | ---: | ---: | ---: |
| Service | Dump size | 326.08 MiB | 316.42 MiB | -9.66 MiB |
| Service | Dumped memory | 325.21 MiB | 315.59 MiB | -9.62 MiB |
| Service | Committed private | 95.69 MiB | 89.22 MiB | -6.47 MiB |
| Service | Committed total | 436.99 MiB | 427.44 MiB | -9.55 MiB |
| Service | GC allocated heap | 53.51 MiB | 43.29 MiB | -10.22 MiB |
| Service | Managed object count | 399,812 | 138,309 | -261,503 |
| Service | `System.String` | 12.18 MiB / 125,755 | 2.96 MiB / 24,812 | -9.22 MiB |
| Service | `LibreHardwareMonitor.Hardware.SensorValue[]` | 2.40 MiB / 242 | 0.25 MiB / 175 | -2.15 MiB |
| Service | Active HTTP requests in dump | 5 | 0 | -5 |
| Desktop | Dump size | 573.56 MiB | 546.83 MiB | -26.73 MiB |
| Desktop | Dumped memory | 569.62 MiB | 546.06 MiB | -23.56 MiB |
| Desktop | Committed private | 154.11 MiB | 149.65 MiB | -4.46 MiB |
| Desktop | Committed total | 684.00 MiB | 656.45 MiB | -27.55 MiB |
| Desktop | GC allocated heap | 23.29 MiB | 12.99 MiB | -10.30 MiB |
| Desktop | Managed object count | 416,842 | 192,817 | -224,025 |
| Desktop | `ProcessInfoDto` | 0.01 MiB / 256 | 0.00 MiB / 57 | Lower |
| Desktop | Active HTTP requests in dump | 2 | 1 | -1 |

Interpretation:

- The new dumps show real directional improvement in managed heap and committed private memory.
- The largest Service managed reduction is `System.String` and LibreHardwareMonitor sensor value history. This supports keeping M06 backoff and continuing M05 category experiments.
- `System.Byte[]` in Service increased from 11.48 MiB to 22.72 MiB, so byte-buffer pressure should be watched in the next dump. It did not offset the total GC heap reduction.
- Desktop still looks dominated by WPF/WinForms/UI, Automation, DirectWrite/font and module mapping overhead. The new dump reduced managed objects and module count, but this is still normal desktop UI resident cost rather than a leak signature.
- M05 `EnableNetwork=false` / `EnableStorage=false` was not tested in this dump because both switches are currently `true`.
