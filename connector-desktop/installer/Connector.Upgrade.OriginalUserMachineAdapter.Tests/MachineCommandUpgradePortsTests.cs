using Connector.Upgrade.Core;
using Connector.Upgrade.MachineDispatcher;
using Connector.Upgrade.MachineIpc;
using Connector.Upgrade.MachineJournal;
using Connector.Upgrade.OriginalUserMachineAdapter;
using Xunit;

namespace Connector.Upgrade.OriginalUserMachineAdapter.Tests;

public sealed class MachineCommandUpgradePortsTests
{
    private static readonly Guid OperationId = Guid.Parse("46dd5b2b-c854-4748-9f5e-78138ee24b96");
    private const string Sid = "S-1-5-21-1-2-3-4";

    [Fact]
    public async Task Inspect_prepares_machine_user_stage_before_original_user_inventory()
    {
        var commands = new List<MachineIpcOperation>();
        var user = new StubOriginalUserPorts(OperationId, Sid)
        {
            Preflight = new UpgradePreflight(Sid, [], [],
                new UserStateSnapshot(Sid, "snapshot", new string('A', 64), true),
                new NetBirdAssessment(NetBirdOwnership.Absent, null), true, true),
        };
        var adapter = New(user, (request, _, _) =>
        {
            commands.Add(request.Operation);
            return ValueTask.FromResult(Result(request, MachineDispatcherStatus.Completed,
                MachineDispatcherCode.None, MachineUpgradeState.InProgress, NetBirdOwnership.Absent));
        });

        var result = await adapter.InspectAsync(default);

        Assert.Equal(NetBirdOwnership.Absent, result.NetBird.Ownership);
        Assert.Equal([MachineIpcOperation.Inspect, MachineIpcOperation.PrepareUserStateStage], commands);
        Assert.Equal(1, user.InspectCalls);
    }

    [Fact]
    public async Task Blocked_or_wrong_phase_user_stage_stops_before_original_user_inventory()
    {
        foreach (var wrongPhase in new[] { false, true })
        {
            var user = new StubOriginalUserPorts(OperationId, Sid);
            var adapter = New(user, (request, _, _) => ValueTask.FromResult(
                request.Operation == MachineIpcOperation.PrepareUserStateStage && !wrongPhase
                    ? Result(request, MachineDispatcherStatus.Blocked, MachineDispatcherCode.UserStateStageUnavailable,
                        MachineUpgradeState.InProgress)
                    : Result(request, MachineDispatcherStatus.Completed, MachineDispatcherCode.None,
                        MachineUpgradeState.InProgress, NetBirdOwnership.Absent,
                        phase: request.Operation == MachineIpcOperation.PrepareUserStateStage
                            ? MachineUpgradePhase.PrepareNetBirdMutation : MachineUpgradePhase.AssessNetBird)));

            await Assert.ThrowsAsync<MachineUpgradeCommandRejectedException>(async () => await adapter.InspectAsync(default));
            Assert.Equal(0, user.InspectCalls);
        }
    }

    [Fact]
    public async Task InstallStagesBeforeCallingOriginalUserAndNeverForwardsBlockedStage()
    {
        var order = new List<string>();
        var user = new StubOriginalUserPorts(OperationId, Sid) { OnInstall = () => order.Add("user-install") };
        var adapter = New(user, (request, _, _) =>
        {
            order.Add("machine-" + request.Operation);
            return ValueTask.FromResult(Result(request, MachineDispatcherStatus.Completed,
                MachineDispatcherCode.None, MachineUpgradeState.InProgress));
        });

        var receipt = await adapter.InstallUnifiedApplicationAsync(default);
        Assert.Equal("user-install", receipt.OperationId);
        Assert.Equal(["machine-StageVelopack", "user-install"], order);

        order.Clear();
        adapter = New(user, (request, _, _) => ValueTask.FromResult(Result(request,
            MachineDispatcherStatus.Blocked, MachineDispatcherCode.VelopackStageUnavailable,
            MachineUpgradeState.NeedsManualRecovery)));
        await Assert.ThrowsAsync<MachineUpgradeCommandRejectedException>(async () =>
            await adapter.InstallUnifiedApplicationAsync(default));
        Assert.Empty(order);
    }

