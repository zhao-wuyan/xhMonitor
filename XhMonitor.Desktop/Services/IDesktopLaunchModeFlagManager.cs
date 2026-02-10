namespace XhMonitor.Desktop.Services;

/// <summary>
/// Desktop 启动显示模式枚举。
/// </summary>
public enum DesktopLaunchMode
{
    /// <summary>
    /// 启动后优先显示悬浮窗模式。
    /// </summary>
    FloatingWindow = 0,

    /// <summary>
    /// 启动后优先显示迷你/贴边模式。
    /// </summary>
    MiniEdgeDock = 1
}

/// <summary>
/// 管理 Desktop 启动显示模式的本地标识文件。
/// </summary>
public interface IDesktopLaunchModeFlagManager
{
    /// <summary>
    /// 读取当前启动模式标识。不存在时返回 null。
    /// </summary>
    DesktopLaunchMode? TryGetLaunchMode();

    /// <summary>
    /// 设置并持久化启动模式标识。
    /// </summary>
    /// <param name="launchMode">目标启动模式。</param>
    void SetLaunchMode(DesktopLaunchMode launchMode);
}
