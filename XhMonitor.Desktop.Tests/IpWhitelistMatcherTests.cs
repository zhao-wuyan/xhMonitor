using System.Net;
using FluentAssertions;
using XhMonitor.Desktop.Services;
using Xunit;

namespace XhMonitor.Desktop.Tests;

public class IpWhitelistMatcherTests
{
    [Fact]
    public void Parse_ShouldHaveNoRules_WhenWhitelistIsEmpty()
    {
        var matcher = IpWhitelistMatcher.Parse("");
        matcher.HasRules.Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_ShouldMatchExactIp()
    {
        var matcher = IpWhitelistMatcher.Parse("192.168.1.10");
        matcher.HasRules.Should().BeTrue();

        matcher.IsAllowed(IPAddress.Parse("192.168.1.10")).Should().BeTrue();
        matcher.IsAllowed(IPAddress.Parse("192.168.1.11")).Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_ShouldMatchCidrIpv4()
    {
        var matcher = IpWhitelistMatcher.Parse("192.168.1.0/24");
        matcher.HasRules.Should().BeTrue();

        matcher.IsAllowed(IPAddress.Parse("192.168.1.1")).Should().BeTrue();
        matcher.IsAllowed(IPAddress.Parse("192.168.1.254")).Should().BeTrue();
        matcher.IsAllowed(IPAddress.Parse("192.168.2.1")).Should().BeFalse();
    }

    [Fact]
    public void Parse_ShouldIgnoreInvalidEntries_ButKeepValidOnes()
    {
        var matcher = IpWhitelistMatcher.Parse("not-an-ip, 10.0.0.1 , 10.0.0.0/8, bad/33");
        matcher.HasRules.Should().BeTrue();

        matcher.IsAllowed(IPAddress.Parse("10.0.0.1")).Should().BeTrue();
        matcher.IsAllowed(IPAddress.Parse("10.123.45.67")).Should().BeTrue();
        matcher.IsAllowed(IPAddress.Parse("192.168.1.1")).Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_ShouldMatchCidrIpv6()
    {
        var matcher = IpWhitelistMatcher.Parse("2001:db8::/32");
        matcher.HasRules.Should().BeTrue();

        matcher.IsAllowed(IPAddress.Parse("2001:db8::1")).Should().BeTrue();
        matcher.IsAllowed(IPAddress.Parse("2001:db9::1")).Should().BeFalse();
    }
}

