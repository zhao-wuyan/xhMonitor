using System.Text.Json.Serialization;

namespace XhMonitor.Core.Models;

/// <summary>
/// 设备信息，从 device_info API 获取
/// </summary>
public sealed record DeviceInfo(
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("is_mac_authorized")] bool IsMacAuthorized,
    [property: JsonPropertyName("device_verified")] bool DeviceVerified,
    [property: JsonPropertyName("nova_id")] string? NovaId,
    [property: JsonPropertyName("is_permanent")] bool IsPermanent,
    [property: JsonPropertyName("usable_until")] string? UsableUntil,
    [property: JsonPropertyName("use_cdk")] bool UseCdk);
