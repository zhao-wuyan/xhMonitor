using System.Diagnostics;
using System.Management;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

public class VramMetricProvider : IMetricProvider
{
    private readonly ILogger<VramMetricProvider>? _logger;
    private readonly object _snapshotLock = new();
    private readonly Func<bool>? _isSupportedOverride;
    private readonly Func<DateTime> _utcNowProvider;
    private readonly Func<IReadOnlyDictionary<int, long>> _processUsageSnapshotFactory;
    private double _cachedMaxVram;
    private VramProcessSnapshot? _snapshot;
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromMilliseconds(750);

    public VramMetricProvider(ILogger<VramMetricProvider>? logger = null)
        : this(logger, isSupportedOverride: null, utcNowProvider: null, processUsageSnapshotFactory: null)
    {
    }

    internal VramMetricProvider(
        ILogger<VramMetricProvider>? logger,
        Func<bool>? isSupportedOverride,
        Func<DateTime>? utcNowProvider,
        Func<IReadOnlyDictionary<int, long>>? processUsageSnapshotFactory)
    {
        _logger = logger;
        _isSupportedOverride = isSupportedOverride;
        _utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);
        _processUsageSnapshotFactory = processUsageSnapshotFactory ?? CaptureUsageByProcess;
    }

    public string MetricId => "vram";
    public string DisplayName => "VRAM Usage";
    public string Unit => "MB";
    public MetricType Type => MetricType.Size;

    public bool IsSupported()
    {
        if (_isSupportedOverride != null)
        {
            return _isSupportedOverride();
        }

        return OperatingSystem.IsWindows() && PerformanceCounterCategory.Exists("GPU Process Memory");
    }

    /// <summary>
    /// 获取 VRAM 最大容量（用于 MaxVram）
    /// </summary>
    public async Task<double> GetSystemTotalAsync()
    {
        if (!IsSupported())
        {
            _logger?.LogWarning("VramMetricProvider: IsSupported() returned false");
            return 0;
        }

        return await Task.Run(() =>
        {
            if (_cachedMaxVram > 0)
            {
                _logger?.LogDebug("VramMetricProvider: Returning cached value {Capacity} MB", _cachedMaxVram);
                return _cachedMaxVram;
            }

            double capacity = 0;

            if (OperatingSystem.IsWindows())
            {
                // 1. Try PowerShell first (most reliable for QWORD values)
                capacity = TryGetVramCapacityFromPowerShell();
                _logger?.LogInformation("VramMetricProvider: PowerShell returned {Capacity} MB", capacity);

                // 2. Fallback to C# Registry API
                if (capacity <= 0)
                {
                    capacity = TryGetVramCapacityFromRegistry();
                    _logger?.LogInformation("VramMetricProvider: Registry API returned {Capacity} MB", capacity);
                }

                // 3. Try DxDiag (slow but reliable)
                if (capacity <= 0)
                {
                    capacity = TryGetVramCapacityFromDxDiag();
                    _logger?.LogInformation("VramMetricProvider: DxDiag returned {Capacity} MB", capacity);
                }
            }

            // 4. Fallback to WMI (has 4GB limit)
            if (capacity <= 0)
            {
                capacity = GetVramCapacityFromWmi();
                _logger?.LogInformation("VramMetricProvider: WMI returned {Capacity} MB", capacity);
            }

            if (capacity > 0)
            {
                _cachedMaxVram = capacity;
                _logger?.LogInformation("VramMetricProvider: Final capacity cached as {Capacity} MB", _cachedMaxVram);
            }

            return capacity;
        });
    }

    /// <summary>
    /// 获取完整的 VRAM 指标（PerformanceCounter 提供者不支持，返回 null）
    /// </summary>
    public Task<VramMetrics?> GetVramMetricsAsync()
    {
        return Task.FromResult<VramMetrics?>(null);
    }

    private double TryGetVramCapacityFromRegistry()
    {
        // Try multiple ControlSet paths
        string[] controlSetPaths = new[]
        {
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}",
            @"SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}",
            @"SYSTEM\ControlSet002\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"
        };

        foreach (var gpuClassKey in controlSetPaths)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var classKey = baseKey.OpenSubKey(gpuClassKey);
                if (classKey == null)
                {
                    _logger?.LogDebug("Registry path not found: {Path}", gpuClassKey);
                    continue;
                }

                double totalBytes = 0;
                var subKeyNames = classKey.GetSubKeyNames();
                _logger?.LogDebug("Found {Count} subkeys in {Path}", subKeyNames.Length, gpuClassKey);

                foreach (var subKeyName in subKeyNames)
                {
                    using var subKey = classKey.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    var value = subKey.GetValue("HardwareInformation.qwMemorySize");
                    if (value is null)
                    {
                        _logger?.LogDebug("Subkey {SubKey}: No qwMemorySize value found", subKeyName);
                        continue;
                    }

                    _logger?.LogDebug("Subkey {SubKey}: qwMemorySize type={Type}, value={Value}",
                        subKeyName, value.GetType().Name, value);

                    try
                    {
                        long raw = 0;

                        // Handle different registry value types
                        if (value is long l)
                        {
                            raw = l;
                            _logger?.LogDebug("Subkey {SubKey}: Parsed as long: {Raw}", subKeyName, raw);
                        }
                        else if (value is int i)
                        {
                            raw = i;
                            _logger?.LogDebug("Subkey {SubKey}: Parsed as int: {Raw}", subKeyName, raw);
                        }
                        else if (value is byte[] bytes && bytes.Length >= sizeof(long))
                        {
                            raw = BitConverter.ToInt64(bytes, 0);
                            _logger?.LogDebug("Subkey {SubKey}: Parsed byte[{Len}] as long: {Raw}", subKeyName, bytes.Length, raw);
                        }
                        else if (value is Array arr && arr.Length >= sizeof(long))
                        {
                            // Handle generic array type (convert to byte array first)
                            var byteArray = new byte[sizeof(long)];
                            Buffer.BlockCopy(arr, 0, byteArray, 0, sizeof(long));
                            raw = BitConverter.ToInt64(byteArray, 0);
                            _logger?.LogDebug("Subkey {SubKey}: Parsed Array[{Len}] as long: {Raw}", subKeyName, arr.Length, raw);
                        }
                        else
                        {
                            _logger?.LogDebug("Subkey {SubKey}: Unsupported type {Type}", subKeyName, value.GetType().Name);
                        }

                        if (raw > 0)
                        {
                            totalBytes += raw;
                            _logger?.LogDebug("Subkey {SubKey}: Added {MB} MB, running total {Total} bytes",
                                subKeyName, raw / 1024.0 / 1024.0, totalBytes);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Subkey {SubKey}: Error parsing value", subKeyName);
                    }
                }

                if (totalBytes > 0)
                {
                    var result = Math.Round(totalBytes / 1024.0 / 1024.0, 1);
                    _logger?.LogInformation("Registry total VRAM: {Result} MB from path {Path}", result, gpuClassKey);
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error reading registry path: {Path}", gpuClassKey);
            }
        }

        _logger?.LogWarning("Registry reading failed: No valid VRAM capacity found in any path");
        return 0;
    }

    private double TryGetVramCapacityFromPowerShell()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"Get-ChildItem 'HKLM:\\SYSTEM\\ControlSet001\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}' -ErrorAction SilentlyContinue | ForEach-Object { $v = (Get-ItemProperty $_.PSPath -Name 'HardwareInformation.qwMemorySize' -ErrorAction SilentlyContinue).'HardwareInformation.qwMemorySize'; if ($v) { if ($v -is [array]) { $v = [System.BitConverter]::ToInt64($v, 0) }; if ($v -gt 0) { $v } } } | Measure-Object -Sum | Select-Object -ExpandProperty Sum\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return 0;

            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(10000);

            if (long.TryParse(output, out var totalBytes) && totalBytes > 0)
            {
                var mb = Math.Round(totalBytes / 1024.0 / 1024.0, 1);
                _logger?.LogInformation("PowerShell returned VRAM: {MB} MB", mb);
                return mb;
            }

            _logger?.LogDebug("PowerShell returned no valid VRAM value: {Output}", output);
            return 0;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "PowerShell VRAM detection failed");
            return 0;
        }
    }

    private double TryGetVramCapacityFromDxDiag()
    {
        string? tempFile = null;
        try
        {
            tempFile = Path.Combine(Path.GetTempPath(), $"dxdiag_{Guid.NewGuid():N}.xml");
            var psi = new ProcessStartInfo
            {
                FileName = "dxdiag",
                Arguments = $"/x \"{tempFile}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            using var proc = Process.Start(psi);
            if (proc == null) return 0;

            if (!proc.WaitForExit(15000))
            {
                proc.Kill();
                return 0;
            }

            if (!File.Exists(tempFile))
                return 0;

            var xml = new System.Xml.XmlDocument();
            xml.Load(tempFile);

            var nodes = xml.SelectNodes("//DisplayMemory");
            if (nodes == null || nodes.Count == 0)
                return 0;

            double totalMB = 0;
            foreach (System.Xml.XmlNode node in nodes)
            {
                var text = node.InnerText?.Trim();
                if (string.IsNullOrEmpty(text)) continue;

                var numStr = new string(text.Where(char.IsDigit).ToArray());
                if (int.TryParse(numStr, out var mb) && mb > 0)
                {
                    totalMB += mb;
                }
            }

            return totalMB > 0 ? Math.Round(totalMB, 1) : 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            try
            {
                if (tempFile != null && File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch { }
        }
    }

    private static double GetVramCapacityFromWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT AdapterRAM FROM Win32_VideoController");
            var totalCapacity = searcher.Get()
                .Cast<ManagementObject>()
                .Select(obj => Convert.ToUInt64(obj["AdapterRAM"]))
                .Where(adapterRAM => adapterRAM > 0)
                .Select(adapterRAM => adapterRAM / 1024.0 / 1024.0)
                .Sum();

            return Math.Round(totalCapacity, 1);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<MetricValue> CollectAsync(int processId)
    {
        if (!IsSupported()) return MetricValue.Error("Not supported");

        return await Task.Run(() =>
        {
            try
            {
                var snapshot = GetOrCreateSnapshot();
                snapshot.UsageByProcessId.TryGetValue(processId, out var totalBytes);

                return new MetricValue { Value = Math.Round(totalBytes / 1024.0 / 1024.0, 1), Unit = Unit, DisplayName = DisplayName, Timestamp = DateTime.Now };
            }
            catch (Exception ex) { return MetricValue.Error(ex.Message); }
        });
    }

    public void Dispose() { }

    internal int DebugGetCachedProcessCount()
    {
        lock (_snapshotLock)
        {
            return _snapshot?.UsageByProcessId.Count ?? 0;
        }
    }

    internal void DebugInvalidateSnapshot()
    {
        lock (_snapshotLock)
        {
            _snapshot = null;
        }
    }

    private VramProcessSnapshot GetOrCreateSnapshot()
    {
        lock (_snapshotLock)
        {
            var now = _utcNowProvider();
            if (_snapshot != null && now - _snapshot.CreatedAtUtc <= SnapshotTtl)
            {
                return _snapshot;
            }

            _snapshot = new VramProcessSnapshot(now, _processUsageSnapshotFactory());
            return _snapshot;
        }
    }

    private static IReadOnlyDictionary<int, long> CaptureUsageByProcess()
    {
        var usageByProcessId = new Dictionary<int, long>();
        var category = new PerformanceCounterCategory("GPU Process Memory");
        var names = category.GetInstanceNames();

        foreach (var name in names)
        {
            if (!TryExtractProcessId(name, out var processId))
            {
                continue;
            }

            using var counter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", name, true);
            var value = counter.RawValue;

            if (value <= 0)
            {
                continue;
            }

            usageByProcessId.TryGetValue(processId, out var current);
            usageByProcessId[processId] = current + value;
        }

        return usageByProcessId;
    }

    private static bool TryExtractProcessId(string instanceName, out int processId)
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

    private sealed record VramProcessSnapshot(
        DateTime CreatedAtUtc,
        IReadOnlyDictionary<int, long> UsageByProcessId);
}
