namespace XhMonitor.Core.Models;

public sealed record PowerStatus(
    double CurrentWatts,
    double LimitWatts,
    int? SchemeIndex,
    PowerScheme Limits);

