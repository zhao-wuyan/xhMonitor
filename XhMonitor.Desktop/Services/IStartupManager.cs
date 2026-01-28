namespace XhMonitor.Desktop.Services;

/// <summary>
/// 开机自启动管理接口，用于依赖注入和单元测试。
/// </summary>
public interface IStartupManager
{
    /// <summary>
    /// 设置开机自启动。
    /// </summary>
    /// <param name="enable">是否启用</param>
    /// <returns>操作是否成功</returns>
    bool SetStartup(bool enable);

    /// <summary>
    /// 检查是否已设置开机自启动。
    /// </summary>
    /// <returns>是否已启用</returns>
    bool IsStartupEnabled();
}
