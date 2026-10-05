using Microsoft.Win32.SafeHandles;

namespace Connector.Upgrade.Velopack;

public static class UnifiedVelopackApplication
{
    public const string PackId = "Structura.Connector.Desktop";
    public const string MainExecutable = "Connector.Desktop.exe";
}

public sealed record VelopackSetupPin(
    string PackId,
    string Version,
    long SizeBytes,
    string Sha256,
    VelopackSetupTrustMode TrustMode = VelopackSetupTrustMode.Authenticode,
    string? SignerThumbprint = null);

public sealed record VelopackSetupInspection(
    string PackId,
    string Version,
    long SizeBytes,
    string Sha256,
    bool SignatureVerified,
    string? SignatureEvidenceId,
    VelopackSetupTrustMode TrustMode = VelopackSetupTrustMode.Authenticode);

public enum VelopackSetupTrustMode
{
    Authenticode = 1,
    SignedManifestHash = 2,
}

public enum VelopackSetupLeaseProtection
{
    ProtectedInstallerStaging = 1,
}

/// <summary>
/// The installer owns and disposes this lease. Its live handle must deny replacement of the staged
/// Setup executable while the service rechecks and starts that same path.
/// </summary>
public interface IVerifiedVelopackSetupLease : IAsyncDisposable
{
    string HandleId { get; }
    string StagedPath { get; }
    SafeFileHandle ContentHandle { get; }
    VelopackSetupLeaseProtection Protection { get; }
    VelopackSetupInspection Inspection { get; }
    /// <summary>
    /// Reopens the exact launch path and proves it still names the leased file. The installer calls
    /// this immediately before starting Setup.exe while the content handle remains live.
    /// </summary>
    void VerifyLaunchPath();
}

public enum VelopackInstallPresence
{
    Absent = 0,
    ExactInstalled = 1,
    Conflicting = 2,
}

public sealed record VelopackRegistration(
    string UserSid,
    string KeyPath,
    string InstallLocation,
    string Publisher,
    string? DisplayVersion,
    string UninstallString,
    string QuietUninstallString);

public sealed record VelopackInstalledIdentity(
    string UserSid,
    string PackId,
    string Version,
    string InstallRoot,
    string SqVersionSha256,
    string MainExecutableSha256,
    string UpdateExecutableSha256,
    string RootLauncherSha256,
    VelopackRegistration Registration);

public sealed record VelopackInstallObservation(
    VelopackInstallPresence Presence,
    VelopackInstalledIdentity? Identity,
    string ObservationId,
    string Detail);

public interface IVelopackInstallProbe
{
    VelopackInstallObservation Inspect(VelopackSetupPin pin, string targetUserSid);
}

public enum VelopackProcessCompletion
{
    Exited = 0,
    TimedOut = 1,
    Cancelled = 2,
    StartFailed = 3,
}

public sealed record VelopackProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout);

public sealed record VelopackProcessResult(
    VelopackProcessCompletion Completion,
    int? ExitCode = null,
    string? Failure = null);

public interface IVelopackProcessRunner
{
    ValueTask<VelopackProcessResult> RunAsync(
        VelopackProcessRequest request,
        CancellationToken cancellationToken);
}

public enum VelopackMutationDisposition
{
    Succeeded = 0,
    SucceededRebootRequired = 1,
    AlreadyInDesiredState = 2,
    ManualRecoveryRequired = 3,
}

public sealed class VelopackInstallReceipt
{
    internal VelopackInstallReceipt(
        string operationId,
        VelopackSetupPin setupPin,
        VelopackInstalledIdentity installedIdentity)
    {
        OperationId = operationId;
        SetupPin = setupPin;
        InstalledIdentity = installedIdentity;
    }

    public string OperationId { get; }
    public VelopackSetupPin SetupPin { get; }
    public VelopackInstalledIdentity InstalledIdentity { get; }

    /// <summary>
    /// Rehydrates an exact receipt from a protected upgrade journal. The caller must validate the
    /// journal's owner and integrity; RollbackAsync still probes the live registration and hashes.
    /// </summary>
    public static VelopackInstallReceipt FromProtectedJournal(
        string operationId,
        VelopackSetupPin setupPin,
        VelopackInstalledIdentity installedIdentity)
    {
        if (!Guid.TryParseExact(operationId, "N", out _) || setupPin is null || installedIdentity is null)
            throw new VelopackInstallInvariantException("The persisted Velopack receipt identity is invalid.");
        if (!string.Equals(setupPin.PackId, installedIdentity.PackId, StringComparison.Ordinal) ||
            !string.Equals(setupPin.Version, installedIdentity.Version, StringComparison.Ordinal))
            throw new VelopackInstallInvariantException("The persisted Velopack receipt does not match its Setup pin.");
        return new VelopackInstallReceipt(operationId, setupPin, installedIdentity);
    }
}

public sealed record VelopackMutationResult(
    VelopackMutationDisposition Disposition,
    VelopackInstallObservation? StatusProbe,
    VelopackProcessResult Process,
    VelopackInstallReceipt? Receipt,
    string Detail);

public sealed class VelopackInstallInvariantException : InvalidOperationException
{
    public VelopackInstallInvariantException(string message) : base(message)
    {
    }

    public VelopackInstallInvariantException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
