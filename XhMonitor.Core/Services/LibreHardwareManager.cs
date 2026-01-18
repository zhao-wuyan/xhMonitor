using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Services;

/// <summary>
/// LibreHardwareMonitor Computer 实例管理器（单例模式）
/// LibreHardwareMonitor Computer instance manager (singleton pattern)
/// </summary>
public class LibreHardwareManager : ILibreHardwareManager
{
    private readonly ILogger<LibreHardwareManager>? _logger;
    private readonly Lazy<Computer?> _computer;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly TimeSpan _snapshotLifetime;
    private IReadOnlyList<SensorReading> _snapshot = Array.Empty<SensorReading>();
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
                var computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    IsMotherboardEnabled = false,
                    IsControllerEnabled = false,
                    IsNetworkEnabled = false,
                    IsStorageEnabled = false
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

        _updateLock.Wait();
        try
        {
            now = DateTime.UtcNow;
            if (now - _snapshotAt < _snapshotLifetime)
            {
                return;
            }

            foreach (var hardware in _computer.Value.Hardware)
            {
                hardware.Update();
            }

            var readings = new List<SensorReading>();
            foreach (var hardware in _computer.Value.Hardware)
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
        var results = new List<float>();

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
        var results = new List<SensorReading>();

        if (!IsAvailable || _computer.Value == null || hardwareTypes == null || hardwareTypes.Count == 0)
        {
            return results;
        }

        try
        {
            var typeSet = new HashSet<HardwareType>(hardwareTypes);
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
                results.Add(new SensorReading(hardware.HardwareType, sensor.SensorType, sensor.Name, sensor.Value.Value));
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

    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            if (_computer.IsValueCreated && _computer.Value != null)
            {
                _computer.Value.Close();
                _logger?.LogInformation("[LibreHardwareManager] Computer instance closed");
            }

            _updateLock.Dispose();
            _snapshot = Array.Empty<SensorReading>();
            _snapshotAt = DateTime.MinValue;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[LibreHardwareManager] Error during Dispose()");
        }
        finally
        {
            _disposed = true;
        }
    }
}
