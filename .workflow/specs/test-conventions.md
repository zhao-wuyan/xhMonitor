---
title: "Test Conventions"
dimension: specs
category: testing
keywords:
  - testing
  - xunit
  - fluentassertions
  - moq
  - dotnet
  - node-test
readMode: required
priority: high
---

# Test Conventions

<spec-entry category="testing" keywords="xunit,fluentassertions,naming,unit-tests,dotnet" date="2026-05-20" source="XhMonitor.Tests/Core/ResultTests.cs:1; XhMonitor.Tests/Data/SqliteConnectionStringResolverTests.cs:1; XhMonitor.Desktop.Tests/AsyncOperationGateTests.cs:1">

### .NET 测试采用 xUnit + FluentAssertions 的行为命名

.NET 测试项目位于 `XhMonitor.Tests` 和 `XhMonitor.Desktop.Tests`，测试文件使用 `*Tests.cs` 命名，并按被测领域或组件分目录组织，例如 `Core`、`Data`、`Services`、`Providers`、`Integration`。

规则：

- 测试类命名为 `{Subject}Tests`。
- 测试方法使用 `{MethodOrScenario}_Should{ExpectedBehavior}` 或 `{MethodOrScenario}_When{Condition}_{ExpectedBehavior}`。
- 使用 `[Fact]` 表示单场景测试；需要多输入矩阵时再引入参数化测试。
- 断言优先使用 FluentAssertions 的 `Should()` 风格。
- 异常断言使用 `Action act = ...` 或 async 等价形式后再 `act.Should().Throw<T>()`。
- 并发或 async 行为测试使用 `TaskCompletionSource` 时应设置 `TaskCreationOptions.RunContinuationsAsynchronously`，避免测试线程上的隐式延续耦合。

</spec-entry>

<spec-entry category="testing" keywords="mocking,external-dependencies,hardware,filesystem,signalr" date="2026-05-20" source="XhMonitor.Tests/XhMonitor.Tests.csproj:17; XhMonitor.Desktop.Tests/XhMonitor.Desktop.Tests.csproj:17">

### 外部依赖测试必须隔离

项目包含硬件监控、外部进程、文件系统、SignalR、SQLite 和 Windows 桌面集成等边界。新增或修改公共行为时，测试应优先隔离这些外部依赖，避免把单元测试变成环境依赖测试。

规则：

- 使用 Moq 或项目现有 fake/stub 替代硬件 provider、SignalR client、filesystem、外部 process 和 OS API。
- SQLite 相关测试应明确使用临时路径或 `:memory:`，并验证相对路径、绝对路径、异常路径等边界。
- LibreHardwareMonitor、GPU、RyzenAdj、D3DKMT 等设备相关测试如果依赖真实硬件，应标识为 integration/diagnostic 场景，不能阻塞普通单元测试稳定性。
- 新增后台 worker 或 hosted service 行为时必须测试 cancellation、异常降级和重复执行边界。

</spec-entry>

<spec-entry category="testing" keywords="frontend,node-test,vite,react,typescript" date="2026-05-20" source="xhmonitor-web/package.json:6">

### 前端测试入口使用 `npm run test`

前端项目 `xhmonitor-web` 定义 `npm run test` 为 `node --test`。新增前端测试时应保持测试快速、确定性强，并避免真实网络请求。

规则：

- 前端公共逻辑优先测试纯函数、数据转换、endpoint 配置、i18n 映射和状态处理边界。
- 需要覆盖 SignalR 或 API 交互时使用 mock，不直接连接后端服务。
- 提交前至少运行受影响范围的 `npm run lint` 和 `npm run test`。
- React UI 行为如果缺少现成测试库支持，先抽出可测试的状态/格式化逻辑，避免引入重量级测试依赖。

</spec-entry>
