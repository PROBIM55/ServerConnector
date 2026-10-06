using System.Text.Json.Serialization;

namespace Connector.Upgrade.Core;

public enum LegacyApplicationKind
{
    StructuraConnector = 1,
    PlatformConnector = 2,
}

public sealed record LegacyApplicationIdentity(
    LegacyApplicationKind Kind,
    Guid ProductCode,
    Guid UpgradeCode);

public sealed record InstalledLegacyApplication(
    LegacyApplicationIdentity Identity,
    string Version,
    bool ExactMsiIdentityVerified);

public sealed record LegacyApplicationPresenceBaseline(
    LegacyApplicationIdentity Identity,
    ExactLegacyPresence Presence,
    string ObservationId,
    string? InstalledVersion);

public sealed record LegacyRollbackPayload(
    LegacyApplicationIdentity Identity,
    string PackageId,
    string InstallerName,
    string Version,
    long SizeBytes,
    string Sha256,
    bool Verified);

public sealed record RollbackPayloadInspection(
    LegacyApplicationIdentity Identity,
    string PackageId,
    string InstallerName,
    string Version,
    long SizeBytes,
    string Sha256,
    bool TrustedSource);

public enum RollbackPayloadProtection
{
    UnprotectedOrUserWritable = 0,
    ProtectedMachineStaging = 1,
}

/// <summary>
/// A content-bound handle whose bytes cannot be replaced for its lifetime. The protection value is
/// an adapter attestation, not an OS guarantee made by Core. A production adapter must enforce ACL-
/// protected machine staging and open/pin the verified content; user-writable paths are forbidden.
/// DisposeAsync releases only the handle.
/// </summary>
public interface IVerifiedRollbackPayloadLease : IAsyncDisposable
{
    string HandleId { get; }
    RollbackPayloadProtection Protection { get; }
    RollbackPayloadInspection Inspection { get; }
}

public sealed record UserStateSnapshot(
    string WindowsUserId,
    string SnapshotId,
    string Sha256,
    bool Verified);

public enum NetBirdOwnership
{
    Absent = 0,
    OwnedByConnector = 1,
    Foreign = 2,
    Unattributed = 3,
}

public sealed record NetBirdAssessment(
    NetBirdOwnership Ownership,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InstallationId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NetBirdOwnedState? OwnedState = null);

public sealed record UpgradePreflight(
    string CurrentWindowsUserId,
    IReadOnlyList<InstalledLegacyApplication> LegacyApplications,
    IReadOnlyList<LegacyRollbackPayload> RollbackPayloads,
    UserStateSnapshot UserState,
    NetBirdAssessment NetBird,
    bool NewServerSchemaAvailable,
    bool LegacyWorkCanDrainSafely,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<LegacyApplicationPresenceBaseline>? LegacyPresenceBaseline = null);

/// <summary>A new Platform enrollment secret. Its string representation is always redacted.</summary>
public sealed class OneTimePlatformToken
{
    public OneTimePlatformToken(string value)
    {
        Value = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A one-time Platform token is required.", nameof(value))
            : value;
    }

    public string Value { get; }

    public override string ToString() => "[REDACTED]";
}

public enum NetBirdChangeKind
{
    NoChange = 0,
    InstalledThisRun = 1,
    UpdatedThisRun = 2,
}

public enum NetBirdOperationStatus
{
    Prepared = 0,
    Applied = 1,
}

/// <summary>
/// User-side handle for one trusted machine-owned NetBird operation. It contains only the
/// operation identity and the observed state transition; package and restore-point details stay
/// in the machine journal.
/// </summary>
public sealed record NetBirdOperationIntent(
    string OperationId,
    NetBirdOwnership PriorOwnership,
    NetBirdChangeKind Change,
    NetBirdOperationStatus Status = NetBirdOperationStatus.Prepared);

/// <summary>Bounded receipt returned by the machine side after it durably records the operation.</summary>
public sealed record NetBirdOperationReceipt(
    string OperationId,
    NetBirdOwnership PriorOwnership,
    NetBirdChangeKind Change,
    NetBirdOperationStatus Status,
    NetBirdOwnership ResultingOwnership,
    bool RebootRequired = false);

public sealed record NetBirdMsiPackageIdentity(
    Guid ProductCode,
    Guid UpgradeCode,
    string ProductVersion,
    string Manufacturer,
    string ProductName);

public sealed record ProtectedNetBirdPackageReceipt(
    string HandleId,
    RollbackPayloadProtection Protection,
    string InstallerName,
    NetBirdMsiPackageIdentity Package,
    long SizeBytes,
    string Sha256,
    string SignerSubject,
    string SignerThumbprint);

