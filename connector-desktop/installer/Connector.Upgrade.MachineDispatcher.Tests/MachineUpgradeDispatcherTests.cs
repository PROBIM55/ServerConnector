using Connector.Upgrade.Core;
using Connector.Upgrade.MachineDispatcher;
using Connector.Upgrade.MachineIpc;
using Connector.Upgrade.MachineJournal;
using Connector.Upgrade.NetBirdMachine;
using Connector.Upgrade.WindowsHost;
using Connector.Upgrade.WindowsMsi;
using Connector.Upgrade.Velopack;
using Connector.Upgrade.VelopackPayload;
using Connector.Upgrade.WindowsUserStage;
using Connector.Upgrade.WindowsPayload;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Connector.Upgrade.MachineDispatcher.Tests;

public sealed class MachineUpgradeDispatcherTests
{
    private static readonly Guid OperationId = Guid.Parse("24c21d46-1f4e-4cab-a00b-e96c7954626d");
    private const string Sid = "S-1-5-18";
    private const string EvidenceId = "netbird-windows-v1:" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly NetBirdOwnedState OwnedState = new("nb-1", "0.40.1", new string('c', 64),
        "netbird-service-v1:" + new string('d', 64));

    [Fact]
    public async Task Rollover_uses_authenticated_dispatcher_identity_and_requires_exact_successor()
    {
        var previousId = Guid.NewGuid();
        var journal = new FakeJournal();
        journal.ReplaceForTest(new MachineUpgradeJournalDocument(
            MachineUpgradeJournalDocument.CurrentSchemaVersion, previousId, Sid, 3,
            MachineUpgradeState.RolledBack, MachineUpgradePhase.Rollback, null, null, null,
            new ProtectedVelopackReceiptIdentifiers(null, null)));
        var nextId = Guid.NewGuid();
        using var dispatcher = new MachineUpgradeDispatcher(nextId, Sid, journal, new FakeNetBirdPort());

        var rejected = await dispatcher.DispatchAsync(new MachineIpcRequest(1, Guid.NewGuid(),
            MachineIpcOperation.Rollover, previousId, Guid.NewGuid()));
        var completed = await dispatcher.DispatchAsync(new MachineIpcRequest(1, Guid.NewGuid(),
            MachineIpcOperation.Rollover, previousId, nextId));

        Assert.Equal(MachineDispatcherCode.InvalidRequest, rejected.Code);
        Assert.Equal(MachineDispatcherStatus.Completed, completed.Status);
        Assert.Equal(nextId, journal.Document!.OperationId);
        Assert.Equal(Sid, journal.Document.InitiatingSid);
        Assert.Equal(MachineUpgradeState.InProgress, journal.Document.State);
        using var staleDispatcher = new MachineUpgradeDispatcher(previousId, Sid, journal, new FakeNetBirdPort());
        var replay = await staleDispatcher.DispatchAsync(Request(MachineIpcOperation.Prepare));
        Assert.Equal(MachineDispatcherCode.JournalOwnershipMismatch, replay.Code);
    }

    [Fact]
    public async Task Inspect_persists_assessment_before_return_and_duplicate_does_not_read_again()
    {
        var journal = new FakeJournal();
        var port = new FakeNetBirdPort();
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        var request = Request(MachineIpcOperation.Inspect);

        var first = await dispatcher.DispatchAsync(request);
        var duplicate = await dispatcher.DispatchAsync(request);

        Assert.Equal(MachineDispatcherStatus.Completed, first.Status);
        Assert.Equal(1, first.Revision);
        Assert.Equal(first.Revision, journal.Document!.Revision);
        Assert.Equal(MachineUpgradePhase.AssessNetBird, journal.Document.Phase);
        Assert.Equal(1, journal.SaveCount);
        Assert.Equal(1, port.InspectCount);
        Assert.Equal(first, duplicate);
    }

