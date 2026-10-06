using Connector.Desktop.Services;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class DesktopBackgroundAgentTests
{
    [Fact]
    public async Task Scheduler_UsesIndependentIntervals()
    {
        var clock = new ManualClock();
        var firstHeartbeat = NewSignal();
        var secondHeartbeat = NewSignal();
        var thirdHeartbeat = NewSignal();
        var firstUpdate = NewSignal();
        var firstTeklaSync = NewSignal();
        var heartbeatCount = 0;
        var updateCount = 0;
        var teklaSyncCount = 0;

        await using var agent = CreateAgent(
            clock,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(3),
            () => true,
            _ =>
            {
                var count = Interlocked.Increment(ref heartbeatCount);
                if (count == 1) firstHeartbeat.TrySetResult();
                if (count == 2) secondHeartbeat.TrySetResult();
                if (count == 3) thirdHeartbeat.TrySetResult();
                return Task.CompletedTask;
            },
            _ =>
            {
                Interlocked.Increment(ref updateCount);
                firstUpdate.TrySetResult();
                return Task.CompletedTask;
            },
            _ =>
            {
                Interlocked.Increment(ref teklaSyncCount);
                firstTeklaSync.TrySetResult();
                return Task.CompletedTask;
            });

        agent.Start(heartbeatEnabled: true);

        clock.Advance(TimeSpan.FromSeconds(1));
        await firstHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((1, 0, 0), (heartbeatCount, updateCount, teklaSyncCount));

        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(secondHeartbeat.Task, firstUpdate.Task).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((2, 1, 0), (heartbeatCount, updateCount, teklaSyncCount));

        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(thirdHeartbeat.Task, firstTeklaSync.Task).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((3, 1, 1), (heartbeatCount, updateCount, teklaSyncCount));
    }

    [Fact]
    public async Task Scheduler_DoesNotOverlapSlowTicks_AndStartIsIdempotent()
    {
        var clock = new ManualClock();
        var firstEntered = NewSignal();
        var updateEntered = NewSignal();
        var releaseFirst = NewSignal();
        var calls = 0;
        var active = 0;
        var maxActive = 0;

        await using var agent = CreateAgent(
            clock,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromDays(1),
            () => true,
            async cancellationToken =>
            {
                var call = Interlocked.Increment(ref calls);
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maxActive, current);
                try
                {
                    if (call == 1)
                    {
                        firstEntered.TrySetResult();
                        await releaseFirst.Task.WaitAsync(cancellationToken);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            _ =>
            {
                updateEntered.TrySetResult();
                return Task.CompletedTask;
            });

        agent.Start(heartbeatEnabled: true);
        agent.Start(heartbeatEnabled: true);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(firstEntered.Task, updateEntered.Task).WaitAsync(TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(1, Volatile.Read(ref maxActive));

        releaseFirst.TrySetResult();
        await agent.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CommonMode_SuppressesLegacyHeartbeatAndTekla_ThenResumesThem()
    {
        var clock = new ManualClock();
        var legacyMode = false;
        var firstUpdate = NewSignal();
        var legacyHeartbeat = NewSignal();
        var legacyTeklaSync = NewSignal();
        var heartbeatCount = 0;
        var updateCount = 0;
        var teklaSyncCount = 0;

        await using var agent = CreateAgent(
            clock,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            () => legacyMode,
            _ =>
            {
                Interlocked.Increment(ref heartbeatCount);
                legacyHeartbeat.TrySetResult();
                return Task.CompletedTask;
            },
            _ =>
            {
                Interlocked.Increment(ref updateCount);
                firstUpdate.TrySetResult();
                return Task.CompletedTask;
            },
            _ =>
            {
                Interlocked.Increment(ref teklaSyncCount);
                legacyTeklaSync.TrySetResult();
                return Task.CompletedTask;
            });

        agent.Start(heartbeatEnabled: true);
        clock.Advance(TimeSpan.FromSeconds(1));
        await firstUpdate.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // The callback signal can arrive before the scheduler finishes this
        // tick and registers its next delay. Keep manual time and mode fixed
        // until that delay exists so the next tick cannot be lost.
        await clock.WaitForScheduledDelayAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((0, 1, 0), (heartbeatCount, updateCount, teklaSyncCount));

        legacyMode = true;
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(legacyHeartbeat.Task, legacyTeklaSync.Task).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((1, 2, 1), (heartbeatCount, updateCount, teklaSyncCount));
    }

    [Fact]
    public async Task StopAsync_CancelsActiveTick_AndStopsFutureTicks()
    {
        var clock = new ManualClock();
        var entered = NewSignal();
        var cancelled = NewSignal();
        var calls = 0;

        await using var agent = CreateAgent(
            clock,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromDays(1),
            TimeSpan.FromDays(1),
            () => true,
            async cancellationToken =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            });

        agent.Start(heartbeatEnabled: true);
        clock.Advance(TimeSpan.FromSeconds(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await agent.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(agent.IsRunning);

        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task BoundedStop_IsolatesNonCooperativeCallback_UntilSafeDisposeBoundary()
    {
        var clock = new ManualClock();
        var entered = NewSignal();
        var release = NewSignal();
        var exited = NewSignal();
        var calls = 0;
        var resourcesDisposed = 0;
        var callbackObservedDisposedResources = 0;
        var agent = CreateAgent(
            clock,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromDays(1),
            TimeSpan.FromDays(1),
            () => true,
            async _ =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task; // Deliberately ignores scheduler cancellation.
                callbackObservedDisposedResources = Volatile.Read(ref resourcesDisposed);
                exited.TrySetResult();
            });

        try
        {
            agent.Start(heartbeatEnabled: true);
            clock.Advance(TimeSpan.FromSeconds(1));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var boundedStop = agent.StopAsync(TimeSpan.FromSeconds(3));
            clock.Advance(TimeSpan.FromSeconds(3));
            var result = await boundedStop.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(result.IsDrained);
            Assert.False(result.Completion.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref resourcesDisposed));

            clock.Advance(TimeSpan.FromDays(2));
            Assert.Equal(1, Volatile.Read(ref calls));

            release.TrySetResult();
            await result.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Interlocked.Exchange(ref resourcesDisposed, 1);

            Assert.Equal(0, Volatile.Read(ref callbackObservedDisposedResources));
        }
        finally
        {
            release.TrySetResult();
            await agent.DisposeAsync();
        }
    }

    private static DesktopBackgroundAgent CreateAgent(
        IDesktopBackgroundClock clock,
        TimeSpan heartbeatInterval,
        TimeSpan updateInterval,
        TimeSpan teklaInterval,
        Func<bool> isLegacyMode,
        Func<CancellationToken, Task> heartbeat,
        Func<CancellationToken, Task>? update = null,
        Func<CancellationToken, Task>? tekla = null) =>
        new(
            heartbeatInterval,
            updateInterval,
            teklaInterval,
            isLegacyMode,
            heartbeat,
            update ?? (_ => Task.CompletedTask),
            tekla ?? (_ => Task.CompletedTask),
            onError: null,
            clock);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current || Interlocked.CompareExchange(ref target, candidate, current) == current) return;
        }
    }

    private sealed class ManualClock : IDesktopBackgroundClock
    {
        private readonly object _gate = new();
        private readonly List<Waiter> _waiters = new();
        private TaskCompletionSource _delayRegistered = NewSignal();
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (_gate) return _utcNow;
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (delay <= TimeSpan.Zero) return Task.CompletedTask;

            var waiter = new Waiter(NewSignal());
            lock (_gate)
            {
                waiter.DueUtc = _utcNow + delay;
                _waiters.Add(waiter);
                _delayRegistered.TrySetResult();
            }

            _ = cancellationToken.Register(() => Cancel(waiter, cancellationToken));
            return waiter.Signal.Task;
        }

        public Task WaitForScheduledDelayAsync()
        {
            lock (_gate)
                return _waiters.Count > 0 ? Task.CompletedTask : _delayRegistered.Task;
        }

        public void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));

            List<Waiter> ready;
            lock (_gate)
            {
                _utcNow += elapsed;
                ready = _waiters.Where(item => item.DueUtc <= _utcNow).ToList();
                foreach (var waiter in ready) _waiters.Remove(waiter);
                if (_waiters.Count == 0) _delayRegistered = NewSignal();
            }

            foreach (var waiter in ready) waiter.Signal.TrySetResult();
        }

        private void Cancel(Waiter waiter, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_waiters.Remove(waiter) && _waiters.Count == 0)
                    _delayRegistered = NewSignal();
            }
            waiter.Signal.TrySetCanceled(cancellationToken);
        }

        private sealed class Waiter(TaskCompletionSource signal)
        {
            public TaskCompletionSource Signal { get; } = signal;
            public DateTimeOffset DueUtc { get; set; }
        }
    }
}
