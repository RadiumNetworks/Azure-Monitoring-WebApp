namespace MonitoringApp.Tests;

public sealed class AnalysisRefreshLoopTests
{
    [Fact]
    public async Task RefreshesAfterExactlyThirtySecondsAndStopsOnCancellation()
    {
        var clock = new ManualTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var running = AnalysisRefreshLoop.RunAsync(() =>
        {
            Interlocked.Increment(ref calls);
            refreshed.TrySetResult();
            return Task.CompletedTask;
        }, cancellation.Token, clock);

        Assert.Equal(TimeSpan.FromSeconds(30), clock.Period);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        await cancellation.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(1, calls);
        Assert.True(clock.Disposed);
    }

    [Fact]
    public async Task SlowRequestsNeverOverlapAndMissedTicksAreCoalesced()
    {
        var clock = new ManualTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var active = 0;
        var maximumActive = 0;
        var running = AnalysisRefreshLoop.RunAsync(async () =>
        {
            var current = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, current);
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                await release.Task;
            }
            else
                next.TrySetResult();
            Interlocked.Decrement(ref active);
        }, cancellation.Token, clock);

        clock.Advance(TimeSpan.FromSeconds(30));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal(1, calls);
        release.SetResult();
        await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
        Assert.Equal(1, maximumActive);
    }

    internal sealed class ManualTimeProvider : TimeProvider
    {
        private TimerCallback? callback;
        private object? state;
        private TimeSpan elapsed;
        private TimeSpan next;
        public TimeSpan Period { get; private set; }
        public bool Disposed { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            this.callback = callback;
            this.state = state;
            next = elapsed + dueTime;
            Period = period;
            return new ManualTimer(this);
        }

        public void Advance(TimeSpan duration)
        {
            elapsed += duration;
            while (!Disposed && next <= elapsed)
            {
                next += Period;
                callback!(state);
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                owner.next = owner.elapsed + dueTime;
                owner.Period = period;
                return !owner.Disposed;
            }

            public void Dispose() => owner.Disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
