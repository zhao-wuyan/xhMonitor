using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using XhMonitor.Core.Common;
using XhMonitor.Core.Configuration;
using XhMonitor.Desktop.Services;

namespace XhMonitor.Desktop.ViewModels;

public class SettingsViewModel : INotifyPropertyChanged
{
    private readonly HttpClient _httpClient;
    private readonly string _apiBaseUrl;
    // 端口属于基础设施配置：由服务端 appsettings.json (Server:Port) 管理，不在数据库/设置界面中暴露。

    // 外观设置
    private string _themeColor = ConfigurationDefaults.Appearance.ThemeColor;
    private int _opacity = ConfigurationDefaults.Appearance.Opacity;

    // 数据采集设置
    private string _processKeywords = string.Join("\n", ConfigurationDefaults.DataCollection.ProcessKeywords);
    private int _topProcessCount = ConfigurationDefaults.DataCollection.TopProcessCount;
    private int _dataRetentionDays = ConfigurationDefaults.DataCollection.DataRetentionDays;

    // 监控设置
    private bool _monitorCpu = ConfigurationDefaults.Monitoring.MonitorCpu;
    private bool _monitorMemory = ConfigurationDefaults.Monitoring.MonitorMemory;
    private bool _monitorGpu = ConfigurationDefaults.Monitoring.MonitorGpu;
    private bool _monitorVram = ConfigurationDefaults.Monitoring.MonitorVram;
    private bool _monitorPower = ConfigurationDefaults.Monitoring.MonitorPower;
    private bool _monitorNetwork = ConfigurationDefaults.Monitoring.MonitorNetwork;
    private bool _adminMode = ConfigurationDefaults.Monitoring.AdminMode;
    private bool _originalAdminMode = ConfigurationDefaults.Monitoring.AdminMode;

    // 系统设置
    private bool _startWithWindows = ConfigurationDefaults.System.StartWithWindows;
    private bool _enableLanAccess = ConfigurationDefaults.System.EnableLanAccess;
    private bool _originalEnableLanAccess = ConfigurationDefaults.System.EnableLanAccess;
    private bool _enableAccessKey = ConfigurationDefaults.System.EnableAccessKey;
    private string _accessKey = ConfigurationDefaults.System.AccessKey;
    private string _ipWhitelist = ConfigurationDefaults.System.IpWhitelist;
    private string _localIpAddress = "正在获取...";

    // Service 状态
    private bool _serviceIsAdmin = false;
    private string _serviceAdminMessage = string.Empty;

    private bool _isSaving = false;

