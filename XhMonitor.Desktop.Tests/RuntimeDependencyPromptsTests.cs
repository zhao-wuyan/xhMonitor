using System.Globalization;
using FluentAssertions;
using XhMonitor.Desktop.Localization;
using Xunit;

namespace XhMonitor.Desktop.Tests;

public class RuntimeDependencyPromptsTests
{
    [Theory]
    [InlineData("zh-CN", true)]
    [InlineData("zh-TW", true)]
    [InlineData("zh-HK", true)]
    [InlineData("en-US", false)]
    [InlineData("ja-JP", false)]
    public void UseChineseUi_ShouldOnlyTreatZhAsChinese(string cultureName, bool expected)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        RuntimeDependencyPrompts.UseChineseUi(culture).Should().Be(expected);
    }

    [Fact]
    public void DotNet8OrHigherRequired_ShouldIncludeLiteInstallerHint_InChinese()
    {
        var prompt = RuntimeDependencyPrompts.DotNet8OrHigherRequired(
            currentRuntimeVersion: new Version(7, 0, 0),
            culture: CultureInfo.GetCultureInfo("zh-TW"));

        prompt.Title.Should().Be("运行环境检测");
        prompt.Message.Should().Contain("当前安装包不带运行环境");
        prompt.Message.Should().Contain("需要安装运行环境");
        prompt.Message.Should().Contain("完整安装包");
        prompt.Message.Should().Contain("7.0.0");
    }

    [Fact]
    public void DesktopRuntimeMissing_ShouldExplainRuntimeNotIncluded_InEnglish()
    {
        var prompt = RuntimeDependencyPrompts.DesktopRuntimeMissing(CultureInfo.GetCultureInfo("en-US"));

        prompt.Title.Should().Be("Missing Desktop Runtime");
        prompt.Message.Should().Contain("lite package");
        prompt.Message.Should().Contain("runtime not included");
        prompt.Message.Should().Contain("download the full installer");
        prompt.DownloadUrl.Should().Be(RuntimeDependencyPrompts.DotNet8DownloadUrl);
    }
}

