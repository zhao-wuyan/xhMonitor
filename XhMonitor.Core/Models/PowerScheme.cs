namespace XhMonitor.Core.Models;

public sealed record PowerScheme(int StapmWatts, int FastWatts, int SlowWatts)
{
    public string ToDisplayString() => $"{StapmWatts}-{FastWatts}-{SlowWatts}";
}

