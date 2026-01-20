using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using XhMonitor.Core.Configuration;

namespace XhMonitor.Desktop.ViewModels;

public class SettingsViewModel : INotifyPropertyChanged
{
    private readonly HttpClient _httpClient;
    private static readonly string ApiBaseUrl = $"http://localhost:{ConfigurationDefaults.System.SignalRPort}/api/v1/config";
    // 端口属于基础设施配置：由服务端 appsettings.json (Server:Port) 管理，不在数据库/设置界面中暴露。

    // 外观设置
    private string _themeColor = ConfigurationDefaults.Appearance.ThemeColor;
    private int _opacity = ConfigurationDefaults.Appearance.Opacity;

    // 数据采集设置
    private ObservableCollection<string> _processKeywords = new();
    private int _topProcessCount = ConfigurationDefaults.DataCollection.TopProcessCount;
    private int _dataRetentionDays = ConfigurationDefaults.DataCollection.DataRetentionDays;

    // 系统设置
    private bool _startWithWindows = ConfigurationDefaults.System.StartWithWindows;

    private bool _isSaving = false;

    public SettingsViewModel()
    {
        _httpClient = new HttpClient();
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

    public ObservableCollection<string> ProcessKeywords
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

    public async Task LoadSettingsAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{ApiBaseUrl}/settings");
            response.EnsureSuccessStatusCode();

            var settings = await response.Content.ReadFromJsonAsync<Dictionary<string, Dictionary<string, string>>>();
            if (settings == null) return;

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
                ProcessKeywords = new ObservableCollection<string>(keywords);
                TopProcessCount = int.Parse(dataCollection["TopProcessCount"]);
                DataRetentionDays = int.Parse(dataCollection["DataRetentionDays"]);
            }

            // 系统设置
            if (settings.TryGetValue("System", out var system))
            {
                StartWithWindows = bool.Parse(system["StartWithWindows"]);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载配置失败: {ex.Message}");
        }
    }

    public async Task<bool> SaveSettingsAsync()
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
                    ["ProcessKeywords"] = JsonSerializer.Serialize(ProcessKeywords.ToArray()),
                    ["TopProcessCount"] = TopProcessCount.ToString(),
                    ["DataRetentionDays"] = DataRetentionDays.ToString()
                },
                ["System"] = new()
                {
                    ["StartWithWindows"] = StartWithWindows.ToString().ToLower()
                }
            };

            var response = await _httpClient.PutAsJsonAsync($"{ApiBaseUrl}/settings", settings);
            response.EnsureSuccessStatusCode();

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存配置失败: {ex.Message}");
            return false;
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
