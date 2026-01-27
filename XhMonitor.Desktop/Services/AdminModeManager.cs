using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace XhMonitor.Desktop.Services;

/// <summary>
/// 管理员模式管理器
/// </summary>
public class AdminModeManager : IAdminModeManager
{
    private static readonly string AdminModeFilePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "admin-mode.flag");

    /// <summary>
    /// 检查当前进程是否以管理员权限运行
    /// </summary>
    /// <returns>是否具有管理员权限</returns>
    public bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to check administrator status: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 以管理员权限重启应用程序
    /// </summary>
    /// <returns>是否成功启动</returns>
    public bool RestartAsAdministrator()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                Debug.WriteLine("Failed to get executable path");
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas" // 请求管理员权限
            };

            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to restart as administrator: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 获取本地缓存的管理员模式开关状态
    /// </summary>
    /// <returns>管理员模式是否启用</returns>
    public bool IsAdminModeEnabled()
    {
        try
        {
            return File.Exists(AdminModeFilePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to check admin mode flag: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 设置本地缓存的管理员模式开关状态
    /// </summary>
    /// <param name="enabled">是否启用管理员模式</param>
    public void SetAdminModeEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                File.WriteAllText(AdminModeFilePath, "1");
            }
            else if (File.Exists(AdminModeFilePath))
            {
                File.Delete(AdminModeFilePath);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to set admin mode flag: {ex.Message}");
        }
    }
}
