using Connector.Upgrade.Core;
using Microsoft.Win32.SafeHandles;

namespace Connector.Upgrade.WindowsPayload;

public sealed record WindowsRollbackPayloadSources(
    string StructuraConnectorMsiPath,
    string PlatformConnectorMsiPath)
{
    internal string GetPath(LegacyApplicationKind kind) => kind switch
    {
        LegacyApplicationKind.StructuraConnector => StructuraConnectorMsiPath,
        LegacyApplicationKind.PlatformConnector => PlatformConnectorMsiPath,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported legacy application."),
    };
}

/// <summary>
/// A Windows MSI lease backed by one open handle. The lease owns <see cref="ContentHandle"/>;
/// callers may borrow it but must never close or dispose it. <see cref="StagedPath"/> is valid for
/// rollback only while this lease is alive. Consumers must pass this exact path to the read/install
/// operation and must not copy or reopen the user-owned source. Core's Inspection.TrustedSource
/// means that the exact pinned bytes were authenticated and are now held in protected destination
/// staging; it does not attest that the original user-owned source path had a trusted ACL.
/// </summary>
public interface IWindowsVerifiedRollbackPayloadLease : IVerifiedRollbackPayloadLease
{
    string StagedPath { get; }
    SafeFileHandle ContentHandle { get; }
}

/// <summary>
/// Stages and reopens rollback MSI content. Reacquisition resolves only an opaque protected-stage
/// handle and kind, then reinspects the live MSI against its embedded repository pin. This
/// component never installs or removes an MSI.
/// </summary>
public interface IWindowsRollbackPayloadStore
{
    ValueTask<IVerifiedRollbackPayloadLease> AcquireVerifiedRollbackPayloadAsync(
        LegacyRollbackPayload payload,
        CancellationToken cancellationToken = default);

    ValueTask<IVerifiedRollbackPayloadLease> ReacquireVerifiedRollbackPayloadAsync(
        ProtectedRollbackPayloadReceipt durableReceipt,
        CancellationToken cancellationToken = default);
}

/// <summary>Elevated, operation-bound staging for one of the two embedded legacy MSI pins.</summary>
public interface IWindowsOperationRollbackPayloadStager
{
    ValueTask<IWindowsVerifiedRollbackPayloadLease> StageVerifiedRollbackPayloadAsync(
        LegacyApplicationKind kind,
        CancellationToken cancellationToken = default);
}

/// <summary>Original-user, read-only reopening of an existing operation-bound MSI slot.</summary>
public interface IWindowsOperationRollbackPayloadOpener
{
    ValueTask<IWindowsVerifiedRollbackPayloadLease> ReopenVerifiedRollbackPayloadAsync(
        ProtectedRollbackPayloadReceipt receipt,
        CancellationToken cancellationToken = default);
}

/// <summary>Signals that an immutable operation-scoped MSI slot cannot be safely adopted.</summary>
public sealed class WindowsRollbackPayloadNeedsManualRecoveryException(LegacyApplicationKind kind)
    : IOException("The protected rollback MSI slot requires manual recovery.")
{
    public LegacyApplicationKind Kind { get; } = kind;
}

internal sealed record MsiPackageIdentity(Guid ProductCode, Guid UpgradeCode, string Version);

internal interface IMsiPackageIdentityReader
{
    MsiPackageIdentity Read(string path);
}

internal interface IWindowsMachineStagingSecurity
{
    string PrepareRoot(string requestedRoot);
    void CreateProtectedDirectory(string path);
    void ProtectAndValidateFile(string path);
    void ValidateSharedDirectory(string path);
    void ValidateProtectedDirectory(string path);
    void ValidateProtectedFile(string path);
    void CreateRollbackBatchDirectory(string path, string initiatingUserSid);
    void ValidateRollbackBatchDirectory(string path, string initiatingUserSid);
    void ProtectAndValidateRollbackFile(string path, string initiatingUserSid);
    void ValidateRollbackFile(string path, string initiatingUserSid);
}
