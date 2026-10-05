using Connector.Upgrade.Core;
using Xunit;

namespace Connector.Upgrade.Tests;

public sealed class UpgradeOrchestratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "connector-upgrade-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Bound_operation_id_is_persisted_as_the_recoverable_journal_run_id()
    {
        Directory.CreateDirectory(_root);
        using var store = new FileUpgradeJournalStore(Path.Combine(_root, "bound-journal.json"));
        var operationId = Guid.NewGuid();
        var result = await new UpgradeOrchestrator(new FakePorts(), store, boundRunId: operationId)
            .ExecuteAsync(new OneTimePlatformToken("new-token"));

        Assert.Equal(UpgradeOutcome.Succeeded, result.Outcome);
        Assert.Equal(operationId, result.RunId);
        Assert.Equal(operationId, (await store.LoadAsync(CancellationToken.None))!.RunId);
    }

    [Fact]
    public async Task Recovery_with_a_different_operation_id_rejects_before_any_machine_port_call()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "mismatch-journal.json");
        using var store = new FileUpgradeJournalStore(path);
        var journal = new UpgradeJournalDocument(UpgradeJournalDocument.CurrentSchemaVersion,
            Guid.NewGuid(), 0, UpgradeJournalState.InProgress, UpgradePhase.Preflight, []);
        await store.SaveAsync(journal, CancellationToken.None);
        var originalBytes = await File.ReadAllBytesAsync(path);
        var ports = new FakePorts();

        var result = await new UpgradeOrchestrator(ports, store, boundRunId: Guid.NewGuid())
            .RecoverInterruptedAsync();

        Assert.Equal(UpgradeOutcome.NeedsManualRecovery, result.Outcome);
        Assert.Equal("operation_identity_mismatch", result.ErrorCode);
        Assert.Empty(ports.Calls);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Success_uses_verified_order_and_commits_after_both_exact_removals()
    {
        var ports = new FakePorts();
        var (result, journal) = await ExecuteAsync(ports);

        Assert.Equal(UpgradeOutcome.Succeeded, result.Outcome);
        Assert.Equal(
            ["Inspect", "AcquirePayload:StructuraConnector", "AcquirePayload:PlatformConnector", "EnsureNetBird", "InstallUnified", "Enroll", "VerifyAccess", "Remove:StructuraConnector", "Remove:PlatformConnector", "VerifyFinal", "ReleasePayload:PlatformConnector", "ReleasePayload:StructuraConnector"],
            ports.Calls);
        Assert.Equal(UpgradeJournalState.Committed, journal.State);
        Assert.True(
            journal.Events.FindIndex(item => item.Kind == UpgradeJournalEventKind.ObservationPassed && item.Code == "new_access_verified") <
            journal.Events.FindIndex(item => item.Mutation == UpgradeMutation.RemoveStructuraConnector));
        Assert.Empty(journal.Recovery!.Preflight.RollbackPayloads);
        Assert.NotNull(journal.Recovery.PlatformEnrollmentReceipt);
        Assert.Equal(2, journal.Recovery.LegacyRemovalReceipts.Count);
        Assert.Equal(2, journal.Recovery.ProtectedRollbackPayloads.Count);
    }

    [Fact]
    public async Task Execute_handles_zero_one_or_two_legacy_msi_from_explicit_presence_baseline()
    {
        var cases = new LegacyApplicationKind[][]
        {
            [],
            [LegacyApplicationKind.PlatformConnector],
            [LegacyApplicationKind.StructuraConnector, LegacyApplicationKind.PlatformConnector],
        };

        for (var index = 0; index < cases.Length; index++)
        {
            Directory.CreateDirectory(_root);
            var present = cases[index];
            var ports = new FakePorts { Preflight = FakePorts.CreatePreflight(NetBirdOwnership.Absent, present) };
            ports.InstalledLegacyKinds.Clear();
            ports.InstalledLegacyKinds.UnionWith(present);
            using var store = new FileUpgradeJournalStore(Path.Combine(_root, $"presence-{index}.json"));

            var result = await new UpgradeOrchestrator(ports, store).ExecuteAsync(new OneTimePlatformToken("new-token"));
            var journal = (await store.LoadAsync(CancellationToken.None))!;

            Assert.Equal(UpgradeOutcome.Succeeded, result.Outcome);
            Assert.Equal(2, journal.Recovery!.Preflight.LegacyPresenceBaseline!.Count);
            Assert.Equal(present.Length, journal.Recovery.Preflight.LegacyApplications.Count);
            Assert.Equal(present.Length, journal.Recovery.ProtectedRollbackPayloads.Count);
            Assert.Equal(present.Length, journal.Recovery.LegacyRemovalReceipts.Count);
            Assert.Equal(present.Select(kind => $"AcquirePayload:{kind}"),
                ports.Calls.Where(call => call.StartsWith("AcquirePayload:", StringComparison.Ordinal)));
            Assert.Equal(present.Select(kind => $"Remove:{kind}"),
                ports.Calls.Where(call => call.StartsWith("Remove:", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task Rollback_restores_exactly_the_legacy_msis_present_at_preflight()
    {
        var cases = new LegacyApplicationKind[][]
        {
            [],
            [LegacyApplicationKind.StructuraConnector],
            [LegacyApplicationKind.StructuraConnector, LegacyApplicationKind.PlatformConnector],
        };

        for (var index = 0; index < cases.Length; index++)
        {
            Directory.CreateDirectory(_root);
            var present = cases[index];
            var ports = new FakePorts
            {
                Preflight = FakePorts.CreatePreflight(NetBirdOwnership.Absent, present),
                FailOperation = "VerifyFinal",
            };
            ports.InstalledLegacyKinds.Clear();
            ports.InstalledLegacyKinds.UnionWith(present);
            using var store = new FileUpgradeJournalStore(Path.Combine(_root, $"rollback-presence-{index}.json"));

            var result = await new UpgradeOrchestrator(ports, store).ExecuteAsync(new OneTimePlatformToken("new-token"));
            var journal = (await store.LoadAsync(CancellationToken.None))!;

            Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
            Assert.Equal(present.Order(), ports.InstalledLegacyKinds.Order());
            Assert.Equal(present.Select(kind => $"Restore:{kind}:").Count(),
                ports.Calls.Count(call => call.StartsWith("Restore:", StringComparison.Ordinal)));
            Assert.Equal(UpgradeJournalState.RolledBack, journal.State);
        }
    }

    [Fact]
    public async Task Interrupted_recovery_restores_missing_installed_baseline_but_never_initially_absent_msi()
    {
        Directory.CreateDirectory(_root);
        var initiallyInstalled = new[] { LegacyApplicationKind.PlatformConnector };
        var journal = CreatePostRemovalInterruptedJournal(
            UpgradePhase.RemovePlatformConnector,
            UpgradeJournalEventKind.MutationIntent,
            UpgradeMutation.RemovePlatformConnector,
            initiallyInstalled,
            initiallyInstalledKinds: initiallyInstalled);
        var ports = new FakePorts();
        ports.InstalledLegacyKinds.Clear(); // Both are absent after the interrupted Platform uninstall.

        var (result, recovered) = await RecoverAsync(ports, journal);

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.Equal(["ReacquirePayload:PlatformConnector", "RestoreMissing:PlatformConnector"],
            ports.Calls.Where(call => call.StartsWith("ReacquirePayload:", StringComparison.Ordinal) ||
                                      call.StartsWith("RestoreMissing:", StringComparison.Ordinal)));
        Assert.DoesNotContain("RestoreMissing:StructuraConnector", ports.Calls);
        Assert.DoesNotContain(LegacyApplicationKind.StructuraConnector, ports.InstalledLegacyKinds);
        Assert.Contains(LegacyApplicationKind.PlatformConnector, ports.InstalledLegacyKinds);
        Assert.Equal(UpgradeJournalState.RolledBack, recovered.State);
    }

    [Fact]
    public async Task Interrupted_recovery_fails_closed_if_an_initially_absent_msi_appears_concurrently()
    {
        Directory.CreateDirectory(_root);
        var initiallyInstalled = new[] { LegacyApplicationKind.PlatformConnector };
        var journal = CreatePostRemovalInterruptedJournal(
            UpgradePhase.RemovePlatformConnector,
            UpgradeJournalEventKind.MutationIntent,
            UpgradeMutation.RemovePlatformConnector,
            initiallyInstalled,
            initiallyInstalledKinds: initiallyInstalled);
        var ports = new FakePorts();
        ports.InstalledLegacyKinds.Clear();
        ports.InstalledLegacyKinds.Add(LegacyApplicationKind.StructuraConnector);

        var (result, recovered) = await RecoverAsync(ports, journal);

        Assert.Equal(UpgradeOutcome.NeedsManualRecovery, result.Outcome);
        Assert.Equal("recovery_legacy_baseline_conflict", recovered.FailureCode);
        Assert.DoesNotContain(ports.Calls, call => call.StartsWith("ReacquirePayload:", StringComparison.Ordinal));
        Assert.DoesNotContain(ports.Calls, call => call.StartsWith("RestoreMissing:", StringComparison.Ordinal));
        Assert.Contains(LegacyApplicationKind.StructuraConnector, ports.InstalledLegacyKinds);
    }

    [Fact]
    public async Task Interrupted_v6_journal_without_presence_baseline_requires_manual_recovery()
    {
        Directory.CreateDirectory(_root);
        var journal = CreatePostRemovalInterruptedJournal(
            UpgradePhase.RemoveStructuraConnector,
            UpgradeJournalEventKind.MutationIntent,
            UpgradeMutation.RemoveStructuraConnector,
            [LegacyApplicationKind.StructuraConnector]);
        journal = journal with
        {
            SchemaVersion = 6,
            Recovery = journal.Recovery! with
            {
                Preflight = journal.Recovery.Preflight with { LegacyPresenceBaseline = null },
            },
        };
        var ports = new FakePorts();

        var (result, recovered) = await RecoverAsync(ports, journal);

        Assert.Equal(UpgradeOutcome.NeedsManualRecovery, result.Outcome);
        Assert.Equal("recovery_metadata_unproven", recovered.FailureCode);
        Assert.DoesNotContain(ports.Calls, call => call.StartsWith("ReacquirePayload:", StringComparison.Ordinal));
        Assert.DoesNotContain(ports.Calls, call => call.StartsWith("RestoreMissing:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_rejects_missing_or_unknown_legacy_presence_instead_of_inferring_absence()
    {
        foreach (var baseline in new LegacyApplicationPresenceBaseline[]?[]
                 {
                     null,
                     FakePorts.CreatePreflight(NetBirdOwnership.Absent).LegacyPresenceBaseline!
                         .Select(item => item.Identity.Kind == LegacyApplicationKind.StructuraConnector
                             ? item with { Presence = ExactLegacyPresence.Unknown }
                             : item)
                         .ToArray(),
                 })
        {
            Directory.CreateDirectory(_root);
            var ports = new FakePorts
            {
                Preflight = FakePorts.CreatePreflight(NetBirdOwnership.Absent) with
                {
                    LegacyPresenceBaseline = baseline,
                },
            };
            using var store = new FileUpgradeJournalStore(Path.Combine(_root, $"invalid-presence-{Guid.NewGuid():N}.json"));

            var result = await new UpgradeOrchestrator(ports, store).ExecuteAsync(new OneTimePlatformToken("new-token"));

            Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
            Assert.DoesNotContain(ports.Calls, call => call is "EnsureNetBird" or "InstallUnified" or "Enroll" ||
                                                       call.StartsWith("AcquirePayload:", StringComparison.Ordinal) ||
                                                       call.StartsWith("Remove:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Reboot_required_from_exact_removal_is_journaled_and_survives_committed_retry()
    {
        var ports = new FakePorts { RebootRequiredOnRemoval = true };
        var (result, journal) = await ExecuteAsync(ports);

        Assert.Equal(UpgradeOutcome.Succeeded, result.Outcome);
        Assert.True(result.RebootRequired);
        Assert.All(journal.Recovery!.LegacyRemovalReceipts, receipt => Assert.True(receipt.RebootRequired));

        using var store = new FileUpgradeJournalStore(Path.Combine(_root, "journal.json"));
        var retry = await new UpgradeOrchestrator(new FakePorts(), store)
            .ExecuteAsync(new OneTimePlatformToken("unused"));
        Assert.Equal(UpgradeOutcome.AlreadyCommitted, retry.Outcome);
        Assert.True(retry.RebootRequired);
    }

    [Fact]
    public async Task NetBird_reboot_required_is_journaled_and_survives_committed_retry()
    {
        var ports = new FakePorts { RebootRequiredOnNetBird = true };
        var (result, journal) = await ExecuteAsync(ports);

        Assert.Equal(UpgradeOutcome.Succeeded, result.Outcome);
        Assert.True(result.RebootRequired);
        Assert.True(journal.Recovery!.NetBirdReceipt!.RebootRequired);

        using var store = new FileUpgradeJournalStore(Path.Combine(_root, "journal.json"));
        var retry = await new UpgradeOrchestrator(new FakePorts(), store)
            .ExecuteAsync(new OneTimePlatformToken("unused"));
        Assert.Equal(UpgradeOutcome.AlreadyCommitted, retry.Outcome);
        Assert.True(retry.RebootRequired);
    }

    [Fact]
    public async Task Failed_access_before_deletion_compensates_only_completed_new_components()
    {
        var ports = new FakePorts { AccessVerification = new(false, true, true) };
        var (result, journal) = await ExecuteAsync(ports);

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.DoesNotContain(ports.Calls, call => call.StartsWith("Remove:", StringComparison.Ordinal));
        Assert.DoesNotContain(ports.Calls, call => call.StartsWith("Restore:", StringComparison.Ordinal));
        Assert.Contains("RemoveEnrollment", ports.Calls);
        Assert.Contains("RemoveUnified", ports.Calls);
        Assert.Contains("CompensateNetBird:InstalledThisRun", ports.Calls);
        Assert.Contains("RestoreUserState", ports.Calls);
        Assert.Equal(UpgradeJournalState.RolledBack, journal.State);
    }

    [Fact]
    public async Task Failure_after_first_legacy_removal_restores_only_that_exact_payload()
    {
        var ports = new FakePorts { FailOperation = "Remove:PlatformConnector" };
        var (result, journal) = await ExecuteAsync(ports);

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.Contains("Restore:StructuraConnector:47B5B343", ports.Calls);
        Assert.DoesNotContain(ports.Calls, call => call.StartsWith("Restore:PlatformConnector", StringComparison.Ordinal));
        Assert.Equal(UpgradeJournalState.RolledBack, journal.State);
    }

    [Fact]
    public async Task Foreign_netbird_is_observed_but_never_changed()
    {
        var ports = new FakePorts
        {
            Preflight = FakePorts.CreatePreflight(NetBirdOwnership.Foreign),
        };
        var (result, journal) = await ExecuteAsync(ports);

        Assert.Equal(UpgradeOutcome.Succeeded, result.Outcome);
        Assert.DoesNotContain("EnsureNetBird", ports.Calls);
        Assert.DoesNotContain("RemoveNetBirdInstalled", ports.Calls);
        Assert.DoesNotContain(ports.Calls, call => call.StartsWith("RestoreNetBird:", StringComparison.Ordinal));
        Assert.Contains(journal.Events, item => item.Kind == UpgradeJournalEventKind.SkippedForeignComponent);
        Assert.Null(journal.Recovery!.Preflight.NetBird.InstallationId);
        Assert.Null(journal.Recovery.Preflight.NetBird.OwnedState);
    }

    [Fact]
    public async Task Preexisting_owned_netbird_with_no_run_delta_survives_failure()
    {
        var ports = new FakePorts
        {
            Preflight = FakePorts.CreatePreflight(NetBirdOwnership.OwnedByConnector),
            NetBirdChange = NetBirdChangeKind.NoChange,
            AccessVerification = new(false, true, true),
        };
        var (result, journal) = await ExecuteAsync(ports);

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.Contains("EnsureNetBird", ports.Calls);
        Assert.DoesNotContain(ports.Calls, call => call.StartsWith("CompensateNetBird:", StringComparison.Ordinal));
        Assert.DoesNotContain(ports.Calls, call => call.StartsWith("RestoreNetBird:", StringComparison.Ordinal));
        Assert.Equal(NetBirdChangeKind.NoChange, journal.Recovery!.NetBirdReceipt!.Change);
        Assert.Equal(NetBirdOperationStatus.Applied, journal.Recovery.NetBirdReceipt.Status);
        Assert.Equal(NetBirdOwnership.OwnedByConnector, journal.Recovery.Preflight.NetBird.Ownership);
        Assert.Null(journal.Recovery.Preflight.NetBird.OwnedState);
        Assert.DoesNotContain(FakePorts.OriginalOwnedNetBird.InstallationId,
            System.Text.Json.JsonSerializer.Serialize(journal.Recovery));
    }

    [Fact]
    public async Task MachineVerifiedPortsAcceptOnlyOpaqueOwnedNetBirdPreflight()
    {
        var ports = new MachineBoundaryFakePorts
        {
            Preflight = FakePorts.CreatePreflight(NetBirdOwnership.OwnedByConnector) with
            {
                NetBird = new NetBirdAssessment(NetBirdOwnership.OwnedByConnector, null),
            },
            NetBirdChange = NetBirdChangeKind.NoChange,
        };
        var (result, journal) = await ExecuteAsync(ports);

        Assert.Equal(UpgradeOutcome.Succeeded, result.Outcome);
        Assert.Equal(NetBirdOwnership.OwnedByConnector, journal.Recovery!.Preflight.NetBird.Ownership);
        Assert.Null(journal.Recovery.Preflight.NetBird.OwnedState);
        Assert.Null(journal.Recovery.Preflight.NetBird.InstallationId);
    }

    [Fact]
    public async Task Updated_owned_netbird_rollback_restores_exact_prior_state_instead_of_uninstalling()
    {
        var ports = new FakePorts
        {
            Preflight = FakePorts.CreatePreflight(NetBirdOwnership.OwnedByConnector),
            NetBirdChange = NetBirdChangeKind.UpdatedThisRun,
            ResultingNetBirdState = FakePorts.UpdatedOwnedNetBird,
            AccessVerification = new(false, true, true),
        };
        var (result, journal) = await ExecuteAsync(ports);

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.Contains("CompensateNetBird:UpdatedThisRun", ports.Calls);
        Assert.DoesNotContain("CompensateNetBird:InstalledThisRun", ports.Calls);
        Assert.Equal(NetBirdOwnership.OwnedByConnector, journal.Recovery!.NetBirdReceipt!.PriorOwnership);
        Assert.Equal(NetBirdOwnership.OwnedByConnector, journal.Recovery.NetBirdReceipt.ResultingOwnership);
        var journalJson = System.Text.Json.JsonSerializer.Serialize(journal.Recovery);
        Assert.DoesNotContain(FakePorts.OriginalOwnedNetBird.InstallationId, journalJson);
        Assert.DoesNotContain(FakePorts.OriginalOwnedNetBird.ConfigurationSha256, journalJson);
        Assert.DoesNotContain(FakePorts.UpdatedOwnedNetBird.ServiceIdentity, journalJson);
        Assert.DoesNotContain("protected-netbird-restore-point", journalJson);
        Assert.DoesNotContain("protected-netbird-target", journalJson);
        Assert.DoesNotContain("SignerSubject", journalJson);
        Assert.DoesNotContain("TargetPackage", journalJson);
    }

    [Fact]
    public async Task Controlled_cancellation_after_mutations_uses_noncancelled_cleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var ports = new FakePorts
        {
            CancelOperation = "VerifyAccess",
            CancellationSource = cancellation,
        };
        var (result, journal) = await ExecuteAsync(ports, cancellation.Token);

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.Equal(UpgradeJournalState.RolledBack, journal.State);
        Assert.NotEmpty(ports.CleanupTokens);
        Assert.All(ports.CleanupTokens, token => Assert.False(token.IsCancellationRequested));
    }

    [Fact]
    public async Task Cancellation_with_pending_mutation_intent_requires_manual_recovery()
    {
        using var cancellation = new CancellationTokenSource();
        var ports = new FakePorts
        {
            CancelOperation = "InstallUnified",
            CancellationSource = cancellation,
        };
        var (result, journal) = await ExecuteAsync(ports, cancellation.Token);

        Assert.Equal(UpgradeOutcome.NeedsManualRecovery, result.Outcome);
        Assert.Equal(UpgradeJournalState.NeedsManualRecovery, journal.State);
        Assert.Contains("CompensateNetBird:InstalledThisRun", ports.Calls);
        Assert.DoesNotContain("RemoveUnified", ports.Calls);
    }

    [Fact]
    public async Task Fake_rollback_asset_pin_is_rejected_before_any_machine_mutation()
    {
        var original = FakePorts.CreatePreflight(NetBirdOwnership.Absent);
        var fake = original.RollbackPayloads[0] with { Sha256 = new string('0', 64) };
        var ports = new FakePorts
        {
            Preflight = original with { RollbackPayloads = [fake, original.RollbackPayloads[1]] },
        };
        var (result, journal) = await ExecuteAsync(ports);

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.Equal(["Inspect"], ports.Calls);
        Assert.Null(journal.Recovery);
    }

    [Fact]
    public async Task Unlocked_legacy_product_identity_is_rejected_before_any_machine_mutation()
    {
        var original = FakePorts.CreatePreflight(NetBirdOwnership.Absent);
        var unlockedIdentity = original.LegacyApplications[0].Identity with { ProductCode = Guid.NewGuid() };
        var unlockedApp = original.LegacyApplications[0] with { Identity = unlockedIdentity };
        var ports = new FakePorts
        {
            Preflight = original with { LegacyApplications = [unlockedApp, original.LegacyApplications[1]] },
        };
        var (result, journal) = await ExecuteAsync(ports);

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.Equal(["Inspect"], ports.Calls);
        Assert.Null(journal.Recovery);
    }

    [Fact]
    public async Task Protected_payload_lease_survives_replacement_attempt_after_verification()
    {
        var ports = new FakePorts
        {
            ReplacePayloadOnOperation = "Remove:PlatformConnector",
            FailOperation = "Remove:PlatformConnector",
        };
        var (result, journal) = await ExecuteAsync(ports);

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.Contains("PayloadReplaced:StructuraConnector", ports.Calls);
        Assert.Contains("Restore:StructuraConnector:47B5B343", ports.Calls);
        Assert.Equal(2, ports.ReleasedPayloadKinds.Count);
        Assert.Equal(UpgradeJournalState.RolledBack, journal.State);
    }

    [Fact]
    public async Task User_writable_payload_handle_is_rejected_and_released_before_mutation()
    {
        var ports = new FakePorts
        {
            PayloadProtection = RollbackPayloadProtection.UnprotectedOrUserWritable,
        };
        var (result, journal) = await ExecuteAsync(ports);

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.Equal(
            ["Inspect", "AcquirePayload:StructuraConnector", "ReleasePayload:StructuraConnector"],
            ports.Calls);
        Assert.Empty(journal.Recovery!.ProtectedRollbackPayloads);
    }

    [Fact]
    public async Task Rollback_failure_is_durably_marked_for_manual_recovery()
    {
        var ports = new FakePorts
        {
            AccessVerification = new(false, false, false),
            FailOperation = "RemoveUnified",
        };
        var (result, journal) = await ExecuteAsync(ports);

        Assert.Equal(UpgradeOutcome.NeedsManualRecovery, result.Outcome);
        Assert.Equal(UpgradeJournalState.NeedsManualRecovery, journal.State);
        Assert.Contains(journal.Events, item =>
            item.Kind == UpgradeJournalEventKind.MutationFailed &&
            item.Mutation == UpgradeMutation.RemoveUnifiedApplication);
    }

    [Fact]
    public async Task Interrupted_intent_is_never_auto_replayed()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "journal.json");
        using var store = new FileUpgradeJournalStore(path);
        var runId = Guid.NewGuid();
        await store.SaveAsync(
            new UpgradeJournalDocument(
                UpgradeJournalDocument.CurrentSchemaVersion,
                runId,
                0,
                UpgradeJournalState.InProgress,
                UpgradePhase.InstallUnifiedApplication,
                [
                    new UpgradeJournalEvent(1, UpgradeJournalEventKind.PhaseEntered, UpgradePhase.InstallUnifiedApplication, null, DateTimeOffset.UtcNow),
                    new UpgradeJournalEvent(2, UpgradeJournalEventKind.MutationIntent, UpgradePhase.InstallUnifiedApplication, UpgradeMutation.InstallUnifiedApplication, DateTimeOffset.UtcNow),
                ]),
            CancellationToken.None);
        var ports = new FakePorts();

        var result = await new UpgradeOrchestrator(ports, store)
            .ExecuteAsync(new OneTimePlatformToken("new-token"));

        Assert.Equal(UpgradeOutcome.Interrupted, result.Outcome);
        Assert.Equal(runId, result.RunId);
        Assert.Empty(ports.Calls);
        Assert.Equal(UpgradeJournalState.InProgress, (await store.LoadAsync(CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Explicit_recovery_after_first_removal_restores_missing_client_and_user_state()
    {
        var ports = new FakePorts { RecoveryUserStateMatchesSnapshot = false };
        ports.InstalledLegacyKinds.Remove(LegacyApplicationKind.StructuraConnector);
        var interrupted = CreateInterruptedRemovalJournal(LegacyApplicationKind.StructuraConnector);

        var (result, journal) = await RecoverAsync(ports, interrupted);

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.Equal(UpgradeJournalState.RolledBack, journal.State);
        Assert.Contains("RestoreMissing:StructuraConnector", ports.Calls);
        Assert.DoesNotContain("RestoreMissing:PlatformConnector", ports.Calls);
        Assert.Contains("RestoreUserState", ports.Calls);
        Assert.DoesNotContain("Enroll", ports.Calls);
        Assert.DoesNotContain(ports.Calls, call => call.StartsWith("Remove:", StringComparison.Ordinal));
        Assert.Equal(2, ports.InstalledLegacyKinds.Count);
        Assert.True(ports.RecoveryUserStateMatchesSnapshot);
    }

    [Theory]
    [InlineData(NetBirdChangeKind.InstalledThisRun, NetBirdInterruptedRecoveryAction.RemovedInstalledThisRun)]
    [InlineData(NetBirdChangeKind.UpdatedThisRun, NetBirdInterruptedRecoveryAction.RestoredPriorOwnedState)]
    [InlineData(NetBirdChangeKind.NoChange, NetBirdInterruptedRecoveryAction.NoMutationObserved)]
    public async Task Interrupted_netbird_plan_is_reconciled_before_legacy_clients_are_touched(
        NetBirdChangeKind change,
        NetBirdInterruptedRecoveryAction recoveryAction)
    {
        var ownership = change == NetBirdChangeKind.InstalledThisRun
            ? NetBirdOwnership.Absent
            : NetBirdOwnership.OwnedByConnector;
        var preflight = FakePorts.CreatePreflight(ownership);
        var plan = FakePorts.CreateNetBirdPlan(preflight.NetBird, change);
        var ports = new FakePorts
        {
            NetBirdRecoveryResult = new(recoveryAction, "netbird-machine-evidence"),
        };

        var (result, journal) = await RecoverAsync(ports, CreateInterruptedNetBirdJournal(preflight, plan));

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.Equal(UpgradeJournalState.RolledBack, journal.State);
        Assert.Contains($"ReconcileNetBird:{change}", ports.Calls);
        Assert.Equal(2, ports.InstalledLegacyKinds.Count);
        Assert.DoesNotContain(ports.Calls, call => call.StartsWith("RestoreMissing:", StringComparison.Ordinal));
        Assert.DoesNotContain(ports.Calls, call => call.StartsWith("Remove:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Explicit_recovery_after_second_removal_restores_both_clients()
    {
        var ports = new FakePorts { RecoveryUserStateMatchesSnapshot = false };
        ports.InstalledLegacyKinds.Clear();
        var interrupted = CreateInterruptedRemovalJournal(LegacyApplicationKind.PlatformConnector);

        var (result, journal) = await RecoverAsync(ports, interrupted);

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.Equal(UpgradeJournalState.RolledBack, journal.State);
        Assert.Contains("RestoreMissing:StructuraConnector", ports.Calls);
        Assert.Contains("RestoreMissing:PlatformConnector", ports.Calls);
        Assert.Equal(2, ports.InstalledLegacyKinds.Count);
        Assert.DoesNotContain("Enroll", ports.Calls);
    }

    [Fact]
    public async Task Recovery_after_first_removal_applied_receipt_restores_missing_client_without_replaying_removal()
    {
        var ports = new FakePorts { RecoveryUserStateMatchesSnapshot = false };
        ports.InstalledLegacyKinds.Remove(LegacyApplicationKind.StructuraConnector);
        var interrupted = CreatePostRemovalInterruptedJournal(
            UpgradePhase.RemoveStructuraConnector,
            UpgradeJournalEventKind.MutationApplied,
            UpgradeMutation.RemoveStructuraConnector,
            LegacyApplicationKind.StructuraConnector);

        var (result, journal) = await RecoverAsync(ports, interrupted);

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.Equal(UpgradeJournalState.RolledBack, journal.State);
        Assert.Contains("RestoreMissing:StructuraConnector", ports.Calls);
        Assert.DoesNotContain("RestoreMissing:PlatformConnector", ports.Calls);
        Assert.DoesNotContain("Enroll", ports.Calls);
        Assert.DoesNotContain(ports.Calls, call => call.StartsWith("Remove:", StringComparison.Ordinal));
        Assert.Equal(2, ports.InstalledLegacyKinds.Count);
    }

    [Fact]
    public async Task Recovery_after_second_removal_applied_receipt_restores_both_clients_without_replaying_removal()
    {
        var ports = new FakePorts { RecoveryUserStateMatchesSnapshot = false };
        ports.InstalledLegacyKinds.Clear();
        var interrupted = CreatePostRemovalInterruptedJournal(
            UpgradePhase.RemovePlatformConnector,
            UpgradeJournalEventKind.MutationApplied,
            UpgradeMutation.RemovePlatformConnector,
            LegacyApplicationKind.StructuraConnector,
            LegacyApplicationKind.PlatformConnector);

        var (result, journal) = await RecoverAsync(ports, interrupted);

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.Equal(UpgradeJournalState.RolledBack, journal.State);
        Assert.Contains("RestoreMissing:StructuraConnector", ports.Calls);
        Assert.Contains("RestoreMissing:PlatformConnector", ports.Calls);
        Assert.DoesNotContain("Enroll", ports.Calls);
        Assert.DoesNotContain(ports.Calls, call => call.StartsWith("Remove:", StringComparison.Ordinal));
        Assert.Equal(2, ports.InstalledLegacyKinds.Count);
    }

    [Fact]
    public async Task Recovery_after_final_observation_before_commit_restores_both_clients_without_forward_replay()
    {
        var ports = new FakePorts { RecoveryUserStateMatchesSnapshot = false };
        ports.InstalledLegacyKinds.Clear();
        var interrupted = CreatePostRemovalInterruptedJournal(
            UpgradePhase.VerifyFinalState,
            UpgradeJournalEventKind.ObservationPassed,
            null,
            LegacyApplicationKind.StructuraConnector,
            LegacyApplicationKind.PlatformConnector,
            lastCode: "final_state_verified");

        var (result, journal) = await RecoverAsync(ports, interrupted);

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.Equal(UpgradeJournalState.RolledBack, journal.State);
        Assert.Contains("RestoreMissing:StructuraConnector", ports.Calls);
        Assert.Contains("RestoreMissing:PlatformConnector", ports.Calls);
        Assert.DoesNotContain("Enroll", ports.Calls);
        Assert.DoesNotContain("InstallUnified", ports.Calls);
        Assert.DoesNotContain(ports.Calls, call => call.StartsWith("Remove:", StringComparison.Ordinal));
        Assert.Equal(2, ports.InstalledLegacyKinds.Count);
    }

    [Fact]
    public async Task Recovery_with_unproven_staged_bytes_fails_closed_with_durable_status()
    {
        var ports = new FakePorts
        {
            RecoveryPayloadProtection = RollbackPayloadProtection.UnprotectedOrUserWritable,
        };
        ports.InstalledLegacyKinds.Remove(LegacyApplicationKind.StructuraConnector);
        var interrupted = CreateInterruptedRemovalJournal(LegacyApplicationKind.StructuraConnector);

        var (result, journal) = await RecoverAsync(ports, interrupted);

        Assert.Equal(UpgradeOutcome.NeedsManualRecovery, result.Outcome);
        Assert.Equal("recovery_payload_unproven", result.ErrorCode);
        Assert.Equal(UpgradeJournalState.NeedsManualRecovery, journal.State);
        Assert.Equal("recovery_payload_unproven", journal.FailureCode);
        Assert.DoesNotContain(ports.Calls, call => call.StartsWith("RestoreMissing:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task File_journal_lease_excludes_a_second_process_for_the_entire_run()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "journal.json");
        using var first = new FileUpgradeJournalStore(path);
        using var second = new FileUpgradeJournalStore(path);

        await using (var firstLease = await first.AcquireLeaseAsync(CancellationToken.None))
        {
            await Assert.ThrowsAsync<UpgradeLeaseUnavailableException>(async () =>
            {
                await using var unexpected = await second.AcquireLeaseAsync(CancellationToken.None);
            });
        }

        await using var acquiredAfterRelease = await second.AcquireLeaseAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<(UpgradeExecutionResult Result, UpgradeJournalDocument Journal)> ExecuteAsync(
        FakePorts ports,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "journal.json");
        using var store = new FileUpgradeJournalStore(path);
        var result = await new UpgradeOrchestrator(ports, store)
            .ExecuteAsync(new OneTimePlatformToken("new-token"), cancellationToken);
        return (result, (await store.LoadAsync(CancellationToken.None))!);
    }

    private async Task<(UpgradeExecutionResult Result, UpgradeJournalDocument Journal)> RecoverAsync(
        FakePorts ports,
        UpgradeJournalDocument interrupted)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "journal.json");
        using var store = new FileUpgradeJournalStore(path);
        await store.SaveAsync(interrupted, CancellationToken.None);
        var result = await new UpgradeOrchestrator(ports, store).RecoverInterruptedAsync();
        return (result, (await store.LoadAsync(CancellationToken.None))!);
    }

    private static UpgradeJournalDocument CreateInterruptedRemovalJournal(LegacyApplicationKind pendingRemoval)
    {
        var phase = pendingRemoval == LegacyApplicationKind.StructuraConnector
            ? UpgradePhase.RemoveStructuraConnector
            : UpgradePhase.RemovePlatformConnector;
        var mutation = pendingRemoval == LegacyApplicationKind.StructuraConnector
            ? UpgradeMutation.RemoveStructuraConnector
            : UpgradeMutation.RemovePlatformConnector;
        var appliedRemovals = pendingRemoval == LegacyApplicationKind.PlatformConnector
            ? new[] { LegacyApplicationKind.StructuraConnector }
            : [];
        return CreatePostRemovalInterruptedJournal(
            phase,
            UpgradeJournalEventKind.MutationIntent,
            mutation,
            appliedRemovals);
    }

    private static UpgradeJournalDocument CreateInterruptedNetBirdJournal(
        UpgradePreflight preflight,
        NetBirdOperationIntent plan)
    {
        var protectedPayloads = preflight.RollbackPayloads.Select(payload =>
            new ProtectedRollbackPayloadReceipt(
                $"protected-{payload.Identity.Kind}",
                payload.Identity.Kind,
                RollbackPayloadProtection.ProtectedMachineStaging)).ToList();
        var persistedPreflight = preflight with
        {
            RollbackPayloads = [],
            NetBird = new NetBirdAssessment(preflight.NetBird.Ownership, null),
        };
        return new UpgradeJournalDocument(
            UpgradeJournalDocument.CurrentSchemaVersion,
            Guid.NewGuid(),
            0,
            UpgradeJournalState.InProgress,
            UpgradePhase.EnsureNetBird,
            [
                new(1, UpgradeJournalEventKind.PhaseEntered, UpgradePhase.EnsureNetBird, null, DateTimeOffset.UtcNow),
                new(2, UpgradeJournalEventKind.MutationIntent, UpgradePhase.EnsureNetBird, UpgradeMutation.EnsureNetBird, DateTimeOffset.UtcNow),
                new(3, UpgradeJournalEventKind.MutationPrepared, UpgradePhase.EnsureNetBird, UpgradeMutation.EnsureNetBird, DateTimeOffset.UtcNow),
            ],
            null,
            new UpgradeRecoveryMetadata(persistedPreflight, protectedPayloads, plan, null, null, null, []));
    }

    private static UpgradeJournalDocument CreatePostRemovalInterruptedJournal(
        UpgradePhase phase,
        UpgradeJournalEventKind lastEventKind,
        UpgradeMutation? lastMutation,
        LegacyApplicationKind firstRemovedKind,
        LegacyApplicationKind? secondRemovedKind = null,
        string? lastCode = null)
    {
        var removedKinds = secondRemovedKind is null
            ? new[] { firstRemovedKind }
            : new[] { firstRemovedKind, secondRemovedKind.Value };
        return CreatePostRemovalInterruptedJournal(
            phase,
            lastEventKind,
            lastMutation,
            removedKinds,
            lastCode);
    }

    private static UpgradeJournalDocument CreatePostRemovalInterruptedJournal(
        UpgradePhase phase,
        UpgradeJournalEventKind lastEventKind,
        UpgradeMutation? lastMutation,
        IReadOnlyCollection<LegacyApplicationKind> removedKinds,
        string? lastCode = null,
        IReadOnlyCollection<LegacyApplicationKind>? initiallyInstalledKinds = null)
    {
        var preflight = FakePorts.CreatePreflight(NetBirdOwnership.Absent, initiallyInstalledKinds);
        var protectedPayloads = preflight.RollbackPayloads.Select(payload =>
            new ProtectedRollbackPayloadReceipt(
                $"protected-{payload.Identity.Kind}",
                payload.Identity.Kind,
                RollbackPayloadProtection.ProtectedMachineStaging)).ToList();
        preflight = preflight with
        {
            RollbackPayloads = [],
            NetBird = new NetBirdAssessment(preflight.NetBird.Ownership, null),
        };
        var appliedRemovals = removedKinds.Select(kind => new LegacyRemovalReceipt(
            preflight.LegacyApplications.Single(item => item.Identity.Kind == kind).Identity,
            $"remove-{kind}"))
            .ToList();
        var netBirdPlan = FakePorts.CreateNetBirdPlan(preflight.NetBird, NetBirdChangeKind.InstalledThisRun);
        var recovery = new UpgradeRecoveryMetadata(
            preflight,
            protectedPayloads,
            netBirdPlan,
            new NetBirdOperationReceipt(
                netBirdPlan.OperationId,
                netBirdPlan.PriorOwnership,
                NetBirdChangeKind.InstalledThisRun,
                NetBirdOperationStatus.Applied,
                NetBirdOwnership.OwnedByConnector),
            new UnifiedApplicationReceipt("unified-op"),
            new PlatformEnrollmentReceipt("new-enrollment"),
            appliedRemovals);
        var events = new List<UpgradeJournalEvent>
        {
            new(1, UpgradeJournalEventKind.PhaseEntered, phase, null, DateTimeOffset.UtcNow),
            new(2, lastEventKind, phase, lastMutation, DateTimeOffset.UtcNow, lastCode),
        };
        return new UpgradeJournalDocument(
            UpgradeJournalDocument.CurrentSchemaVersion,
            Guid.NewGuid(),
            0,
            UpgradeJournalState.InProgress,
            phase,
            events,
            null,
            recovery);
    }

    private class FakePorts : IUpgradePorts
    {
        public static readonly NetBirdOwnedState OriginalOwnedNetBird = new(
            "netbird-existing",
            "0.79.0",
            new string('A', 64),
            "NT SERVICE\\NetBird");
        public static readonly NetBirdOwnedState UpdatedOwnedNetBird = new(
            "netbird-existing",
            "0.80.0",
            new string('B', 64),
            "NT SERVICE\\NetBird");
        public static readonly NetBirdOwnedState NewlyInstalledNetBird = new(
            "netbird-new",
            "0.79.0",
            new string('C', 64),
            "NT SERVICE\\NetBird");
        public const string NetBirdOperationId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private static readonly LegacyApplicationIdentity Structura = new(
            LegacyApplicationKind.StructuraConnector,
            Guid.Parse("8C16FFEC-F35D-45AF-BE71-65BB07533BF8"),
            Guid.Parse("0E67CBE8-8F77-45EA-B89D-E58C8C554B37"));
        private static readonly LegacyApplicationIdentity Platform = new(
            LegacyApplicationKind.PlatformConnector,
            Guid.Parse("93CCDE79-6601-4232-8489-8A6435FD6D62"),
            Guid.Parse("A7E5408C-1238-4A45-8A84-E3AE45107D7A"));

        public List<string> Calls { get; } = [];
        public List<CancellationToken> CleanupTokens { get; } = [];
        public List<LegacyApplicationKind> ReleasedPayloadKinds { get; } = [];
        public HashSet<LegacyApplicationKind> InstalledLegacyKinds { get; } =
            [LegacyApplicationKind.StructuraConnector, LegacyApplicationKind.PlatformConnector];
        public UpgradePreflight Preflight { get; set; } = CreatePreflight(NetBirdOwnership.Absent);
        public NewAccessVerification AccessVerification { get; set; } = new(true, true, true);
        public NetBirdChangeKind NetBirdChange { get; set; } = NetBirdChangeKind.InstalledThisRun;
        public NetBirdOwnedState ResultingNetBirdState { get; set; } = NewlyInstalledNetBird;
        public NetBirdInterruptedRecoveryResult NetBirdRecoveryResult { get; set; } =
            new(NetBirdInterruptedRecoveryAction.NoMutationObserved, "netbird-recovery-evidence");
        public string? FailOperation { get; set; }
        public string? CancelOperation { get; set; }
        public CancellationTokenSource? CancellationSource { get; set; }
        public string? ReplacePayloadOnOperation { get; set; }
        public RollbackPayloadProtection PayloadProtection { get; set; } = RollbackPayloadProtection.ProtectedMachineStaging;
        public RollbackPayloadProtection RecoveryPayloadProtection { get; set; } = RollbackPayloadProtection.ProtectedMachineStaging;
        public bool RecoveryUserStateMatchesSnapshot { get; set; } = true;
        public bool RebootRequiredOnRemoval { get; set; }
        public bool RebootRequiredOnNetBird { get; set; }
        private string _liveStructuraPayloadSha256 = "47B5B3434CED41C3B9A71615906CBAF835B6CB45433554BE612E7BF2D88EF515";

        public static UpgradePreflight CreatePreflight(
            NetBirdOwnership ownership,
            IReadOnlyCollection<LegacyApplicationKind>? installedKinds = null)
        {
            installedKinds ??= [LegacyApplicationKind.StructuraConnector, LegacyApplicationKind.PlatformConnector];
            var legacy = new[]
            {
                (Kind: LegacyApplicationKind.StructuraConnector, Identity: Structura, Version: "1.0.31",
                    PackageId: "structura-connector", Installer: "Connector.Desktop.Setup.msi", Size: 114074894L,
                    Sha256: "47B5B3434CED41C3B9A71615906CBAF835B6CB45433554BE612E7BF2D88EF515"),
                (Kind: LegacyApplicationKind.PlatformConnector, Identity: Platform, Version: "1.2.1",
                    PackageId: "platform-connector", Installer: "Platform.Connector.Desktop.Setup.msi", Size: 59929448L,
                    Sha256: "D6666FD1613B16879AB6FDA9912D3476D0BA0BE2A782A7F934772D9F89DEDC3D"),
            };
            var present = legacy.Where(item => installedKinds.Contains(item.Kind)).ToArray();
            var baseline = legacy.Select(item => new LegacyApplicationPresenceBaseline(
                item.Identity,
                installedKinds.Contains(item.Kind) ? ExactLegacyPresence.ExactInstalled : ExactLegacyPresence.Absent,
                $"fixture-{item.Kind}-presence",
                installedKinds.Contains(item.Kind) ? item.Version : null)).ToArray();
            return new UpgradePreflight(
                "S-1-5-21-current",
                present.Select(item => new InstalledLegacyApplication(item.Identity, item.Version, true)).ToArray(),
                present.Select(item => new LegacyRollbackPayload(item.Identity, item.PackageId, item.Installer,
                    item.Version, item.Size, item.Sha256, true)).ToArray(),
                new("S-1-5-21-current", "snapshot-1", "state-sha", true),
                new(
                    ownership,
                    ownership == NetBirdOwnership.Absent ? null : "netbird-existing",
                    ownership == NetBirdOwnership.OwnedByConnector ? OriginalOwnedNetBird : null),
                true,
                true,
                baseline);
        }

        public static NetBirdOperationIntent CreateNetBirdPlan(
            NetBirdAssessment assessment,
            NetBirdChangeKind change)
        {
            return new NetBirdOperationIntent(
                NetBirdOperationId,
                assessment.Ownership,
                change,
                NetBirdOperationStatus.Prepared);
        }

        public ValueTask<UpgradePreflight> InspectAsync(CancellationToken cancellationToken)
        {
            Record("Inspect");
            return ValueTask.FromResult(Preflight);
        }

        public ValueTask<NetBirdOperationIntent> PrepareOwnedNetBirdMutationAsync(
            NetBirdAssessment assessment,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(CreateNetBirdPlan(assessment, NetBirdChange));
        }

        public ValueTask<NetBirdOperationReceipt> ApplyOwnedNetBirdMutationAsync(
            NetBirdOperationIntent plan,
            CancellationToken cancellationToken)
        {
            Record("EnsureNetBird");
            return ValueTask.FromResult(new NetBirdOperationReceipt(
                plan.OperationId,
                plan.PriorOwnership,
                plan.Change,
                NetBirdOperationStatus.Applied,
                plan.Change == NetBirdChangeKind.InstalledThisRun || plan.PriorOwnership == NetBirdOwnership.OwnedByConnector
                    ? NetBirdOwnership.OwnedByConnector
                    : plan.PriorOwnership,
                RebootRequiredOnNetBird));
        }

        public ValueTask<NetBirdInterruptedRecoveryResult> ReconcileInterruptedNetBirdAsync(
            NetBirdOperationIntent plan,
            CancellationToken cancellationToken)
        {
            RecordCleanup($"ReconcileNetBird:{plan.Change}", cancellationToken);
            return ValueTask.FromResult(NetBirdRecoveryResult);
        }

        public ValueTask CompensateNetBirdMutationAsync(
            NetBirdOperationIntent operation,
            NetBirdOperationReceipt receipt,
            CancellationToken cancellationToken)
        {
            Assert.Equal(NetBirdOperationId, operation.OperationId);
            Assert.Equal(operation.OperationId, receipt.OperationId);
            RecordCleanup($"CompensateNetBird:{receipt.Change}", cancellationToken);
            return ValueTask.CompletedTask;
        }

        public ValueTask<UnifiedApplicationReceipt> InstallUnifiedApplicationAsync(CancellationToken cancellationToken)
        {
            Record("InstallUnified");
            return ValueTask.FromResult(new UnifiedApplicationReceipt("unified-op"));
        }

        public ValueTask RemoveUnifiedApplicationAsync(UnifiedApplicationReceipt receipt, CancellationToken cancellationToken)
        {
            RecordCleanup("RemoveUnified", cancellationToken);
            return ValueTask.CompletedTask;
        }

        public ValueTask<PlatformEnrollmentReceipt> EnrollAsync(OneTimePlatformToken token, CancellationToken cancellationToken)
        {
            Assert.Equal("new-token", token.Value);
            Record("Enroll");
            return ValueTask.FromResult(new PlatformEnrollmentReceipt("new-enrollment"));
        }

        public ValueTask RemoveNewEnrollmentAsync(PlatformEnrollmentReceipt receipt, CancellationToken cancellationToken)
        {
            Assert.Equal("new-enrollment", receipt.EnrollmentId);
            RecordCleanup("RemoveEnrollment", cancellationToken);
            return ValueTask.CompletedTask;
        }

        public ValueTask<NewAccessVerification> VerifyNewAccessAsync(PlatformEnrollmentReceipt receipt, CancellationToken cancellationToken)
        {
            Record("VerifyAccess");
            return ValueTask.FromResult(AccessVerification);
        }

        public ValueTask<LegacyRemovalReceipt> RemoveExactLegacyApplicationAsync(LegacyApplicationIdentity identity, CancellationToken cancellationToken)
        {
            Record($"Remove:{identity.Kind}");
            InstalledLegacyKinds.Remove(identity.Kind);
            return ValueTask.FromResult(new LegacyRemovalReceipt(
                identity,
                $"remove-{identity.Kind}",
                RebootRequiredOnRemoval));
        }

        public ValueTask<InterruptedUpgradeInspection> InspectInterruptedUpgradeAsync(
            UpgradeRecoveryMetadata recovery,
            CancellationToken cancellationToken)
        {
            Calls.Add("InspectRecovery");
            var observations = recovery.Preflight.LegacyPresenceBaseline!
                .Select(item => new LegacyRecoveryObservation(
                    item.Identity,
                    InstalledLegacyKinds.Contains(item.Identity.Kind)
                        ? ExactLegacyPresence.ExactInstalled
                        : ExactLegacyPresence.Absent,
                    $"observation-{item.Identity.Kind}"))
                .ToArray();
            return ValueTask.FromResult(new InterruptedUpgradeInspection(
                recovery.Preflight.CurrentWindowsUserId,
                observations,
                true,
                RecoveryUserStateMatchesSnapshot));
        }

        public ValueTask<IVerifiedRollbackPayloadLease> ReacquireVerifiedRollbackPayloadAsync(
            ProtectedRollbackPayloadReceipt durableReceipt,
            CancellationToken cancellationToken)
        {
            var pin = LegacyUpgradeLock.LoadEmbedded().Get(durableReceipt.Kind);
            var inspection = new RollbackPayloadInspection(
                pin.Identity, pin.PackageId, pin.InstallerName, pin.Version,
                pin.SizeBytes, pin.Sha256, true);
            Calls.Add($"ReacquirePayload:{durableReceipt.Kind}");
            IVerifiedRollbackPayloadLease lease = new FakePayloadLease(
                durableReceipt.HandleId,
                inspection,
                RecoveryPayloadProtection,
                () => Calls.Add($"ReleaseRecoveryPayload:{durableReceipt.Kind}"));
            return ValueTask.FromResult(lease);
        }

        public ValueTask RestoreMissingLegacyApplicationAsync(
            IVerifiedRollbackPayloadLease payloadLease,
            LegacyRecoveryObservation absentApplication,
            CancellationToken cancellationToken)
        {
            Assert.Equal(ExactLegacyPresence.Absent, absentApplication.Presence);
            Assert.Equal(payloadLease.Inspection.Identity, absentApplication.Identity);
            RecordCleanup($"RestoreMissing:{absentApplication.Identity.Kind}", cancellationToken);
            InstalledLegacyKinds.Add(absentApplication.Identity.Kind);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IVerifiedRollbackPayloadLease> AcquireVerifiedRollbackPayloadAsync(
            LegacyRollbackPayload payload,
            CancellationToken cancellationToken)
        {
            Record($"AcquirePayload:{payload.Identity.Kind}");
            var inspection = new RollbackPayloadInspection(
                payload.Identity,
                payload.PackageId,
                payload.InstallerName,
                payload.Version,
                payload.SizeBytes,
                payload.Sha256,
                true);
            IVerifiedRollbackPayloadLease lease = new FakePayloadLease(
                $"protected-{payload.Identity.Kind}",
                inspection,
                PayloadProtection,
                () =>
                {
                    ReleasedPayloadKinds.Add(payload.Identity.Kind);
                    Calls.Add($"ReleasePayload:{payload.Identity.Kind}");
                });
            return ValueTask.FromResult(lease);
        }

        public ValueTask RestoreExactLegacyApplicationAsync(
            IVerifiedRollbackPayloadLease payloadLease,
            LegacyRemovalReceipt removal,
            CancellationToken cancellationToken)
        {
            Assert.Equal(payloadLease.Inspection.Identity, removal.Identity);
            Assert.Equal(RollbackPayloadProtection.ProtectedMachineStaging, payloadLease.Protection);
            if (removal.Identity.Kind == LegacyApplicationKind.StructuraConnector &&
                ReplacePayloadOnOperation is not null)
                Assert.NotEqual(_liveStructuraPayloadSha256, payloadLease.Inspection.Sha256);
            RecordCleanup(
                $"Restore:{payloadLease.Inspection.Identity.Kind}:{payloadLease.Inspection.Sha256[..8]}",
                cancellationToken);
            InstalledLegacyKinds.Add(removal.Identity.Kind);
            return ValueTask.CompletedTask;
        }

        public ValueTask RestoreUserStateAsync(UserStateSnapshot snapshot, CancellationToken cancellationToken)
        {
            RecordCleanup("RestoreUserState", cancellationToken);
            RecoveryUserStateMatchesSnapshot = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask<FinalUpgradeVerification> VerifyFinalStateAsync(CancellationToken cancellationToken)
        {
            Record("VerifyFinal");
            return ValueTask.FromResult(new FinalUpgradeVerification(true, true, true));
        }

        private void Record(string operation)
        {
            Calls.Add(operation);
            if (ReplacePayloadOnOperation == operation)
            {
                _liveStructuraPayloadSha256 = new string('0', 64);
                Calls.Add("PayloadReplaced:StructuraConnector");
            }
            if (CancelOperation == operation)
            {
                CancellationSource?.Cancel();
                throw new OperationCanceledException(CancellationSource?.Token ?? new CancellationToken(canceled: true));
            }
            if (FailOperation == operation) throw new InvalidOperationException($"Injected failure at {operation}.");
        }

        private void RecordCleanup(string operation, CancellationToken cancellationToken)
        {
            CleanupTokens.Add(cancellationToken);
            Record(operation);
        }

        private sealed class FakePayloadLease(
            string handleId,
            RollbackPayloadInspection inspection,
            RollbackPayloadProtection protection,
            Action release) : IVerifiedRollbackPayloadLease
        {
            private bool _released;

            public string HandleId => handleId;
            public RollbackPayloadProtection Protection => protection;
            public RollbackPayloadInspection Inspection => inspection;

            public ValueTask DisposeAsync()
            {
                if (!_released)
                {
                    _released = true;
                    release();
                }
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class MachineBoundaryFakePorts : FakePorts, IUpgradeMachineVerifiedNetBirdPorts { }
}
