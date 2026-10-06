namespace Connector.Upgrade.Core;

public sealed partial class UpgradeOrchestrator
{
    /// <summary>
    /// Explicit operator recovery for an interrupted removal. It never consumes an enrollment token
    /// and never retries an uninstall intent; current exact MSI presence decides what is restored.
    /// </summary>
    public async ValueTask<UpgradeExecutionResult> RecoverInterruptedAsync(
        CancellationToken cancellationToken = default)
    {
        await using var journalLease = await _journalStore.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var existing = await _journalStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return new UpgradeExecutionResult(UpgradeOutcome.Interrupted, Guid.Empty, "recovery_journal_missing");
        if (_boundRunId is { } expectedRunId && existing.RunId != expectedRunId)
            return new UpgradeExecutionResult(UpgradeOutcome.NeedsManualRecovery, existing.RunId,
                "operation_identity_mismatch");
        if (existing.State == UpgradeJournalState.Committed)
            return new UpgradeExecutionResult(UpgradeOutcome.AlreadyCommitted, existing.RunId,
                RebootRequired: existing.Recovery?.UnifiedApplicationReceipt?.RebootRequired == true ||
                    existing.Recovery?.LegacyRemovalReceipts.Any(receipt => receipt.RebootRequired) == true ||
                    existing.Recovery?.NetBirdReceipt?.RebootRequired == true);
        if (existing.State == UpgradeJournalState.RolledBack)
            return new UpgradeExecutionResult(UpgradeOutcome.FailedAndRolledBack, existing.RunId, existing.FailureCode);
        if (existing.State == UpgradeJournalState.NeedsManualRecovery)
            return new UpgradeExecutionResult(UpgradeOutcome.NeedsManualRecovery, existing.RunId, existing.FailureCode);

        _journal = existing;
        var runId = existing.RunId;
        var recovery = existing.Recovery;
        if (recovery is null)
            return await MarkManualRecoveryAsync(runId, "recovery_journal_not_actionable").ConfigureAwait(false);

        if (IsSupportedInterruptedNetBird(existing))
            return await RecoverInterruptedNetBirdAsync(runId, recovery).ConfigureAwait(false);
        if (!IsSupportedInterruptedRemoval(existing))
            return await MarkManualRecoveryAsync(runId, "recovery_journal_not_actionable").ConfigureAwait(false);

        try
        {
            ValidateRecoveryMetadata(recovery);
        }
        catch (Exception)
        {
            return await MarkManualRecoveryAsync(runId, "recovery_metadata_unproven").ConfigureAwait(false);
        }

        try
        {
            await PersistAsync(
                _journal with
                {
                    State = UpgradeJournalState.RollingBack,
                    Phase = UpgradePhase.Rollback,
                    FailureCode = "interrupted_removal_recovery",
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await MarkManualRecoveryAsync(runId, "recovery_journal_write_failed").ConfigureAwait(false);
        }

        InterruptedUpgradeInspection inspection;
        try
        {
            inspection = await _ports.InspectInterruptedUpgradeAsync(recovery, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await MarkManualRecoveryAsync(runId, "recovery_inspection_failed").ConfigureAwait(false);
        }

        var inspectionError = ValidateRecoveryInspection(recovery, inspection, requireStateMatch: false);
        if (inspectionError is not null)
            return await MarkManualRecoveryAsync(runId, inspectionError).ConfigureAwait(false);

        var payloadLeases = new List<IVerifiedRollbackPayloadLease>();
        try
        {
            foreach (var absent in inspection.LegacyApplications.Where(item =>
                         item.Presence == ExactLegacyPresence.Absent && WasInstalledAtPreflight(recovery.Preflight, item.Identity.Kind)))
            {
                var durable = recovery.ProtectedRollbackPayloads.Single(item => item.Kind == absent.Identity.Kind);
                var acquireMutation = absent.Identity.Kind == LegacyApplicationKind.StructuraConnector
                    ? UpgradeMutation.AcquireStructuraRollbackPayload
                    : UpgradeMutation.AcquirePlatformRollbackPayload;
                await RecordIntentAsync(acquireMutation, CancellationToken.None).ConfigureAwait(false);

                IVerifiedRollbackPayloadLease payloadLease;
                try
                {
                    payloadLease = await _ports.ReacquireVerifiedRollbackPayloadAsync(durable, CancellationToken.None)
                        .ConfigureAwait(false);
                    ArgumentNullException.ThrowIfNull(payloadLease);
                    payloadLeases.Add(payloadLease);
                    ValidatePayloadLease(_upgradeLock.Get(absent.Identity.Kind), payloadLease);
                    if (!string.Equals(payloadLease.HandleId, durable.HandleId, StringComparison.Ordinal))
                        throw new UpgradeInvariantException("Recovery payload handle does not match the durable journal identity.");
                    await AppendEventAsync(
                        UpgradeJournalEventKind.ObservationPassed,
                        acquireMutation,
                        $"recovery_payload_reacquired:{absent.Identity.Kind}",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return await MarkManualRecoveryAsync(runId, "recovery_payload_unproven").ConfigureAwait(false);
                }
            }

            var restoreErrors = new List<Exception>();
            foreach (var absent in inspection.LegacyApplications.Where(item =>
                         item.Presence == ExactLegacyPresence.Absent && WasInstalledAtPreflight(recovery.Preflight, item.Identity.Kind)))
            {
                var payloadLease = payloadLeases.Single(item => item.Inspection.Identity == absent.Identity);
                var mutation = absent.Identity.Kind == LegacyApplicationKind.StructuraConnector
                    ? UpgradeMutation.RestoreStructuraConnector
                    : UpgradeMutation.RestorePlatformConnector;
                await TryCompensateAsync(
                    mutation,
                    ct => _ports.RestoreMissingLegacyApplicationAsync(payloadLease, absent, ct),
                    restoreErrors).ConfigureAwait(false);
            }
            if (restoreErrors.Count > 0)
                return await MarkManualRecoveryAsync(runId, "recovery_legacy_restore_failed").ConfigureAwait(false);

            var restoredInspection = await _ports.InspectInterruptedUpgradeAsync(recovery, CancellationToken.None)
                .ConfigureAwait(false);
            var restoredError = ValidateRecoveryInspection(
                recovery, restoredInspection, requireStateMatch: false, allowMissingPreviouslyInstalled: false);
            if (restoredError is not null)
                return await MarkManualRecoveryAsync(runId, "recovery_legacy_restore_unverified").ConfigureAwait(false);

            var cleanupErrors = new List<Exception>();
            await TryCompensateAsync(
                UpgradeMutation.RemoveNewEnrollment,
                ct => _ports.RemoveNewEnrollmentAsync(recovery.PlatformEnrollmentReceipt!, ct),
                cleanupErrors).ConfigureAwait(false);
            await TryCompensateAsync(
                UpgradeMutation.RemoveUnifiedApplication,
                ct => _ports.RemoveUnifiedApplicationAsync(recovery.UnifiedApplicationReceipt!, ct),
                cleanupErrors).ConfigureAwait(false);

            var netBird = recovery.NetBirdReceipt;
            if (netBird is not null && netBird.Change != NetBirdChangeKind.NoChange && recovery.NetBirdPlan is not null)
                await TryCompensateAsync(
                    UpgradeMutation.RollbackNetBird,
                    ct => _ports.CompensateNetBirdMutationAsync(recovery.NetBirdPlan, netBird, ct),
                    cleanupErrors).ConfigureAwait(false);

            await TryCompensateAsync(
                UpgradeMutation.RestoreUserState,
                ct => _ports.RestoreUserStateAsync(recovery.Preflight.UserState, ct),
                cleanupErrors).ConfigureAwait(false);
            if (cleanupErrors.Count > 0)
                return await MarkManualRecoveryAsync(runId, "recovery_compensation_failed").ConfigureAwait(false);

            var finalInspection = await _ports.InspectInterruptedUpgradeAsync(recovery, CancellationToken.None)
                .ConfigureAwait(false);
            var finalError = ValidateRecoveryInspection(
                recovery, finalInspection, requireStateMatch: true, allowMissingPreviouslyInstalled: false);
            if (finalError is not null)
                return await MarkManualRecoveryAsync(runId, "recovery_final_verification_failed").ConfigureAwait(false);

            await PersistAsync(
                _journal with
                {
                    State = UpgradeJournalState.RolledBack,
                    FailureCode = "interrupted_removal_recovered",
                },
                CancellationToken.None).ConfigureAwait(false);
            return new UpgradeExecutionResult(
                UpgradeOutcome.FailedAndRolledBack,
                runId,
                "interrupted_removal_recovered");
        }
        catch (Exception)
        {
            return await MarkManualRecoveryAsync(runId, "recovery_unexpected_failure").ConfigureAwait(false);
        }
        finally
        {
            foreach (var payloadLease in payloadLeases.AsEnumerable().Reverse())
                await payloadLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<UpgradeExecutionResult> RecoverInterruptedNetBirdAsync(
        Guid runId,
        UpgradeRecoveryMetadata recovery)
    {
        try
        {
            ValidateDurablePreflightMetadata(recovery);
            if (recovery.NetBirdPlan is null)
                throw new UpgradeInvariantException("Durable NetBird operation intent is missing.");
            ValidateNetBirdPlan(recovery.Preflight.NetBird, recovery.NetBirdPlan);
        }
        catch (Exception)
        {
            return await MarkManualRecoveryAsync(runId, "recovery_netbird_plan_unproven").ConfigureAwait(false);
        }

        try
        {
            await PersistAsync(
                _journal! with
                {
                    State = UpgradeJournalState.RollingBack,
                    Phase = UpgradePhase.Rollback,
                    FailureCode = "interrupted_netbird_recovery",
                },
                CancellationToken.None).ConfigureAwait(false);

            if (recovery.NetBirdPlan is { } plan)
            {
                var result = await _ports.ReconcileInterruptedNetBirdAsync(plan, CancellationToken.None)
                    .ConfigureAwait(false);
                ValidateNetBirdRecoveryResult(plan, result);
                await AppendEventAsync(
                    UpgradeJournalEventKind.MutationCompensated,
                    UpgradeMutation.RollbackNetBird,
                    $"netbird_reconciled:{result.Action}:{result.EvidenceId}",
                    CancellationToken.None).ConfigureAwait(false);
            }

            var inspection = await _ports.InspectInterruptedUpgradeAsync(recovery, CancellationToken.None)
                .ConfigureAwait(false);
            var inspectionError = ValidateRecoveryInspection(
                recovery, inspection, requireStateMatch: true, allowMissingPreviouslyInstalled: false);
            if (inspectionError is not null)
                return await MarkManualRecoveryAsync(runId, "recovery_legacy_state_changed_during_netbird").ConfigureAwait(false);

            await PersistAsync(
                _journal! with
                {
                    State = UpgradeJournalState.RolledBack,
                    FailureCode = "interrupted_netbird_recovered",
                },
                CancellationToken.None).ConfigureAwait(false);
            return new UpgradeExecutionResult(
                UpgradeOutcome.FailedAndRolledBack,
                runId,
                "interrupted_netbird_recovered");
        }
        catch (Exception)
        {
            return await MarkManualRecoveryAsync(runId, "recovery_netbird_unproven").ConfigureAwait(false);
        }
    }

    private static bool IsSupportedInterruptedRemoval(UpgradeJournalDocument journal)
    {
        if (journal.State != UpgradeJournalState.InProgress) return false;
        return journal.Phase is
            UpgradePhase.RemoveStructuraConnector or
            UpgradePhase.RemovePlatformConnector or
            UpgradePhase.VerifyFinalState or
            UpgradePhase.Commit;
    }

    private static bool IsSupportedInterruptedNetBird(UpgradeJournalDocument journal) =>
        journal.State == UpgradeJournalState.InProgress && journal.Phase == UpgradePhase.EnsureNetBird;

    private void ValidateRecoveryMetadata(UpgradeRecoveryMetadata recovery)
    {
        ValidateDurablePreflightMetadata(recovery);
        if (recovery.UnifiedApplicationReceipt is null || recovery.PlatformEnrollmentReceipt is null)
            throw new UpgradeInvariantException("Recovery receipts are incomplete.");
        if (recovery.NetBirdPlan is not null)
            ValidateNetBirdPlan(recovery.Preflight.NetBird, recovery.NetBirdPlan);
        else if (recovery.Preflight.NetBird.Ownership is NetBirdOwnership.Absent or NetBirdOwnership.OwnedByConnector)
            throw new UpgradeInvariantException("Durable NetBird mutation plan is missing.");

        var netBird = recovery.NetBirdReceipt;
        if (recovery.Preflight.NetBird.Ownership is NetBirdOwnership.Foreign or NetBirdOwnership.Unattributed)
        {
            if (netBird is not null) throw new UpgradeInvariantException("Unowned NetBird cannot have a mutation receipt.");
        }
        else
        {
            if (netBird is null) throw new UpgradeInvariantException("NetBird mutation receipt is missing.");
            if (recovery.NetBirdPlan is null)
                throw new UpgradeInvariantException("Durable NetBird operation intent is missing.");
            ValidateNetBirdReceipt(recovery.NetBirdPlan, netBird);
        }
    }

    private void ValidateDurablePreflightMetadata(UpgradeRecoveryMetadata recovery)
    {
        ValidatePreflight(recovery.Preflight, persistedRecoverySummary: true);
        var installedKinds = recovery.Preflight.LegacyPresenceBaseline!
            .Where(item => item.Presence == ExactLegacyPresence.ExactInstalled)
            .Select(item => item.Identity.Kind)
            .ToArray();
        if (recovery.ProtectedRollbackPayloads.Count != installedKinds.Length)
            throw new UpgradeInvariantException("Durable rollback payload receipts are incomplete.");
        foreach (var kind in installedKinds)
        {
            var durable = recovery.ProtectedRollbackPayloads.Single(item => item.Kind == kind);
            if (durable.Protection != RollbackPayloadProtection.ProtectedMachineStaging ||
                string.IsNullOrWhiteSpace(durable.HandleId) || !Enum.IsDefined(durable.Kind))
                throw new UpgradeInvariantException("Protected rollback payload attestation is missing.");
            _ = _upgradeLock.Get(kind);
        }
        if (recovery.LegacyRemovalReceipts.Any(receipt =>
                !installedKinds.Contains(receipt.Identity.Kind) ||
                receipt.Identity != _upgradeLock.Get(receipt.Identity.Kind).Identity) ||
            recovery.LegacyRemovalReceipts.Select(receipt => receipt.Identity.Kind).Distinct().Count() !=
                recovery.LegacyRemovalReceipts.Count)
            throw new UpgradeInvariantException("Legacy removal receipts conflict with the immutable presence baseline.");
    }

    private static string? ValidateRecoveryInspection(
        UpgradeRecoveryMetadata recovery,
        InterruptedUpgradeInspection inspection,
        bool requireStateMatch,
        bool allowMissingPreviouslyInstalled = true)
    {
        if (!string.Equals(
                inspection.CurrentWindowsUserId,
                recovery.Preflight.CurrentWindowsUserId,
                StringComparison.OrdinalIgnoreCase))
            return "recovery_windows_user_mismatch";
        if (!inspection.UserStateCanBeRestored)
            return "recovery_user_state_unrestorable";
        if (requireStateMatch && !inspection.UserStateMatchesSnapshot)
            return "recovery_user_state_unverified";
        if (inspection.LegacyApplications.Count != RequiredLegacyOrder.Length)
            return "recovery_legacy_identity_unproven";
        foreach (var kind in RequiredLegacyOrder)
        {
            var baseline = recovery.Preflight.LegacyPresenceBaseline?.SingleOrDefault(item => item.Identity.Kind == kind);
            if (baseline is null) return "recovery_legacy_identity_unproven";
            var matches = inspection.LegacyApplications.Where(item => item.Identity.Kind == kind).ToArray();
            if (matches.Length != 1 || matches[0].Identity != baseline.Identity ||
                matches[0].Presence is ExactLegacyPresence.IdentityMismatch or ExactLegacyPresence.Unknown ||
                string.IsNullOrWhiteSpace(matches[0].ObservationId))
                return "recovery_legacy_identity_unproven";
            if (baseline.Presence == ExactLegacyPresence.Absent && matches[0].Presence != ExactLegacyPresence.Absent)
                return "recovery_legacy_baseline_conflict";
            if (baseline.Presence == ExactLegacyPresence.ExactInstalled &&
                matches[0].Presence == ExactLegacyPresence.Absent && !allowMissingPreviouslyInstalled)
                return "recovery_legacy_state_changed";
        }
        return null;
    }

    private async ValueTask<UpgradeExecutionResult> MarkManualRecoveryAsync(Guid runId, string errorCode)
    {
        try
        {
            await PersistAsync(
                _journal! with
                {
                    State = UpgradeJournalState.NeedsManualRecovery,
                    Phase = UpgradePhase.Rollback,
                    FailureCode = errorCode,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The returned status remains fail-closed even if durable storage itself is unavailable.
        }
        return new UpgradeExecutionResult(UpgradeOutcome.NeedsManualRecovery, runId, errorCode);
    }
}
