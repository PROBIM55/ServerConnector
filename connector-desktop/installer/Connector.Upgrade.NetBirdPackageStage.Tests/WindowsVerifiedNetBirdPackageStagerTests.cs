using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;
using Connector.Upgrade.NetBirdPackageStage;
using Xunit;

namespace Connector.Upgrade.NetBirdPackageStage.Tests;

public sealed class WindowsVerifiedNetBirdPackageStagerTests
{
    [Fact]
    public async Task StagesPinnedBytesAndKeepsThemLockedUntilLeaseDisposal()
    {
        using var fixture = new Fixture();
        var stager = fixture.CreateStager();
        var lease = await stager.StageAndVerifyAsync(fixture.SourcePath, fixture.Pin, default);
        try
        {
            Assert.Equal(RollbackPayloadProtection.ProtectedMachineStaging, lease.Protection);
            Assert.Equal(fixture.Pin.InstallerSha256, lease.Sha256);
            Assert.Equal(Fixture.ProductCode, lease.Package.ProductCode);
            Assert.Equal(fixture.Pin.SignerThumbprint, lease.SignerThumbprint);
            Assert.Throws<IOException>(() => File.WriteAllBytes(lease.StagedPath, [9]));
        }
        finally { await lease.DisposeAsync(); }
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(lease.StagedPath)!, fixture.Pin.InstallerName), [9]);
    }

    [Fact]
    public async Task ReacquireRejectsTamperedProtectedBytes()
    {
        using var fixture = new Fixture();
        var stager = fixture.CreateStager();
        var lease = await stager.StageAndVerifyAsync(fixture.SourcePath, fixture.Pin, default);
        var receipt = fixture.Receipt(lease);
        var path = lease.StagedPath;
        await lease.DisposeAsync();
        File.WriteAllBytes(path, [1, 2, 3]);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await stager.ReacquireAsync(receipt, fixture.Pin, default));
    }

    [Fact]
    public async Task ReacquireFromANewStagerUsesTheDurableSlotAndExactBytes()
    {
        using var fixture = new Fixture();
        var first = await fixture.CreateStager().StageAndVerifyAsync(fixture.SourcePath, fixture.Pin, default);
        var receipt = fixture.Receipt(first);
        var path = first.StagedPath;
        await first.DisposeAsync();

        var reacquired = await fixture.CreateStager().ReacquireAsync(receipt, fixture.Pin, default);
        try
        {
            Assert.Equal(path, reacquired.StagedPath);
            Assert.Equal(receipt.HandleId, reacquired.HandleId);
            Assert.Equal(File.ReadAllBytes(fixture.SourcePath), File.ReadAllBytes(reacquired.StagedPath));
        }
        finally { await reacquired.DisposeAsync(); }
    }

    [Fact]
    public async Task StagingTheSamePinAfterLeaseDisposalReverifiesAndReturnsAFreshLease()
    {
        using var fixture = new Fixture();
        var stager = fixture.CreateStager();
        var first = await stager.StageAndVerifyAsync(fixture.SourcePath, fixture.Pin, default);
        var path = first.StagedPath;
        var handle = first.HandleId;
        await first.DisposeAsync();

        var second = await stager.StageAndVerifyAsync(fixture.SourcePath, fixture.Pin, default);
        try
        {
            Assert.Equal(path, second.StagedPath);
            Assert.Equal(handle, second.HandleId);
            Assert.Equal(4, fixture.Reader.ReadCount);
            Assert.Equal(4, fixture.Signer.VerifyCount);
        }
        finally { await second.DisposeAsync(); }
    }

    [Fact]
    public async Task InterruptedTemporaryStageDoesNotStrandTheNextStageAttempt()
    {
        using var fixture = new Fixture();
        fixture.Security.FailNextFileProtection();
        var stager = fixture.CreateStager();

        await Assert.ThrowsAsync<IOException>(async () => await stager.StageAndVerifyAsync(fixture.SourcePath, fixture.Pin, default));
        var contentDirectory = Path.Combine(fixture.Root, "stage", "sha256-" + fixture.Pin.InstallerSha256.ToLowerInvariant());
        Assert.NotEmpty(Directory.GetFiles(contentDirectory, "*.partial", SearchOption.AllDirectories));

        var lease = await stager.StageAndVerifyAsync(fixture.SourcePath, fixture.Pin, default);
        try
        {
            Assert.Equal(fixture.Pin.InstallerSha256, lease.Sha256);
            Assert.Contains("stage-", lease.StagedPath, StringComparison.Ordinal);
        }
        finally { await lease.DisposeAsync(); }
    }

    [Fact]
    public async Task PartialLegacyDestinationIsLeftUntouchedAndDoesNotBlockRetry()
    {
        using var fixture = new Fixture();
        var contentDirectory = Path.Combine(fixture.Root, "stage", "sha256-" + fixture.Pin.InstallerSha256.ToLowerInvariant());
        Directory.CreateDirectory(contentDirectory);
        var stranded = Path.Combine(contentDirectory, fixture.Pin.InstallerName);
        File.WriteAllBytes(stranded, [9]);

        var lease = await fixture.CreateStager().StageAndVerifyAsync(fixture.SourcePath, fixture.Pin, default);
        try
        {
            Assert.NotEqual(stranded, lease.StagedPath);
            Assert.Equal([9], File.ReadAllBytes(stranded));
        }
        finally { await lease.DisposeAsync(); }
    }

    [Fact]
    public async Task ReacquireRejectsReceiptForForeignContentOrPath()
    {
        using var fixture = new Fixture();
        var stager = fixture.CreateStager();
        var lease = await stager.StageAndVerifyAsync(fixture.SourcePath, fixture.Pin, default);
        try
        {
            var receipt = fixture.Receipt(lease) with
            {
                HandleId = "netbird-msi-v1:sha256:" + new string('0', 64),
                InstallerName = "foreign.msi",
            };
            await Assert.ThrowsAsync<InvalidDataException>(async () => await stager.ReacquireAsync(receipt, fixture.Pin, default));
        }
        finally { await lease.DisposeAsync(); }
    }

    [Fact]
    public async Task RejectsForeignSourcePathBeforeItCanEnterMachineStaging()
    {
        using var fixture = new Fixture();
        var foreignPath = Path.Combine(fixture.Root, "foreign.msi");
        File.Copy(fixture.SourcePath, foreignPath);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.CreateStager().StageAndVerifyAsync(foreignPath, fixture.Pin, default));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "stage", "sha256-" + fixture.Pin.InstallerSha256.ToLowerInvariant())));
    }

    [Fact]
    public void ProductionAclValidatorRejectsUntrustedOwnerAndWritablePrincipal()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "unsafe");
        Directory.CreateDirectory(path);
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Missing current SID.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(user);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.WriteData | FileSystemRights.Delete, AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), acl);

        Assert.Throws<UnauthorizedAccessException>(() => WindowsNetBirdMachineStagingSecurity.ValidateEntry(path, true, true, FileSystemRights.WriteData | FileSystemRights.Delete));
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly Guid ProductCode = Guid.Parse("463D0C9D-ED41-451E-A44F-937932C8B267");
        internal static readonly Guid UpgradeCode = Guid.Parse("6456EC4E-3AD6-4B9B-A2BE-98E81CB21CCF");
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "netbird-stage-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            SourcePath = Path.Combine(Root, "netbird.msi");
            File.WriteAllBytes(SourcePath, [1, 2, 3, 4, 5, 6]);
            var bytes = File.ReadAllBytes(SourcePath);
            Pin = new OfficialNetBirdPackagePin("0.79.0", "0.76.0", new Uri("https://github.com/netbirdio/netbird/releases/tag/v0.79.0"), Path.GetFileName(SourcePath), bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "CN=NetBird GmbH", "7B41FCCAFCB794720FE07D381F9CBDF18AB5900F");
        }
        public string Root { get; }
        public string SourcePath { get; }
        public OfficialNetBirdPackagePin Pin { get; }
        public TemporarySecurity Security { get; } = new();
        public Reader Reader { get; } = new();
        public Signer Signer { get; } = new();
        public WindowsVerifiedNetBirdPackageStager CreateStager() => new(Path.Combine(Root, "stage"), Security, Reader, Signer, requireEmbeddedPin: false);
        public ProtectedNetBirdPackageReceipt Receipt(IVerifiedNetBirdPackageLease lease) => new(lease.HandleId, lease.Protection, Pin.InstallerName, lease.Package, lease.SizeBytes, lease.Sha256, lease.SignerSubject, lease.SignerThumbprint);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class TemporarySecurity : INetBirdMachineStagingSecurity
    {
        private bool _failNextFileProtection;
        public void FailNextFileProtection() => _failNextFileProtection = true;
        public string PrepareRoot(string path) { Directory.CreateDirectory(path); return Path.GetFullPath(path); }
        public void CreateProtectedDirectory(string path) => Directory.CreateDirectory(path);
        public void ProtectAndValidateFile(string path)
        {
            if (_failNextFileProtection)
            {
                _failNextFileProtection = false;
                throw new IOException("Simulated interruption while publishing the staged file.");
            }
        }
        public void ValidateProtectedDirectory(string path) { }
        public void ValidateProtectedFile(string path) { }
    }
    private sealed class Reader : INetBirdMsiPropertyReader
    {
        public int ReadCount { get; private set; }
        public NetBirdMsiProperties Read(string path)
        {
            ReadCount++;
            return new(Fixture.ProductCode, Fixture.UpgradeCode, "0.79.0", "NetBird GmbH", "NetBird");
        }
    }
    private sealed class Signer : IAuthenticodeSignerVerifier
    {
        public int VerifyCount { get; private set; }
        public AuthenticodeSigner VerifyTrusted(string path)
        {
            VerifyCount++;
            return new("CN=NetBird GmbH, O=NetBird GmbH, C=DE", "7B41FCCAFCB794720FE07D381F9CBDF18AB5900F");
        }
    }
}
