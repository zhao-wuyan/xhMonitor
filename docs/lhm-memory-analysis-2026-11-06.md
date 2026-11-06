# LHM 桥接内存归因与社区对照

日期：2026-11-06。分支：`fix/ryzenadj-pawnio-395`。

## 1. 范围与结论

第 1～10 节保存改造前的分模块调查与社区对照；其中的源码描述和行号均对应调查时版本。后续生产改造及独立进程验收记录见第 11 节，不能将两批实验混为同一基准。

已确认的热点：

1. LHM 磁盘采集引入 DiskInfoToolkit 及其 PCI 设备数据库，明显增加常驻对象和进程提交。
2. `ProcessVramCollector.CaptureGpuUsagePercent()` 针对每个 GPU Engine 分别调用 `PerformanceCounter.NextValue()`；当前环境约 600 个实例，每周期产生约 80 MiB 托管分配。
3. 这不是“几 GiB 对象一直存活”的证据；多数是短命分配，但会制造 GC、CPU 和提交空间压力。
4. 没有证据证明这些内存问题或旧驱动尝试导致过掉盘。不得据此推导存储故障根因。

## 2. 实验条件与证据边界

- 目标平台为 AMD Ryzen AI Max+ 395；这些结果不外推为所有硬件上的固定成本。
- 当前依赖解析：LHM 0.9.6、DiskInfoToolkit 1.1.2、System.Diagnostics.PerformanceCounter 8.0.0；探针目标 .NET 8。依据 `lhm-bridge/obj/project.assets.json`，不是仅依据项目声明范围。
- 桥接默认采样间隔：1000 ms。当前实现是在采集完成后 `Task.Delay(1000)`，不是严格每秒完成一次。
- 沿用生产启动环境：`DOTNET_GCConserveMemory=9`、`DOTNET_gcConcurrent=0`、`DOTNET_GCRetainVM=0`。
- 原桥接每 5 个成功快照执行一次强制、阻塞、压缩式完整 GC。
- 分模块探针在检查点额外执行完整 GC 和等待 finalizer，用于比较存活量；因此这些检查点不是未经干预的生产进程峰值。
- 完整七组隔离实验均通过 UAC 以管理员权限运行。后续 Engine／VRAM 二分实验为普通权限；该二分仅测量 Windows GPU 性能计数器路径，不用于证明 CPU 底层传感器权限或完整硬件兼容性。
- 每组独立进程，硬件采样约 30 秒；模块结果不能相加，多个模块共享运行时、程序集和缓存。
- 使用 `GC.GetTotalMemory(false)`、`GC.GetGCMemoryInfo()`、进程 Private Bytes／Working Set、`VirtualQuery` 分类和 `dotnet-dump` 的 `eeheap -gc`／`dumpheap -stat`／`gcheapstat`。
- 原始临时探针、JSON 和 heap dump 已清理。下文数字是会话工具输出的汇总；本文件不包含可重新打开的原始 dump，也不能替代一次可重复运行的正式基准。

### 指标解释

- Private Bytes：进程私有提交，作为内存比较主指标。
- Working Set：当前驻留页面，含代码映像、共享页面等；不能全部视为进程独占物理内存。
- 托管存活对象：GC 后仍在使用的对象，不等于整个进程提交。
- `GCMemoryInfo.TotalCommittedBytes` 是最近一次 GC 观察值；和稍后的 dump 可能不同。禁止跨时点直接相减制作精确内存分布。
- `VirtualQuery` 的映像／映射／私有区域描述虚拟内存布局，不等同于各类实际驻留或独占提交。
- 虚拟地址预留不等于物理内存消耗。

## 3. 证据保留原则

本次归档优先保存仍可由会话工具输出及保留的实验汇总核对的数据。更早的 hostfxr 进程内托管实验不在本轮分模块实验的同一组测量中，其原始结果文件已不可读取，本文不混入其定量结果。

更换语言包装层或合并进程并不自动移除 CLR、GC、JIT 和 LHM 依赖；具体收益须由同版本、同采集功能的独立实验验证。

## 4. 分模块内存隔离结果

下表取每组最后一个 GC 后检查点；基线组为采集关闭时的最后一次测量。

| 开启功能 | Private Bytes | Working Set | 托管存活对象 |
| --- | ---: | ---: | ---: |
| 采集关闭，仅运行时基线 | 8.85 MiB | 30.36 MiB | 0.47 MiB |
| 仅 CPU | 10.78 MiB | 38.41 MiB | 0.78 MiB |
| 仅 GPU 传感器 | 16.64 MiB | 49.18 MiB | 0.62 MiB |
| 仅磁盘 | 39.24 MiB | 72.09 MiB | 6.48 MiB |
| CPU＋GPU＋磁盘，不含进程 GPU 采集 | 45.96 MiB | 83.12 MiB | 6.68 MiB |
| 仅进程 GPU／显存采集 | 36.54 MiB | 68.91 MiB | 2.92 MiB |
| 完整采集 | 57.45 MiB | 98.75 MiB | 8.93 MiB |

