using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using Connector.Upgrade.Velopack;
using Connector.Upgrade.VelopackPayload;
using Xunit;

namespace Connector.Upgrade.VelopackPayload.Tests;

public sealed class WindowsVerifiedVelopackSetupLeaseTests
{
    [Fact]
    public async Task StagingTheSamePinAfterLeaseDisposalReusesOnlyTheVerifiedImmutableSlot()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new StagerFixture();
        var first = await fixture.CreateStager().StageAndVerifyAsync();
        var path = first.StagedPath;
        var handleId = first.HandleId;
        await first.DisposeAsync();

        var second = await fixture.CreateStager().StageAndVerifyAsync();
        try
        {
            Assert.Equal(path, second.StagedPath);
            Assert.Equal(handleId, second.HandleId);
            Assert.Equal(4, fixture.VerifyCount);
        }
        finally { await second.DisposeAsync(); }
    }

    [Fact]
    public async Task InterruptedStageLeavesItsPartialFileAndTheNextAttemptUsesANewSlot()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new StagerFixture();
        fixture.FailNextFileProtection();

        await Assert.ThrowsAsync<IOException>(async () => await fixture.CreateStager().StageAndVerifyAsync());
        var batch = fixture.BatchDirectory;
        var partial = Assert.Single(Directory.EnumerateFiles(batch, "*.partial", SearchOption.AllDirectories));

        var lease = await fixture.CreateStager().StageAndVerifyAsync();
        try
        {
            Assert.NotEqual(Path.GetDirectoryName(partial), Path.GetDirectoryName(lease.StagedPath));
            Assert.True(File.Exists(partial));
        }
        finally { await lease.DisposeAsync(); }
    }

    [Fact]
    public async Task ReacquireFromANewStagerRequiresTheExactSlotReceiptAndFileIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new StagerFixture();
        var first = await fixture.CreateStager().StageAndVerifyAsync();
        var receipt = fixture.Receipt((IWindowsVerifiedVelopackSetupLease)first);
        await first.DisposeAsync();

        var reacquired = await fixture.CreateStager().ReacquireAsync(receipt);
        try
        {
            Assert.Equal(receipt.StagedPath, reacquired.StagedPath);
            Assert.Equal(receipt.FileIdentity, ((IWindowsVerifiedVelopackSetupLease)reacquired).FileIdentity);
            Assert.Equal(receipt.HandleId, reacquired.HandleId);
        }
        finally { await reacquired.DisposeAsync(); }
    }

    [Fact]
    public async Task ReacquireByOpaqueHandleReverifiesTheCanonicalProtectedSlot()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new StagerFixture();
        var first = await fixture.CreateStager().StageAndVerifyAsync();
        var handleId = first.HandleId;
        var path = first.StagedPath;
        await first.DisposeAsync();

        var reacquired = await fixture.CreateStager().ReacquireByHandleAsync(handleId);
        try
        {
            Assert.Equal(handleId, reacquired.HandleId);
            Assert.Equal(path, reacquired.StagedPath);
            Assert.Equal(3, fixture.VerifyCount);
        }
        finally { await reacquired.DisposeAsync(); }
    }

    [Fact]
    public async Task StagingSlotsArePartitionedByInitiatingUserSid()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new StagerFixture();
        var first = await fixture.CreateStager().StageAndVerifyAsync();
        var second = await fixture.CreateStager("S-1-5-21-1-2-3-4").StageAndVerifyAsync();
        try
        {
            Assert.NotEqual(first.HandleId, second.HandleId);
            Assert.NotEqual(first.StagedPath, second.StagedPath);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReacquireRejectsAReceiptWhosePathDoesNotMatchItsSlotHandle()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new StagerFixture();
        var lease = await fixture.CreateStager().StageAndVerifyAsync();
        var receipt = fixture.Receipt((IWindowsVerifiedVelopackSetupLease)lease) with
        {
            StagedPath = Path.Combine(fixture.Root, "foreign", "Setup.exe"),
        };
        await lease.DisposeAsync();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.CreateStager().ReacquireAsync(receipt));
    }

    [Fact]
    public async Task LiveLeaseDeniesWriteDeleteAndRenameAndProvesTheLaunchPathFileId()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new LeaseFixture();
        var stream = new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var inspection = new VelopackSetupInspection("Structura.Connector.Desktop", "1.2.3", 3,
            Convert.ToHexString(SHA256.HashData([1, 2, 3])), true, "fixture");
        await using var lease = new WindowsVerifiedVelopackSetupLease("fixture", fixture.Path, inspection,
            WindowsVelopackSetupStager.GetNtfsIdentity(stream.SafeFileHandle), stream);

        Assert.Throws<IOException>(() => File.WriteAllBytes(fixture.Path, [9]));
        Assert.Throws<IOException>(() => File.Delete(fixture.Path));
        Assert.Throws<IOException>(() => File.Move(fixture.Path, fixture.Path + ".moved"));
        lease.VerifyLaunchPath();
    }

    [Fact]
    public void RejectsEmptySignerPolicyAndMalformedPinBeforeTouchingSource()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "velopack-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Throws<InvalidDataException>(() => new WindowsVelopackSetupStager(new VelopackSetupSource("missing.exe"),
                new VelopackSetupPin("pack", "1.0", 1, new string('0', 64)), new VelopackSetupSignaturePolicy(new HashSet<string>()), "S-1-5-21-1-2-3-4", root));
            Assert.Throws<InvalidDataException>(() => new WindowsVelopackSetupStager(new VelopackSetupSource("missing.exe"),
                new VelopackSetupPin("pack", "1.0", 1, "not-a-hash"), new VelopackSetupSignaturePolicy(new HashSet<string> { "AA" }), "S-1-5-21-1-2-3-4", root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void CertificateFreeSetupPinRejectsSignerEvidenceAndSignerPolicy()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "velopack-certfree-policy-" + Guid.NewGuid().ToString("N"));
        var pin = new VelopackSetupPin("pack", "1.0", 1, new string('0', 64),
            VelopackSetupTrustMode.SignedManifestHash);
        try
        {
            Assert.Throws<InvalidDataException>(() => new WindowsVelopackSetupStager(
                new VelopackSetupSource("missing.exe"), pin with { SignerThumbprint = "fixture" }, null,
                "S-1-5-21-1-2-3-4", root));
            Assert.Throws<InvalidDataException>(() => new WindowsVelopackSetupStager(
                new VelopackSetupSource("missing.exe"), pin,
                new VelopackSetupSignaturePolicy(new HashSet<string> { "fixture" }),
                "S-1-5-21-1-2-3-4", root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void RejectsAclThatGrantsWorldWriteBySid()
    {
        if (!OperatingSystem.IsWindows()) return;
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.WriteData | FileSystemRights.Delete,
            AccessControlType.Allow));

        Assert.Throws<UnauthorizedAccessException>(() => WindowsVelopackSetupStager.ValidateProtectedAcl(security, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
    }

    [Fact]
    public void AllowsOnlyReadExecuteForTheExplicitInitiatingSid()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        WindowsVelopackSetupStager.ValidateProtectedAcl(security, sid);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.WriteData, AccessControlType.Allow));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsVelopackSetupStager.ValidateProtectedAcl(security, sid));
    }

    [Fact]
    public void SharedAncestorsCanBeReadByTwoUsersWhileTheirSlotsStayIsolated()
    {
        if (!OperatingSystem.IsWindows()) return;
        var first = new SecurityIdentifier("S-1-5-21-1-2-3-4");
        var second = new SecurityIdentifier("S-1-5-21-1-2-3-5");
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        static DirectorySecurity ProtectedFor(SecurityIdentifier reader)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(reader, FileSystemRights.ReadAndExecute,
                AccessControlType.Allow));
            return security;
        }

        var sharedRoot = ProtectedFor(users);
        WindowsVelopackSetupStager.ValidateProtectedAcl(sharedRoot, first, sharedDirectory: true);
        WindowsVelopackSetupStager.ValidateProtectedAcl(sharedRoot, second, sharedDirectory: true);

        var firstSlot = ProtectedFor(first);
        WindowsVelopackSetupStager.ValidateProtectedAcl(firstSlot, first);
        Assert.Throws<UnauthorizedAccessException>(() =>
            WindowsVelopackSetupStager.ValidateProtectedAcl(firstSlot, second));
        Assert.Throws<UnauthorizedAccessException>(() =>
            WindowsVelopackSetupStager.ValidateProtectedAcl(firstSlot, second, sharedDirectory: true));
    }

    [Fact]
    public async Task OriginalUserReopensVerifiedSlotWithoutAttemptingMachineStage()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new StagerFixture();
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        await using var staged = await fixture.CreateStager(sid).StageAndVerifyAsync();

        await using var reopened = await fixture.CreateStager(sid, readOnlyExisting: true).StageAndVerifyAsync();
        Assert.Equal(staged.HandleId, reopened.HandleId);
        Assert.Equal(staged.StagedPath, reopened.StagedPath);
        Assert.Equal(staged.Inspection, reopened.Inspection);
        reopened.VerifyLaunchPath();

        Assert.Throws<UnauthorizedAccessException>(() =>
            fixture.CreateStager("S-1-5-21-1-2-3-99", readOnlyExisting: true));
    }

    [Fact]
    public async Task CertificateFreeSetupLeaseUsesSignedLengthAndHashWithoutCallingAuthenticode()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new StagerFixture();
        var pin = fixture.Pin with
        {
            TrustMode = VelopackSetupTrustMode.SignedManifestHash,
            SignerThumbprint = null,
        };
        await using var lease = await fixture.CreateStager(pin, signaturePolicy: null).StageAndVerifyAsync();

        Assert.Equal(0, fixture.VerifyCount);
        Assert.Equal(pin.SizeBytes, lease.Inspection.SizeBytes);
        Assert.Equal(pin.Sha256, lease.Inspection.Sha256);
        Assert.Equal(VelopackSetupTrustMode.SignedManifestHash, lease.Inspection.TrustMode);
        Assert.Null(lease.Inspection.SignatureEvidenceId);
        lease.VerifyLaunchPath();
    }

    [Fact]
    public async Task CertificateFreeSetupLeaseRejectsLengthMismatchBeforeAuthenticode()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new StagerFixture();
        var pin = fixture.Pin with
        {
            SizeBytes = fixture.Pin.SizeBytes + 1,
            TrustMode = VelopackSetupTrustMode.SignedManifestHash,
            SignerThumbprint = null,
        };

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.CreateStager(pin, signaturePolicy: null).StageAndVerifyAsync());
        Assert.Equal(0, fixture.VerifyCount);
    }

    [Fact]
    public void RejectsJunctionInMachineStagingPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "velopack-junction-" + Guid.NewGuid().ToString("N"));
        var target = root + "-target";
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(target);
            var link = System.IO.Path.Combine(root, "junction");
            try { Directory.CreateSymbolicLink(link, target); }
            catch (UnauthorizedAccessException) { return; }
            catch (IOException) { return; }
            catch (PlatformNotSupportedException) { return; }
            Assert.Throws<InvalidDataException>(() => WindowsVelopackSetupStager.AssertNoReparseComponents(System.IO.Path.Combine(link, "Setup.exe")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); if (Directory.Exists(target)) Directory.Delete(target, true); }
    }

    private sealed class LeaseFixture : IDisposable
    {
        public LeaseFixture() { Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "velopack-lease-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); Path = System.IO.Path.Combine(Root, "Setup.exe"); File.WriteAllBytes(Path, [1, 2, 3]); }
        public string Root { get; }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class StagerFixture : IDisposable
    {
        private bool _failNextFileProtection;
        public StagerFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "velopack-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            SourcePath = Path.Combine(Root, "source.exe");
            File.WriteAllBytes(SourcePath, [1, 2, 3, 4]);
            var bytes = File.ReadAllBytes(SourcePath);
            Pin = new VelopackSetupPin("fixture", "1.2.3", bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)), VelopackSetupTrustMode.Authenticode, "fixture");
        }

        public string Root { get; }
        public string SourcePath { get; }
        public VelopackSetupPin Pin { get; }
        public int VerifyCount { get; private set; }
        public string BatchDirectory => Path.Combine(Root, "setup-" + Pin.Sha256.ToLowerInvariant() + "-" +
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("S-1-5-32-545"))).ToLowerInvariant()[..24]);
        public void FailNextFileProtection() => _failNextFileProtection = true;
        public WindowsVelopackSetupStager CreateStager(string initiatingSid = "S-1-5-32-545",
            bool readOnlyExisting = false) => CreateStager(Pin,
                new VelopackSetupSignaturePolicy(new HashSet<string> { "fixture" }), initiatingSid, readOnlyExisting);
        public WindowsVelopackSetupStager CreateStager(VelopackSetupPin pin,
            VelopackSetupSignaturePolicy? signaturePolicy, string initiatingSid = "S-1-5-32-545",
            bool readOnlyExisting = false) => new(
            new VelopackSetupSource(SourcePath), pin,
            signaturePolicy,
            initiatingSid, Root,
            new VelopackSetupStagerHooks(
                path => { if (readOnlyExisting) throw new InvalidOperationException("Read-only lease attempted machine staging."); Directory.CreateDirectory(path); },
                _ => { },
                path => { if (readOnlyExisting) throw new InvalidOperationException("Read-only lease attempted file protection."); ProtectFile(path); },
                _ => { },
                VerifySigner), readOnlyExisting);
        public ProtectedVelopackSetupReceipt Receipt(IWindowsVerifiedVelopackSetupLease lease) => new(
            lease.HandleId, lease.StagedPath, lease.Inspection, lease.FileIdentity);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }

        private void ProtectFile(string path)
        {
            if (_failNextFileProtection)
            {
                _failNextFileProtection = false;
                throw new IOException("Simulated interruption while protecting the staged Setup.exe.");
            }
        }

        private string VerifySigner(string path)
        {
            VerifyCount++;
            return "fixture";
        }
    }
}
