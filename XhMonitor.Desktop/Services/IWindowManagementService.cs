using XhMonitor.Desktop.Models;

namespace XhMonitor.Desktop.Services;

public interface IWindowManagementService
{
    void InitializeMainWindow();

    void ShowMainWindow();

    void HideMainWindow();

    void CloseMainWindow();

    void ApplyDisplaySettings(TaskbarDisplaySettings settings);

    Task RefreshDisplayModesAsync();

    bool TryActivateEdgeDockMode();
}
