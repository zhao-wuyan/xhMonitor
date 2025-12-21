namespace XhMonitor.Core.Models;

/// <summary>
/// 动作执行结果
/// </summary>
public class ActionResult
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 结果消息
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 执行耗时（毫秒）
    /// </summary>
    public long ElapsedMilliseconds { get; set; }

    /// <summary>
    /// 附加数据
    /// </summary>
    public Dictionary<string, object>? Data { get; set; }

    /// <summary>
    /// 创建成功结果
    /// </summary>
    public static ActionResult Ok(string message = "", Dictionary<string, object>? data = null) => new()
    {
        Success = true,
        Message = message,
        Data = data
    };

    /// <summary>
    /// 创建失败结果
    /// </summary>
    public static ActionResult Fail(string message, Dictionary<string, object>? data = null) => new()
    {
        Success = false,
        Message = message,
        Data = data
    };
}
