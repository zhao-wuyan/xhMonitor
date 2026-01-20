using System.Collections.Concurrent;
using System.Diagnostics;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

public class CpuMetricProvider : IMetricProvider
{
    private PerformanceCounterCategory? _processCategory;
    private Dictionary<int, double>? _cachedCpuData;
    private Dictionary<int, (long RawValue, DateTime Timestamp)>? _previousSamples;
    private DateTime _cacheTimestamp = DateTime.MinValue;
    private readonly TimeSpan _cacheLifetime = TimeSpan.FromSeconds(1);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private PerformanceCounter? _systemCounter;
    private bool _isWarmedUp;
    private int _readCategoryCallCount;

    public string MetricId => "cpu";
    public string DisplayName => "CPU Usage";
    public string Unit => "%";
    public MetricType Type => MetricType.Percentage;

    public bool IsSupported() => OperatingSystem.IsWindows();

    public Task<double> GetSystemTotalAsync()
    {
        if (!IsSupported()) return Task.FromResult(0.0);

        return Task.Run(() =>
        {
            try
            {
                if (_systemCounter == null)
                {
                    _systemCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                    if (!_isWarmedUp)
                    {
                        _systemCounter.NextValue();
                        Thread.Sleep(100);
                    }
                }

                var value = _systemCounter.NextValue();
                return Math.Round(value, 1);
            }
            catch
            {
                return 0.0;
            }
        });
    }

    /// <summary>
    /// 获取完整的 VRAM 指标（不适用于此提供者）
    /// </summary>
    public Task<VramMetrics?> GetVramMetricsAsync()
    {
        return Task.FromResult<VramMetrics?>(null);
    }

    public async Task<MetricValue> CollectAsync(int processId)
    {
        if (!IsSupported()) return MetricValue.Error("Not supported");

        try
        {
            var cpuData = await GetBatchCpuDataAsync();

            if (cpuData.TryGetValue(processId, out var cpuValue))
            {
                return new MetricValue
                {
                    Value = Math.Round(cpuValue, 1),
                    Unit = Unit,
                    DisplayName = DisplayName,
                    Timestamp = DateTime.Now
                };
            }

            return MetricValue.Error("Process not found");
        }
        catch (Exception ex)
        {
            return MetricValue.Error(ex.Message);
        }
    }

    private async Task<Dictionary<int, double>> GetBatchCpuDataAsync()
    {
        if (_cachedCpuData != null && DateTime.UtcNow - _cacheTimestamp < _cacheLifetime)
        {
            return _cachedCpuData;
        }

        await _cacheLock.WaitAsync();
        try
        {
            if (_cachedCpuData != null && DateTime.UtcNow - _cacheTimestamp < _cacheLifetime)
            {
                return _cachedCpuData;
            }

            return await Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                _processCategory ??= new PerformanceCounterCategory("Process");

                var data = _processCategory.ReadCategory();
                var result = new Dictionary<int, double>();
                var newSamples = new Dictionary<int, (long RawValue, DateTime Timestamp)>();
                var now = DateTime.UtcNow;

                if (data.Contains("ID Process") && data.Contains("% Processor Time"))
                {
                    var pidCollection = data["ID Process"];
                    var cpuCollection = data["% Processor Time"];

                    foreach (string instanceName in pidCollection.Keys)
                    {
                        try
                        {
                            var pidSample = pidCollection[instanceName];
                            var cpuSample = cpuCollection[instanceName];

                            int pid = (int)pidSample.RawValue;
                            long currentRawValue = cpuSample.RawValue;

                            newSamples[pid] = (currentRawValue, now);

                            if (_previousSamples != null && _previousSamples.TryGetValue(pid, out var previous))
                            {
                                var timeDiff = (now - previous.Timestamp).TotalSeconds;
                                if (timeDiff > 0)
                                {
                                    var valueDiff = currentRawValue - previous.RawValue;
                                    double cpuPercent = (valueDiff / timeDiff) / 10000000.0 / Environment.ProcessorCount * 100;
                                    result[pid] = Math.Max(0, Math.Min(100, cpuPercent));
                                }
                            }
                        }
                        catch { }
                    }
                }

                _previousSamples = newSamples;
                _cachedCpuData = result;
                _cacheTimestamp = DateTime.UtcNow;
                _readCategoryCallCount++;

                sw.Stop();
                if (sw.ElapsedMilliseconds > 100)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CpuMetricProvider] ReadCategory took {sw.ElapsedMilliseconds}ms, processed {result.Count} processes");
                }

                return result;
            });
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task WarmupAsync()
    {
        if (!IsSupported() || _isWarmedUp) return;

        await Task.Run(() =>
        {
            try
            {
                _systemCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                _systemCounter.NextValue();

                _processCategory = new PerformanceCounterCategory("Process");
                var data = _processCategory.ReadCategory();

                var initialSamples = new Dictionary<int, (long RawValue, DateTime Timestamp)>();
                var now = DateTime.UtcNow;

                if (data.Contains("ID Process") && data.Contains("% Processor Time"))
                {
                    var pidCollection = data["ID Process"];
                    var cpuCollection = data["% Processor Time"];

                    foreach (string instanceName in pidCollection.Keys)
                    {
                        try
                        {
                            var pidSample = pidCollection[instanceName];
                            var cpuSample = cpuCollection[instanceName];

                            int pid = (int)pidSample.RawValue;
                            long rawValue = cpuSample.RawValue;
                            initialSamples[pid] = (rawValue, now);
                        }
                        catch { }
                    }

                    _previousSamples = initialSamples;
                }

                _isWarmedUp = true;
            }
            catch { }
        });
    }

    public void CleanupStaleCounters()
    {
        // 批量模式下不需要清理,缓存会自动过期
    }

    public (int CallCount, int CachedProcesses, TimeSpan CacheAge) GetStatistics()
    {
        var cacheAge = _cachedCpuData != null
            ? DateTime.UtcNow - _cacheTimestamp
            : TimeSpan.Zero;

        return (
            CallCount: _readCategoryCallCount,
            CachedProcesses: _cachedCpuData?.Count ?? 0,
            CacheAge: cacheAge
        );
    }

    public void Dispose()
    {
        _systemCounter?.Dispose();
        _cacheLock.Dispose();
    }
}
