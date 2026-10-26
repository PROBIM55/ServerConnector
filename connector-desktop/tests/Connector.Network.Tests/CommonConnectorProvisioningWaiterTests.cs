using System.Net;
using Connector.Access.Client;
using Connector.Network;

namespace Connector.Network.Tests;

public sealed class CommonConnectorProvisioningWaiterTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private static CommonConnectorConnectionSnapshot Snapshot(CommonConnectorConnectionStatus status, string code = "fixture") =>
        new(status, "device-1", 1, code);

    private static ValueTask<CommonConnectorConnectionSnapshot> WaitAsync(CommonConnectorConnectionSnapshot initial,
        Func<CancellationToken, ValueTask<CommonConnectorConnectionSnapshot>> resume,
        CancellationToken cancellationToken = default, TimeProvider? timeProvider = null,
        Func<CancellationToken, ValueTask>? confirmReady = null,
        Action<CommonConnectorProvisioningProgress>? progress = null,
        CommonConnectorProvisioningOptions? options = null,
        Func<CancellationToken, ValueTask>? closeAuthorization = null) =>
        CommonConnectorProvisioningWaiter.WaitAsync(_ => ValueTask.FromResult(initial), resume,
            closeAuthorization ?? (_ => ValueTask.CompletedTask), cancellationToken, timeProvider, confirmReady, progress, options);

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
        await clock.AdvanceArmedAsync(TimeSpan.FromSeconds(2));

        var result = await pending.WaitAsync(Deadline);

        Assert.Equal(CommonConnectorConnectionStatus.Ready, result.Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ServerBusyUnavailableAndNetworkErrors_KeepWaitingWithBoundedBackoffAndRetryAfter()
    {
        var clock = new ManualTimeProvider();
        var failures = new Queue<Exception>(
        [
            new ConnectorEnrollmentProtocolException("internal", 500),
            new ConnectorEnrollmentProtocolException("unavailable", 503, "service_unavailable", TimeSpan.FromSeconds(7)),
            new HttpRequestException("connection reset"),
            new ConnectorEnrollmentProtocolException("busy", 409, "netbird_provider_busy"),
            new TaskCanceledException("HttpClient timeout"),
        ]);
        var reports = new List<CommonConnectorProvisioningProgress>();
        var pending = WaitAsync(Snapshot(CommonConnectorConnectionStatus.AwaitingVpnAuthorization),
            _ => failures.TryDequeue(out var failure)
                ? ValueTask.FromException<CommonConnectorConnectionSnapshot>(failure)
                : ValueTask.FromResult(Snapshot(CommonConnectorConnectionStatus.Ready)),
            timeProvider: clock, progress: reports.Add).AsTask();

        var delays = new List<TimeSpan>();
        for (var poll = 0; poll < 6; poll++) delays.Add(await clock.AdvanceArmedAsync());
        var result = await pending.WaitAsync(Deadline);

        Assert.Equal(CommonConnectorConnectionStatus.Ready, result.Status);
        // Poll, then doubling backoff capped at 10 s in the foreground; Retry-After wins.
        Assert.Equal(new[] { 2, 4, 7, 10, 10, 10 }, delays.Select(delay => (int)delay.TotalSeconds));
        Assert.Equal(new string?[]
        {
            null,
            "ConnectorEnrollmentProtocolException/500",
            "ConnectorEnrollmentProtocolException/503/service_unavailable",
            "HttpRequestException/Unknown",
            "ConnectorEnrollmentProtocolException/409/netbird_provider_busy",
            "TaskCanceledException",
        }, reports.Select(report => report.TransientFailure));
        Assert.All(reports, report => Assert.Equal(CommonConnectorConnectionPhase.Network, report.Phase));
        Assert.DoesNotContain(reports, report => report.TransientFailure?.Contains("reset") == true);
    }

    [Fact]
    public async Task TransientFailureOfTheFirstRequest_ContinuesWithResumeAndNeverRepeatsStart()
    {
        var clock = new ManualTimeProvider();
        var starts = 0;
        var resumes = 0;
        var pending = CommonConnectorProvisioningWaiter.WaitAsync(
            _ => { starts++; return ValueTask.FromException<CommonConnectorConnectionSnapshot>(new ConnectorEnrollmentProtocolException("gateway", 502)); },
            _ => { resumes++; return ValueTask.FromResult(Snapshot(CommonConnectorConnectionStatus.Ready)); },
            _ => ValueTask.CompletedTask, timeProvider: clock).AsTask();
        await clock.AdvanceArmedAsync();

        var result = await pending.WaitAsync(Deadline);

        Assert.Equal(CommonConnectorConnectionStatus.Ready, result.Status);
        Assert.Equal(1, starts);
        Assert.Equal(1, resumes);
    }

    [Fact]
    public async Task RouteNotYetReachableAfterReady_KeepsWaitingInVerifyPhaseUntilConfirmed()
    {
        var clock = new ManualTimeProvider();
        var confirmations = 0;
        var reports = new List<CommonConnectorProvisioningProgress>();
        var pending = WaitAsync(Snapshot(CommonConnectorConnectionStatus.Ready, "ready"),
            _ => ValueTask.FromResult(Snapshot(CommonConnectorConnectionStatus.Ready, "ready")),
            timeProvider: clock,
            confirmReady: _ => ++confirmations == 1
                ? ValueTask.FromException(new NetworkGateException("service_offline", "health probe failed"))
                : ValueTask.CompletedTask,
            progress: reports.Add).AsTask();
        await clock.AdvanceArmedAsync();

        var result = await pending.WaitAsync(Deadline);

        Assert.Equal(CommonConnectorConnectionStatus.Ready, result.Status);
        Assert.Equal(2, confirmations);
        var report = Assert.Single(reports);
        Assert.Equal(CommonConnectorConnectionPhase.Verify, report.Phase);
        Assert.Equal("NetworkGateException/service_offline", report.TransientFailure);
    }

    [Fact]
    public async Task PastTheForegroundBudget_ContinuesInBackgroundAndReturnsReadyByItself()
    {
        var clock = new ManualTimeProvider();
        var options = new CommonConnectorProvisioningOptions { ForegroundBudget = TimeSpan.FromSeconds(6) };
        var polls = 0;
        var reports = new List<CommonConnectorProvisioningProgress>();
        var pending = WaitAsync(Snapshot(CommonConnectorConnectionStatus.AccessPending, "access_providers_initializing"),
            _ => ValueTask.FromResult(++polls switch
            {
                < 3 => Snapshot(CommonConnectorConnectionStatus.AccessPending, "access_providers_initializing"),
                < 5 => Snapshot(CommonConnectorConnectionStatus.AwaitingVpnAuthorization, "vpn_authorization_pending"),
                _ => Snapshot(CommonConnectorConnectionStatus.Ready, "ready"),
            }),
            timeProvider: clock, progress: reports.Add, options: options).AsTask();

        var delays = new List<TimeSpan>();
        for (var poll = 0; poll < 5; poll++) delays.Add(await clock.AdvanceArmedAsync());
        var result = await pending.WaitAsync(Deadline);

        Assert.Equal(CommonConnectorConnectionStatus.Ready, result.Status);
        Assert.Equal(new[] { false, false, false, true, true }, reports.Select(report => report.Background));
        Assert.Equal(new[] { 2, 2, 2, 15, 15 }, delays.Select(delay => (int)delay.TotalSeconds));
        Assert.Equal(new[]
        {
            CommonConnectorConnectionPhase.Access, CommonConnectorConnectionPhase.Access, CommonConnectorConnectionPhase.Access,
            CommonConnectorConnectionPhase.Network, CommonConnectorConnectionPhase.Network,
        }, reports.Select(report => report.Phase));
        Assert.Equal(TimeSpan.FromSeconds(6), reports[3].Elapsed);
    }

    [Fact]
    public async Task UserCancellationDuringBackgroundWait_ClosesAuthorizationOnce()
    {
        var clock = new ManualTimeProvider();
        using var user = new CancellationTokenSource();
        var closed = 0;
        var reports = new List<CommonConnectorProvisioningProgress>();
        var pending = WaitAsync(Snapshot(CommonConnectorConnectionStatus.AwaitingVpnAuthorization),
            _ => ValueTask.FromResult(Snapshot(CommonConnectorConnectionStatus.AwaitingVpnAuthorization)),
            user.Token, clock, progress: reports.Add,
            options: new CommonConnectorProvisioningOptions { ForegroundBudget = TimeSpan.FromSeconds(2) },
            closeAuthorization: ct => { Assert.False(ct.IsCancellationRequested); closed++; return ValueTask.CompletedTask; }).AsTask();
        await clock.AdvanceArmedAsync();
        await clock.WaitArmedAsync();
        Assert.True(reports[^1].Background);

        user.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
        Assert.Equal(1, closed);
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
    [InlineData(CommonConnectorConnectionStatus.Unknown)]
    [InlineData(CommonConnectorConnectionStatus.EnrollmentRequired)]
    public async Task TerminalState_IsNotRetried(CommonConnectorConnectionStatus status)
    {
        var calls = 0;
        var result = await WaitAsync(Snapshot(status),
            ct => { calls++; return ValueTask.FromResult(Snapshot(CommonConnectorConnectionStatus.Ready)); });
        Assert.Equal(status, result.Status);
        Assert.Equal(0, calls);
    }

    public static TheoryData<Exception> DefinitiveFailures => new()
    {
        new ConnectorEnrollmentProtocolException("revoked", (int)HttpStatusCode.Unauthorized, "device_unauthorized"),
        new ConnectorEnrollmentProtocolException("policy", (int)HttpStatusCode.Forbidden, "access_denied"),
        new ConnectorEnrollmentProtocolException("inactive", (int)HttpStatusCode.Forbidden, "device_not_active"),
        new ConnectorEnrollmentProtocolException("providers", (int)HttpStatusCode.Forbidden, "provider_configuration"),
        // A 503 that names missing configuration is not «try again later».
        new ConnectorEnrollmentProtocolException("vpn", (int)HttpStatusCode.ServiceUnavailable, "vpn_not_configured"),
        new NetworkGateException("netbird_up_failed_repeatedly", "up failed three times"),
        new ConnectorEnrollmentProtocolException("The VPN state is not current transport for this device."),
        new ConnectorClockSkewException(TimeSpan.FromMinutes(5)),
        new ConnectorEnrollmentStateException("Device enrollment is required."),
        new NetworkGateException("netbird_management_mismatch", "foreign management"),
        new HttpRequestException("pin", new System.Security.Authentication.AuthenticationException("pin mismatch")),
        new InvalidOperationException("denied"),
    };

    [Theory]
    [MemberData(nameof(DefinitiveFailures))]
    public async Task DefinitiveFailure_EndsTheWaitAndKeepsTheCoordinatorsState(Exception failure)
    {
        var clock = new ManualTimeProvider();
        var closed = 0;
        var pending = WaitAsync(Snapshot(CommonConnectorConnectionStatus.AccessPending),
            _ => ValueTask.FromException<CommonConnectorConnectionSnapshot>(failure),
            timeProvider: clock, closeAuthorization: _ => { closed++; return ValueTask.CompletedTask; }).AsTask();
        await clock.AdvanceArmedAsync();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(Deadline));

        Assert.Same(failure, error);
        Assert.Equal(0, closed); // An actionable state (sign-in, clock, denial) is not erased to «Не подключено».
    }

    [Fact]
    public async Task AmbiguousLegacyPendingAnswer_EndsAfterItsLimitWithAccessNotGranted()
    {
        var clock = new ManualTimeProvider();
        var closed = 0;
        var options = new CommonConnectorProvisioningOptions
        {
            ForegroundBudget = TimeSpan.FromSeconds(4), AmbiguousPendingLimit = TimeSpan.FromSeconds(20),
        };
        var pending = WaitAsync(Snapshot(CommonConnectorConnectionStatus.AccessPending, "access_profile_pending_or_denied"),
            _ => ValueTask.FromResult(Snapshot(CommonConnectorConnectionStatus.AccessPending, "access_profile_pending_or_denied")),
            timeProvider: clock, options: options, closeAuthorization: _ => { closed++; return ValueTask.CompletedTask; }).AsTask();
        // 2 + 2 s in the foreground, then 15 s background polls: at 34 s the 20 s limit has passed.
        for (var poll = 0; poll < 4; poll++) await clock.AdvanceArmedAsync();

        var error = await Assert.ThrowsAsync<CommonConnectorWaitEndedException>(() => pending.WaitAsync(Deadline));

        Assert.Equal("access_not_granted", error.Code);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task ExplicitAccessPending_KeepsWaitingPastTheAmbiguousLimit()
    {
        var clock = new ManualTimeProvider();
        var polls = 0;
        var options = new CommonConnectorProvisioningOptions { AmbiguousPendingLimit = TimeSpan.FromSeconds(3) };
        var pending = WaitAsync(Snapshot(CommonConnectorConnectionStatus.AccessPending, "access_profile_pending"),
            _ => ValueTask.FromResult(++polls < 4
                ? Snapshot(CommonConnectorConnectionStatus.AccessPending, "access_profile_pending")
                : Snapshot(CommonConnectorConnectionStatus.Ready, "ready")),
            timeProvider: clock, options: options).AsTask();
        for (var poll = 0; poll < 4; poll++) await clock.AdvanceArmedAsync();

        Assert.Equal(CommonConnectorConnectionStatus.Ready, (await pending.WaitAsync(Deadline)).Status);
    }

    [Fact]
    public async Task CallerCancelledAfterResumeInstalledReady_ClosesAuthorizationWithIndependentToken()
    {
        var clock = new ManualTimeProvider();
        using var user = new CancellationTokenSource();
        var authorized = false;
        var cleanupCalls = 0;
        var pending = WaitAsync(Snapshot(CommonConnectorConnectionStatus.AccessPending),
            ct =>
            {
                authorized = true;
                user.Cancel();
                return ValueTask.FromResult(Snapshot(CommonConnectorConnectionStatus.Ready));
            },
            user.Token, clock,
            closeAuthorization: ct =>
            {
                Assert.False(ct.IsCancellationRequested);
                authorized = false;
                cleanupCalls++;
                return ValueTask.CompletedTask;
            }).AsTask();
        await clock.AdvanceArmedAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
        Assert.False(authorized);
        Assert.Equal(1, cleanupCalls);
    }

    internal sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_timers) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>Waits until the code under test armed a timer and returns its delay.</summary>
        public async Task<TimeSpan> WaitArmedAsync()
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (true)
            {
                lock (_timers)
                {
                    var armed = _timers.Where(timer => timer.Due != long.MaxValue).Select(timer => timer.Due).DefaultIfEmpty(long.MaxValue).Min();
                    if (armed != long.MaxValue) return TimeSpan.FromTicks(armed - _ticks);
                }
                if (DateTime.UtcNow > deadline) throw new TimeoutException("No timer was armed.");
                await Task.Delay(5);
            }
        }

        /// <summary>Advances to the next armed timer (or by the given span) and returns the waited delay.</summary>
        public async Task<TimeSpan> AdvanceArmedAsync(TimeSpan? by = null)
        {
            var delay = await WaitArmedAsync();
            Advance(by ?? delay);
            return delay;
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
