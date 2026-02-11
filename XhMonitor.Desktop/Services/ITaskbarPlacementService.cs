using XhMonitor.Desktop.Models;

namespace XhMonitor.Desktop.Services;

public interface ITaskbarPlacementService
{
    bool TryGetPlacement(double windowWidth, double windowHeight, out double left, out double top, out EdgeDockSide dockSide);
}
