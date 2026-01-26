namespace XhMonitor.Core.Models;

public sealed record PowerSchemeSwitchResult(
    bool Success,
    string Message,
    int? PreviousSchemeIndex,
    int NewSchemeIndex,
    PowerScheme? NewScheme)
{
    public static PowerSchemeSwitchResult Fail(string message) => new(false, message, null, -1, null);
}

