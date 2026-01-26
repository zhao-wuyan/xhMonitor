namespace XhMonitor.Core.Models;

/// <summary>
/// RyzenAdj 输出快照（来自 <c>ryzenadj -i</c> 的表格）。<br/>
/// 数值单位沿用 RyzenAdj 原始输出（通常为 mW / mA / °C 等）。功耗相关字段在 Provider 中再做单位换算。
/// </summary>
public sealed record RyzenAdjSnapshot(
    double StapmLimit,
    double StapmValue,
    double FastLimit,
    double FastValue,
    double SlowLimit,
    double SlowValue);

