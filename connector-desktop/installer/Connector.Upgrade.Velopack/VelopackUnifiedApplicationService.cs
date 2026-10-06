using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Connector.Upgrade.Velopack;

/// <summary>
/// Installs the pinned unified application for the current Windows user and compensates only the
/// exact identity installed by this service call. It never mutates either legacy application.
/// </summary>
public sealed class VelopackUnifiedApplicationService
{
    private const int Success = 0;
    private const int SuccessRebootRequired = 3010;
    private static readonly Regex VersionPattern = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant);

    private readonly IVelopackInstallProbe _probe;
    private readonly IVelopackProcessRunner _runner;
    private readonly string _currentUserSid;
    private readonly string _localApplicationData;
    private readonly TimeSpan _timeout;

    public VelopackUnifiedApplicationService(
        IVelopackInstallProbe probe,
        IVelopackProcessRunner runner,
        string currentUserSid,
        string localApplicationData,
        TimeSpan timeout)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _currentUserSid = string.IsNullOrWhiteSpace(currentUserSid)
            ? throw new ArgumentException("The current Windows SID is required.", nameof(currentUserSid))
            : currentUserSid;
        _localApplicationData = string.IsNullOrWhiteSpace(localApplicationData) ||
                                !Path.IsPathFullyQualified(localApplicationData)
            ? throw new ArgumentException("The current user's LocalApplicationData path must be absolute.", nameof(localApplicationData))
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(localApplicationData));
        _timeout = timeout > TimeSpan.Zero
            ? timeout
            : throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public static VelopackUnifiedApplicationService CreateForCurrentUser(TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Velopack installer service is available only on Windows.");
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows identity has no SID.");
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var probe = new SystemVelopackInstallProbe(sid, localApplicationData);
        return new VelopackUnifiedApplicationService(
            probe,
            new SystemVelopackProcessRunner(),
            sid,
            localApplicationData,
            timeout);
    }

    public async ValueTask<VelopackMutationResult> InstallAsync(
        VelopackSetupPin pin,
        IVerifiedVelopackSetupLease setupLease,
        string targetUserSid,
        CancellationToken cancellationToken = default)
    {
        ValidatePin(pin);
        ValidateSameUser(targetUserSid);
        ValidateLease(setupLease, pin);

        var before = _probe.Inspect(pin, targetUserSid);
        if (before.Presence != VelopackInstallPresence.Absent)
            throw new VelopackInstallInvariantException(
                "The Velopack packId already has foreign or pre-existing state; this run will not replace it.");

        setupLease.VerifyLaunchPath();
        var process = await RunSafelyAsync(
            new VelopackProcessRequest(setupLease.StagedPath, ["--silent"], _timeout),
            cancellationToken).ConfigureAwait(false);
        var (after, probeFailure) = ProbeSafely(pin, targetUserSid);

        if (process.Completion != VelopackProcessCompletion.Exited)
            return Manual(process, after, probeFailure,
                "Setup.exe did not report a final exit code; its exact outcome requires manual recovery.");

        var exact = after?.Presence == VelopackInstallPresence.ExactInstalled && after.Identity is not null;
        if ((process.ExitCode == Success || process.ExitCode == SuccessRebootRequired) && exact)
        {
            var receipt = new VelopackInstallReceipt(
                Guid.NewGuid().ToString("N"),
                pin,
                after!.Identity!);
            return new VelopackMutationResult(
                process.ExitCode == SuccessRebootRequired
                    ? VelopackMutationDisposition.SucceededRebootRequired
                    : VelopackMutationDisposition.Succeeded,
                after,
                process,
                receipt,
                process.ExitCode == SuccessRebootRequired
                    ? "The exact installation was verified; Windows requested a reboot."
                    : "The exact installation was verified.");
        }

        return Manual(process, after, probeFailure,
            "Setup.exe exit status and the exact installed identity were not both proven.");
    }

    public async ValueTask<VelopackMutationResult> RollbackAsync(
        VelopackInstallReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidatePin(receipt.SetupPin);
        ValidateSameUser(receipt.InstalledIdentity.UserSid);
        ValidateReceiptRoot(receipt);

        var before = _probe.Inspect(receipt.SetupPin, receipt.InstalledIdentity.UserSid);
        if (before.Presence == VelopackInstallPresence.Absent)
            return new VelopackMutationResult(
                VelopackMutationDisposition.AlreadyInDesiredState,
                before,
                new VelopackProcessResult(VelopackProcessCompletion.Exited, Success),
                null,
                "The exact installation issued by this run is already absent; no process was started.");
        if (before.Presence != VelopackInstallPresence.ExactInstalled ||
            before.Identity != receipt.InstalledIdentity)
            throw new VelopackInstallInvariantException(
                "Rollback refused because the current Velopack identity differs from the install receipt.");

        var updateExecutable = Path.Combine(receipt.InstalledIdentity.InstallRoot, "Update.exe");
        var process = await RunSafelyAsync(
            new VelopackProcessRequest(updateExecutable, ["uninstall", "--silent"], _timeout),
            cancellationToken).ConfigureAwait(false);
        var (after, probeFailure) = ProbeSafely(receipt.SetupPin, receipt.InstalledIdentity.UserSid);

        if (process.Completion != VelopackProcessCompletion.Exited)
            return Manual(process, after, probeFailure,
                "Update.exe did not report a final exit code; its exact outcome requires manual recovery.");

        if ((process.ExitCode == Success || process.ExitCode == SuccessRebootRequired) &&
            after?.Presence == VelopackInstallPresence.Absent)
            return new VelopackMutationResult(
                process.ExitCode == SuccessRebootRequired
                    ? VelopackMutationDisposition.SucceededRebootRequired
                    : VelopackMutationDisposition.Succeeded,
                after,
                process,
                null,
                process.ExitCode == SuccessRebootRequired
                    ? "The exact install was removed; Windows requested a reboot."
                    : "The exact install was removed.");

        return Manual(process, after, probeFailure,
            "Update.exe exit status and confirmed absence were not both proven.");
    }

    private void ValidateSameUser(string targetUserSid)
    {
        if (string.IsNullOrWhiteSpace(targetUserSid) ||
            !string.Equals(targetUserSid, _currentUserSid, StringComparison.OrdinalIgnoreCase))
            throw new VelopackInstallInvariantException(
                "Velopack Setup.exe must run in the same Windows user profile selected by the installer.");
    }

    private void ValidateReceiptRoot(VelopackInstallReceipt receipt)
    {
        if (string.IsNullOrWhiteSpace(receipt.OperationId))
            throw new VelopackInstallInvariantException("The install receipt has no operation identity.");
        var expectedRoot = Path.Combine(_localApplicationData, receipt.SetupPin.PackId);
        if (!PathsEqual(receipt.InstalledIdentity.InstallRoot, expectedRoot) ||
            !string.Equals(receipt.InstalledIdentity.PackId, receipt.SetupPin.PackId, StringComparison.Ordinal) ||
            !string.Equals(receipt.InstalledIdentity.Version, receipt.SetupPin.Version, StringComparison.Ordinal))
            throw new VelopackInstallInvariantException("The install receipt does not identify the pinned same-user root.");
    }

    private static void ValidatePin(VelopackSetupPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (!string.Equals(pin.PackId, UnifiedVelopackApplication.PackId, StringComparison.Ordinal))
            throw new VelopackInstallInvariantException("The setup pin is not for the unified Connector packId.");
        if (!VersionPattern.IsMatch(pin.Version))
            throw new VelopackInstallInvariantException("The setup pin version is invalid.");
        if (pin.SizeBytes <= 0)
            throw new VelopackInstallInvariantException("The setup pin size must be positive.");
        if (!Enum.IsDefined(pin.TrustMode))
            throw new VelopackInstallInvariantException("The setup pin trust mode is invalid.");
        ValidateSha256(pin.Sha256, "setup pin");
    }

    private static void ValidateLease(IVerifiedVelopackSetupLease lease, VelopackSetupPin pin)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Protection != VelopackSetupLeaseProtection.ProtectedInstallerStaging ||
            string.IsNullOrWhiteSpace(lease.HandleId) ||
            lease.ContentHandle.IsInvalid || lease.ContentHandle.IsClosed)
            throw new VelopackInstallInvariantException("A live protected Setup.exe lease is required.");
        if (!Path.IsPathFullyQualified(lease.StagedPath) ||
            !string.Equals(Path.GetExtension(lease.StagedPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new VelopackInstallInvariantException("The staged Setup.exe path must be an absolute executable path.");

        var inspection = lease.Inspection ??
            throw new VelopackInstallInvariantException("The protected Setup.exe lease has no inspection.");
        if (inspection.TrustMode != pin.TrustMode || !inspection.SignatureVerified || (inspection.TrustMode switch
            {
                VelopackSetupTrustMode.Authenticode => string.IsNullOrWhiteSpace(inspection.SignatureEvidenceId),
                VelopackSetupTrustMode.SignedManifestHash => inspection.SignatureEvidenceId is not null,
                _ => true,
            }))
            throw new VelopackInstallInvariantException("The staged Setup.exe release trust was not verified.");
        if (!string.Equals(inspection.PackId, pin.PackId, StringComparison.Ordinal) ||
            !string.Equals(inspection.Version, pin.Version, StringComparison.Ordinal) ||
            inspection.SizeBytes != pin.SizeBytes ||
            !string.Equals(inspection.Sha256, pin.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new VelopackInstallInvariantException("The protected Setup.exe inspection does not match the pin.");

        FileInfo file;
        try
        {
            file = new FileInfo(lease.StagedPath);
            if (!file.Exists || file.Length != pin.SizeBytes)
                throw new VelopackInstallInvariantException("The staged Setup.exe size does not match the pin.");
            using var stream = new FileStream(lease.StagedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actualHash, pin.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new VelopackInstallInvariantException("The staged Setup.exe hash does not match the pin.");
        }
        catch (VelopackInstallInvariantException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SystemException)
        {
            throw new VelopackInstallInvariantException("The staged Setup.exe could not be reverified.", error);
        }
    }

    private async ValueTask<VelopackProcessResult> RunSafelyAsync(
        VelopackProcessRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new VelopackProcessResult(
                VelopackProcessCompletion.Cancelled,
                Failure: "The process runner cancelled without a final exit code.");
        }
        catch (Exception error) when (error is SystemException or InvalidOperationException)
        {
            return new VelopackProcessResult(
                VelopackProcessCompletion.StartFailed,
                Failure: error.GetType().Name);
        }
    }

    private (VelopackInstallObservation? Observation, string? Failure) ProbeSafely(
        VelopackSetupPin pin,
        string targetUserSid)
    {
        try
        {
            return (_probe.Inspect(pin, targetUserSid), null);
        }
        catch (Exception error) when (error is SystemException or InvalidOperationException)
        {
            return (null, error.Message);
        }
    }

    private static VelopackMutationResult Manual(
        VelopackProcessResult process,
        VelopackInstallObservation? probe,
        string? probeFailure,
        string detail) => new(
            VelopackMutationDisposition.ManualRecoveryRequired,
            probe,
            process,
            null,
            $"{detail} Probe: {probe?.Detail ?? probeFailure ?? "unavailable"}");

    private static void ValidateSha256(string value, string source)
    {
        if (value.Length != 64)
            throw new VelopackInstallInvariantException($"The {source} SHA-256 is invalid.");
        try
        {
            _ = Convert.FromHexString(value);
        }
        catch (FormatException error)
        {
            throw new VelopackInstallInvariantException($"The {source} SHA-256 is invalid.", error);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
