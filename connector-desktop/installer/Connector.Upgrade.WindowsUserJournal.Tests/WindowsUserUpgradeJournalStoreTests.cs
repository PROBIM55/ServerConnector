using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsUserJournal;
using Xunit;

namespace Connector.Upgrade.WindowsUserJournal.Tests;

public sealed class WindowsUserUpgradeJournalStoreTests
{
    [Fact]
    public async Task CreatesUserOwnedProtectedFilesAndPersistsAcrossInstancesWithCas()
    {
        using var fixture = new UserJournalFixture();
        var runId = Guid.NewGuid();
        var initial = Journal(runId, 0);
        using (var store = fixture.CreateStore())
        await using (await store.AcquireLeaseAsync(default))
            await store.SaveAsync(initial, default);

        using var reopened = fixture.CreateStore();
        await using var lease = await reopened.AcquireLeaseAsync(default);
        Assert.Equal(0, (await reopened.LoadAsync(default))!.Revision);
        await reopened.SaveAsync(initial with { Revision = 1, Phase = UpgradePhase.EnsureNetBird }, default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await reopened.SaveAsync(initial with { Revision = 1 }, default));
        fixture.AssertProtected("journal.json");
        fixture.AssertProtected("journal.lease");
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public void RejectsWrongOriginalUserSid()
    {
        using var fixture = new UserJournalFixture();
        Assert.Throws<UnauthorizedAccessException>(() => fixture.CreateStore("S-1-5-18"));
    }

    [Fact]
    public async Task LeaseExcludesSecondStoreAndReadWriteRequireOwnedLease()
    {
        using var fixture = new UserJournalFixture();
        using var first = fixture.CreateStore();
        using var second = fixture.CreateStore();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await first.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await first.SaveAsync(Journal(Guid.NewGuid(), 0), default));
        await using var held = await first.AcquireLeaseAsync(default);
        await Assert.ThrowsAsync<UpgradeLeaseUnavailableException>(async () => await second.AcquireLeaseAsync(default));
    }

    [Fact]
    public async Task BootstrapperGuardExcludesSecondPromptButKeepsJournalLeaseIndependent()
    {
        using var fixture = new UserJournalFixture();
        using var first = fixture.CreateStore();
        using var second = fixture.CreateStore();

        using (first.AcquireBootstrapperRunGuard())
        {
            first.Dispose(); // Production releases the journal store while the UI still owns this guard.
            Assert.Throws<UpgradeLeaseUnavailableException>(() => second.AcquireBootstrapperRunGuard());
            await using var journalLease = await second.AcquireLeaseAsync(default);
        }

        using var reopened = second.AcquireBootstrapperRunGuard();
        fixture.AssertProtected("bootstrapper.guard");
    }

