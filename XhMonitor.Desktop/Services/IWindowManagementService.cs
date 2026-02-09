namespace XhMonitor.Desktop.Services;

public interface IWindowManagementService
{
    void InitializeMainWindow();

    void ShowMainWindow();

    void HideMainWindow();

    void CloseMainWindow();

    Task RefreshDisplayModesAsync();
}
