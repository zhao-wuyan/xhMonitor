# Power Switch Optimization - Lite³ Planning Doc

**Created**: 2026-01-28
**Task Slug**: power-switch-optimization
**Complexity**: moderate

---

## Task Description

为了避免功耗切换验证端口启动时机比较慢，在切换功耗前，即开始长按power的时候就开始校验设备。避免先启动监控，后启动校验端口导致无法切换的问题。

## Status

- [x] Phase 1: Requirements clarified
- [x] Phase 1.5: Complexity assessed (moderate)
- [ ] Phase 2: Tool selection
- [ ] Phase 3: Multi-mode analysis
- [ ] Phase 4: User decision
- [ ] Phase 5: Direct execution

## Complexity Assessment

**Level**: moderate

**Triggers**:
- 涉及多个服务集成 (Desktop + Service)
- 启动流程优化
- 异步初始化时序调整

**Key Files Identified**:
1. `XhMonitor.Desktop/Services/WindowManagementService.cs:169-198` - 长按事件处理
2. `XhMonitor.Service/Controllers/PowerController.cs:47-66` - 功耗切换 API
3. `XhMonitor.Core/Services/DeviceVerifier.cs:44-176` - 设备验证服务
4. `XhMonitor.Service/Worker.cs:58-94` - 启动流程
5. `XhMonitor.Service/Program.cs:235-246` - 服务注册

## Analysis Summary

### Current Flow
1. **Desktop 启动** → WindowManagementService 初始化
2. **用户长按 Power** → OnMetricActionRequested 触发
3. **调用 API** → PowerController.SwitchToNextScheme
4. **设备验证** → DeviceVerifier.IsPowerSwitchEnabledAsync (首次调用时初始化)
5. **功耗切换** → RyzenAdjPowerProvider.SwitchToNextSchemeAsync

### Problem
- DeviceVerifier 采用懒加载 (EnsureInitializedAsync)
- 首次调用时需要 HTTP 请求验证设备 (5秒超时)
- 导致长按响应慢

### Solution Approach
**提前初始化设备验证**：在长按开始时立即触发 DeviceVerifier 初始化，而不是等到 API 调用时才初始化。

## Execution Plan

### Option 1: Desktop 侧预热 (推荐)
在 WindowManagementService 中，当检测到长按开始时，立即调用一个预热 API 触发设备验证初始化。

**Changes**:
1. 添加预热 API: `GET /api/v1/power/warmup`
2. 修改 OnMetricActionRequested: 在长按开始时调用预热 API
3. 预热 API 仅触发 DeviceVerifier.IsPowerSwitchEnabledAsync，不执行功耗切换

### Option 2: Service 侧启动预热
在 Worker.ExecuteAsync 启动流程中添加 Phase 2.6，提前初始化 DeviceVerifier。

**Changes**:
1. 在 Worker.ExecuteAsync 中添加 DeviceVerifier 预热阶段
2. 调用 DeviceVerifier.IsPowerSwitchEnabledAsync 完成初始化

### Option 3: 改为单例预初始化
将 DeviceVerifier 改为在服务注册时立即初始化，而不是懒加载。

**Changes**:
1. 修改 DeviceVerifier 构造函数，立即启动初始化任务
2. 移除 EnsureInitializedAsync 的懒加载逻辑

## Progress Log

- 2026-01-28 10:00: Task created, ACE context gathered
- 2026-01-28 10:05: Complexity assessed as moderate, planning doc created
- 2026-01-28 23:57: Tool selection completed (claude + code-developer)
- 2026-01-28 23:58: Analysis completed, identified root cause and solution
- 2026-01-28 23:59: User cancelled workflow

## Decisions Made

| Decision | Rationale | Timestamp |
|----------|-----------|-----------|
| Create planning doc | Moderate complexity with multi-service integration | 2026-01-28 10:05 |
| Use claude CLI + code-developer | Single CLI analysis with code implementation agent | 2026-01-28 23:57 |
| Workflow cancelled | User decision | 2026-01-28 23:59 |

## Analysis Summary

### Root Cause
DeviceVerifier 采用懒加载模式，首次调用时需要 HTTP 请求验证设备（5秒超时），导致长按功耗切换响应慢。

### Recommended Solution
在 Worker.ExecuteAsync 启动流程中添加 Phase 2.6 设备验证预热阶段，服务启动时完成验证。

### Key Files
- `XhMonitor.Service/Worker.cs:58-94` - 启动流程
- `XhMonitor.Core/Services/DeviceVerifier.cs:44-176` - 设备验证服务
- `XhMonitor.Desktop/Services/WindowManagementService.cs:189-198` - 长按事件处理
- `XhMonitor.Service/Controllers/PowerController.cs:47-66` - 功耗切换 API

---

**Status**: Cancelled by user
