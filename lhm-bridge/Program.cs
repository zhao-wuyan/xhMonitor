// lhm-bridge — LibreHardwareMonitor 传感器读取子进程
//
// 由 xhm-service 以子进程方式拉起，通过 stdout JSON Lines 输出系统级传感器快照。
// 采集范围与原 XhMonitor.Core/Services/LibreHardwareManager.cs 对齐：
//   - CPU Temperature（对应 SystemMetricProvider.GetTemperatures）
//   - GPU Temperature + Load（对应 LibreHardwareMonitorGpuProvider）
//   - Storage Throughput（对应 SystemMetricProvider.GetDiskUsages via LHM）
//
// 契约：
//   stdout — 每行一个 LhmSnapshot JSON；除此之外不写任何内容
//   stderr — 诊断日志；首行固定为 banner JSON（含 is_admin），供父进程探测能力
//   退出码 — 0 优雅退出 / 1 LHM 初始化失败 / 2 --require-admin 且非管理员

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

[assembly: InternalsVisibleTo("XhMonitor.Tests")]

const int DefaultIntervalMs = 1000;
const int MinIntervalMs = 200;
const int MaxConsecutiveCollectionFailures = 3;
const int SnapshotGcInterval = 5;

var intervalMs = DefaultIntervalMs;
var requireAdmin = false;

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (arg is "--require-admin")
    {
        requireAdmin = true;
    }
    else if (arg.StartsWith("--interval=", StringComparison.Ordinal))
    {
        intervalMs = ParseInterval(arg["--interval=".Length..], intervalMs);
    }
    else if (arg is "--interval" && i + 1 < args.Length)
    {
        intervalMs = ParseInterval(args[++i], intervalMs);
    }
    else if (arg is "--help" or "-h")
    {
        Console.Error.WriteLine("usage: lhm-bridge [--interval <ms>] [--require-admin]");
        return 0;
    }
}

var isAdmin = IsRunningAsAdministrator();

// banner 先于任何采集写出，父进程可据此决定是否提权重启
Console.Error.WriteLine(JsonSerializer.Serialize(new BridgeBanner(
    Component: "lhm-bridge",
    IsAdmin: isAdmin,
    IntervalMs: intervalMs,
    Pid: Environment.ProcessId)));

if (!isAdmin)
{
    if (requireAdmin)
    {
        Console.Error.WriteLine("[lhm-bridge] FATAL: --require-admin set but process is not elevated");
        return 2;
    }
    Console.Error.WriteLine("[lhm-bridge] WARNING: not elevated; CPU/GPU temperature sensors will be unavailable");
}

var computer = BridgeComputerConfiguration.Create();

try
{
    computer.Open();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[lhm-bridge] Computer.Open() failed: {ex.Message}");
    return 1;
}

using var processVramCollector = new ProcessVramCollector();

using var cts = new CancellationTokenSource();

// 优雅退出：Ctrl+C（SIGINT）+ SIGTERM + SIGHUP。
// 三者都只置位 token，主循环收尾后走同一条 computer.Close() 路径并返回 0。
Console.CancelKeyPress += (_, e) => { e.Cancel = true; Cancel("SIGINT"); };

var signalRegistrations = new List<PosixSignalRegistration>();
foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
{
    try
    {
        signalRegistrations.Add(PosixSignalRegistration.Create(signal, ctx =>
        {
            ctx.Cancel = true;         // 抑制默认的立即终止
            Cancel(ctx.Signal.ToString());
        }));
    }
    catch (PlatformNotSupportedException)
    {
        // 该平台不支持此信号；Console.CancelKeyPress 仍覆盖 Ctrl+C
    }
}

Console.Error.WriteLine($"[lhm-bridge] started, interval={intervalMs}ms, emitting JSON Lines on stdout");

var visitor = new UpdateVisitor();
var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

var collectionFailures = new ConsecutiveFailureBudget(MaxConsecutiveCollectionFailures);
var successfulSnapshots = 0;
var exitCode = 0;
var cpuTemperatureUnavailableLogged = false;

