using XhMonitor.Core.Models;

namespace XhMonitor.Core.Configuration;

/// <summary>
/// 设备验证配置选项
/// </summary>
public sealed class DeviceVerificationOptions
{
    /// <summary>
    /// 设备信息 API 端点
    /// </summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:5050/device_info";

    /// <summary>
    /// HTTP 请求超时（秒）
    /// </summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// 设备-方案映射配置
    /// </summary>
    public List<DeviceSchemeMappingConfig> Devices { get; set; } = new();

    /// <summary>
    /// 可复用功耗方案配置
    /// </summary>
    public Dictionary<string, List<PowerSchemeConfig>> SchemeProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 设备-方案映射配置（用于 JSON 绑定）
/// </summary>
public sealed class DeviceSchemeMappingConfig
{
    /// <summary>
    /// 设备名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 引用 Power:DeviceVerification:SchemeProfiles 中的功耗方案 key
    /// </summary>
    public string SchemeKey { get; set; } = string.Empty;

    /// <summary>
    /// 平台标识（如 amd_395）
    /// </summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>
    /// 是否要求 MAC 授权
    /// </summary>
    public bool RequireMacAuthorized { get; set; }

    /// <summary>
    /// 系统/产品/主板厂商匹配片段（来自 SMBIOS）
    /// </summary>
    public string[] HardwareManufacturerContains { get; set; } = [];

    /// <summary>
    /// 系统/产品/主板型号匹配片段（来自 SMBIOS）
    /// </summary>
    public string[] HardwareModelContains { get; set; } = [];

    /// <summary>
    /// 转换为 DeviceSchemeMapping
    /// </summary>
    public DeviceSchemeMapping ToMapping(PowerScheme[] schemes)
    {
        var condition = new DeviceVerificationCondition(
            Platform,
            RequireMacAuthorized,
            HardwareManufacturerContains ?? [],
            HardwareModelContains ?? []);
        return new DeviceSchemeMapping(Name, SchemeKey, condition, schemes);
    }
}

/// <summary>
/// 功耗方案配置（用于 JSON 绑定）
/// </summary>
public sealed class PowerSchemeConfig
{
    public int StapmWatts { get; set; }
    public int FastWatts { get; set; }
    public int SlowWatts { get; set; }
}