    [Fact]
    public async Task FailsClosedOnCorruptJournalAndAclDrift()
    {
        using var fixture = new UserJournalFixture();
        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync(default);
        await store.SaveAsync(Journal(Guid.NewGuid(), 0), default);
        File.WriteAllText(Path.Combine(fixture.Root, "journal.json"), "{ broken", Encoding.UTF8);
        await Assert.ThrowsAnyAsync<Exception>(async () => await store.LoadAsync(default));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task InterruptedLegacyJournalReturnsManualRecoveryWithoutRewritingBytes(int schemaVersion)
    {
        using var fixture = new UserJournalFixture();
        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync(default);
        await store.SaveAsync(Journal(Guid.NewGuid(), 0), default);
        var path = Path.Combine(fixture.Root, "journal.json");
        var recovery = schemaVersion == 5
            ? ",\"recovery\":{\"preflight\":null,\"protectedRollbackPayloads\":[{\"handleId\":\"legacy-stage\",\"protection\":\"ProtectedMachineStaging\",\"inspection\":{\"identity\":{\"kind\":\"StructuraConnector\"},\"packageId\":\"secret-package\",\"installerName\":\"secret.msi\",\"sha256\":\"secret-hash\"}}]}"
            : ",\"recovery\":null";
        var legacy = $"{{\"schemaVersion\":{schemaVersion},\"runId\":\"e4bc795c-d464-470e-8daa-c57328aeff3b\",\"revision\":7,\"state\":\"InProgress\",\"phase\":\"EnsureNetBird\",\"events\":[],\"failureCode\":null{recovery}}}";
        File.WriteAllText(path, legacy, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var loaded = await store.LoadAsync(default);

        Assert.NotNull(loaded);
        Assert.Equal(UpgradeJournalDocument.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(UpgradeJournalState.NeedsManualRecovery, loaded.State);
        Assert.Equal($"journal_schema_v{schemaVersion}_requires_manual_recovery", loaded.FailureCode);
        Assert.Null(loaded.Recovery);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.SaveAsync(loaded with { Revision = loaded.Revision + 1 }, default));
        Assert.Equal(legacy, File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public async Task RealUserJournalStoresOnlyOpaquePayloadReceipts()
    {
        using var fixture = new UserJournalFixture();
        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync(default);
        var pins = LegacyUpgradeLock.LoadEmbedded().Pins;
        var preflight = new UpgradePreflight(
            "S-1-5-21-journal-test",
            pins.Select(pin => new InstalledLegacyApplication(pin.Identity, pin.Version, true)).ToArray(),
            [],
            new UserStateSnapshot("S-1-5-21-journal-test", "snapshot-id", new string('C', 64), true),
            new NetBirdAssessment(NetBirdOwnership.Absent, null),
            true,
            true);
        var receipts = pins.Select(pin => new ProtectedRollbackPayloadReceipt(
            $"opaque-stage:{Guid.NewGuid():N}:{(int)pin.Kind}",
            pin.Kind,
            RollbackPayloadProtection.ProtectedMachineStaging)).ToList();
        var recovery = new UpgradeRecoveryMetadata(preflight, receipts, null, null, null, null, []);
        var journal = Journal(Guid.NewGuid(), 0) with { Recovery = recovery };

        await store.SaveAsync(journal, default);
        var text = File.ReadAllText(Path.Combine(fixture.Root, "journal.json"));
        using var document = System.Text.Json.JsonDocument.Parse(text);
        var receiptText = document.RootElement.GetProperty("recovery")
            .GetProperty("protectedRollbackPayloads").GetRawText();

        foreach (var pin in pins)
        {
            Assert.DoesNotContain(pin.PackageId, text, StringComparison.Ordinal);
            Assert.DoesNotContain(pin.InstallerName, text, StringComparison.Ordinal);
            Assert.DoesNotContain(pin.Sha256, text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("NetBirdOwnedState", text, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallationId", text, StringComparison.Ordinal);
        Assert.DoesNotContain("packageId", receiptText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("installerName", receiptText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha256", receiptText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("opaque-stage", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsJournalAclWithUnexpectedPrincipal()
    {
        using var fixture = new UserJournalFixture();
        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync(default);
        await store.SaveAsync(Journal(Guid.NewGuid(), 0), default);
        var path = Path.Combine(fixture.Root, "journal.json");
        var acl = FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new FileInfo(path), acl);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await store.LoadAsync(default));
    }

    [Fact]
    public async Task RejectsOversizedJournalBeforeDeserialization()
    {
        using var fixture = new UserJournalFixture();
        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync(default);
        await store.SaveAsync(Journal(Guid.NewGuid(), 0), default);
        using (var file = new FileStream(Path.Combine(fixture.Root, "journal.json"), FileMode.Create, FileAccess.Write, FileShare.None))
            file.SetLength(16 * 1024 * 1024 + 1);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.LoadAsync(default));
    }

    [Fact]
    public async Task OversizedStagedSaveDoesNotReplacePreviousRevision()
    {
        using var fixture = new UserJournalFixture();
        var initial = Journal(Guid.NewGuid(), 0);
        using (var store = fixture.CreateStore())
        await using (await store.AcquireLeaseAsync(default))
            await store.SaveAsync(initial, default);

        using (var oversized = fixture.CreateStore(writer: (stream, _) =>
        {
            var bytes = new byte[16 * 1024 * 1024 + 1];
            stream.Write(bytes);
        }))
        await using (var lease = await oversized.AcquireLeaseAsync(default))
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await oversized.SaveAsync(initial with { Revision = 1 }, default));

        using var reopened = fixture.CreateStore();
        await using var reopenedLease = await reopened.AcquireLeaseAsync(default);
        Assert.Equal(0, (await reopened.LoadAsync(default))!.Revision);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, ".journal.*.tmp"));
    }

    [Fact]
    public void FailsClosedWithoutRewritingAnExistingIntermediateDirectoryAcl()
    {
        using var fixture = new UserJournalFixture();
        using (var store = fixture.CreateStore()) { }
        var path = Path.Combine(fixture.Container, "Structura");
        var directory = new DirectoryInfo(path);
        var acl = FileSystemAclExtensions.GetAccessControl(directory);
        var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        acl.AddAccessRule(new FileSystemAccessRule(world, FileSystemRights.Read, AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(directory, acl);

        var before = FileSystemAclExtensions.GetAccessControl(directory).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        Assert.Throws<UnauthorizedAccessException>(() => fixture.CreateStore());
        var after = FileSystemAclExtensions.GetAccessControl(directory).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task PartialTemporaryWritePreservesPreviousRevisionAndCleansUp()
    {
        using var fixture = new UserJournalFixture();
        var initial = Journal(Guid.NewGuid(), 0);
        using (var store = fixture.CreateStore())
        await using (await store.AcquireLeaseAsync(default))
            await store.SaveAsync(initial, default);
        using var failing = fixture.CreateStore(writer: (stream, _) =>
        {
            stream.Write(Encoding.UTF8.GetBytes("partial"));
            throw new IOException("injected failure after partial temp write");
        });
        await using var secondLease = await failing.AcquireLeaseAsync(default);
        await Assert.ThrowsAsync<IOException>(async () => await failing.SaveAsync(initial with { Revision = 1 }, default));
        await secondLease.DisposeAsync();
        using var reopened = fixture.CreateStore();
        await using var checkLease = await reopened.AcquireLeaseAsync(default);
        Assert.Equal(0, (await reopened.LoadAsync(default))!.Revision);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, ".journal.*.tmp"));
    }

    [Fact]
    public async Task SameUserCanRetryOnlyAfterTerminalRollbackAndKeepsImmutableArchive()
    {
        using var fixture = new UserJournalFixture();
        var previous = Guid.NewGuid();
        var next = Guid.NewGuid();
        var oldJournal = Journal(previous, 2) with { State = UpgradeJournalState.RolledBack, Phase = UpgradePhase.Rollback, FailureCode = "operation_failed" };
        using (var seed = fixture.CreateStore())
        await using (await seed.AcquireLeaseAsync(default))
            await seed.SaveAsync(Journal(previous, 0), default);

        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync(default);
        // Move the journal to the exact terminal state using its normal CAS contract.
        var original = await store.LoadAsync(default);
        await store.SaveAsync(oldJournal with { Revision = original!.Revision + 1 }, default);
        var archivedSource = File.ReadAllBytes(Path.Combine(fixture.Root, "journal.json"));

        await store.PrepareRolloverAsync(previous, next, default);
        var prepared = await store.LoadRolloverAsync(default);
        Assert.Equal(UserJournalRolloverState.Prepared, prepared!.State);
        Assert.Equal(previous, prepared.PreviousRunId);
        Assert.Equal(archivedSource, File.ReadAllBytes(Path.Combine(fixture.Root, $"journal.archive-{previous:N}.json")));
        Assert.Equal(previous, (await store.LoadAsync(default))!.RunId);

        await store.CommitRolloverAsync(previous, next, default);
        var fresh = await store.LoadAsync(default);
        Assert.Equal(next, fresh!.RunId);
        Assert.Equal(0, fresh.Revision);
        Assert.Empty(fresh.Events);
        Assert.Equal(UpgradeJournalState.InProgress, fresh.State);
        Assert.Equal(UserJournalRolloverState.Committed, (await store.LoadRolloverAsync(default))!.State);
        fixture.AssertProtected($"journal.archive-{previous:N}.json");
        Assert.Equal(archivedSource, File.ReadAllBytes(Path.Combine(fixture.Root, $"journal.archive-{previous:N}.json")));
    }

    [Fact]
    public async Task RolloverRejectsNonterminalJournalAndCorruptJournal()
    {
        using var fixture = new UserJournalFixture();
        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync(default);
        var previous = Guid.NewGuid();
        await store.SaveAsync(Journal(previous, 0), default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.PrepareRolloverAsync(previous, Guid.NewGuid(), default));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "rollover.json")));

        var incomplete = Journal(previous, 1) with
        {
            State = UpgradeJournalState.RolledBack,
            FailureCode = "operation_failed",
            Events = [new UpgradeJournalEvent(1, UpgradeJournalEventKind.MutationIntent, UpgradePhase.EnsureNetBird,
                UpgradeMutation.EnsureNetBird, DateTimeOffset.UtcNow)],
        };
        await store.SaveAsync(incomplete, default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.PrepareRolloverAsync(previous, Guid.NewGuid(), default));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "rollover.json")));

