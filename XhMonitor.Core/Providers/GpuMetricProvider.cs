using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;
using XhMonitor.Core.Monitoring;

namespace XhMonitor.Core.Providers;

public class GpuMetricProvider : IMetricProvider
{
    private readonly ConcurrentDictionary<int, List<PerformanceCounter>> _counters = new();
    private readonly ConcurrentDictionary<int, DateTime> _lastAccessTime = new();
    private readonly ILogger<GpuMetricProvider>? _logger;
    private readonly DxgiGpuMonitor _dxgiMonitor = new();
    private bool? _isSupported;
    private bool _dxgiInitialized;
    private int _cycleCount = 0;
    private const int CleanupIntervalCycles = 10; // 每 10 次调用清理一次
    private const int TtlSeconds = 60; // 60 秒未访问则清理

    public GpuMetricProvider(ILogger<GpuMetricProvider>? logger = null)
    {
        _logger = logger;

        // 初始化 DXGI 监控（暂时禁用 D3DKMT，避免崩溃）
        // TODO: 修复 D3DKMT API 调用的结构体布局问题
        /*
        try
        {
            _dxgiInitialized = _dxgiMonitor.Initialize();
            if (_dxgiInitialized)
            {
                _logger?.LogInformation("DXGI GPU monitor initialized successfully");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to initialize DXGI GPU monitor");
            _dxgiInitialized = false;
        }
        */
        _dxgiInitialized = false;
    }

    public string MetricId => "gpu";
    public string DisplayName => "GPU Usage";
    public string Unit => "%";
    public MetricType Type => MetricType.Percentage;

    public bool IsSupported()
    {
        if (_isSupported.HasValue) return _isSupported.Value;
        _isSupported = OperatingSystem.IsWindows() && PerformanceCounterCategory.Exists("GPU Engine");
        return _isSupported.Value;
    }

    public async Task<double> GetSystemTotalAsync()
    {
        // 优先使用 D3DKMT API（更准确，无需创建大量计数器）
        if (_dxgiInitialized)
        {
            return await Task.Run(() =>
            {
                try
                {
                    return _dxgiMonitor.GetGpuUsage();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to query GPU usage via D3DKMT");
                    return 0.0;
                }
            });
        }

        // Fallback: 使用 ReadCategory 批量读取（但无法获取正确的百分比值）
        // 添加超时保护，避免首次调用时卡死
        var task = Task.Run(() =>
        {
            if (!IsSupported()) return 0.0;

            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                var data = category.ReadCategory();

                if (!data.Contains("Utilization Percentage"))
                    return 0.0;

                var utilizationData = data["Utilization Percentage"];
                float maxUtilization = 0;

                foreach (string instanceName in utilizationData.Keys)
                {
                    try
                    {
                        var sample = utilizationData[instanceName];
                        // RawValue 是纳秒级时间戳，需要通过 PerformanceCounter 计算才能得到百分比
                        // 由于 ReadCategory 只返回 RawValue，我们需要使用单个计数器来获取正确的值
                        // 暂时跳过，使用进程级计数器代替
                        continue;
                    }
                    catch
                    {
                        // 跳过无效实例
                        continue;
                    }
                }

                // ReadCategory 无法获取 CookedValue，返回 0 表示不支持系统级 GPU 监控
                // 用户应该查看进程级 GPU 使用率
                return 0.0;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to query system GPU usage");
                return 0.0;
            }
        });

        // 5秒超时保护
        if (await Task.WhenAny(task, Task.Delay(5000)) == task)
        {
            return await task;
        }
        else
        {
            _logger?.LogWarning("GPU usage query timed out after 5 seconds, returning 0");
            return 0.0;
        }
    }

    public async Task<MetricValue> CollectAsync(int processId)
    {
        if (!IsSupported()) return MetricValue.Error("Not supported");

        // 更新访问时间
        _lastAccessTime[processId] = DateTime.UtcNow;

        // 定期清理过期条目
        if (++_cycleCount >= CleanupIntervalCycles)
        {
            _cycleCount = 0;
            CleanupExpiredEntries();
        }

        return await Task.Run(() =>
        {
            try
            {
                if (!_counters.TryGetValue(processId, out var counters))
                {
                    counters = InitCounters(processId);
                    if (counters.Count > 0) _counters.TryAdd(processId, counters);
                }

                // If still empty, try re-init (process might have started using GPU)
                if ((counters == null || counters.Count == 0) && _counters.ContainsKey(processId) == false)
                {
                     counters = InitCounters(processId);
                     if (counters.Count > 0) _counters.TryAdd(processId, counters);
                }

                float total = 0;
                if (counters != null)
                {
                    foreach (var c in counters)
                    {
                        try { total += c.NextValue(); } catch { }
                    }
                }

                return new MetricValue
                {
                    Value = Math.Round(total, 1),
                    Unit = Unit,
                    DisplayName = DisplayName,
                    Timestamp = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                return MetricValue.Error(ex.Message);
            }
        });
    }

    private static List<PerformanceCounter> InitCounters(int pid)
    {
        var list = new List<PerformanceCounter>();
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var instanceNames = category.GetInstanceNames();
            var prefix = $"pid_{pid}_";
            foreach (var name in instanceNames.Where(n => n.Contains(prefix)))
            {
                var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                try { c.NextValue(); list.Add(c); } catch { c.Dispose(); }
            }
        }
        catch { }
        return list;
    }

    public Task WarmupAsync()
    {
        // 禁用系统级预热（避免迭代所有 GPU Engine 实例）
        // 进程级计数器按需创建
        _logger?.LogDebug("WarmupAsync disabled - process-level counters created on demand");
        return Task.CompletedTask;
    }

    private void CleanupExpiredEntries()
    {
        var now = DateTime.UtcNow;
        var expiredPids = _lastAccessTime
            .Where(kvp => (now - kvp.Value).TotalSeconds > TtlSeconds)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var pid in expiredPids)
        {
            if (_counters.TryRemove(pid, out var counters))
            {
                foreach (var counter in counters)
                {
                    try { counter.Dispose(); } catch { }
                }
            }
            _lastAccessTime.TryRemove(pid, out _);
        }

        if (expiredPids.Count > 0)
        {
            _logger?.LogDebug("Cleaned up {Count} expired GPU counter entries", expiredPids.Count);
        }
    }

    public void Dispose()
    {
        foreach(var list in _counters.Values)
            foreach(var c in list)
                c.Dispose();
        _counters.Clear();
        _lastAccessTime.Clear();
        _dxgiMonitor?.Dispose();
    }
}