    [Fact]
    public async Task Prepare_reuses_durable_plan_for_duplicate_correlation_and_new_correlation()
    {
        var journal = new FakeJournal();
        var port = new FakeNetBirdPort();
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        await dispatcher.DispatchAsync(Request(MachineIpcOperation.Inspect));
        var request = Request(MachineIpcOperation.Prepare);

        var first = await dispatcher.DispatchAsync(request);
        var duplicate = await dispatcher.DispatchAsync(request);
        var repeated = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Prepare));

        Assert.Equal(MachineDispatcherStatus.Completed, first.Status);
        Assert.Equal(MachineUpgradePhase.PrepareNetBirdMutation, first.Phase);
        Assert.Equal(3, first.Revision);
        Assert.Equal(1, port.PrepareCount);
        Assert.Equal(OperationId, port.PreparedOperationId);
        Assert.Equal(3, journal.SaveCount);
        Assert.Equal(first with { CorrelationId = duplicate.CorrelationId }, duplicate);
        Assert.Equal(MachineDispatcherStatus.Completed, repeated.Status);
        Assert.Equal(1, port.PrepareCount);
    }

    [Fact]
    public async Task Foreign_journal_owner_is_blocked_without_calling_port()
    {
        var journal = new FakeJournal { RejectOwner = true };
        var port = new FakeNetBirdPort();
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);

        var result = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Inspect));

        Assert.Equal(MachineDispatcherStatus.Blocked, result.Status);
        Assert.Equal(MachineDispatcherCode.JournalOwnershipMismatch, result.Code);
        Assert.Equal(0, port.InspectCount);
        Assert.Null(journal.Document);
    }

    [Fact]
    public async Task Foreign_or_unattributed_assessment_never_reaches_prepare_or_apply()
    {
        var journal = new FakeJournal();
        var port = new FakeNetBirdPort
        {
            AssessmentResult = new NetBirdAssessment(NetBirdOwnership.Unattributed, null),
        };
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);

        var inspected = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Inspect));
        var prepared = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Prepare));

        Assert.Equal(NetBirdOwnership.Unattributed, inspected.NetBirdOwnership);
        Assert.Equal(MachineDispatcherStatus.Blocked, prepared.Status);
        Assert.Equal(MachineDispatcherCode.UnsafeNetBirdOwnership, prepared.Code);
        Assert.Equal(0, port.PrepareCount);
        Assert.Equal(0, port.ApplyCount);
        Assert.Equal(MachineUpgradePhase.AssessNetBird, journal.Document!.Phase);
    }

    [Fact]
    public async Task Foreign_or_unattributed_durable_assessment_never_reaches_reconcile()
    {
        var journal = new FakeJournal();
        var port = new FakeNetBirdPort();
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        await PrepareAsync(dispatcher);
        var foreign = new NetBirdAssessment(NetBirdOwnership.Foreign, Guid.NewGuid().ToString("D"));
        journal.ReplaceForTest(journal.Document! with
        {
            Revision = journal.Document.Revision + 1,
            Phase = MachineUpgradePhase.MutateNetBird,
            NetBirdAssessment = foreign,
            NetBirdMutationPlan = journal.Document.NetBirdMutationPlan! with { PriorAssessment = foreign },
        });

        var result = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Reconcile));

        Assert.Equal(MachineDispatcherStatus.Blocked, result.Status);
        Assert.Equal(MachineDispatcherCode.UnsafeNetBirdOwnership, result.Code);
        Assert.Equal(0, port.ReconcileCount);
    }

    [Fact]
    public async Task Unsupported_stage_request_fails_closed_without_acquiring_journal_or_calling_port()
    {
        var journal = new FakeJournal();
        var port = new FakeNetBirdPort();
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);

        var result = await dispatcher.DispatchAsync(Request(MachineIpcOperation.StageVelopack));
        var rollbackResult = await dispatcher.DispatchAsync(Request(MachineIpcOperation.StageStructuraRollback));

        Assert.Equal(MachineDispatcherStatus.Blocked, result.Status);
        Assert.Equal(MachineDispatcherCode.UnsupportedOperation, result.Code);
        Assert.Equal(MachineDispatcherStatus.Blocked, rollbackResult.Status);
        Assert.Equal(MachineDispatcherCode.UnsupportedOperation, rollbackResult.Code);
        Assert.Equal(0, journal.AcquireCount);
        Assert.Equal(0, port.InspectCount);
        Assert.Equal(0, port.PrepareCount);
        Assert.Equal(0, port.ApplyCount);
        Assert.Equal(0, port.ReconcileCount);
    }

    [Fact]
    public async Task User_state_stage_requires_inspection_and_uses_trusted_session_identity_idempotently()
    {
        var journal = new FakeJournal();
        var port = new FakeNetBirdPort();
        var calls = new List<(string Sid, string Operation)>();
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port,
            prepareUserStateStage: (sid, operation) => calls.Add((sid, operation)));

        var tooEarly = await dispatcher.DispatchAsync(Request(MachineIpcOperation.PrepareUserStateStage));
        Assert.Equal(MachineDispatcherStatus.Blocked, tooEarly.Status);
        Assert.Empty(calls);

        await dispatcher.DispatchAsync(Request(MachineIpcOperation.Inspect));
        var first = await dispatcher.DispatchAsync(Request(MachineIpcOperation.PrepareUserStateStage));
        var replay = await dispatcher.DispatchAsync(Request(MachineIpcOperation.PrepareUserStateStage));

        Assert.Equal(MachineDispatcherStatus.Completed, first.Status);
        Assert.Equal(MachineUpgradePhase.AssessNetBird, first.Phase);
        Assert.Equal([(Sid, OperationId.ToString("N")), (Sid, OperationId.ToString("N"))], calls);
        Assert.Equal(MachineDispatcherStatus.Completed, replay.Status);

        var prepared = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Prepare));
        var tooLate = await dispatcher.DispatchAsync(Request(MachineIpcOperation.PrepareUserStateStage));
        Assert.Equal(MachineDispatcherStatus.Completed, prepared.Status);
        Assert.Equal(MachineDispatcherCode.UserStateStageUnavailable, tooLate.Code);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task User_state_stage_failure_fails_closed_before_netbird_prepare()
    {
        var journal = new FakeJournal();
        var port = new FakeNetBirdPort();
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port,
            prepareUserStateStage: (_, _) => throw new IOException("unsafe or occupied stage"));
        await dispatcher.DispatchAsync(Request(MachineIpcOperation.Inspect));

        var result = await dispatcher.DispatchAsync(Request(MachineIpcOperation.PrepareUserStateStage));

        Assert.Equal(MachineDispatcherStatus.Blocked, result.Status);
        Assert.Equal(MachineDispatcherCode.OperationFailed, result.Code);
        Assert.Equal(0, port.PrepareCount);
    }

    [Fact]
    public async Task User_state_stage_manual_recovery_is_typed_without_disclosing_stage_path()
    {
        const string privateStagePath = @"C:\Users\OriginalUser\AppData\Local\Connector\stage\secret";
        var journal = new FakeJournal();
        var port = new FakeNetBirdPort();
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port,
            prepareUserStateStage: (_, _) => throw new UserStateStageNeedsManualRecoveryException(
                privateStagePath, new InvalidDataException("stage is occupied")));
        await dispatcher.DispatchAsync(Request(MachineIpcOperation.Inspect));

        var result = await dispatcher.DispatchAsync(Request(MachineIpcOperation.PrepareUserStateStage));

        Assert.Equal(MachineDispatcherStatus.Blocked, result.Status);
        Assert.Equal(MachineDispatcherCode.NeedsManualRecovery, result.Code);
        Assert.Null(result.State);
        Assert.Equal(0, port.PrepareCount);
        Assert.DoesNotContain(privateStagePath, System.Text.Json.JsonSerializer.Serialize(result),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StageVelopack_persists_only_opaque_handle_and_replay_reacquires_without_copying()
    {
        var journal = new FakeJournal();
        var stager = new FakeSetupStager();
        MachineDispatcherResult result;
        using (var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, new FakeNetBirdPort(), stager))
            result = await dispatcher.DispatchAsync(Request(MachineIpcOperation.StageVelopack));
        var restartedStager = new FakeSetupStager();
        using var restartedDispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal,
            new FakeNetBirdPort(), restartedStager);
        var replay = await restartedDispatcher.DispatchAsync(Request(MachineIpcOperation.StageVelopack));
        var requestFrame = System.Text.Encoding.UTF8.GetString(MachineIpcFrameCodec.Encode(Request(MachineIpcOperation.StageVelopack)));
        var resultFrame = System.Text.Encoding.UTF8.GetString(MachineDispatcherResultFrameCodec.Encode(result));

        Assert.Equal(MachineDispatcherStatus.Completed, result.Status);
        Assert.Equal(1, journal.SaveCount);
        Assert.Equal(1, stager.StageCount);
        Assert.Equal(0, stager.ReacquireCount);
        Assert.Equal(1, restartedStager.ReacquireCount);
        Assert.Equal(0, restartedStager.StageCount);
        Assert.Equal(stager.HandleId, journal.Document!.VelopackReceipts.SetupHandleId);
        Assert.Equal(1, journal.Document.Revision);
        Assert.DoesNotContain(stager.HandleId, resultFrame, StringComparison.Ordinal);
        Assert.DoesNotContain("Setup.exe", requestFrame, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Setup.exe", resultFrame, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MachineDispatcherStatus.Completed, replay.Status);
    }

    [Fact]
    public async Task StageVelopack_failed_or_cancelled_stage_never_persists_a_handle()
    {
        foreach (var cancel in new[] { false, true })
        {
            var journal = new FakeJournal();
            var stager = new FakeSetupStager { CancelStage = cancel, FailStage = !cancel };
            using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, new FakeNetBirdPort(), stager);

            var result = await dispatcher.DispatchAsync(Request(MachineIpcOperation.StageVelopack));

            Assert.Equal(cancel ? MachineDispatcherStatus.Cancelled : MachineDispatcherStatus.Blocked, result.Status);
            Assert.Null(journal.Document!.VelopackReceipts.SetupHandleId);
            Assert.Equal(0, journal.SaveCount);
            Assert.Equal(0, stager.ReacquireCount);
        }
    }

    [Fact]
    public async Task StageVelopack_wrong_journal_sid_does_not_reacquire_or_stage()
    {
        var journal = new FakeJournal();
        var firstStager = new FakeSetupStager();
        using (var first = new MachineUpgradeDispatcher(OperationId, Sid, journal, new FakeNetBirdPort(), firstStager))
            Assert.Equal(MachineDispatcherStatus.Completed,
                (await first.DispatchAsync(Request(MachineIpcOperation.StageVelopack))).Status);
        var wrongOwnerStager = new FakeSetupStager();
        using var wrongOwner = new MachineUpgradeDispatcher(OperationId, "S-1-5-19", journal,
            new FakeNetBirdPort(), wrongOwnerStager);

        var result = await wrongOwner.DispatchAsync(Request(MachineIpcOperation.StageVelopack));

        Assert.Equal(MachineDispatcherCode.JournalOwnershipMismatch, result.Code);
        Assert.Equal(0, wrongOwnerStager.StageCount);
        Assert.Equal(0, wrongOwnerStager.ReacquireCount);
    }

    [Fact]
    public async Task StageVelopack_rejects_caller_supplied_setup_path_fields()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(
            "{\"version\":1,\"correlationId\":\"24c21d46-1f4e-4cab-a00b-e96c7954626d\",\"operation\":\"stageVelopack\",\"setupPath\":\"C:\\\\attacker\\\\Setup.exe\"}");
        var frame = new byte[MachineIpcFrameCodec.HeaderLength + payload.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(MachineIpcFrameCodec.HeaderLength));
        using var stream = new MemoryStream(frame);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await MachineIpcFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task StageRollbackPayloads_require_assessment_persist_opaque_handles_and_reverify_on_replay()
    {
        var journal = new FakeJournal();
        var stager = new FakeRollbackPayloadStager(OperationId);
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, new FakeNetBirdPort(),
            rollbackPayloadStager: stager);

        var tooEarly = await dispatcher.DispatchAsync(Request(MachineIpcOperation.StageStructuraRollback));
        Assert.Equal(MachineDispatcherCode.UserStateStageUnavailable, tooEarly.Code);
        Assert.Empty(stager.StagedKinds);

        await dispatcher.DispatchAsync(Request(MachineIpcOperation.Inspect));
        var structura = await dispatcher.DispatchAsync(Request(MachineIpcOperation.StageStructuraRollback));
        var platform = await dispatcher.DispatchAsync(Request(MachineIpcOperation.StagePlatformRollback));
        var replay = await dispatcher.DispatchAsync(Request(MachineIpcOperation.StageStructuraRollback));
        var serialized = System.Text.Encoding.UTF8.GetString(MachineDispatcherResultFrameCodec.Encode(structura));

        Assert.Equal(MachineDispatcherStatus.Completed, structura.Status);
        Assert.Equal(MachineDispatcherStatus.Completed, platform.Status);
        Assert.Equal(MachineDispatcherStatus.Completed, replay.Status);
        Assert.Equal(3, stager.StagedKinds.Count); // replay re-inspects the deterministic slot
        Assert.Equal(3, journal.SaveCount); // one inspection and two new receipts; replay does not save
        Assert.Equal($"windows-msi-op-v1:{OperationId:N}:1", journal.Document!.RollbackPayloadReceipts!.StructuraConnectorHandleId);
        Assert.Equal($"windows-msi-op-v1:{OperationId:N}:2", journal.Document.RollbackPayloadReceipts.PlatformConnectorHandleId);
        Assert.Equal(3, journal.Document.Revision);
        Assert.DoesNotContain("msi", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StageRollbackPayload_partial_slot_marks_manual_recovery_without_leaking_details()
    {
        var journal = new FakeJournal();
        var stager = new FakeRollbackPayloadStager(OperationId)
        {
            NeedsManualRecoveryKind = LegacyApplicationKind.PlatformConnector,
        };
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, new FakeNetBirdPort(),
            rollbackPayloadStager: stager);
        await dispatcher.DispatchAsync(Request(MachineIpcOperation.Inspect));

        var result = await dispatcher.DispatchAsync(Request(MachineIpcOperation.StagePlatformRollback));
        var serialized = System.Text.Json.JsonSerializer.Serialize(result);

        Assert.Equal(MachineDispatcherStatus.Blocked, result.Status);
        Assert.Equal(MachineDispatcherCode.NeedsManualRecovery, result.Code);
        Assert.Equal(MachineUpgradeState.NeedsManualRecovery, journal.Document!.State);
        Assert.Null(journal.Document.RollbackPayloadReceipts);
        Assert.DoesNotContain("StagedPath", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StageRollbackPayload_commits_receipt_even_if_request_is_cancelled_after_stage()
    {
        var journal = new FakeJournal();
        using var cancellation = new CancellationTokenSource();
        var stager = new FakeRollbackPayloadStager(OperationId) { CancelAfterStage = cancellation };
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, new FakeNetBirdPort(),
            rollbackPayloadStager: stager);
        await dispatcher.DispatchAsync(Request(MachineIpcOperation.Inspect));

        var result = await dispatcher.DispatchAsync(Request(MachineIpcOperation.StageStructuraRollback), cancellation.Token);

        Assert.Equal(MachineDispatcherStatus.Completed, result.Status);
        Assert.NotNull(journal.Document!.RollbackPayloadReceipts?.StructuraConnectorHandleId);
        Assert.Equal(2, journal.SaveCount); // inspection plus durable handle commit
    }

    [Fact]
    public async Task Remove_compensates_durable_apply_once_and_replay_returns_saved_completion()
    {
        var journal = new FakeJournal();
        var port = new FakeNetBirdPort
        {
            OnApply = (_, _) => ValueTask.FromResult<NetBirdMutationReceipt>(
                new NetBirdInstalledThisRunReceipt(OperationId.ToString("D"), OwnedState)),
            OnRemove = (_, _) =>
            {
                Assert.Equal(MachineUpgradeState.RollingBack, journal.Document!.State);
                Assert.Equal(MachineUpgradePhase.Rollback, journal.Document.Phase);
                Assert.False(journal.Document.NetBirdCompensation!.Completed);
                return ValueTask.CompletedTask;
            },
        };
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        await PrepareAsync(dispatcher);
        Assert.Equal(MachineDispatcherStatus.Completed,
            (await dispatcher.DispatchAsync(Request(MachineIpcOperation.Apply))).Status);

        var removed = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Remove));
        var replay = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Remove));

        Assert.Equal(MachineDispatcherStatus.Completed, removed.Status);
        Assert.Equal(MachineUpgradeState.RolledBack, journal.Document!.State);
        Assert.True(journal.Document.NetBirdCompensation!.Completed);
        Assert.Equal(1, port.RemoveCount);
        Assert.Equal(MachineDispatcherStatus.Completed, replay.Status);
        Assert.Equal(1, port.RemoveCount);
    }

    [Fact]
    public async Task Apply_replay_returns_durable_reboot_required_receipt()
    {
        var journal = new FakeJournal();
        var port = new FakeNetBirdPort
        {
            OnApply = (_, _) => ValueTask.FromResult<NetBirdMutationReceipt>(
                new NetBirdInstalledThisRunReceipt(OperationId.ToString("D"), OwnedState) { RebootRequired = true }),
        };
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        await PrepareAsync(dispatcher);

        var applied = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Apply));
        var replay = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Apply));

        Assert.True(applied.RebootRequired);
        Assert.True(journal.Document!.RecoveryReceipt!.RebootRequired);
        Assert.True(replay.RebootRequired);
        Assert.Equal(1, port.ApplyCount);
    }

    [Fact]
    public async Task NoChange_receipt_remains_a_compensation_noop()
    {
        var journal = new FakeJournal();
        var owned = new NetBirdOwnedState("nb-1", "0.40.1", new string('c', 64),
            "netbird-service-v1:" + new string('d', 64));
        var assessment = new NetBirdAssessment(NetBirdOwnership.OwnedByConnector, "nb-1", owned);
        var plan = FakeNetBirdPort.Plan with
        {
            Change = NetBirdChangeKind.NoChange,
            OperationId = null,
            PriorAssessment = assessment,
            PriorRestorePoint = null,
        };
        var port = new FakeNetBirdPort
        {
            AssessmentResult = assessment,
            OnPrepare = _ => ValueTask.FromResult(plan),
            OnApply = (_, _) => ValueTask.FromResult<NetBirdMutationReceipt>(new NetBirdNoChangeReceipt(owned)),
        };
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        await PrepareAsync(dispatcher);
        await dispatcher.DispatchAsync(Request(MachineIpcOperation.Apply));

        var result = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Remove));

        Assert.Equal(MachineDispatcherStatus.Completed, result.Status);
        Assert.Equal(MachineUpgradeState.InProgress, journal.Document!.State);
        Assert.Null(journal.Document.NetBirdCompensation);
        Assert.Equal(0, port.RemoveCount);
        Assert.Equal(0, port.RestoreCount);
    }

    [Fact]
    public async Task Compensation_rejects_dispatcher_with_wrong_trusted_sid()
    {
        var journal = new FakeJournal();
        var port = new FakeNetBirdPort
        {
            OnApply = (_, _) => ValueTask.FromResult<NetBirdMutationReceipt>(
                new NetBirdInstalledThisRunReceipt(OperationId.ToString("D"), OwnedState)),
        };
        using (var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port))
        {
            await PrepareAsync(dispatcher);
            await dispatcher.DispatchAsync(Request(MachineIpcOperation.Apply));
        }
        using var wrongOwner = new MachineUpgradeDispatcher(OperationId, "S-1-5-19", journal, port);

        var result = await wrongOwner.DispatchAsync(Request(MachineIpcOperation.Remove));

        Assert.Equal(MachineDispatcherCode.JournalOwnershipMismatch, result.Code);
        Assert.Equal(0, port.RemoveCount);
    }

    [Fact]
    public async Task Compensation_fails_closed_when_durable_assessment_is_foreign_or_unattributed()
    {
        foreach (var ownership in new[] { NetBirdOwnership.Foreign, NetBirdOwnership.Unattributed })
        {
            var journal = new FakeJournal();
            var port = new FakeNetBirdPort
            {
                OnApply = (_, _) => ValueTask.FromResult<NetBirdMutationReceipt>(
                    new NetBirdInstalledThisRunReceipt(OperationId.ToString("D"), OwnedState)),
            };
            using (var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port))
            {
                await PrepareAsync(dispatcher);
                await dispatcher.DispatchAsync(Request(MachineIpcOperation.Apply));
            }
            var foreign = new NetBirdAssessment(ownership, "foreign-installation");
            journal.ReplaceForTest(journal.Document! with
            {
                Revision = journal.Document.Revision + 1,
                NetBirdAssessment = foreign,
                NetBirdMutationPlan = journal.Document.NetBirdMutationPlan! with { PriorAssessment = foreign },
            });
            using var recovery = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);

            var result = await recovery.DispatchAsync(Request(MachineIpcOperation.Remove));

            Assert.Equal(MachineDispatcherCode.NeedsManualRecovery, result.Code);
            Assert.Equal(0, port.RemoveCount);
        }
    }

    [Fact]
    public async Task Restore_uses_only_the_durable_machine_receipt()
    {
        var journal = new FakeJournal();
        var assessment = new NetBirdAssessment(NetBirdOwnership.OwnedByConnector, "nb-1", OwnedState);
        var prior = new NetBirdOwnedRestorePoint("restore-handle", OwnedState,
            RollbackPayloadProtection.ProtectedMachineStaging, FakeNetBirdPort.Plan.TargetPackage.Package, new string('e', 64));
        var plan = FakeNetBirdPort.Plan with
        {
            Change = NetBirdChangeKind.UpdatedThisRun,
            PriorAssessment = assessment,
            PriorRestorePoint = prior,
        };
        var updated = OwnedState with { Version = "0.41.0" };
        var port = new FakeNetBirdPort
        {
            AssessmentResult = assessment,
            OnPrepare = _ => ValueTask.FromResult(plan),
            OnApply = (_, _) => ValueTask.FromResult<NetBirdMutationReceipt>(
                new NetBirdUpdatedThisRunReceipt(OperationId.ToString("D"), prior, updated)),
            OnRestore = (receipt, _) =>
            {
                Assert.Equal(prior, receipt.PriorRestorePoint);
                Assert.Equal(updated, receipt.UpdatedOwnedState);
                Assert.Equal(MachineUpgradeState.RollingBack, journal.Document!.State);
                return ValueTask.CompletedTask;
            },
        };
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        await PrepareAsync(dispatcher);
        await dispatcher.DispatchAsync(Request(MachineIpcOperation.Apply));

        var restored = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Restore));

        Assert.Equal(MachineDispatcherStatus.Completed, restored.Status);
        Assert.Equal(MachineNetBirdCompensationAction.RestoreUpdatedThisRun, journal.Document!.NetBirdCompensation!.Action);
        Assert.Equal(1, port.RestoreCount);
    }

    [Fact]
    public async Task Interrupted_compensation_reconciles_once_and_does_not_replay_remove()
    {
        var journal = new FakeJournal();
        var port = new FakeNetBirdPort
        {
            OnApply = (_, _) => ValueTask.FromResult<NetBirdMutationReceipt>(
                new NetBirdInstalledThisRunReceipt(OperationId.ToString("D"), OwnedState)),
            OnReconcile = (_, _) => ValueTask.FromResult(new NetBirdInterruptedRecoveryResult(
                NetBirdInterruptedRecoveryAction.NoMutationObserved, EvidenceId)),
        };
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        await PrepareAsync(dispatcher);
        await dispatcher.DispatchAsync(Request(MachineIpcOperation.Apply));
        journal.ReplaceForTest(journal.Document! with
        {
            Revision = journal.Document.Revision + 1,
            State = MachineUpgradeState.RollingBack,
            Phase = MachineUpgradePhase.Rollback,
            NetBirdCompensation = new MachineNetBirdCompensation(
                MachineNetBirdCompensationAction.RemoveInstalledThisRun, Completed: false),
        });

        var reconciled = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Reconcile));
        var repeated = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Remove));

        Assert.Equal(MachineDispatcherStatus.Completed, reconciled.Status);
        Assert.Equal(NetBirdInterruptedRecoveryAction.RemovedInstalledThisRun, reconciled.ReconciliationAction);
        Assert.Equal(EvidenceId, reconciled.ReconciliationEvidenceId);
        Assert.Equal(MachineUpgradeState.RolledBack, journal.Document!.State);
        Assert.Equal(1, port.ReconcileCount);
        Assert.Equal(MachineDispatcherStatus.Completed, repeated.Status);
        Assert.Equal(0, port.RemoveCount);
    }

    [Fact]
    public async Task Apply_saves_intent_before_port_and_persists_exact_receipt_for_duplicate()
    {
        var journal = new FakeJournal();
        var port = new FakeNetBirdPort
        {
            OnApply = (plan, _) =>
            {
                Assert.Equal(MachineUpgradePhase.MutateNetBird, journal.Document!.Phase);
                Assert.Equal(4, journal.Document.Revision);
                return ValueTask.FromResult<NetBirdMutationReceipt>(
                    new NetBirdInstalledThisRunReceipt(OperationId.ToString("D"), OwnedState));
            },
        };
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        await PrepareAsync(dispatcher);

        var first = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Apply));
        var duplicate = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Apply));
        var reconcile = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Reconcile));

        Assert.Equal(MachineDispatcherStatus.Completed, first.Status);
        Assert.Equal(MachineUpgradePhase.MutateNetBird, first.Phase);
        Assert.Equal(5, first.Revision);
        Assert.Equal(NetBirdChangeKind.InstalledThisRun, journal.Document!.RecoveryReceipt!.Change);
        Assert.Equal(1, port.ApplyCount);
        Assert.Equal(0, port.ReconcileCount);
        Assert.Equal(MachineDispatcherStatus.Completed, duplicate.Status);
        Assert.Equal(1, port.ApplyCount);
        Assert.Equal(MachineDispatcherStatus.Blocked, reconcile.Status);
        Assert.Equal(MachineDispatcherCode.MutationAlreadyApplied, reconcile.Code);
        Assert.Equal(0, port.ReconcileCount);
    }

    [Fact]
    public async Task Completed_apply_receipt_is_saved_even_when_cancellation_arrives_during_port_call()
    {
        var journal = new FakeJournal();
        using var cancellation = new CancellationTokenSource();
        var port = new FakeNetBirdPort
        {
            OnApply = (_, _) =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult<NetBirdMutationReceipt>(
                    new NetBirdInstalledThisRunReceipt(OperationId.ToString("D"), OwnedState));
            },
        };
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        await PrepareAsync(dispatcher);

        var result = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Apply), cancellation.Token);

        Assert.Equal(MachineDispatcherStatus.Completed, result.Status);
        Assert.NotNull(journal.Document!.RecoveryReceipt);
        Assert.Equal(1, port.ApplyCount);
    }

    [Fact]
    public async Task Uncertain_apply_retry_never_replays_msi_and_reconcile_receipt_is_durable()
    {
        var journal = new FakeJournal();
        using var cancellation = new CancellationTokenSource();
        var port = new FakeNetBirdPort
        {
            OnApply = (_, _) =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                return ValueTask.FromResult<NetBirdMutationReceipt>(
                    new NetBirdInstalledThisRunReceipt(OperationId.ToString("D"), OwnedState));
            },
            OnReconcile = (_, _) => ValueTask.FromResult(new NetBirdInterruptedRecoveryResult(
                NetBirdInterruptedRecoveryAction.RemovedInstalledThisRun, EvidenceId)),
        };
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        await PrepareAsync(dispatcher);

        var cancelled = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Apply), cancellation.Token);
        using var retryDispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        var retryApply = await retryDispatcher.DispatchAsync(Request(MachineIpcOperation.Apply));
        var reconciled = await retryDispatcher.DispatchAsync(Request(MachineIpcOperation.Reconcile));
        var duplicateReconcile = await retryDispatcher.DispatchAsync(Request(MachineIpcOperation.Reconcile));

        Assert.Equal(MachineDispatcherStatus.Cancelled, cancelled.Status);
        Assert.Equal(MachineDispatcherCode.ReconciliationRequired, retryApply.Code);
        Assert.Equal(1, port.ApplyCount);
        Assert.Equal(MachineDispatcherStatus.Completed, reconciled.Status);
        Assert.Equal(NetBirdInterruptedRecoveryAction.RemovedInstalledThisRun, reconciled.ReconciliationAction);
        Assert.Equal(EvidenceId, reconciled.ReconciliationEvidenceId);
        Assert.Equal(reconciled.Revision, journal.Document!.Revision);
        Assert.Equal(MachineUpgradeState.RolledBack, journal.Document.State);
        Assert.Equal(NetBirdInterruptedRecoveryAction.RemovedInstalledThisRun, journal.Document.NetBirdReconciliation!.Action);
        Assert.Equal(EvidenceId, journal.Document.NetBirdReconciliation.EvidenceId);
        Assert.Equal(reconciled with { CorrelationId = duplicateReconcile.CorrelationId }, duplicateReconcile);
        Assert.Equal(1, port.ReconcileCount);
    }

    [Fact]
    public async Task Foreign_or_unattributed_reconcile_failure_is_persisted_as_manual_and_never_retried()
    {
        var journal = new FakeJournal();
        using var cancellation = new CancellationTokenSource();
        var port = new FakeNetBirdPort
        {
            OnApply = (_, _) =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("unreachable");
            },
            OnReconcile = (_, _) => throw new WindowsHostManualRecoveryRequiredException(
                "Foreign NetBird state was preserved."),
        };
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        await PrepareAsync(dispatcher);
        _ = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Apply), cancellation.Token);
        using var retryDispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);

        var result = await retryDispatcher.DispatchAsync(Request(MachineIpcOperation.Reconcile));
        var duplicate = await retryDispatcher.DispatchAsync(Request(MachineIpcOperation.Reconcile));

        Assert.Equal(MachineDispatcherStatus.Blocked, result.Status);
        Assert.Equal(MachineDispatcherCode.NeedsManualRecovery, result.Code);
        Assert.Equal(MachineUpgradeState.NeedsManualRecovery, journal.Document!.State);
        Assert.Null(journal.Document.NetBirdReconciliation);
        Assert.Equal(1, port.ReconcileCount);
        Assert.Equal(MachineDispatcherCode.NeedsManualRecovery, duplicate.Code);
        Assert.Equal(1, port.ReconcileCount);
    }

    private static async Task PrepareAsync(MachineUpgradeDispatcher dispatcher)
    {
        await dispatcher.DispatchAsync(Request(MachineIpcOperation.Inspect));
        await dispatcher.DispatchAsync(Request(MachineIpcOperation.Prepare));
    }

    [Fact]
    public async Task Cancelled_prepare_enters_manual_recovery_and_retry_does_not_repeat_port_work()
    {
        var journal = new FakeJournal();
        using var cancellation = new CancellationTokenSource();
        var port = new FakeNetBirdPort
        {
            OnPrepare = token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(FakeNetBirdPort.Plan);
            }
        };
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        await dispatcher.DispatchAsync(Request(MachineIpcOperation.Inspect));
        var request = Request(MachineIpcOperation.Prepare);

        var cancelled = await dispatcher.DispatchAsync(request, cancellation.Token);
        using var retryDispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        var retry = await retryDispatcher.DispatchAsync(request);

        Assert.Equal(MachineDispatcherStatus.Blocked, cancelled.Status);
        Assert.Equal(MachineDispatcherCode.NeedsManualRecovery, cancelled.Code);
        Assert.Equal(MachineUpgradePhase.PrepareNetBirdMutation, journal.Document!.Phase);
        Assert.Equal(MachineUpgradeState.NeedsManualRecovery, journal.Document.State);
        Assert.Null(journal.Document.NetBirdMutationPlan);
        Assert.Equal(MachineDispatcherStatus.Blocked, retry.Status);
        Assert.Equal(MachineDispatcherCode.NeedsManualRecovery, retry.Code);
        Assert.Equal(1, port.PrepareCount);
    }

    [Fact]
    public async Task Failed_prepare_enters_manual_recovery_and_never_repeats_port_work()
    {
        var journal = new FakeJournal();
        var port = new FakeNetBirdPort
        {
            OnPrepare = _ => throw new IOException("staging failed after prepare began"),
        };
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        await dispatcher.DispatchAsync(Request(MachineIpcOperation.Inspect));

        var first = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Prepare));
        using var retryDispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        var retry = await retryDispatcher.DispatchAsync(Request(MachineIpcOperation.Prepare));

        Assert.Equal(MachineDispatcherStatus.Blocked, first.Status);
        Assert.Equal(MachineDispatcherCode.NeedsManualRecovery, first.Code);
        Assert.Equal(MachineUpgradeState.NeedsManualRecovery, journal.Document!.State);
        Assert.Null(journal.Document.NetBirdMutationPlan);
        Assert.Equal(MachineDispatcherCode.NeedsManualRecovery, retry.Code);
        Assert.Equal(1, port.PrepareCount);
        Assert.Equal(0, port.ApplyCount);
    }

    private static MachineIpcRequest Request(MachineIpcOperation operation) =>
        new(MachineIpcFrameCodec.CurrentVersion, Guid.NewGuid(), operation);

    private sealed class FakeJournal : IMachineUpgradeJournal
    {
        public MachineUpgradeJournalDocument? Document { get; private set; }
        public bool RejectOwner { get; init; }
        public int AcquireCount { get; private set; }
        public int SaveCount { get; private set; }

        public void ReplaceForTest(MachineUpgradeJournalDocument document) => Document = document;

        public ValueTask<IAsyncDisposable> AcquireLeaseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCount++;
            return ValueTask.FromResult<IAsyncDisposable>(new FakeLease());
        }

        public ValueTask<MachineUpgradeJournalDocument?> LoadAsync(Guid operationId, string initiatingSid,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RejectOwner || (Document is not null &&
                (Document.OperationId != operationId || Document.InitiatingSid != initiatingSid)))
                throw new UnauthorizedAccessException();
            return ValueTask.FromResult(Document);
        }

        public ValueTask InitializeAsync(MachineUpgradeJournalDocument document, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Document is not null) throw new InvalidOperationException();
            Document = document;
            return ValueTask.CompletedTask;
        }

        public ValueTask SaveAsync(Guid operationId, string initiatingSid, long expectedRevision,
            MachineUpgradeJournalDocument next, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Document is null || Document.OperationId != operationId || Document.InitiatingSid != initiatingSid ||
                Document.Revision != expectedRevision || next.Revision != expectedRevision + 1)
                throw new InvalidOperationException();
            Document = next;
            SaveCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<MachineUpgradeJournalDocument> RolloverAsync(Guid previousOperationId,
            MachineUpgradeJournalDocument next, string initiatingSid, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Document is null || Document.OperationId != previousOperationId ||
                Document.InitiatingSid != initiatingSid || Document.State != MachineUpgradeState.RolledBack ||
                next.InitiatingSid != initiatingSid)
                throw new UnauthorizedAccessException();
            Document = next;
            return ValueTask.FromResult(next);
        }

        public void Dispose() { }

        private sealed class FakeLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSetupStager : IWindowsVelopackSetupStager
    {
        public static readonly string Handle = "windows-velopack-setup-v1:" + new string('a', 64) + ":stage:0123456789abcdef0123456789abcdef";
        public string HandleId => Handle;
        public int StageCount { get; private set; }
        public int ReacquireCount { get; private set; }
        public bool FailStage { get; init; }
        public bool CancelStage { get; init; }

        public ValueTask<IVerifiedVelopackSetupLease> StageAndVerifyAsync(CancellationToken cancellationToken = default)
        {
            StageCount++;
            if (CancelStage) throw new OperationCanceledException();
            if (FailStage) return ValueTask.FromException<IVerifiedVelopackSetupLease>(new IOException("stage failed"));
            return ValueTask.FromResult<IVerifiedVelopackSetupLease>(new FakeSetupLease(Handle));
        }

        public ValueTask<IVerifiedVelopackSetupLease> ReacquireByHandleAsync(string handleId, CancellationToken cancellationToken = default)
        {
            ReacquireCount++;
            if (handleId != Handle) return ValueTask.FromException<IVerifiedVelopackSetupLease>(new InvalidDataException());
            return ValueTask.FromResult<IVerifiedVelopackSetupLease>(new FakeSetupLease(Handle));
        }

        public ValueTask<IVerifiedVelopackSetupLease> ReacquireAsync(ProtectedVelopackSetupReceipt receipt, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IVerifiedVelopackSetupLease>(new NotSupportedException());
    }

    private sealed class FakeSetupLease(string handleId) : IVerifiedVelopackSetupLease
    {
        public string HandleId { get; } = handleId;
        public string StagedPath => "C:\\ProgramData\\StructuraConnectorInstaller\\VelopackSetup\\Setup.exe";
        public SafeFileHandle ContentHandle => new(IntPtr.Zero, ownsHandle: false);
        public VelopackSetupLeaseProtection Protection => VelopackSetupLeaseProtection.ProtectedInstallerStaging;
        public VelopackSetupInspection Inspection => new("Structura.Connector.Desktop", "1.2.3", 3, new string('b', 64), true, "fixture");
        public void VerifyLaunchPath() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRollbackPayloadStager(Guid operationId) : IWindowsOperationRollbackPayloadStager
    {
        public List<LegacyApplicationKind> StagedKinds { get; } = [];
        public LegacyApplicationKind? NeedsManualRecoveryKind { get; init; }
        public CancellationTokenSource? CancelAfterStage { get; init; }

        public ValueTask<IWindowsVerifiedRollbackPayloadLease> StageVerifiedRollbackPayloadAsync(
            LegacyApplicationKind kind, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StagedKinds.Add(kind);
            if (NeedsManualRecoveryKind == kind)
                return ValueTask.FromException<IWindowsVerifiedRollbackPayloadLease>(
                    new WindowsRollbackPayloadNeedsManualRecoveryException(kind));
            CancelAfterStage?.Cancel();
            var handle = $"windows-msi-op-v1:{operationId:N}:{(int)kind}";
            return ValueTask.FromResult<IWindowsVerifiedRollbackPayloadLease>(new FakeRollbackPayloadLease(handle, kind));
        }
    }

    private sealed class FakeRollbackPayloadLease(string handleId, LegacyApplicationKind kind)
        : IWindowsVerifiedRollbackPayloadLease
    {
        public string HandleId { get; } = handleId;
        public RollbackPayloadProtection Protection => RollbackPayloadProtection.ProtectedMachineStaging;
        public RollbackPayloadInspection Inspection => new(
            new LegacyApplicationIdentity(kind, Guid.Parse("aabf0d7e-2f5a-44d9-b9b6-583f28e3d377"),
                Guid.Parse("f7bd31c1-3c5e-4fc8-8ac7-12e6154a1677")),
            kind == LegacyApplicationKind.StructuraConnector ? "structura" : "platform",
            kind == LegacyApplicationKind.StructuraConnector ? "Structura.Connector.Desktop.Setup.msi" : "Platform.Connector.Desktop.Setup.msi",
            "1.2.3", 123, new string('a', 64), TrustedSource: true);
        public string StagedPath => "opaque-test-path-never-serialized";
        public SafeFileHandle ContentHandle => new(IntPtr.Zero, ownsHandle: false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeNetBirdPort : IWindowsHostNetBirdPort
    {
        public static readonly NetBirdAssessment Assessment = new(NetBirdOwnership.Absent, null);
        private static readonly NetBirdMsiPackageIdentity Package = new(
            Guid.Parse("47922123-6922-4a45-a631-b567e19f9640"),
            Guid.Parse("9a1cb54a-eb3a-4b09-98a2-235d104a21d6"), "0.40.1", "NetBird", "NetBird");
        public static readonly NetBirdMutationPlan Plan = new(
            NetBirdChangeKind.InstalledThisRun,
            OperationId.ToString("D"),
            Assessment,
            new ProtectedNetBirdPackageReceipt("handle-1", RollbackPayloadProtection.ProtectedMachineStaging,
                "netbird.msi", Package, 1024, new string('a', 64), "NetBird", new string('b', 40)),
            null);

        public int InspectCount { get; private set; }
        public int PrepareCount { get; private set; }
        public int ApplyCount { get; private set; }
        public int ReconcileCount { get; private set; }
        public int RemoveCount { get; private set; }
        public int RestoreCount { get; private set; }
        public Guid? PreparedOperationId { get; private set; }
        public NetBirdAssessment AssessmentResult { get; init; } = Assessment;
        public Func<CancellationToken, ValueTask<NetBirdMutationPlan>>? OnPrepare { get; init; }
        public Func<NetBirdMutationPlan, CancellationToken, ValueTask<NetBirdMutationReceipt>>? OnApply { get; init; }
        public Func<NetBirdMutationPlan, CancellationToken, ValueTask<NetBirdInterruptedRecoveryResult>>? OnReconcile { get; init; }
        public Func<NetBirdInstalledThisRunReceipt, CancellationToken, ValueTask>? OnRemove { get; init; }
        public Func<NetBirdUpdatedThisRunReceipt, CancellationToken, ValueTask>? OnRestore { get; init; }

        public ValueTask<NetBirdAssessment> InspectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCount++;
            return ValueTask.FromResult(AssessmentResult);
        }

        public ValueTask<NetBirdMutationPlan> PrepareAsync(NetBirdAssessment assessment, Guid operationId, CancellationToken cancellationToken)
        {
            PreparedOperationId = operationId;
            PrepareCount++;
            return OnPrepare?.Invoke(cancellationToken) ?? ValueTask.FromResult(Plan with { OperationId = operationId.ToString("D") });
        }

        public ValueTask<NetBirdMutationReceipt> ApplyAsync(NetBirdMutationPlan plan, CancellationToken cancellationToken)
        {
            ApplyCount++;
            return OnApply?.Invoke(plan, cancellationToken) ?? throw new InvalidOperationException("Unexpected Apply.");
        }

        public ValueTask<NetBirdInterruptedRecoveryResult> ReconcileAsync(NetBirdMutationPlan plan, CancellationToken cancellationToken)
        {
            ReconcileCount++;
            return OnReconcile?.Invoke(plan, cancellationToken) ?? throw new InvalidOperationException("Unexpected Reconcile.");
        }

        public ValueTask RemoveInstalledThisRunAsync(NetBirdInstalledThisRunReceipt receipt, CancellationToken cancellationToken)
        {
            RemoveCount++;
            return OnRemove?.Invoke(receipt, cancellationToken) ?? ValueTask.CompletedTask;
        }

        public ValueTask RestoreUpdatedThisRunAsync(NetBirdUpdatedThisRunReceipt receipt, CancellationToken cancellationToken)
        {
            RestoreCount++;
            return OnRestore?.Invoke(receipt, cancellationToken) ?? ValueTask.CompletedTask;
        }
    }
}