        File.WriteAllText(Path.Combine(fixture.Root, "journal.json"), "{ broken", Encoding.UTF8);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await store.PrepareRolloverAsync(previous, Guid.NewGuid(), default));
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("archived")]
    [InlineData("next-journal")]
    public async Task RolloverResumesIdempotentlyAfterEveryDurableCrashPoint(string crashPoint)
    {
        using var fixture = new UserJournalFixture();
        var previous = Guid.NewGuid();
        var next = Guid.NewGuid();
        var terminal = Journal(previous, 0) with { State = UpgradeJournalState.RolledBack, Phase = UpgradePhase.Rollback, FailureCode = "operation_failed" };
        using (var seed = fixture.CreateStore())
        await using (await seed.AcquireLeaseAsync(default))
            await seed.SaveAsync(terminal, default);

        var crashed = false;
        using (var store = fixture.CreateStore(rolloverCheckpoint: checkpoint =>
        {
            if (!crashed && checkpoint == crashPoint) { crashed = true; throw new IOException("simulated process crash"); }
        }))
        await using (var lease = await store.AcquireLeaseAsync(default))
        {
            if (crashPoint == "next-journal")
            {
                await store.PrepareRolloverAsync(previous, next, default);
                await Assert.ThrowsAsync<IOException>(async () => await store.CommitRolloverAsync(previous, next, default));
            }
            else
                await Assert.ThrowsAsync<IOException>(async () => await store.PrepareRolloverAsync(previous, next, default));
        }
        Assert.True(crashed);

        using var resumed = fixture.CreateStore();
        await using var resumedLease = await resumed.AcquireLeaseAsync(default);
        await resumed.PrepareRolloverAsync(previous, next, default);
        await resumed.CommitRolloverAsync(previous, next, default);
        Assert.Equal(next, (await resumed.LoadAsync(default))!.RunId);
        Assert.Equal(next, (await resumed.LoadRolloverAsync(default))!.NextRunId);
        Assert.Equal(previous, (await resumed.LoadRolloverAsync(default))!.PreviousRunId);
    }

    [Fact]
    public void RejectsReparseRoot()
    {
        using var fixture = new UserJournalFixture();
        var target = Path.Combine(fixture.Container, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(fixture.Container, "linked-root");
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Symlink creation is unavailable on this Windows account.
        }
        Assert.Throws<InvalidDataException>(() => fixture.CreateStoreAtRoot(link));
    }

    private static UpgradeJournalDocument Journal(Guid runId, long revision) => new(
        UpgradeJournalDocument.CurrentSchemaVersion, runId, revision, UpgradeJournalState.InProgress,
        UpgradePhase.Preflight, []);
}