    [Fact]
    public async Task MachineAndUserNetBirdInventoryMismatchFailsClosed()
    {
        var user = new StubOriginalUserPorts(OperationId, Sid)
        {
            Preflight = new UpgradePreflight(Sid, [], [],
                new UserStateSnapshot(Sid, "snapshot", new string('A', 64), true),
                new NetBirdAssessment(NetBirdOwnership.Foreign, null), true, true),
        };
        var adapter = New(user, (request, _, _) => ValueTask.FromResult(Result(request,
            MachineDispatcherStatus.Completed, MachineDispatcherCode.None,
            MachineUpgradeState.InProgress, NetBirdOwnership.Absent)));

        await Assert.ThrowsAsync<MachineUpgradeCommandRejectedException>(async () =>
            await adapter.InspectAsync(default));
    }

    [Fact]
    public async Task OwnedMachineInventoryStaysOpaqueInOriginalUserPreflight()
    {
        var statusOnly = new StubOriginalUserPorts(OperationId, Sid)
        {
            Preflight = new UpgradePreflight(Sid, [], [],
                new UserStateSnapshot(Sid, "snapshot", new string('A', 64), true),
                new NetBirdAssessment(NetBirdOwnership.OwnedByConnector, null), true, true),
        };
        var adapter = New(statusOnly, (request, _, _) => ValueTask.FromResult(Result(request,
            MachineDispatcherStatus.Completed, MachineDispatcherCode.None,
            MachineUpgradeState.InProgress, NetBirdOwnership.OwnedByConnector)));

        var accepted = await adapter.InspectAsync(default);
        Assert.Null(accepted.NetBird.InstallationId);
        Assert.Null(accepted.NetBird.OwnedState);

        var rich = new StubOriginalUserPorts(OperationId, Sid)
        {
            Preflight = statusOnly.Preflight! with
            {
                NetBird = new NetBirdAssessment(NetBirdOwnership.OwnedByConnector, "machine-install-id"),
            },
        };
        adapter = New(rich, (request, _, _) => ValueTask.FromResult(Result(request,
            MachineDispatcherStatus.Completed, MachineDispatcherCode.None,
            MachineUpgradeState.InProgress, NetBirdOwnership.OwnedByConnector)));
        await Assert.ThrowsAsync<MachineUpgradeCommandRejectedException>(async () =>
            await adapter.InspectAsync(default));
    }

    [Fact]
    public async Task ReconcileDoesNotPretendAppliedMachineMutationWasRolledBack()
    {
        var adapter = New(new StubOriginalUserPorts(OperationId, Sid), (request, _, _) =>
            ValueTask.FromResult(Result(request, MachineDispatcherStatus.Blocked,
                MachineDispatcherCode.MutationAlreadyApplied, MachineUpgradeState.InProgress,
                NetBirdOwnership.Absent, NetBirdChangeKind.InstalledThisRun)));
        var intent = new NetBirdOperationIntent(OperationId.ToString("D"), NetBirdOwnership.Absent,
            NetBirdChangeKind.InstalledThisRun);

        await Assert.ThrowsAsync<MachineUpgradeCommandRejectedException>(async () =>
            await adapter.ReconcileInterruptedNetBirdAsync(intent, default));
    }

    [Fact]
    public async Task NetBirdForwardAndCompensationUseOnlyFixedMachineCommands()
    {
        var commands = new List<MachineIpcOperation>();
        var adapter = New(new StubOriginalUserPorts(OperationId, Sid), (request, _, _) =>
        {
            commands.Add(request.Operation);
            var state = request.Operation == MachineIpcOperation.Remove
                ? MachineUpgradeState.RolledBack : MachineUpgradeState.InProgress;
            return ValueTask.FromResult(Result(request, MachineDispatcherStatus.Completed,
                MachineDispatcherCode.None, state, NetBirdOwnership.Absent,
                NetBirdChangeKind.InstalledThisRun) with { RebootRequired = request.Operation == MachineIpcOperation.Apply });
        });
        var assessment = new NetBirdAssessment(NetBirdOwnership.Absent, null);

        var intent = await adapter.PrepareOwnedNetBirdMutationAsync(assessment, default);
        var receipt = await adapter.ApplyOwnedNetBirdMutationAsync(intent, default);
        await adapter.CompensateNetBirdMutationAsync(intent, receipt, default);

        Assert.Equal([MachineIpcOperation.Prepare, MachineIpcOperation.Apply,
            MachineIpcOperation.Remove], commands);
        Assert.Equal(NetBirdOwnership.OwnedByConnector, receipt.ResultingOwnership);
        Assert.Equal(OperationId.ToString("D"), receipt.OperationId);
        Assert.True(receipt.RebootRequired);
    }

