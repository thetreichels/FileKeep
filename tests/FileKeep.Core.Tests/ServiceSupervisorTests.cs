using FileKeep.Service;
using Xunit;

namespace FileKeep.Core.Tests;

/// <summary>
/// SF-6: the service host races the dashboard and the scheduler — if
/// either half faults or ends, the other is stopped and the failure
/// propagates, instead of the survivor running forever while the host
/// reports RUNNING.
/// </summary>
public sealed class ServiceSupervisorTests
{
    /// <summary>A half that runs until its token is cancelled, and
    /// reports when it observed the cancellation.</summary>
    private static (Func<CancellationToken, Task> Run, Task ObservedStop) Forever()
    {
        var stopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task> run = async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { stopped.TrySetResult(); }
        };
        return (run, stopped.Task);
    }

    [Fact]
    public async Task FirstFault_FailsTheService_AndStopsTheSecond()
    {
        var (forever, observedStop) = Forever();
        var boom = new InvalidOperationException("port already bound");
        Func<CancellationToken, Task> faulty = _ => Task.FromException(boom);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ServiceSupervisor.RunTogetherAsync(
                "dashboard", faulty, "scheduler", forever,
                CancellationToken.None));
        Assert.Same(boom, ex);
        // The survivor was cancelled, not left running.
        await observedStop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SecondFault_FailsTheService_AndStopsTheFirst()
    {
        var (forever, observedStop) = Forever();
        var boom = new InvalidOperationException("scheduler died");
        Func<CancellationToken, Task> faulty = _ => Task.FromException(boom);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ServiceSupervisor.RunTogetherAsync(
                "dashboard", forever, "scheduler", faulty,
                CancellationToken.None));
        Assert.Same(boom, ex);
        await observedStop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HalfEndingWithoutFault_StillFailsTheService()
    {
        var (forever, observedStop) = Forever();
        Func<CancellationToken, Task> endsCleanly = _ => Task.CompletedTask;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ServiceSupervisor.RunTogetherAsync(
                "dashboard", endsCleanly, "scheduler", forever,
                CancellationToken.None));
        Assert.Contains("dashboard", ex.Message);
        await observedStop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExternalCancellation_ReturnsCleanly()
    {
        var (first, firstStopped) = Forever();
        var (second, secondStopped) = Forever();
        using var cts = new CancellationTokenSource();

        Task run = ServiceSupervisor.RunTogetherAsync(
            "dashboard", first, "scheduler", second, cts.Token);
        cts.Cancel();
        // A requested stop is not a crash: no exception escapes.
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await firstStopped.WaitAsync(TimeSpan.FromSeconds(5));
        await secondStopped.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
