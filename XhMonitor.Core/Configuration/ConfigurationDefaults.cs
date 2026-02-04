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
        public const int Opacity = 90;
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
        public static readonly string[] ProcessKeywords = new[] { "python", "llama-server" };

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
         /// 是否启用局域网访问默认值。
         /// 该设置为用户偏好，存储在数据库 ApplicationSettings (System.EnableLanAccess)，可在运行时修改。
         /// 启用后，Web服务器将监听 0.0.0.0，允许局域网内其他设备访问。
         /// </summary>
        public const bool EnableLanAccess = false;

        /// <summary>
         /// 是否启用访问密钥认证默认值。
         /// 该设置为用户偏好，存储在数据库 ApplicationSettings (System.EnableAccessKey)，可在运行时修改。
         /// 启用后，局域网访问需要提供访问密钥。
         /// </summary>
        public const bool EnableAccessKey = false;

        /// <summary>
         /// 访问密钥默认值（空表示未设置）。
         /// 该设置为用户偏好，存储在数据库 ApplicationSettings (System.AccessKey)，可在运行时修改。
         /// </summary>
        public const string AccessKey = "";

        /// <summary>
         /// IP白名单默认值（空表示不限制）。
         /// 该设置为用户偏好，存储在数据库 ApplicationSettings (System.IpWhitelist)，可在运行时修改。
         /// 格式：逗号分隔的IP地址或CIDR，例如："192.168.1.100,192.168.1.0/24"
         /// </summary>
        public const string IpWhitelist = "";

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

    /// <summary>
    /// 配置键名常量，用于类型安全的字典键访问。
    /// 避免魔法字符串，支持编译时检查和 IDE 重构。
    /// </summary>
    public static class Keys
    {
        /// <summary>外观设置键名。</summary>
        public static class Appearance
        {
            public const string ThemeColor = "ThemeColor";
            public const string Opacity = "Opacity";
        }

        /// <summary>数据采集设置键名。</summary>
        public static class DataCollection
        {
            public const string ProcessKeywords = "ProcessKeywords";
            public const string TopProcessCount = "TopProcessCount";
            public const string DataRetentionDays = "DataRetentionDays";
        }

        /// <summary>监控开关设置键名。</summary>
        public static class Monitoring
        {
            public const string MonitorCpu = "MonitorCpu";
            public const string MonitorMemory = "MonitorMemory";
            public const string MonitorGpu = "MonitorGpu";
            public const string MonitorVram = "MonitorVram";
            public const string MonitorPower = "MonitorPower";
            public const string MonitorNetwork = "MonitorNetwork";
            public const string AdminMode = "AdminMode";
        }

        /// <summary>系统设置键名。</summary>
        public static class System
        {
            public const string StartWithWindows = "StartWithWindows";
            public const string EnableLanAccess = "EnableLanAccess";
            public const string EnableAccessKey = "EnableAccessKey";
            public const string AccessKey = "AccessKey";
            public const string IpWhitelist = "IpWhitelist";
        }

        /// <summary>分类名称常量。</summary>
        public static class Categories
        {
            public const string Appearance = "Appearance";
            public const string DataCollection = "DataCollection";
            public const string Monitoring = "Monitoring";
            public const string System = "System";
        }
    }
}
