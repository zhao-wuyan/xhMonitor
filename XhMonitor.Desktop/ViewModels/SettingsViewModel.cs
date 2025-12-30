using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace XhMonitor.Desktop.ViewModels;

public class SettingsViewModel : INotifyPropertyChanged
{
    private readonly HttpClient _httpClient;
    private const string ApiBaseUrl = "http://localhost:35179/api/v1/config";

    // 外观设置
    private string _themeColor = "Dark";
    private int _opacity = 60;

    // 数据采集设置
    private ObservableCollection<string> _processKeywords = new();
    private int _systemInterval = 1000;
    private int _processInterval = 5000;
    private int _topProcessCount = 10;
    private int _dataRetentionDays = 30;

    // 系统设置
    private bool _startWithWindows = false;
    private int _signalRPort = 35179;
    private int _webPort = 35180;

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

    public int SystemInterval
    {
        get => _systemInterval;
        set => SetProperty(ref _systemInterval, value);
    }

    public int ProcessInterval
    {
        get => _processInterval;
        set => SetProperty(ref _processInterval, value);
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

    public int SignalRPort
    {
        get => _signalRPort;
        set => SetProperty(ref _signalRPort, value);
    }

    public int WebPort
    {
        get => _webPort;
        set => SetProperty(ref _webPort, value);
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
                ThemeColor = JsonSerializer.Deserialize<string>(appearance["ThemeColor"]) ?? "Dark";
                Opacity = int.Parse(appearance["Opacity"]);
            }

            // 数据采集设置
            if (settings.TryGetValue("DataCollection", out var dataCollection))
            {
                var keywords = JsonSerializer.Deserialize<string[]>(dataCollection["ProcessKeywords"]) ?? Array.Empty<string>();
                ProcessKeywords = new ObservableCollection<string>(keywords);
                SystemInterval = int.Parse(dataCollection["SystemInterval"]);
                ProcessInterval = int.Parse(dataCollection["ProcessInterval"]);
                TopProcessCount = int.Parse(dataCollection["TopProcessCount"]);
                DataRetentionDays = int.Parse(dataCollection["DataRetentionDays"]);
            }

            // 系统设置
            if (settings.TryGetValue("System", out var system))
            {
                StartWithWindows = bool.Parse(system["StartWithWindows"]);
                SignalRPort = int.Parse(system["SignalRPort"]);
                WebPort = int.Parse(system["WebPort"]);
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
                    ["SystemInterval"] = SystemInterval.ToString(),
                    ["ProcessInterval"] = ProcessInterval.ToString(),
                    ["TopProcessCount"] = TopProcessCount.ToString(),
                    ["DataRetentionDays"] = DataRetentionDays.ToString()
                },
                ["System"] = new()
                {
                    ["StartWithWindows"] = StartWithWindows.ToString().ToLower(),
                    ["SignalRPort"] = SignalRPort.ToString(),
                    ["WebPort"] = WebPort.ToString()
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
