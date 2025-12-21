namespace XhMonitor.Core.Enums;

/// <summary>
/// 动作执行类型
/// </summary>
public enum ActionType
{
    /// <summary>
    /// 命令行命令
    /// </summary>
    Command,

    /// <summary>
    /// 脚本文件（PowerShell、Batch等）
    /// </summary>
    Script,

    /// <summary>
    /// 插件（动态加载的DLL）
    /// </summary>
    Plugin
}