本机短时实验中，CPU 温度采集不是主要内存来源。磁盘详细采集和进程 GPU 性能计数器路径优先级更高。不能将独立模块数值直接相加，也不能据此保证关闭某模块必然释放精确的同等数量。

## 5. 堆快照：磁盘 PCI 数据库是主要存活对象来源之一

完整采集快照总对象约 8.9 MiB；其中：

| 类型 | 数量 | 对象自身大小，不含引用对象 |
| --- | ---: | ---: |
| `System.String` | 50,889 | 3,922,888 B |
| `System.Byte[]` | 553 | 1,258,028 B |
| `DiskInfoToolkit.PCI.PCIDevice` | 20,356 | 814,240 B |
| `List<DiskInfoToolkit.PCI.PCISubDevice>` | 20,356 | 651,392 B |
| `DiskInfoToolkit.PCI.PCISubDevice` | 17,510 | 560,320 B |
| `DiskInfoToolkit.PCI.PCIVendor` | 2,493 | 99,720 B |
| `System.Diagnostics.PerformanceCounter` | 609 | 102,312 B |

PCI 相关对象、数组和容器合计 2,816,736 B，约 2.69 MiB，尚不含引用的名称字符串。不能把所有 `System.String` 都算作 PCI 数据库。

堆快照时 GC 实际提交约 17.27 MiB。较早完整采样检查点的最近 GC 提交报告约 33.15 MiB；两者时点不同，可能包含空闲段回收等变化。未取得原生分配调用栈，尚不能将进程剩余提交精确归因到每个 DLL。

当前 `BuildDiskSnapshot()` 只消费名称、总容量、已用容量、读速率、写速率，没有把整套 PCI／SMART 详细信息作为输出合同。因此值得验证用 Windows 基础接口保持这些字段而取消 LHM Storage 的方案。

代码证据：`lhm-bridge/Program.cs` 的 `BuildDiskSnapshot()` 和 `BridgeComputerConfiguration.Create()`。行号参考当前调查版本约 331～407、741～752。

## 6. 分配热点：GPU Engine 逐实例采样

`ProcessVramCollector.CaptureGpuUsagePercent()`：

1. 每周期枚举 `GPU Engine` 实例。
2. 为每个实例缓存一个 `PerformanceCounter`。
3. 对每个已有实例分别调用 `NextValue()`。
4. 当前环境缓存约 601～609 个实例。

二分实验结果：

| 路径 | 每周期托管分配，稳态检查点差值 | 最后托管存活对象 | 缓存的 Engine 计数器 |
| --- | ---: | ---: | ---: |
| 仅 GPU Engine 使用率 | 80.24 MiB | 2.90 MiB | 601 |
| 仅进程／适配器显存路径 | 0.94 MiB | 2.56 MiB | 0 |

仅进程 GPU／显存组约 30 秒累计分配 2,057,025,808 B，约 1.92 GiB，但 GC 后存活对象约 2.92 MiB。累计分配不是当前占用；本次实验不能据此认定泄漏。

.NET 8 源码证明：`NextValue()` 调用 `NextSample()`，后者调用 `PerformanceCounterLib.GetCategorySample()`；底层重新读取类别数据并构建 `CategorySample`。缓存 `PerformanceCounter` 不等于缓存类别快照。因此逐实例读取会重复类别级工作。

源码：

- https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Diagnostics.PerformanceCounter/src/System/Diagnostics/PerformanceCounter.cs
- https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Diagnostics.PerformanceCounter/src/System/Diagnostics/PerformanceCounterLib.cs

项目位置：`lhm-bridge/Program.cs` 的 `CaptureGpuUsagePercent()`，调查版本约 505～585；显存路径 `CaptureUsageMb()` 约 450～504。

## 7. 待验证优化顺序

以下是建议，尚未落地为生产优化，也没有收益承诺：

1. 优先验证 GPU Engine 类别级批量快照或原生 PDH 查询，每周期一次采集，再计算各实例差分。保留进程使用率“最忙 engine”的现有统计口径，不直接累加到超过 100%。
2. 验证用 Windows 基础接口读取磁盘容量和吞吐，避免只为这几个字段引入完整设备数据库；保留逐物理盘、命名、单位、采样差分和错误语义。
3. 修复上述热点后重新测 Private Bytes、CPU、分配率和采样准确性，再决定是否继续替换 .NET 桥接。
4. 不把强制 GC、工作集裁剪或减少功能当作等价性能优化。
5. 继续保持 PawnIO 的权限要求、跨进程 PCI 互斥、有限重试及故障隔离；低内存不等于硬件访问零风险。

