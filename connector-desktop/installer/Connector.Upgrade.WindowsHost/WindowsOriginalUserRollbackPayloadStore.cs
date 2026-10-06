using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsPayload;

namespace Connector.Upgrade.WindowsHost;

/// <summary>
/// Original-user view of rollback MSI content staged by the authenticated machine helper.
/// This port cannot create a machine slot or copy an MSI from a user-writable path.
/// </summary>
public sealed class WindowsOriginalUserRollbackPayloadStore : IWindowsRollbackPayloadStore
{
    private readonly Guid _operationId;
    private readonly IWindowsOperationRollbackPayloadOpener _opener;
    private readonly LegacyUpgradeLock _upgradeLock;

    public WindowsOriginalUserRollbackPayloadStore(
        Guid operationId,
        IWindowsOperationRollbackPayloadOpener opener,
        LegacyUpgradeLock? upgradeLock = null)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A trusted operation ID is required.", nameof(operationId));
        _operationId = operationId;
        _opener = opener ?? throw new ArgumentNullException(nameof(opener));
        _upgradeLock = upgradeLock ?? LegacyUpgradeLock.LoadEmbedded();
    }

    public async ValueTask<IVerifiedRollbackPayloadLease> AcquireVerifiedRollbackPayloadAsync(
        LegacyRollbackPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var pin = _upgradeLock.Get(payload.Identity.Kind);
        if (!payload.Verified || payload.Identity != pin.Identity || payload.PackageId != pin.PackageId ||
            payload.InstallerName != pin.InstallerName || payload.Version != pin.Version ||
            payload.SizeBytes != pin.SizeBytes || !string.Equals(payload.Sha256, pin.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The requested rollback MSI differs from the embedded lock.");

        var receipt = ReceiptFor(pin.Kind);
        var lease = await _opener.ReopenVerifiedRollbackPayloadAsync(receipt, cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateLease(lease, pin, receipt.HandleId);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<IVerifiedRollbackPayloadLease> ReacquireVerifiedRollbackPayloadAsync(
        ProtectedRollbackPayloadReceipt durableReceipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(durableReceipt);
        var pin = _upgradeLock.Get(durableReceipt.Kind);
        var expected = ReceiptFor(pin.Kind);
        if (durableReceipt != expected)
            throw new InvalidDataException("The rollback receipt belongs to another operation or MSI.");

        var lease = await _opener.ReopenVerifiedRollbackPayloadAsync(durableReceipt, cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateLease(lease, pin, expected.HandleId);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private ProtectedRollbackPayloadReceipt ReceiptFor(LegacyApplicationKind kind) =>
        new($"windows-msi-op-v1:{_operationId:N}:{(int)kind}", kind,
            RollbackPayloadProtection.ProtectedMachineStaging);

    private static void ValidateLease(IWindowsVerifiedRollbackPayloadLease lease, LegacyUpgradePin pin, string handleId)
    {
        if (lease.HandleId != handleId ||
            lease.Protection != RollbackPayloadProtection.ProtectedMachineStaging ||
            lease.Inspection.Identity != pin.Identity || lease.Inspection.PackageId != pin.PackageId ||
            lease.Inspection.InstallerName != pin.InstallerName || lease.Inspection.Version != pin.Version ||
            lease.Inspection.SizeBytes != pin.SizeBytes ||
            !string.Equals(lease.Inspection.Sha256, pin.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !lease.Inspection.TrustedSource)
            throw new InvalidDataException("The reopened rollback MSI does not match its operation and embedded lock.");
    }
}
