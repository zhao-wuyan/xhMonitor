using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XhMonitor.Core.Configuration;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Services;

/// <summary>
/// 设备验证服务实现
/// </summary>
public sealed class DeviceVerifier : IDeviceVerifier
{
    private const string SupportedPowerMonitoringPlatform = "amd_395";

    private readonly HttpClient _httpClient;
    private readonly DeviceVerificationOptions _options;
    private readonly ILogger<DeviceVerifier>? _logger;
    private readonly IHardwarePlatformDetector _hardwarePlatformDetector;
    private readonly Dictionary<string, DeviceSchemeMapping> _deviceMappings;

    private DeviceInfo? _cachedDeviceInfo;
    private HardwarePlatformInfo _cachedHardwarePlatformInfo = HardwarePlatformInfo.Empty;
    private string? _verifiedDeviceName;
    private bool _powerMonitoringEnabled;
    private string? _disabledReason;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public DeviceVerifier(
        HttpClient httpClient,
        IOptions<DeviceVerificationOptions> options,
        ILogger<DeviceVerifier>? logger = null)
        : this(httpClient, options, new WmiHardwarePlatformDetector(), logger)
    {
    }

    public DeviceVerifier(
        HttpClient httpClient,
        IOptions<DeviceVerificationOptions> options,
        IHardwarePlatformDetector hardwarePlatformDetector,
        ILogger<DeviceVerifier>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _hardwarePlatformDetector = hardwarePlatformDetector ?? throw new ArgumentNullException(nameof(hardwarePlatformDetector));
        _logger = logger;

        var schemeProfiles = (_options.SchemeProfiles ?? new Dictionary<string, List<PowerSchemeConfig>>()).ToDictionary(
            p => p.Key,
            p => (p.Value ?? new List<PowerSchemeConfig>())
                .Select(s => new PowerScheme(s.StapmWatts, s.FastWatts, s.SlowWatts))
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);

        // 构建设备映射字典
        _deviceMappings = _options.Devices
            .Select(d => d.ToMapping(ResolveSchemes(d, schemeProfiles)))
            .ToDictionary(m => m.Name, m => m, StringComparer.OrdinalIgnoreCase);

        _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public async Task<DeviceInfo?> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return _cachedDeviceInfo;
    }

    public string? GetVerifiedDeviceName()
    {
        return _verifiedDeviceName;
    }

    public PowerScheme[]? GetSchemesForDevice(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            return null;
        }