while (!cts.IsCancellationRequested)
{
    try
    {
        computer.Accept(visitor);
        var snapshot = BuildSnapshot(
            computer,
            processVramCollector,
            ref cpuTemperatureUnavailableLogged);
        Console.WriteLine(JsonSerializer.Serialize(snapshot, options));
        Console.Out.Flush();
        collectionFailures.RecordSuccess();
        if (++successfulSnapshots % SnapshotGcInterval == 0)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }
    catch (Exception ex)
    {
        var failures = collectionFailures.RecordFailure();
        Console.Error.WriteLine(
            $"[lhm-bridge] collection error ({failures}/{MaxConsecutiveCollectionFailures}): {ex.Message}");

        if (collectionFailures.IsExhausted)
        {
            Console.Error.WriteLine(
                $"[lhm-bridge] FATAL: collection failed {failures} consecutive times; exiting for supervisor restart");
            exitCode = 3;
            break;
        }
    }

    try { await Task.Delay(intervalMs, cts.Token); }
    catch (OperationCanceledException) { break; }
}

foreach (var registration in signalRegistrations) registration.Dispose();
computer.Close();
Console.Error.WriteLine("[lhm-bridge] stopped");
return exitCode;

// ── helpers ─────────────────────────────────────────────────────────────────

void Cancel(string reason)
{
    if (cts.IsCancellationRequested) return;
    Console.Error.WriteLine($"[lhm-bridge] shutdown requested ({reason})");
    try { cts.Cancel(); } catch (ObjectDisposedException) { }
}

static int ParseInterval(string raw, int fallback) =>
    int.TryParse(raw, out var parsed) && parsed >= MinIntervalMs ? parsed : fallback;

static bool IsRunningAsAdministrator()
{
    if (!OperatingSystem.IsWindows()) return false;
    try
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    catch (Exception)
    {
        return false;
    }
}

// ── snapshot builder ────────────────────────────────────────────────────────

