# Understanding Document

**Session ID**: DBG-web-desktop-2026-01-25
**Bug Description**: web端的显存和内存使用率异常，请参考desktop的取值方案进行修复，且名称中也不需要显示当前是什么方案。
**Started**: 2026-01-25T23:38:00+08:00

---

## Exploration Timeline

### Iteration 1 - Initial Exploration (2026-01-25 23:38)

#### Current Understanding

- Web 端系统摘要当前通过 `calculateSystemSummary` 把所有进程的指标值累加，App、浮窗和任务栏组件都依赖该汇总结果。
- 后端已有系统级指标推送 `ReceiveSystemUsage`，包含 `TotalMemory/TotalVram` 和 `MaxMemory/MaxVram`，Desktop 端使用该方案。
- 进程级内存/显存指标单位为 MB，累加会明显偏大；GPU/CPU 为百分比，累加也可能异常，但当前问题聚焦内存/显存。
- 指标 `DisplayName` 在 Provider 中为固定英文名称（如 `Memory Usage`/`VRAM Usage`），未发现方案/模式类文本，Web 端再用 i18n 映射。

#### Evidence from Code Search

- `xhmonitor-web/src/utils.ts`：`calculateSystemSummary` 累加 `processes[].metrics`。
- `xhmonitor-web/src/App.tsx`、`xhmonitor-web/src/pages/DesktopWidget/FloatingWidget.tsx`、`xhmonitor-web/src/pages/DesktopWidget/TaskbarWidget.tsx`：系统摘要与历史曲线基于 `calculateSystemSummary`。
- `XhMonitor.Service/Worker.cs`：`SendSystemUsageAsync` 推送 `ReceiveSystemUsage`（系统级内存/显存 + 上限）。
- `XhMonitor.Service/Controllers/ConfigController.cs`：`GetMetrics` 返回 Provider 的 `DisplayName/Unit`。
- `XhMonitor.Core/Providers/*MetricProvider.cs`：`DisplayName` 为 `CPU Usage/Memory Usage/GPU Usage/VRAM Usage`。

#### Hypotheses Generated

- **H1**: Web 端把进程级内存/显存求和当作系统总量，导致值异常偏大。
- **H2**: 系统级 `ReceiveSystemUsage` 的内存/显存总量与硬件上限是合理的，和进程求和存在显著差异。
- **H3**: `DisplayName` 不包含方案/模式信息，若仍显示方案文本则来自 Web 端处理逻辑。

---

### Iteration 2 - Evidence Analysis (2026-01-25 23:42)

#### Log Analysis Results

**H1**: CONFIRMED  
- Evidence: 浏览器日志提示 `No client method with the name 'receivesystemusage' found`  
- Reasoning: Web 端未订阅 `ReceiveSystemUsage`，系统摘要只能基于进程指标累加。

**H2**: CONFIRMED  
- Evidence: 服务端 `Worker.SendSystemUsageAsync` 已发送 `ReceiveSystemUsage`；客户端无 handler 导致数据未被使用  
- Reasoning: Desktop 端使用该事件，Web 端未订阅因此出现差异。

**H3**: CONFIRMED  
- Evidence: Provider `DisplayName` 固定为 `CPU/Memory/GPU/VRAM Usage`  
- Reasoning: 后端未附加方案/模式文本，Web 端也未做拼接。

#### Corrected Understanding

- ~~Web 端可能已订阅系统级数据~~ → Web 端未订阅 `ReceiveSystemUsage`，导致系统摘要走进程累加。
  - Why wrong: 控制台警告表明客户端缺少对应方法。
  - Evidence: `No client method with the name 'receivesystemusage' found`

#### New Insights

- Web 端系统摘要应优先使用 `ReceiveSystemUsage` 的系统级数据，以与 Desktop 端保持一致。

---

### Iteration 3 - Resolution (2026-01-25 23:48)

#### Fix Applied

- 添加 `ReceiveSystemUsage` 订阅并规范字段到 camelCase。
- `calculateSystemSummary` 支持注入系统级数据，覆盖 `cpu/gpu/memory/vram` 总量。
- App / FloatingWidget / TaskbarWidget 改为使用系统级总量生成摘要与历史。
- 维持指标 `DisplayName` 原值，不展示方案/模式信息。
- 清理调试埋点代码（保留理解文档与假设记录）。

#### Verification Results

- 用户反馈仍出现 `44777.6%` / `30757.5%`。

---

### Iteration 4 - Evidence Analysis (2026-01-25 23:58)

#### Log Analysis Results

**H4**: CONFIRMED  
- Evidence: `LibreHardwareMonitorMemoryProvider` 的 `DisplayName` 包含方案文本，`Unit` 为 `%`；`LibreHardwareMonitorVramProvider` `Unit` 为 `%` 但系统总量实际返回 MB。  
- Reasoning: Web 端用系统级 MB 总量渲染，却使用了 `%` 的单位与带方案的显示名，导致数值显示为异常百分比且名称包含方案。

#### Corrected Understanding

- ~~Provider 的 DisplayName 固定为英文且不含方案~~ → `LibreHardwareMonitorMemoryProvider` 返回带方案的中文 DisplayName。
  - Why wrong: 新增的 LHM Provider 覆盖了内置命名。
  - Evidence: `DisplayName => "内存使用率 (LibreHardwareMonitor)"`

#### New Insights

- Web 端与 Desktop 端都按 MB/GB 展示内存与显存，应统一在元数据层对 `memory/vram` 进行标准化。

---

### Iteration 5 - Resolution (2026-01-25 23:59)

#### Fix Applied

- 在 `ConfigController.GetMetrics` 中标准化 `cpu/gpu/memory/vram` 的 DisplayName：统一为 `CPU/GPU/Memory/VRAM Usage`。
- 对 `memory/vram` 强制使用 `Unit = MB`、`Type/Category = Size`，消除 `%` 单位与方案文本。

#### Verification Results

- (等待用户验证)

#### Lessons Learned

1. 指标元数据是多端共用契约，不能让 Provider 方案细节泄露到 UI。
2. 系统级指标与进程级指标共享同一元数据，单位必须一致。

---

## Current Consolidated Understanding

### What We Know
- Web 端此前未订阅 `ReceiveSystemUsage`，导致系统摘要用进程累加。
- LHM Provider 的 DisplayName/Unit 与系统级 MB 总量不匹配，导致 `%` 异常与方案文本。
- Desktop 端使用 MB/GB 展示内存与显存。

### What Was Disproven
- ~~Web 端已使用系统级总量~~（控制台警告证实未订阅）
- ~~DisplayName 固定为英文且不含方案~~（LHM Provider 覆盖）

### Current Investigation Focus
- 等待用户验证修复后 Web 端内存/显存显示是否与 Desktop 一致。

### Remaining Questions
- 需要确认修复后 Web 端显示的内存/显存是否与 Desktop 一致。
