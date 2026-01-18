using System.Collections.Generic;
using LibreHardwareMonitor.Hardware;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Interfaces;

/// <summary>
/// LibreHardwareMonitor Computer 实例管理接口
/// Manages LibreHardwareMonitor Computer instance lifecycle
/// </summary>
public interface ILibreHardwareManager : IDisposable
{
    /// <summary>
    /// 初始化 Computer 实例并打开硬件监控
    /// Initialize Computer instance and open hardware monitoring
    /// </summary>
    /// <returns>初始化是否成功 / Whether initialization succeeded</returns>
    bool Initialize();

    /// <summary>
    /// 获取指定硬件类型和传感器类型的传感器值
    /// Get sensor value for specified hardware type and sensor type
    /// </summary>
    /// <param name="hardwareType">硬件类型 / Hardware type</param>
    /// <param name="sensorType">传感器类型 / Sensor type</param>
    /// <returns>传感器值，未找到时返回 null / Sensor value, null if not found</returns>
    float? GetSensorValue(HardwareType hardwareType, SensorType sensorType);

    /// <summary>
    /// 获取所有匹配的传感器值（用于多 GPU 场景）
    /// Get all matching sensor values (for multi-GPU scenarios)
    /// </summary>
    /// <param name="hardwareType">硬件类型 / Hardware type</param>
    /// <param name="sensorType">传感器类型 / Sensor type</param>
    /// <returns>所有匹配的传感器值列表 / List of all matching sensor values</returns>
    List<float> GetAllSensorValues(HardwareType hardwareType, SensorType sensorType);

    /// <summary>
    /// 获取指定硬件类型、传感器类型和传感器名称的传感器值
    /// Get sensor value for specified hardware type, sensor type and sensor name
    /// </summary>
    /// <param name="hardwareType">硬件类型 / Hardware type</param>
    /// <param name="sensorType">传感器类型 / Sensor type</param>
    /// <param name="sensorNamePattern">传感器名称模式（支持部分匹配）/ Sensor name pattern (supports partial match)</param>
    /// <returns>传感器值，未找到时返回 null / Sensor value, null if not found</returns>
    float? GetSensorValueByName(HardwareType hardwareType, SensorType sensorType, string sensorNamePattern);

    /// <summary>
    /// 批量获取指定硬件类型的传感器值（单次更新）
    /// Get sensor values for specified hardware types with a single update
    /// </summary>
    /// <param name="hardwareTypes">硬件类型集合 / Hardware types</param>
    /// <param name="sensorType">传感器类型 / Sensor type</param>
    /// <returns>传感器列表 / Sensor readings</returns>
    IReadOnlyList<SensorReading> GetSensorValues(IReadOnlyCollection<HardwareType> hardwareTypes, SensorType sensorType);

    /// <summary>
    /// LibreHardwareMonitor 是否可用（初始化成功且无错误）
    /// Whether LibreHardwareMonitor is available (initialized successfully without errors)
    /// </summary>
    bool IsAvailable { get; }
}
