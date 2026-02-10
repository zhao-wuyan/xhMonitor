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
    private bool _enableFloatingMode = ConfigurationDefaults.Monitoring.EnableFloatingMode;
    private bool _enableEdgeDockMode = ConfigurationDefaults.Monitoring.EnableEdgeDockMode;
    private string _dockCpuLabel = ConfigurationDefaults.Monitoring.DockCpuLabel;
    private string _dockMemoryLabel = ConfigurationDefaults.Monitoring.DockMemoryLabel;
    private string _dockGpuLabel = ConfigurationDefaults.Monitoring.DockGpuLabel;
    private string _dockPowerLabel = ConfigurationDefaults.Monitoring.DockPowerLabel;
    private string _dockUploadLabel = ConfigurationDefaults.Monitoring.DockUploadLabel;
    private string _dockDownloadLabel = ConfigurationDefaults.Monitoring.DockDownloadLabel;
    private int _dockColumnGap = ConfigurationDefaults.Monitoring.DockColumnGap;
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
    private readonly int _webPort;

    // Service 状态
    private bool _serviceIsAdmin = false;
    private string _serviceAdminMessage = string.Empty;

    private bool _isSaving = false;

    /// <param name="httpClient">HttpClient instance from IHttpClientFactory</param>
    public SettingsViewModel(HttpClient httpClient, IServiceDiscovery serviceDiscovery)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _webPort = serviceDiscovery.WebPort;
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

    public bool EnableFloatingMode
    {
        get => _enableFloatingMode;
        set => SetProperty(ref _enableFloatingMode, value);
    }

    public bool EnableEdgeDockMode
    {
        get => _enableEdgeDockMode;
        set => SetProperty(ref _enableEdgeDockMode, value);
    }

    public string DockCpuLabel
    {
        get => _dockCpuLabel;
        set => SetProperty(ref _dockCpuLabel, value ?? string.Empty);
    }

    public string DockMemoryLabel
    {
        get => _dockMemoryLabel;
        set => SetProperty(ref _dockMemoryLabel, value ?? string.Empty);
    }

    public string DockGpuLabel
    {
        get => _dockGpuLabel;
        set => SetProperty(ref _dockGpuLabel, value ?? string.Empty);
    }

    public string DockPowerLabel
    {
        get => _dockPowerLabel;
        set => SetProperty(ref _dockPowerLabel, value ?? string.Empty);
    }

    public string DockUploadLabel
    {
        get => _dockUploadLabel;
        set => SetProperty(ref _dockUploadLabel, value ?? string.Empty);
    }

    public string DockDownloadLabel
    {
        get => _dockDownloadLabel;
        set => SetProperty(ref _dockDownloadLabel, value ?? string.Empty);
    }

    public int DockColumnGap
    {
        get => _dockColumnGap;
        set => SetProperty(ref _dockColumnGap, Math.Clamp(value, 0, 24));
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
        set
        {
            if (!SetProperty(ref _localIpAddress, value))
            {
                return;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalIpEndpoint)));
        }
    }

    public int WebPort => _webPort;

    public string LocalIpEndpoint
    {
        get
        {
            var parts = (LocalIpAddress ?? string.Empty)
                .Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var endpoints = parts
                .Where(part => IPAddress.TryParse(part, out _))
                .Select(part => $"{part}:{WebPort}")
                .ToList();

            if (endpoints.Count > 0)
            {
                return string.Join(", ", endpoints);
            }

            return $"{LocalIpAddress} (端口 {WebPort})";
        }
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
                MonitorCpu = GetBool(monitoring, ConfigurationDefaults.Keys.Monitoring.MonitorCpu, ConfigurationDefaults.Monitoring.MonitorCpu);
                MonitorMemory = GetBool(monitoring, ConfigurationDefaults.Keys.Monitoring.MonitorMemory, ConfigurationDefaults.Monitoring.MonitorMemory);
                MonitorGpu = GetBool(monitoring, ConfigurationDefaults.Keys.Monitoring.MonitorGpu, ConfigurationDefaults.Monitoring.MonitorGpu);
                MonitorVram = GetBool(monitoring, ConfigurationDefaults.Keys.Monitoring.MonitorVram, ConfigurationDefaults.Monitoring.MonitorVram);
                MonitorPower = GetBool(monitoring, ConfigurationDefaults.Keys.Monitoring.MonitorPower, ConfigurationDefaults.Monitoring.MonitorPower);
                MonitorNetwork = GetBool(monitoring, ConfigurationDefaults.Keys.Monitoring.MonitorNetwork, ConfigurationDefaults.Monitoring.MonitorNetwork);
                AdminMode = GetBool(monitoring, ConfigurationDefaults.Keys.Monitoring.AdminMode, ConfigurationDefaults.Monitoring.AdminMode);
                EnableFloatingMode = GetBool(monitoring, ConfigurationDefaults.Keys.Monitoring.EnableFloatingMode, ConfigurationDefaults.Monitoring.EnableFloatingMode);
                EnableEdgeDockMode = GetBool(monitoring, ConfigurationDefaults.Keys.Monitoring.EnableEdgeDockMode, ConfigurationDefaults.Monitoring.EnableEdgeDockMode);
                DockCpuLabel = GetString(monitoring, ConfigurationDefaults.Keys.Monitoring.DockCpuLabel, ConfigurationDefaults.Monitoring.DockCpuLabel);
                DockMemoryLabel = GetString(monitoring, ConfigurationDefaults.Keys.Monitoring.DockMemoryLabel, ConfigurationDefaults.Monitoring.DockMemoryLabel);
                DockGpuLabel = GetString(monitoring, ConfigurationDefaults.Keys.Monitoring.DockGpuLabel, ConfigurationDefaults.Monitoring.DockGpuLabel);
                DockPowerLabel = GetString(monitoring, ConfigurationDefaults.Keys.Monitoring.DockPowerLabel, ConfigurationDefaults.Monitoring.DockPowerLabel);
                DockUploadLabel = GetString(monitoring, ConfigurationDefaults.Keys.Monitoring.DockUploadLabel, ConfigurationDefaults.Monitoring.DockUploadLabel);
                DockDownloadLabel = GetString(monitoring, ConfigurationDefaults.Keys.Monitoring.DockDownloadLabel, ConfigurationDefaults.Monitoring.DockDownloadLabel);
                DockColumnGap = GetInt(monitoring, ConfigurationDefaults.Keys.Monitoring.DockColumnGap, ConfigurationDefaults.Monitoring.DockColumnGap);
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
                    [ConfigurationDefaults.Keys.Monitoring.AdminMode] = AdminMode.ToString().ToLower(),
                    [ConfigurationDefaults.Keys.Monitoring.EnableFloatingMode] = EnableFloatingMode.ToString().ToLower(),
                    [ConfigurationDefaults.Keys.Monitoring.EnableEdgeDockMode] = EnableEdgeDockMode.ToString().ToLower(),
                    [ConfigurationDefaults.Keys.Monitoring.DockCpuLabel] = (DockCpuLabel ?? string.Empty).Trim(),
                    [ConfigurationDefaults.Keys.Monitoring.DockMemoryLabel] = (DockMemoryLabel ?? string.Empty).Trim(),
                    [ConfigurationDefaults.Keys.Monitoring.DockGpuLabel] = (DockGpuLabel ?? string.Empty).Trim(),
                    [ConfigurationDefaults.Keys.Monitoring.DockPowerLabel] = (DockPowerLabel ?? string.Empty).Trim(),
                    [ConfigurationDefaults.Keys.Monitoring.DockUploadLabel] = (DockUploadLabel ?? string.Empty).Trim(),
                    [ConfigurationDefaults.Keys.Monitoring.DockDownloadLabel] = (DockDownloadLabel ?? string.Empty).Trim(),
                    [ConfigurationDefaults.Keys.Monitoring.DockColumnGap] = DockColumnGap.ToString()
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
            var nicCandidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(nic => new
                {
                    Nic = nic,
                    IsVirtual = IsVirtualAdapter(nic),
                    HasGateway = HasIpv4Gateway(nic),
                })
                .ToList();

            // 过滤策略（用户视角“局域网可访问 IP”优先）：
            // 1) 优先选择：非虚拟网卡 + 有默认网关（一般就是当前真实局域网出口）
            // 2) 回退：非虚拟网卡（某些场景可能没有网关，例如直连/特殊网络）
            // 3) 兜底：不过滤（避免完全显示“未检测到”）
            var selectedNics = nicCandidates
                .Where(x => !x.IsVirtual && x.HasGateway)
                .Select(x => x.Nic)
                .ToList();

            if (selectedNics.Count == 0)
            {
                selectedNics = nicCandidates
                    .Where(x => !x.IsVirtual)
                    .Select(x => x.Nic)
                    .ToList();
            }

            if (selectedNics.Count == 0)
            {
                selectedNics = nicCandidates.Select(x => x.Nic).ToList();
            }

            // 用户明确希望保留某些“内网穿透/VPN”类型网卡（即使没有默认网关）。
            selectedNics.AddRange(nicCandidates.Where(x => IsAllowedTunnelAdapter(x.Nic)).Select(x => x.Nic));
            selectedNics = selectedNics.GroupBy(nic => nic.Id).Select(g => g.First()).ToList();

            var candidates = selectedNics
                .SelectMany(GetUnicastAddressesSafe)
                .Select(ua => ua.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                .Where(ip => !IPAddress.IsLoopback(ip))
                .Where(ip => !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal)) // APIPA
                .Distinct()
                .ToList();

            var sorted = candidates
                .OrderByDescending(IsPrivateIpv4)
                .ThenBy(ip => ip.ToString(), StringComparer.Ordinal)
                .Select(ip => ip.ToString())
                .ToList();

            LocalIpAddress = sorted.Count > 0 ? string.Join(", ", sorted) : "未检测到";
        }
        catch
        {
            LocalIpAddress = "获取失败";
        }
    }

    private static bool HasIpv4Gateway(NetworkInterface nic)
    {
        try
        {
            var gateways = nic.GetIPProperties().GatewayAddresses;
            return gateways
                .Select(g => g?.Address)
                .Where(addr => addr != null && addr.AddressFamily == AddressFamily.InterNetwork)
                .Any(addr => !IPAddress.Any.Equals(addr));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAllowedTunnelAdapter(NetworkInterface nic)
    {
        // 白名单：允许特定“内网穿透 / Overlay 网络 / VPN”网卡出现在可访问地址里。
        // 目的：这些网卡可能没有默认网关，但仍可用于访问 Web（例如同一 Tailscale/ZeroTier 网络内的设备）。
        var name = (nic.Name ?? string.Empty).ToLowerInvariant();
        var desc = (nic.Description ?? string.Empty).ToLowerInvariant();
        var hay = $"{name} {desc}";

        return name.StartsWith("et_", StringComparison.Ordinal) ||
               desc.StartsWith("et_", StringComparison.Ordinal) ||
               hay.Contains("easytier", StringComparison.Ordinal) ||
               hay.Contains("tailscale", StringComparison.Ordinal) ||
               hay.Contains("zerotier", StringComparison.Ordinal) ||
               hay.Contains("wireguard", StringComparison.Ordinal) ||
               hay.Contains("wintun", StringComparison.Ordinal) ||
               hay.Contains("openvpn", StringComparison.Ordinal) ||
               hay.Contains("hamachi", StringComparison.Ordinal);
    }

    private static bool IsVirtualAdapter(NetworkInterface nic)
    {
        if (IsAllowedTunnelAdapter(nic))
        {
            return false;
        }

        var name = (nic.Name ?? string.Empty).ToLowerInvariant();
        var desc = (nic.Description ?? string.Empty).ToLowerInvariant();

        // 常见虚拟/容器/VPN 网卡关键字（尽量覆盖中文/英文）
        // 目标：把 Docker/WSL/Hyper-V/VMware/VirtualBox/TAP 等从“局域网可访问 IP”候选中排除。
        var hay = $"{name} {desc}";
        return hay.Contains("virtual", StringComparison.Ordinal) ||
               hay.Contains("虚拟", StringComparison.Ordinal) ||
               hay.Contains("vmware", StringComparison.Ordinal) ||
               hay.Contains("virtualbox", StringComparison.Ordinal) ||
               hay.Contains("hyper-v", StringComparison.Ordinal) ||
               hay.Contains("vethernet", StringComparison.Ordinal) ||
               hay.Contains("vEthernet".ToLowerInvariant(), StringComparison.Ordinal) ||
               hay.Contains("docker", StringComparison.Ordinal) ||                                                                  
               hay.Contains("wsl", StringComparison.Ordinal) ||
               hay.Contains("teredo", StringComparison.Ordinal) ||
               hay.Contains("isatap", StringComparison.Ordinal) ||
               hay.Contains("6to4", StringComparison.Ordinal) ||
               hay.Contains("npcap", StringComparison.Ordinal) ||
               hay.Contains("loopback", StringComparison.Ordinal);
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

    private static bool GetBool(Dictionary<string, string> settings, string key, bool fallback)
    {
        return settings.TryGetValue(key, out var raw) && bool.TryParse(raw, out var parsed)
            ? parsed
            : fallback;
    }

    private static int GetInt(Dictionary<string, string> settings, string key, int fallback)
    {
        return settings.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed)
            ? parsed
            : fallback;
    }

    private static string GetString(Dictionary<string, string> settings, string key, string fallback)
    {
        if (!settings.TryGetValue(key, out var raw))
        {
            return fallback;
        }

        var normalized = (raw ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
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