static LhmSnapshot BuildSnapshot(
    Computer computer,
    ProcessVramCollector processVramCollector,
    ref bool cpuTemperatureUnavailableLogged)
{
    var cpuTemperatureSensors = new List<LhmSensorReading>();
    var gpuTemperatureSensors = new List<LhmSensorReading>();
    var gpuLoadSensors = new List<LhmSensorReading>();
    var gpuMemoryUsedSensors = new List<LhmSensorReading>();
    var gpuMemoryTotalSensors = new List<LhmSensorReading>();
    var disks = new List<LhmDiskSnapshot>();
    double diskRead = 0, diskWrite = 0;

    foreach (var hw in computer.Hardware)
    {
        switch (hw.HardwareType)
        {
            case HardwareType.Cpu:
                foreach (var sensor in hw.Sensors)
                {
                    if (sensor.SensorType == SensorType.Temperature)
                    {
                        cpuTemperatureSensors.Add(new LhmSensorReading(
                            hw.Name, sensor.Name, sensor.SensorType, sensor.Value));
                    }
                }
                break;

            case HardwareType.GpuAmd:
            case HardwareType.GpuNvidia:
            case HardwareType.GpuIntel:
                foreach (var sensor in hw.Sensors)
                {
                    if (sensor.SensorType is SensorType.Temperature or SensorType.Load)
                    {
                        var reading = new LhmSensorReading(
                            hw.Name, sensor.Name, sensor.SensorType, sensor.Value);
                        if (sensor.SensorType == SensorType.Temperature)
                        {
                            gpuTemperatureSensors.Add(reading);
                        }
                        else
                        {
                            gpuLoadSensors.Add(reading);
                        }
                    }
                    else if (sensor.SensorType == SensorType.SmallData || sensor.SensorType == SensorType.Data)
                    {
                        // LHM 的 SmallData 为 MB，Data 为 GB；进入选择器前统一成 MB。
                        var name = sensor.Name;
                        var memoryMb = LhmSelection.NormalizeGpuMemoryMb(
                            sensor.SensorType,
                            sensor.Value);
                        if (name.Contains("Used", StringComparison.OrdinalIgnoreCase) &&
                            (name.Contains("Memory", StringComparison.OrdinalIgnoreCase) || name.Contains("VRAM", StringComparison.OrdinalIgnoreCase)))
                        {
                            gpuMemoryUsedSensors.Add(new LhmSensorReading(
                                hw.Name, name, SensorType.SmallData, memoryMb));
                        }
                        else if ((name.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("Available", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("Free", StringComparison.OrdinalIgnoreCase)) &&
                                 (name.Contains("Memory", StringComparison.OrdinalIgnoreCase) || name.Contains("VRAM", StringComparison.OrdinalIgnoreCase)))
                        {
                            gpuMemoryTotalSensors.Add(new LhmSensorReading(
                                hw.Name, name, SensorType.SmallData, memoryMb));
                        }
                    }
                }
                break;


            case HardwareType.Storage:
                {
                    var disk = BuildDiskSnapshot(hw);
                    if (disk != null)
                    {
                        disks.Add(disk);
                        diskRead += disk.ReadMbps ?? 0.0;
                        diskWrite += disk.WriteMbps ?? 0.0;
                    }
                    break;
                }
        }
    }

    var gpuMemoryUsed = LhmSelection.SelectGpuMemoryValue(gpuMemoryUsedSensors);
    var gpuMemoryTotal = LhmSelection.SelectGpuMemoryTotal(
        gpuMemoryTotalSensors,
        gpuMemoryUsedSensors);
    if (!gpuMemoryUsed.HasValue || !gpuMemoryTotal.HasValue)
    {
        var counterUsedMb = processVramCollector.CaptureSystemUsageMb();
        if (counterUsedMb.HasValue && processVramCollector.SystemCapacityMb.HasValue)
        {
            gpuMemoryUsed = counterUsedMb;
            gpuMemoryTotal = processVramCollector.SystemCapacityMb;
        }
    }
    var (cpuTemp, cpuTempLabel) = LhmSelection.SelectTemperatureWithLabel(cpuTemperatureSensors);
    if (!cpuTemp.HasValue && !cpuTemperatureUnavailableLogged)
    {
        cpuTemperatureUnavailableLogged = true;
        var sensorDetails = cpuTemperatureSensors.Count == 0
            ? "<none>"
            : string.Join(
                "; ",
                cpuTemperatureSensors.Select(sensor =>
                {
                    var value = sensor.Value is float present
                        ? present.ToString("R", CultureInfo.InvariantCulture)
                        : "null";
                    return $"{sensor.HardwareName}/{sensor.Name}={value}";
                }));
        Console.Error.WriteLine(
            $"[lhm-bridge] WARNING: CPU temperature unavailable after sensor selection; " +
            $"sensor_count={cpuTemperatureSensors.Count}; sensors={sensorDetails}");
    }
    var (gpuTemp, _) = LhmSelection.SelectTemperatureWithLabel(gpuTemperatureSensors);
    var gpuLoad = LhmSelection.SelectGpuLoad(gpuLoadSensors);
    var processGpuUsage = processVramCollector.CaptureGpuUsagePercent();
    var processVramMb = processVramCollector.CaptureUsageMb();

    return new LhmSnapshot(
        Timestamp: DateTime.UtcNow,
        CpuTemp: cpuTemp.HasValue ? Math.Round(cpuTemp.Value, 1) : null,
        CpuTempLabel: cpuTempLabel,
        GpuTemp: gpuTemp.HasValue ? Math.Round(gpuTemp.Value, 1) : null,
        GpuMemoryUsedMb: gpuMemoryUsed.HasValue ? Math.Round(gpuMemoryUsed.Value, 1) : null,
        GpuMemoryTotalMb: gpuMemoryTotal.HasValue ? Math.Round(gpuMemoryTotal.Value, 1) : null,
        GpuLoad: gpuLoad.HasValue ? Math.Round(gpuLoad.Value, 1) : null,
        DiskReadMbps: Math.Round(diskRead, 3),
        DiskWriteMbps: Math.Round(diskWrite, 3),
        ProcessGpuUsage: processGpuUsage,
        ProcessVramMb: processVramMb,
        Disks: disks
    );
}

static LhmDiskSnapshot? BuildDiskSnapshot(IHardware hw)
{
    double readBytesPerSecond = 0.0, writeBytesPerSecond = 0.0;
    var readFound = false;
    var writeFound = false;
    float? totalSpaceGib = null;
    float? freeSpaceGib = null;

    foreach (var sensor in hw.Sensors)
    {
        if (!sensor.Value.HasValue || sensor.Value.Value < 0) continue;

        var value = sensor.Value.Value;
        if (sensor.SensorType == SensorType.Throughput)
        {
            if (ContainsAny(sensor.Name, DiskSensorNamePatterns.Read) &&
                !ContainsAny(sensor.Name, DiskSensorNamePatterns.Write))
            {
                readFound = true;
                readBytesPerSecond += value;
            }
            else if (ContainsAny(sensor.Name, DiskSensorNamePatterns.Write))
            {
                writeFound = true;
                writeBytesPerSecond += value;
            }

            continue;
        }

        if (sensor.SensorType != SensorType.Data &&
            sensor.SensorType != SensorType.SmallData)
        {
            continue;
        }

        if (value > 0 &&
            ContainsAny(sensor.Name, DiskSensorNamePatterns.TotalSpace) &&
            (!totalSpaceGib.HasValue || value > totalSpaceGib.Value))
        {
            totalSpaceGib = value;
        }

        if (ContainsAny(sensor.Name, DiskSensorNamePatterns.FreeSpace) &&
            (!freeSpaceGib.HasValue || value > freeSpaceGib.Value))
        {
            freeSpaceGib = value;
        }
    }

    long? totalBytes = totalSpaceGib.HasValue
        ? ConvertGibToBytes(totalSpaceGib.Value)
        : null;
    long? usedBytes = null;
    if (totalBytes.HasValue && freeSpaceGib.HasValue)
    {
        var freeBytes = ConvertGibToBytes(freeSpaceGib.Value);
        usedBytes = Math.Clamp(totalBytes.Value - freeBytes, 0L, totalBytes.Value);
    }

    double? readMbps = readFound ? readBytesPerSecond / 1_048_576.0 : null;
    double? writeMbps = writeFound ? writeBytesPerSecond / 1_048_576.0 : null;

    if (!totalBytes.HasValue &&
        !usedBytes.HasValue &&
        !readMbps.HasValue &&
        !writeMbps.HasValue)
    {
        return null;
    }

    return new LhmDiskSnapshot(
        Name: hw.Name,
        TotalBytes: totalBytes,
        UsedBytes: usedBytes,
        ReadMbps: readMbps,
        WriteMbps: writeMbps);
}

static long ConvertGibToBytes(float gib) =>
    (long)Math.Round(gib * 1024.0 * 1024.0 * 1024.0);

static bool ContainsAny(string name, string[] patterns)
{
    foreach (var pattern in patterns)
    {
        if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return true;
    }

    return false;
}


internal sealed class ProcessVramCollector : IDisposable
{
    private const double BytesPerMegabyte = 1024.0 * 1024.0;
    private const string GpuClassRegistryPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private readonly bool _processCountersSupported;
    private readonly bool _adapterCountersSupported;
    private readonly bool _gpuEngineCountersSupported;
    private readonly Dictionary<string, PerformanceCounter>? _gpuEngineCounters;

    internal ProcessVramCollector()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        _gpuEngineCounters = new(StringComparer.OrdinalIgnoreCase);

        _processCountersSupported = CategoryExists("GPU Process Memory");
        _adapterCountersSupported = CategoryExists("GPU Adapter Memory");
        _gpuEngineCountersSupported = CategoryExists("GPU Engine");
        SystemCapacityMb = CaptureRegistryCapacityMb();
    }

    internal double? SystemCapacityMb { get; }

    internal IReadOnlyDictionary<int, double> CaptureUsageMb()
    {
        var usageByProcessId = new Dictionary<int, long>();
        if (!OperatingSystem.IsWindows() || !_processCountersSupported)
        {
            return new Dictionary<int, double>();
        }

        string[] instanceNames;
        try
        {
            instanceNames = new PerformanceCounterCategory("GPU Process Memory").GetInstanceNames();
        }
        catch (UnauthorizedAccessException)
        {
            return new Dictionary<int, double>();
        }
        catch (InvalidOperationException)
        {
            return new Dictionary<int, double>();
        }

        foreach (var instanceName in instanceNames)
        {
            if (!TryExtractProcessId(instanceName, out var processId))
            {
                continue;
            }

            try
            {
                using var counter = new PerformanceCounter(
                    "GPU Process Memory",
                    "Dedicated Usage",
                    instanceName,
                    readOnly: true);
                var bytes = counter.RawValue;
                if (bytes <= 0)
                {
                    continue;
                }

                usageByProcessId.TryGetValue(processId, out var current);
                usageByProcessId[processId] = checked(current + bytes);
            }
            catch (InvalidOperationException)
            {
                // 进程退出会使对应 counter instance 在枚举后失效。
            }
        }

        return usageByProcessId.ToDictionary(
            pair => pair.Key,
            pair => Math.Round(pair.Value / BytesPerMegabyte, 1));
    }
    internal IReadOnlyDictionary<int, double> CaptureGpuUsagePercent()
    {
        var usageByProcessId = new Dictionary<int, double>();
        if (!OperatingSystem.IsWindows() ||
            !_gpuEngineCountersSupported ||
            _gpuEngineCounters is null)
        {
            return usageByProcessId;
        }

        string[] instanceNames;
        try
        {
            instanceNames = new PerformanceCounterCategory("GPU Engine").GetInstanceNames();
        }
        catch (UnauthorizedAccessException)
        {
            return usageByProcessId;
        }
        catch (InvalidOperationException)
        {
            return usageByProcessId;
        }

        var activeInstances = new HashSet<string>(instanceNames, StringComparer.OrdinalIgnoreCase);
        foreach (var instanceName in instanceNames)
        {
            if (!TryExtractProcessId(instanceName, out var processId))
            {
                continue;
            }

            if (!_gpuEngineCounters.TryGetValue(instanceName, out var counter))
            {
                try
                {
                    counter = new PerformanceCounter(
                        "GPU Engine",
                        "Utilization Percentage",
                        instanceName,
                        readOnly: true);
                    counter.NextValue();
                    _gpuEngineCounters.Add(instanceName, counter);
                }
                catch (InvalidOperationException)
                {
                    counter?.Dispose();
                }
                continue;
            }

            try
            {
                var usage = counter.NextValue();
                if (!float.IsFinite(usage) || usage <= 0)
                {
                    continue;
                }

                usageByProcessId.TryGetValue(processId, out var current);
                // 与 Windows 系统口径一致：进程 GPU 百分比取最忙的 engine。
                usageByProcessId[processId] = Math.Max(current, Math.Min(100.0, usage));
            }
            catch (InvalidOperationException)
            {
                counter.Dispose();
                _gpuEngineCounters.Remove(instanceName);
            }
        }

        foreach (var staleInstance in _gpuEngineCounters.Keys
                     .Where(instance => !activeInstances.Contains(instance))
                     .ToArray())
        {
            _gpuEngineCounters.Remove(staleInstance, out var counter);
            counter?.Dispose();
        }

        return usageByProcessId.ToDictionary(
            pair => pair.Key,
            pair => Math.Round(pair.Value, 1));
    }


    internal double? CaptureSystemUsageMb()
    {
        if (!OperatingSystem.IsWindows() || !_adapterCountersSupported)
        {
            return null;
        }

        try
        {
            long totalBytes = 0;
            var category = new PerformanceCounterCategory("GPU Adapter Memory");
            foreach (var instanceName in category.GetInstanceNames())
            {
                try
                {
                    using var counter = new PerformanceCounter(
                        "GPU Adapter Memory",
                        "Dedicated Usage",
                        instanceName,
                        readOnly: true);
                    var bytes = counter.RawValue;
                    if (bytes > 0)
                    {
                        totalBytes = checked(totalBytes + bytes);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Adapter counter instance 可能在枚举后失效。
                }
            }

            return Math.Round(totalBytes / BytesPerMegabyte, 1);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool CategoryExists(string categoryName)
    {
        try
        {
            return PerformanceCounterCategory.Exists(categoryName);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static double? CaptureRegistryCapacityMb()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var classKey = baseKey.OpenSubKey(GpuClassRegistryPath);
            if (classKey == null)
            {
                return null;
            }

            long totalBytes = 0;
            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                using var adapterKey = classKey.OpenSubKey(subKeyName);
                var bytes = ParseRegistryMemoryBytes(
                    adapterKey?.GetValue("HardwareInformation.qwMemorySize"));
                if (bytes > 0)
                {
                    totalBytes = checked(totalBytes + bytes);
                }
            }

            return totalBytes > 0
                ? Math.Round(totalBytes / BytesPerMegabyte, 1)
                : null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    internal static long ParseRegistryMemoryBytes(object? value)
    {
        return value switch
        {
            long bytes when bytes > 0 => bytes,
            int bytes when bytes > 0 => bytes,
            byte[] bytes when bytes.Length >= sizeof(long) =>
                Math.Max(0, BitConverter.ToInt64(bytes, 0)),
            _ => 0,
        };
    }

    internal static bool TryExtractProcessId(string instanceName, out int processId)
    {
        processId = 0;
        const string marker = "pid_";
        var start = instanceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        start += marker.Length;
        var end = instanceName.IndexOf('_', start);
        if (end < 0)
        {
            end = instanceName.Length;
        }

        return int.TryParse(instanceName[start..end], out processId);
    }
    public void Dispose()
    {
        if (!OperatingSystem.IsWindows() || _gpuEngineCounters is null)
        {
            return;
        }

        foreach (var counter in _gpuEngineCounters.Values)
        {
            counter.Dispose();
        }
        _gpuEngineCounters.Clear();
    }

}

internal static class BridgeComputerConfiguration
{
    internal static Computer Create() =>
        new()
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = false,
            IsStorageEnabled = true,
            IsMotherboardEnabled = false,
            IsControllerEnabled = false,
        };
}

internal sealed class ConsecutiveFailureBudget
{
    private readonly int _limit;
    private int _failures;

    internal ConsecutiveFailureBudget(int limit)
    {
        _limit = limit > 0
            ? limit
            : throw new ArgumentOutOfRangeException(nameof(limit));
    }

    internal int RecordFailure() => ++_failures;

    internal void RecordSuccess() => _failures = 0;

    internal bool IsExhausted => _failures >= _limit;
}

internal readonly record struct LhmSensorReading(
    string HardwareName,
    string Name,
    SensorType SensorType,
    float? Value);


internal static class LhmSelection
{

    private static readonly string[] EngineSensorNamePatterns =
    [
        "D3D 3D",
        "D3D Compute",
        "D3D Copy",
        "D3D Video",
        "D3D Video Decode",
        "D3D Video Encode",
        "D3D Video Processor",
        "D3D Video Codec",
        "D3D Video Jpeg",
        "Graphics",
        "Compute",
        "Copy",
        "Video",
    ];

    private static readonly string[] CoreSensorNamePatterns =
    [
        "GPU Core",
        "GPU Usage",
        "GPU Load",
    ];


    internal static (double? Value, string? Label) SelectTemperatureWithLabel(
        IEnumerable<LhmSensorReading> sensors)
    {
        LhmSensorReading? coreMax = null;
        LhmSensorReading? package = null;
        LhmSensorReading? coreAverage = null;
        LhmSensorReading? highest = null;

        foreach (var sensor in sensors)
        {
            if (sensor.SensorType != SensorType.Temperature ||
                sensor.Value is not float value ||
                !float.IsFinite(value) ||
                value <= 0 ||
                sensor.Name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (sensor.Name.Equals("Core Max", StringComparison.OrdinalIgnoreCase) &&
                !coreMax.HasValue)
            {
                coreMax = sensor;
            }

            if ((sensor.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase) ||
                 sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                 sensor.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                 sensor.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
                 sensor.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)) &&
                !package.HasValue)
            {
                package = sensor;
            }

            if (sensor.Name.Equals("Core Average", StringComparison.OrdinalIgnoreCase) &&
                !coreAverage.HasValue)
            {
                coreAverage = sensor;
            }

            if (IsHigher(value, highest))
            {
                highest = sensor;
            }
        }

        var selected = coreMax ?? package ?? coreAverage ?? highest;
        return selected is { Value: float selectedValue }
            ? ((double)selectedValue, selected.Value.Name)
            : (null, null);
    }

    internal static double? SelectGpuLoad(IEnumerable<LhmSensorReading> sensors)
    {
        LhmSensorReading? maxCore = null;
        LhmSensorReading? maxEngine = null;
        LhmSensorReading? maxAny = null;

        foreach (var sensor in sensors)
        {
            if (sensor.SensorType != SensorType.Load ||
                sensor.Value is not float value ||
                !float.IsFinite(value))
            {
                continue;
            }

            if (IsHigher(value, maxAny))
            {
                maxAny = sensor;
            }

            if (ContainsAny(sensor.Name, EngineSensorNamePatterns) && IsHigher(value, maxEngine))
            {
                maxEngine = sensor;
            }

            if (ContainsAny(sensor.Name, CoreSensorNamePatterns) && IsHigher(value, maxCore))
            {
                maxCore = sensor;
            }
        }

        if (TryGetPositive(maxCore, out var coreLoad))
        {
            return coreLoad;
        }

        if (TryGetPositive(maxEngine, out var engineLoad))
        {
            return engineLoad;
        }

        return TryGetPositive(maxAny, out var fallbackLoad) ? fallbackLoad : null;
    }

    /// <summary>
    /// 把 LHM GPU memory 传感器统一转换成 MB。
    /// </summary>
    internal static float? NormalizeGpuMemoryMb(SensorType sensorType, float? value)
    {
        if (value is not float present || !float.IsFinite(present) || present < 0)
        {
            return null;
        }

        return sensorType == SensorType.Data ? present * 1024.0f : present;
    }

    /// <summary>
    /// 按 hardware 选择最大的有效 used 值后求和，避免重复传感器导致重复计数。
    /// </summary>
    internal static double? SelectGpuMemoryValue(IEnumerable<LhmSensorReading> sensors)
    {
        var maximumByHardware = MaxByHardware(sensors, static _ => true, allowZero: false);
        return maximumByHardware.Count == 0
            ? null
            : maximumByHardware.Values.Sum();
    }

    /// <summary>
    /// 选择 GPU 总显存（MB）。每个 hardware 优先取显式 Total；
    /// 否则只用同一 hardware 的 Used + Available/Free 估算。
    /// </summary>
    internal static double? SelectGpuMemoryTotal(
        IEnumerable<LhmSensorReading> totalSensors,
        IEnumerable<LhmSensorReading> usedSensors)
    {
        var totalCandidates = totalSensors.ToArray();
        var explicitTotalByHardware = MaxByHardware(
            totalCandidates,
            static sensor => sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase),
            allowZero: false);
        var availableByHardware = MaxByHardware(
            totalCandidates,
            static sensor => !sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase),
            allowZero: true);
        var usedByHardware = MaxByHardware(
            usedSensors,
            static _ => true,
            allowZero: false);

        foreach (var hardware in usedByHardware.Keys)
        {
            if (!explicitTotalByHardware.ContainsKey(hardware) &&
                !availableByHardware.ContainsKey(hardware))
            {
                return null;
            }
        }

        var totalMb = explicitTotalByHardware.Values.Sum();
        var hasCapacity = explicitTotalByHardware.Count > 0;
        foreach (var (hardware, availableMb) in availableByHardware)
        {
            if (explicitTotalByHardware.ContainsKey(hardware))
            {
                continue;
            }
            if (!usedByHardware.TryGetValue(hardware, out var usedMb))
            {
                return null;
            }

            totalMb += usedMb + availableMb;
            hasCapacity = true;
        }

        return hasCapacity && totalMb > 0 ? totalMb : null;
    }

    private static Dictionary<string, double> MaxByHardware(
        IEnumerable<LhmSensorReading> sensors,
        Func<LhmSensorReading, bool> include,
        bool allowZero)
    {
        var maximumByHardware = new Dictionary<string, double>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var sensor in sensors)
        {
            if (!include(sensor) ||
                sensor.Value is not float value ||
                !float.IsFinite(value) ||
                (allowZero ? value < 0 : value <= 0))
            {
                continue;
            }

            if (!maximumByHardware.TryGetValue(sensor.HardwareName, out var current) ||
                value > current)
            {
                maximumByHardware[sensor.HardwareName] = value;
            }
        }

        return maximumByHardware;
    }


    private static bool IsHigher(float value, LhmSensorReading? candidate) =>
        candidate is not { Value: float current } || value > current;

    private static bool TryGetPositive(LhmSensorReading? candidate, out double value)
    {
        if (candidate is { Value: float candidateValue } && candidateValue > 0)
        {
            value = candidateValue;
            return true;
        }

        value = 0.0;
        return false;
    }


    private static bool ContainsAny(string name, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
// ── types ───────────────────────────────────────────────────────────────────

record BridgeBanner(
    [property: JsonPropertyName("component")] string Component,
    [property: JsonPropertyName("is_admin")] bool IsAdmin,
    [property: JsonPropertyName("interval_ms")] int IntervalMs,
    [property: JsonPropertyName("pid")] int Pid
);

record LhmDiskSnapshot(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("total_bytes")] long? TotalBytes,
    [property: JsonPropertyName("used_bytes")] long? UsedBytes,
    [property: JsonPropertyName("read_mbps")] double? ReadMbps,
    [property: JsonPropertyName("write_mbps")] double? WriteMbps
);

