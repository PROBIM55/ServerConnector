using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;
using Connector.Upgrade.Velopack;
using Connector.Upgrade.VelopackPayload;
using System.Text.Json;

namespace Connector.Upgrade.WindowsHost;

public sealed class WindowsHostNetBirdPort(
    NetBirdMachineInstallService service,
    INetBirdMachineStatePort state,
    string sourceMsiPath) : IWindowsHostNetBirdPort
{
    private readonly NetBirdMachineInstallService _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly INetBirdMachineStatePort _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly string _sourceMsiPath = Path.IsPathFullyQualified(sourceMsiPath)
        ? Path.GetFullPath(sourceMsiPath)
        : throw new ArgumentException("The NetBird MSI source path must be absolute.", nameof(sourceMsiPath));

    public async ValueTask<NetBirdAssessment> InspectAsync(CancellationToken cancellationToken)
    {
        var inspection = await _state.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!inspection.InspectionComplete || string.IsNullOrWhiteSpace(inspection.EvidenceId))
            throw new NetBirdMachineInvariantException("NetBird machine inventory is incomplete.");
        return inspection.Assessment;
    }

    public ValueTask<NetBirdMutationPlan> PrepareAsync(
        NetBirdAssessment assessment,
        Guid operationId,
        CancellationToken cancellationToken) =>
        _service.PrepareOwnedMutationAsync(assessment, operationId, _sourceMsiPath, cancellationToken);

    public ValueTask<NetBirdMutationReceipt> ApplyAsync(
        NetBirdMutationPlan plan,
        CancellationToken cancellationToken) =>
        _service.ApplyPreparedMutationAsync(plan, cancellationToken);

    public ValueTask<NetBirdInterruptedRecoveryResult> ReconcileAsync(
        NetBirdMutationPlan plan,
        CancellationToken cancellationToken) =>
        _service.ReconcileInterruptedMutationAsync(plan, cancellationToken);

    public ValueTask RemoveInstalledThisRunAsync(
        NetBirdInstalledThisRunReceipt receipt,
        CancellationToken cancellationToken) =>
        _service.RemoveInstalledThisRunAsync(receipt, cancellationToken);

    public ValueTask RestoreUpdatedThisRunAsync(
        NetBirdUpdatedThisRunReceipt receipt,
        CancellationToken cancellationToken) =>
        _service.RestoreUpdatedThisRunAsync(receipt, cancellationToken);
}

