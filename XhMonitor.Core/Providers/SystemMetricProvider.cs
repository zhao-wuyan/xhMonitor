using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
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
    private readonly HashSet<string> _verifiedPhysicalAdapters = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _verificationLock = new(1, 1);
    private bool _disposed;

    public SystemMetricProvider(
        IEnumerable<IMetricProvider> providers,
        ILogger<SystemMetricProvider>? logger = null,
        ILibreHardwareManager? hardwareManager = null,
        IPowerProvider? powerProvider = null)
    {
        _logger = logger;
        _providers = BuildProviderMap(providers, logger);
        _hardwareManager = hardwareManager;
        _powerProvider = powerProvider;
        StartPhysicalAdapterVerification();
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

        return new SystemUsage
        {
            TotalCpu = totalCpu,
            TotalGpu = totalGpu,
            TotalMemory = totalMemory,
            TotalVram = totalVram,
            UploadSpeed = uploadSpeed,
            DownloadSpeed = downloadSpeed,
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
            List<string> adapterNames = [];
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                    adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(adapter.Name))
                {
                    continue;
                }

                adapterNames.Add(adapter.Name);
            }

            List<string> verifiedAdapters = [];
            await _verificationLock.WaitAsync();
            try
            {
                _verifiedPhysicalAdapters.Clear();
                foreach (var name in adapterNames)
                {
                    _verifiedPhysicalAdapters.Add(name);
                }

                verifiedAdapters.AddRange(_verifiedPhysicalAdapters);
            }
            finally
            {
                _verificationLock.Release();
            }

            if (verifiedAdapters.Count > 0)
            {
                _logger?.LogInformation("[SystemMetricProvider] Verified physical adapters: {Adapters}",
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
            return _verifiedPhysicalAdapters.Contains(hardwareName);
        }
        finally
        {
            _verificationLock.Release();
        }
    }

    private (double UploadSpeed, double DownloadSpeed) GetNetworkSpeed()
    {
        if (_hardwareManager == null || !_hardwareManager.IsAvailable)
        {
            return (0.0, 0.0);
        }

        try
        {
            var sensors = _hardwareManager.GetSensorValues(
                new[] { HardwareType.Network },
                SensorType.Throughput);

            if (sensors.Count == 0)
            {
                return (0.0, 0.0);
            }

            double uploadBytesPerSecond = 0.0;
            double downloadBytesPerSecond = 0.0;
            var hasVerifiedAdapters = HasVerifiedPhysicalAdapters();

            foreach (var sensor in sensors)
            {
                if (IsVirtualAdapter(sensor.HardwareName))
                {
                    _logger?.LogDebug("[SystemMetricProvider] Skip virtual adapter: {HardwareName}, Sensor={SensorName}",
                        sensor.HardwareName, sensor.Name);
                    continue;
                }

                if (hasVerifiedAdapters && !IsPhysicalAdapterVerified(sensor.HardwareName))
                {
                    _logger?.LogDebug("[SystemMetricProvider] Skip unverified adapter: {HardwareName}, Sensor={SensorName}",
                        sensor.HardwareName, sensor.Name);
                    continue;
                }

                if (sensor.Value <= 0)
                {
                    continue;
                }

                if (ContainsAny(sensor.Name, UploadSensorNamePatterns))
                {
                    uploadBytesPerSecond += sensor.Value;
                }
                else if (ContainsAny(sensor.Name, DownloadSensorNamePatterns))
                {
                    downloadBytesPerSecond += sensor.Value;
                }
            }

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
    public bool PowerAvailable { get; set; }
    public double TotalPower { get; set; }
    public double MaxPower { get; set; }
    public int? PowerSchemeIndex { get; set; }
    public DateTime Timestamp { get; set; }
}