public sealed record NetBirdOwnedState(
    string InstallationId,
    string Version,
    string ConfigurationSha256,
    string ServiceIdentity);

/// <summary>
/// Machine-side rich result used by the legacy in-process adapter. Never persist or send this type
/// through the original-user journal or machine IPC; use <see cref="NetBirdOperationReceipt"/>.
/// </summary>
public abstract record NetBirdMutationReceipt(NetBirdChangeKind Change)
{
    public bool RebootRequired { get; init; }
}

public sealed record NetBirdNoChangeReceipt(NetBirdOwnedState ExistingState)
    : NetBirdMutationReceipt(NetBirdChangeKind.NoChange);

public sealed record NetBirdInstalledThisRunReceipt(string OperationId, NetBirdOwnedState InstalledState)
    : NetBirdMutationReceipt(NetBirdChangeKind.InstalledThisRun);

public sealed record NetBirdOwnedRestorePoint(
    string HandleId,
    NetBirdOwnedState OwnedState,
    RollbackPayloadProtection Protection,
    NetBirdMsiPackageIdentity Package,
    string InstallerSha256);

/// <summary>
/// Machine-side rich mutation plan used by the legacy in-process adapter. Its protected package and
/// restore-point handles belong to machine storage and must never enter the original-user journal or IPC.
/// </summary>
public sealed record NetBirdMutationPlan(
    NetBirdChangeKind Change,
    string? OperationId,
    NetBirdAssessment PriorAssessment,
    ProtectedNetBirdPackageReceipt TargetPackage,
    NetBirdOwnedRestorePoint? PriorRestorePoint);

public enum NetBirdInterruptedRecoveryAction
{
    NoMutationObserved = 0,
    RemovedInstalledThisRun = 1,
    RestoredPriorOwnedState = 2,
}

public sealed record NetBirdInterruptedRecoveryResult(
    NetBirdInterruptedRecoveryAction Action,
    string EvidenceId);

public sealed record NetBirdUpdatedThisRunReceipt(
    string OperationId,
    NetBirdOwnedRestorePoint PriorRestorePoint,
    NetBirdOwnedState UpdatedOwnedState)
    : NetBirdMutationReceipt(NetBirdChangeKind.UpdatedThisRun)
{
    public NetBirdOwnedState PriorOwnedState => PriorRestorePoint.OwnedState;
}
/// <summary>
/// Opaque, bounded product-specific recovery evidence. Core persists it in the durable journal
/// without interpreting it; the application adapter must revalidate it against live state.
/// </summary>
public sealed record UnifiedApplicationReceipt(
    string OperationId,
    string? RecoveryMetadataJson = null,
    bool RebootRequired = false);
public sealed record PlatformEnrollmentReceipt(string EnrollmentId);
public sealed record LegacyRemovalReceipt(
    LegacyApplicationIdentity Identity,
    string OperationId,
    bool RebootRequired = false);

public enum ExactLegacyPresence
{
    ExactInstalled,
    Absent,
    IdentityMismatch,
    Unknown,
}

public sealed record LegacyRecoveryObservation(
    LegacyApplicationIdentity Identity,
    ExactLegacyPresence Presence,
    string ObservationId);

public sealed record InterruptedUpgradeInspection(
    string CurrentWindowsUserId,
    IReadOnlyList<LegacyRecoveryObservation> LegacyApplications,
    bool UserStateCanBeRestored,
    bool UserStateMatchesSnapshot);

public sealed record NewAccessVerification(bool PlatformApi, bool VpnRoute, bool Smb, bool SmbRequired = true)
{
    public bool IsSuccessful => PlatformApi && VpnRoute && (!SmbRequired || Smb);
}

public sealed record FinalUpgradeVerification(
    bool UnifiedApplicationReady,
    bool NewAccessStillValid,
    bool ExactLegacyApplicationsAbsent)
{
    public bool IsSuccessful =>
        UnifiedApplicationReady && NewAccessStillValid && ExactLegacyApplicationsAbsent;
}