public sealed class VelopackUnifiedApplicationPort : IWindowsHostUnifiedApplicationPort
{
    private static readonly JsonSerializerOptions RecoveryJsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
    };
    private const int MaxRecoveryMetadataChars = 64 * 1024;
    private readonly IWindowsVelopackSetupStager _stager;
    private readonly VelopackSetupPin _pin;
    private readonly IVelopackInstallProbe _probe;
    private readonly VelopackUnifiedApplicationService _service;
    private readonly string _currentUserSid;
    private readonly Dictionary<string, VelopackInstallReceipt> _liveReceipts = new(StringComparer.Ordinal);

    public VelopackUnifiedApplicationPort(
        IWindowsVelopackSetupStager stager,
        VelopackSetupPin pin,
        IVelopackInstallProbe probe,
        IVelopackProcessRunner processRunner,
        string currentUserSid,
        string localApplicationData,
        TimeSpan timeout)
    {
        _stager = stager ?? throw new ArgumentNullException(nameof(stager));
        _pin = pin ?? throw new ArgumentNullException(nameof(pin));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _currentUserSid = string.IsNullOrWhiteSpace(currentUserSid)
            ? throw new ArgumentException("The current Windows SID is required.", nameof(currentUserSid))
            : currentUserSid;
        _service = new VelopackUnifiedApplicationService(
            _probe,
            processRunner,
            _currentUserSid,
            localApplicationData,
            timeout);
    }

    public async ValueTask<UnifiedApplicationReceipt> InstallAsync(CancellationToken cancellationToken)
    {
        await using var lease = await _stager.StageAndVerifyAsync(cancellationToken).ConfigureAwait(false);
        if (lease is not IWindowsVerifiedVelopackSetupLease windowsLease)
            throw new VelopackInstallInvariantException(
                "Setup.exe requires a Windows lease with exact launch-path identity verification.");

        var result = await _service
            .InstallAsync(_pin, windowsLease, _currentUserSid, cancellationToken)
            .ConfigureAwait(false);
        if (result.Disposition is not (VelopackMutationDisposition.Succeeded or
                                       VelopackMutationDisposition.SucceededRebootRequired) ||
            result.Receipt is null)
        {
            throw new WindowsHostManualRecoveryRequiredException(
                "Velopack Setup.exe did not produce an exact compensatable install receipt.");
        }

        var metadata = new WindowsHostVelopackRecoveryMetadata(
            result.Receipt.OperationId,
            result.Receipt.SetupPin,
            result.Receipt.InstalledIdentity);
        var recoveryJson = JsonSerializer.Serialize(metadata, RecoveryJsonOptions);
        if (recoveryJson.Length > MaxRecoveryMetadataChars)
            throw new WindowsHostManualRecoveryRequiredException("Velopack recovery metadata exceeds the journal limit.");
        _liveReceipts.Add(result.Receipt.OperationId, result.Receipt);
        return new UnifiedApplicationReceipt(
            result.Receipt.OperationId,
            recoveryJson,
            result.Disposition == VelopackMutationDisposition.SucceededRebootRequired);
    }

    public async ValueTask RemoveAsync(
        UnifiedApplicationReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var metadata = ReadRecoveryMetadata(receipt);
        if (!_liveReceipts.TryGetValue(receipt.OperationId, out var exact))
            exact = VelopackInstallReceipt.FromProtectedJournal(
                metadata.OperationId, metadata.SetupPin, metadata.InstalledIdentity);
        var result = await _service.RollbackAsync(exact, cancellationToken).ConfigureAwait(false);
        if (result.Disposition is not (VelopackMutationDisposition.Succeeded or
                                       VelopackMutationDisposition.SucceededRebootRequired or
                                       VelopackMutationDisposition.AlreadyInDesiredState))
        {
            throw new WindowsHostManualRecoveryRequiredException(
                "The exact Velopack install could not be proven absent during compensation.");
        }
        _liveReceipts.Remove(receipt.OperationId);
    }

    public ValueTask<bool> IsExactInstallReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observation = _probe.Inspect(_pin, _currentUserSid);
        var ready = observation.Presence == VelopackInstallPresence.ExactInstalled &&
                    observation.Identity is not null &&
                    _liveReceipts.Values.Any(receipt => receipt.InstalledIdentity == observation.Identity);
        return ValueTask.FromResult(ready);
    }

    public WindowsHostVelopackRecoveryMetadata? GetRecoveryMetadata(UnifiedApplicationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return ReadRecoveryMetadata(receipt);
    }

    private WindowsHostVelopackRecoveryMetadata ReadRecoveryMetadata(UnifiedApplicationReceipt receipt)
    {
        if (string.IsNullOrWhiteSpace(receipt.RecoveryMetadataJson) ||
            receipt.RecoveryMetadataJson.Length > MaxRecoveryMetadataChars)
            throw new WindowsHostManualRecoveryRequiredException("The persisted Velopack receipt is missing or oversized.");
        WindowsHostVelopackRecoveryMetadata metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<WindowsHostVelopackRecoveryMetadata>(
                receipt.RecoveryMetadataJson, RecoveryJsonOptions)
                ?? throw new JsonException("Velopack receipt is empty.");
        }
        catch (JsonException error)
        {
            throw new WindowsHostManualRecoveryRequiredException("The persisted Velopack receipt is invalid.", error);
        }
        if (metadata.SetupPin is null || metadata.InstalledIdentity is null ||
            metadata.InstalledIdentity.Registration is null ||
            !string.Equals(metadata.OperationId, receipt.OperationId, StringComparison.Ordinal) ||
            !string.Equals(metadata.InstalledIdentity.UserSid, _currentUserSid, StringComparison.OrdinalIgnoreCase) ||
            metadata.SetupPin != _pin ||
            metadata.InstalledIdentity.PackId != _pin.PackId ||
            metadata.InstalledIdentity.Version != _pin.Version)
            throw new WindowsHostManualRecoveryRequiredException("The persisted Velopack receipt changed identity.");
        return metadata;
    }
}