        return _deviceMappings.TryGetValue(deviceName, out var mapping) ? mapping.Schemes : null;
    }

    public bool IsPowerMonitoringEnabled()
    {
        return _powerMonitoringEnabled;
    }

    public async Task<bool> IsPowerMonitoringEnabledAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return _powerMonitoringEnabled;
    }

    public bool IsPowerSwitchEnabled()
    {
        return HasVerifiedDeviceWithSchemes();
    }

    public async Task<bool> IsPowerSwitchEnabledAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return HasVerifiedDeviceWithSchemes();
    }

    public string? GetDisabledReason()
    {
        return _disabledReason;
    }

    private bool HasVerifiedDeviceWithSchemes() =>
        !string.IsNullOrEmpty(_verifiedDeviceName) &&
        GetSchemesForDevice(_verifiedDeviceName) is { Length: > 0 };

    private PowerScheme[] ResolveSchemes(
        DeviceSchemeMappingConfig device,
        IReadOnlyDictionary<string, PowerScheme[]> schemeProfiles)
    {
        if (string.IsNullOrWhiteSpace(device.SchemeKey))
        {
            _logger?.LogWarning(
                "[DeviceVerifier] Device {DeviceName} has no SchemeKey; power switching will be disabled for this device",
                device.Name);
            return [];
        }

        if (schemeProfiles.TryGetValue(device.SchemeKey, out var schemes))
        {
            return schemes;
        }

        _logger?.LogWarning(
            "[DeviceVerifier] Device {DeviceName} references missing power scheme profile {SchemeKey}; power switching will be disabled for this device",
            device.Name,
            device.SchemeKey);
        return [];
    }

    public async Task RetryVerificationAsync(CancellationToken ct = default)
    {
        // 若已验证通过，无需重新验证
        if (!string.IsNullOrEmpty(_verifiedDeviceName))
        {
            return;
        }

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 强制重新初始化
            _initialized = false;
            await InitializeAsync(ct).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await InitializeAsync(ct).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        _logger?.LogInformation("[DeviceVerifier] Initializing device verification...");

        const string defaultErrMsg = "当前设备不支持此功能";

        _verifiedDeviceName = null;
        _powerMonitoringEnabled = false;
        _disabledReason = null;
        _cachedDeviceInfo = null;
        _cachedHardwarePlatformInfo = _hardwarePlatformDetector.Detect();
        LogHardwarePlatformInfo(_cachedHardwarePlatformInfo);
        _powerMonitoringEnabled = IsHardwareSupportedForPowerMonitoring(_cachedHardwarePlatformInfo);

        foreach (var mapping in _deviceMappings.Values)
        {
            if (!mapping.HasHardwarePlatformCondition)
            {
                continue;
            }

            if (mapping.Matches(_cachedHardwarePlatformInfo))
            {
                _verifiedDeviceName = mapping.Name;
                _disabledReason = GetSwitchDisabledReason(mapping);
                _logger?.LogInformation(
                    "[DeviceVerifier] Device verified by SMBIOS hardware platform: {DeviceName}, power_monitoring_enabled={PowerMonitoringEnabled}",
                    mapping.Name,
                    _powerMonitoringEnabled);
                return;
            }
        }

        // 尝试获取设备信息
        DeviceInfo? deviceInfo = null;
        try
        {
            var response = await _httpClient.GetAsync(_options.Endpoint, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                deviceInfo = JsonSerializer.Deserialize<DeviceInfo>(json, options);
            }
            else
            {
                // _disabledReason = $"设备验证服务返回错误: {(int)response.StatusCode}";
                _disabledReason = defaultErrMsg;
                _logger?.LogWarning("[DeviceVerifier] Device info API returned {StatusCode}", response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            // _disabledReason = "无法连接设备验证服务";
            _disabledReason = defaultErrMsg;
            _logger?.LogWarning(ex, "[DeviceVerifier] Failed to connect to device info API");
        }
        catch (TaskCanceledException)
        {
            // _disabledReason = "设备验证服务连接超时";
            _disabledReason = defaultErrMsg;
            _logger?.LogWarning("[DeviceVerifier] Device info API request timed out");
        }
        catch (Exception ex)
        {
            // _disabledReason = $"设备验证失败: {ex.Message}";
            _disabledReason = defaultErrMsg;
            _logger?.LogError(ex, "[DeviceVerifier] Unexpected error during device verification");
        }

        _cachedDeviceInfo = deviceInfo;
        _powerMonitoringEnabled = _powerMonitoringEnabled ||
            IsPlatformSupportedForPowerMonitoring(deviceInfo?.Platform);

        if (deviceInfo == null)
        {
            _logger?.LogWarning("[DeviceVerifier] Device verification failed: {Reason}", _disabledReason);
            return;
        }

        // 匹配设备
        foreach (var mapping in _deviceMappings.Values)
        {
            if (mapping.HasHardwarePlatformCondition)
            {
                continue;
            }

            if (mapping.Matches(deviceInfo))
            {
                _verifiedDeviceName = mapping.Name;
                _disabledReason = GetSwitchDisabledReason(mapping);
                _logger?.LogInformation(
                    "[DeviceVerifier] Device verified: {DeviceName} (platform={Platform}, mac_authorized={MacAuthorized})",
                    mapping.Name, deviceInfo.Platform, deviceInfo.IsMacAuthorized);
                return;
            }
        }

        // 没有匹配的设备
        // _disabledReason = $"设备未授权 (platform={deviceInfo.Platform}, mac_authorized={deviceInfo.IsMacAuthorized})";
        _disabledReason = defaultErrMsg;
        _logger?.LogWarning(
            "[DeviceVerifier] No matching device found for platform={Platform}, mac_authorized={MacAuthorized}",
            deviceInfo.Platform, deviceInfo.IsMacAuthorized);
    }

    private static bool IsPlatformSupportedForPowerMonitoring(string? platform) =>
        string.Equals(platform, SupportedPowerMonitoringPlatform, StringComparison.OrdinalIgnoreCase);

    private static bool IsHardwareSupportedForPowerMonitoring(HardwarePlatformInfo hardware)
    {
        var processorName = hardware.ProcessorName ?? string.Empty;
        return processorName.Contains("AMD Ryzen AI Max", StringComparison.OrdinalIgnoreCase) &&
            processorName.Contains("395", StringComparison.OrdinalIgnoreCase);
    }

    private void LogHardwarePlatformInfo(HardwarePlatformInfo hardware)
    {
        _logger?.LogInformation(
            "[DeviceVerifier] SMBIOS platform: system_manufacturer={SystemManufacturer}, system_model={SystemModel}, product_vendor={ProductVendor}, product_name={ProductName}, baseboard_manufacturer={BaseBoardManufacturer}, baseboard_product={BaseBoardProduct}, bios_manufacturer={BiosManufacturer}, bios_version={BiosVersion}, processor_name={ProcessorName}",
            hardware.SystemManufacturer,
            hardware.SystemModel,
            hardware.SystemProductVendor,
            hardware.SystemProductName,
            hardware.BaseBoardManufacturer,
            hardware.BaseBoardProduct,
            hardware.BiosManufacturer,
            hardware.BiosVersion,
            hardware.ProcessorName);
    }

    private static string? GetSwitchDisabledReason(DeviceSchemeMapping mapping) =>
        mapping.Schemes.Length > 0 ? null : "功耗切换方案未配置";
}
