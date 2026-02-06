using System.Globalization;

namespace XhMonitor.Desktop.Localization;

public sealed record RuntimePrompt(string Title, string Message, string DownloadUrl);

public static class RuntimeDependencyPrompts
{
    public const string DotNet8DownloadUrl = "https://dotnet.microsoft.com/download/dotnet/8.0";

    public static bool UseChineseUi(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        return string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase);
    }

    public static RuntimePrompt DotNet8OrHigherRequired(Version currentRuntimeVersion, CultureInfo? culture = null)
    {
        var isZh = UseChineseUi(culture);

        if (isZh)
        {
            return new RuntimePrompt(
                Title: "运行环境检测",
                Message:
                $"当前 .NET 版本：{currentRuntimeVersion}\n" +
                "XhMonitor 需要 .NET 8.0 或更高版本。\n\n" +
                "说明：当前安装包不带运行环境，且你的系统缺少相关运行环境。\n" +
                "你需要安装运行环境，或者下载包含运行环境的完整安装包。\n\n" +
                "【重要】请下载并安装 .NET Desktop Runtime 8.0：\n" +
                $"{DotNet8DownloadUrl}\n\n" +
                "安装步骤：\n" +
                "1. 点击上方链接访问官方下载页\n" +
                "2. 找到 \".NET Desktop Runtime 8.0.x\" 部分\n" +
                "3. 下载 Windows x64 版本（约 55 MB）\n" +
                "4. 安装后重启电脑\n\n" +
                "是否立即打开下载页面？",
                DownloadUrl: DotNet8DownloadUrl);
        }

        return new RuntimePrompt(
            Title: "Runtime requirement",
            Message:
            "You must install or update .NET to run this application.\n\n" +
            $"Current .NET version: {currentRuntimeVersion}\n" +
            "XhMonitor requires .NET 8.0 or later.\n\n" +
            "Note: This installer does not include the runtime, and your system is missing the required runtime.\n" +
            "Install the runtime, or download the full installer that includes the runtime.\n\n" +
            "IMPORTANT: Download and install .NET Desktop Runtime 8.0:\n" +
            $"{DotNet8DownloadUrl}\n\n" +
            "Open the download page now?",
            DownloadUrl: DotNet8DownloadUrl);
    }

    public static RuntimePrompt DesktopRuntimeMissing(CultureInfo? culture = null)
    {
        var isZh = UseChineseUi(culture);

        if (isZh)
        {
            return new RuntimePrompt(
                Title: "缺少 Desktop Runtime",
                Message:
                "检测到你使用的是轻量级版本（当前安装包不带运行环境）。\n" +
                "你的系统缺少 .NET Desktop Runtime，无法运行 XhMonitor。\n\n" +
                "你需要安装运行环境，或者下载包含运行环境的完整安装包。\n\n" +
                "【重要】请下载并安装 .NET Desktop Runtime 8.0：\n" +
                $"{DotNet8DownloadUrl}\n\n" +
                "注意：不要下载 .NET Runtime（基础版），必须下载 Desktop Runtime！\n\n" +
                "是否立即打开下载页面？",
                DownloadUrl: DotNet8DownloadUrl);
        }

        return new RuntimePrompt(
            Title: "Missing Desktop Runtime",
            Message:
            "You must install or update .NET to run this application.\n\n" +
            "This is the lite package (runtime not included).\n" +
            "Your system is missing .NET Desktop Runtime, so XhMonitor cannot run.\n\n" +
            "Install the runtime, or download the full installer that includes the runtime.\n\n" +
            "IMPORTANT: Download and install .NET Desktop Runtime 8.0:\n" +
            $"{DotNet8DownloadUrl}\n\n" +
            "Open the download page now?",
            DownloadUrl: DotNet8DownloadUrl);
    }
}
