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
    private readonly HttpClient _httpClient;
    private readonly DeviceVerificationOptions _options;
    private readonly ILogger<DeviceVerifier>? _logger;
    private readonly Dictionary<string, DeviceSchemeMapping> _deviceMappings;

    private DeviceInfo? _cachedDeviceInfo;
    private string? _verifiedDeviceName;
    private string? _disabledReason;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public DeviceVerifier(
        HttpClient httpClient,
        IOptions<DeviceVerificationOptions> options,
        ILogger<DeviceVerifier>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;

        // 构建设备映射字典
        _deviceMappings = _options.Devices
            .Select(d => d.ToMapping())
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

    public bool IsPowerSwitchEnabled()
    {
        return !string.IsNullOrEmpty(_verifiedDeviceName);
    }

    public async Task<bool> IsPowerSwitchEnabledAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return !string.IsNullOrEmpty(_verifiedDeviceName);
    }

    public string? GetDisabledReason()
    {
        return _disabledReason;
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

        if (deviceInfo == null)
        {
            _logger?.LogWarning("[DeviceVerifier] Device verification failed: {Reason}", _disabledReason);
            return;
        }

        // 匹配设备
        foreach (var mapping in _deviceMappings.Values)
        {
            if (mapping.Matches(deviceInfo))
            {
                _verifiedDeviceName = mapping.Name;
                _disabledReason = null;
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
}