record LhmSnapshot(
    [property: JsonPropertyName("ts")] DateTime Timestamp,
    [property: JsonPropertyName("cpu_temp")] double? CpuTemp,
    [property: JsonPropertyName("cpu_temp_label")] string? CpuTempLabel,
    [property: JsonPropertyName("gpu_temp")] double? GpuTemp,
    [property: JsonPropertyName("gpu_memory_used_mb")] double? GpuMemoryUsedMb,
    [property: JsonPropertyName("gpu_memory_total_mb")] double? GpuMemoryTotalMb,
    [property: JsonPropertyName("gpu_load")] double? GpuLoad,
    [property: JsonPropertyName("disk_read_mbps")] double DiskReadMbps,
    [property: JsonPropertyName("disk_write_mbps")] double DiskWriteMbps,
    [property: JsonPropertyName("process_gpu_usage")] IReadOnlyDictionary<int, double> ProcessGpuUsage,
    [property: JsonPropertyName("process_vram_mb")] IReadOnlyDictionary<int, double> ProcessVramMb,
    [property: JsonPropertyName("disks")] IReadOnlyList<LhmDiskSnapshot> Disks
);

static class DiskSensorNamePatterns
{
    public static readonly string[] Read = ["Read", "Read Rate", "Read Speed"];
    public static readonly string[] Write = ["Write", "Write Rate", "Write Speed"];
    public static readonly string[] TotalSpace = ["Total Space", "Total Size", "Total Capacity"];
    public static readonly string[] FreeSpace = ["Free Space", "Available Space", "Available", "Free"];
}

class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) { computer.Traverse(this); }
    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware) sub.Accept(this);
    }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}
