using System.Diagnostics;
using System.Security.Principal;

namespace XhMonitor.Desktop.Services;

/// <summary>
/// 管理员模式管理器
/// </summary>
public static class AdminModeManager
{
    /// <summary>
    /// 检查当前进程是否以管理员权限运行
    /// </summary>
    /// <returns>是否具有管理员权限</returns>
    public static bool IsRunningAsAdministrator()
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
    public static bool RestartAsAdministrator()
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
}
