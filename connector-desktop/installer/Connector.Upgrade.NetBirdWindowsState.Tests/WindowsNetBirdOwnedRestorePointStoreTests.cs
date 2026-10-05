using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;

namespace Connector.Upgrade.NetBirdWindowsState.Tests;

public sealed class WindowsNetBirdOwnedRestorePointStoreTests
{
    [Fact]
    public async Task Capture_rejects_foreign_without_touching_backend()
    {
        var backend = new FakeRestoreBackend(new FakeWindowsRestoreLease());
        var store = new WindowsNetBirdOwnedRestorePointStore(backend);
        var foreign = ExactOwnedInspection() with
        {
            Assessment = new NetBirdAssessment(NetBirdOwnership.Foreign, "foreign"),
        };

        await Assert.ThrowsAsync<NetBirdMachineInvariantException>(async () =>
            await store.CaptureAsync(foreign, CancellationToken.None));

        Assert.Equal(0, backend.CaptureCalls);
    }

    [Fact]
    public async Task Capture_returns_only_lease_bound_to_exact_owned_inspection()
    {
        var lease = new FakeWindowsRestoreLease();
        var backend = new FakeRestoreBackend(lease);
        var store = new WindowsNetBirdOwnedRestorePointStore(backend);

        var actual = await store.CaptureAsync(ExactOwnedInspection(), CancellationToken.None);

        Assert.Same(lease, actual);
        Assert.Equal(1, backend.CaptureCalls);
        Assert.False(lease.Disposed);
    }

    [Fact]
    public async Task Capture_disposes_backend_lease_with_wrong_operation_fence()
    {
        var lease = new FakeWindowsRestoreLease
        {
            OwnerOperationId = WindowsNetBirdStateTestSupport.NewOperationId,
        };
        var backend = new FakeRestoreBackend(lease);
        var store = new WindowsNetBirdOwnedRestorePointStore(backend);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.CaptureAsync(ExactOwnedInspection(), CancellationToken.None));

        Assert.True(lease.Disposed);
    }

    [Fact]
    public async Task Reacquire_rejects_invalid_handle_before_backend()
    {
        var lease = new FakeWindowsRestoreLease();
        var backend = new FakeRestoreBackend(lease);
        var store = new WindowsNetBirdOwnedRestorePointStore(backend);
        var invalid = lease.RestorePoint with { HandleId = "..\\foreign" };

        await Assert.ThrowsAsync<NetBirdMachineInvariantException>(async () =>
            await store.ReacquireAsync(invalid, CancellationToken.None));

        Assert.Equal(0, backend.ReacquireCalls);
    }

    [Fact]
    public async Task Reacquire_disposes_lease_when_manifest_receipt_differs()
    {
        var lease = new FakeWindowsRestoreLease();
        var backend = new FakeRestoreBackend(lease);
        var store = new WindowsNetBirdOwnedRestorePointStore(backend);
        var requested = lease.RestorePoint with
        {
            InstallerSha256 = new string('9', 64),
        };

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.ReacquireAsync(requested, CancellationToken.None));

        Assert.Equal(1, backend.ReacquireCalls);
        Assert.True(lease.Disposed);
    }

    private static NetBirdMachineInspection ExactOwnedInspection()
    {
        var marker = WindowsNetBirdStateTestSupport.Marker();
        return new NetBirdMachineInspection(
            true,
            new NetBirdAssessment(
                NetBirdOwnership.OwnedByConnector,
                marker.InstallationId,
                marker.OwnedState),
            marker.Package,
            marker.InstallerSha256,
            true,
            "owned-evidence",
            marker.OperationId);
    }
}
