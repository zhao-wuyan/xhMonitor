using Xunit;

namespace XhMonitor.Tests.Collections;

/// <summary>
/// LibreHardwareMonitor/LibreHardwareManager 属于 native/驱动交互的集成路径。
/// 为避免并行运行导致测试宿主崩溃（AccessViolationException），将相关测试放入同一 Collection 并禁用并行。
/// </summary>
[CollectionDefinition("LibreHardwareMonitor", DisableParallelization = true)]
public sealed class LibreHardwareMonitorCollection;