    [Theory]
    [InlineData(LegacyApplicationKind.StructuraConnector, MachineIpcOperation.StageStructuraRollback)]
    [InlineData(LegacyApplicationKind.PlatformConnector, MachineIpcOperation.StagePlatformRollback)]
    public async Task AcquireStagesFixedKindThenReopensOnlyExactPinnedUserLease(
        LegacyApplicationKind kind, MachineIpcOperation expectedOperation)
    {
        var pin = LegacyUpgradeLock.LoadEmbedded().Get(kind);
        var opened = new List<ProtectedRollbackPayloadReceipt>();
        var lease = Lease(pin, $"windows-msi-op-v1:{OperationId:N}:{(int)kind}");
        var user = new StubOriginalUserPorts(OperationId, Sid)
        {
            OnOpenRollback = (receipt, _) =>
            {
                opened.Add(receipt);
                return ValueTask.FromResult<IVerifiedRollbackPayloadLease>(lease);
            },
        };
        var commands = new List<MachineIpcOperation>();
        var adapter = New(user, (request, _, _) =>
        {
            commands.Add(request.Operation);
            return ValueTask.FromResult(Result(request, MachineDispatcherStatus.Completed,
                MachineDispatcherCode.None, MachineUpgradeState.InProgress,
                phase: MachineUpgradePhase.AssessNetBird));
        });

        var result = await adapter.AcquireVerifiedRollbackPayloadAsync(Payload(pin), default);

        Assert.Same(lease, result);
        Assert.Equal([expectedOperation], commands);
        var openedReceipt = Assert.Single(opened);
        Assert.Equal(lease.HandleId, openedReceipt.HandleId);
        Assert.Equal(kind, openedReceipt.Kind);
        Assert.Equal(RollbackPayloadProtection.ProtectedMachineStaging, openedReceipt.Protection);
        Assert.Equal(0, user.AcquireCalls);
    }

    [Fact]
    public async Task AcquireRejectsWrongStageStateOrPhaseBeforeOpeningUserLease()
    {
        foreach (var wrongState in new[] { false, true })
        {
            var pin = LegacyUpgradeLock.LoadEmbedded().Get(LegacyApplicationKind.StructuraConnector);
            var user = new StubOriginalUserPorts(OperationId, Sid);
            var adapter = New(user, (request, _, _) => ValueTask.FromResult(Result(request,
                MachineDispatcherStatus.Completed, MachineDispatcherCode.None,
                wrongState ? MachineUpgradeState.RolledBack : MachineUpgradeState.InProgress,
                phase: wrongState ? MachineUpgradePhase.AssessNetBird : MachineUpgradePhase.PrepareNetBirdMutation)));

            await Assert.ThrowsAsync<MachineUpgradeCommandRejectedException>(async () =>
                await adapter.AcquireVerifiedRollbackPayloadAsync(Payload(pin), default));
            Assert.Empty(user.OpenedRollbackReceipts);
        }
    }

