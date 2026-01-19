namespace XhMonitor.Core.Interfaces;

/// <summary>
/// 进程名称解析器接口
/// </summary>
public interface IProcessNameResolver
{
    /// <summary>
    /// 解析进程显示名称
    /// </summary>
    /// <param name="processName">进程名称</param>
    /// <param name="commandLine">命令行参数</param>
    /// <returns>解析后的显示名称</returns>
    string Resolve(string processName, string commandLine);
}
