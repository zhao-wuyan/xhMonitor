namespace XhMonitor.Desktop.Models;

public sealed class PowerSchemeSwitchResponse
{
    public string Message { get; set; } = string.Empty;

    public int? PreviousSchemeIndex { get; set; }

    public int NewSchemeIndex { get; set; }

    public PowerSchemeDto? Scheme { get; set; }
}

public sealed class PowerSchemeDto
{
    public int StapmWatts { get; set; }
    public int FastWatts { get; set; }
    public int SlowWatts { get; set; }

    public string ToDisplayString() => $"{StapmWatts}-{FastWatts}-{SlowWatts}";
}

