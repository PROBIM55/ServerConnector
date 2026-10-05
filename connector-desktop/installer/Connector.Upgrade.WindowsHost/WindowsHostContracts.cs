using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;
using Connector.Upgrade.UserState;
using Connector.Upgrade.Velopack;
using Connector.Upgrade.WindowsMsi;

namespace Connector.Upgrade.WindowsHost;

public sealed record WindowsHostSession(string OperationId, string InitiatingUserSid)
{
    public void Validate()
    {
        if (!Guid.TryParseExact(OperationId, "N", out _))
            throw new ArgumentException("The Windows host operation id must be a GUID in N format.", nameof(OperationId));
        if (string.IsNullOrWhiteSpace(InitiatingUserSid))
            throw new ArgumentException("The initiating Windows SID is required.", nameof(InitiatingUserSid));
    }
}

public sealed record WindowsHostReadiness(
    bool NewServerSchemaAvailable,
    bool LegacyWorkCanDrainSafely,
    string EvidenceId);

/// <summary>Live environment evidence that is not owned by an installer module.</summary>
public interface IWindowsHostEnvironmentPort
{
    string GetCurrentUserSid();

    ValueTask<WindowsHostReadiness> InspectReadinessAsync(CancellationToken cancellationToken);

    ValueTask<LegacyProcessDrainProof> DrainLegacyApplicationAsync(
        LegacyApplicationKind kind,
        CancellationToken cancellationToken);
}

/// <summary>Consumes the one-time Platform token and owns exact compensation of that enrollment.</summary>
public interface IPlatformEnrollmentPort
{
    ValueTask<PlatformEnrollmentReceipt> EnrollAsync(
        OneTimePlatformToken token,
        CancellationToken cancellationToken);

    ValueTask RemoveAsync(PlatformEnrollmentReceipt receipt, CancellationToken cancellationToken);
}

/// <summary>Performs live API, VPN route and SMB checks for the exact new enrollment.</summary>
public interface ILiveNewAccessVerificationPort
{
    ValueTask<NewAccessVerification> VerifyAsync(
        PlatformEnrollmentReceipt receipt,
        CancellationToken cancellationToken);
}

public sealed record WindowsHostUserStateRecoveryMetadata(
    int SchemaVersion,
    string OperationId,
    string OwnerSid,
    UserStateSnapshotHandle Snapshot,
    UserStateTargetFingerprint OriginalTargets)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record WindowsHostUserStateRecoveryStatus(
    bool CanRestore,
    bool MatchesSnapshot,
    string EvidenceId);

public interface IWindowsHostUserStatePort
{
    ValueTask<UserStateSnapshot> CaptureAsync(
        string initiatingUserSid,
        string operationId,
        CancellationToken cancellationToken);

    ValueTask<WindowsHostUserStateRecoveryStatus> InspectRecoveryAsync(
        UserStateSnapshot snapshot,
        CancellationToken cancellationToken);

    ValueTask RestoreAsync(UserStateSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed record WindowsHostVelopackRecoveryMetadata(
    string OperationId,
    VelopackSetupPin SetupPin,
    VelopackInstalledIdentity InstalledIdentity);

public interface IWindowsHostUnifiedApplicationPort
{
    ValueTask<UnifiedApplicationReceipt> InstallAsync(CancellationToken cancellationToken);

    ValueTask RemoveAsync(UnifiedApplicationReceipt receipt, CancellationToken cancellationToken);

    ValueTask<bool> IsExactInstallReadyAsync(CancellationToken cancellationToken);

    WindowsHostVelopackRecoveryMetadata? GetRecoveryMetadata(UnifiedApplicationReceipt receipt);
}

public interface IWindowsHostNetBirdPort
{
    ValueTask<NetBirdAssessment> InspectAsync(CancellationToken cancellationToken);

    ValueTask<NetBirdMutationPlan> PrepareAsync(
        NetBirdAssessment assessment,
        Guid operationId,
        CancellationToken cancellationToken);

    ValueTask<NetBirdMutationReceipt> ApplyAsync(
        NetBirdMutationPlan plan,
        CancellationToken cancellationToken);

    ValueTask<NetBirdInterruptedRecoveryResult> ReconcileAsync(
        NetBirdMutationPlan plan,
        CancellationToken cancellationToken);

    ValueTask RemoveInstalledThisRunAsync(
        NetBirdInstalledThisRunReceipt receipt,
        CancellationToken cancellationToken);

    ValueTask RestoreUpdatedThisRunAsync(
        NetBirdUpdatedThisRunReceipt receipt,
        CancellationToken cancellationToken);
}

public sealed class WindowsHostManualRecoveryRequiredException : InvalidOperationException
{
    public WindowsHostManualRecoveryRequiredException(string message) : base(message) { }
    public WindowsHostManualRecoveryRequiredException(string message, Exception innerException)
        : base(message, innerException) { }
}
