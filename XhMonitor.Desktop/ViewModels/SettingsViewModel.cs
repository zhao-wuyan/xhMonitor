using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
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

    // 系统设置
    private bool _startWithWindows = ConfigurationDefaults.System.StartWithWindows;

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

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
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
            if (settings.TryGetValue("Appearance", out var appearance))
            {
                ThemeColor = JsonSerializer.Deserialize<string>(appearance["ThemeColor"]) ?? ConfigurationDefaults.Appearance.ThemeColor;
                Opacity = int.Parse(appearance["Opacity"]);
            }

            // 数据采集设置
            if (settings.TryGetValue("DataCollection", out var dataCollection))
            {
                var keywords = JsonSerializer.Deserialize<string[]>(dataCollection["ProcessKeywords"]) ?? Array.Empty<string>();
                ProcessKeywords = string.Join("\n", keywords);
                TopProcessCount = int.Parse(dataCollection["TopProcessCount"]);
                DataRetentionDays = int.Parse(dataCollection["DataRetentionDays"]);
            }

            // 监控设置
            if (settings.TryGetValue("Monitoring", out var monitoring))
            {
                MonitorCpu = bool.Parse(monitoring["MonitorCpu"]);
                MonitorMemory = bool.Parse(monitoring["MonitorMemory"]);
                MonitorGpu = bool.Parse(monitoring["MonitorGpu"]);
                MonitorVram = bool.Parse(monitoring["MonitorVram"]);
                MonitorPower = bool.Parse(monitoring["MonitorPower"]);
                MonitorNetwork = bool.Parse(monitoring["MonitorNetwork"]);
                AdminMode = bool.Parse(monitoring["AdminMode"]);
            }

            // 系统设置
            if (settings.TryGetValue("System", out var system))
            {
                StartWithWindows = bool.Parse(system["StartWithWindows"]);
            }

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
            var settings = new Dictionary<string, Dictionary<string, string>>
            {
                ["Appearance"] = new()
                {
                    ["ThemeColor"] = JsonSerializer.Serialize(ThemeColor),
                    ["Opacity"] = Opacity.ToString()
                },
                ["DataCollection"] = new()
                {
                    ["ProcessKeywords"] = JsonSerializer.Serialize(
                        ProcessKeywords.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                    ["TopProcessCount"] = TopProcessCount.ToString(),
                    ["DataRetentionDays"] = DataRetentionDays.ToString()
                },
                ["Monitoring"] = new()
                {
                    ["MonitorCpu"] = MonitorCpu.ToString().ToLower(),
                    ["MonitorMemory"] = MonitorMemory.ToString().ToLower(),
                    ["MonitorGpu"] = MonitorGpu.ToString().ToLower(),
                    ["MonitorVram"] = MonitorVram.ToString().ToLower(),
                    ["MonitorPower"] = MonitorPower.ToString().ToLower(),
                    ["MonitorNetwork"] = MonitorNetwork.ToString().ToLower(),
                    ["AdminMode"] = AdminMode.ToString().ToLower()
                },
                ["System"] = new()
                {
                    ["StartWithWindows"] = StartWithWindows.ToString().ToLower()
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

    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
