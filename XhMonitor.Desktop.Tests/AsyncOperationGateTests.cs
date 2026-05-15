using FluentAssertions;
using XhMonitor.Desktop.Services;

namespace XhMonitor.Desktop.Tests;

public class AsyncOperationGateTests
{
    [Fact]
    public void TryEnter_ShouldRejectSecondEntry_UntilScopeDisposed()
    {
        var gate = new AsyncOperationGate();

        var firstEntered = gate.TryEnter(out var firstScope);
        var secondEntered = gate.TryEnter(out var secondScope);

        firstEntered.Should().BeTrue();
        secondEntered.Should().BeFalse();
        gate.IsRunning.Should().BeTrue();

        secondScope.Dispose();
        firstScope.Dispose();

        gate.IsRunning.Should().BeFalse();
        gate.TryEnter(out var thirdScope).Should().BeTrue();
        thirdScope.Dispose();
    }

    [Fact]
    public async Task TryEnter_ShouldAllowOnlyOneConcurrentCaller()
    {
        var gate = new AsyncOperationGate();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstTask = Task.Run(async () =>
        {
            gate.TryEnter(out var scope).Should().BeTrue();
            using (scope)
            {
                ready.SetResult();
                await release.Task;
            }
        });

        await ready.Task;

        gate.TryEnter(out var secondScope).Should().BeFalse();
        secondScope.Dispose();

        release.SetResult();
        await firstTask;

        gate.TryEnter(out var thirdScope).Should().BeTrue();
        thirdScope.Dispose();
    }
}