## 8. 参考与历史

- 旧实验另见 [memory-optimization-experiments.md](memory-optimization-experiments.md)，其 WPF／ASP.NET Core 架构和日期不同，不应与本次独立桥接数字直接比较。
- .NET 原生托管：https://learn.microsoft.com/en-us/dotnet/core/tutorials/netcore-hosting
- NativeAOT 类库：https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries
- `GCMemoryInfo.TotalCommittedBytes` 口径：https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-system-runtime

## 9. 社区对照期间的本机 PDH 验证

使用 Python `ctypes` 直接调用 Windows `pdh.dll`，没有修改生产代码、安装驱动或提权。按 Microsoft 官方示例创建 query，加入 `\GPU Engine(*)\Utilization Percentage`，首轮预热，再每隔 1 秒执行一次 `PdhCollectQueryData()`，用 `PdhGetFormattedCounterArrayW()` 一次获取所有实例。

每轮数组读取包括“查询缓冲区大小”和“填充数组”两次调用，不是对 601 个实例分别采样；计时包含 collect、两次数组调用和 Python 解析，不含等待。

| 轮次 | 实例数 | 可解析 PID 的有效实例 | 无效状态项 | 非零实例 | 数组缓冲区 | 采集及读取耗时 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 601 | 601 | 0 | 8 | 92,440 B | 1.767 ms |
| 2 | 601 | 601 | 0 | 8 | 92,440 B | 1.743 ms |
| 3 | 601 | 601 | 0 | 9 | 92,440 B | 1.817 ms |

query 关闭返回成功，管理员状态为 false。单项 `CStatus` 只接受 VALID_DATA／NEW_DATA；检查了值为有限数，排除了“API 返回成功但没有有效数据”的情况。

结论：本机无需新增硬件驱动，即可用 Windows 现有性能计数器批量读取 GPU Engine。92,440 B 是本次调用者数组缓冲区，不是 PDH 总内存；未测其内部原生分配、完整桥接稳态内存，也未做任务管理器逐进程数值交叉验证，不能当作正式性能 A/B。

生产化仍须验证首样本、进程退出／实例增删、计数器缺失、多 GPU LUID、重复实例与 locale；遇到 `PDH_MORE_DATA` 重新查询大小。缺失数据不能默认为有效 0%。

官方参考：

- [PDH 通配符数组 API 与完整示例](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhgetformattedcounterarrayw)
- [英文计数器名称与本地化注意事项](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhaddenglishcounterw)
- [Microsoft 对 GPU／逐进程最忙 engine 及显存口径的说明](https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/)。该文说明任务管理器的数据来自 VidSch／VidMm，不等于宣称任务管理器内部使用了本探针的 PDH 调用路径。

## 10. 社区方案对照

### 10.1 “Native” 命名不代表移除了 .NET 或保持相同功能

