using System.Reflection;
using Connector.Upgrade.Core;
using Connector.Upgrade.OriginalUserSession;
using Connector.Upgrade.WindowsUserJournal;
using Xunit;

namespace Connector.Upgrade.OriginalUserSession.Tests;

public sealed class WindowsOriginalUserUpgradeSessionTests
{
    private const string Sid = "S-1-5-21-1000-2000-3000-4000";

    [Fact]
    public void Same_original_identity_creates_a_bound_session()
    {
        var store = new MemoryJournalStore();
        using var session = WindowsOriginalUserUpgradeSessionFactory.CreateForTesting(
            Sid, new OpaqueFakePorts(), () => new(Sid, "DOMAIN\\initiating-user"), () => store);

        Assert.NotNull(session);
        Assert.Equal(0, store.DisposeCount);
    }

    [Fact]
    public void Foreign_sid_is_rejected_before_the_journal_factory_can_probe_filesystem()
    {
        var journalFactoryCalled = false;
        Assert.Throws<UnauthorizedAccessException>(() =>
            WindowsOriginalUserUpgradeSessionFactory.CreateForTesting(
                Sid,
                new OpaqueFakePorts(),
                () => new("S-1-5-21-foreign", "DOMAIN\\other-user"),
                () =>
                {
                    journalFactoryCalled = true;
                    throw new Xunit.Sdk.XunitException("Journal factory must not be called.");
                }));

        Assert.False(journalFactoryCalled);
    }

    [Fact]
    public async Task Restarted_original_user_session_reports_interruption_without_replaying_mutations()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var tempParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "connector-original-user-session-tests"));
        var root = Path.GetFullPath(Path.Combine(tempParent, Guid.NewGuid().ToString("N")));
        Assert.True(IsStrictlyUnder(tempParent, root), "The isolated journal root must remain under its explicit temporary parent.");
        Directory.CreateDirectory(tempParent);
        var boundary = tempParent;
        var runId = Guid.NewGuid();
        using (var seedStore = CreateIsolatedRealStore(root, sid, boundary))
        await using (await seedStore.AcquireLeaseAsync(default))
            await seedStore.SaveAsync(new UpgradeJournalDocument(
                UpgradeJournalDocument.CurrentSchemaVersion,
                runId,
                0,
                UpgradeJournalState.InProgress,
                UpgradePhase.EnsureNetBird,
                []), default);

        var store = CreateIsolatedRealStore(root, sid, boundary);
        var ports = new OpaqueFakePorts();
        var session = WindowsOriginalUserUpgradeSessionFactory.CreateForTesting(
            sid, ports, () => new(sid, identity.Name!), () => store);
        try
        {
            var result = await session.ExecuteAsync(new OneTimePlatformToken("test-token"));
            Assert.Equal(UpgradeOutcome.Interrupted, result.Outcome);
            Assert.Equal(runId, result.RunId);
            Assert.Empty(ports.Calls);
            var recovered = await session.RecoverInterruptedAsync();
            Assert.Equal(UpgradeOutcome.NeedsManualRecovery, recovered.Outcome);
            Assert.Equal(result.RunId, recovered.RunId);
        }
        finally
        {
            session.Dispose();
            if (IsStrictlyUnder(tempParent, root) && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            if (Directory.Exists(tempParent) && !Directory.EnumerateFileSystemEntries(tempParent).Any())
                Directory.Delete(tempParent);
        }
    }

    private static bool IsStrictlyUnder(string parent, string target)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(target));
        return relative != "." && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static WindowsUserUpgradeJournalStore CreateIsolatedRealStore(string root, string sid, string boundary)
    {
        var constructor = typeof(WindowsUserUpgradeJournalStore).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(string), typeof(string), typeof(bool), typeof(Action<Stream, UpgradeJournalDocument>), typeof(string)],
            modifiers: null)!;
        return (WindowsUserUpgradeJournalStore)constructor.Invoke([root, sid, true, null, boundary]);
    }

    private sealed class MemoryJournalStore : IUpgradeJournalStore, IDisposable
    {
        public int DisposeCount { get; private set; }
        public ValueTask<IUpgradeJournalLease> AcquireLeaseAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<UpgradeJournalDocument?> LoadAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask SaveAsync(UpgradeJournalDocument journal, CancellationToken cancellationToken) => throw new NotImplementedException();
        public void Dispose() => DisposeCount++;
    }

    private sealed class OpaqueFakePorts : IUpgradePorts, IUpgradeSessionBoundPorts
    {
        public List<string> Calls { get; } = [];
        public void AssertSessionMatches(string operationId, string initiatingUserSid) { }
        public ValueTask<UpgradePreflight> InspectAsync(CancellationToken cancellationToken)
        {
            Calls.Add("inspect");
            return ValueTask.FromException<UpgradePreflight>(new InvalidOperationException("An interrupted run must not restart preflight."));
        }
        public ValueTask<NetBirdOperationIntent> PrepareOwnedNetBirdMutationAsync(NetBirdAssessment assessment, CancellationToken cancellationToken)
        {
            Calls.Add("prepare-netbird");
            return ValueTask.FromResult(new NetBirdOperationIntent(
                Guid.NewGuid().ToString("N"), assessment.Ownership, NetBirdChangeKind.InstalledThisRun));
        }
        public ValueTask<NetBirdOperationReceipt> ApplyOwnedNetBirdMutationAsync(NetBirdOperationIntent operation, CancellationToken cancellationToken)
        {
            Calls.Add("apply-netbird");
            return ValueTask.FromResult(new NetBirdOperationReceipt(
                operation.OperationId, operation.PriorOwnership, operation.Change,
                NetBirdOperationStatus.Applied, NetBirdOwnership.OwnedByConnector));
        }
        public ValueTask<UnifiedApplicationReceipt> InstallUnifiedApplicationAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask RemoveUnifiedApplicationAsync(UnifiedApplicationReceipt receipt, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<PlatformEnrollmentReceipt> EnrollAsync(OneTimePlatformToken token, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask RemoveNewEnrollmentAsync(PlatformEnrollmentReceipt receipt, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<NewAccessVerification> VerifyNewAccessAsync(PlatformEnrollmentReceipt receipt, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<IVerifiedRollbackPayloadLease> AcquireVerifiedRollbackPayloadAsync(LegacyRollbackPayload payload, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<LegacyRemovalReceipt> RemoveExactLegacyApplicationAsync(LegacyApplicationIdentity identity, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask RestoreExactLegacyApplicationAsync(IVerifiedRollbackPayloadLease payloadLease, LegacyRemovalReceipt removal, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask RestoreUserStateAsync(UserStateSnapshot snapshot, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<FinalUpgradeVerification> VerifyFinalStateAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<InterruptedUpgradeInspection> InspectInterruptedUpgradeAsync(UpgradeRecoveryMetadata recovery, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask<IVerifiedRollbackPayloadLease> ReacquireVerifiedRollbackPayloadAsync(ProtectedRollbackPayloadReceipt durableReceipt, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask RestoreMissingLegacyApplicationAsync(IVerifiedRollbackPayloadLease payloadLease, LegacyRecoveryObservation absentApplication, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
