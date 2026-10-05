namespace Connector.Upgrade.Core;

public sealed partial class UpgradeOrchestrator(
    IUpgradePorts ports,
    IUpgradeJournalStore journalStore,
    LegacyUpgradeLock? suppliedLock = null,
    Guid? boundRunId = null)
{
    private readonly IUpgradePorts _ports = ports;
    private readonly IUpgradeJournalStore _journalStore = journalStore;
    private static readonly LegacyApplicationKind[] RequiredLegacyOrder =
    [
        LegacyApplicationKind.StructuraConnector,
        LegacyApplicationKind.PlatformConnector,
    ];

    private readonly LegacyUpgradeLock _upgradeLock = suppliedLock ?? LegacyUpgradeLock.LoadEmbedded();
    private readonly Guid? _boundRunId = boundRunId == Guid.Empty
        ? throw new ArgumentException("The bound upgrade operation must have a nonempty id.", nameof(boundRunId))
        : boundRunId;
    private UpgradeJournalDocument? _journal;

    public async ValueTask<UpgradeExecutionResult> ExecuteAsync(
        OneTimePlatformToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        await using var lease = await _journalStore.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);

        var existing = await _journalStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (_boundRunId is { } expectedRunId && existing.RunId != expectedRunId)
                return new UpgradeExecutionResult(UpgradeOutcome.NeedsManualRecovery, existing.RunId,
                    "operation_identity_mismatch");
            var outcome = existing.State switch
            {
                UpgradeJournalState.Committed => UpgradeOutcome.AlreadyCommitted,
                UpgradeJournalState.NeedsManualRecovery => UpgradeOutcome.NeedsManualRecovery,
                _ => UpgradeOutcome.Interrupted,
            };
            return new UpgradeExecutionResult(outcome, existing.RunId, existing.FailureCode,
                outcome == UpgradeOutcome.AlreadyCommitted && JournalRequiresReboot(existing));
        }

        var runId = _boundRunId ?? Guid.NewGuid();
        _journal = new UpgradeJournalDocument(
            UpgradeJournalDocument.CurrentSchemaVersion,
            runId,
            0,
            UpgradeJournalState.InProgress,
            UpgradePhase.Preflight,
            [new UpgradeJournalEvent(1, UpgradeJournalEventKind.PhaseEntered, UpgradePhase.Preflight, null, DateTimeOffset.UtcNow)]);
        await _journalStore.SaveAsync(_journal, cancellationToken).ConfigureAwait(false);

        UpgradePreflight? preflight = null;
        NetBirdOperationIntent? netBirdPlan = null;
        NetBirdOperationReceipt? netBird = null;
        UnifiedApplicationReceipt? unified = null;
        PlatformEnrollmentReceipt? enrollment = null;
        var removals = new List<LegacyRemovalReceipt>();
        var rollbackPayloadLeases = new List<IVerifiedRollbackPayloadLease>();
        UpgradeMutation? pendingMutation = null;

        try
        {
            preflight = await _ports.InspectAsync(cancellationToken).ConfigureAwait(false);
            ValidatePreflight(preflight,
                requireOwnedNetBirdState: _ports is not IUpgradeMachineVerifiedNetBirdPorts);
            await RecordPreflightAsync(preflight, cancellationToken).ConfigureAwait(false);

            foreach (var kind in RequiredLegacyOrder.Where(kind => WasInstalledAtPreflight(preflight, kind)))
            {
                var payload = preflight.RollbackPayloads.Single(item => item.Identity.Kind == kind);
                var mutation = kind == LegacyApplicationKind.StructuraConnector
                    ? UpgradeMutation.AcquireStructuraRollbackPayload
                    : UpgradeMutation.AcquirePlatformRollbackPayload;
                await RecordIntentAsync(mutation, cancellationToken).ConfigureAwait(false);
                pendingMutation = mutation;
                var payloadLease = await _ports.AcquireVerifiedRollbackPayloadAsync(payload, cancellationToken)
                    .ConfigureAwait(false);
                pendingMutation = null;
                ArgumentNullException.ThrowIfNull(payloadLease);
                rollbackPayloadLeases.Add(payloadLease);
                ValidatePayloadLease(_upgradeLock.Get(kind), payloadLease);
                await RecordPayloadLeaseAsync(mutation, payloadLease, cancellationToken).ConfigureAwait(false);
            }

            await EnterForwardPhaseAsync(UpgradePhase.EnsureNetBird, cancellationToken).ConfigureAwait(false);
            if (preflight.NetBird.Ownership is NetBirdOwnership.Absent or NetBirdOwnership.OwnedByConnector)
            {
                await RecordIntentAsync(UpgradeMutation.EnsureNetBird, cancellationToken).ConfigureAwait(false);
                netBirdPlan = await _ports
                    .PrepareOwnedNetBirdMutationAsync(preflight.NetBird, cancellationToken)
                    .ConfigureAwait(false);
                ValidateNetBirdPlan(preflight.NetBird, netBirdPlan);
                await RecordNetBirdPlanAsync(netBirdPlan, cancellationToken).ConfigureAwait(false);
                pendingMutation = UpgradeMutation.EnsureNetBird;
                netBird = await _ports.ApplyOwnedNetBirdMutationAsync(netBirdPlan, cancellationToken).ConfigureAwait(false);
                pendingMutation = null;
                ValidateNetBirdReceipt(netBirdPlan, netBird);
                await RecordAppliedAsync(
                    UpgradeMutation.EnsureNetBird,
                    recovery => recovery with { NetBirdReceipt = netBird },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await AppendEventAsync(
                    UpgradeJournalEventKind.SkippedForeignComponent,
                    null,
                    preflight.NetBird.Ownership == NetBirdOwnership.Foreign
                        ? "foreign_netbird_preserved"
                        : "unattributed_netbird_preserved",
                    cancellationToken).ConfigureAwait(false);
            }

            await EnterForwardPhaseAsync(UpgradePhase.InstallUnifiedApplication, cancellationToken).ConfigureAwait(false);
            await RecordIntentAsync(UpgradeMutation.InstallUnifiedApplication, cancellationToken).ConfigureAwait(false);
            pendingMutation = UpgradeMutation.InstallUnifiedApplication;
            unified = await _ports.InstallUnifiedApplicationAsync(cancellationToken).ConfigureAwait(false);
            pendingMutation = null;
            await RecordAppliedAsync(
                UpgradeMutation.InstallUnifiedApplication,
                recovery => recovery with { UnifiedApplicationReceipt = unified },
                cancellationToken).ConfigureAwait(false);

            await EnterForwardPhaseAsync(UpgradePhase.EnrollPlatform, cancellationToken).ConfigureAwait(false);
            await RecordIntentAsync(UpgradeMutation.EnrollPlatform, cancellationToken).ConfigureAwait(false);
            pendingMutation = UpgradeMutation.EnrollPlatform;
            enrollment = await _ports.EnrollAsync(token, cancellationToken).ConfigureAwait(false);
            pendingMutation = null;
            await RecordAppliedAsync(
                UpgradeMutation.EnrollPlatform,
                recovery => recovery with { PlatformEnrollmentReceipt = enrollment },
                cancellationToken).ConfigureAwait(false);

            await EnterForwardPhaseAsync(UpgradePhase.VerifyNewAccess, cancellationToken).ConfigureAwait(false);
            var access = await _ports.VerifyNewAccessAsync(enrollment, cancellationToken).ConfigureAwait(false);
            if (!access.IsSuccessful)
                throw new UpgradeInvariantException("New Platform API, VPN and SMB access were not all verified.");
            await RecordObservationAsync("new_access_verified", cancellationToken).ConfigureAwait(false);

            foreach (var kind in RequiredLegacyOrder.Where(kind => WasInstalledAtPreflight(preflight, kind)))
            {
                var phase = kind == LegacyApplicationKind.StructuraConnector
                    ? UpgradePhase.RemoveStructuraConnector
                    : UpgradePhase.RemovePlatformConnector;
                var mutation = kind == LegacyApplicationKind.StructuraConnector
                    ? UpgradeMutation.RemoveStructuraConnector
                    : UpgradeMutation.RemovePlatformConnector;
                var identity = preflight.LegacyPresenceBaseline!.Single(item => item.Identity.Kind == kind).Identity;

                await EnterForwardPhaseAsync(phase, cancellationToken).ConfigureAwait(false);
                await RecordIntentAsync(mutation, cancellationToken).ConfigureAwait(false);
                pendingMutation = mutation;
                var removal = await _ports.RemoveExactLegacyApplicationAsync(identity, cancellationToken).ConfigureAwait(false);
                pendingMutation = null;
                if (removal.Identity != identity)
                    throw new UpgradeInvariantException("Legacy removal receipt does not match the requested MSI identity.");
                removals.Add(removal);
                await RecordAppliedAsync(
                    mutation,
                    recovery => recovery with { LegacyRemovalReceipts = [.. recovery.LegacyRemovalReceipts, removal] },
                    cancellationToken).ConfigureAwait(false);
            }

            await EnterForwardPhaseAsync(UpgradePhase.VerifyFinalState, cancellationToken).ConfigureAwait(false);
            var final = await _ports.VerifyFinalStateAsync(cancellationToken).ConfigureAwait(false);
            if (!final.IsSuccessful)
                throw new UpgradeInvariantException("Final unified application state was not verified.");
            await RecordObservationAsync("final_state_verified", cancellationToken).ConfigureAwait(false);

            await EnterForwardPhaseAsync(UpgradePhase.Commit, cancellationToken).ConfigureAwait(false);
            await PersistAsync(
                _journal with { State = UpgradeJournalState.Committed, FailureCode = null },
                cancellationToken).ConfigureAwait(false);
            return new UpgradeExecutionResult(UpgradeOutcome.Succeeded, runId, RebootRequired: JournalRequiresReboot(_journal));
        }
        catch (Exception error)
        {
            var uncertainCanceledMutation = error is OperationCanceledException && pendingMutation is not null;
            return await RollbackAsync(
                    runId,
                    error.GetType().Name,
                    preflight,
                    netBirdPlan,
                    netBird,
                    unified,
                    enrollment,
                    removals,
                    rollbackPayloadLeases,
                    uncertainCanceledMutation)
                .ConfigureAwait(false);
        }
        finally
        {
            foreach (var payloadLease in rollbackPayloadLeases.AsEnumerable().Reverse())
                await payloadLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool JournalRequiresReboot(UpgradeJournalDocument journal) =>
        journal.Recovery?.NetBirdReceipt?.RebootRequired == true ||
        journal.Recovery?.UnifiedApplicationReceipt?.RebootRequired == true ||
        journal.Recovery?.LegacyRemovalReceipts.Any(receipt => receipt.RebootRequired) == true;

    private void ValidatePreflight(UpgradePreflight preflight, bool persistedRecoverySummary = false,
        bool requireOwnedNetBirdState = true)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        if (!preflight.NewServerSchemaAvailable || !preflight.LegacyWorkCanDrainSafely)
            throw new UpgradeInvariantException("Server compatibility and safe legacy work drain are required.");
        if (string.IsNullOrWhiteSpace(preflight.CurrentWindowsUserId))
            throw new UpgradeInvariantException("The current Windows user identity is missing.");

        var apps = preflight.LegacyApplications;
        var baseline = preflight.LegacyPresenceBaseline;
        if (baseline is null || baseline.Count != RequiredLegacyOrder.Length ||
            RequiredLegacyOrder.Any(kind => baseline.Count(item => item.Identity.Kind == kind) != 1))
            throw new UpgradeInvariantException("An explicit exact presence baseline for both legacy MSI identities is required.");

        foreach (var kind in RequiredLegacyOrder)
        {
            var pin = _upgradeLock.Get(kind);
            var observation = baseline.Single(item => item.Identity.Kind == kind);
            if (observation.Identity != pin.Identity || string.IsNullOrWhiteSpace(observation.ObservationId) ||
                observation.Presence is not (ExactLegacyPresence.ExactInstalled or ExactLegacyPresence.Absent))
                throw new UpgradeInvariantException($"The exact {kind} MSI presence was not proven against the repository lock.");

            var installed = apps.Where(item => item.Identity.Kind == kind).ToArray();
            if (observation.Presence == ExactLegacyPresence.ExactInstalled)
            {
                if (installed.Length != 1 || !installed[0].ExactMsiIdentityVerified ||
                    installed[0].Identity != pin.Identity || observation.InstalledVersion != pin.Version ||
                    !string.Equals(installed[0].Version, pin.Version, StringComparison.Ordinal))
                    throw new UpgradeInvariantException($"Installed {kind} does not match the repository MSI lock.");
            }
            else if (installed.Length != 0 || observation.InstalledVersion is not null)
            {
                throw new UpgradeInvariantException($"Absent {kind} has contradictory installed-MSI data.");
            }

            if (!persistedRecoverySummary && observation.Presence == ExactLegacyPresence.ExactInstalled)
            {
                var payloads = preflight.RollbackPayloads.Where(payload => payload.Identity.Kind == kind).ToArray();
                if (payloads.Length != 1)
                    throw new UpgradeInvariantException($"Exactly one rollback payload is required for {kind}.");
                ValidatePayload(pin, payloads[0]);
            }
        }

        var installedCount = baseline.Count(item => item.Presence == ExactLegacyPresence.ExactInstalled);
        if (apps.Count != installedCount ||
            preflight.RollbackPayloads.Count != (persistedRecoverySummary ? 0 : installedCount))
            throw new UpgradeInvariantException("Legacy installed-MSI and rollback payload sets do not match the exact presence baseline.");
        if (!preflight.UserState.Verified ||
            !string.Equals(
                preflight.UserState.WindowsUserId,
                preflight.CurrentWindowsUserId,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(preflight.UserState.SnapshotId) ||
            string.IsNullOrWhiteSpace(preflight.UserState.Sha256))
            throw new UpgradeInvariantException("A verified same-user state snapshot is required.");

        if (preflight.NetBird.Ownership == NetBirdOwnership.OwnedByConnector)
        {
            var ownedState = preflight.NetBird.OwnedState;
            var invalidOwnedState = persistedRecoverySummary
                ? ownedState is not null && !IsValidOwnedState(ownedState)
                : requireOwnedNetBirdState
                    ? !IsValidOwnedState(ownedState) ||
                      !string.Equals(preflight.NetBird.InstallationId, ownedState?.InstallationId, StringComparison.Ordinal)
                    : ownedState is not null || preflight.NetBird.InstallationId is not null;
            if (invalidOwnedState)
                throw new UpgradeInvariantException("Owned NetBird state is incomplete or inconsistent.");
        }
        if (preflight.NetBird.Ownership != NetBirdOwnership.OwnedByConnector &&
            preflight.NetBird.OwnedState is not null)
            throw new UpgradeInvariantException("Unowned NetBird assessment contains connector-owned state.");
    }

    private static bool WasInstalledAtPreflight(UpgradePreflight preflight, LegacyApplicationKind kind) =>
        preflight.LegacyPresenceBaseline!.Single(item => item.Identity.Kind == kind).Presence == ExactLegacyPresence.ExactInstalled;

    private static void ValidatePayload(LegacyUpgradePin pin, LegacyRollbackPayload payload)
    {
        if (!payload.Verified || payload.Identity != pin.Identity ||
            !string.Equals(payload.PackageId, pin.PackageId, StringComparison.Ordinal) ||
            !string.Equals(payload.InstallerName, pin.InstallerName, StringComparison.Ordinal) ||
            !string.Equals(payload.Version, pin.Version, StringComparison.Ordinal) ||
            payload.SizeBytes != pin.SizeBytes ||
            !string.Equals(payload.Sha256, pin.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new UpgradeInvariantException($"Rollback payload for {pin.Kind} does not match the repository lock.");
    }

    private static void ValidatePayloadLease(LegacyUpgradePin pin, IVerifiedRollbackPayloadLease payloadLease)
    {
        ArgumentNullException.ThrowIfNull(payloadLease);
        var inspection = payloadLease.Inspection;
        if (!inspection.TrustedSource || inspection.Identity != pin.Identity ||
            payloadLease.Protection != RollbackPayloadProtection.ProtectedMachineStaging ||
            string.IsNullOrWhiteSpace(payloadLease.HandleId) ||
            !string.Equals(inspection.PackageId, pin.PackageId, StringComparison.Ordinal) ||
            !string.Equals(inspection.InstallerName, pin.InstallerName, StringComparison.Ordinal) ||
            !string.Equals(inspection.Version, pin.Version, StringComparison.Ordinal) ||
            inspection.SizeBytes != pin.SizeBytes ||
            !string.Equals(inspection.Sha256, pin.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new UpgradeInvariantException($"Live rollback asset for {pin.Kind} no longer matches the repository lock.");
    }

    private static void ValidateNetBirdReceipt(
        NetBirdOperationIntent operation,
        NetBirdOperationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!IsTrustedOperationId(operation.OperationId) ||
            !string.Equals(operation.OperationId, receipt.OperationId, StringComparison.Ordinal) ||
            operation.PriorOwnership != receipt.PriorOwnership || operation.Change != receipt.Change ||
            operation.Status != NetBirdOperationStatus.Prepared || receipt.Status != NetBirdOperationStatus.Applied)
            throw new UpgradeInvariantException("NetBird receipt does not match its trusted operation intent.");

        var valid = (operation.PriorOwnership, operation.Change, receipt.ResultingOwnership) switch
        {
            (NetBirdOwnership.Absent, NetBirdChangeKind.InstalledThisRun, NetBirdOwnership.OwnedByConnector) =>
                true,
            (NetBirdOwnership.OwnedByConnector, NetBirdChangeKind.NoChange, NetBirdOwnership.OwnedByConnector) =>
                true,
            (NetBirdOwnership.OwnedByConnector, NetBirdChangeKind.UpdatedThisRun, NetBirdOwnership.OwnedByConnector) =>
                true,
            _ => false,
        };
        if (!valid)
            throw new UpgradeInvariantException("NetBird receipt does not describe the exact delta made by this run.");
    }

    private static void ValidateNetBirdPlan(NetBirdAssessment assessment, NetBirdOperationIntent plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var valid = IsTrustedOperationId(plan.OperationId) &&
            plan.Status == NetBirdOperationStatus.Prepared && plan.PriorOwnership == assessment.Ownership &&
            (assessment.Ownership, plan.Change) switch
        {
            (NetBirdOwnership.Absent, NetBirdChangeKind.InstalledThisRun) => true,
            (NetBirdOwnership.OwnedByConnector, NetBirdChangeKind.NoChange or NetBirdChangeKind.UpdatedThisRun) =>
                true,
            _ => false,
        };
        if (!valid)
            throw new UpgradeInvariantException("NetBird operation does not describe the exact permitted delta.");
    }

    private static bool IsTrustedOperationId(string? value) =>
        value is not null && Guid.TryParseExact(value, "N", out var operationId) && operationId != Guid.Empty;

    private static bool IsValidOwnedState(NetBirdOwnedState? state)
    {
        if (state is null || string.IsNullOrWhiteSpace(state.InstallationId) || string.IsNullOrWhiteSpace(state.Version) ||
            string.IsNullOrWhiteSpace(state.ServiceIdentity) || string.IsNullOrWhiteSpace(state.ConfigurationSha256) ||
            state.ConfigurationSha256.Length != 64)
            return false;
        try
        {
            _ = Convert.FromHexString(state.ConfigurationSha256);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async ValueTask<UpgradeExecutionResult> RollbackAsync(
        Guid runId,
        string failureCode,
        UpgradePreflight? preflight,
        NetBirdOperationIntent? netBirdPlan,
        NetBirdOperationReceipt? netBird,
        UnifiedApplicationReceipt? unified,
        PlatformEnrollmentReceipt? enrollment,
        IReadOnlyList<LegacyRemovalReceipt> removals,
        IReadOnlyList<IVerifiedRollbackPayloadLease> rollbackPayloadLeases,
        bool forceManualRecovery)
    {
        var rollbackErrors = new List<Exception>();
        try
        {
            await PersistAsync(
                _journal! with
                {
                    State = UpgradeJournalState.RollingBack,
                    Phase = UpgradePhase.Rollback,
                    FailureCode = failureCode,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception journalError)
        {
            rollbackErrors.Add(journalError);
        }

        if (preflight is not null)
        {
            foreach (var removal in removals.Reverse())
            {
                var payloadLease = rollbackPayloadLeases.Single(item => item.Inspection.Identity == removal.Identity);
                var mutation = removal.Identity.Kind == LegacyApplicationKind.StructuraConnector
                    ? UpgradeMutation.RestoreStructuraConnector
                    : UpgradeMutation.RestorePlatformConnector;
                await TryCompensateAsync(
                    mutation,
                    ct => _ports.RestoreExactLegacyApplicationAsync(payloadLease, removal, ct),
                    rollbackErrors).ConfigureAwait(false);
            }
        }

        if (enrollment is not null)
            await TryCompensateAsync(
                UpgradeMutation.RemoveNewEnrollment,
                ct => _ports.RemoveNewEnrollmentAsync(enrollment, ct),
                rollbackErrors).ConfigureAwait(false);

        if (unified is not null)
            await TryCompensateAsync(
                UpgradeMutation.RemoveUnifiedApplication,
                ct => _ports.RemoveUnifiedApplicationAsync(unified, ct),
                rollbackErrors).ConfigureAwait(false);

        if (netBird is not null && netBird.Change != NetBirdChangeKind.NoChange && netBirdPlan is not null)
            await TryCompensateAsync(
                UpgradeMutation.RollbackNetBird,
                ct => _ports.CompensateNetBirdMutationAsync(netBirdPlan, netBird, ct),
                rollbackErrors).ConfigureAwait(false);

        if (netBird is null && netBirdPlan is { Change: not NetBirdChangeKind.NoChange })
            await TryCompensateAsync(
                UpgradeMutation.RollbackNetBird,
                async ct =>
                {
                    var recovered = await _ports.ReconcileInterruptedNetBirdAsync(netBirdPlan, ct).ConfigureAwait(false);
                    ValidateNetBirdRecoveryResult(netBirdPlan, recovered);
                },
                rollbackErrors).ConfigureAwait(false);

        if (preflight is not null && (unified is not null || enrollment is not null || removals.Count > 0))
            await TryCompensateAsync(
                UpgradeMutation.RestoreUserState,
                ct => _ports.RestoreUserStateAsync(preflight.UserState, ct),
                rollbackErrors).ConfigureAwait(false);

        var finalState = rollbackErrors.Count == 0 && !forceManualRecovery
            ? UpgradeJournalState.RolledBack
            : UpgradeJournalState.NeedsManualRecovery;
        try
        {
            await PersistAsync(
                _journal! with { State = finalState, FailureCode = failureCode },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception journalError)
        {
            rollbackErrors.Add(journalError);
            finalState = UpgradeJournalState.NeedsManualRecovery;
        }

        return new UpgradeExecutionResult(
            finalState == UpgradeJournalState.RolledBack
                ? UpgradeOutcome.FailedAndRolledBack
                : UpgradeOutcome.NeedsManualRecovery,
            runId,
            failureCode);
    }

    private async ValueTask TryCompensateAsync(
        UpgradeMutation mutation,
        Func<CancellationToken, ValueTask> compensation,
        ICollection<Exception> errors)
    {
        try
        {
            await RecordIntentAsync(mutation, CancellationToken.None).ConfigureAwait(false);
            await compensation(CancellationToken.None).ConfigureAwait(false);
            await AppendEventAsync(
                UpgradeJournalEventKind.MutationCompensated,
                mutation,
                null,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
            try
            {
                await AppendEventAsync(
                    UpgradeJournalEventKind.MutationFailed,
                    mutation,
                    error.GetType().Name,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception journalError)
            {
                errors.Add(journalError);
            }
        }
    }

    private async ValueTask RecordPreflightAsync(UpgradePreflight preflight, CancellationToken cancellationToken)
    {
        var userSafePreflight = preflight with
        {
            RollbackPayloads = [],
            NetBird = new NetBirdAssessment(preflight.NetBird.Ownership, null),
        };
        var recovery = new UpgradeRecoveryMetadata(userSafePreflight, [], null, null, null, null, []);
        var events = CopyEventsWith(
            UpgradeJournalEventKind.ObservationPassed,
            _journal!.Phase,
            null,
            "preflight_verified");
        await PersistAsync(_journal with { Events = events, Recovery = recovery }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RecordNetBirdPlanAsync(
        NetBirdOperationIntent plan,
        CancellationToken cancellationToken)
    {
        var recovery = _journal!.Recovery
            ?? throw new UpgradeInvariantException("Preflight recovery metadata was not persisted.");
        var events = CopyEventsWith(
            UpgradeJournalEventKind.MutationPrepared,
            _journal.Phase,
            UpgradeMutation.EnsureNetBird,
            $"netbird_operation:{plan.Change}");
        await PersistAsync(
            _journal with { Events = events, Recovery = recovery with { NetBirdPlan = plan } },
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateNetBirdRecoveryResult(
        NetBirdOperationIntent plan,
        NetBirdInterruptedRecoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(result.EvidenceId))
            throw new UpgradeInvariantException("NetBird recovery returned no durable evidence identity.");
        var valid = (plan.Change, result.Action) switch
        {
            (NetBirdChangeKind.InstalledThisRun, NetBirdInterruptedRecoveryAction.NoMutationObserved) => true,
            (NetBirdChangeKind.InstalledThisRun, NetBirdInterruptedRecoveryAction.RemovedInstalledThisRun) => true,
            (NetBirdChangeKind.UpdatedThisRun, NetBirdInterruptedRecoveryAction.NoMutationObserved) => true,
            (NetBirdChangeKind.UpdatedThisRun, NetBirdInterruptedRecoveryAction.RestoredPriorOwnedState) => true,
            (NetBirdChangeKind.NoChange, NetBirdInterruptedRecoveryAction.NoMutationObserved) => true,
            _ => false,
        };
        if (!valid)
            throw new UpgradeInvariantException("NetBird recovery action does not match the durable plan.");
    }

    private async ValueTask RecordPayloadLeaseAsync(
        UpgradeMutation mutation,
        IVerifiedRollbackPayloadLease payloadLease,
        CancellationToken cancellationToken)
    {
        var recovery = _journal!.Recovery
            ?? throw new UpgradeInvariantException("Preflight recovery metadata was not persisted.");
        var receipt = new ProtectedRollbackPayloadReceipt(
            payloadLease.HandleId,
            payloadLease.Inspection.Identity.Kind,
            payloadLease.Protection);
        var events = CopyEventsWith(
            UpgradeJournalEventKind.MutationApplied,
            _journal.Phase,
            mutation,
            $"protected_payload_acquired:{payloadLease.Inspection.Identity.Kind}");
        await PersistAsync(
            _journal with
            {
                Events = events,
                Recovery = recovery with
                {
                    ProtectedRollbackPayloads = [.. recovery.ProtectedRollbackPayloads, receipt],
                },
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnterForwardPhaseAsync(UpgradePhase phase, CancellationToken cancellationToken)
    {
        if (_journal is null || _journal.State != UpgradeJournalState.InProgress || phase <= _journal.Phase || phase >= UpgradePhase.Rollback)
            throw new UpgradeInvariantException($"Invalid upgrade phase transition to {phase}.");
        var events = CopyEventsWith(UpgradeJournalEventKind.PhaseEntered, phase, null, null);
        await PersistAsync(_journal with { Phase = phase, Events = events }, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask RecordIntentAsync(UpgradeMutation mutation, CancellationToken cancellationToken) =>
        AppendEventAsync(UpgradeJournalEventKind.MutationIntent, mutation, null, cancellationToken);

    private async ValueTask RecordAppliedAsync(
        UpgradeMutation mutation,
        Func<UpgradeRecoveryMetadata, UpgradeRecoveryMetadata> updateRecovery,
        CancellationToken cancellationToken)
    {
        var recovery = _journal!.Recovery
            ?? throw new UpgradeInvariantException("Preflight recovery metadata was not persisted.");
        var events = CopyEventsWith(UpgradeJournalEventKind.MutationApplied, _journal.Phase, mutation, null);
        await PersistAsync(
            _journal with { Events = events, Recovery = updateRecovery(recovery) },
            cancellationToken).ConfigureAwait(false);
    }

    private ValueTask RecordObservationAsync(string code, CancellationToken cancellationToken) =>
        AppendEventAsync(UpgradeJournalEventKind.ObservationPassed, null, code, cancellationToken);

    private async ValueTask AppendEventAsync(
        UpgradeJournalEventKind kind,
        UpgradeMutation? mutation,
        string? code,
        CancellationToken cancellationToken)
    {
        var events = CopyEventsWith(kind, _journal!.Phase, mutation, code);
        await PersistAsync(_journal with { Events = events }, cancellationToken).ConfigureAwait(false);
    }

    private List<UpgradeJournalEvent> CopyEventsWith(
        UpgradeJournalEventKind kind,
        UpgradePhase phase,
        UpgradeMutation? mutation,
        string? code)
    {
        var events = new List<UpgradeJournalEvent>(_journal!.Events)
        {
            new(_journal.Events.Count + 1, kind, phase, mutation, DateTimeOffset.UtcNow, code),
        };
        return events;
    }

    private async ValueTask PersistAsync(UpgradeJournalDocument next, CancellationToken cancellationToken)
    {
        next = next with { Revision = _journal!.Revision + 1 };
        await _journalStore.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        _journal = next;
    }
}
