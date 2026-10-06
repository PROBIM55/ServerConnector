using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsHost;
using Connector.Upgrade.WindowsPayload;
using Microsoft.Win32.SafeHandles;

namespace Connector.Upgrade.WindowsHost.Tests;

public sealed class WindowsOriginalUserRollbackPayloadStoreTests
{
    private static readonly Guid OperationId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task Acquire_ReopensOnlyOperationHandleAndEmbeddedPin()
    {
        var pin = LegacyUpgradeLock.LoadEmbedded().Get(LegacyApplicationKind.StructuraConnector);
        var opener = new RecordingOpener(pin);
        var store = new WindowsOriginalUserRollbackPayloadStore(OperationId, opener);

        await using var lease = await store.AcquireVerifiedRollbackPayloadAsync(Payload(pin));

        Assert.Equal($"windows-msi-op-v1:{OperationId:N}:1", lease.HandleId);
        Assert.Equal(1, opener.Calls);
        Assert.Equal(new ProtectedRollbackPayloadReceipt(lease.HandleId, pin.Kind,
            RollbackPayloadProtection.ProtectedMachineStaging), opener.LastReceipt);
    }

    [Fact]
    public async Task Acquire_RejectsChangedPayloadBeforeOpeningMachineSlot()
    {
        var pin = LegacyUpgradeLock.LoadEmbedded().Get(LegacyApplicationKind.PlatformConnector);
        var opener = new RecordingOpener(pin);
        var store = new WindowsOriginalUserRollbackPayloadStore(OperationId, opener);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.AcquireVerifiedRollbackPayloadAsync(
            Payload(pin) with { SizeBytes = pin.SizeBytes + 1 }).AsTask());
        Assert.Equal(0, opener.Calls);
    }

    [Fact]
    public async Task Reacquire_RejectsForeignHandleBeforeOpeningMachineSlot()
    {
        var pin = LegacyUpgradeLock.LoadEmbedded().Get(LegacyApplicationKind.StructuraConnector);
        var opener = new RecordingOpener(pin);
        var store = new WindowsOriginalUserRollbackPayloadStore(OperationId, opener);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReacquireVerifiedRollbackPayloadAsync(
            new ProtectedRollbackPayloadReceipt($"windows-msi-op-v1:{Guid.NewGuid():N}:1", pin.Kind,
                RollbackPayloadProtection.ProtectedMachineStaging)).AsTask());
        Assert.Equal(0, opener.Calls);
    }

    [Fact]
    public async Task Acquire_DisposesMismatchedOpenedLease()
    {
        var pin = LegacyUpgradeLock.LoadEmbedded().Get(LegacyApplicationKind.StructuraConnector);
        var opener = new RecordingOpener(pin) { WrongHandle = true };
        var store = new WindowsOriginalUserRollbackPayloadStore(OperationId, opener);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.AcquireVerifiedRollbackPayloadAsync(
            Payload(pin)).AsTask());
        Assert.True(opener.LastLease!.Disposed);
    }

    private static LegacyRollbackPayload Payload(LegacyUpgradePin pin) => new(
        pin.Identity, pin.PackageId, pin.InstallerName, pin.Version, pin.SizeBytes, pin.Sha256, true);

    private sealed class RecordingOpener(LegacyUpgradePin pin) : IWindowsOperationRollbackPayloadOpener
    {
        public int Calls { get; private set; }
        public bool WrongHandle { get; init; }
        public ProtectedRollbackPayloadReceipt? LastReceipt { get; private set; }
        public FakeLease? LastLease { get; private set; }

        public ValueTask<IWindowsVerifiedRollbackPayloadLease> ReopenVerifiedRollbackPayloadAsync(
            ProtectedRollbackPayloadReceipt receipt, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastReceipt = receipt;
            LastLease = new FakeLease(
                WrongHandle ? "wrong" : receipt.HandleId,
                new RollbackPayloadInspection(pin.Identity, pin.PackageId, pin.InstallerName,
                    pin.Version, pin.SizeBytes, pin.Sha256, true));
            return ValueTask.FromResult<IWindowsVerifiedRollbackPayloadLease>(LastLease);
        }
    }

    private sealed class FakeLease(string handleId, RollbackPayloadInspection inspection)
        : IWindowsVerifiedRollbackPayloadLease
    {
        public string HandleId { get; } = handleId;
        public RollbackPayloadProtection Protection => RollbackPayloadProtection.ProtectedMachineStaging;
        public RollbackPayloadInspection Inspection { get; } = inspection;
        public string StagedPath => "unused-in-contract-test";
        public SafeFileHandle ContentHandle { get; } = new(IntPtr.Zero, ownsHandle: false);
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            ContentHandle.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
