using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace XhMonitor.Desktop.Services;

/// <summary>
/// 管理 Windows 开机自启动功能（使用计划任务实现，支持管理员权限启动且无 UAC 弹框）
/// </summary>
public class StartupManager : IStartupManager
{
    private const string TaskName = "XhMonitor";
    private readonly IAdminModeManager _adminModeManager;

    public StartupManager(IAdminModeManager adminModeManager)
    {
        _adminModeManager = adminModeManager;
    }

    /// <summary>
    /// 设置开机自启动（使用计划任务）
    /// </summary>
    /// <param name="enable">是否启用</param>
    /// <returns>操作是否成功</returns>
    public bool SetStartup(bool enable)
    {
        try
        {
            if (enable)
            {
                return CreateScheduledTask();
            }
            else
            {
                // 删除计划任务
                var result = DeleteScheduledTask();

                // 同时清理旧版本可能遗留的注册表项（兼容性处理）
                CleanupLegacyRegistryEntry();

                return result;
            }
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
            // 使用 schtasks /query 检查任务是否存在
            var result = RunSchtasks($"/query /tn \"{TaskName}\" /fo LIST", requireAdmin: false);
            return result.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to check startup status: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 创建计划任务
    /// </summary>
    private bool CreateScheduledTask()
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            Debug.WriteLine("Failed to get executable path");
            return false;
        }

        // 根据管理员模式决定是否以最高权限运行
        var runLevel = _adminModeManager.IsAdminModeEnabled() ? "HIGHEST" : "LIMITED";

        // 创建计划任务
        // /sc onlogon - 用户登录时启动
        // /rl HIGHEST - 以最高权限运行（管理员），不会触发 UAC
        // /rl LIMITED - 以普通权限运行
        // /f - 强制创建（覆盖已存在的任务，无需先删除）
        var arguments = $"/create /tn \"{TaskName}\" /tr \"\\\"{exePath}\\\"\" /sc onlogon /rl {runLevel} /f";

        var result = RunSchtasks(arguments, requireAdmin: true);

        if (result.ExitCode == 0)
        {
            Debug.WriteLine($"Startup enabled with run level: {runLevel}");
            return true;
        }
        else
        {
            Debug.WriteLine($"Failed to create scheduled task: {result.Output}");
            return false;
        }
    }

    /// <summary>
    /// 删除计划任务
    /// </summary>
    private bool DeleteScheduledTask()
    {
        var result = RunSchtasks($"/delete /tn \"{TaskName}\" /f", requireAdmin: true);

        // 当 requireAdmin=true 时，无法获取输出，只能依赖 ExitCode
        // ExitCode 0 = 成功
        // ExitCode 1 = 任务不存在（也视为成功）
        // ExitCode -1 = 用户取消 UAC 或启动失败
        if (result.ExitCode == 0 || result.ExitCode == 1)
        {
            // 验证任务是否真的被删除（防止 UAC 提升后退出码不准确）
            if (IsStartupEnabled())
            {
                Debug.WriteLine("Task deletion reported success but task still exists");
                return false;
            }

            Debug.WriteLine("Startup disabled or task does not exist");
            return true;
        }
        else
        {
            Debug.WriteLine($"Failed to delete scheduled task, exit code: {result.ExitCode}");
            return false;
        }
    }

    /// <summary>
    /// 清理旧版本遗留的注册表项（兼容性处理）
    /// </summary>
    private static void CleanupLegacyRegistryEntry()
    {
        const string registryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string appName = "XhMonitor";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryKeyPath, writable: true);
            if (key?.GetValue(appName) != null)
            {
                key.DeleteValue(appName, throwOnMissingValue: false);
                Debug.WriteLine("Legacy registry entry cleaned up");
            }
        }
        catch (Exception ex)
        {
            // 注册表清理失败不影响主流程
            Debug.WriteLine($"Failed to cleanup legacy registry entry: {ex.Message}");
        }
    }

    /// <summary>
    /// 运行 schtasks 命令
    /// </summary>
    /// <param name="arguments">命令参数</param>
    /// <param name="requireAdmin">是否需要管理员权限</param>
    /// <returns>执行结果</returns>
    private static (int ExitCode, string Output) RunSchtasks(string arguments, bool requireAdmin)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = requireAdmin,
            Verb = requireAdmin ? "runas" : string.Empty,
            CreateNoWindow = !requireAdmin,
            RedirectStandardOutput = !requireAdmin,
            RedirectStandardError = !requireAdmin,
            // .NET 8 默认不支持 GBK，使用 null 让系统自动选择编码
            StandardOutputEncoding = null,
            StandardErrorEncoding = null
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return (-1, "Failed to start process");
            }

            string output;
            if (requireAdmin)
            {
                // 管理员模式下无法重定向输出
                process.WaitForExit(10000);
                output = string.Empty;
            }
            else
            {
                output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit(10000);
            }

            return (process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户取消了 UAC 提示
            Debug.WriteLine("User cancelled UAC prompt");
            return (-1, "User cancelled UAC prompt");
        }
    }

    /// <summary>
    /// 更新计划任务的运行级别（当管理员模式变更时调用）
    /// </summary>
    /// <returns>操作是否成功</returns>
    public bool UpdateRunLevel()
    {
        if (!IsStartupEnabled())
        {
            // 如果没有启用开机自启动，不需要更新
            return true;
        }

        // 重新创建任务以更新运行级别
        return CreateScheduledTask();
    }
}