    /// <param name="httpClient">HttpClient instance from IHttpClientFactory</param>
    public SettingsViewModel(HttpClient httpClient, IServiceDiscovery serviceDiscovery)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiBaseUrl = $"{serviceDiscovery.ApiBaseUrl.TrimEnd('/')}/api/v1/config";
    }

    public string ThemeColor
    {
        get => _themeColor;
        set => SetProperty(ref _themeColor, value);
    }

    public int Opacity
    {
        get => _opacity;
        set => SetProperty(ref _opacity, value);
    }

    public string ProcessKeywords
    {
        get => _processKeywords;
        set => SetProperty(ref _processKeywords, value);
    }

    public int TopProcessCount
    {
        get => _topProcessCount;
        set => SetProperty(ref _topProcessCount, value);
    }

    public int DataRetentionDays
    {
        get => _dataRetentionDays;
        set => SetProperty(ref _dataRetentionDays, value);
    }

    public bool MonitorCpu
    {
        get => _monitorCpu;
        set => SetProperty(ref _monitorCpu, value);
    }

    public bool MonitorMemory
    {
        get => _monitorMemory;
        set => SetProperty(ref _monitorMemory, value);
    }

    public bool MonitorGpu
    {
        get => _monitorGpu;
        set => SetProperty(ref _monitorGpu, value);
    }

    public bool MonitorVram
    {
        get => _monitorVram;
        set => SetProperty(ref _monitorVram, value);
    }

    public bool MonitorPower
    {
        get => _monitorPower;
        set => SetProperty(ref _monitorPower, value);
    }

    public bool MonitorNetwork
    {
        get => _monitorNetwork;
        set => SetProperty(ref _monitorNetwork, value);
    }

    public bool AdminMode
    {
        get => _adminMode;
        set => SetProperty(ref _adminMode, value);
    }

    /// <summary>
    /// 获取加载时的原始 AdminMode 值，用于检测变更。
    /// </summary>
    public bool OriginalAdminMode => _originalAdminMode;

    /// <summary>
    /// 更新原始 AdminMode 值（保存成功后调用）。
    /// </summary>
    public void UpdateOriginalAdminMode()
    {
        _originalAdminMode = AdminMode;
    }

    /// <summary>
    /// 获取加载时的原始 EnableLanAccess 值，用于检测变更。
    /// </summary>
    public bool OriginalEnableLanAccess => _originalEnableLanAccess;

    /// <summary>
    /// 更新原始 EnableLanAccess 值（保存成功后调用）。
    /// </summary>
    public void UpdateOriginalEnableLanAccess()
    {
        _originalEnableLanAccess = EnableLanAccess;
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool EnableLanAccess
    {
        get => _enableLanAccess;
        set => SetProperty(ref _enableLanAccess, value);
    }

    public bool EnableAccessKey
    {
        get => _enableAccessKey;
        set => SetProperty(ref _enableAccessKey, value);
    }

    public string AccessKey
    {
        get => _accessKey;
        set => SetProperty(ref _accessKey, value ?? string.Empty);
    }

    public string IpWhitelist
    {
        get => _ipWhitelist;
        set => SetProperty(ref _ipWhitelist, value ?? string.Empty);
    }

    public string LocalIpAddress
    {
        get => _localIpAddress;
        set => SetProperty(ref _localIpAddress, value);
    }

    /// <summary>
    /// Service 当前是否以管理员权限运行
    /// </summary>
    public bool ServiceIsAdmin
    {
        get => _serviceIsAdmin;
        set => SetProperty(ref _serviceIsAdmin, value);
    }

    /// <summary>
    /// Service 管理员状态消息
    /// </summary>
    public string ServiceAdminMessage
    {
        get => _serviceAdminMessage;
        set => SetProperty(ref _serviceAdminMessage, value);
    }

    public bool IsSaving
    {
        get => _isSaving;
        set => SetProperty(ref _isSaving, value);
    }

    public string GetApiBaseUrl() => _apiBaseUrl;

    public async Task<Result<bool, string>> LoadSettingsAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_apiBaseUrl}/settings");
            response.EnsureSuccessStatusCode();

            var settings = await response.Content.ReadFromJsonAsync<Dictionary<string, Dictionary<string, string>>>();
            if (settings == null)
            {
                return Result<bool, string>.Failure("配置为空，无法加载设置。");
            }

            // 外观设置
            if (settings.TryGetValue(ConfigurationDefaults.Keys.Categories.Appearance, out var appearance))
            {
                ThemeColor = JsonSerializer.Deserialize<string>(appearance[ConfigurationDefaults.Keys.Appearance.ThemeColor]) ?? ConfigurationDefaults.Appearance.ThemeColor;
                Opacity = int.Parse(appearance[ConfigurationDefaults.Keys.Appearance.Opacity]);
            }

            // 数据采集设置
            if (settings.TryGetValue(ConfigurationDefaults.Keys.Categories.DataCollection, out var dataCollection))
            {
                var keywords = JsonSerializer.Deserialize<string[]>(dataCollection[ConfigurationDefaults.Keys.DataCollection.ProcessKeywords]) ?? Array.Empty<string>();
                ProcessKeywords = string.Join("\n", keywords);
                TopProcessCount = int.Parse(dataCollection[ConfigurationDefaults.Keys.DataCollection.TopProcessCount]);
                DataRetentionDays = int.Parse(dataCollection[ConfigurationDefaults.Keys.DataCollection.DataRetentionDays]);
            }

            // 监控设置
            if (settings.TryGetValue(ConfigurationDefaults.Keys.Categories.Monitoring, out var monitoring))
            {
                MonitorCpu = bool.Parse(monitoring[ConfigurationDefaults.Keys.Monitoring.MonitorCpu]);
                MonitorMemory = bool.Parse(monitoring[ConfigurationDefaults.Keys.Monitoring.MonitorMemory]);
                MonitorGpu = bool.Parse(monitoring[ConfigurationDefaults.Keys.Monitoring.MonitorGpu]);
                MonitorVram = bool.Parse(monitoring[ConfigurationDefaults.Keys.Monitoring.MonitorVram]);
                MonitorPower = bool.Parse(monitoring[ConfigurationDefaults.Keys.Monitoring.MonitorPower]);
                MonitorNetwork = bool.Parse(monitoring[ConfigurationDefaults.Keys.Monitoring.MonitorNetwork]);
                AdminMode = bool.Parse(monitoring[ConfigurationDefaults.Keys.Monitoring.AdminMode]);
                _originalAdminMode = AdminMode; // 保存原始值用于变更检测
            }

            // 系统设置
            if (settings.TryGetValue(ConfigurationDefaults.Keys.Categories.System, out var system))
            {
                StartWithWindows = bool.Parse(system[ConfigurationDefaults.Keys.System.StartWithWindows]);
                if (system.TryGetValue(ConfigurationDefaults.Keys.System.EnableLanAccess, out var enableLanAccessValue))
                {
                    EnableLanAccess = bool.Parse(enableLanAccessValue);
                    _originalEnableLanAccess = EnableLanAccess; // 保存原始值用于变更检测
                }
                if (system.TryGetValue(ConfigurationDefaults.Keys.System.EnableAccessKey, out var enableAccessKeyValue))
                {
                    EnableAccessKey = bool.Parse(enableAccessKeyValue);
                }
                if (system.TryGetValue(ConfigurationDefaults.Keys.System.AccessKey, out var accessKeyValue))
                {
                    AccessKey = accessKeyValue;
                }
                if (system.TryGetValue(ConfigurationDefaults.Keys.System.IpWhitelist, out var ipWhitelistValue))
                {
                    IpWhitelist = ipWhitelistValue;
                }
            }

            // 加载本机IP地址
            LoadLocalIpAddress();

            // 加载 Service 管理员状态
            await LoadAdminStatusAsync();

            return Result<bool, string>.Success(true);
        }
        catch (Exception ex)
        {
            return Result<bool, string>.Failure(ex.Message);
        }
    }

    public async Task<Result<bool, string>> SaveSettingsAsync()
    {
        IsSaving = true;
        try
        {
            if (EnableAccessKey)
            {
                var trimmedAccessKey = (AccessKey ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(trimmedAccessKey))
                {
                    trimmedAccessKey = GenerateAccessKey();
                }

                if (!string.Equals(AccessKey, trimmedAccessKey, StringComparison.Ordinal))
                {
                    AccessKey = trimmedAccessKey;
                }
            }

            var settings = new Dictionary<string, Dictionary<string, string>>
            {
                [ConfigurationDefaults.Keys.Categories.Appearance] = new()
                {
                    [ConfigurationDefaults.Keys.Appearance.ThemeColor] = JsonSerializer.Serialize(ThemeColor),
                    [ConfigurationDefaults.Keys.Appearance.Opacity] = Opacity.ToString()
                },
                [ConfigurationDefaults.Keys.Categories.DataCollection] = new()
                {
                    [ConfigurationDefaults.Keys.DataCollection.ProcessKeywords] = JsonSerializer.Serialize(
                        ProcessKeywords.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                    [ConfigurationDefaults.Keys.DataCollection.TopProcessCount] = TopProcessCount.ToString(),
                    [ConfigurationDefaults.Keys.DataCollection.DataRetentionDays] = DataRetentionDays.ToString()
                },
                [ConfigurationDefaults.Keys.Categories.Monitoring] = new()
                {
                    [ConfigurationDefaults.Keys.Monitoring.MonitorCpu] = MonitorCpu.ToString().ToLower(),
                    [ConfigurationDefaults.Keys.Monitoring.MonitorMemory] = MonitorMemory.ToString().ToLower(),
                    [ConfigurationDefaults.Keys.Monitoring.MonitorGpu] = MonitorGpu.ToString().ToLower(),
                    [ConfigurationDefaults.Keys.Monitoring.MonitorVram] = MonitorVram.ToString().ToLower(),
                    [ConfigurationDefaults.Keys.Monitoring.MonitorPower] = MonitorPower.ToString().ToLower(),
                    [ConfigurationDefaults.Keys.Monitoring.MonitorNetwork] = MonitorNetwork.ToString().ToLower(),
                    [ConfigurationDefaults.Keys.Monitoring.AdminMode] = AdminMode.ToString().ToLower()
                },
                [ConfigurationDefaults.Keys.Categories.System] = new()
                {
                    [ConfigurationDefaults.Keys.System.StartWithWindows] = StartWithWindows.ToString().ToLower(),
                    [ConfigurationDefaults.Keys.System.EnableLanAccess] = EnableLanAccess.ToString().ToLower(),
                    [ConfigurationDefaults.Keys.System.EnableAccessKey] = EnableAccessKey.ToString().ToLower(),
                    [ConfigurationDefaults.Keys.System.AccessKey] = AccessKey ?? string.Empty,
                    [ConfigurationDefaults.Keys.System.IpWhitelist] = IpWhitelist ?? string.Empty
                }
            };

            var response = await _httpClient.PutAsJsonAsync($"{_apiBaseUrl}/settings", settings);
            response.EnsureSuccessStatusCode();

            return Result<bool, string>.Success(true);
        }
        catch (Exception ex)
        {
            return Result<bool, string>.Failure(ex.Message);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private static string GenerateAccessKey()
    {
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }

        var base64Url = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        return base64Url.Length > 32 ? base64Url[..32] : base64Url;
    }

    /// <summary>
    /// 加载 Service 管理员权限状态
    /// </summary>
    public async Task LoadAdminStatusAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_apiBaseUrl}/admin-status");
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<AdminStatusResponse>();
                if (result != null)
                {
                    ServiceIsAdmin = result.IsAdmin;
                    ServiceAdminMessage = result.Message ?? string.Empty;
                }
            }
        }
        catch
        {
            ServiceIsAdmin = false;
            ServiceAdminMessage = "无法连接到 Service";
        }
    }

    /// <summary>
    /// 加载本机IP地址
    /// </summary>
    private void LoadLocalIpAddress()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(GetUnicastAddressesSafe)
                .Select(ua => ua.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                .Where(ip => !IPAddress.IsLoopback(ip))
                .Where(ip => !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal)) // APIPA
                .Distinct()
                .ToList();

            var best = candidates
                .OrderByDescending(IsPrivateIpv4)
                .ThenBy(ip => ip.ToString(), StringComparer.Ordinal)
                .FirstOrDefault();

            LocalIpAddress = best?.ToString() ?? "未检测到";
        }
        catch
        {
            LocalIpAddress = "获取失败";
        }
    }

    private static IEnumerable<UnicastIPAddressInformation> GetUnicastAddressesSafe(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().UnicastAddresses.Cast<UnicastIPAddressInformation>();
        }
        catch
        {
            return Enumerable.Empty<UnicastIPAddressInformation>();
        }
    }

    private static bool IsPrivateIpv4(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        // 10.0.0.0/8
        if (bytes[0] == 10)
        {
            return true;
        }

        // 172.16.0.0/12
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        {
            return true;
        }

        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return true;
        }

        return false;
    }

    private class AdminStatusResponse
    {
        public bool IsAdmin { get; set; }
        public string? Message { get; set; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
