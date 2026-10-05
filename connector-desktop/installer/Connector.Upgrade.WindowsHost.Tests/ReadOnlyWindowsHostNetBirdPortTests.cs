using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsHost;
using Xunit;

namespace Connector.Upgrade.WindowsHost.Tests;

public sealed class ReadOnlyWindowsHostNetBirdPortTests
{
    [Theory]
    [InlineData(NetBirdOwnership.Absent)]
    [InlineData(NetBirdOwnership.OwnedByConnector)]
    [InlineData(NetBirdOwnership.Foreign)]
    [InlineData(NetBirdOwnership.Unattributed)]
    public async Task Inspect_returns_only_the_validated_machine_ownership(NetBirdOwnership ownership)
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        CancellationToken observedToken = default;
        var port = new ReadOnlyWindowsHostNetBirdPort(token =>
        {
            calls++;
            observedToken = token;
            return ValueTask.FromResult(ownership);
        });

        var assessment = await port.InspectAsync(cancellation.Token);

        Assert.Equal(1, calls);
        Assert.Equal(cancellation.Token, observedToken);
        Assert.Equal(ownership, assessment.Ownership);
        Assert.Null(assessment.InstallationId);
        Assert.Null(assessment.OwnedState);
    }

    [Fact]
    public async Task Inspect_rejects_unknown_machine_ownership_values()
    {
        var port = new ReadOnlyWindowsHostNetBirdPort(_ =>
            ValueTask.FromResult((NetBirdOwnership)int.MaxValue));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await port.InspectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cancelled_inspection_does_not_call_the_machine_delegate()
    {
        var calls = 0;
        var port = new ReadOnlyWindowsHostNetBirdPort(_ =>
        {
            calls++;
            return ValueTask.FromResult(NetBirdOwnership.Absent);
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await port.InspectAsync(cancellation.Token));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Every_mutation_entry_point_fails_closed()
    {
        var calls = 0;
        var port = new ReadOnlyWindowsHostNetBirdPort(_ =>
        {
            calls++;
            return ValueTask.FromResult(NetBirdOwnership.Absent);
        });

        Assert.Throws<NotSupportedException>(() => port.PrepareAsync(null!, Guid.NewGuid(), CancellationToken.None));
        Assert.Throws<NotSupportedException>(() => port.ApplyAsync(null!, CancellationToken.None));
        Assert.Throws<NotSupportedException>(() => port.ReconcileAsync(null!, CancellationToken.None));
        Assert.Throws<NotSupportedException>(() => port.RemoveInstalledThisRunAsync(null!, CancellationToken.None));
        Assert.Throws<NotSupportedException>(() => port.RestoreUpdatedThisRunAsync(null!, CancellationToken.None));
        Assert.Equal(0, calls);
    }
}
