using LibreHardwareMonitor.Hardware;

namespace XhMonitor.Core.Models;

public sealed record SensorReading(HardwareType HardwareType, SensorType SensorType, string Name, float Value);
