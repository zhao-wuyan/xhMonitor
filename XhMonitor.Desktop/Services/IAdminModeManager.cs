namespace XhMonitor.Desktop.Services;

/// <summary>
/// 管理员模式管理接口，用于依赖注入和单元测试。
/// </summary>
public interface IAdminModeManager
{
    /// <summary>
    /// 检查当前进程是否以管理员权限运行。
    /// </summary>
    /// <returns>是否具有管理员权限</returns>
    bool IsRunningAsAdministrator();

    /// <summary>
    /// 以管理员权限重启应用程序。
    /// </summary>
    /// <returns>是否成功启动</returns>
    bool RestartAsAdministrator();

    /// <summary>
    /// 获取本地缓存的管理员模式开关状态（用于启动时判断 Service 是否需要管理员权限）。
    /// </summary>
    /// <returns>管理员模式是否启用</returns>
    bool IsAdminModeEnabled();

    /// <summary>
    /// 设置本地缓存的管理员模式开关状态。
    /// </summary>
    /// <param name="enabled">是否启用管理员模式</param>
    void SetAdminModeEnabled(bool enabled);
}
