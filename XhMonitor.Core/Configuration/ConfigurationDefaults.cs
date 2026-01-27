namespace XhMonitor.Core.Configuration;

/// <summary>
/// 应用默认配置值的单一来源。
/// </summary>
public static class ConfigurationDefaults
{
    /// <summary>
    /// 外观设置默认值。
    /// </summary>
    public static class Appearance
    {
        /// <summary>
         /// 主题颜色默认值。可选值由前端/客户端主题实现决定。
         /// 该设置为用户偏好，存储在数据库 ApplicationSettings (Appearance.ThemeColor)，可在运行时修改。
         /// </summary>
        public const string ThemeColor = "Dark";

        /// <summary>
         /// 窗口透明度默认值。范围: 0-100。
         /// 该设置为用户偏好，存储在数据库 ApplicationSettings (Appearance.Opacity)，可在运行时修改。
         /// </summary>
        public const int Opacity = 60;
    }

    /// <summary>
    /// 数据采集设置默认值。
    /// </summary>
    public static class DataCollection
    {
        /// <summary>
         /// 进程关键词默认列表。用于筛选需要关注的进程。
         /// 该设置为用户偏好，存储在数据库 ApplicationSettings (DataCollection.ProcessKeywords)，可在运行时修改。
         /// </summary>
        public static readonly string[] ProcessKeywords = new[] { "python", "llama" };

        /// <summary>
         /// 系统指标采集间隔(毫秒)。建议大于 0。
         /// 当前系统级采集间隔由服务端 appsettings.json (Monitor:SystemUsageIntervalSeconds) 管理；此处仅作为默认/兼容值。
         /// </summary>
        public const int SystemInterval = 1000;

        /// <summary>
         /// 进程指标采集间隔(毫秒)。建议大于 0。
         /// 当前进程级采集间隔由服务端 appsettings.json (Monitor:IntervalSeconds) 管理；此处仅作为默认/兼容值。
         /// </summary>
        public const int ProcessInterval = 5000;

        /// <summary>
         /// 进程列表展示的 Top N 数量。建议大于 0。
         /// 该设置为用户偏好，存储在数据库 ApplicationSettings (DataCollection.TopProcessCount)，可在运行时修改。
         /// </summary>
        public const int TopProcessCount = 10;

        /// <summary>
         /// 数据保留天数。建议大于 0。
         /// 该设置为用户偏好，存储在数据库 ApplicationSettings (DataCollection.DataRetentionDays)，可在运行时修改。
         /// </summary>
        public const int DataRetentionDays = 30;
    }

    /// <summary>
    /// 监控开关设置默认值。
    /// </summary>
    public static class Monitoring
    {
        /// <summary>
        /// 是否启用 CPU 监控默认值。
        /// </summary>
        public const bool MonitorCpu = true;

        /// <summary>
        /// 是否启用内存监控默认值。
        /// </summary>
        public const bool MonitorMemory = true;

        /// <summary>
        /// 是否启用 GPU 监控默认值。
        /// </summary>
        public const bool MonitorGpu = true;

        /// <summary>
        /// 是否启用显存监控默认值。
        /// </summary>
        public const bool MonitorVram = true;

        /// <summary>
        /// 是否启用功耗监控默认值。
        /// </summary>
        public const bool MonitorPower = true;

        /// <summary>
        /// 是否启用网络监控默认值。
        /// </summary>
        public const bool MonitorNetwork = true;

        /// <summary>
        /// 是否启用管理员模式默认值。
        /// </summary>
        public const bool AdminMode = false;
    }

    /// <summary>
    /// 系统设置默认值。
    /// </summary>
    public static class System
    {
        /// <summary>
         /// 是否随 Windows 启动默认值。
         /// 该设置为用户偏好，存储在数据库 ApplicationSettings (System.StartWithWindows)，可在运行时修改。
         /// </summary>
        public const bool StartWithWindows = false;

        /// <summary>
         /// SignalR 服务端口默认值。范围: 1-65535。
         /// 端口属于基础设施配置：由服务端 appsettings.json (Server:Port) 管理，不存储在数据库中。
         /// </summary>
        public const int SignalRPort = 35179;

        /// <summary>
         /// Web 服务端口默认值。范围: 1-65535。
         /// Web 前端端口属于基础设施配置，不存储在数据库中。
         /// </summary>
        public const int WebPort = 35180;
    }
}
