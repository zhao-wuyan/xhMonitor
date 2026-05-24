using XhMonitor.Core.Models;

namespace XhMonitor.Core.Interfaces;

public interface IHardwarePlatformDetector
{
    HardwarePlatformInfo Detect();
}
