using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

namespace XhMonitor.Desktop.Services;

/// <summary>
/// 管理 Windows 开机自启动功能
/// </summary>
public class StartupManager : IStartupManager
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "XhMonitor";

    /// <summary>
    /// 设置开机自启动
    /// </summary>
    /// <param name="enable">是否启用</param>
    /// <returns>操作是否成功</returns>
    public bool SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
            if (key == null)
            {
                Debug.WriteLine("Failed to open registry key");
                return false;
            }

            if (enable)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    Debug.WriteLine("Failed to get executable path");
                    return false;
                }

                key.SetValue(AppName, $"\"{exePath}\"");
                Debug.WriteLine($"Startup enabled: {exePath}");
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
                Debug.WriteLine("Startup disabled");
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to set startup: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 检查是否已设置开机自启动
    /// </summary>
    /// <returns>是否已启用</returns>
    public bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: false);
            if (key == null)
            {
                return false;
            }

            var value = key.GetValue(AppName) as string;
            return !string.IsNullOrEmpty(value);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to check startup status: {ex.Message}");
            return false;
        }
    }
}
