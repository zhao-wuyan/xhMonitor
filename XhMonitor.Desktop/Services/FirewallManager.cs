using System.Diagnostics;
using System.Security.Principal;

namespace XhMonitor.Desktop.Services;

/// <summary>
/// Windows防火墙管理器，用于自动配置防火墙规则
/// </summary>
public static class FirewallManager
{
    private const string RuleName = "XhMonitor Web Access";
    private const string RuleDescription = "Allow inbound connections to XhMonitor web interface";
    private const int UacCanceledExitCode = 1223;

    /// <summary>
    /// 配置防火墙规则（如果启用局域网访问）
    /// </summary>
    /// <param name="enableLanAccess">是否启用局域网访问</param>
    /// <param name="port">Web服务端口</param>
    /// <returns>操作结果</returns>
    public static async Task<(bool Success, string Message)> ConfigureFirewallAsync(bool enableLanAccess, int port)
    {
        try
        {
            if (enableLanAccess)
            {
                // 检查规则是否已存在
                var exists = await CheckRuleExistsAsync();

                if (exists)
                {
                    // 更新现有规则
                    var (updateOk, updateError) = await UpdateFirewallRuleAsync(port);
                    return updateOk
                        ? (true, $"防火墙规则已更新（端口 {port}）")
                        : (false, updateError ?? "更新防火墙规则失败");
                }
                else
                {
                    // 创建新规则
                    var (createOk, createError) = await CreateFirewallRuleAsync(port);
                    return createOk
                        ? (true, $"防火墙规则已创建（端口 {port}）")
                        : (false, createError ?? "创建防火墙规则失败，可能需要管理员权限");
                }
            }
            else
            {
                // 禁用局域网访问时，删除防火墙规则
                var exists = await CheckRuleExistsAsync();
                if (exists)
                {
                    var (deleteOk, deleteError) = await DeleteFirewallRuleAsync();
                    return deleteOk
                        ? (true, "防火墙规则已删除")
                        : (false, deleteError ?? "删除防火墙规则失败");
                }

                return (true, "无需配置防火墙");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"配置防火墙失败: {ex.Message}");
            return (false, $"配置防火墙失败: {ex.Message}");
        }
    }

    private static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static ProcessStartInfo CreateNetshStartInfo(string arguments, bool elevate)
    {
        if (elevate)
        {
            return new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = false
            };
        }

        return new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunNetshAsync(
        string arguments,
        bool elevate,
        CancellationToken cancellationToken = default)
    {
        var startInfo = CreateNetshStartInfo(arguments, elevate);
        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return (-1, string.Empty, "Failed to start netsh process.");
        }

        if (!startInfo.RedirectStandardOutput)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (process.ExitCode, string.Empty, string.Empty);
        }

        var readOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var readErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdOut = await readOutTask.ConfigureAwait(false);
        var stdErr = await readErrTask.ConfigureAwait(false);
        return (process.ExitCode, stdOut, stdErr);
    }

    /// <summary>
    /// 检查防火墙规则是否存在
    /// </summary>
    private static async Task<bool> CheckRuleExistsAsync()
    {
        try
        {
            var (_, output, _) = await RunNetshAsync(
                $"advfirewall firewall show rule name=\"{RuleName}\"",
                elevate: false).ConfigureAwait(false);

            // 如果规则存在，输出会包含规则名称
            return output.Contains(RuleName);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 创建防火墙规则
    /// </summary>
    private static async Task<(bool Success, string? Error)> CreateFirewallRuleAsync(int port)
    {
        try
        {
            var elevate = !IsRunningAsAdministrator();
            var (exitCode, _, stdErr) = await RunNetshAsync(
                $"advfirewall firewall add rule name=\"{RuleName}\" " +
                $"description=\"{RuleDescription}\" " +
                $"dir=in action=allow protocol=TCP localport={port} " +
                $"profile=any enable=yes",
                elevate).ConfigureAwait(false);

            if (exitCode == 0)
            {
                Debug.WriteLine($"防火墙规则创建成功: {RuleName} (端口 {port})");
                return (true, null);
            }

            if (exitCode == UacCanceledExitCode)
            {
                return (false, "已取消管理员授权，无法配置防火墙规则。");
            }

            Debug.WriteLine($"创建防火墙规则失败 (ExitCode: {exitCode}): {stdErr}");
            return (false, "创建防火墙规则失败，可能需要管理员权限。");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"创建防火墙规则异常: {ex.Message}");
            return (false, "创建防火墙规则异常。");
        }
    }

    /// <summary>
    /// 更新防火墙规则
    /// </summary>
    private static async Task<(bool Success, string? Error)> UpdateFirewallRuleAsync(int port)
    {
        try
        {
            var elevate = !IsRunningAsAdministrator();
            var (exitCode, _, stdErr) = await RunNetshAsync(
                $"advfirewall firewall set rule name=\"{RuleName}\" dir=in new " +
                $"enable=yes profile=any protocol=TCP localport={port} action=allow",
                elevate).ConfigureAwait(false);

            if (exitCode == 0)
            {
                Debug.WriteLine($"防火墙规则更新成功: {RuleName} (端口 {port})");
                return (true, null);
            }

            if (exitCode == UacCanceledExitCode)
            {
                return (false, "已取消管理员授权，无法更新防火墙规则。");
            }

            Debug.WriteLine($"更新防火墙规则失败 (ExitCode: {exitCode}): {stdErr}");

            // 极端情况：规则不存在（或被外部删除），回退到创建规则。
            var exists = await CheckRuleExistsAsync().ConfigureAwait(false);
            if (!exists)
            {
                return await CreateFirewallRuleAsync(port).ConfigureAwait(false);
            }

            return (false, "更新防火墙规则失败，可能需要管理员权限。");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"更新防火墙规则异常: {ex.Message}");
            return (false, "更新防火墙规则异常。");
        }
    }

    /// <summary>
    /// 删除防火墙规则
    /// </summary>
    private static async Task<(bool Success, string? Error)> DeleteFirewallRuleAsync()
    {
        try
        {
            var elevate = !IsRunningAsAdministrator();
            var (exitCode, _, stdErr) = await RunNetshAsync(
                $"advfirewall firewall delete rule name=\"{RuleName}\"",
                elevate).ConfigureAwait(false);

            if (exitCode == 0)
            {
                Debug.WriteLine($"防火墙规则删除成功: {RuleName}");
                return (true, null);
            }

            if (exitCode == UacCanceledExitCode)
            {
                return (false, "已取消管理员授权，无法删除防火墙规则。");
            }

            Debug.WriteLine($"删除防火墙规则失败 (ExitCode: {exitCode}): {stdErr}");
            return (false, "删除防火墙规则失败，可能需要管理员权限。");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"删除防火墙规则异常: {ex.Message}");
            return (false, "删除防火墙规则异常。");
        }
    }

    /// <summary>
    /// 获取当前防火墙规则状态
    /// </summary>
    public static async Task<string> GetFirewallStatusAsync()
    {
        try
        {
            var exists = await CheckRuleExistsAsync();
            if (!exists)
            {
                return "未配置";
            }

            var (_, output, _) = await RunNetshAsync(
                $"advfirewall firewall show rule name=\"{RuleName}\"",
                elevate: false).ConfigureAwait(false);

            // 解析端口信息
            var lines = output.Split('\n');
            foreach (var line in lines)
            {
                if (line.Contains("LocalPort:") || line.Contains("本地端口:"))
                {
                    var port = line.Split(':')[1].Trim();
                    return $"已配置（端口 {port}）";
                }
            }

            return "已配置";
        }
        catch
        {
            return "未知";
        }
    }
}