    [Fact]
    public async Task ReacquireOpensExistingDurableHandleWithoutStagingAndValidatesBinding()
    {
        var kind = LegacyApplicationKind.PlatformConnector;
        var pin = LegacyUpgradeLock.LoadEmbedded().Get(kind);
        var handle = $"windows-msi-op-v1:{OperationId:N}:{(int)kind}";
        var lease = Lease(pin, handle);
        var user = new StubOriginalUserPorts(OperationId, Sid)
        {
            OnOpenRollback = (receipt, _) => ValueTask.FromResult<IVerifiedRollbackPayloadLease>(lease),
        };
        var commands = new List<MachineIpcOperation>();
        var adapter = New(user, (request, _, _) =>
        {
            commands.Add(request.Operation);
            throw new InvalidOperationException("Restart recovery must not create a new machine stage.");
        });
        var receipt = new ProtectedRollbackPayloadReceipt(handle, kind, RollbackPayloadProtection.ProtectedMachineStaging);

        var reopened = await adapter.ReacquireVerifiedRollbackPayloadAsync(receipt, default);

        Assert.Same(lease, reopened);
        Assert.Empty(commands);
        Assert.Equal(receipt, Assert.Single(user.OpenedRollbackReceipts));
        await Assert.ThrowsAsync<MachineUpgradeCommandRejectedException>(async () =>
            await adapter.ReacquireVerifiedRollbackPayloadAsync(
                receipt with { HandleId = $"windows-msi-op-v1:{Guid.NewGuid():N}:{(int)kind}" }, default));
        Assert.Single(user.OpenedRollbackReceipts);
    }

    [Fact]
    public async Task AcquireDisposesAndRejectsLeaseWithUntrustedOrMismatchedInspection()
    {
        var pin = LegacyUpgradeLock.LoadEmbedded().Get(LegacyApplicationKind.StructuraConnector);
        var handle = $"windows-msi-op-v1:{OperationId:N}:{(int)pin.Kind}";
        var invalidLease = Lease(pin, handle, sha256: new string('0', 64));
        var user = new StubOriginalUserPorts(OperationId, Sid)
        {
            OnOpenRollback = (_, _) => ValueTask.FromResult<IVerifiedRollbackPayloadLease>(invalidLease),
        };
        var adapter = New(user, (request, _, _) => ValueTask.FromResult(Result(request,
            MachineDispatcherStatus.Completed, MachineDispatcherCode.None,
            MachineUpgradeState.InProgress, phase: MachineUpgradePhase.AssessNetBird)));

        await Assert.ThrowsAsync<MachineUpgradeCommandRejectedException>(async () =>
            await adapter.AcquireVerifiedRollbackPayloadAsync(Payload(pin), default));
        Assert.True(invalidLease.Disposed);
    }

    [Fact]
    public void WrongOriginalUserBindingIsRejectedBeforeAnyMachineCommand()
    {
        var user = new StubOriginalUserPorts(OperationId, Sid);
        Assert.Throws<UnauthorizedAccessException>(() => New(user, (_, _, _) =>
            throw new Exception("Machine transport must not be opened."), Guid.NewGuid()));
    }

    private static MachineCommandUpgradePorts New(StubOriginalUserPorts user,
        Func<MachineIpcRequest, TimeSpan, CancellationToken, ValueTask<MachineDispatcherResult>> send,
        Guid? operationId = null) =>
        new(operationId ?? OperationId, Sid, user, send, TimeSpan.FromSeconds(5));

    private static MachineDispatcherResult Result(MachineIpcRequest request,
        MachineDispatcherStatus status, MachineDispatcherCode code, MachineUpgradeState state,
        NetBirdOwnership? ownership = null, NetBirdChangeKind? change = null,
        MachineUpgradePhase phase = MachineUpgradePhase.AssessNetBird) =>
        new(request.Version, request.CorrelationId, request.Operation, status, code, state, phase, 1, ownership, change);

    private static LegacyRollbackPayload Payload(LegacyUpgradePin pin) => new(
        pin.Identity, pin.PackageId, pin.InstallerName, pin.Version, pin.SizeBytes, pin.Sha256, Verified: true);

    private static TestRollbackLease Lease(LegacyUpgradePin pin, string handle, string? sha256 = null) => new(
        handle, RollbackPayloadProtection.ProtectedMachineStaging,
        new RollbackPayloadInspection(pin.Identity, pin.PackageId, pin.InstallerName,
            pin.Version, pin.SizeBytes, sha256 ?? pin.Sha256, TrustedSource: true));

