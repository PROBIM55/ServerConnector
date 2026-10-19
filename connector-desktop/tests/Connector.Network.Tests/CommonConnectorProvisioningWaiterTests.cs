using Connector.Network;

namespace Connector.Network.Tests;

public sealed class CommonConnectorProvisioningWaiterTests
{
    private static CommonConnectorConnectionSnapshot Snapshot(CommonConnectorConnectionStatus status) =>
        new(status, "device-1", 1, "fixture");

    private static ValueTask<CommonConnectorConnectionSnapshot> WaitAsync(CommonConnectorConnectionSnapshot initial,
        Func<CancellationToken, ValueTask<CommonConnectorConnectionSnapshot>> resume,
        CancellationToken cancellationToken = default, TimeProvider? timeProvider = null) =>
        CommonConnectorProvisioningWaiter.WaitAsync(initial, resume, _ => ValueTask.CompletedTask, cancellationToken, timeProvider);

    [Fact]
    public async Task Pending_RechecksSameDeviceUntilReady()
    {
        var clock = new ManualTimeProvider();
        var calls = 0;
        var pending = WaitAsync(Snapshot(CommonConnectorConnectionStatus.AccessPending),
            ct => { ct.ThrowIfCancellationRequested(); calls++; return ValueTask.FromResult(Snapshot(CommonConnectorConnectionStatus.Ready)); },
            timeProvider: clock).AsTask();
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, calls);
        clock.Advance(TimeSpan.FromSeconds(2));

        var result = await pending;

        Assert.Equal(CommonConnectorConnectionStatus.Ready, result.Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Deadline_CancelsInFlightResumeAndNeverClaimsReady()
    {
        var clock = new ManualTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCancelled = false;
        var pending = WaitAsync(Snapshot(CommonConnectorConnectionStatus.AccessPending),
            async ct =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { requestCancelled = ct.IsCancellationRequested; throw; }
                return Snapshot(CommonConnectorConnectionStatus.Ready);
            }, timeProvider: clock).AsTask();
        clock.Advance(TimeSpan.FromSeconds(2));
        await started.Task;
        clock.Advance(TimeSpan.FromSeconds(118));

        var result = await pending;

        Assert.True(requestCancelled);
        Assert.Equal(CommonConnectorConnectionStatus.AccessPending, result.Status);
        Assert.Null(result.AccessProfile);
    }

    [Fact]
    public async Task UserCancellation_IsPropagatedWithoutStartingResume()
    {
        var clock = new ManualTimeProvider();
        using var user = new CancellationTokenSource();
        var calls = 0;
        var pending = WaitAsync(Snapshot(CommonConnectorConnectionStatus.AwaitingVpnAuthorization),
            ct => { calls++; return ValueTask.FromResult(Snapshot(CommonConnectorConnectionStatus.Ready)); },
            user.Token, clock).AsTask();
        user.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(CommonConnectorConnectionStatus.Ready)]
    [InlineData(CommonConnectorConnectionStatus.Denied)]
    [InlineData(CommonConnectorConnectionStatus.Revoked)]
    [InlineData(CommonConnectorConnectionStatus.Failed)]
    public async Task TerminalState_IsNotRetried(CommonConnectorConnectionStatus status)
    {
        var calls = 0;
        var result = await WaitAsync(Snapshot(status),
            ct => { calls++; return ValueTask.FromResult(Snapshot(CommonConnectorConnectionStatus.Ready)); });
        Assert.Equal(status, result.Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ResumeError_IsNotHiddenByPolling()
    {
        var clock = new ManualTimeProvider();
        var pending = WaitAsync(Snapshot(CommonConnectorConnectionStatus.AccessPending),
            ct => ValueTask.FromException<CommonConnectorConnectionSnapshot>(new InvalidOperationException("denied")),
            timeProvider: clock).AsTask();
        clock.Advance(TimeSpan.FromSeconds(2));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Equal("denied", error.Message);
    }

    [Fact]
    public async Task CallerCancelledAfterResumeInstalledReady_ClosesAuthorizationWithIndependentToken()
    {
        var clock = new ManualTimeProvider();
        using var user = new CancellationTokenSource();
        var authorized = false;
        var cleanupCalls = 0;
        var pending = CommonConnectorProvisioningWaiter.WaitAsync(Snapshot(CommonConnectorConnectionStatus.AccessPending),
            ct =>
            {
                authorized = true;
                user.Cancel();
                return ValueTask.FromResult(Snapshot(CommonConnectorConnectionStatus.Ready));
            },
            ct =>
            {
                Assert.False(ct.IsCancellationRequested);
                authorized = false;
                cleanupCalls++;
                return ValueTask.CompletedTask;
            }, user.Token, clock).AsTask();
        clock.Advance(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(authorized);
        Assert.Equal(1, cleanupCalls);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_timers) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            List<ManualTimer> ready;
            lock (_timers)
            {
                _ticks += elapsed.Ticks;
                ready = _timers.Where(timer => timer.Due <= _ticks).ToList();
                foreach (var timer in ready) timer.Due = long.MaxValue;
            }
            foreach (var timer in ready) timer.Fire();
        }
        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public long Due = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot test timers are expected.");
                lock (owner._timers) Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                return true;
            }
            public void Fire() => callback(state);
            public void Dispose() { lock (owner._timers) owner._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