| 项目／资料 | 已明确的实际做法 | 对本项目的含义 | 内存证据与边界 |
| --- | --- | --- | --- |
| [LibreHardwareMonitor_NativeNodeIntegration](https://github.com/herrbasan/LibreHardwareMonitor_NativeNodeIntegration) | C++ N-API → .NET hosting → C# bridge → LHM；随包带 self-contained .NET 9 | 是包装与调用方式参考，不是移除运行时的原生重写；README 示例还关闭了 storage／DIMM 等路径 | README 的约 80 MB 是发行目录大小，不是 RAM；未取得可比较的进程内存实测，也未核实 395／PawnIO 支持和顶层复用许可 |
| [thisismypc 的只读 LHM 移植说明](https://github.com/samboland/thisismypc/blob/main/third-party/librehardwaremonitor/README.md) | 从 LHM 0.9.6 源码截取只读 NVIDIA NvAPI／NVML，针对 NativeAOT；保留源码为 MPL-2.0 | 说明按需裁剪可行，但其 CPU、AMD、Storage、PawnIO 明确不可用，不能替代 395 CPU 温度采集 | 没有同硬件、同功能基准；不是完整 LHM 的无损 NativeAOT 转换 |
| [TrafficMonitor 版本说明](https://github.com/zhongyang219/TrafficMonitor/blob/master/README_en-us.md) | 1.86 起 Lite 也提供 GPU／磁盘使用率；温度能力移到 Hardware Monitor 插件 | 可借鉴基础指标与高权限传感器分离，不能用关闭温度的 Lite 占用证明同功能更轻 | README 不是内存基准；基础 GPU／磁盘使用率不等于本项目的逐进程 GPU／显存及全部磁盘字段 |

NativeAOT 是独立的编译／部署选项，不是把现有 DLL 换个包装名。Microsoft 说明它移除运行时 JIT，但仍携带运行时支持代码，且有动态程序集加载、动态代码生成、裁剪、内置 COM 等限制。需要在完整依赖链和目标硬件上验证，不能由只读 NVIDIA 子集的成功外推为整套 LHM 已兼容。

参考：[Native AOT 官方部署说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)。

### 10.2 GPU：TaskSmack 有直接对应热点的批量实现

TaskSmack 是 **C++23** 项目，MIT 许可，不是 Rust 项目。本次核对的是源码提交 `623e63a524c52de8b0f7ddb842c4d8d901f56594`，不是只看已关闭的 [issue #383](https://github.com/mgradwohl/tasksmack/issues/383)。该 issue 早期提出累加多个 engine；当前代码已改成最忙 engine，不能照搬提案。

实际实现：

- [`PDHGPUProbe.cpp`](https://github.com/mgradwohl/tasksmack/blob/623e63a524c52de8b0f7ddb842c4d8d901f56594/src/Platform/Windows/PDHGPUProbe.cpp) 的 `readProcessGPUCounters()` 每次 query 读取执行一次 `PdhCollectQueryData()`，再读取使用率／dedicated memory／shared memory 三种通配符数组，不逐实例调用 `NextValue()`。
- [`PDHGPUProbeImpl.h`](https://github.com/mgradwohl/tasksmack/blob/623e63a524c52de8b0f7ddb842c4d8d901f56594/src/Platform/Windows/PDHGPUProbeImpl.h) 使用持久化 wildcard counters；每次 collect 自动纳入新实例，无需每周期枚举并重建数百个计数器。
- `readCounterArray()` 复用 `std::vector<std::byte> arrayBuffer`；使用率与显存数组共用 scratch buffer，所以必须处理完前一数组后才能读取下一数组。
- 按 PID＋GPU LUID 分组，保留物理节点／engine identity；同一 engine 的条目归并后，再取最忙 engine，而不是跨并行 engine 累加。
- 检查每项 `CStatus`；首样本作为 baseline，显存 gauge 可先读，使用率 rate 需要第二次 collect；显存的 dedicated／shared 分开返回。
- process 与 adapter query 分开维护，各自的差分周期不能混用。

性能证据分层：源码注释报告在 651 个实例环境中 wildcard collect＋array read 约 1 ms，逐实例查询约 8～30 ms，重枚举约 140 ms。这是作者的特定环境记录，不是本文复测，更不是 RAM 数字。本机独立可用性与耗时检查见第 9 节。未找到该项目可直接与本桥接比较的 Private Bytes／Working Set 基准。

可借鉴的是“一个 query 批量采集＋复用缓冲区＋稳定实例元数据＋既有统计口径”，可在 C# P/Invoke 或 Rust 中实现，不要求为了优化先换语言。不要整段照搬：其失败添加的 counter 会在后续读取中再尝试、还带短时旧值缓存；本项目需要独立保持有限重试和数据新鲜度合同。这些普通 PDH 添加操作也不能与内核驱动重装混为一谈。

### 10.3 磁盘：sysinfo 证明原生基础采集可行，但卷不等于物理盘

本次核对 [sysinfo 的 Windows `disk.rs`](https://github.com/GuillaumeGomez/sysinfo/blob/main/src/windows/disk.rs)，MIT 许可。读取时文件 blob SHA 为 `14625b8b4f1db7eb89ed09bda8a8f6477680ff54`；这是上游 `main` 的参考，不是声明本项目已升级到该版本。

实现使用：

- `FindFirstVolumeW()`／`FindNextVolumeW()` 枚举卷 GUID，读取挂载路径和卷信息。
- `GetDiskFreeSpaceExW()` 读取文件系统容量与空闲量。
- `DeviceIoControl(IOCTL_DISK_PERFORMANCE)` 读取累计 `BytesRead`／`BytesWritten`，保存前值，暴露本次更新与上次更新的字节差，不是已经除以时间的 MB/s。
- 这条磁盘路径使用 Windows 已有接口，不需要为了这些字段加载 DiskInfoToolkit 的 PCI 名称数据库或额外安装自带硬件驱动。

不能直接替换 `BuildDiskSnapshot()` 的原因：

1. sysinfo 以卷／挂载路径枚举；一个物理盘可以有多个卷，一个卷也可能跨多个物理盘。同卷多个挂载点不能重复计数。
2. 容量是文件系统容量，磁盘型号与卷名不同；必须保留当前逐物理盘的命名和容量汇总语义。
3. 差值须除以真实单调时钟间隔，再除以 `1_048_576` 才是当前输出的 MiB/s 数值；不能把每次 refresh 字节差直接写入 `ReadMbps`／`WriteMbps`。
4. 设备重建、计数器回退与读失败须按缺失／重新预热处理，不能伪装成成功读到 0。sysinfo 的失败保留值行为不应未经合同核对就照搬。
5. 原生 API 不等于“无副作用或所有权限都可用”。[Microsoft 说明](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-ioctl_disk_performance) `IOCTL_DISK_PERFORMANCE` 会启用磁盘性能计数器；本轮没有执行这个 IOCTL、验证每类设备权限，也没有为试验更改系统计数器配置。

因此更适合作为轻量原生实现参考，而不是为了移除一套大依赖就无条件引入另一个整套系统库。尚无磁盘替换后的本机功能／内存 A/B；模块隔离中的“仅磁盘 39.24 MiB”不能解释为全部可回收空间。

此外，sysinfo 的 [Windows `component.rs`](https://github.com/GuillaumeGomez/sysinfo/blob/main/src/windows/component.rs) 从 `root\WMI` 查询 `MSAcpi_ThermalZoneTemperature`，不是直接读取 AMD CPU 的温度寄存器。不能把这个通用 ACPI thermal zone 当作 395 CPU package／Tctl 温度的等价实现。

### 10.4 CPU／PawnIO：有原生通道，但移植的不只是 DLL 调用

[PawnIO 官网](https://pawnio.eu/) 说明官方版是签名的通用内核驱动，执行硬件访问模块；它不是无需内核驱动的用户态传感器 API。官方 [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) 发布签名模块，源码为 LGPL-2.1-or-later。模块存在不等于本机安全策略允许驱动加载；本轮不安装或重载驱动。

上游 `master` 的源码证据：

- [`AMDFamily17.p`](https://github.com/namazso/PawnIO.Modules/blob/master/AMDFamily17.p)：`main()` 限制 AMD x64、family `0x17`～`0x1A`；`ioctl_read_smn` 通过 PCI SMN 地址／数据寄存器访问。调用注释明确要求先获取 `\BaseNamedObjects\Access_PCI` mutant。逻辑上只读的 SMN 读取也会写入地址选择寄存器，不能简化为“完全不写硬件”。
- [`RyzenSMU.p`](https://github.com/namazso/PawnIO.Modules/blob/master/RyzenSMU.p)：`get_code_name()` 明确将 family/model `0x1A70` 识别为 `CPU_StrixHalo`，地址表也包含该分支。这里比仅凭“支持 AMD”更有证据，但只是模块型号路由，不是完整温度／功耗指令的验收。

这证明可在原生客户端中复用硬件访问通道；仍需自己实现 CPUID 识别、寄存器地址与值转换、并发互斥、权限处理、模块与固件版本匹配，以及有限重试。CPU 温度路径不能与 Ryzen SMU 功耗控制混成一种能力。上游 `master` 的新增支持也不能直接等同于项目携带的签名模块版本。

本次仅 CPU 采集的 Private Bytes 为 10.78 MiB、托管存活对象 0.78 MiB；相对另外两个热点，当前没有足够收益证据支持优先重写它。选择保留 LHM 的已验证 CPU 路径，先优化不依赖额外硬件驱动的 GPU Engine 批量读取，再处理磁盘基础字段。

### 10.5 对照后的选择

| 方向 | 决定 | 依据／验收缺口 |
| --- | --- | --- |
| GPU Engine 逐实例改通配符批量读取 | 优先验证生产实现 | 有社区实际源码和本机只读采样证据；需验证动态实例、多 GPU、分配率、CPU 与数值口径 |
| 磁盘基础字段从 LHM Storage 拆出 | 第二优先级 | 有原生社区实现，但必须解决物理盘／卷映射和去重；不能以减少字段代替等价迁移 |
| LHM CPU 温度 → 原生 PawnIO | 暂不优先 | 原生访问通道存在，但当前 CPU 内存成本小，完整兼容与并发责任增加 |
| C++／Rust 包装现有 .NET DLL | 不作为主要减重措施 | 改变调用边界，不自动消除 CLR／LHM 或当前重复采样 |
| NVIDIA 子集移植／关闭温度的 Lite | 不作为 395 同功能替代 | 采集能力不等价，缺少同功能内存基准 |

本次对照没有取得“完整保留本项目功能、支持 395、同时具备可复现低内存数字”的现成替代包证据。选择社区已有的窄路径组合，而不是据语言、包名或不等功能的截图承诺内存降到某个数值。

以上为改造前调查结论。随后按批准计划实施的变更与验证如下。

## 11. 批量 GPU 与原生物理磁盘采集

### 实现边界

- `WindowsPdhQuery.cs` 为 `\GPU Engine(*)\Utilization Percentage` 保持一个 PDH query，复用非托管缓冲区。进程 GPU 仍按 PID 取最忙 engine，不跨 engine／GPU 求和；首样本及失败后重新预热，异常帧不复用旧数组。
- `WindowsNativeDiskReader.cs`／`NativeDiskCollector.cs` 按物理盘接口与 DiskNumber 维护身份，型号和 geometry 容量按身份缓存，布局、卷 extent 和可用空间每周期更新；不扫描 PCI 数据库、SMART 或 ATA／NVMe pass-through。
- 已用空间从有效分区长度扣除去重的匹配卷可用空间，不把未分配空间算作已用。不可读 Windows 数据卷、动态盘、其他 OS 分区及无法分摊空闲空间的多 extent 卷使用 unknown；吞吐首样本和失败后首帧同样为 unknown，不伪造 0。
- `Program.cs` 已关闭 LHM Storage，并删除旧 Storage 快照构建、磁盘名称模式和 GB／GiB 反向换算；保留 LHM CPU／整卡 GPU、Dedicated Usage 显存、原 JSON 字段和磁盘汇总语义。没有修改采样周期、每 5 个成功快照一次的 GC 策略或 3 个 GC 环境变量，也没有裁剪发布依赖。
- **容量修正：旧值约高 7.37%，新值回到真实字节数。** 旧 LHM 将字节除以 `1000³`，桥接却乘回 `1024³`，放大系数为 `1.073741824`。原生路径直接采用 geometry 字节数；这不是磁盘缩水或仅是浮点误差。管理员逐盘精确核对尚未完成。

### 已执行的普通权限验证

- 新建独立 `lhm-bridge.Tests` 并接入根解决方案，不引用 WPF；GPU 聚合、容量边界和速率差分共 61 个用例通过。包含大时间戳先相减再转 double 的失败前／修复后回归，避免时间差被舍入成 0、速率变成 Infinity。Rust 原有 LHM JSON／逐盘消费合同测试通过；workspace 共 212 个测试通过。
- 最终桥接直接运行取得 31 帧；专用 WPF GPU 诊断进程使用硬件渲染 Tier 2，多帧读数随活动变化，正常关闭后连续 8 帧不再包含其 PID，没有结束用户原有程序。后续另完成同 PID 任务管理器可视趋势核对，见本节末尾。
- 临时 Rust example 使用真实 `LhmBridgeManager`；最终 `final-v2` 在 30 秒内取得 23 个去重快照，首帧为 7.06 秒，`is_sensor_elevated=false`，执行 `shutdown().await`；结束后没有本次桥接残留。诊断源码及原始输出保存在隔离证据目录，example 已从仓库移除。该结果不单独证明 C# Dispose 已执行。
- 最终 self-contained／single-file 发布运行 32 秒，取得 29 个有效 JSON 快照。通过隔离 Windows 控制台发送真实 Ctrl+C 事件，观察到 `shutdown requested (SIGINT)`、`[lhm-bridge] stopped` 和退出码 0；未删除传递依赖。WSL PTY 键盘输入未转发 Console 事件的尝试不计为正常退出证据。
- 普通权限下，旧桥接、仅 GPU 改造版及最终版的逐盘数组均为空；原生磁盘独立入口连续调用 5 次同样为空。因此这里只验证无权限时的降级行为，不能宣称两盘型号、容量、读写、热插拔或完整传感器能力已经验收。

### 普通权限性能证据

隔离目录：`%TEMP%\xhmonitor-lhm-dc2a47789ef84f7cb9b58595c319d124`。保存了 baseline／gpu／final-v2 源码、`project.assets.json`、发布产物、JSON Lines、进程采样、运行时 counters 与进程／Engine 上下文。有效比较各轮的实际发布依赖及 framework-dependent 解析资产相同，LHM 为 0.9.6，实际 CLR 均为 .NET 8.0.23；single-file 构建工具资产另存，不混作运行依赖变化。

GPU 阶段两轮均为 617 个 Engine 实例。稳态分配率从 `63.04 MiB/s` 降至 `0.94 MiB/s`，下降 `98.50%`，达到本机阶段目标。该对比不包含管理员才可读取的磁盘数据，不能用于证明磁盘替换收益。

普通权限最终有效对照为 `baseline-6 → final-5 → final-6 → baseline-7`；每轮 150 秒、前 30 秒预热、后 120 秒统计。测量期间只有一份自建实验桥接运行，用户原有程序保留。四轮活跃 GPU PID 集合一致，Engine 范围为 593～601，变化小于 1.4%。

| 轮次 | 稳态 Private Bytes 中位数（MiB） | 分配率均值（MiB/s） | CPU 时间／成功快照（ms） | 成功快照数 |
| --- | ---: | ---: | ---: | ---: |
| baseline-6 | 47.79 | 60.91 | 321.01 | 110 |
| final-5 | 30.30 | 0.954 | 26.40 | 140 |
| final-6 | 29.58 | 0.954 | 23.78 | 140 |
| baseline-7 | 48.02 | 60.91 | 322.05 | 109 |

最终版两轮 Private Bytes 中位数较基线范围低 36.6%～38.4%，每成功快照 CPU 时间没有因采样变慢而虚降。但四轮的 `disks=[]`、CPU 温度缺失，**这只是普通权限下的完整桥接对照，不是管理员全硬件／磁盘改造收益验收**。

`final-long-3` 连续运行 600 秒取得 574 帧。期间自行启动并正常关闭 3 次 Tier 2 GPU 诊断进程，每次有 18～19 帧正值，结束后最后 30 帧均无对应 PID。句柄中位数从 30～100 秒窗口的 525 降至 500～600 秒窗口的 504，没有随 3 次进程出现／消失而持续累积；私有提交中位数同期从 30.58 MiB 降至 29.06 MiB。没有进行物理设备热插拔，不能据此宣称磁盘缓存／句柄生命周期已经通过真实硬件验收。

无效尝试也保留在 `invalid-attempts.json`：`baseline-2／final-1／final-2／baseline-3` 因 WSL PTY 探针残留而不参与结论；首次长期观察因 PowerShell 空 ExitCode 判断错误中断。残留探针只按核实过的自建 PID 强制回收；修正后重新完成以上四轮和 10 分钟观察，没有使用无效轮次填补验收。

管理员硬件、GPU 可视核对及性能目标现均完成，结果见下文。过程中发生的 UAC 取消、界面元素失效和被遮挡截图均未当作通过证据；随后在用户再次授权的同一管理员会话内完成剩余验证。未安装或更新驱动，未调整电源或 Windows 安全设置。

### 管理员硬件验收（已执行）

旧版与最终版独立短跑均输出两块物理盘，CPU 温度均有多帧非零读数。最终版按 DiskNumber 排序；独立 Win32 探针的 geometry 原始字节数、Windows `Get-Disk` 和桥接输出完全一致：

| DiskNumber | 型号 | TotalBytes（B） | UsedBytes（B） |
| --- | --- | ---: | ---: |
| 0 | ZHITAI TiPlus7100 4TB | 4096805658624 | unknown |
| 1 | PCIe SSD | 1024209543168 | 1023810875392 |

- ZHITAI 盘存在计划列出的 Linux filesystem／LVM 类型，并且另有基本数据卷无法读取可用空间。旧版与新版均省略 `used_bytes`，不是本次切换造成的数据回归。
- PCIe SSD 的有效分区长度合计为 `1024208474112 B`；去重匹配卷可用空间合计为 `397598720 B`；两者相减与桥接 36 帧逐盘读数一致。该盘还存在不在旧库既有其他 OS 识别范围内的 GPT 分区类型；本次按批准计划保持该识别范围，不能把其已分配长度口径解释为已经读取 Linux 文件系统真实空闲量。
- 型号、布局、卷 GUID、extent、可用空间与独立 geometry 结果保存在 `admin-windows-*.json`、`admin-native-metadata.json` 和 `admin-capacity-verification.json`，没有合并成总项或按盘符重复输出。

只在证据目录创建一个随机命名的 64 MiB 文件，WriteThrough 写入一次并 flush，随后按 64 KiB 块以 `FILE_FLAG_NO_BUFFERING` 顺序读取一次。设备报告逻辑扇区 512 B、物理扇区 4096 B，缓冲区按 4096 B 对齐；读回 SHA-256 与写入一致。临时文件已删除，没有读取或覆盖用户文件。

| 采样路径 | ZHITAI 读峰值（MiB/s） | ZHITAI 写峰值（MiB/s） | PCIe SSD 读／写峰值（MiB/s） |
| --- | ---: | ---: | ---: |
| 桥接逐盘快照 | 13.424 | 14.932 | 0／0 |
| Windows PhysicalDisk 旁路 | 13.757 | 15.128 | 0／0 |

峰值来自不同采样时刻，不要求逐点相等；两路均观察到文件所在 Disk 0 的读写正值，空闲 Disk 1 没有复制同样读数。证据为 `admin-io.jsonl`、`admin-io-result.json` 和 `admin-physicaldisk.csv`。

真实 Rust reader 在 30 秒内取得 26 个去重快照，首帧约 3.03 秒，`is_sensor_elevated=true`；26 帧均含 CPU 温度和两块盘，名称、容量与直接桥接一致。已调用 `shutdown().await`。管理员 self-contained 发布随后运行并收到真实 Ctrl+C，输出 `shutdown requested (SIGINT)` 和 `[lhm-bridge] stopped`；本轮未另行保存其退出码，不把 IPC 强杀路径当作 Dispose 证据。

任务管理器核对的首次尝试因 `ElementNotAvailableException` 中断；截图及失败日志保存在 `failed-taskmgr-review-1/`，不计为通过。随后一次截图受前台 IDE 遮挡，也未用于宣称完整可视核对。最终补验每次显式置前 Task Manager，按同一 PID 32752 过滤，观察到 GPU `2.7% → 0% → 2.8%`；对应桥接同 PID 数据呈现活跃／空闲／恢复变化，退出后最后 8 帧均无 PID 残留。3 张有效截图已复制到证据目录的 `taskmgr-visible-active.png`、`taskmgr-visible-idle.png`、`taskmgr-visible-resumed.png`，数值和采样记录见 `taskmgr-visible-verification.json`。

### 管理员同工况性能验收（已完成）

顺序为 `admin-baseline-1 → admin-gpu-1 → admin-final-1 → admin-final-2 → admin-baseline-2`，实验桥接不并行；每轮 150 秒，前 30 秒预热，后 120 秒统计。GPU 可视补验在首轮桥接启动及测量计时前完成并关闭全部自建窗口，不进入性能统计窗口。未改变 1000 ms 间隔、每 5 个成功快照一次的 GC 或 3 个既有 GC 环境变量。

| 轮次 | 稳态 Private Bytes 中位数（MiB） | 分配率均值（MiB/s） | CPU 时间／成功快照（ms） | 成功快照数 |
| --- | ---: | ---: | ---: | ---: |
| admin-baseline-1 | 54.10 | 56.203 | 363.70 | 100 |
| admin-gpu-1 | 53.56 | 0.917 | 52.13 | 129 |
| admin-final-1 | 30.90 | 0.943 | 29.76 | 140 |
| admin-final-2 | 30.73 | 0.935 | 30.57 | 139 |
| admin-baseline-2 | 53.67 | 55.526 | 384.15 | 98 |

- GPU 阶段分配率较相邻基线降低 **98.37%**，超过 90% 目标；该阶段的主要收益是减少短命分配和 CPU，而不是明显降低常驻私有提交。
- 最终版两轮 Private Bytes 中位数，即使按较低基线与较高最终值保守比较，也降低 **42.43%**，超过 15% 目标。每成功快照 CPU 时间的最差比值为基线的 **0.0841 倍**，低于 1.1 倍上限；不是靠采集更慢获得收益。
- 各轮实际 CLR 均为 .NET 8.0.23，LHM 均为 0.9.6，实际发布依赖一致，源码与 `admin-final-source/` 的 SHA-256 对照一致。Engine 范围为 593～601，变化约 1.35%；主要活跃 GPU PID 一致，最终版首轮另有既存 Explorer PID 832 的偶发小读数，没有因此缩减实例或关闭采集项目。各轮完整进程与 Engine 上下文均保留。
- 全部管理员比较轮次均保留两块物理盘和 CPU 温度。两轮最终版与 10 分钟运行的每帧均保留相同型号、精确容量及既有 unknown 语义，没有采集错误；逐盘顺序保持 DiskNumber 升序。

`admin-final-long` 连续运行 600 秒，取得 572 个有效快照；每帧保留 CPU 温度及两盘数据。自行启动并正常关闭 3 个诊断进程，分别有 19／18／18 帧正 GPU 读数，最后 30 帧均无相应 PID 残留。句柄中位数从 30～100 秒窗口的 526 降至 500～600 秒窗口的 505，没有随重复出现／消失持续累积；对应 Private Bytes 中位数为 30.46／29.62 MiB。

最终结果见 `admin-performance-summary.json`、`acceptance-verdict.json`；全部自建桥接、诊断、任务管理器和管理员验收进程已回收，用户原有桥接 PID 20552 保留。临时 Rust example 已移除，未启动安装器或覆盖已安装版本。

**验证边界：** 未物理拔插内置 SSD，也未挂载、解锁或更改分区来制造热插拔场景，因此不宣称设备物理拔插已通过实测。ZHITAI 已用空间仍按 Linux／LVM 和不可读卷规则为 unknown；PCIe SSD 未识别 OS 分区仍沿用批准计划的既有分区识别范围，不宣称取得 Linux 文件系统空闲量。


