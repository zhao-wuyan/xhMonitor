using XhMonitor.Core.Enums;

namespace XhMonitor.Core.Interfaces;

public interface IGpuVendorDetector
{
    GpuVendor DetectVendor();
}

