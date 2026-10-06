using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsMsi;

namespace Connector.Upgrade.NetBirdMachine;

public sealed record NetBirdMachineInspection(
    bool InspectionComplete,
    NetBirdAssessment Assessment,
    NetBirdMsiPackageIdentity? InstalledPackage,
    string? InstallerSha256,
    bool ServiceIdentityVerified,
    string EvidenceId,
    string? OwnerOperationId = null);

public sealed record NetBirdInstalledServiceVerification(
    bool IsExact,
    NetBirdMsiPackageIdentity Package,
    string ServiceIdentity,
    string EvidenceId);

/// <summary>
/// A live lease over the exact MSI bytes copied into ACL-protected machine staging. Implementations
/// must verify the embedded official pin after the protected copy, including Authenticode trust,
/// signer subject/thumbprint, SHA-256, size, filename and MSI ProductVersion. The protected bytes
/// must remain non-replaceable until the lease is disposed.
/// </summary>
public interface IVerifiedNetBirdPackageLease : IAsyncDisposable
{
    string HandleId { get; }
    string StagedPath { get; }
    RollbackPayloadProtection Protection { get; }
    NetBirdMsiPackageIdentity Package { get; }
    long SizeBytes { get; }
    string Sha256 { get; }
    bool AuthenticodeTrusted { get; }
    string SignerSubject { get; }
    string SignerThumbprint { get; }
}

public interface IVerifiedNetBirdPackageStager
{
    ValueTask<IVerifiedNetBirdPackageLease> StageAndVerifyAsync(
        string sourceMsiPath,
        OfficialNetBirdPackagePin pin,
        CancellationToken cancellationToken);

    ValueTask<IVerifiedNetBirdPackageLease> ReacquireAsync(
        ProtectedNetBirdPackageReceipt receipt,
        OfficialNetBirdPackagePin pin,
        CancellationToken cancellationToken);
}

/// <summary>
/// Readback and protected owner-marker operations. InspectAsync must classify every discovered
/// NetBird artifact; incomplete inventory is represented by InspectionComplete=false. The verify
/// methods ignore the owner marker and prove the exact MSI registration, signed CLI and service
/// name/image/start identity after msiexec has returned.
/// </summary>
public interface INetBirdMachineStatePort
{
    ValueTask<NetBirdMachineInspection> InspectAsync(CancellationToken cancellationToken);

    ValueTask<NetBirdInstalledServiceVerification> VerifyInstalledPackageAndServiceAsync(
        NetBirdMsiPackageIdentity expectedPackage,
        CancellationToken cancellationToken);

    ValueTask<bool> VerifyPackageAbsentAsync(
        NetBirdMsiPackageIdentity expectedPackage,
        CancellationToken cancellationToken);

    ValueTask PublishOwnershipAsync(
        string operationId,
        IVerifiedNetBirdPackageLease package,
        NetBirdInstalledServiceVerification service,
        CancellationToken cancellationToken);

    ValueTask RemoveOwnershipAsync(
        NetBirdOwnedState exactState,
        CancellationToken cancellationToken);

    ValueTask RestoreOwnershipAsync(
        IVerifiedNetBirdRestorePointLease restorePoint,
        NetBirdInstalledServiceVerification service,
        CancellationToken cancellationToken);
}

public interface IVerifiedNetBirdRestorePointLease : IAsyncDisposable
{
    NetBirdOwnedRestorePoint RestorePoint { get; }
    NetBirdMsiPackageIdentity Package { get; }
    string StagedPath { get; }
    long SizeBytes { get; }
    string Sha256 { get; }
    bool AuthenticodeTrusted { get; }
    string SignerSubject { get; }
    string SignerThumbprint { get; }
}

/// <summary>
/// Captures the exact currently-owned MSI and protected owner/configuration state before update.
/// Reacquire must bind the durable handle to those same protected bytes and state.
/// </summary>
public interface INetBirdOwnedRestorePointStore
{
    ValueTask<IVerifiedNetBirdRestorePointLease> CaptureAsync(
        NetBirdMachineInspection exactOwnedInstallation,
        CancellationToken cancellationToken);

    ValueTask<IVerifiedNetBirdRestorePointLease> ReacquireAsync(
        NetBirdOwnedRestorePoint restorePoint,
        CancellationToken cancellationToken);
}

public interface INetBirdMsiMutationRunner
{
    ValueTask<WindowsMsiProcessResult> InstallAsync(
        string protectedMsiPath,
        CancellationToken cancellationToken);

    ValueTask<WindowsMsiProcessResult> UninstallExactAsync(
        Guid productCode,
        CancellationToken cancellationToken);
}

public interface ITrustedElevatedCallerGate
{
    void AssertTrustedElevatedCaller();
}

public sealed class NetBirdMachineInvariantException : InvalidOperationException
{
    public NetBirdMachineInvariantException(string message) : base(message) { }
    public NetBirdMachineInvariantException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class NetBirdManualRecoveryRequiredException : InvalidOperationException
{
    public NetBirdManualRecoveryRequiredException(string message) : base(message) { }
    public NetBirdManualRecoveryRequiredException(string message, Exception innerException) : base(message, innerException) { }
}
