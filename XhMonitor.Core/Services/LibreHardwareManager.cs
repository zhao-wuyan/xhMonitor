using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Services;

/// <summary>
/// LibreHardwareMonitor Computer 实例管理器（单例模式）
/// LibreHardwareMonitor Computer instance manager (singleton pattern)
/// </summary>
public class LibreHardwareManager : ILibreHardwareManager, IAsyncDisposable
{
    private readonly ILogger<LibreHardwareManager>? _logger;
    private readonly Lazy<Computer?> _computer;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly TimeSpan _snapshotLifetime;
    private IReadOnlyList<SensorReading> _snapshot = [];
    private DateTime _snapshotAt = DateTime.MinValue;
    private bool _disposed;

    public bool IsAvailable { get; private set; }

    public LibreHardwareManager(ILogger<LibreHardwareManager>? logger = null, TimeSpan? snapshotLifetime = null)
    {
        _logger = logger;
        _snapshotLifetime = snapshotLifetime ?? TimeSpan.FromSeconds(1);
        _computer = new Lazy<Computer?>(() =>
        {
            try
            {
                // 配置硬件监控类型 - Configure hardware monitoring types
                var computer = new Computer
                {
                    IsCpuEnabled = true,              // 启用CPU监控 - Enable CPU monitoring
                    IsGpuEnabled = true,              // 启用GPU监控 - Enable GPU monitoring
                    IsMemoryEnabled = true,           // 启用内存监控 - Enable memory monitoring
                    IsMotherboardEnabled = false,     // 禁用主板监控 - Disable motherboard monitoring
                    IsControllerEnabled = false,      // 禁用控制器监控 - Disable controller monitoring
                    IsNetworkEnabled = true,         // 禁用网络监控 - Disable network monitoring
                    IsStorageEnabled = false          // 禁用存储监控 - Disable storage monitoring
                };

                computer.Open();
                _logger?.LogInformation("[LibreHardwareManager] Computer instance initialized successfully");
                return computer;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[LibreHardwareManager] Failed to initialize Computer instance");
                return null;
            }
        });
    }

    public bool Initialize()
    {
        try
        {
            var computer = _computer.Value;
            IsAvailable = computer != null;

            if (!IsAvailable)
            {
                _logger?.LogWarning("[LibreHardwareManager] Computer initialization failed, IsAvailable=false");
            }

            return IsAvailable;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LibreHardwareManager] Exception during Initialize()");
            IsAvailable = false;
            return false;
        }
    }

    private void EnsureSnapshotFresh()
    {
        var now = DateTime.UtcNow;
        if (now - _snapshotAt < _snapshotLifetime)
        {
            return;
        }

        var acquired = _updateLock.WaitAsync(0).GetAwaiter().GetResult();
        if (!acquired)
        {
            return;
        }
        try
        {
            now = DateTime.UtcNow;
            if (now - _snapshotAt < _snapshotLifetime)
            {
                return;
            }

            var computer = _computer.Value;
            if (computer == null)
            {
                return;
            }

            foreach (var hardware in computer.Hardware)
            {
                hardware.Update();
            }

            List<SensorReading> readings = [];
            foreach (var hardware in computer.Hardware)
            {
                AddSensorReadings(hardware, readings);
                foreach (var subHardware in hardware.SubHardware)
                {
                    AddSensorReadings(subHardware, readings);
                }
            }

            _snapshot = readings;
            _snapshotAt = now;
        }
        finally
        {
            _updateLock.Release();
        }
    }

    public float? GetSensorValue(HardwareType hardwareType, SensorType sensorType)
    {
        if (!IsAvailable || _computer.Value == null)
        {
            return null;
        }

        try
        {
            EnsureSnapshotFresh();
            foreach (var sensor in _snapshot)
            {
                if (sensor.HardwareType == hardwareType && sensor.SensorType == sensorType)
                {
                    return sensor.Value;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LibreHardwareManager] Error getting sensor value for {HardwareType}/{SensorType}",
                hardwareType, sensorType);
            return null;
        }
    }

    public List<float> GetAllSensorValues(HardwareType hardwareType, SensorType sensorType)
    {
        List<float> results = [];

        if (!IsAvailable || _computer.Value == null)
        {
            return results;
        }

        try
        {
            EnsureSnapshotFresh();
            foreach (var sensor in _snapshot)
            {
                if (sensor.HardwareType == hardwareType && sensor.SensorType == sensorType)
                {
                    results.Add(sensor.Value);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LibreHardwareManager] Error getting all sensor values for {HardwareType}/{SensorType}",
                hardwareType, sensorType);
        }

        return results;
    }

    public IReadOnlyList<SensorReading> GetSensorValues(IReadOnlyCollection<HardwareType> hardwareTypes, SensorType sensorType)
    {
        List<SensorReading> results = [];

        if (!IsAvailable || _computer.Value == null || hardwareTypes == null || hardwareTypes.Count == 0)
        {
            return results;
        }

        try
        {
            HashSet<HardwareType> typeSet = [.. hardwareTypes];
            EnsureSnapshotFresh();
            foreach (var sensor in _snapshot)
            {
                if (sensor.SensorType == sensorType && typeSet.Contains(sensor.HardwareType))
                {
                    results.Add(sensor);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LibreHardwareManager] Error getting sensor values for {SensorType}", sensorType);
        }

        return results;
    }

    private static void AddSensorReadings(IHardware hardware, List<SensorReading> results)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value.HasValue)
            {
                results.Add(new SensorReading(hardware.HardwareType, hardware.Name, sensor.SensorType, sensor.Name, sensor.Value.Value));
            }
        }
    }

    public float? GetSensorValueByName(HardwareType hardwareType, SensorType sensorType, string sensorNamePattern)
    {
        if (!IsAvailable || _computer.Value == null)
        {
            return null;
        }

        try
        {
            EnsureSnapshotFresh();
            foreach (var sensor in _snapshot)
            {
                if (sensor.HardwareType == hardwareType &&
                    sensor.SensorType == sensorType &&
                    sensor.Name.Contains(sensorNamePattern, StringComparison.OrdinalIgnoreCase))
                {
                    return sensor.Value;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LibreHardwareManager] Error getting sensor value by name for {HardwareType}/{SensorType}/{Pattern}",
                hardwareType, sensorType, sensorNamePattern);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_computer.IsValueCreated && _computer.Value != null)
            {
                _computer.Value.Close();
                _logger?.LogInformation("[LibreHardwareManager] Computer instance closed");
            }

            if (_updateLock is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                _updateLock.Dispose();
            }

            _snapshot = [];
            _snapshotAt = DateTime.MinValue;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LibreHardwareManager] Error during DisposeAsync()");
        }
        finally
        {
            _disposed = true;
        }
    }

    public void Dispose()
    {
        DisposeAsync().GetAwaiter().GetResult();
    }
}
