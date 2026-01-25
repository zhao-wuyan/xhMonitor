using System.Diagnostics;
using XhMonitor.Core.Common;

namespace XhMonitor.Desktop.Services;

public interface IProcessManager
{
    Result<bool, string> KillProcess(int processId, bool entireTree);

    bool TryGetProcessById(int processId, out Process? process);
}
