using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Connector.Access.AspNetCore;
using Connector.Access.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Connector.Access.Tests;

public sealed class ProviderDispatchSpeedTests
{
    [Fact]
    public void Retry_schedule_is_short_for_not_ready_and_capped_exponential_otherwise()
    {
        var options = new DeviceAccessOptions();

        var notReady = new[] { 0, 15, 60, 300, 1800, 7200 }
            .Select(seconds => ProviderRetryPolicy.NotReadyDelay(TimeSpan.FromSeconds(seconds)).TotalSeconds);
        var generic = Enumerable.Range(1, 9).Select(attempt => ProviderRetryPolicy.ExponentialDelay(options, attempt).TotalSeconds);

        Assert.Equal(new double[] { 2, 3, 5, 10, 30, 60 }, notReady);
        Assert.Equal(new double[] { 5, 10, 20, 40, 80, 160, 300, 300, 300 }, generic);
        Assert.Equal(TimeSpan.FromMinutes(5), ProviderRetryPolicy.ExponentialDelay(options, int.MaxValue));
    }

    [Fact]
    public async Task Not_ready_apply_is_retried_after_two_seconds_not_thirty()
    {
        var provider = new ScriptedProvider("vpn") { Failure = () => new DeviceAccessProviderNotReadyException("peer is connecting") };
        await using var fixture = await CreateFixtureAsync(provider);
        await EnrollAndRequestProfileAsync(fixture);

        var started = fixture.Clock.GetUtcNow();
        var first = await fixture.Outbox.ProcessOneAsync();
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1500));
        var early = await fixture.Outbox.ProcessOneAsync();
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(600));
        var second = await fixture.Outbox.ProcessOneAsync();

        Assert.False(first!.Succeeded);
        Assert.Equal("DeviceAccessProviderNotReadyException", first.Error);
        Assert.Equal(started.AddSeconds(2), first.RetryAtUtc);
        Assert.Null(early);
        Assert.Equal(first.CommandId, second!.CommandId);
        Assert.Equal(started + TimeSpan.FromMilliseconds(1500) + TimeSpan.FromMilliseconds(600) + TimeSpan.FromSeconds(2), second.RetryAtUtc);
        Assert.Equal(0, (await fixture.ReadOutboxAsync(second.CommandId)).AttemptCount);
    }

    [Fact]
    public async Task Not_ready_waits_never_park_the_apply()
    {
        var provider = new ScriptedProvider("vpn") { Failure = () => new DeviceAccessProviderNotReadyException("peer is connecting") };
        await using var fixture = await CreateFixtureAsync(provider, options => options.ProviderDeadLetterAttempts = 2);
        await EnrollAndRequestProfileAsync(fixture);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = await fixture.Outbox.ProcessOneAsync();
            Assert.NotNull(result);
            Assert.False(result!.DeadLettered);
            fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        }
    }

    [Fact]
    public async Task Parked_apply_is_revived_by_the_devices_vpn_reads()
    {
        var provider = new ScriptedProvider("vpn") { Failure = () => new InvalidOperationException("provider down") };
        await using var fixture = await CreateFixtureAsync(provider, options => options.ProviderDeadLetterAttempts = 1);
        var device = await EnrollAndRequestProfileAsync(fixture);
        var parked = await fixture.Outbox.ProcessOneAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));

        await fixture.Profiles.ResumePendingAccessAsync(device);
        provider.Failure = null;
        var revived = await fixture.Outbox.ProcessOneAsync();

        Assert.True(parked!.DeadLettered);
        Assert.True(revived!.Succeeded, revived.Error);
    }

    [Fact]
    public async Task Waiting_apply_is_rearmed_by_vpn_reads_at_most_once_per_interval()
    {
        var provider = new ScriptedProvider("vpn") { Failure = () => new DeviceAccessProviderNotReadyException("peer is connecting") };
        await using var fixture = await CreateFixtureAsync(provider);
        var device = await EnrollAndRequestProfileAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        var slow = await fixture.Outbox.ProcessOneAsync();
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(60), slow!.RetryAtUtc);
        fixture.Clock.Advance(ProviderRetryPolicy.WaitRearmGap);

        await fixture.Profiles.ResumePendingAccessAsync(device);
        var rearmed = await fixture.Outbox.ProcessOneAsync();
        await fixture.Profiles.ResumePendingAccessAsync(device);
        var throttled = await fixture.Outbox.ProcessOneAsync();

        Assert.Equal(slow.CommandId, rearmed!.CommandId);
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(60), rearmed.RetryAtUtc);
        Assert.Null(throttled);
    }

    [Fact]
    public async Task Row_counted_by_an_older_release_restarts_counting_instead_of_parking()
    {
        var provider = new ScriptedProvider("vpn") { Failure = () => new InvalidOperationException("provider down") };
        await using var fixture = await CreateFixtureAsync(provider);
        await EnrollAndRequestProfileAsync(fixture);
        await fixture.ExecuteSqlAsync("UPDATE connector_access_provider_outbox SET attempt_count = 4291, status = 'error', last_error = 'NetBirdPeerNotReadyException'");

        var result = await fixture.Outbox.ProcessOneAsync();

        Assert.False(result!.DeadLettered);
        Assert.Equal(1, (await fixture.ReadOutboxAsync(result.CommandId)).AttemptCount);
    }

    [Fact]
    public async Task Fence_busy_release_at_the_limit_stays_claimable()
    {
        var provider = new ScriptedProvider("vpn");
        await using var fixture = await CreateFixtureAsync(provider, options => options.ProviderDeadLetterAttempts = 3);
        await EnrollAndRequestProfileAsync(fixture);
        await fixture.ExecuteSqlAsync("UPDATE connector_access_provider_outbox SET attempt_count = 2");
        var now = fixture.Clock.GetUtcNow();
        var claimed = await fixture.ProviderOutbox.TryClaimNextAsync("lease-1", now, now.AddMinutes(2), default);
        await fixture.ExecuteSqlAsync(
            "UPDATE connector_access_provider_state SET dispatch_lease_id = 'other', dispatch_lease_until_utc = $until",
            ("$until", now.AddHours(1)));

        var busy = await fixture.Outbox.DispatchClaimedAsync(claimed!);
        var released = await fixture.ReadOutboxAsync(claimed!.CommandId);
        await fixture.ExecuteSqlAsync("UPDATE connector_access_provider_state SET dispatch_lease_id = NULL, dispatch_lease_until_utc = NULL");
        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        var retried = await fixture.Outbox.ProcessOneAsync();

        Assert.Equal("dispatch_busy", busy.Error);
        Assert.Equal(("pending", "dispatch_busy", 2L), released);
        Assert.True(retried!.Succeeded, retried.Error);
    }

    [Fact]
    public async Task Revoke_is_claimed_before_a_fresh_apply()
    {
        var provider = new ScriptedProvider("vpn");
        await using var fixture = await CreateFixtureAsync(provider);
        await EnrollAndRequestProfileAsync(fixture);
        var revokedDevice = await EnrollAsync(fixture);
        await fixture.Administration.RevokeAsync(new RevokeDeviceCommand("admin-1", "company-1", revokedDevice.DeviceId));

        var first = await fixture.Outbox.ProcessOneAsync();

        Assert.True(first!.Succeeded, first.Error);
        Assert.Equal(DeviceAccessProviderCommandKind.Revoke, Assert.Single(provider.Commands).Kind);
    }

    [Fact]
    public async Task Provider_http_timeout_is_a_retryable_failure_and_does_not_hold_the_lease()
    {
        var provider = new ScriptedProvider("vpn") { Failure = () => new TaskCanceledException("HttpClient.Timeout elapsed") };
        await using var fixture = await CreateFixtureAsync(provider);
        await EnrollAndRequestProfileAsync(fixture);

        var timedOut = await fixture.Outbox.ProcessOneAsync();
        var durable = await fixture.ReadOutboxAsync(timedOut!.CommandId);
        provider.Failure = null;
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        var retried = await fixture.Outbox.ProcessOneAsync();

        Assert.False(timedOut.Succeeded);
        Assert.Equal("provider_timeout", timedOut.Error);
        Assert.Equal("error", durable.Status);
        Assert.Equal("provider_timeout", durable.Error);
        Assert.True(retried!.Succeeded, retried.Error);
    }

    [Fact]
    public async Task Host_shutdown_releases_the_claim_immediately()
    {
        var provider = new ScriptedProvider("vpn") { BlockUntilCancelled = true };
        await using var fixture = await CreateFixtureAsync(provider);
        await EnrollAndRequestProfileAsync(fixture);
        using var stopping = new CancellationTokenSource();

        var dispatch = fixture.Outbox.ProcessOneAsync(stopping.Token).AsTask();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        provider.BlockUntilCancelled = false;
        var next = await fixture.Outbox.ProcessOneAsync();

        Assert.True(next!.Succeeded, next.Error);
        // The cancelled dispatch is not counted as a failure.
        Assert.Equal(1, (await fixture.ReadOutboxAsync(next.CommandId)).AttemptCount);
    }

    [Fact]
    public async Task Repeatedly_failing_apply_is_parked_and_revived_only_by_its_device()
    {
        var provider = new ScriptedProvider("vpn") { Failure = () => new InvalidOperationException("provider down") };
        await using var fixture = await CreateFixtureAsync(provider, options => options.ProviderDeadLetterAttempts = 3);
        var device = await EnrollAndRequestProfileAsync(fixture);

        ProviderDispatchResult? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            last = await fixture.Outbox.ProcessOneAsync();
            fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        }

        var parked = await fixture.Outbox.ProcessOneAsync();
        var durable = await fixture.ReadOutboxAsync(last!.CommandId);
        await fixture.Profiles.GetProfileAsync(device);
        var revived = await fixture.Outbox.ProcessOneAsync();

        Assert.True(last.DeadLettered);
        Assert.Null(last.RetryAtUtc);
        Assert.Null(parked);
        Assert.Equal("error", durable.Status);
        Assert.Equal("dead_letter:InvalidOperationException", durable.Error);
        Assert.Equal(3, durable.AttemptCount);
        Assert.Equal(last.CommandId, revived!.CommandId);
        Assert.Equal(1, (await fixture.ReadOutboxAsync(revived.CommandId)).AttemptCount);
    }

    [Fact]
    public async Task Parked_apply_is_not_revived_by_profile_requests_inside_the_revive_interval()
    {
        var provider = new ScriptedProvider("vpn") { Failure = () => new InvalidOperationException("provider down") };
        await using var fixture = await CreateFixtureAsync(provider, options => options.ProviderDeadLetterAttempts = 1);
        var device = await EnrollAndRequestProfileAsync(fixture);

        var parked = await fixture.Outbox.ProcessOneAsync();
        await fixture.Profiles.GetProfileAsync(device);
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        var stillParked = await fixture.Outbox.ProcessOneAsync();

        Assert.True(parked!.DeadLettered);
        Assert.Null(stillParked);
    }

    [Fact]
    public async Task Revoke_is_never_parked_even_after_the_attempt_limit()
    {
        var provider = new ScriptedProvider("vpn") { Failure = () => new InvalidOperationException("provider down") };
        await using var fixture = await CreateFixtureAsync(provider, options => options.ProviderDeadLetterAttempts = 2);
        var enrolled = await EnrollAsync(fixture);
        var revoked = await fixture.Administration.RevokeAsync(new RevokeDeviceCommand("admin-1", "company-1", enrolled.DeviceId));
        Assert.True(revoked.IsSuccess);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = await fixture.Outbox.ProcessOneAsync();
            Assert.NotNull(result);
            Assert.False(result!.DeadLettered);
            fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        }

        provider.Failure = null;
        var completed = await fixture.Outbox.ProcessOneAsync();
        Assert.True(completed!.Succeeded, completed.Error);
        Assert.Equal(DeviceAccessProviderCommandKind.Revoke, Assert.Single(provider.Commands).Kind);
    }

    [Fact]
    public async Task Fresh_command_is_claimed_before_an_older_failing_command()
    {
        var provider = new ScriptedProvider("vpn") { Failure = () => new InvalidOperationException("provider down") };
        await using var fixture = await CreateFixtureAsync(provider);
        await EnrollAndRequestProfileAsync(fixture);
        var failing = await fixture.Outbox.ProcessOneAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        await EnrollAndRequestProfileAsync(fixture);
        provider.Failure = null;
        var next = await fixture.Outbox.ProcessOneAsync();
        var after = await fixture.Outbox.ProcessOneAsync();

        Assert.NotEqual(failing!.CommandId, next!.CommandId);
        Assert.True(next.Succeeded, next.Error);
        Assert.Equal(failing.CommandId, after!.CommandId);
    }

    [Fact]
    public async Task Provider_lane_claims_only_its_own_provider_commands()
    {
        var alpha = new ScriptedProvider("alpha");
        var beta = new ScriptedProvider("beta");
        await using var fixture = await CreateFixtureAsync([alpha, beta]);
        await EnrollAndRequestProfileAsync(fixture);

        var betaFirst = await fixture.Outbox.ProcessNextForProviderAsync("beta");
        var betaDrained = await fixture.Outbox.ProcessNextForProviderAsync("beta");
        var alphaNext = await fixture.Outbox.ProcessNextForProviderAsync("alpha");

        Assert.Equal("beta", betaFirst!.ProviderName);
        Assert.Null(betaDrained);
        Assert.Equal("alpha", alphaNext!.ProviderName);
        Assert.Empty(alpha.Commands.Where(command => command.ProviderName == "beta"));
    }

    [Fact]
    public async Task Enqueue_signals_the_provider_lane_only_when_a_command_is_written()
    {
        var provider = new ScriptedProvider("vpn");
        await using var fixture = await CreateFixtureAsync(provider);
        var signal = fixture.Services.GetRequiredService<ConnectorAccessDispatchSignal>();
        var device = await EnrollAndRequestProfileAsync(fixture);

        var woken = await signal.WaitAsync("vpn", TimeSpan.Zero, TimeProvider.System, default);
        await fixture.Profiles.GetProfileAsync(device);
        var unchanged = await signal.WaitAsync("vpn", TimeSpan.Zero, TimeProvider.System, default);

        Assert.True(woken);
        Assert.False(unchanged);
    }

    [Fact]
    public async Task Dispatcher_wakes_on_enqueue_and_a_blocked_provider_does_not_delay_another()
    {
        var slow = new ScriptedProvider("slow") { BlockUntilCancelled = true };
        var fast = new ScriptedProvider("fast");
        await using var fixture = await CreateFixtureAsync(
            [slow, fast],
            options => options.ProviderDispatchIdlePoll = TimeSpan.FromMinutes(1),
            services =>
            {
                services.AddLogging();
                services.AddConnectorAccessProviderDispatch();
            });
        var worker = Assert.Single(fixture.Services.GetServices<IHostedService>());
        await worker.StartAsync(default);
        try
        {
            // Let the lanes reach their idle wait before anything is enqueued.
            await Task.Delay(200);
            await EnrollAndRequestProfileAsync(fixture);

            // Signal-driven: the idle poll is one minute, so finishing inside
            // the generous wait below can only come from the wake-up signal.
            await fast.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.False(slow.Completed.Task.IsCompleted);
        }
        finally
        {
            await worker.StopAsync(default);
        }
    }

    [Fact]
    public async Task Stored_commands_of_a_provider_removed_from_required_are_still_dispatched()
    {
        var vpn = new ScriptedProvider("vpn");
        var smb = new ScriptedProvider("smb");
        await using var fixture = await CreateFixtureAsync(
            [vpn, smb],
            options => options.ProviderDispatchIdlePoll = TimeSpan.FromMinutes(1),
            services =>
            {
                services.AddLogging();
                services.AddConnectorAccessProviderDispatch();
            });
        await EnrollAndRequestProfileAsync(fixture);
        // The operator drops smb from the required list after its command was stored.
        Assert.True(fixture.Services.GetRequiredService<IOptions<DeviceAccessOptions>>().Value.RequiredAccessProviders.Remove("smb"));
        var worker = Assert.Single(fixture.Services.GetServices<IHostedService>());
        await worker.StartAsync(default);
        try
        {
            await smb.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await vpn.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await worker.StopAsync(default);
        }
    }

    [Fact]
    public async Task Waiting_apply_does_not_reset_its_pause_and_is_parked_after_the_ceiling()
    {
        var provider = new ScriptedProvider("vpn") { Failure = () => new DeviceAccessProviderNotReadyException("peer is connecting") };
        await using var fixture = await CreateFixtureAsync(provider);
        var device = await EnrollAndRequestProfileAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        var first = await fixture.Outbox.ProcessOneAsync();
        fixture.Clock.Advance(ProviderRetryPolicy.WaitRearmGap);

        await fixture.Profiles.ResumePendingAccessAsync(device);
        var rearmed = await fixture.Outbox.ProcessOneAsync();
        await fixture.Profiles.ResumePendingAccessAsync(device);
        var tooSoon = await fixture.Outbox.ProcessOneAsync();

        Assert.Equal(first!.CommandId, rearmed!.CommandId);
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(60), rearmed.RetryAtUtc);
        Assert.False(rearmed.DeadLettered);
        Assert.Null(tooSoon);

        fixture.Clock.Advance(ProviderRetryPolicy.WaitCeiling);
        var parked = await fixture.Outbox.ProcessOneAsync();
        Assert.True(parked!.DeadLettered);
    }

    [Fact]
    public void Vpn_state_cache_reuses_only_a_fresh_ready_state_of_the_same_identity_and_revision()
    {
        var cache = new ConnectorVpnStateCache(TimeSpan.FromSeconds(15));
        var identity = new AuthenticatedDevice("device-1", "user-1", "company-1", new string('a', 64), 7);
        var now = DateTimeOffset.UtcNow;
        var maximumAge = TimeSpan.FromSeconds(120);
        var ready = new ConnectorVpnTransportState("device-1", 7, "ready", "peer-1", "https://vpn.test", ["100.64.0.2"], now);

        cache.Store(identity, 7, ready with { Status = "connecting" }, now, maximumAge);
        Assert.Null(cache.TryGet(identity, 7, now, maximumAge));

        cache.Store(identity, 7, ready, now, maximumAge);
        Assert.Same(ready, cache.TryGet(identity, 7, now.AddSeconds(14), maximumAge)!.Value);
        Assert.Null(cache.TryGet(identity, 8, now.AddSeconds(1), maximumAge));

        cache.Store(identity, 7, ready, now, maximumAge);
        Assert.Null(cache.TryGet(identity with { CertificateSha256 = new string('b', 64) }, 7, now, maximumAge));

        cache.Store(identity, 7, ready, now, maximumAge);
        Assert.Null(cache.TryGet(identity, 7, now.AddSeconds(15), maximumAge));

        // Never longer than half of the accepted observation age.
        cache.Store(identity, 7, ready, now, TimeSpan.FromSeconds(10));
        Assert.Null(cache.TryGet(identity, 7, now.AddSeconds(5), TimeSpan.FromSeconds(10)));

        cache.Store(identity, 7, ready, now, maximumAge);
        cache.Invalidate("device-1");
        Assert.Null(cache.TryGet(identity, 7, now, maximumAge));
    }

    [Fact]
    public void Vpn_state_cache_is_bounded()
    {
        var cache = new ConnectorVpnStateCache(TimeSpan.FromSeconds(15), maximumEntries: 3);
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 10; index++)
        {
            var identity = new AuthenticatedDevice($"device-{index}", "user-1", "company-1", new string('a', 64), 7);
            cache.Store(identity, 7,
                new ConnectorVpnTransportState(identity.DeviceId, 7, "ready", "peer", "https://vpn.test", ["100.64.0.2"], now),
                now, TimeSpan.FromSeconds(120));
        }

        Assert.InRange(cache.Count, 1, 3);
    }

    private static Task<AccessFixture> CreateFixtureAsync(
        ScriptedProvider provider,
        Action<DeviceAccessOptions>? configure = null) =>
        CreateFixtureAsync([provider], configure);

    private static async Task<AccessFixture> CreateFixtureAsync(
        ScriptedProvider[] providers,
        Action<DeviceAccessOptions>? configure = null,
        Action<IServiceCollection>? services = null) =>
        await AccessFixture.CreateAsync(
            providers,
            options =>
            {
                options.RequiredAccessProviders.Clear();
                foreach (var provider in providers) options.RequiredAccessProviders.Add(provider.ProviderName);
                configure?.Invoke(options);
            },
            services);

    private static async Task<DeviceEnrollmentResponse> EnrollAsync(AccessFixture fixture)
    {
        var issue = await fixture.IssueAsync();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var enrolled = await fixture.EnrollAsync(issue.Token, request.CreateSigningRequestPem());
        Assert.True(enrolled.IsSuccess, enrolled.Failure?.Code);
        return enrolled.Value!;
    }

    private static async Task<AuthenticatedDevice> EnrollAndRequestProfileAsync(AccessFixture fixture)
    {
        var enrolled = await EnrollAsync(fixture);
        using var certificate = X509Certificate2.CreateFromPem(enrolled.ClientCertificatePem);
        var authentication = await fixture.Authenticator.AuthenticateAsync(certificate);
        Assert.True(authentication.IsSuccess, authentication.Failure?.Code);
        var profile = await fixture.Profiles.GetProfileAsync(authentication.Value!);
        Assert.Equal("access_pending", profile.Failure!.Code);
        return authentication.Value!;
    }

    private static IServiceProvider CreateHttpServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureHttpJsonOptions(_ => { });
        return services.BuildServiceProvider();
    }

    private sealed class ScriptedProvider(string providerName) : IDeviceAccessGrantProvider
    {
        public string ProviderName { get; } = providerName;
        public Func<Exception>? Failure { get; set; }
        public bool BlockUntilCancelled { get; set; }
        public ConcurrentQueue<DeviceAccessProviderCommand> Commands { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<DeviceAccessProviderReceipt> ApplyAsync(DeviceAccessProviderCommand command, CancellationToken cancellationToken) =>
            ExecuteAsync(command, cancellationToken);

        public ValueTask<DeviceAccessProviderReceipt> RevokeAsync(DeviceAccessProviderCommand command, CancellationToken cancellationToken) =>
            ExecuteAsync(command, cancellationToken);

        private async ValueTask<DeviceAccessProviderReceipt> ExecuteAsync(DeviceAccessProviderCommand command, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            if (BlockUntilCancelled)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (Failure is { } failure)
            {
                throw failure();
            }

            Commands.Enqueue(command);
            Completed.TrySetResult();
            return new DeviceAccessProviderReceipt(command.CommandId, command.DesiredRevision, command.Kind);
        }
    }
}
