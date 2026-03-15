using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

/// <summary>
/// 系统级指标提供者 - 统一管理系统总量指标采集
/// </summary>
public class SystemMetricProvider : ISystemMetricProvider, IAsyncDisposable, IDisposable
{
    private readonly Dictionary<string, IMetricProvider> _providers;
    private readonly ILogger<SystemMetricProvider>? _logger;
    private readonly ILibreHardwareManager? _hardwareManager;
    private readonly IPowerProvider? _powerProvider;
    private int _hardwareManagerInitAttempted;
    private int _storageSensorsMissingLogged;
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
        "Kernel"
    ];

    private static readonly string[] DiskReadSensorNamePatterns =
    [
        "Read",
        "Read Rate",
        "Read Speed"
    ];

    private static readonly string[] DiskWriteSensorNamePatterns =
    [
        "Write",
        "Write Rate",
        "Write Speed"
    ];

    private static readonly string[] DiskTotalSpaceSensorNamePatterns =
    [
        "Total Space",
        "Total Size",
        "Total Capacity"
    ];

    private static readonly string[] DiskFreeSpaceSensorNamePatterns =
    [
        "Free Space",
        "Available Space",
        "Available",
        "Free"
    ];
    private readonly HashSet<string> _verifiedPhysicalAdapters = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _verificationLock = new(1, 1);
    private bool _disposed;
    private int _networkDiagnosticsLogged;
    private int _networkUnavailableLogged;
    private int _networkSensorsMissingLogged;

    private enum NetworkAdapterCategory
    {
        Physical,
        Virtual,
        Unknown
    }

    private enum NetworkAdapterAggregationMode
    {
        Sum,
        Max
    }

    private sealed record NetworkAdapterThroughput(
        string HardwareName,
        double UploadBytesPerSecond,
        double DownloadBytesPerSecond,
        NetworkAdapterCategory Category);

    public SystemMetricProvider(
        IEnumerable<IMetricProvider> providers,
        ILogger<SystemMetricProvider>? logger = null,
        ILibreHardwareManager? hardwareManager = null,
        IPowerProvider? powerProvider = null,
        IReadOnlyCollection<string>? verifiedPhysicalAdapterSignatures = null)
    {
        _logger = logger;
        _providers = BuildProviderMap(providers, logger);
        _hardwareManager = hardwareManager;
        _powerProvider = powerProvider;

        if (verifiedPhysicalAdapterSignatures != null)
        {
            foreach (var signature in verifiedPhysicalAdapterSignatures
                         .Where(s => !string.IsNullOrWhiteSpace(s))
                         .Select(s => s.Trim())
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _verifiedPhysicalAdapters.Add(signature);
            }
        }
        else
        {
            StartPhysicalAdapterVerification();
        }
    }

    private void StartPhysicalAdapterVerification()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                await VerifyPhysicalAdaptersAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[SystemMetricProvider] Failed to verify physical adapters");
            }
        });
    }

    private static Dictionary<string, IMetricProvider> BuildProviderMap(
        IEnumerable<IMetricProvider> providers,
        ILogger<SystemMetricProvider>? logger)
    {
        var map = new Dictionary<string, IMetricProvider>(StringComparer.OrdinalIgnoreCase);
        if (providers == null)
        {
            return map;
        }

        foreach (var provider in providers)
        {
            if (provider == null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(provider.MetricId))
            {
                logger?.LogWarning("SystemMetricProvider: provider MetricId is empty: {ProviderType}", provider.GetType().FullName);
                continue;
            }

            if (!map.TryAdd(provider.MetricId, provider))
            {
                logger?.LogWarning("SystemMetricProvider: duplicate MetricId ignored: {MetricId}", provider.MetricId);
            }
        }

        return map;
    }

    private IMetricProvider? GetProvider(string metricId)
    {
        if (string.IsNullOrWhiteSpace(metricId))
        {
            return null;
        }

        _providers.TryGetValue(metricId, out var provider);
        return provider;
    }

    private bool HasProvider(string metricId)
    {
        return GetProvider(metricId) != null;
    }

    /// <summary>
    /// 预热所有性能计数器
    /// </summary>
    public async Task WarmupAsync()
    {
        List<Task> tasks = [];

        foreach (var provider in _providers.Values)
        {
            switch (provider)
            {
                case CpuMetricProvider cpuMetricProvider:
                    tasks.Add(cpuMetricProvider.WarmupAsync());
                    break;
                case GpuMetricProvider gpuMetricProvider:
                    tasks.Add(gpuMetricProvider.WarmupAsync());
                    break;
            }
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }
    }

    /// <summary>
    /// 获取硬件限制(最大容量)
    /// </summary>
    public async Task<HardwareLimits> GetHardwareLimitsAsync()
    {
        var vramMaxTask = GetMaxVramAsync();

        var maxMemory = GetMaxMemory();
        var maxVram = await vramMaxTask;

        return new HardwareLimits
        {
            MaxMemory = maxMemory,
            MaxVram = maxVram
        };
    }

    /// <summary>
    /// 获取 VRAM 最大容量
    /// </summary>
    private async Task<double> GetMaxVramAsync()
    {
        var vramProvider = GetProvider("vram");
        if (vramProvider == null)
        {
            return 0.0;
        }

        // Try GetVramMetricsAsync first (works for any provider that implements it)
        var vramMetrics = await vramProvider.GetVramMetricsAsync();
        if (vramMetrics != null && vramMetrics.IsValid)
        {
            return vramMetrics.Total;
        }

        // Fallback to the provider's total (legacy providers return max capacity here)
        return await vramProvider.GetSystemTotalAsync();
    }

    /// <summary>
    /// 获取系统使用率
    /// </summary>
    public async Task<SystemUsage> GetSystemUsageAsync()
    {
        var cpuTask = GetProvider("cpu")?.GetSystemTotalAsync() ?? Task.FromResult(0.0);
        var gpuTask = GetProvider("gpu")?.GetSystemTotalAsync() ?? Task.FromResult(0.0);
        var vramTask = GetVramUsageAsync();
        var powerTask = _powerProvider != null && _powerProvider.IsSupported()
            ? _powerProvider.GetStatusAsync()
            : Task.FromResult<PowerStatus?>(null);

        var totalMemory = GetMemoryUsage();

        await Task.WhenAll(cpuTask, gpuTask, vramTask, powerTask);
        var totalCpu = await cpuTask;
        var totalGpu = await gpuTask;
        var totalVram = await vramTask;
        var powerStatus = await powerTask;
        var (uploadSpeed, downloadSpeed) = GetNetworkSpeed();
        var disks = GetDiskUsages();

        return new SystemUsage
        {
            TotalCpu = totalCpu,
            TotalGpu = totalGpu,
            TotalMemory = totalMemory,
            TotalVram = totalVram,
            UploadSpeed = uploadSpeed,
            DownloadSpeed = downloadSpeed,
            Disks = disks,
            PowerAvailable = powerStatus != null,
            TotalPower = powerStatus?.CurrentWatts ?? 0.0,
            MaxPower = powerStatus?.LimitWatts ?? 0.0,
            PowerSchemeIndex = powerStatus?.SchemeIndex,
            Timestamp = DateTime.UtcNow
        };
    }

    private double GetMaxMemory()
    {
        if (OperatingSystem.IsWindows() && TryGetPhysicalMemoryDetails(out var totalMb, out _))
        {
            return totalMb;
        }
        return 0.0;
    }

    private double GetMemoryUsage()
    {
        if (OperatingSystem.IsWindows() && TryGetPhysicalMemoryDetails(out var totalMb, out var availMb))
        {
            return totalMb - availMb;
        }
        return 0.0;
    }

    private async Task<double> GetVramUsageAsync()
    {
        var vramProvider = GetProvider("vram");
        if (vramProvider == null)
        {
            return 0.0;
        }

        var vramMetrics = await vramProvider.GetVramMetricsAsync();
        if (vramMetrics != null && vramMetrics.IsValid)
        {
            _logger?.LogInformation("[SystemMetricProvider] GetVramUsageAsync: Used={Used} MB, Total={Total} MB",
                vramMetrics.Used, vramMetrics.Total);
            return vramMetrics.Used;
        }

        return await vramProvider.GetSystemTotalAsync();
    }

    private static readonly string[] UploadSensorNamePatterns =
    [
        "upload",
        "send",
        "sent",
        "tx"
    ];

    private static readonly string[] DownloadSensorNamePatterns =
    [
        "download",
        "receive",
        "received",
        "rx"
    ];

    private static bool IsVirtualAdapter(string? hardwareName)
    {
        if (string.IsNullOrWhiteSpace(hardwareName))
        {
            return false;
        }

        foreach (var keyword in VirtualAdapterKeywords)
        {
            if (hardwareName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task VerifyPhysicalAdaptersAsync()
    {
        try
        {
            List<string> adapterSignatures = [];
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                    adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(adapter.Name))
                {
                    adapterSignatures.Add(adapter.Name);
                }

                if (!string.IsNullOrWhiteSpace(adapter.Description) &&
                    !string.Equals(adapter.Description, adapter.Name, StringComparison.OrdinalIgnoreCase))
                {
                    adapterSignatures.Add(adapter.Description);
                }
            }

            List<string> verifiedAdapters = [];
            await _verificationLock.WaitAsync();
            try
            {
                _verifiedPhysicalAdapters.Clear();
                foreach (var signature in adapterSignatures.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    _verifiedPhysicalAdapters.Add(signature);
                }

                verifiedAdapters.AddRange(_verifiedPhysicalAdapters);
            }
            finally
            {
                _verificationLock.Release();
            }

            if (verifiedAdapters.Count > 0)
            {
                _logger?.LogInformation("[SystemMetricProvider] Verified physical adapter signatures: {Adapters}",
                    string.Join(", ", verifiedAdapters));
            }
            else
            {
                _logger?.LogDebug("[SystemMetricProvider] No physical adapters detected for verification");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SystemMetricProvider] Failed to verify physical adapters");
        }
    }

    private bool HasVerifiedPhysicalAdapters()
    {
        _verificationLock.Wait();
        try
        {
            return _verifiedPhysicalAdapters.Count > 0;
        }
        finally
        {
            _verificationLock.Release();
        }
    }

    private bool IsPhysicalAdapterVerified(string? hardwareName)
    {
        if (string.IsNullOrWhiteSpace(hardwareName))
        {
            return false;
        }

        _verificationLock.Wait();
        try
        {
            if (_verifiedPhysicalAdapters.Contains(hardwareName))
            {
                return true;
            }

            foreach (var signature in _verifiedPhysicalAdapters)
            {
                if (string.IsNullOrWhiteSpace(signature))
                {
                    continue;
                }

                if (hardwareName.Contains(signature, StringComparison.OrdinalIgnoreCase) ||
                    signature.Contains(hardwareName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            _verificationLock.Release();
        }
    }

    private void EnsureHardwareManagerInitialized()
    {
        if (_hardwareManager == null || _hardwareManager.IsAvailable)
        {
            return;
        }

        if (Interlocked.Exchange(ref _hardwareManagerInitAttempted, 1) == 1)
        {
            return;
        }

        try
        {
            _hardwareManager.Initialize();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[SystemMetricProvider] Failed to initialize LibreHardwareManager");
        }
    }

    private (double UploadSpeed, double DownloadSpeed) GetNetworkSpeed()
    {
        EnsureHardwareManagerInitialized();
        if (_hardwareManager == null || !_hardwareManager.IsAvailable)
        {
            LogNetworkUnavailableOnce();
            LogNetworkDiagnosticsOnce([], [], NetworkAdapterCategory.Unknown, NetworkAdapterAggregationMode.Sum, [], null);
            return (0.0, 0.0);
        }

        try
        {
            var sensors = _hardwareManager.GetSensorValues(
                new[] { HardwareType.Network },
                SensorType.Throughput);

            if (sensors.Count == 0)
            {
                LogNetworkSensorsMissingOnce();
                LogNetworkDiagnosticsOnce([], [], NetworkAdapterCategory.Unknown, NetworkAdapterAggregationMode.Sum, [], null);
                return (0.0, 0.0);
            }

            var adapterSensors = new Dictionary<string, (double UploadBytesPerSecond, double DownloadBytesPerSecond)>(StringComparer.OrdinalIgnoreCase);
            foreach (var sensor in sensors)
            {
                if (string.IsNullOrWhiteSpace(sensor.HardwareName))
                {
                    continue;
                }

                if (!adapterSensors.TryGetValue(sensor.HardwareName, out var existing))
                {
                    existing = (0.0, 0.0);
                }

                if (sensor.Value <= 0)
                {
                    continue;
                }

                if (ContainsAny(sensor.Name, UploadSensorNamePatterns))
                {
                    existing.UploadBytesPerSecond += sensor.Value;
                }
                else if (ContainsAny(sensor.Name, DownloadSensorNamePatterns))
                {
                    existing.DownloadBytesPerSecond += sensor.Value;
                }

                adapterSensors[sensor.HardwareName] = existing;
            }

            if (adapterSensors.Count == 0)
            {
                LogNetworkSensorsMissingOnce();
                LogNetworkDiagnosticsOnce(sensors, [], NetworkAdapterCategory.Unknown, NetworkAdapterAggregationMode.Sum, [], null);
                return (0.0, 0.0);
            }

            var adapters = new List<NetworkAdapterThroughput>(adapterSensors.Count);
            foreach (var entry in adapterSensors)
            {
                var category = GetNetworkAdapterCategory(entry.Key);
                adapters.Add(new NetworkAdapterThroughput(entry.Key, entry.Value.UploadBytesPerSecond, entry.Value.DownloadBytesPerSecond, category));
            }

            var physicalAdapters = adapters.Where(a => a.Category == NetworkAdapterCategory.Physical).ToList();
            var virtualAdapters = adapters.Where(a => a.Category == NetworkAdapterCategory.Virtual).ToList();
            var unknownAdapters = adapters.Where(a => a.Category == NetworkAdapterCategory.Unknown).ToList();

            IReadOnlyList<NetworkAdapterThroughput> selectedAdapters;
            NetworkAdapterCategory selectedCategory;
            if (physicalAdapters.Count > 0)
            {
                selectedAdapters = physicalAdapters;
                selectedCategory = NetworkAdapterCategory.Physical;
            }
            else if (virtualAdapters.Count > 0)
            {
                selectedAdapters = virtualAdapters;
                selectedCategory = NetworkAdapterCategory.Virtual;
            }
            else
            {
                selectedAdapters = unknownAdapters;
                selectedCategory = NetworkAdapterCategory.Unknown;
            }

            var aggregationMode = selectedCategory == NetworkAdapterCategory.Virtual
                ? NetworkAdapterAggregationMode.Max
                : NetworkAdapterAggregationMode.Sum;

            var (uploadBytesPerSecond, downloadBytesPerSecond, primaryAdapter) = AggregateNetworkThroughput(selectedAdapters, aggregationMode);

            LogNetworkDiagnosticsOnce(sensors, adapters, selectedCategory, aggregationMode, selectedAdapters, primaryAdapter);

            return (
                ConvertThroughputToMbps(uploadBytesPerSecond),
                ConvertThroughputToMbps(downloadBytesPerSecond));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SystemMetricProvider] Failed to read network throughput via LibreHardwareMonitor");
            return (0.0, 0.0);
        }
    }

    private NetworkAdapterCategory GetNetworkAdapterCategory(string hardwareName)
    {
        if (IsVirtualAdapter(hardwareName))
        {
            return NetworkAdapterCategory.Virtual;
        }

        if (IsPhysicalAdapterVerified(hardwareName))
        {
            return NetworkAdapterCategory.Physical;
        }

        return NetworkAdapterCategory.Unknown;
    }

    private static (double UploadBytesPerSecond, double DownloadBytesPerSecond, NetworkAdapterThroughput? PrimaryAdapter) AggregateNetworkThroughput(
        IReadOnlyList<NetworkAdapterThroughput> adapters,
        NetworkAdapterAggregationMode mode)
    {
        if (adapters == null || adapters.Count == 0)
        {
            return (0.0, 0.0, null);
        }

        if (mode == NetworkAdapterAggregationMode.Sum)
        {
            double totalUpload = 0.0;
            double totalDownload = 0.0;
            foreach (var adapter in adapters)
            {
                totalUpload += adapter.UploadBytesPerSecond;
                totalDownload += adapter.DownloadBytesPerSecond;
            }

            return (totalUpload, totalDownload, null);
        }

        // Max: pick the single adapter with the highest total throughput to avoid double counting on virtual networks.
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

        if (best == null)
        {
            return (0.0, 0.0, null);
        }

        return (best.UploadBytesPerSecond, best.DownloadBytesPerSecond, best);
    }

    private void LogNetworkUnavailableOnce()
    {
        if (_logger == null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _networkUnavailableLogged, 1) == 1)
        {
            return;
        }

        _logger.LogWarning(
            "[SystemMetricProvider] LibreHardwareMonitor is not available. Network throughput will be reported as 0. " +
            "If this persists, try running the service as Administrator.");
    }

    private void LogNetworkSensorsMissingOnce()
    {
        if (_logger == null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _networkSensorsMissingLogged, 1) == 1)
        {
            return;
        }

        _logger.LogWarning(
            "[SystemMetricProvider] No network throughput sensors detected via LibreHardwareMonitor (HardwareType.Network/SensorType.Throughput). " +
            "Network throughput will be reported as 0.");
    }

    private void LogNetworkDiagnosticsOnce(
        IReadOnlyList<SensorReading> sensors,
        IReadOnlyList<NetworkAdapterThroughput> allAdapters,
        NetworkAdapterCategory selectedCategory,
        NetworkAdapterAggregationMode aggregationMode,
        IReadOnlyList<NetworkAdapterThroughput> selectedAdapters,
        NetworkAdapterThroughput? primaryAdapter)
    {
        if (_logger == null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _networkDiagnosticsLogged, 1) == 1)
        {
            return;
        }

        try
        {
            var osAdapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => !string.IsNullOrWhiteSpace(adapter.Name))
                .Select(adapter =>
                {
                    var name = adapter.Name.Trim();
                    var desc = string.IsNullOrWhiteSpace(adapter.Description) ? string.Empty : adapter.Description.Trim();
                    return $"Name='{name}', Type={adapter.NetworkInterfaceType}, Status={adapter.OperationalStatus}, Desc='{desc}'";
                })
                .ToList();

            if (osAdapters.Count > 0)
            {
                _logger.LogInformation("[SystemMetricProvider] Network interfaces (OS): {Adapters}", string.Join(" | ", osAdapters));
            }
            else
            {
                _logger.LogInformation("[SystemMetricProvider] Network interfaces (OS): none");
            }

            if (sensors.Count > 0)
            {
                var sensorSummary = sensors
                    .Where(s => !string.IsNullOrWhiteSpace(s.HardwareName))
                    .GroupBy(s => s.HardwareName, StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                    {
                        var names = group.Select(s => s.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase);
                        return $"{group.Key}: [{string.Join(", ", names)}]";
                    })
                    .ToList();

                _logger.LogInformation("[SystemMetricProvider] Network throughput sensors (LHM): {Sensors}",
                    string.Join(" | ", sensorSummary));
            }
            else
            {
                _logger.LogInformation("[SystemMetricProvider] Network throughput sensors (LHM): none");
            }

            if (allAdapters.Count > 0)
            {
                var categorized = allAdapters.Select(a =>
                {
                    var up = ConvertThroughputToMbps(a.UploadBytesPerSecond);
                    var down = ConvertThroughputToMbps(a.DownloadBytesPerSecond);
                    return $"{a.Category}:{a.HardwareName} (Up={up:0.###}MB/s, Down={down:0.###}MB/s)";
                }).ToList();

                _logger.LogInformation("[SystemMetricProvider] Network adapters categorized: {Adapters}", string.Join(" | ", categorized));
            }

            if (selectedAdapters.Count > 0)
            {
                var selectedNames = selectedAdapters.Select(a => a.HardwareName).ToList();
                _logger.LogInformation(
                    "[SystemMetricProvider] Network throughput selection: Category={Category}, Aggregation={Aggregation}, Selected={Adapters}, Primary={Primary}",
                    selectedCategory,
                    aggregationMode,
                    string.Join(", ", selectedNames),
                    primaryAdapter?.HardwareName ?? "(none)");
            }
            else
            {
                _logger.LogInformation(
                    "[SystemMetricProvider] Network throughput selection: Category={Category}, Aggregation={Aggregation}, Selected=(none)",
                    selectedCategory,
                    aggregationMode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SystemMetricProvider] Failed to log network diagnostics");
        }
    }

    private IReadOnlyList<DiskUsage> GetDiskUsages()
    {
        try
        {
            EnsureHardwareManagerInitialized();
            if (_hardwareManager == null || !_hardwareManager.IsAvailable)
            {
                return [];
            }

            var throughputSensors = _hardwareManager.GetSensorValues(
                new[] { HardwareType.Storage },
                SensorType.Throughput) ?? Array.Empty<SensorReading>();
            var dataSensors = _hardwareManager.GetSensorValues(
                new[] { HardwareType.Storage },
                SensorType.Data) ?? Array.Empty<SensorReading>();
            var smallDataSensors = _hardwareManager.GetSensorValues(
                new[] { HardwareType.Storage },
                SensorType.SmallData) ?? Array.Empty<SensorReading>();

            var diskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddHardwareNames(diskNames, throughputSensors);
            AddHardwareNames(diskNames, dataSensors);
            AddHardwareNames(diskNames, smallDataSensors);

            if (diskNames.Count == 0)
            {
                if (Interlocked.Exchange(ref _storageSensorsMissingLogged, 1) == 0)
                {
                    _logger?.LogWarning(
                        "[SystemMetricProvider] No storage sensors detected via LibreHardwareMonitor (HardwareType.Storage). " +
                        "throughput={ThroughputCount}, data={DataCount}, smallData={SmallDataCount}. " +
                        "If LibreHardwareMonitor GUI can see disks but this process cannot, try running the service as Administrator.",
                        throughputSensors.Count,
                        dataSensors.Count,
                        smallDataSensors.Count);
                }
                return [];
            }

            var results = new List<DiskUsage>(diskNames.Count);
            foreach (var diskName in diskNames)
            {
                var (readSpeed, writeSpeed) = GetDiskThroughput(throughputSensors, diskName);
                var (totalBytes, usedBytes) = GetDiskCapacityFromLhm(dataSensors, smallDataSensors, diskName);

                if (totalBytes == null && usedBytes == null && readSpeed == null && writeSpeed == null)
                {
                    continue;
                }

                results.Add(new DiskUsage
                {
                    Name = diskName,
                    TotalBytes = totalBytes,
                    UsedBytes = usedBytes,
                    ReadSpeed = readSpeed,
                    WriteSpeed = writeSpeed
                });
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SystemMetricProvider] Failed to read disk metrics");
            return [];
        }
    }

    private static void AddHardwareNames(HashSet<string> diskNames, IReadOnlyList<SensorReading> sensors)
    {
        if (sensors == null || sensors.Count == 0)
        {
            return;
        }

        foreach (var sensor in sensors)
        {
            if (string.IsNullOrWhiteSpace(sensor.HardwareName))
            {
                continue;
            }

            diskNames.Add(sensor.HardwareName);
        }
    }

    private static (double? ReadSpeed, double? WriteSpeed) GetDiskThroughput(IReadOnlyList<SensorReading> sensors, string diskName)
    {
        if (sensors == null || sensors.Count == 0 || string.IsNullOrWhiteSpace(diskName))
        {
            return (null, null);
        }

        double readBytesPerSecond = 0.0;
        double writeBytesPerSecond = 0.0;
        var readFound = false;
        var writeFound = false;

        foreach (var sensor in sensors)
        {
            if (!string.Equals(sensor.HardwareName, diskName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (sensor.Value < 0)
            {
                continue;
            }

            if (ContainsAny(sensor.Name, DiskReadSensorNamePatterns) &&
                !ContainsAny(sensor.Name, DiskWriteSensorNamePatterns))
            {
                readFound = true;
                readBytesPerSecond += sensor.Value;
            }
            else if (ContainsAny(sensor.Name, DiskWriteSensorNamePatterns))
            {
                writeFound = true;
                writeBytesPerSecond += sensor.Value;
            }
        }

        var readSpeed = readFound ? ConvertThroughputToMbps(readBytesPerSecond) : (double?)null;
        var writeSpeed = writeFound ? ConvertThroughputToMbps(writeBytesPerSecond) : (double?)null;
        return (readSpeed, writeSpeed);
    }

    private static float? MaxNullable(float? left, float? right)
    {
        if (!left.HasValue)
        {
            return right;
        }

        if (!right.HasValue)
        {
            return left;
        }

        return left.Value >= right.Value ? left : right;
    }

    private static (long? TotalBytes, long? UsedBytes) GetDiskCapacityFromLhm(
        IReadOnlyList<SensorReading> dataSensors,
        IReadOnlyList<SensorReading> smallDataSensors,
        string diskName)
    {
        if (string.IsNullOrWhiteSpace(diskName))
        {
            return (null, null);
        }

        var totalSpaceGb = MaxNullable(
            FindDiskSensorValue(dataSensors, diskName, DiskTotalSpaceSensorNamePatterns, requirePositive: true),
            FindDiskSensorValue(smallDataSensors, diskName, DiskTotalSpaceSensorNamePatterns, requirePositive: true));

        var freeSpaceGb = MaxNullable(
            FindDiskSensorValue(dataSensors, diskName, DiskFreeSpaceSensorNamePatterns, requirePositive: false),
            FindDiskSensorValue(smallDataSensors, diskName, DiskFreeSpaceSensorNamePatterns, requirePositive: false));

        long? totalBytes = null;
        if (totalSpaceGb.HasValue && totalSpaceGb.Value > 0)
        {
            totalBytes = ConvertGbToBytes(totalSpaceGb.Value);
        }

        long? usedBytes = null;
        if (totalBytes.HasValue && freeSpaceGb.HasValue && freeSpaceGb.Value >= 0)
        {
            var freeBytes = ConvertGbToBytes(freeSpaceGb.Value);
            usedBytes = totalBytes.Value - freeBytes;
        }

        if (totalBytes.HasValue && usedBytes.HasValue && usedBytes.Value > totalBytes.Value)
        {
            usedBytes = totalBytes;
        }
        else if (usedBytes.HasValue && usedBytes.Value < 0)
        {
            usedBytes = 0;
        }

        return (totalBytes, usedBytes);
    }

    private static float? FindDiskSensorValue(
        IReadOnlyList<SensorReading> sensors,
        string diskName,
        string[] namePatterns,
        bool requirePositive)
    {
        if (sensors == null || sensors.Count == 0)
        {
            return null;
        }

        float? best = null;
        foreach (var sensor in sensors)
        {
            if (!string.Equals(sensor.HardwareName, diskName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ContainsAny(sensor.Name, namePatterns))
            {
                continue;
            }

            if (requirePositive && sensor.Value <= 0)
            {
                continue;
            }

            if (best == null || sensor.Value > best.Value)
            {
                best = sensor.Value;
            }
        }

        return best;
    }

    private static long ConvertGbToBytes(float gb)
    {
        // LibreHardwareMonitor 对存储容量的展示通常为 GB（接近 GiB 语义）。这里按 1024^3 换算为 bytes。
        return (long)Math.Round(gb * 1024.0 * 1024.0 * 1024.0);
    }

    private static bool ContainsAny(string name, string[] patterns)
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

    private static double ConvertThroughputToMbps(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return 0.0;
        }

        // LibreHardwareMonitor Throughput 原始单位为 Bytes/s（B/s）。
        // 系统侧对外保持 MB/s 语义（与 IMetricsClient 契约一致），不在此处做过早 Round，避免小网速丢失精度。
        return bytesPerSecond / (1024.0 * 1024.0);
    }

    #region Windows API

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint DwLength;
        public uint DwMemoryLoad;
        public ulong UllTotalPhys;
        public ulong UllAvailPhys;
        public ulong UllTotalPageFile;
        public ulong UllAvailPageFile;
        public ulong UllTotalVirtual;
        public ulong UllAvailVirtual;
        public ulong UllAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static bool TryGetPhysicalMemoryDetails(out double totalMb, out double availMb)
    {
        totalMb = 0;
        availMb = 0;

        var status = new MemoryStatusEx { DwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return false;
        }

        totalMb = status.UllTotalPhys / 1024.0 / 1024.0;
        availMb = status.UllAvailPhys / 1024.0 / 1024.0;
        return totalMb > 0 && availMb >= 0;
    }

    #endregion

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        DisposeAsync().GetAwaiter().GetResult();
    }
}

/// <summary>
/// 硬件限制数据模型
/// </summary>
public class HardwareLimits
{
    public double MaxMemory { get; set; }
    public double MaxVram { get; set; }
}

/// <summary>
/// 系统使用率数据模型
/// </summary>
public class SystemUsage
{
    public double TotalCpu { get; set; }
    public double TotalGpu { get; set; }
    public double TotalMemory { get; set; }
    public double TotalVram { get; set; }
    public double UploadSpeed { get; set; }
    public double DownloadSpeed { get; set; }
    public IReadOnlyList<DiskUsage> Disks { get; set; } = [];
    public bool PowerAvailable { get; set; }
    public double TotalPower { get; set; }
    public double MaxPower { get; set; }
    public int? PowerSchemeIndex { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// 物理硬盘使用情况（用于前端磁盘卡片）
/// </summary>
public class DiskUsage
{
    public string Name { get; set; } = string.Empty;
    public long? TotalBytes { get; set; }
    public long? UsedBytes { get; set; }
    public double? ReadSpeed { get; set; }
    public double? WriteSpeed { get; set; }
}
