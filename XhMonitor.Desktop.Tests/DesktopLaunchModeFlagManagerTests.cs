using FluentAssertions;
using XhMonitor.Desktop.Services;
using Xunit;

namespace XhMonitor.Desktop.Tests;

public sealed class DesktopLaunchModeFlagManagerTests : IDisposable
{
    private const string FloatingModeFlagFileName = "launch-mode-floating.flag";
    private const string MiniEdgeDockModeFlagFileName = "launch-mode-mini-edge-dock.flag";

    private readonly string _testDirectoryPath;

    public DesktopLaunchModeFlagManagerTests()
    {
        _testDirectoryPath = Path.Combine(
            Path.GetTempPath(),
            $"xhmonitor-launch-mode-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectoryPath);
    }

    [Fact]
    public void TryGetLaunchMode_ShouldReturnNull_WhenNoFlagFileExists()
    {
        var manager = new DesktopLaunchModeFlagManager(_testDirectoryPath);

        var launchMode = manager.TryGetLaunchMode();

        launchMode.Should().BeNull();
    }

    [Fact]
    public void SetLaunchMode_ShouldPersistFloatingWindow_AndRemoveMiniEdgeDockFlag()
    {
        var manager = new DesktopLaunchModeFlagManager(_testDirectoryPath);
        var floatingFlagPath = Path.Combine(_testDirectoryPath, FloatingModeFlagFileName);
        var miniEdgeDockFlagPath = Path.Combine(_testDirectoryPath, MiniEdgeDockModeFlagFileName);
        File.WriteAllText(miniEdgeDockFlagPath, "1");

        manager.SetLaunchMode(DesktopLaunchMode.FloatingWindow);

        manager.TryGetLaunchMode().Should().Be(DesktopLaunchMode.FloatingWindow);
        File.Exists(floatingFlagPath).Should().BeTrue();
        File.Exists(miniEdgeDockFlagPath).Should().BeFalse();
    }

    [Fact]
    public void SetLaunchMode_ShouldPersistMiniEdgeDock_AndRemoveFloatingWindowFlag()
    {
        var manager = new DesktopLaunchModeFlagManager(_testDirectoryPath);
        var floatingFlagPath = Path.Combine(_testDirectoryPath, FloatingModeFlagFileName);
        var miniEdgeDockFlagPath = Path.Combine(_testDirectoryPath, MiniEdgeDockModeFlagFileName);
        File.WriteAllText(floatingFlagPath, "1");

        manager.SetLaunchMode(DesktopLaunchMode.MiniEdgeDock);

        manager.TryGetLaunchMode().Should().Be(DesktopLaunchMode.MiniEdgeDock);
        File.Exists(miniEdgeDockFlagPath).Should().BeTrue();
        File.Exists(floatingFlagPath).Should().BeFalse();
    }

    [Fact]
    public void TryGetLaunchMode_ShouldUseNewestFlag_WhenBothFlagsExist()
    {
        var manager = new DesktopLaunchModeFlagManager(_testDirectoryPath);
        var floatingFlagPath = Path.Combine(_testDirectoryPath, FloatingModeFlagFileName);
        var miniEdgeDockFlagPath = Path.Combine(_testDirectoryPath, MiniEdgeDockModeFlagFileName);
        File.WriteAllText(floatingFlagPath, "1");
        File.WriteAllText(miniEdgeDockFlagPath, "1");

        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(floatingFlagPath, now.AddMinutes(1));
        File.SetLastWriteTimeUtc(miniEdgeDockFlagPath, now);

        manager.TryGetLaunchMode().Should().Be(DesktopLaunchMode.FloatingWindow);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectoryPath))
            {
                Directory.Delete(_testDirectoryPath, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }
}