    private sealed class StubOriginalUserPorts(Guid operationId, string sid) : IUpgradePorts,
        IOriginalUserOperationRollbackPayloadOpener
    {
        public UpgradePreflight? Preflight { get; init; }
        public int InspectCalls { get; private set; }
        public int AcquireCalls { get; private set; }
        public List<ProtectedRollbackPayloadReceipt> OpenedRollbackReceipts { get; } = [];
        public Func<ProtectedRollbackPayloadReceipt, CancellationToken, ValueTask<IVerifiedRollbackPayloadLease>>? OnOpenRollback { get; init; }
        public Action? OnInstall { get; init; }
        public void AssertSessionMatches(string candidateOperationId, string candidateSid)
        {
            if (candidateOperationId != operationId.ToString("N") || candidateSid != sid)
                throw new UnauthorizedAccessException("Wrong original-user operation or SID.");
        }

        public ValueTask<UpgradePreflight> InspectAsync(CancellationToken token)
        {
            InspectCalls++;
            return ValueTask.FromResult(Preflight ?? throw new NotSupportedException());
        }
        public ValueTask<UnifiedApplicationReceipt> InstallUnifiedApplicationAsync(CancellationToken token)
        {
            OnInstall?.Invoke();
            return ValueTask.FromResult(new UnifiedApplicationReceipt("user-install"));
        }
        public ValueTask RemoveUnifiedApplicationAsync(UnifiedApplicationReceipt receipt, CancellationToken token) => Throw();
        public ValueTask<PlatformEnrollmentReceipt> EnrollAsync(OneTimePlatformToken token, CancellationToken cancellationToken) => Throw<PlatformEnrollmentReceipt>();
        public ValueTask RemoveNewEnrollmentAsync(PlatformEnrollmentReceipt receipt, CancellationToken token) => Throw();
        public ValueTask<NewAccessVerification> VerifyNewAccessAsync(PlatformEnrollmentReceipt receipt, CancellationToken token) => Throw<NewAccessVerification>();
        public ValueTask<IVerifiedRollbackPayloadLease> AcquireVerifiedRollbackPayloadAsync(LegacyRollbackPayload payload, CancellationToken token)
        {
            AcquireCalls++;
            return Throw<IVerifiedRollbackPayloadLease>();
        }
        public ValueTask<LegacyRemovalReceipt> RemoveExactLegacyApplicationAsync(LegacyApplicationIdentity identity, CancellationToken token) => Throw<LegacyRemovalReceipt>();
        public ValueTask RestoreExactLegacyApplicationAsync(IVerifiedRollbackPayloadLease lease, LegacyRemovalReceipt removal, CancellationToken token) => Throw();
        public ValueTask RestoreUserStateAsync(UserStateSnapshot snapshot, CancellationToken token) => Throw();
        public ValueTask<FinalUpgradeVerification> VerifyFinalStateAsync(CancellationToken token) => Throw<FinalUpgradeVerification>();
        public ValueTask<InterruptedUpgradeInspection> InspectInterruptedUpgradeAsync(UpgradeRecoveryMetadata recovery, CancellationToken token) => Throw<InterruptedUpgradeInspection>();
        public ValueTask<IVerifiedRollbackPayloadLease> ReacquireVerifiedRollbackPayloadAsync(ProtectedRollbackPayloadReceipt receipt, CancellationToken token) => Throw<IVerifiedRollbackPayloadLease>();
        public ValueTask<IVerifiedRollbackPayloadLease> OpenExistingRollbackPayloadAsync(ProtectedRollbackPayloadReceipt receipt, CancellationToken token)
        {
            OpenedRollbackReceipts.Add(receipt);
            return OnOpenRollback?.Invoke(receipt, token) ?? Throw<IVerifiedRollbackPayloadLease>();
        }
        public ValueTask RestoreMissingLegacyApplicationAsync(IVerifiedRollbackPayloadLease lease, LegacyRecoveryObservation observation, CancellationToken token) => Throw();
        private static ValueTask Throw() => ValueTask.FromException(new NotSupportedException());
        private static ValueTask<T> Throw<T>() => ValueTask.FromException<T>(new NotSupportedException());
    }

    private sealed class TestRollbackLease(string handleId, RollbackPayloadProtection protection,
        RollbackPayloadInspection inspection) : IVerifiedRollbackPayloadLease
    {
        public string HandleId { get; } = handleId;
        public RollbackPayloadProtection Protection { get; } = protection;
        public RollbackPayloadInspection Inspection { get; } = inspection;
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