internal sealed class UserJournalFixture : IDisposable
{
    private static readonly string CurrentSid = WindowsIdentity.GetCurrent().User!.Value;
    public string Container { get; } = Path.Combine(Path.GetTempPath(), "user-upgrade-journal-" + Guid.NewGuid().ToString("N"));
    public string Root => Path.Combine(Container, "Structura", "Connector", "UpgradeJournal");

    public UserJournalFixture() => Directory.CreateDirectory(Container);

    public WindowsUserUpgradeJournalStore CreateStore(
        string? sid = null,
        Action<Stream, UpgradeJournalDocument>? writer = null,
        Action<string>? rolloverCheckpoint = null) =>
        new(Root, sid ?? CurrentSid, allowNonLocalAppDataRoot: true, writer, Container, rolloverCheckpoint);

    public WindowsUserUpgradeJournalStore CreateStoreAtRoot(string rootPath) =>
        new(rootPath, CurrentSid, allowNonLocalAppDataRoot: true, rootBoundary: Container);

    public void AssertProtectedDirectory(string path)
    {
        var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path), AccessControlSections.Owner | AccessControlSections.Access);
        Assert.Equal(CurrentSid, Assert.IsType<SecurityIdentifier>(security.GetOwner(typeof(SecurityIdentifier))).Value);
        Assert.True(security.AreAccessRulesProtected);
        var sids = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Select(rule => rule.IdentityReference.Value).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>([CurrentSid, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value], StringComparer.Ordinal), sids);
    }

    public void AssertProtected(string fileName)
    {
        var path = Path.Combine(Root, fileName);
        var security = FileSystemAclExtensions.GetAccessControl(new FileInfo(path), AccessControlSections.Owner | AccessControlSections.Access);
        Assert.Equal(CurrentSid, Assert.IsType<SecurityIdentifier>(security.GetOwner(typeof(SecurityIdentifier))).Value);
        Assert.True(security.AreAccessRulesProtected);
        var sids = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Select(rule => rule.IdentityReference.Value).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>([CurrentSid, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value], StringComparer.Ordinal), sids);
        AssertProtectedDirectory(Path.Combine(Container, "Structura"));
        AssertProtectedDirectory(Path.Combine(Container, "Structura", "Connector"));
        AssertProtectedDirectory(Root);
    }

    public void Dispose()
    {
        if (Directory.Exists(Container)) Directory.Delete(Container, recursive: true);
    }
}
