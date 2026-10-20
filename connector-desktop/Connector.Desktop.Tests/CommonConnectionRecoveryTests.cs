using Connector.Desktop.Services;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class CommonConnectionRecoveryTests
{
    private static readonly CommonRecoveryIdentity Identity = new("device-a", "user-a", "company-a");

    [Theory]
    [InlineData("vpn_revoked")]
    [InlineData("vpn_denied")]
    [InlineData("vpn_revision_regressed")]
    [InlineData("overlay_identity_mismatch")]
    [InlineData("overlay_trust_unavailable")]
    [InlineData("issued_credential_rejected")]
    [InlineData("unrecognized")]
    public async Task Terminal_or_unknown_reasons_never_resume(string code)
    {
        await using var recovery = new CommonConnectionRecovery(delay: (_, _) => Task.CompletedTask);
        recovery.MarkReady(recovery.BeginConnect(), Identity);
        var calls = 0;
        recovery.Request(code, Identity.DeviceId, _ => { calls++; return Task.FromResult(new CommonRecoveryResult(true, "ready")); });
        await recovery.Completion;
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Retry_requires_operational_ready_and_the_same_device()
    {
        await using var recovery = new CommonConnectionRecovery(delay: (_, _) => Task.CompletedTask);
        var epoch = recovery.BeginConnect();
        var calls = 0;
        Task<CommonRecoveryResult> Resume(CommonRecoveryAttempt _) { calls++; return Task.FromResult(new CommonRecoveryResult(true, "ready")); }
        recovery.Request("overlay_not_ready", Identity.DeviceId, Resume);
        await recovery.Completion;
        recovery.MarkReady(epoch, Identity);
        recovery.Request("overlay_not_ready", "foreign-device", Resume);
        await recovery.Completion;
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Recovering_connection_is_single_owned_and_bounded()
    {
        var delays = new List<TimeSpan>();
        await using var recovery = new CommonConnectionRecovery(delay: (duration, _) => { delays.Add(duration); return Task.CompletedTask; });
        recovery.MarkReady(recovery.BeginConnect(), Identity);
        var calls = 0;
        recovery.Request("overlay_not_ready", Identity.DeviceId, attempt =>
        {
            Assert.Equal(Identity, attempt.Identity);
            calls++;
            return Task.FromResult(new CommonRecoveryResult(false, "overlay_not_ready"));
        });
        await recovery.Completion;
        Assert.Equal(5, calls);
        Assert.Equal(new[] { 2d, 5d, 10d, 20d, 30d }, delays.Select(delay => delay.TotalSeconds));
        Assert.Equal("", recovery.Progress);
    }

    [Fact]
    public async Task Stop_cancels_pending_delay_and_old_success_cannot_restore_intent()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var recovery = new CommonConnectionRecovery(delay: async (_, token) =>
        {
            waiting.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var epoch = recovery.BeginConnect();
        recovery.MarkReady(epoch, Identity);
        var calls = 0;
        recovery.Request("overlay_not_ready", Identity.DeviceId, _ => { calls++; return Task.FromResult(new CommonRecoveryResult(true, "ready")); });
        await waiting.Task;
        recovery.Stop();
        recovery.MarkReady(epoch, Identity);
        await recovery.Completion;
        Assert.False(recovery.IsCurrent(epoch));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Logout_during_non_cancellable_start_cannot_complete_activation()
    {
        await using var recovery = new CommonConnectionRecovery();
        var epoch = recovery.BeginConnect();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = recovery.StartOwnedAsync(epoch, async () => { entered.TrySetResult(); await release.Task; }, CancellationToken.None);
        await entered.Task;
        recovery.Stop();
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.False(recovery.IsCurrent(epoch));
    }

    [Fact]
    public async Task New_manual_intent_supersedes_an_inflight_recovery()
    {
        await using var recovery = new CommonConnectionRecovery(delay: (_, _) => Task.CompletedTask);
        var oldEpoch = recovery.BeginConnect();
        recovery.MarkReady(oldEpoch, Identity);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recovery.Request("vpn_revision_advanced", Identity.DeviceId, async _ =>
        {
            entered.TrySetResult(); await release.Task;
            recovery.MarkReady(oldEpoch, Identity);
            return new(true, "ready");
        });
        var oldCompletion = recovery.Completion;
        await entered.Task;
        var newEpoch = recovery.BeginConnect();
        release.TrySetResult();
        await oldCompletion;
        Assert.False(recovery.IsCurrent(oldEpoch));
        Assert.True(recovery.IsCurrent(newEpoch));
        var calls = 0;
        recovery.Request("overlay_not_ready", Identity.DeviceId, _ => { calls++; return Task.FromResult(new CommonRecoveryResult(true, "ready")); });
        await recovery.Completion;
        Assert.Equal(0, calls); // New intent has not reached its own operational Ready.
    }
}
