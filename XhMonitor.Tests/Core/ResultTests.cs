using FluentAssertions;
using XhMonitor.Core.Common;

namespace XhMonitor.Tests.Core;

public class ResultTests
{
    [Fact]
    public void Success_ShouldExposeValue()
    {
        var result = Result<int, string>.Success(5);

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Value.Should().Be(5);
        Action act = () => _ = result.Error;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Failure_ShouldExposeError()
    {
        var result = Result<int, string>.Failure("error");

        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("error");
        Action act = () => _ = result.Value;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ImplicitConversion_ShouldCreateExpectedResult()
    {
        Result<int, string> success = 7;
        Result<int, string> failure = "failed";

        success.IsSuccess.Should().BeTrue();
        success.Value.Should().Be(7);
        failure.IsFailure.Should().BeTrue();
        failure.Error.Should().Be("failed");
    }
}
