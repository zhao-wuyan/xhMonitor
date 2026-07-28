// lhm-bridge — LibreHardwareMonitor 传感器读取子进程
//
// 由 xhm-service 以子进程方式拉起，通过 stdout JSON Lines 输出系统级传感器快照。
// 采集范围与原 XhMonitor.Core/Services/LibreHardwareManager.cs 对齐：
//   - CPU Temperature（对应 SystemMetricProvider.GetTemperatures）
//   - GPU Temperature + Load（对应 LibreHardwareMonitorGpuProvider）
//   - Network Throughput（对应 SystemMetricProvider.GetNetworkSpeed via LHM）
//   - Storage Throughput（对应 SystemMetricProvider.GetDiskUsages via LHM）
//
// 契约：
//   stdout — 每行一个 LhmSnapshot JSON；除此之外不写任何内容
//   stderr — 诊断日志；首行固定为 banner JSON（含 is_admin），供父进程探测能力
//   退出码 — 0 优雅退出 / 1 LHM 初始化失败 / 2 --require-admin 且非管理员

using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibreHardwareMonitor.Hardware;

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

var physicalAdapterSignatures = GetPhysicalAdapterSignatures();

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

while (!cts.IsCancellationRequested)
{
    try
    {
        computer.Accept(visitor);
        var snapshot = BuildSnapshot(computer, physicalAdapterSignatures);
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
    IReadOnlyCollection<string> physicalAdapterSignatures)
{
    var cpuTemperatureSensors = new List<LhmSensorReading>();
    var gpuTemperatureSensors = new List<LhmSensorReading>();
    var gpuLoadSensors = new List<LhmSensorReading>();
    var networkSensors = new List<LhmSensorReading>();
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
                }
                break;

            case HardwareType.Network:
                foreach (var sensor in hw.Sensors)
                {
                    if (sensor.SensorType == SensorType.Throughput)
                    {
                        networkSensors.Add(new LhmSensorReading(
                            hw.Name, sensor.Name, sensor.SensorType, sensor.Value));
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

    var (cpuTemp, cpuTempLabel) = LhmSelection.SelectTemperatureWithLabel(cpuTemperatureSensors);
    var (gpuTemp, _) = LhmSelection.SelectTemperatureWithLabel(gpuTemperatureSensors);
    var gpuLoad = LhmSelection.SelectGpuLoad(gpuLoadSensors);
    var (netUp, netDown) = LhmSelection.SelectNetworkThroughput(
        networkSensors,
        physicalAdapterSignatures);

    return new LhmSnapshot(
        Timestamp:       DateTime.UtcNow,
        CpuTemp:         cpuTemp.HasValue ? Math.Round(cpuTemp.Value, 1) : null,
        CpuTempLabel:    cpuTempLabel,
        GpuTemp:         gpuTemp.HasValue ? Math.Round(gpuTemp.Value, 1) : null,
        GpuLoad:         gpuLoad.HasValue ? Math.Round(gpuLoad.Value,  1) : null,
        NetUploadMbps:   LhmSelection.BytesPerSecondToMbps(netUp),
        NetDownloadMbps: LhmSelection.BytesPerSecondToMbps(netDown),
        DiskReadMbps:    Math.Round(diskRead,  3),
        DiskWriteMbps:   Math.Round(diskWrite, 3),
        Disks:           disks
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
        Name:       hw.Name,
        TotalBytes: totalBytes,
        UsedBytes:  usedBytes,
        ReadMbps:   readMbps,
        WriteMbps:  writeMbps);
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

static IReadOnlyCollection<string> GetPhysicalAdapterSignatures()
{
    var signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    try
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(adapter.Name))
            {
                signatures.Add(adapter.Name);
            }

            if (!string.IsNullOrWhiteSpace(adapter.Description) &&
                !string.Equals(adapter.Description, adapter.Name, StringComparison.OrdinalIgnoreCase))
            {
                signatures.Add(adapter.Description);
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[lhm-bridge] network adapter verification failed: {ex.Message}");
    }

    return signatures;
}

internal static class BridgeComputerConfiguration
{
    internal static Computer Create() =>
        new()
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = false,
            IsNetworkEnabled = true,
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

internal enum NetworkAdapterCategory
{
    Physical,
    Virtual,
    Unknown,
}

internal readonly record struct NetworkAdapterThroughput(
    string HardwareName,
    double UploadBytesPerSecond,
    double DownloadBytesPerSecond,
    NetworkAdapterCategory Category);

internal static class LhmSelection
{
    private static readonly string[] VirtualAdapterKeywords =
    [
        "vEthernet",
        "Hyper-V",
        "VirtualBox",
        "VMware",
        "TAP-",
        "VPN",
        "Radmin",
        "Loopback",
        "Pseudo",
        "WireGuard",
        "OpenVPN",
        "Tun",
        "Fortinet",
        "Cisco AnyConnect",
        "TeamViewer",
        "AnyDesk",
        "Kernel",
    ];

    private static readonly string[] UploadSensorNamePatterns =
    [
        "upload",
        "send",
        "sent",
        "tx",
    ];

    private static readonly string[] DownloadSensorNamePatterns =
    [
        "download",
        "receive",
        "received",
        "rx",
    ];

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

    internal static double BytesPerSecondToMbps(double bytesPerSecond) =>
        bytesPerSecond / 1_048_576.0;

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

    internal static (double UploadBytesPerSecond, double DownloadBytesPerSecond)
        SelectNetworkThroughput(
            IEnumerable<LhmSensorReading> sensors,
            IReadOnlyCollection<string> physicalAdapterSignatures)
    {
        var byAdapter = new Dictionary<string, (double Upload, double Download)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var sensor in sensors)
        {
            if (sensor.SensorType != SensorType.Throughput ||
                string.IsNullOrWhiteSpace(sensor.HardwareName))
            {
                continue;
            }

            if (!byAdapter.TryGetValue(sensor.HardwareName, out var throughput))
            {
                throughput = (0.0, 0.0);
            }

            if (sensor.Value is float value && float.IsFinite(value) && value > 0)
            {
                if (ContainsAny(sensor.Name, UploadSensorNamePatterns))
                {
                    throughput.Upload += value;
                }
                else if (ContainsAny(sensor.Name, DownloadSensorNamePatterns))
                {
                    throughput.Download += value;
                }
            }

            byAdapter[sensor.HardwareName] = throughput;
        }

        var physical = new List<NetworkAdapterThroughput>();
        var virtualAdapters = new List<NetworkAdapterThroughput>();
        var unknown = new List<NetworkAdapterThroughput>();
        foreach (var (hardwareName, throughput) in byAdapter)
        {
            var adapter = new NetworkAdapterThroughput(
                hardwareName,
                throughput.Upload,
                throughput.Download,
                GetNetworkAdapterCategory(hardwareName, physicalAdapterSignatures));
            switch (adapter.Category)
            {
                case NetworkAdapterCategory.Physical:
                    physical.Add(adapter);
                    break;
                case NetworkAdapterCategory.Virtual:
                    virtualAdapters.Add(adapter);
                    break;
                default:
                    unknown.Add(adapter);
                    break;
            }
        }

        if (physical.Count > 0)
        {
            return Sum(physical);
        }

        if (virtualAdapters.Count > 0)
        {
            return Max(virtualAdapters);
        }

        return Sum(unknown);
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

    private static NetworkAdapterCategory GetNetworkAdapterCategory(
        string hardwareName,
        IReadOnlyCollection<string> physicalAdapterSignatures)
    {
        if (ContainsAny(hardwareName, VirtualAdapterKeywords))
        {
            return NetworkAdapterCategory.Virtual;
        }

        foreach (var signature in physicalAdapterSignatures)
        {
            if (!string.IsNullOrWhiteSpace(signature) &&
                (hardwareName.Contains(signature, StringComparison.OrdinalIgnoreCase) ||
                 signature.Contains(hardwareName, StringComparison.OrdinalIgnoreCase)))
            {
                return NetworkAdapterCategory.Physical;
            }
        }

        return NetworkAdapterCategory.Unknown;
    }

    private static (double UploadBytesPerSecond, double DownloadBytesPerSecond)
        Sum(IReadOnlyList<NetworkAdapterThroughput> adapters)
    {
        double upload = 0.0;
        double download = 0.0;
        foreach (var adapter in adapters)
        {
            upload += adapter.UploadBytesPerSecond;
            download += adapter.DownloadBytesPerSecond;
        }

        return (upload, download);
    }

    private static (double UploadBytesPerSecond, double DownloadBytesPerSecond)
        Max(IReadOnlyList<NetworkAdapterThroughput> adapters)
    {
        NetworkAdapterThroughput? best = null;
        var bestTotal = double.MinValue;
        foreach (var adapter in adapters)
        {
            var total = adapter.UploadBytesPerSecond + adapter.DownloadBytesPerSecond;
            if (total > bestTotal)
            {
                bestTotal = total;
                best = adapter;
            }
        }

        return best is { } selectedAdapter
            ? (selectedAdapter.UploadBytesPerSecond, selectedAdapter.DownloadBytesPerSecond)
            : (0.0, 0.0);
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
    [property: JsonPropertyName("component")]   string Component,
    [property: JsonPropertyName("is_admin")]    bool   IsAdmin,
    [property: JsonPropertyName("interval_ms")] int    IntervalMs,
    [property: JsonPropertyName("pid")]         int    Pid
);

record LhmDiskSnapshot(
    [property: JsonPropertyName("name")]        string  Name,
    [property: JsonPropertyName("total_bytes")] long?   TotalBytes,
    [property: JsonPropertyName("used_bytes")]  long?   UsedBytes,
    [property: JsonPropertyName("read_mbps")]   double? ReadMbps,
    [property: JsonPropertyName("write_mbps")]  double? WriteMbps
);

record LhmSnapshot(
    [property: JsonPropertyName("ts")]               DateTime  Timestamp,
    [property: JsonPropertyName("cpu_temp")]          double?   CpuTemp,
    [property: JsonPropertyName("cpu_temp_label")]    string?   CpuTempLabel,
    [property: JsonPropertyName("gpu_temp")]          double?   GpuTemp,
    [property: JsonPropertyName("gpu_load")]          double?   GpuLoad,
    [property: JsonPropertyName("net_up_mbps")]       double    NetUploadMbps,
    [property: JsonPropertyName("net_down_mbps")]     double    NetDownloadMbps,
    [property: JsonPropertyName("disk_read_mbps")]    double                    DiskReadMbps,
    [property: JsonPropertyName("disk_write_mbps")]   double                    DiskWriteMbps,
    [property: JsonPropertyName("disks")]             IReadOnlyList<LhmDiskSnapshot> Disks
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
