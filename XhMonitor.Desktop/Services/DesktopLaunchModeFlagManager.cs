using System.Diagnostics;
using System.IO;

namespace XhMonitor.Desktop.Services;

/// <summary>
/// 使用本地 flag 文件持久化 Desktop 启动显示模式。
/// </summary>
public sealed class DesktopLaunchModeFlagManager : IDesktopLaunchModeFlagManager
{
    private readonly string _floatingModeFlagPath;
    private readonly string _miniEdgeDockModeFlagPath;

    public DesktopLaunchModeFlagManager(string? desktopBaseDirectory = null)
    {
        var baseDirectory = string.IsNullOrWhiteSpace(desktopBaseDirectory)
            ? AppDomain.CurrentDomain.BaseDirectory
            : desktopBaseDirectory;

        _floatingModeFlagPath = Path.Combine(baseDirectory, "launch-mode-floating.flag");
        _miniEdgeDockModeFlagPath = Path.Combine(baseDirectory, "launch-mode-mini-edge-dock.flag");
    }

    public DesktopLaunchMode? TryGetLaunchMode()
    {
        try
        {
            var hasFloatingFlag = File.Exists(_floatingModeFlagPath);
            var hasMiniEdgeDockFlag = File.Exists(_miniEdgeDockModeFlagPath);

            if (!hasFloatingFlag && !hasMiniEdgeDockFlag)
            {
                return null;
            }

            if (hasFloatingFlag && !hasMiniEdgeDockFlag)
            {
                return DesktopLaunchMode.FloatingWindow;
            }

            if (!hasFloatingFlag && hasMiniEdgeDockFlag)
            {
                return DesktopLaunchMode.MiniEdgeDock;
            }

            var floatingWriteTime = File.GetLastWriteTimeUtc(_floatingModeFlagPath);
            var miniEdgeDockWriteTime = File.GetLastWriteTimeUtc(_miniEdgeDockModeFlagPath);
            return miniEdgeDockWriteTime >= floatingWriteTime
                ? DesktopLaunchMode.MiniEdgeDock
                : DesktopLaunchMode.FloatingWindow;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load desktop launch mode flag: {ex.Message}");
            return null;
        }
    }

    public void SetLaunchMode(DesktopLaunchMode launchMode)
    {
        try
        {
            switch (launchMode)
            {
                case DesktopLaunchMode.FloatingWindow:
                    File.WriteAllText(_floatingModeFlagPath, "1");
                    SafeDelete(_miniEdgeDockModeFlagPath);
                    break;
                case DesktopLaunchMode.MiniEdgeDock:
                    File.WriteAllText(_miniEdgeDockModeFlagPath, "1");
                    SafeDelete(_floatingModeFlagPath);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to set desktop launch mode flag: {ex.Message}");
        }
    }

    private static void SafeDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // ignore
        }
    }
}
