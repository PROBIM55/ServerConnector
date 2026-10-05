using System.Security.AccessControl;
using System.Security.Principal;
using Connector.Upgrade.Core;
using Xunit;

namespace Connector.Upgrade.WindowsJournal.Tests;

public sealed class WindowsProtectedUpgradeJournalStoreTests
{
    [Fact]
    public async Task PersistsAtomicJournalAcrossStoreRestartAndEnforcesCas()
    {
        using var fixture = new ProtectedJournalFixture();
        var runId = Guid.NewGuid();
        var initial = CreateJournal(runId, revision: 0);

        using (var first = fixture.CreateStore())
        await using (await first.AcquireLeaseAsync(CancellationToken.None))
            await first.SaveAsync(initial, CancellationToken.None);

        using var restarted = fixture.CreateStore();
        await using var restartedLease = await restarted.AcquireLeaseAsync(CancellationToken.None);
        var loaded = await restarted.LoadAsync(CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(initial.RunId, loaded.RunId);
        Assert.Equal(initial.Revision, loaded.Revision);
        Assert.Equal(initial.Phase, loaded.Phase);
        await restarted.SaveAsync(initial with { Revision = 1, Phase = UpgradePhase.EnsureNetBird }, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await restarted.SaveAsync(initial with { Revision = 1 }, CancellationToken.None));
        Assert.Equal(UpgradePhase.EnsureNetBird, (await restarted.LoadAsync(CancellationToken.None))!.Phase);
        fixture.AssertProtectedFile("journal.json");
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public async Task LeaseExcludesAnotherStoreUntilReleased()
    {
        using var fixture = new ProtectedJournalFixture();
        using var first = fixture.CreateStore();
        using var second = fixture.CreateStore();

        await using (await first.AcquireLeaseAsync(CancellationToken.None))
            await Assert.ThrowsAsync<UpgradeLeaseUnavailableException>(async () =>
                await second.AcquireLeaseAsync(CancellationToken.None));

        await using var reacquired = await second.AcquireLeaseAsync(CancellationToken.None);
        fixture.AssertProtectedFile("journal.lease");
    }

    [Fact]
    public async Task RejectsForgedJournalWithUserWritableAcl()
    {
        using var fixture = new ProtectedJournalFixture();
        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync(CancellationToken.None);
        await store.SaveAsync(CreateJournal(Guid.NewGuid(), 0), CancellationToken.None);
        fixture.GrantWorldReadWrite("journal.json");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadAndWriteRequireLeaseOwnedByTheCallingStore()
    {
        using var fixture = new ProtectedJournalFixture();
        using var store = fixture.CreateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.SaveAsync(CreateJournal(Guid.NewGuid(), 0), CancellationToken.None));
    }

    [Fact]
    public async Task SecondInstanceCannotRaceTheSameNextRevisionWhileFirstOwnsLease()
    {
        using var fixture = new ProtectedJournalFixture();
        using var first = fixture.CreateStore();
        using var second = fixture.CreateStore();
        var initial = CreateJournal(Guid.NewGuid(), 0);
        await using var firstLease = await first.AcquireLeaseAsync(CancellationToken.None);
        await first.SaveAsync(initial, CancellationToken.None);

        var competingSave = Task.Run(async () =>
            await second.SaveAsync(initial with { Revision = 1 }, CancellationToken.None));
        await first.SaveAsync(
            initial with { Revision = 1, Phase = UpgradePhase.EnsureNetBird },
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => competingSave);
        Assert.Equal(UpgradePhase.EnsureNetBird, (await first.LoadAsync(CancellationToken.None))!.Phase);
    }

    [Fact]
    public async Task LeaseCanReleaseAfterStoreDisposeAndAnotherStoreCanAcquire()
    {
        using var fixture = new ProtectedJournalFixture();
        var disposedStore = fixture.CreateStore();
        var lease = await disposedStore.AcquireLeaseAsync(CancellationToken.None);

        disposedStore.Dispose();
        await lease.DisposeAsync();

        using var replacement = fixture.CreateStore();
        await using var replacementLease = await replacement.AcquireLeaseAsync(CancellationToken.None);
        fixture.AssertProtectedFile("journal.lease");
    }

    [Fact]
    public async Task RejectsAclDriftOnLeaseBeforeOpeningIt()
    {
        using var fixture = new ProtectedJournalFixture();
        using var store = fixture.CreateStore();
        await using (await store.AcquireLeaseAsync(CancellationToken.None)) { }
        fixture.GrantWorldReadWrite("journal.lease");

        await Assert.ThrowsAsync<UpgradeLeaseUnavailableException>(async () =>
            await store.AcquireLeaseAsync(CancellationToken.None));
    }

    [Fact]
    public void RejectsProtectedDirectoryAclDriftAtConstruction()
    {
        using var fixture = new ProtectedJournalFixture();
        fixture.GrantWorldDirectoryMutation();

        Assert.Throws<UnauthorizedAccessException>(() =>
            fixture.CreateStore());
    }

    [Fact]
    public void ProductionAclUsesAdministratorsOwnerAndSystemAdministratorsOnly()
    {
        var acl = WindowsJournalSecurity.CreateProtectedFileAcl();
        var owner = Assert.IsType<SecurityIdentifier>(acl.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        Assert.Equal(2, rules.Length);
        Assert.Contains(rules, rule =>
            ((SecurityIdentifier)rule.IdentityReference).IsWellKnown(WellKnownSidType.LocalSystemSid));
        Assert.Contains(rules, rule =>
            ((SecurityIdentifier)rule.IdentityReference).IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
    }

    private static UpgradeJournalDocument CreateJournal(Guid runId, long revision) => new(
        UpgradeJournalDocument.CurrentSchemaVersion,
        runId,
        revision,
        UpgradeJournalState.InProgress,
        UpgradePhase.Preflight,
        []);
}

internal sealed class ProtectedJournalFixture : IDisposable
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier CurrentSid = WindowsIdentity.GetCurrent().User!;
    private readonly string _container = Path.Combine(
        Path.GetTempPath(),
        "protected-journal-tests-" + Guid.NewGuid().ToString("N"));

    public ProtectedJournalFixture()
    {
        SecurityProfile = new WindowsJournalSecurityProfile(
            CurrentSid,
            [CurrentSid.Value],
            [CurrentSid.Value, SystemSid.Value],
            [CurrentSid.Value, SystemSid.Value],
            RequireElevatedCaller: false);
        Directory.CreateDirectory(_container);
        Root = Path.Combine(_container, "journal");
        FileSystemAclExtensions.Create(
            new DirectoryInfo(Root),
            WindowsJournalSecurity.CreateProtectedDirectoryAcl(SecurityProfile));
        WindowsJournalSecurity.ValidateProtectedDirectory(Root, SecurityProfile);
    }

    public string Root { get; }
    private WindowsJournalSecurityProfile SecurityProfile { get; }

    public WindowsProtectedUpgradeJournalStore CreateStore() => new(Root, SecurityProfile);

    public void AssertProtectedFile(string name) =>
        WindowsJournalSecurity.ValidateProtectedFile(Path.Combine(Root, name), SecurityProfile);

    public void GrantWorldReadWrite(string name)
    {
        var path = Path.Combine(Root, name);
        var security = FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData | FileSystemRights.WriteData,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
    }

    public void GrantWorldDirectoryMutation()
    {
        var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(Root));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.DeleteSubdirectoriesAndFiles,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(Root), security);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_container))
            return;
        try
        {
            var reset = new DirectorySecurity();
            reset.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            reset.SetOwner(WindowsIdentity.GetCurrent().User!);
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(Root), reset);
        }
        finally
        {
            Directory.Delete(_container, recursive: true);
        }
    }
}
