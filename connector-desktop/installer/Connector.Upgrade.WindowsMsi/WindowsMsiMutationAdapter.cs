using System.Security.Principal;
using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsPayload;

namespace Connector.Upgrade.WindowsMsi;

/// <summary>
/// Exact mutation adapter for the two embedded legacy MSI pins. The caller remains responsible for
/// orchestration, process drain, privilege acquisition, lease acquisition and journal persistence.
/// </summary>
public sealed class WindowsMsiMutationAdapter
{
    private const int ErrorSuccess = 0;
    private const int ErrorSuccessRebootRequired = 3010;
    private const int ErrorUnknownProduct = 1605;
    private const int ErrorInstallAlreadyRunning = 1618;

    private readonly IWindowsMsiInventory _inventory;
    private readonly IWindowsMsiProcessRunner _runner;
    private readonly LegacyUpgradeLock _upgradeLock;
    private readonly string _currentUserSid;
    private readonly string _msiexecPath;
    private readonly TimeSpan _timeout;
    private readonly Func<LegacyApplicationKind, CancellationToken, ValueTask>? _beforeRemoval;

    public WindowsMsiMutationAdapter(
        IWindowsMsiInventory inventory,
        IWindowsMsiProcessRunner runner,
        string currentUserSid,
        string msiexecPath,
        TimeSpan timeout,
        LegacyUpgradeLock? upgradeLock = null,
        Func<LegacyApplicationKind, CancellationToken, ValueTask>? beforeRemoval = null)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _currentUserSid = string.IsNullOrWhiteSpace(currentUserSid)
            ? throw new ArgumentException("The current Windows SID is required.", nameof(currentUserSid))
            : currentUserSid;
        _msiexecPath = Path.IsPathFullyQualified(msiexecPath)
            ? msiexecPath
            : throw new ArgumentException("The msiexec path must be absolute.", nameof(msiexecPath));
        _timeout = timeout > TimeSpan.Zero
            ? timeout
            : throw new ArgumentOutOfRangeException(nameof(timeout));
        _upgradeLock = upgradeLock ?? LegacyUpgradeLock.LoadEmbedded();
        _beforeRemoval = beforeRemoval;
    }

    public static WindowsMsiMutationAdapter CreateForCurrentUser(
        TimeSpan timeout,
        Func<LegacyApplicationKind, CancellationToken, ValueTask>? beforeRemoval = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Windows MSI adapter is available only on Windows.");
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows identity has no SID.");
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return new WindowsMsiMutationAdapter(
            new WindowsInstallerInventory(sid),
            new SystemWindowsMsiProcessRunner(),
            sid,
            Path.Combine(systemDirectory, "msiexec.exe"),
            timeout,
            beforeRemoval: beforeRemoval);
    }

    public ExactWindowsMsiInspection InspectExact(LegacyApplicationKind kind)
    {
        var pin = _upgradeLock.Get(kind);
        var snapshot = _inventory.Inspect(pin);
        if (snapshot.UpgradeCode != pin.Identity.UpgradeCode)
            throw new WindowsMsiInvariantException($"Inventory returned the wrong UpgradeCode for {kind}.");

        var foreignRelated = snapshot.RelatedProductCodes
            .Where(productCode => productCode != pin.Identity.ProductCode)
            .Distinct()
            .ToArray();
        if (foreignRelated.Length != 0)
            throw new WindowsMsiInvariantException(
                $"UpgradeCode for {kind} has a foreign registered ProductCode.");

        var foreignRegistrations = snapshot.Registrations
            .Where(registration =>
                registration.ProductCode != pin.Identity.ProductCode ||
                registration.UpgradeCode != pin.Identity.UpgradeCode)
            .ToArray();
        if (foreignRegistrations.Length != 0)
            throw new WindowsMsiInvariantException($"Inventory returned a foreign MSI registration for {kind}.");

        var registrations = snapshot.Registrations
            .Where(registration => registration.ProductCode == pin.Identity.ProductCode)
            .ToArray();
        if (registrations.Length == 0)
        {
            if (snapshot.RelatedProductCodes.Contains(pin.Identity.ProductCode))
                throw new WindowsMsiInvariantException(
                    $"Windows Installer related-product membership for {kind} has no resolvable registration.");
            return new ExactWindowsMsiInspection(
                pin.Identity,
                pin.Version,
                ExactWindowsMsiPresence.Absent,
                null);
        }

        if (registrations.Length != 1)
            throw new WindowsMsiInvariantException($"Windows Installer registration for {kind} is ambiguous.");
        if (!snapshot.RelatedProductCodes.Contains(pin.Identity.ProductCode))
            throw new WindowsMsiInvariantException($"UpgradeCode membership is missing for installed {kind}.");

        var registration = registrations[0];
        ValidateProfile(pin.Kind, registration);
        if (!string.Equals(registration.Version, pin.Version, StringComparison.Ordinal))
            throw new WindowsMsiInvariantException($"Installed version for {kind} does not match the embedded lock.");

        return new ExactWindowsMsiInspection(
            pin.Identity,
            pin.Version,
            ExactWindowsMsiPresence.ExactInstalled,
            registration);
    }

    public async ValueTask<WindowsMsiMutationResult> RemoveExactAsync(
        LegacyApplicationKind kind,
        LegacyProcessDrainProof drainProof,
        IWindowsVerifiedRollbackPayloadLease rollbackLease,
        CancellationToken cancellationToken = default)
    {
        var pin = _upgradeLock.Get(kind);
        ValidateDrainProof(drainProof, pin.Kind);
        ValidateLease(rollbackLease, pin);
        var before = InspectExact(kind);
        if (before.Presence == ExactWindowsMsiPresence.Absent)
            return NoProcessResult(pin, WindowsMsiMutationDisposition.AlreadyInDesiredState, before,
                "The exact MSI was already absent; no process was started.");

        var request = new WindowsMsiProcessRequest(
            _msiexecPath,
            ["/x", FormatGuid(pin.Identity.ProductCode), "/qn", "/norestart"],
            _timeout);
        if (_beforeRemoval is not null)
            await _beforeRemoval(kind, cancellationToken).ConfigureAwait(false);
        var process = await RunSafelyAsync(request, cancellationToken).ConfigureAwait(false);
        return ProbeAndClassify(pin, rollbackLease, process, desiredPresence: ExactWindowsMsiPresence.Absent);
    }

    public async ValueTask<WindowsMsiMutationResult> RestoreExactAsync(
        LegacyApplicationKind kind,
        IWindowsVerifiedRollbackPayloadLease rollbackLease,
        CancellationToken cancellationToken = default)
    {
        var pin = _upgradeLock.Get(kind);
        ValidateLease(rollbackLease, pin);
        var before = InspectExact(kind);
        if (before.Presence == ExactWindowsMsiPresence.ExactInstalled)
            return NoProcessResult(pin, WindowsMsiMutationDisposition.AlreadyInDesiredState, before,
                "The exact MSI was already installed; no process was started.");

        var stagedPath = rollbackLease.StagedPath;
        var request = new WindowsMsiProcessRequest(
            _msiexecPath,
            ["/i", stagedPath, "/qn", "/norestart"],
            _timeout);
        var process = await RunSafelyAsync(request, cancellationToken).ConfigureAwait(false);
        return ProbeAndClassify(
            pin,
            rollbackLease,
            process,
            desiredPresence: ExactWindowsMsiPresence.ExactInstalled);
    }

    private WindowsMsiMutationResult ProbeAndClassify(
        LegacyUpgradePin pin,
        IWindowsVerifiedRollbackPayloadLease lease,
        WindowsMsiProcessResult process,
        ExactWindowsMsiPresence desiredPresence)
    {
        ExactWindowsMsiInspection? probe = null;
        string? probeFailure = null;
        try
        {
            ValidateLease(lease, pin);
            probe = InspectExact(pin.Kind);
        }
        catch (Exception error)
        {
            probeFailure = error.Message;
        }

        if (process.Completion is WindowsMsiProcessCompletion.Cancelled or WindowsMsiProcessCompletion.TimedOut)
            return new WindowsMsiMutationResult(
                pin.Identity,
                WindowsMsiMutationDisposition.ManualRecoveryRequired,
                probe,
                process,
                $"Waiting ended before msiexec reported a result. Exact probe: {DescribeProbe(probe, probeFailure)}");
        if (process.Completion == WindowsMsiProcessCompletion.StartFailed)
            return new WindowsMsiMutationResult(
                pin.Identity,
                WindowsMsiMutationDisposition.ManualRecoveryRequired,
                probe,
                process,
                $"msiexec did not report a started operation. Exact probe: {DescribeProbe(probe, probeFailure)}");

        var desired = probe?.Presence == desiredPresence;
        if (process.ExitCode == ErrorSuccess && desired)
            return Success(pin, probe!, process, rebootRequired: false);
        if (process.ExitCode == ErrorSuccessRebootRequired && desired)
            return Success(pin, probe!, process, rebootRequired: true);
        if (process.ExitCode == ErrorUnknownProduct &&
            desiredPresence == ExactWindowsMsiPresence.Absent && desired)
            return new WindowsMsiMutationResult(
                pin.Identity,
                WindowsMsiMutationDisposition.AlreadyInDesiredState,
                probe,
                process,
                "msiexec reported ERROR_UNKNOWN_PRODUCT and the exact post-probe confirmed absence.");
        if (process.ExitCode == ErrorInstallAlreadyRunning && probe is not null && !desired)
            return new WindowsMsiMutationResult(
                pin.Identity,
                WindowsMsiMutationDisposition.RetryRequired,
                probe,
                process,
                "Windows Installer reported ERROR_INSTALL_ALREADY_RUNNING; the exact state is unchanged.");

        return new WindowsMsiMutationResult(
            pin.Identity,
            WindowsMsiMutationDisposition.ManualRecoveryRequired,
            probe,
            process,
            $"msiexec outcome and exact desired state were not both proven. Exact probe: {DescribeProbe(probe, probeFailure)}");
    }

    private static WindowsMsiMutationResult Success(
        LegacyUpgradePin pin,
        ExactWindowsMsiInspection probe,
        WindowsMsiProcessResult process,
        bool rebootRequired) =>
        new(
            pin.Identity,
            rebootRequired
                ? WindowsMsiMutationDisposition.SucceededRebootRequired
                : WindowsMsiMutationDisposition.Succeeded,
            probe,
            process,
            rebootRequired
                ? "The exact desired MSI state was verified; Windows requested a reboot."
                : "The exact desired MSI state was verified.");

    private static WindowsMsiMutationResult NoProcessResult(
        LegacyUpgradePin pin,
        WindowsMsiMutationDisposition disposition,
        ExactWindowsMsiInspection inspection,
        string detail) =>
        new(
            pin.Identity,
            disposition,
            inspection,
            new WindowsMsiProcessResult(WindowsMsiProcessCompletion.Exited, ErrorSuccess),
            detail);

    private async ValueTask<WindowsMsiProcessResult> RunSafelyAsync(
        WindowsMsiProcessRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new WindowsMsiProcessResult(
                WindowsMsiProcessCompletion.Cancelled,
                Failure: "The process runner cancelled without a final exit code.");
        }
        catch (Exception error) when (error is SystemException or InvalidOperationException)
        {
            return new WindowsMsiProcessResult(
                WindowsMsiProcessCompletion.StartFailed,
                Failure: error.GetType().Name);
        }
    }

    private void ValidateProfile(LegacyApplicationKind kind, WindowsMsiRegistration registration)
    {
        // Both legacy MSI pins are current-user unmanaged products. Keep one profile policy for
        // initial inspection and every post-mutation probe; do not infer context from product kind.
        var valid = kind is LegacyApplicationKind.StructuraConnector or LegacyApplicationKind.PlatformConnector &&
            registration.Context == WindowsMsiRegistrationContext.UserUnmanaged &&
            string.Equals(registration.UserSid, _currentUserSid, StringComparison.OrdinalIgnoreCase);
        if (!valid)
            throw new WindowsMsiInvariantException(
                $"Windows Installer registration for {kind} belongs to the wrong context or user profile.");
    }

    private static void ValidateDrainProof(LegacyProcessDrainProof proof, LegacyApplicationKind kind)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (proof.Kind != kind || !proof.Drained || string.IsNullOrWhiteSpace(proof.EvidenceId))
            throw new WindowsMsiInvariantException(
                $"A positive, exact process-drain proof is required before removing {kind}.");
    }

    private static void ValidateLease(IWindowsVerifiedRollbackPayloadLease lease, LegacyUpgradePin pin)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var inspection = lease.Inspection;
        Microsoft.Win32.SafeHandles.SafeFileHandle contentHandle;
        try
        {
            contentHandle = lease.ContentHandle;
        }
        catch (ObjectDisposedException error)
        {
            throw new WindowsMsiInvariantException(
                $"A live protected rollback lease matching the embedded lock is required for {pin.Kind}.",
                error);
        }
        if (lease.Protection != RollbackPayloadProtection.ProtectedMachineStaging ||
            inspection.Identity != pin.Identity ||
            !inspection.TrustedSource ||
            !string.Equals(inspection.PackageId, pin.PackageId, StringComparison.Ordinal) ||
            !string.Equals(inspection.InstallerName, pin.InstallerName, StringComparison.Ordinal) ||
            !string.Equals(inspection.Version, pin.Version, StringComparison.Ordinal) ||
            inspection.SizeBytes != pin.SizeBytes ||
            !string.Equals(inspection.Sha256, pin.Sha256, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(lease.StagedPath), pin.InstallerName, StringComparison.Ordinal) ||
            contentHandle.IsClosed ||
            contentHandle.IsInvalid)
            throw new WindowsMsiInvariantException(
                $"A live protected rollback lease matching the embedded lock is required for {pin.Kind}.");
    }

    private static string DescribeProbe(ExactWindowsMsiInspection? probe, string? failure) =>
        probe is null ? $"unavailable ({failure ?? "unknown failure"})" : probe.Presence.ToString();

    private static string FormatGuid(Guid value) => $"{{{value:D}}}".ToUpperInvariant();
}
