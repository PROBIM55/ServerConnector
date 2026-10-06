using Connector.Upgrade.Core;
using Connector.Upgrade.MachineIpc;
using Connector.Upgrade.MachineJournal;
using Connector.Upgrade.VelopackPayload;
using Connector.Upgrade.WindowsHost;
using Connector.Upgrade.WindowsPayload;
using Connector.Upgrade.WindowsUserStage;

namespace Connector.Upgrade.MachineDispatcher;

/// <summary>
/// In-process dispatcher for fixed machine upgrade requests. Operation identity and initiating SID are trusted
/// construction context and are never read from an IPC request.
/// </summary>
public sealed class MachineUpgradeDispatcher : IDisposable
{
    private readonly Guid _operationId;
    private readonly string _initiatingSid;
    private readonly IMachineUpgradeJournal _journal;
    private readonly IWindowsHostNetBirdPort _netBird;
    private readonly IWindowsVelopackSetupStager? _velopackSetupStager;
    private readonly Action<string, string>? _prepareUserStateStage;
    private readonly IWindowsOperationRollbackPayloadStager? _rollbackPayloadStager;
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private int _disposed;

    public MachineUpgradeDispatcher(
        Guid operationId,
        string initiatingSid,
        IMachineUpgradeJournal journal,
        IWindowsHostNetBirdPort netBird,
        IWindowsVelopackSetupStager? velopackSetupStager = null,
        Action<string, string>? prepareUserStateStage = null,
        IWindowsOperationRollbackPayloadStager? rollbackPayloadStager = null)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A trusted operation id is required.", nameof(operationId));
        if (string.IsNullOrWhiteSpace(initiatingSid) || initiatingSid.Length > 184)
            throw new ArgumentException("A trusted initiating SID is required.", nameof(initiatingSid));
        _operationId = operationId;
        _initiatingSid = initiatingSid;
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _netBird = netBird ?? throw new ArgumentNullException(nameof(netBird));
        _velopackSetupStager = velopackSetupStager;
        _prepareUserStateStage = prepareUserStateStage;
        _rollbackPayloadStager = rollbackPayloadStager;
    }

    public async ValueTask<MachineDispatcherResult> DispatchAsync(
        MachineIpcRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(MachineUpgradeDispatcher));
        if (request is null || request.Version != MachineIpcFrameCodec.CurrentVersion || request.CorrelationId == Guid.Empty ||
            !Enum.IsDefined(request.Operation))
            return Result(request, MachineDispatcherStatus.Blocked, MachineDispatcherCode.InvalidRequest);

        if (request.Operation is not (MachineIpcOperation.Inspect or MachineIpcOperation.Prepare or
            MachineIpcOperation.Apply or MachineIpcOperation.Reconcile or MachineIpcOperation.Remove or
            MachineIpcOperation.Restore or MachineIpcOperation.StageVelopack or MachineIpcOperation.PrepareUserStateStage or
            MachineIpcOperation.StageStructuraRollback or MachineIpcOperation.StagePlatformRollback or MachineIpcOperation.Rollover))
            return Result(request, MachineDispatcherStatus.Blocked, MachineDispatcherCode.UnsupportedOperation);
        if ((request.Operation == MachineIpcOperation.Rollover &&
             (request.PreviousOperationId is not { } previous || previous == Guid.Empty ||
              request.NextOperationId != _operationId || previous == _operationId)) ||
            (request.Operation != MachineIpcOperation.Rollover &&
             (request.PreviousOperationId is not null || request.NextOperationId is not null)))
            return Result(request, MachineDispatcherStatus.Blocked, MachineDispatcherCode.InvalidRequest);

        try
        {
            await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result(request, MachineDispatcherStatus.Cancelled, MachineDispatcherCode.Cancelled);
        }

        try
        {
            return request.Operation switch
            {
                MachineIpcOperation.Inspect => await InspectAsync(request, cancellationToken).ConfigureAwait(false),
                MachineIpcOperation.Prepare => await PrepareAsync(request, cancellationToken).ConfigureAwait(false),
                MachineIpcOperation.Apply => await ApplyAsync(request, cancellationToken).ConfigureAwait(false),
                MachineIpcOperation.Reconcile => await ReconcileAsync(request, cancellationToken).ConfigureAwait(false),
                MachineIpcOperation.Remove => await CompensateAsync(request, MachineNetBirdCompensationAction.RemoveInstalledThisRun, cancellationToken).ConfigureAwait(false),
                MachineIpcOperation.Restore => await CompensateAsync(request, MachineNetBirdCompensationAction.RestoreUpdatedThisRun, cancellationToken).ConfigureAwait(false),
                MachineIpcOperation.StageVelopack => await StageVelopackAsync(request, cancellationToken).ConfigureAwait(false),
                MachineIpcOperation.PrepareUserStateStage => await PrepareUserStateStageAsync(request, cancellationToken).ConfigureAwait(false),
                MachineIpcOperation.StageStructuraRollback => await StageRollbackPayloadAsync(
                    request, LegacyApplicationKind.StructuraConnector, cancellationToken).ConfigureAwait(false),
                MachineIpcOperation.StagePlatformRollback => await StageRollbackPayloadAsync(
                    request, LegacyApplicationKind.PlatformConnector, cancellationToken).ConfigureAwait(false),
                MachineIpcOperation.Rollover => await RolloverAsync(request, cancellationToken).ConfigureAwait(false),
                _ => Result(request, MachineDispatcherStatus.Blocked, MachineDispatcherCode.UnsupportedOperation),
            };
        }
        catch (OperationCanceledException)
        {
            return Result(request, MachineDispatcherStatus.Cancelled, MachineDispatcherCode.Cancelled);
        }
        catch (UnauthorizedAccessException)
        {
            return Result(request, MachineDispatcherStatus.Blocked, MachineDispatcherCode.JournalOwnershipMismatch);
        }
        catch (InvalidOperationException)
        {
            return Result(request, MachineDispatcherStatus.Blocked, MachineDispatcherCode.JournalFailure);
        }
        catch (InvalidDataException)
        {
            return Result(request, MachineDispatcherStatus.Blocked, MachineDispatcherCode.JournalFailure);
        }
        catch (UserStateStageNeedsManualRecoveryException)
        {
            // Report only the bounded recovery code. Never expose the retained stage path or
            // exception details over the machine IPC result.
            return Result(request, MachineDispatcherStatus.Blocked, MachineDispatcherCode.NeedsManualRecovery);
        }
        catch (WindowsRollbackPayloadNeedsManualRecoveryException)
        {
            return Result(request, MachineDispatcherStatus.Blocked, MachineDispatcherCode.NeedsManualRecovery);
        }
        catch (Exception)
        {
            return Result(request, MachineDispatcherStatus.Blocked, MachineDispatcherCode.OperationFailed);
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    private async ValueTask<MachineDispatcherResult> RolloverAsync(
        MachineIpcRequest request, CancellationToken cancellationToken)
    {
        var next = new MachineUpgradeJournalDocument(
            MachineUpgradeJournalDocument.CurrentSchemaVersion, _operationId, _initiatingSid, 0,
            MachineUpgradeState.InProgress, MachineUpgradePhase.Preflight, null, null, null,
            new ProtectedVelopackReceiptIdentifiers(null, null));
        await using var lease = await _journal.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var active = await _journal.RolloverAsync(request.PreviousOperationId!.Value, next, _initiatingSid,
            cancellationToken).ConfigureAwait(false);
        return Snapshot(request, active, MachineDispatcherStatus.Completed, MachineDispatcherCode.None);
    }

    private async ValueTask<MachineDispatcherResult> PrepareUserStateStageAsync(
        MachineIpcRequest request, CancellationToken cancellationToken)
    {
        if (_prepareUserStateStage is null)
            return Result(request, MachineDispatcherStatus.Blocked, MachineDispatcherCode.UnsupportedOperation);

        await using var lease = await _journal.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var document = await GetOrInitializeAsync(cancellationToken).ConfigureAwait(false);
        if (document.State != MachineUpgradeState.InProgress ||
            document.Phase != MachineUpgradePhase.AssessNetBird || document.NetBirdAssessment is null)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.UserStateStageUnavailable);

        _prepareUserStateStage(_initiatingSid, _operationId.ToString("N"));
        return Snapshot(request, document, MachineDispatcherStatus.Completed, MachineDispatcherCode.None);
    }

    private async ValueTask<MachineDispatcherResult> StageVelopackAsync(
        MachineIpcRequest request,
        CancellationToken cancellationToken)
    {
        if (_velopackSetupStager is null)
            return Result(request, MachineDispatcherStatus.Blocked, MachineDispatcherCode.UnsupportedOperation);

        await using var lease = await _journal.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var document = await GetOrInitializeAsync(cancellationToken).ConfigureAwait(false);
        if (document.State != MachineUpgradeState.InProgress ||
            document.Phase > MachineUpgradePhase.InstallVelopack)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.VelopackStageUnavailable);

        if (document.VelopackReceipts.SetupHandleId is { } existingHandle)
        {
            // Resolve and reverify the protected slot from its opaque handle. No caller path or
            // rich receipt is required, and a restarted/replayed stage cannot copy a new slot.
            await using var existing = await _velopackSetupStager.ReacquireByHandleAsync(
                existingHandle, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existing.HandleId, existingHandle, StringComparison.Ordinal))
                throw new InvalidDataException("The staged Setup handle changed during reacquisition.");
            return Snapshot(request, document, MachineDispatcherStatus.Completed, MachineDispatcherCode.None);
        }

        string handleId;
        await using (var staged = await _velopackSetupStager.StageAndVerifyAsync(cancellationToken).ConfigureAwait(false))
            handleId = staged.HandleId;
        if (!IsSafeSetupHandle(handleId))
            throw new InvalidDataException("The Setup stager returned an invalid opaque handle.");

        var next = document with
        {
            Revision = document.Revision + 1,
            VelopackReceipts = document.VelopackReceipts with { SetupHandleId = handleId },
        };
        // Staging already changed protected machine state. Commit its handle even if cancellation
        // arrives now so a retry reacquires this exact slot instead of staging another copy.
        await _journal.SaveAsync(_operationId, _initiatingSid, document.Revision, next, CancellationToken.None)
            .ConfigureAwait(false);
        return Snapshot(request, next, MachineDispatcherStatus.Completed, MachineDispatcherCode.None);
    }

    private static bool IsSafeSetupHandle(string? value)
    {
        const string prefix = "windows-velopack-setup-v1:";
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var parts = value[prefix.Length..].Split(':');
        return parts.Length == 3 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit) &&
            parts[1] == "stage" && Guid.TryParseExact(parts[2], "N", out _);
    }

    private async ValueTask<MachineDispatcherResult> StageRollbackPayloadAsync(
        MachineIpcRequest request, LegacyApplicationKind kind, CancellationToken cancellationToken)
    {
        if (_rollbackPayloadStager is null)
            return Result(request, MachineDispatcherStatus.Blocked, MachineDispatcherCode.UnsupportedOperation);

        await using var lease = await _journal.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var document = await GetOrInitializeAsync(cancellationToken).ConfigureAwait(false);
        if (document.State != MachineUpgradeState.InProgress ||
            document.Phase != MachineUpgradePhase.AssessNetBird || document.NetBirdAssessment is null)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.UserStateStageUnavailable);

        var expectedHandle = FormatRollbackPayloadHandle(_operationId, kind);
        IWindowsVerifiedRollbackPayloadLease staged;
        try
        {
            // Stage is also the idempotent replay path: it reopens and re-verifies an existing
            // immutable operation slot rather than selecting or accepting any caller path.
            staged = await _rollbackPayloadStager.StageVerifiedRollbackPayloadAsync(kind, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WindowsRollbackPayloadNeedsManualRecoveryException error)
        {
            if (error.Kind != kind) throw new InvalidDataException("The rollback stager reported a different MSI kind.");
            var manual = await MarkNeedsManualRecoveryAsync(document).ConfigureAwait(false);
            return Snapshot(request, manual, MachineDispatcherStatus.Blocked, MachineDispatcherCode.NeedsManualRecovery);
        }

        await using (staged.ConfigureAwait(false))
        {
            if (!string.Equals(staged.HandleId, expectedHandle, StringComparison.Ordinal) ||
                staged.Inspection.Identity.Kind != kind ||
                staged.Protection != RollbackPayloadProtection.ProtectedMachineStaging ||
                !staged.Inspection.TrustedSource)
                throw new InvalidDataException("The rollback stager returned an unbound or unprotected MSI lease.");

            var receipts = document.RollbackPayloadReceipts ?? new ProtectedRollbackPayloadReceiptIdentifiers();
            var previousHandle = kind == LegacyApplicationKind.StructuraConnector
                ? receipts.StructuraConnectorHandleId
                : receipts.PlatformConnectorHandleId;
            if (previousHandle is not null)
            {
                if (!string.Equals(previousHandle, staged.HandleId, StringComparison.Ordinal))
                    throw new InvalidDataException("The durable rollback MSI handle changed during replay.");
                return Snapshot(request, document, MachineDispatcherStatus.Completed, MachineDispatcherCode.None);
            }

            var nextReceipts = kind == LegacyApplicationKind.StructuraConnector
                ? receipts with { StructuraConnectorHandleId = staged.HandleId }
                : receipts with { PlatformConnectorHandleId = staged.HandleId };
            var next = document with
            {
                Revision = document.Revision + 1,
                RollbackPayloadReceipts = nextReceipts,
            };
            // Once the protected stage has returned, persist its opaque handle even if the
            // request token was cancelled so a retry can only adopt this exact operation slot.
            await _journal.SaveAsync(_operationId, _initiatingSid, document.Revision, next, CancellationToken.None)
                .ConfigureAwait(false);
            return Snapshot(request, next, MachineDispatcherStatus.Completed, MachineDispatcherCode.None);
        }
    }

    private static string FormatRollbackPayloadHandle(Guid operationId, LegacyApplicationKind kind) =>
        $"windows-msi-op-v1:{operationId:N}:{(int)kind}";

    private async ValueTask<MachineDispatcherResult> InspectAsync(MachineIpcRequest request, CancellationToken cancellationToken)
    {
        await using var lease = await _journal.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var document = await GetOrInitializeAsync(cancellationToken).ConfigureAwait(false);

        if (document.NetBirdAssessment is not null)
            return Completed(request, document, document.NetBirdAssessment.Ownership, document.NetBirdMutationPlan?.Change);
        if (document.Phase != MachineUpgradePhase.Preflight)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.PreparationAlreadyStarted);

        var assessment = await _netBird.InspectAsync(cancellationToken).ConfigureAwait(false);
        var next = document with
        {
            Revision = document.Revision + 1,
            Phase = MachineUpgradePhase.AssessNetBird,
            NetBirdAssessment = assessment,
        };
        // Once a live assessment succeeds, cancellation cannot turn a successful operation into a replay.
        await _journal.SaveAsync(_operationId, _initiatingSid, document.Revision, next, CancellationToken.None).ConfigureAwait(false);
        return Completed(request, next, assessment.Ownership);
    }

    private async ValueTask<MachineDispatcherResult> PrepareAsync(MachineIpcRequest request, CancellationToken cancellationToken)
    {
        await using var lease = await _journal.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var document = await GetOrInitializeAsync(cancellationToken).ConfigureAwait(false);

        if (document.State == MachineUpgradeState.NeedsManualRecovery)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.NeedsManualRecovery,
                document.NetBirdAssessment?.Ownership, document.NetBirdMutationPlan?.Change);
        if (document.NetBirdMutationPlan is not null)
        {
            if (document.NetBirdAssessment is null || !IsPlanBound(document.NetBirdMutationPlan, document.NetBirdAssessment))
                return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.InvalidMutationPlan,
                    document.NetBirdAssessment?.Ownership, document.NetBirdMutationPlan.Change);
            return document.State == MachineUpgradeState.InProgress && document.Phase == MachineUpgradePhase.PrepareNetBirdMutation
                ? Completed(request, document, document.NetBirdAssessment.Ownership, document.NetBirdMutationPlan.Change)
                : Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.PreparationAlreadyStarted,
                    document.NetBirdAssessment.Ownership, document.NetBirdMutationPlan.Change);
        }
        if (document.NetBirdAssessment is null)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.AssessmentRequired);
        if (document.NetBirdAssessment.Ownership is NetBirdOwnership.Foreign or NetBirdOwnership.Unattributed)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.UnsafeNetBirdOwnership,
                document.NetBirdAssessment.Ownership);
        if (document.Phase != MachineUpgradePhase.AssessNetBird)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.PreparationAlreadyStarted);

        // Persist intent first. If Prepare has begun but its plan was not saved, retries fail closed.
        var checkpoint = document with
        {
            Revision = document.Revision + 1,
            Phase = MachineUpgradePhase.PrepareNetBirdMutation,
        };
        await _journal.SaveAsync(_operationId, _initiatingSid, document.Revision, checkpoint, cancellationToken).ConfigureAwait(false);

        NetBirdMutationPlan plan;
        try
        {
            plan = await _netBird.PrepareAsync(document.NetBirdAssessment, _operationId, cancellationToken).ConfigureAwait(false);
            if (!IsPlanBound(plan, document.NetBirdAssessment))
                throw new InvalidDataException("NetBird preparation did not return a supported plan bound to the assessment.");
        }
        catch (Exception)
        {
            // The port may have staged a protected package or restore point before failing.
            // Without a durable plan there is no safe retry contract, so make the operation terminal.
            var manual = await MarkNeedsManualRecoveryAsync(checkpoint).ConfigureAwait(false);
            return Snapshot(request, manual, MachineDispatcherStatus.Blocked, MachineDispatcherCode.NeedsManualRecovery,
                document.NetBirdAssessment.Ownership);
        }
        var prepared = checkpoint with { Revision = checkpoint.Revision + 1, NetBirdMutationPlan = plan };
        // Prepare may create protected staging. Commit the returned plan even if cancellation arrived
        // after the port completed, so a duplicate request can return the same durable result.
        await _journal.SaveAsync(_operationId, _initiatingSid, checkpoint.Revision, prepared, CancellationToken.None).ConfigureAwait(false);
        return Completed(request, prepared, document.NetBirdAssessment.Ownership, plan.Change);
    }

    private async ValueTask<MachineDispatcherResult> ApplyAsync(MachineIpcRequest request, CancellationToken cancellationToken)
    {
        await using var lease = await _journal.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var document = await GetOrInitializeAsync(cancellationToken).ConfigureAwait(false);

        if (document.RecoveryReceipt is not null)
            return document.State == MachineUpgradeState.NeedsManualRecovery
                ? Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.NeedsManualRecovery,
                    document.NetBirdAssessment?.Ownership, document.RecoveryReceipt.Change)
                : Completed(request, document, document.NetBirdAssessment?.Ownership, document.RecoveryReceipt.Change);
        if (document.NetBirdReconciliation is not null)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.NeedsManualRecovery,
                document.NetBirdAssessment?.Ownership, document.NetBirdMutationPlan?.Change,
                document.NetBirdReconciliation.Action, document.NetBirdReconciliation.EvidenceId);
        if (document.NetBirdMutationPlan is null || document.NetBirdAssessment is null)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.AssessmentRequired);
        if (!HasTrustedOperationId(document.NetBirdMutationPlan, document.NetBirdAssessment))
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.InvalidMutationPlan,
                document.NetBirdAssessment.Ownership, document.NetBirdMutationPlan.Change);
        if (!IsPlanBound(document.NetBirdMutationPlan, document.NetBirdAssessment))
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.InvalidMutationPlan,
                document.NetBirdAssessment.Ownership, document.NetBirdMutationPlan.Change);
        if (document.State != MachineUpgradeState.InProgress)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.NeedsManualRecovery,
                document.NetBirdAssessment.Ownership, document.NetBirdMutationPlan.Change);
        if (document.Phase == MachineUpgradePhase.MutateNetBird)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.ReconciliationRequired,
                document.NetBirdAssessment.Ownership, document.NetBirdMutationPlan.Change);
        if (document.Phase != MachineUpgradePhase.PrepareNetBirdMutation)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.PreparationAlreadyStarted,
                document.NetBirdAssessment.Ownership, document.NetBirdMutationPlan.Change);

        // The durable phase is the mutation intent. After this save, no retry may invoke MSI again
        // unless a later Reconcile proves and records the outcome.
        var intent = document with { Revision = document.Revision + 1, Phase = MachineUpgradePhase.MutateNetBird };
        await _journal.SaveAsync(_operationId, _initiatingSid, document.Revision, intent, cancellationToken).ConfigureAwait(false);

        var receipt = await _netBird.ApplyAsync(intent.NetBirdMutationPlan!, cancellationToken).ConfigureAwait(false);
        var durableReceipt = ToRecoveryReceipt(intent.NetBirdMutationPlan!, receipt);
        var applied = intent with { Revision = intent.Revision + 1, RecoveryReceipt = durableReceipt };
        // The port may finish its MSI work as cancellation arrives; persist its exact state receipt regardless.
        await _journal.SaveAsync(_operationId, _initiatingSid, intent.Revision, applied, CancellationToken.None).ConfigureAwait(false);
        return Completed(request, applied, intent.NetBirdAssessment!.Ownership, durableReceipt.Change);
    }

    private async ValueTask<MachineDispatcherResult> ReconcileAsync(MachineIpcRequest request, CancellationToken cancellationToken)
    {
        await using var lease = await _journal.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var document = await GetOrInitializeAsync(cancellationToken).ConfigureAwait(false);

        if (document.NetBirdCompensation is { Completed: true } completedCompensation)
            return completedCompensation.EvidenceId is { } completedEvidence
                ? Snapshot(request, document, MachineDispatcherStatus.Completed, MachineDispatcherCode.None,
                    document.NetBirdAssessment?.Ownership, document.RecoveryReceipt?.Change,
                    CompensationReconciliationAction(completedCompensation.Action), completedEvidence)
                : Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.MutationAlreadyApplied,
                    document.NetBirdAssessment?.Ownership, document.RecoveryReceipt?.Change);
        if (document.NetBirdCompensation is { Completed: false })
        {
            if (document.State == MachineUpgradeState.NeedsManualRecovery)
                return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.NeedsManualRecovery,
                    document.NetBirdAssessment?.Ownership, document.RecoveryReceipt?.Change);
            return await ReconcileCompensationAsync(request, document, cancellationToken).ConfigureAwait(false);
        }

        if (document.RecoveryReceipt is not null)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.MutationAlreadyApplied,
                document.NetBirdAssessment?.Ownership, document.RecoveryReceipt.Change);
        if (document.NetBirdReconciliation is not null)
        {
            if (document.State == MachineUpgradeState.NeedsManualRecovery)
                return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.NeedsManualRecovery,
                    document.NetBirdAssessment?.Ownership, document.NetBirdMutationPlan?.Change);
            if (document.State == MachineUpgradeState.RollingBack)
                return await CompleteReconciledRollbackAsync(request, document).ConfigureAwait(false);
            return document.State == MachineUpgradeState.RolledBack
                ? Snapshot(request, document, MachineDispatcherStatus.Completed, MachineDispatcherCode.None,
                    document.NetBirdAssessment?.Ownership, document.NetBirdMutationPlan?.Change,
                    document.NetBirdReconciliation.Action, document.NetBirdReconciliation.EvidenceId)
                : Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.ReconciliationRequired,
                    document.NetBirdAssessment?.Ownership, document.NetBirdMutationPlan?.Change);
        }
        if (document.NetBirdMutationPlan is null)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.AssessmentRequired);
        if (document.NetBirdAssessment?.Ownership is NetBirdOwnership.Foreign or NetBirdOwnership.Unattributed)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.UnsafeNetBirdOwnership,
                document.NetBirdAssessment.Ownership, document.NetBirdMutationPlan.Change);
        if (document.NetBirdAssessment is null || !IsPlanBound(document.NetBirdMutationPlan, document.NetBirdAssessment))
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.InvalidMutationPlan,
                document.NetBirdAssessment?.Ownership, document.NetBirdMutationPlan.Change);
        if (document.State == MachineUpgradeState.NeedsManualRecovery)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.NeedsManualRecovery,
                document.NetBirdAssessment?.Ownership, document.NetBirdMutationPlan.Change);
        if (document.Phase != MachineUpgradePhase.MutateNetBird)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.ReconciliationRequired,
                document.NetBirdAssessment?.Ownership, document.NetBirdMutationPlan.Change);

        NetBirdInterruptedRecoveryResult result;
        try
        {
            result = await _netBird.ReconcileAsync(document.NetBirdMutationPlan, cancellationToken).ConfigureAwait(false);
            if (!IsValidReconciliation(document.NetBirdMutationPlan, result))
                throw new InvalidDataException("NetBird reconciliation did not return a supported owned-state result.");
        }
        catch (OperationCanceledException)
        {
            var manual = await MarkNeedsManualRecoveryAsync(document).ConfigureAwait(false);
            return Snapshot(request, manual,
                MachineDispatcherStatus.Blocked, MachineDispatcherCode.NeedsManualRecovery,
                document.NetBirdAssessment?.Ownership, document.NetBirdMutationPlan.Change);
        }
        catch (Exception)
        {
            var manual = await MarkNeedsManualRecoveryAsync(document).ConfigureAwait(false);
            return Snapshot(request, manual, MachineDispatcherStatus.Blocked, MachineDispatcherCode.NeedsManualRecovery,
                document.NetBirdAssessment?.Ownership, document.NetBirdMutationPlan.Change);
        }

        var reconciled = document with
        {
            Revision = document.Revision + 1,
            State = MachineUpgradeState.RollingBack,
            Phase = MachineUpgradePhase.Rollback,
            NetBirdReconciliation = new NetBirdReconciliationReceipt(result.Action, result.EvidenceId),
        };
        await _journal.SaveAsync(_operationId, _initiatingSid, document.Revision, reconciled, CancellationToken.None).ConfigureAwait(false);
        return await CompleteReconciledRollbackAsync(request, reconciled).ConfigureAwait(false);
    }

    private async ValueTask<MachineDispatcherResult> CompleteReconciledRollbackAsync(
        MachineIpcRequest request,
        MachineUpgradeJournalDocument rollingBack)
    {
        var completed = rollingBack with
        {
            Revision = rollingBack.Revision + 1,
            State = MachineUpgradeState.RolledBack,
        };
        await _journal.SaveAsync(_operationId, _initiatingSid, rollingBack.Revision, completed, CancellationToken.None).ConfigureAwait(false);
        return Snapshot(request, completed, MachineDispatcherStatus.Completed, MachineDispatcherCode.None,
            rollingBack.NetBirdAssessment?.Ownership, rollingBack.NetBirdMutationPlan?.Change,
            rollingBack.NetBirdReconciliation!.Action, rollingBack.NetBirdReconciliation.EvidenceId);
    }

    private async ValueTask<MachineDispatcherResult> CompensateAsync(
        MachineIpcRequest request,
        MachineNetBirdCompensationAction action,
        CancellationToken cancellationToken)
    {
        await using var lease = await _journal.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var document = await GetOrInitializeAsync(cancellationToken).ConfigureAwait(false);

        if (document.NetBirdCompensation is { } existing)
            return existing.Action == action && existing.Completed
                ? Completed(request, document, document.NetBirdAssessment?.Ownership, document.RecoveryReceipt?.Change)
                : Snapshot(request, document, MachineDispatcherStatus.Blocked,
                    document.State == MachineUpgradeState.NeedsManualRecovery
                        ? MachineDispatcherCode.NeedsManualRecovery
                        : MachineDispatcherCode.ReconciliationRequired,
                    document.NetBirdAssessment?.Ownership, document.RecoveryReceipt?.Change);
        if (document.RecoveryReceipt is null || document.NetBirdMutationPlan is null || document.NetBirdAssessment is null)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.AssessmentRequired);
        if (document.RecoveryReceipt.Change == NetBirdChangeKind.NoChange)
            return Completed(request, document, document.NetBirdAssessment.Ownership, NetBirdChangeKind.NoChange);

        var expectedAction = document.RecoveryReceipt.Change switch
        {
            NetBirdChangeKind.InstalledThisRun => MachineNetBirdCompensationAction.RemoveInstalledThisRun,
            NetBirdChangeKind.UpdatedThisRun => MachineNetBirdCompensationAction.RestoreUpdatedThisRun,
            _ => (MachineNetBirdCompensationAction?)null,
        };
        if (expectedAction != action)
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.InvalidRequest,
                document.NetBirdAssessment.Ownership, document.RecoveryReceipt.Change);
        if (document.State != MachineUpgradeState.InProgress ||
            document.NetBirdAssessment.Ownership is NetBirdOwnership.Foreign or NetBirdOwnership.Unattributed ||
            !IsPlanBound(document.NetBirdMutationPlan, document.NetBirdAssessment) ||
            !IsRecoveryReceiptBound(document))
            return Snapshot(request, document, MachineDispatcherStatus.Blocked, MachineDispatcherCode.NeedsManualRecovery,
                document.NetBirdAssessment.Ownership, document.RecoveryReceipt.Change);

        var rollbackPhase = document.Phase > MachineUpgradePhase.Rollback
            ? MachineUpgradePhase.RecoveryRollback
            : MachineUpgradePhase.Rollback;
        var intent = document with
        {
            Revision = document.Revision + 1,
            State = MachineUpgradeState.RollingBack,
            Phase = rollbackPhase,
            NetBirdCompensation = new MachineNetBirdCompensation(action, Completed: false),
        };
        await _journal.SaveAsync(_operationId, _initiatingSid, document.Revision, intent, CancellationToken.None).ConfigureAwait(false);

        try
        {
            if (action == MachineNetBirdCompensationAction.RemoveInstalledThisRun)
            {
                var receipt = new NetBirdInstalledThisRunReceipt(
                    document.RecoveryReceipt.OperationId!, document.RecoveryReceipt.ResultingOwnedState!);
                await _netBird.RemoveInstalledThisRunAsync(receipt, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var plan = document.NetBirdMutationPlan;
                var durable = document.RecoveryReceipt;
                var restorePoint = plan.PriorRestorePoint! with
                {
                    HandleId = durable.PriorRestoreHandleId!,
                    Protection = durable.PriorRestoreProtection!.Value,
                    OwnedState = durable.PriorOwnedState!,
                    Package = durable.PriorPackage!,
                    InstallerSha256 = durable.PriorInstallerSha256!,
                };
                var receipt = new NetBirdUpdatedThisRunReceipt(
                    durable.OperationId!, restorePoint, durable.ResultingOwnedState!);
                await _netBird.RestoreUpdatedThisRunAsync(receipt, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            var manual = await MarkNeedsManualRecoveryAsync(intent).ConfigureAwait(false);
            return Snapshot(request, manual, MachineDispatcherStatus.Blocked, MachineDispatcherCode.NeedsManualRecovery,
                document.NetBirdAssessment.Ownership, document.RecoveryReceipt.Change);
        }

        var completed = intent with
        {
            Revision = intent.Revision + 1,
            State = MachineUpgradeState.RolledBack,
            NetBirdCompensation = intent.NetBirdCompensation! with { Completed = true },
        };
        await _journal.SaveAsync(_operationId, _initiatingSid, intent.Revision, completed, CancellationToken.None).ConfigureAwait(false);
        return Completed(request, completed, document.NetBirdAssessment.Ownership, document.RecoveryReceipt.Change);
    }

    private async ValueTask<MachineDispatcherResult> ReconcileCompensationAsync(
        MachineIpcRequest request,
        MachineUpgradeJournalDocument document,
        CancellationToken cancellationToken)
    {
        if (document.NetBirdCompensation is not { Completed: false } intent || document.NetBirdMutationPlan is null ||
            document.NetBirdAssessment is null || document.RecoveryReceipt is null || !IsRecoveryReceiptBound(document) ||
            document.NetBirdAssessment.Ownership is NetBirdOwnership.Foreign or NetBirdOwnership.Unattributed ||
            !IsPlanBound(document.NetBirdMutationPlan, document.NetBirdAssessment))
            return await ManualRecoveryResultAsync(request, document).ConfigureAwait(false);

        try
        {
            var result = await _netBird.ReconcileAsync(document.NetBirdMutationPlan, cancellationToken).ConfigureAwait(false);
            var expected = intent.Action switch
            {
                MachineNetBirdCompensationAction.RemoveInstalledThisRun =>
                    result.Action is NetBirdInterruptedRecoveryAction.RemovedInstalledThisRun or NetBirdInterruptedRecoveryAction.NoMutationObserved,
                MachineNetBirdCompensationAction.RestoreUpdatedThisRun =>
                    result.Action is NetBirdInterruptedRecoveryAction.RestoredPriorOwnedState or NetBirdInterruptedRecoveryAction.NoMutationObserved,
                _ => false,
            };
            if (!expected || !IsSafeEvidenceId(result.EvidenceId))
                return await ManualRecoveryResultAsync(request, document).ConfigureAwait(false);

            var completed = document with
            {
                Revision = document.Revision + 1,
                State = MachineUpgradeState.RolledBack,
                NetBirdCompensation = intent with { Completed = true, EvidenceId = result.EvidenceId },
            };
            await _journal.SaveAsync(_operationId, _initiatingSid, document.Revision, completed, CancellationToken.None).ConfigureAwait(false);
            return Snapshot(request, completed, MachineDispatcherStatus.Completed, MachineDispatcherCode.None,
                document.NetBirdAssessment.Ownership, document.RecoveryReceipt.Change,
                CompensationReconciliationAction(intent.Action), result.EvidenceId);
        }
        catch (Exception)
        {
            return await ManualRecoveryResultAsync(request, document).ConfigureAwait(false);
        }
    }

    private async ValueTask<MachineDispatcherResult> ManualRecoveryResultAsync(
        MachineIpcRequest request,
        MachineUpgradeJournalDocument document)
    {
        var manual = document.State == MachineUpgradeState.NeedsManualRecovery
            ? document
            : await MarkNeedsManualRecoveryAsync(document).ConfigureAwait(false);
        return Snapshot(request, manual, MachineDispatcherStatus.Blocked, MachineDispatcherCode.NeedsManualRecovery,
            document.NetBirdAssessment?.Ownership, document.RecoveryReceipt?.Change);
    }

    private static NetBirdInterruptedRecoveryAction CompensationReconciliationAction(MachineNetBirdCompensationAction action) =>
        action == MachineNetBirdCompensationAction.RemoveInstalledThisRun
            ? NetBirdInterruptedRecoveryAction.RemovedInstalledThisRun
            : NetBirdInterruptedRecoveryAction.RestoredPriorOwnedState;

    private bool IsRecoveryReceiptBound(MachineUpgradeJournalDocument document)
    {
        var receipt = document.RecoveryReceipt!;
        var plan = document.NetBirdMutationPlan!;
        return receipt.Change switch
        {
            NetBirdChangeKind.NoChange => plan.Change == NetBirdChangeKind.NoChange && receipt.OperationId is null &&
                receipt.ResultingOwnedState == plan.PriorAssessment.OwnedState,
            NetBirdChangeKind.InstalledThisRun => plan.Change == NetBirdChangeKind.InstalledThisRun &&
                string.Equals(receipt.OperationId, _operationId.ToString("D"), StringComparison.Ordinal) &&
                receipt.ResultingOwnedState is not null && receipt.PriorOwnedState is null &&
                receipt.PriorRestoreHandleId is null,
            NetBirdChangeKind.UpdatedThisRun => plan.Change == NetBirdChangeKind.UpdatedThisRun &&
                string.Equals(receipt.OperationId, _operationId.ToString("D"), StringComparison.Ordinal) &&
                receipt.ResultingOwnedState is not null && receipt.PriorOwnedState == plan.PriorRestorePoint?.OwnedState &&
                receipt.PriorRestoreHandleId == plan.PriorRestorePoint?.HandleId &&
                receipt.PriorRestoreProtection == plan.PriorRestorePoint?.Protection &&
                receipt.PriorPackage == plan.PriorRestorePoint?.Package &&
                receipt.PriorInstallerSha256 == plan.PriorRestorePoint?.InstallerSha256,
            _ => false,
        };
    }

    private async ValueTask<MachineUpgradeJournalDocument> MarkNeedsManualRecoveryAsync(MachineUpgradeJournalDocument document)
    {
        var manual = document with
        {
            Revision = document.Revision + 1,
            State = MachineUpgradeState.NeedsManualRecovery,
        };
        await _journal.SaveAsync(_operationId, _initiatingSid, document.Revision, manual, CancellationToken.None).ConfigureAwait(false);
        return manual;
    }

    private static bool IsValidReconciliation(NetBirdMutationPlan plan, NetBirdInterruptedRecoveryResult? result)
    {
        if (result is null || !IsSafeEvidenceId(result.EvidenceId)) return false;
        return (plan.Change, result.Action) switch
        {
            (NetBirdChangeKind.NoChange, NetBirdInterruptedRecoveryAction.NoMutationObserved) => true,
            (NetBirdChangeKind.InstalledThisRun, NetBirdInterruptedRecoveryAction.NoMutationObserved or
                NetBirdInterruptedRecoveryAction.RemovedInstalledThisRun) => true,
            (NetBirdChangeKind.UpdatedThisRun, NetBirdInterruptedRecoveryAction.NoMutationObserved or
                NetBirdInterruptedRecoveryAction.RestoredPriorOwnedState) => true,
            _ => false,
        };
    }

    private bool IsPlanBound(NetBirdMutationPlan plan, NetBirdAssessment assessment)
    {
        if (plan is null || assessment is null || plan.PriorAssessment != assessment ||
            !HasTrustedOperationId(plan, assessment))
            return false;
        return (assessment.Ownership, plan.Change) switch
        {
            (NetBirdOwnership.Absent, NetBirdChangeKind.InstalledThisRun) =>
                true,
            (NetBirdOwnership.OwnedByConnector, NetBirdChangeKind.NoChange) =>
                plan.PriorRestorePoint is null,
            (NetBirdOwnership.OwnedByConnector, NetBirdChangeKind.UpdatedThisRun) =>
                plan.PriorRestorePoint is not null,
            _ => false,
        };
    }

    private bool HasTrustedOperationId(NetBirdMutationPlan plan, NetBirdAssessment assessment) =>
        assessment.Ownership == NetBirdOwnership.OwnedByConnector &&
        plan.Change == NetBirdChangeKind.NoChange
            ? plan.OperationId is null
            : string.Equals(plan.OperationId, _operationId.ToString("D"), StringComparison.Ordinal);

    private static bool IsSafeEvidenceId(string? value)
    {
        const string prefix = "netbird-windows-v1:";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) return false;
        var parts = value.Split('|');
        return parts.Length is 1 or 2 && parts.All(part =>
            part.StartsWith(prefix, StringComparison.Ordinal) && part.Length == prefix.Length + 64 &&
            part.AsSpan(prefix.Length).ToString().All(Uri.IsHexDigit));
    }

    private static NetBirdRecoveryReceipt ToRecoveryReceipt(NetBirdMutationPlan plan, NetBirdMutationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return receipt switch
        {
            NetBirdNoChangeReceipt noChange when plan.Change == NetBirdChangeKind.NoChange =>
                new NetBirdRecoveryReceipt(noChange.Change, null, null, null, null, noChange.ExistingState),
            NetBirdInstalledThisRunReceipt installed when plan.Change == NetBirdChangeKind.InstalledThisRun &&
                string.Equals(installed.OperationId, plan.OperationId, StringComparison.Ordinal) =>
                new NetBirdRecoveryReceipt(installed.Change, installed.OperationId, null, null, null, installed.InstalledState,
                    RebootRequired: installed.RebootRequired),
            NetBirdUpdatedThisRunReceipt updated when plan.Change == NetBirdChangeKind.UpdatedThisRun &&
                string.Equals(updated.OperationId, plan.OperationId, StringComparison.Ordinal) =>
                new NetBirdRecoveryReceipt(updated.Change, updated.OperationId,
                    updated.PriorRestorePoint.HandleId, updated.PriorRestorePoint.Protection,
                    updated.PriorOwnedState, updated.UpdatedOwnedState,
                    updated.PriorRestorePoint.Package, updated.PriorRestorePoint.InstallerSha256,
                    updated.RebootRequired),
            _ => throw new InvalidDataException("NetBird Apply returned a receipt that does not match the durable mutation plan."),
        };
    }

    private async ValueTask<MachineUpgradeJournalDocument> GetOrInitializeAsync(CancellationToken cancellationToken)
    {
        var document = await _journal.LoadAsync(_operationId, _initiatingSid, cancellationToken).ConfigureAwait(false);
        if (document is not null) return document;

        var initial = new MachineUpgradeJournalDocument(
            MachineUpgradeJournalDocument.CurrentSchemaVersion,
            _operationId,
            _initiatingSid,
            0,
            MachineUpgradeState.InProgress,
            MachineUpgradePhase.Preflight,
            null,
            null,
            null,
            new ProtectedVelopackReceiptIdentifiers(null, null));
        await _journal.InitializeAsync(initial, cancellationToken).ConfigureAwait(false);
        return initial;
    }

    private static MachineDispatcherResult Completed(
        MachineIpcRequest request,
        MachineUpgradeJournalDocument document,
        NetBirdOwnership? ownership,
        NetBirdChangeKind? change = null) =>
        Snapshot(request, document, MachineDispatcherStatus.Completed, MachineDispatcherCode.None, ownership, change);

    private static MachineDispatcherResult Snapshot(
        MachineIpcRequest request,
        MachineUpgradeJournalDocument document,
        MachineDispatcherStatus status,
        MachineDispatcherCode code,
        NetBirdOwnership? ownership = null,
        NetBirdChangeKind? change = null,
        NetBirdInterruptedRecoveryAction? reconciliationAction = null,
        string? reconciliationEvidenceId = null) =>
        new(MachineIpcFrameCodec.CurrentVersion, request.CorrelationId, request.Operation, status, code,
            document.State, document.Phase, document.Revision, ownership, change, reconciliationAction, reconciliationEvidenceId,
            document.RecoveryReceipt?.RebootRequired ?? false);

    private static MachineDispatcherResult Result(
        MachineIpcRequest? request,
        MachineDispatcherStatus status,
        MachineDispatcherCode code) =>
        new(MachineIpcFrameCodec.CurrentVersion, request?.CorrelationId ?? Guid.Empty,
            request?.Operation ?? MachineIpcOperation.Inspect, status, code);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _journal.Dispose();
        _dispatchGate.Dispose();
    }
}