/// <summary>
/// Mutating methods must either return a receipt after the exact effect is durable, or throw before
/// producing an externally visible effect. Adapters with an uncertain outcome must fail closed and
/// require manual recovery rather than reporting a successful receipt.
/// </summary>
public interface IUpgradeRecoveryPorts
{
    ValueTask<InterruptedUpgradeInspection> InspectInterruptedUpgradeAsync(
        UpgradeRecoveryMetadata recovery,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reopens the same protected staged content identified in the durable journal and rechecks its
    /// identity, size and hash. It must fail closed when the durable object cannot be proven exact.
    /// </summary>
    ValueTask<IVerifiedRollbackPayloadLease> ReacquireVerifiedRollbackPayloadAsync(
        ProtectedRollbackPayloadReceipt durableReceipt,
        CancellationToken cancellationToken);

    ValueTask RestoreMissingLegacyApplicationAsync(
        IVerifiedRollbackPayloadLease payloadLease,
        LegacyRecoveryObservation absentApplication,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reconciles a machine-journaled NetBird operation by trusted operation id. Package and
    /// restore-point evidence are resolved and validated by the machine side.
    /// </summary>
    ValueTask<NetBirdInterruptedRecoveryResult> ReconcileInterruptedNetBirdAsync(
        NetBirdOperationIntent operation,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<NetBirdInterruptedRecoveryResult>(
            new NotSupportedException("A machine-command NetBird recovery adapter is required."));
}

public interface IUpgradePorts : IUpgradeRecoveryPorts
{
    ValueTask<UpgradePreflight> InspectAsync(CancellationToken cancellationToken);

    ValueTask<NetBirdOperationIntent> PrepareOwnedNetBirdMutationAsync(
        NetBirdAssessment assessment,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<NetBirdOperationIntent>(
            new NotSupportedException("A machine-command NetBird adapter is required."));

    ValueTask<NetBirdOperationReceipt> ApplyOwnedNetBirdMutationAsync(
        NetBirdOperationIntent operation,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<NetBirdOperationReceipt>(
            new NotSupportedException("A machine-command NetBird adapter is required."));

    ValueTask CompensateNetBirdMutationAsync(
        NetBirdOperationIntent operation,
        NetBirdOperationReceipt receipt,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(
            new NotSupportedException("A machine-command NetBird compensation adapter is required."));

    ValueTask<UnifiedApplicationReceipt> InstallUnifiedApplicationAsync(CancellationToken cancellationToken);

    ValueTask RemoveUnifiedApplicationAsync(
        UnifiedApplicationReceipt receipt,
        CancellationToken cancellationToken);

    ValueTask<PlatformEnrollmentReceipt> EnrollAsync(
        OneTimePlatformToken token,
        CancellationToken cancellationToken);

    ValueTask RemoveNewEnrollmentAsync(
        PlatformEnrollmentReceipt receipt,
        CancellationToken cancellationToken);

    ValueTask<NewAccessVerification> VerifyNewAccessAsync(
        PlatformEnrollmentReceipt receipt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Pins verified MSI content in protected machine staging. The returned handle must remain
    /// bound to those exact bytes until disposed, including across legacy removal and rollback.
    /// </summary>
    ValueTask<IVerifiedRollbackPayloadLease> AcquireVerifiedRollbackPayloadAsync(
        LegacyRollbackPayload payload,
        CancellationToken cancellationToken);

    ValueTask<LegacyRemovalReceipt> RemoveExactLegacyApplicationAsync(
        LegacyApplicationIdentity identity,
        CancellationToken cancellationToken);

    ValueTask RestoreExactLegacyApplicationAsync(
        IVerifiedRollbackPayloadLease payloadLease,
        LegacyRemovalReceipt removal,
        CancellationToken cancellationToken);

    ValueTask RestoreUserStateAsync(
        UserStateSnapshot snapshot,
        CancellationToken cancellationToken);

    ValueTask<FinalUpgradeVerification> VerifyFinalStateAsync(CancellationToken cancellationToken);
}

/// <summary>Proof that an original-user adapter is bound to one trusted operation and Windows SID.</summary>
public interface IUpgradeSessionBoundPorts
{
    void AssertSessionMatches(string operationId, string initiatingUserSid);
}

/// <summary>
/// Original-user capability to open only a machine-staged, operation-bound rollback MSI.
/// The implementation must not create a slot or choose a source path.
/// </summary>
public interface IOriginalUserOperationRollbackPayloadOpener : IUpgradeSessionBoundPorts
{
    ValueTask<IVerifiedRollbackPayloadLease> OpenExistingRollbackPayloadAsync(
        ProtectedRollbackPayloadReceipt receipt,
        CancellationToken cancellationToken);
}

/// <summary>
/// Marks an adapter whose NetBird assessment and mutations are verified by the authenticated
/// machine helper. Rich machine inventory must stay out of the original-user process and journal.
/// </summary>
public interface IUpgradeMachineVerifiedNetBirdPorts { }

public enum UpgradeOutcome
{
    Succeeded,
    AlreadyCommitted,
    FailedAndRolledBack,
    NeedsManualRecovery,
    Interrupted,
}

public sealed record UpgradeExecutionResult(
    UpgradeOutcome Outcome,
    Guid RunId,
    string? ErrorCode = null,
    bool RebootRequired = false);

public sealed class UpgradeInvariantException(string message) : InvalidOperationException(message);
