using System.Diagnostics;
using XhMonitor.Core.Common;

namespace XhMonitor.Desktop.Services;

public sealed class ProcessManager : IProcessManager
{
    public Result<bool, string> KillProcess(int processId, bool entireTree)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: entireTree);
            process.WaitForExit(5000);
            return Result<bool, string>.Success(true);
        }
        catch (ArgumentException)
        {
            return Result<bool, string>.Failure("进程不存在");
        }
        catch (InvalidOperationException)
        {
            return Result<bool, string>.Failure("进程已退出");
        }
        catch (UnauthorizedAccessException)
        {
            return Result<bool, string>.Failure("无权限关闭进程");
        }
        catch (Exception ex)
        {
            return Result<bool, string>.Failure($"关闭进程失败: {ex.Message}");
        }
    }

    public bool TryGetProcessById(int processId, out Process? process)
    {
        process = null;
        try
        {
            process = Process.GetProcessById(processId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
