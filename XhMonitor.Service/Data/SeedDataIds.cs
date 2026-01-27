namespace XhMonitor.Service.Data;

/// <summary>
/// 种子数据 ID 常量类，集中管理所有 HasData 种子数据的 ID 值。
/// EF Core HasData 要求显式指定 ID，此类提供单一来源以避免硬编码和冲突。
///
/// ID 分配策略：
/// - ApplicationSettings: 1-100 范围
/// - AlertConfiguration: 1-100 范围
/// </summary>
internal static class SeedDataIds
{
    /// <summary>
    /// ApplicationSettings 表种子数据 ID。
    /// </summary>
    public static class ApplicationSettings
    {
        // 外观设置 (1-2)
        public const int ThemeColor = 1;
        public const int Opacity = 2;

        // 数据采集设置 (3, 6-7)
        public const int ProcessKeywords = 3;
        public const int TopProcessCount = 6;
        public const int DataRetentionDays = 7;

        // 系统设置 (8)
        public const int StartWithWindows = 8;

        // 监控开关设置 (9-15)
        public const int MonitorCpu = 9;
        public const int MonitorMemory = 10;
        public const int MonitorGpu = 11;
        public const int MonitorVram = 12;
        public const int MonitorPower = 13;
        public const int MonitorNetwork = 14;
        public const int AdminMode = 15;
    }

    /// <summary>
    /// AlertConfiguration 表种子数据 ID。
    /// </summary>
    public static class AlertConfiguration
    {
        public const int Cpu = 1;
        public const int Memory = 2;
        public const int Gpu = 3;
        public const int Vram = 4;
    }
}
