using Connector.Upgrade.Core;
using Connector.Upgrade.MachineCommandChannel;
using Connector.Upgrade.MachineDispatcher;
using Connector.Upgrade.MachineIpc;
using Connector.Upgrade.MachineJournal;

namespace Connector.Upgrade.OriginalUserMachineAdapter;

/// <summary>
/// Keeps original-user MSI, settings, enrollment and Velopack operations in the initiating process.
/// Only fixed, payload-free machine commands cross the authenticated helper channel.
/// </summary>
public sealed class MachineCommandUpgradePorts : IUpgradePorts, IUpgradeSessionBoundPorts,
    IUpgradeMachineVerifiedNetBirdPorts
{
    private readonly Guid _operationId;
    private readonly string _initiatingSid;
    private readonly IUpgradePorts _userPorts;
    private readonly IOriginalUserOperationRollbackPayloadOpener _rollbackOpener;
    private readonly IReadOnlyDictionary<LegacyApplicationKind, LegacyUpgradePin> _rollbackPins;
    private readonly Func<MachineIpcRequest, TimeSpan, CancellationToken, ValueTask<MachineDispatcherResult>> _send;
    private readonly TimeSpan _timeout;

    public MachineCommandUpgradePorts(Guid operationId, string initiatingSid,
        IUpgradePorts originalUserPorts, MachineCommandClient authenticatedMachineClient, TimeSpan timeout)
        : this(operationId, initiatingSid, originalUserPorts, BindClient(authenticatedMachineClient), timeout) { }

    internal MachineCommandUpgradePorts(Guid operationId, string initiatingSid,
        IUpgradePorts originalUserPorts,
        Func<MachineIpcRequest, TimeSpan, CancellationToken, ValueTask<MachineDispatcherResult>> send,
        TimeSpan timeout)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A trusted operation ID is required.", nameof(operationId));
        if (string.IsNullOrWhiteSpace(initiatingSid)) throw new ArgumentException("The initiating SID is required.", nameof(initiatingSid));
        if (timeout <= TimeSpan.Zero || timeout > MachineCommandClient.MaximumTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _operationId = operationId;
        _initiatingSid = initiatingSid;
        _userPorts = originalUserPorts ?? throw new ArgumentNullException(nameof(originalUserPorts));
        if (originalUserPorts is not IUpgradeSessionBoundPorts bound)
            throw new UnauthorizedAccessException("Original-user ports have no operation/SID binding.");
        bound.AssertSessionMatches(operationId.ToString("N"), initiatingSid);
        if (originalUserPorts is not IOriginalUserOperationRollbackPayloadOpener rollbackOpener)
            throw new UnauthorizedAccessException("Original-user ports do not provide the required existing-slot rollback opener.");
        rollbackOpener.AssertSessionMatches(operationId.ToString("N"), initiatingSid);
        _rollbackOpener = rollbackOpener;
        _rollbackPins = LegacyUpgradeLock.LoadEmbedded().Pins.ToDictionary(pin => pin.Kind);
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _timeout = timeout;
    }

    public void AssertSessionMatches(string operationId, string initiatingUserSid)
    {
        if (!string.Equals(operationId, _operationId.ToString("N"), StringComparison.Ordinal) ||
            !string.Equals(initiatingUserSid, _initiatingSid, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The machine adapter belongs to a different upgrade operation or user.");
        ((IUpgradeSessionBoundPorts)_userPorts).AssertSessionMatches(operationId, initiatingUserSid);
    }

    public async ValueTask<UpgradePreflight> InspectAsync(CancellationToken cancellationToken)
    {
        var machine = await CompletedAsync(MachineIpcOperation.Inspect, cancellationToken).ConfigureAwait(false);
        if (machine.State != MachineUpgradeState.InProgress || machine.Phase != MachineUpgradePhase.AssessNetBird)
            throw new MachineUpgradeCommandRejectedException("Machine inventory is not in the verified preflight phase.");
        if (machine.NetBirdOwnership is not { } ownership || !Enum.IsDefined(ownership))
            throw new MachineUpgradeCommandRejectedException("Machine inventory returned no trusted NetBird ownership.");
        var stage = await CompletedAsync(MachineIpcOperation.PrepareUserStateStage, cancellationToken).ConfigureAwait(false);
        if (stage.State != MachineUpgradeState.InProgress || stage.Phase != MachineUpgradePhase.AssessNetBird)
            throw new MachineUpgradeCommandRejectedException("Machine user-state stage was not durably prepared before preflight.");
        var preflight = await _userPorts.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (preflight.NetBird.InstallationId is not null || preflight.NetBird.OwnedState is not null)
            throw new MachineUpgradeCommandRejectedException(
                "Original-user preflight must contain NetBird ownership only.");
        if (preflight.NetBird.Ownership != ownership)
            throw new MachineUpgradeCommandRejectedException("Original-user and machine NetBird observations disagree.");
        return preflight;
    }

    public async ValueTask<NetBirdOperationIntent> PrepareOwnedNetBirdMutationAsync(
        NetBirdAssessment assessment, CancellationToken cancellationToken)
    {
        if (assessment.Ownership is not (NetBirdOwnership.Absent or NetBirdOwnership.OwnedByConnector))
            throw new MachineUpgradeCommandRejectedException("Foreign or unattributed NetBird may not be mutated.");
        var machine = await CompletedAsync(MachineIpcOperation.Prepare, cancellationToken).ConfigureAwait(false);
        if (machine.NetBirdOwnership != assessment.Ownership || machine.NetBirdChange is not { } change ||
            !Enum.IsDefined(change) || machine.State != MachineUpgradeState.InProgress)
            throw new MachineUpgradeCommandRejectedException("Machine NetBird preparation differs from the verified preflight.");
        return new NetBirdOperationIntent(_operationId.ToString("D"), assessment.Ownership, change);
    }

    public async ValueTask<NetBirdOperationReceipt> ApplyOwnedNetBirdMutationAsync(
        NetBirdOperationIntent operation, CancellationToken cancellationToken)
    {
        AssertOperation(operation);
        if (operation.Status != NetBirdOperationStatus.Prepared)
            throw new MachineUpgradeCommandRejectedException("Only a prepared NetBird operation may be applied.");
        var machine = await CompletedAsync(MachineIpcOperation.Apply, cancellationToken).ConfigureAwait(false);
        if (machine.NetBirdOwnership != operation.PriorOwnership || machine.NetBirdChange != operation.Change ||
            machine.State != MachineUpgradeState.InProgress)
            throw new MachineUpgradeCommandRejectedException("Machine NetBird apply receipt differs from the durable intent.");
        var resultingOwnership = operation.Change == NetBirdChangeKind.NoChange
            ? operation.PriorOwnership
            : NetBirdOwnership.OwnedByConnector;
        return new NetBirdOperationReceipt(operation.OperationId, operation.PriorOwnership,
            operation.Change, NetBirdOperationStatus.Applied, resultingOwnership, machine.RebootRequired);
    }

    public async ValueTask CompensateNetBirdMutationAsync(NetBirdOperationIntent operation,
        NetBirdOperationReceipt receipt, CancellationToken cancellationToken)
    {
        AssertOperation(operation);
        if (receipt.OperationId != operation.OperationId || receipt.Change != operation.Change ||
            receipt.PriorOwnership != operation.PriorOwnership || receipt.Status != NetBirdOperationStatus.Applied)
            throw new MachineUpgradeCommandRejectedException("NetBird compensation receipt does not match the operation.");
        if (operation.Change == NetBirdChangeKind.NoChange) return;
        var command = operation.Change == NetBirdChangeKind.InstalledThisRun
            ? MachineIpcOperation.Remove : MachineIpcOperation.Restore;
        var machine = await CompletedAsync(command, cancellationToken).ConfigureAwait(false);
        if (machine.NetBirdChange != operation.Change || machine.State != MachineUpgradeState.RolledBack)
            throw new MachineUpgradeCommandRejectedException("Machine NetBird compensation was not durably completed.");
    }

    public async ValueTask<NetBirdInterruptedRecoveryResult> ReconcileInterruptedNetBirdAsync(
        NetBirdOperationIntent operation, CancellationToken cancellationToken)
    {
        AssertOperation(operation);
        var machine = await CompletedAsync(MachineIpcOperation.Reconcile, cancellationToken).ConfigureAwait(false);
        if (machine.NetBirdChange != operation.Change || machine.State != MachineUpgradeState.RolledBack ||
            machine.ReconciliationAction is not { } action || !Enum.IsDefined(action) ||
            string.IsNullOrWhiteSpace(machine.ReconciliationEvidenceId))
            throw new MachineUpgradeCommandRejectedException("Machine NetBird reconciliation has no durable result.");
        return new NetBirdInterruptedRecoveryResult(action, machine.ReconciliationEvidenceId);
    }

    public async ValueTask<UnifiedApplicationReceipt> InstallUnifiedApplicationAsync(CancellationToken cancellationToken)
    {
        var machine = await CompletedAsync(MachineIpcOperation.StageVelopack, cancellationToken).ConfigureAwait(false);
        if (machine.State != MachineUpgradeState.InProgress)
            throw new MachineUpgradeCommandRejectedException("Machine Setup staging was not durably completed.");
        // The original-user port opens only an existing protected slot and rechecks signed bytes.
        return await _userPorts.InstallUnifiedApplicationAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask RemoveUnifiedApplicationAsync(UnifiedApplicationReceipt receipt, CancellationToken cancellationToken) =>
        _userPorts.RemoveUnifiedApplicationAsync(receipt, cancellationToken);
    public ValueTask<PlatformEnrollmentReceipt> EnrollAsync(OneTimePlatformToken token, CancellationToken cancellationToken) =>
        _userPorts.EnrollAsync(token, cancellationToken);
    public ValueTask RemoveNewEnrollmentAsync(PlatformEnrollmentReceipt receipt, CancellationToken cancellationToken) =>
        _userPorts.RemoveNewEnrollmentAsync(receipt, cancellationToken);
    public ValueTask<NewAccessVerification> VerifyNewAccessAsync(PlatformEnrollmentReceipt receipt, CancellationToken cancellationToken) =>
        _userPorts.VerifyNewAccessAsync(receipt, cancellationToken);
    public async ValueTask<IVerifiedRollbackPayloadLease> AcquireVerifiedRollbackPayloadAsync(
        LegacyRollbackPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Identity is null || !Enum.IsDefined(payload.Identity.Kind) ||
            !_rollbackPins.TryGetValue(payload.Identity.Kind, out var pin) ||
            !payload.Verified || payload.Identity != pin.Identity ||
            !string.Equals(payload.PackageId, pin.PackageId, StringComparison.Ordinal) ||
            !string.Equals(payload.InstallerName, pin.InstallerName, StringComparison.Ordinal) ||
            !string.Equals(payload.Version, pin.Version, StringComparison.Ordinal) || payload.SizeBytes != pin.SizeBytes ||
            !string.Equals(payload.Sha256, pin.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new MachineUpgradeCommandRejectedException("Rollback payload metadata differs from the embedded repository pin.");

        var operation = payload.Identity.Kind switch
        {
            LegacyApplicationKind.StructuraConnector => MachineIpcOperation.StageStructuraRollback,
            LegacyApplicationKind.PlatformConnector => MachineIpcOperation.StagePlatformRollback,
            _ => throw new MachineUpgradeCommandRejectedException("Rollback MSI kind is unsupported."),
        };
        var machine = await CompletedAsync(operation, cancellationToken).ConfigureAwait(false);
        if (machine.State != MachineUpgradeState.InProgress || machine.Phase != MachineUpgradePhase.AssessNetBird)
            throw new MachineUpgradeCommandRejectedException("Machine rollback MSI staging did not complete in the assessment phase.");

        var receipt = new ProtectedRollbackPayloadReceipt(
            FormatRollbackPayloadHandle(_operationId, payload.Identity.Kind),
            payload.Identity.Kind,
            RollbackPayloadProtection.ProtectedMachineStaging);
        return await ReopenAndValidateRollbackPayloadAsync(receipt, pin, cancellationToken).ConfigureAwait(false);
    }
    public ValueTask<LegacyRemovalReceipt> RemoveExactLegacyApplicationAsync(LegacyApplicationIdentity identity, CancellationToken cancellationToken) =>
        _userPorts.RemoveExactLegacyApplicationAsync(identity, cancellationToken);
    public ValueTask RestoreExactLegacyApplicationAsync(IVerifiedRollbackPayloadLease payloadLease,
        LegacyRemovalReceipt removal, CancellationToken cancellationToken) =>
        _userPorts.RestoreExactLegacyApplicationAsync(payloadLease, removal, cancellationToken);
    public ValueTask RestoreUserStateAsync(UserStateSnapshot snapshot, CancellationToken cancellationToken) =>
        _userPorts.RestoreUserStateAsync(snapshot, cancellationToken);
    public ValueTask<FinalUpgradeVerification> VerifyFinalStateAsync(CancellationToken cancellationToken) =>
        _userPorts.VerifyFinalStateAsync(cancellationToken);
    public ValueTask<InterruptedUpgradeInspection> InspectInterruptedUpgradeAsync(UpgradeRecoveryMetadata recovery,
        CancellationToken cancellationToken) => _userPorts.InspectInterruptedUpgradeAsync(recovery, cancellationToken);
    public ValueTask<IVerifiedRollbackPayloadLease> ReacquireVerifiedRollbackPayloadAsync(
        ProtectedRollbackPayloadReceipt durableReceipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(durableReceipt);
        if (!Enum.IsDefined(durableReceipt.Kind) || !_rollbackPins.TryGetValue(durableReceipt.Kind, out var pin) ||
            durableReceipt.Protection != RollbackPayloadProtection.ProtectedMachineStaging ||
            !string.Equals(durableReceipt.HandleId, FormatRollbackPayloadHandle(_operationId, durableReceipt.Kind),
                StringComparison.Ordinal))
            throw new MachineUpgradeCommandRejectedException("Durable rollback receipt is not bound to this operation, kind, and protected staging.");
        return ReopenAndValidateRollbackPayloadAsync(durableReceipt, pin, cancellationToken);
    }

    private async ValueTask<IVerifiedRollbackPayloadLease> ReopenAndValidateRollbackPayloadAsync(
        ProtectedRollbackPayloadReceipt receipt, LegacyUpgradePin pin, CancellationToken cancellationToken)
    {
        _rollbackOpener.AssertSessionMatches(_operationId.ToString("N"), _initiatingSid);
        var lease = await _rollbackOpener.OpenExistingRollbackPayloadAsync(receipt, cancellationToken)
            .ConfigureAwait(false);
        if (lease is null)
            throw new MachineUpgradeCommandRejectedException("Original-user rollback opener returned no lease.");
        try
        {
            var inspection = lease.Inspection;
            if (!string.Equals(lease.HandleId, receipt.HandleId, StringComparison.Ordinal) ||
                lease.Protection != RollbackPayloadProtection.ProtectedMachineStaging || !inspection.TrustedSource ||
                inspection.Identity != pin.Identity || inspection.Identity.Kind != receipt.Kind ||
                !string.Equals(inspection.PackageId, pin.PackageId, StringComparison.Ordinal) ||
                !string.Equals(inspection.InstallerName, pin.InstallerName, StringComparison.Ordinal) ||
                !string.Equals(inspection.Version, pin.Version, StringComparison.Ordinal) ||
                inspection.SizeBytes != pin.SizeBytes ||
                !string.Equals(inspection.Sha256, pin.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new MachineUpgradeCommandRejectedException("Original-user rollback opener returned content outside the protected embedded pin.");
            return lease;
        }
        catch
        {
            try { await lease.DisposeAsync().ConfigureAwait(false); }
            catch { /* Preserve the validation failure. */ }
            throw;
        }
    }

    private static string FormatRollbackPayloadHandle(Guid operationId, LegacyApplicationKind kind) =>
        $"windows-msi-op-v1:{operationId:N}:{(int)kind}";
    public ValueTask RestoreMissingLegacyApplicationAsync(IVerifiedRollbackPayloadLease payloadLease,
        LegacyRecoveryObservation absentApplication, CancellationToken cancellationToken) =>
        _userPorts.RestoreMissingLegacyApplicationAsync(payloadLease, absentApplication, cancellationToken);

    private void AssertOperation(NetBirdOperationIntent operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!string.Equals(operation.OperationId, _operationId.ToString("D"), StringComparison.Ordinal) ||
            !Enum.IsDefined(operation.PriorOwnership) || !Enum.IsDefined(operation.Change))
            throw new MachineUpgradeCommandRejectedException("NetBird operation does not belong to this machine session.");
    }

    private async ValueTask<MachineDispatcherResult> CompletedAsync(MachineIpcOperation operation, CancellationToken token)
    {
        var request = new MachineIpcRequest(MachineIpcFrameCodec.CurrentVersion, Guid.NewGuid(), operation);
        var result = await _send(request, _timeout, token).ConfigureAwait(false);
        if (result is null)
            throw new MachineUpgradeCommandRejectedException($"Machine command {operation} returned no result.");
        if (result.Version != request.Version || result.CorrelationId != request.CorrelationId ||
            result.Operation != request.Operation || result.Status != MachineDispatcherStatus.Completed ||
            result.Code != MachineDispatcherCode.None || result.State == MachineUpgradeState.NeedsManualRecovery)
            throw new MachineUpgradeCommandRejectedException(
                $"Machine command {operation} was not durably completed ({result.Status}/{result.Code}).");
        if (operation == MachineIpcOperation.PrepareUserStateStage &&
            (result.State != MachineUpgradeState.InProgress || result.Phase != MachineUpgradePhase.AssessNetBird))
            throw new MachineUpgradeCommandRejectedException("Machine user-state stage is outside the allowed preflight phase.");
        return result;
    }

    private static Func<MachineIpcRequest, TimeSpan, CancellationToken, ValueTask<MachineDispatcherResult>> BindClient(
        MachineCommandClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return (request, limit, token) => client.SendAsync(request, limit, token);
    }
}

public sealed class MachineUpgradeCommandRejectedException(string message) : InvalidOperationException(message);
