using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsPayload;
using Xunit;

namespace Connector.Upgrade.WindowsPayload.Tests;

public sealed class WindowsRollbackPayloadStoreTests
{
    [Fact]
    public async Task StagesExactlyTwoLockedPayloadsAndPinsTheirHandles()
    {
        using var fixture = new PayloadFixture();
        var store = fixture.CreateStore();

        var structura = Assert.IsAssignableFrom<IWindowsVerifiedRollbackPayloadLease>(
            await store.AcquireVerifiedRollbackPayloadAsync(fixture.ToPayload(fixture.StructuraPin)));
        var platform = Assert.IsAssignableFrom<IWindowsVerifiedRollbackPayloadLease>(
            await store.AcquireVerifiedRollbackPayloadAsync(fixture.ToPayload(fixture.PlatformPin)));
        try
        {
            Assert.Equal(RollbackPayloadProtection.ProtectedMachineStaging, structura.Protection);
            Assert.True(structura.Inspection.TrustedSource);
            Assert.False(structura.ContentHandle.IsClosed);
            Assert.False(platform.ContentHandle.IsClosed);
            Assert.Equal(fixture.StructuraPin.Sha256, structura.Inspection.Sha256);
            Assert.Equal(fixture.PlatformPin.Sha256, platform.Inspection.Sha256);
            Assert.Equal(2, Directory.EnumerateFiles(Path.GetDirectoryName(structura.StagedPath)!).Count());
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.AcquireVerifiedRollbackPayloadAsync(fixture.ToPayload(fixture.StructuraPin)));
        }
        finally
        {
            await structura.DisposeAsync();
            await platform.DisposeAsync();
        }
    }

    [Fact]
    public async Task SourceCannotBeSwappedAfterItIsOpenedForInspection()
    {
        using var fixture = new PayloadFixture();
        Exception? swapFailure = null;
        var reader = new FakeMsiReader(fixture.IdentityForPath, path =>
        {
            if (!string.Equals(path, fixture.StructuraPath, StringComparison.OrdinalIgnoreCase))
                return;
            var replacement = path + ".replacement";
            File.WriteAllBytes(replacement, [9, 9, 9]);
            try
            {
                File.Move(replacement, path, overwrite: true);
            }
            catch (Exception error)
            {
                swapFailure = error;
                File.Delete(replacement);
            }
        });
        var store = fixture.CreateStore(reader);

        await using var lease = await store.AcquireVerifiedRollbackPayloadAsync(
            fixture.ToPayload(fixture.StructuraPin));

        Assert.True(
            swapFailure is IOException or UnauthorizedAccessException,
            $"Expected Windows to block source replacement, got {swapFailure?.GetType().Name ?? "no error"}.");
        Assert.Equal(fixture.StructuraPin.Sha256, lease.Inspection.Sha256);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("hash")]
    [InlineData("product")]
    [InlineData("upgrade")]
    [InlineData("version")]
    public async Task RejectsLockedSizeHashOrMsiIdentityDrift(string drift)
    {
        using var fixture = new PayloadFixture();
        var pin = fixture.StructuraPin;
        pin = drift switch
        {
            "size" => pin with { SizeBytes = pin.SizeBytes + 1 },
            "hash" => pin with { Sha256 = new string('0', 64) },
            "product" => pin with { Identity = pin.Identity with { ProductCode = Guid.NewGuid() } },
            "upgrade" => pin with { Identity = pin.Identity with { UpgradeCode = Guid.NewGuid() } },
            "version" => pin with { Version = "99.0.0" },
            _ => throw new ArgumentOutOfRangeException(nameof(drift)),
        };
        var store = fixture.CreateStore(pins: fixture.PinsWithStructura(pin));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.AcquireVerifiedRollbackPayloadAsync(fixture.ToPayload(pin)));
        Assert.Empty(Directory.EnumerateFiles(fixture.StagingRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PinnedDestinationCannotChangeUntilLeaseIsDisposed()
    {
        using var fixture = new PayloadFixture();
        var store = fixture.CreateStore();
        var lease = Assert.IsAssignableFrom<IWindowsVerifiedRollbackPayloadLease>(
            await store.AcquireVerifiedRollbackPayloadAsync(fixture.ToPayload(fixture.StructuraPin)));
        var ownedHandle = lease.ContentHandle;

        Assert.Throws<IOException>(() => File.WriteAllBytes(lease.StagedPath, [0]));
        await lease.DisposeAsync();

        Assert.True(ownedHandle.IsClosed);
        Assert.Throws<ObjectDisposedException>(() => _ = lease.ContentHandle);
        File.WriteAllBytes(lease.StagedPath, [0]);
        Assert.Equal(1, new FileInfo(lease.StagedPath).Length);
    }

    [Fact]
    public async Task ReacquireRejectsContentDriftAtDurablePath()
    {
        using var fixture = new PayloadFixture();
        var store = fixture.CreateStore();
        var lease = Assert.IsAssignableFrom<IWindowsVerifiedRollbackPayloadLease>(
            await store.AcquireVerifiedRollbackPayloadAsync(fixture.ToPayload(fixture.StructuraPin)));
        var receipt = new ProtectedRollbackPayloadReceipt(
            lease.HandleId, lease.Inspection.Identity.Kind, lease.Protection);
        var path = lease.StagedPath;
        await lease.DisposeAsync();
        File.WriteAllBytes(path, [1, 2]);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.ReacquireVerifiedRollbackPayloadAsync(receipt));
    }

    [Fact]
    public async Task ReacquireRejectsReceiptKindThatDiffersFromOpaqueHandle()
    {
        using var fixture = new PayloadFixture();
        var store = fixture.CreateStore();
        var lease = Assert.IsAssignableFrom<IWindowsVerifiedRollbackPayloadLease>(
            await store.AcquireVerifiedRollbackPayloadAsync(fixture.ToPayload(fixture.StructuraPin)));
        var wrongKindReceipt = new ProtectedRollbackPayloadReceipt(
            lease.HandleId,
            LegacyApplicationKind.PlatformConnector,
            RollbackPayloadProtection.ProtectedMachineStaging);
        await lease.DisposeAsync();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.ReacquireVerifiedRollbackPayloadAsync(wrongKindReceipt));
    }

    [Fact]
    public async Task OperationScopedStageReplayAdoptsTheSamePinnedSlotWithoutCopying()
    {
        using var fixture = new PayloadFixture();
        var sid = CurrentSid();
        var operationId = Guid.NewGuid();
        var security = new TemporaryStagingSecurity();
        var stager = fixture.CreateOperationStager(operationId, sid, security);
        var first = await stager.StageVerifiedRollbackPayloadAsync(LegacyApplicationKind.StructuraConnector);
        var receipt = new ProtectedRollbackPayloadReceipt(first.HandleId, first.Inspection.Identity.Kind, first.Protection);
        var path = Assert.IsAssignableFrom<IWindowsVerifiedRollbackPayloadLease>(first).StagedPath;
        var bytes = File.ReadAllBytes(path);
        var protectCalls = security.RollbackFileProtectionCalls;
        await first.DisposeAsync();

        var replay = await fixture.CreateOperationStager(operationId, sid, security)
            .StageVerifiedRollbackPayloadAsync(LegacyApplicationKind.StructuraConnector);
        try
        {
            Assert.Equal(receipt.HandleId, replay.HandleId);
            Assert.Equal(receipt.Kind, replay.Inspection.Identity.Kind);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(protectCalls, security.RollbackFileProtectionCalls);
        }
        finally { await replay.DisposeAsync(); }
    }

    [Fact]
    public void OperationScopedStagerRejectsGroupSidBeforePreparingAnyPath()
    {
        using var fixture = new PayloadFixture();
        var security = new TemporaryStagingSecurity();

        Assert.Throws<ArgumentException>(() => fixture.CreateOperationStager(
            Guid.NewGuid(), "S-1-5-32-545", security));

        Assert.Equal(0, security.PrepareRootCalls);
        Assert.False(Directory.Exists(fixture.StagingRoot));
    }

    [Fact]
    public void ProductionStagerRequiresTokenBackedIdentityAndRejectsGroupBeforePreparingRoot()
    {
        var publicConstructor = Assert.Single(typeof(WindowsOperationRollbackPayloadStager).GetConstructors());
        var parameters = publicConstructor.GetParameters();
        Assert.Equal(typeof(Guid), parameters[0].ParameterType);
        Assert.Equal(typeof(WindowsIdentity), parameters[1].ParameterType);
        Assert.Equal(typeof(SecurityIdentifier), parameters[2].ParameterType);
        Assert.Equal(typeof(WindowsRollbackPayloadSources), parameters[3].ParameterType);

        if (!OperatingSystem.IsWindows())
            return;

        using var fixture = new PayloadFixture();
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var groupSid = new SecurityIdentifier("S-1-5-32-545");
        var expectedRoot = Path.Combine(fixture.Root, "must-not-be-created");

        Assert.Throws<UnauthorizedAccessException>(() => new WindowsOperationRollbackPayloadStager(
            Guid.NewGuid(), identity, groupSid,
            new WindowsRollbackPayloadSources(fixture.StructuraPath, fixture.PlatformPath), expectedRoot));
        Assert.False(Directory.Exists(expectedRoot));
    }

    [Fact]
    public async Task OperationScopedStageRefusesForeignBatchAndDoesNotReplaceCorruptSlot()
    {
        using var fixture = new PayloadFixture();
        var sid = CurrentSid();
        var operationId = Guid.NewGuid();
        var security = new TemporaryStagingSecurity();
        var first = await fixture.CreateOperationStager(operationId, sid, security)
            .StageVerifiedRollbackPayloadAsync(LegacyApplicationKind.PlatformConnector);
        var path = Assert.IsAssignableFrom<IWindowsVerifiedRollbackPayloadLease>(first).StagedPath;
        await first.DisposeAsync();

        var foreignStager = fixture.CreateOperationStager(operationId, "S-1-5-21-100-200-300-2002", security);
        var foreignError = await Assert.ThrowsAsync<WindowsRollbackPayloadNeedsManualRecoveryException>(async () =>
            await foreignStager.StageVerifiedRollbackPayloadAsync(LegacyApplicationKind.PlatformConnector));
        Assert.Equal(LegacyApplicationKind.PlatformConnector, foreignError.Kind);

        File.WriteAllBytes(path, [0xFA, 0xCE]);
        var corruptBytes = File.ReadAllBytes(path);
        var corruptError = await Assert.ThrowsAsync<WindowsRollbackPayloadNeedsManualRecoveryException>(async () =>
            await fixture.CreateOperationStager(operationId, sid, security)
                .StageVerifiedRollbackPayloadAsync(LegacyApplicationKind.PlatformConnector));
        Assert.Equal(LegacyApplicationKind.PlatformConnector, corruptError.Kind);
        Assert.Equal(corruptBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task OperationScopedOriginalUserReopensReadOnlyAndRejectsOtherSid()
    {
        using var fixture = new PayloadFixture();
        var sid = CurrentSid();
        var operationId = Guid.NewGuid();
        var security = new TemporaryStagingSecurity();
        var staged = await fixture.CreateOperationStager(operationId, sid, security)
            .StageVerifiedRollbackPayloadAsync(LegacyApplicationKind.StructuraConnector);
        var receipt = new ProtectedRollbackPayloadReceipt(staged.HandleId, staged.Inspection.Identity.Kind, staged.Protection);
        var path = Assert.IsAssignableFrom<IWindowsVerifiedRollbackPayloadLease>(staged).StagedPath;
        await staged.DisposeAsync();
        var originalBytes = File.ReadAllBytes(path);
        var originalWriteTime = File.GetLastWriteTimeUtc(path);

        var opener = fixture.CreateOperationOpener(operationId, sid, security);
        var reopened = await opener.ReopenVerifiedRollbackPayloadAsync(receipt);
        try
        {
            Assert.Equal(originalBytes, File.ReadAllBytes(path));
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(path, [0x00]));
            Assert.Equal(originalBytes, File.ReadAllBytes(path));
        }
        finally { await reopened.DisposeAsync(); }

        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(path));
        var otherUser = fixture.CreateOperationOpener(operationId, "S-1-5-21-100-200-300-2002", security);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await otherUser.ReopenVerifiedRollbackPayloadAsync(receipt));
    }

    private static string CurrentSid()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User?.Value ?? throw new InvalidOperationException("The test process has no Windows SID.");
    }

    [Fact]
    public void ProductionSecurityRejectsUserWritableTempDestination()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var fixture = new PayloadFixture();
        var security = new WindowsMachineStagingSecurity();

        Assert.Throws<InvalidDataException>(() => security.PrepareRoot(fixture.StagingRoot));
    }

    [Fact]
    public void ProductionSecurityRejectsUnsafeAcl()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var fixture = new PayloadFixture();
        var path = Path.Combine(fixture.Root, "untrusted-acl");
        Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The test process has no Windows SID.");
        security.SetOwner(owner);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.WriteData | FileSystemRights.Delete,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);

        var productionSecurity = new WindowsMachineStagingSecurity();
        Assert.Throws<UnauthorizedAccessException>(() =>
            productionSecurity.ValidateProtectedDirectory(path));
    }

    [Fact]
    public void ProductionSecurityRejectsReparsePointAncestor()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var fixture = new PayloadFixture();
        var target = Path.Combine(fixture.Root, "security-junction-target");
        var child = Path.Combine(target, "child");
        Directory.CreateDirectory(child);
        var junction = Path.Combine(fixture.Root, "security-junction");
        CreateJunction(junction, target);
        try
        {
            var productionSecurity = new WindowsMachineStagingSecurity();
            Assert.Throws<InvalidDataException>(() =>
                productionSecurity.ValidateProtectedDirectory(Path.Combine(junction, "child")));
        }
        finally
        {
            if (Directory.Exists(junction))
                Directory.Delete(junction, recursive: false);
        }
    }

    [Fact]
    public async Task RejectsAReparsePointInSourceAncestors()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var fixture = new PayloadFixture();
        var target = Path.Combine(fixture.Root, "junction-target");
        Directory.CreateDirectory(target);
        var targetFile = Path.Combine(target, fixture.StructuraPin.InstallerName);
        File.Copy(fixture.StructuraPath, targetFile);
        var junction = Path.Combine(fixture.Root, "junction");
        CreateJunction(junction, target);
        try
        {
            var sources = new WindowsRollbackPayloadSources(
                Path.Combine(junction, fixture.StructuraPin.InstallerName),
                fixture.PlatformPath);
            var store = fixture.CreateStore(sources: sources);

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await store.AcquireVerifiedRollbackPayloadAsync(fixture.ToPayload(fixture.StructuraPin)));
        }
        finally
        {
            if (Directory.Exists(junction))
                Directory.Delete(junction, recursive: false);
        }
    }

    private static void CreateJunction(string junction, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            ArgumentList = { "/d", "/c", "mklink", "/J", junction, target },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start mklink for the test junction.");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Could not create test junction: " + process.StandardError.ReadToEnd());
    }

    private sealed class PayloadFixture : IDisposable
    {
        private static readonly MsiPackageIdentity StructuraIdentity = new(
            Guid.Parse("8C16FFEC-F35D-45AF-BE71-65BB07533BF8"),
            Guid.Parse("0E67CBE8-8F77-45EA-B89D-E58C8C554B37"),
            "1.0.31");
        private static readonly MsiPackageIdentity PlatformIdentity = new(
            Guid.Parse("93CCDE79-6601-4232-8489-8A6435FD6D62"),
            Guid.Parse("A7E5408C-1238-4A45-8A84-E3AE45107D7A"),
            "1.2.1");

        public PayloadFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "windows-payload-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            StagingRoot = Path.Combine(Root, "stage");
            StructuraPath = Path.Combine(Root, "Connector.Desktop.Setup.msi");
            PlatformPath = Path.Combine(Root, "Platform.Connector.Desktop.Setup.msi");
            File.WriteAllBytes(StructuraPath, [1, 2, 3, 4, 5]);
            File.WriteAllBytes(PlatformPath, [6, 7, 8, 9]);
            StructuraPin = CreatePin(
                LegacyApplicationKind.StructuraConnector,
                "structura-connector",
                StructuraPath,
                StructuraIdentity);
            PlatformPin = CreatePin(
                LegacyApplicationKind.PlatformConnector,
                "platform-connector",
                PlatformPath,
                PlatformIdentity);
        }

        public string Root { get; }
        public string StagingRoot { get; }
        public string StructuraPath { get; }
        public string PlatformPath { get; }
        public LegacyUpgradePin StructuraPin { get; }
        public LegacyUpgradePin PlatformPin { get; }

        public WindowsRollbackPayloadStore CreateStore(
            IMsiPackageIdentityReader? reader = null,
            IReadOnlyDictionary<LegacyApplicationKind, LegacyUpgradePin>? pins = null,
            WindowsRollbackPayloadSources? sources = null) => new(
                sources ?? new WindowsRollbackPayloadSources(StructuraPath, PlatformPath),
                StagingRoot,
                pins ?? PinsWithStructura(StructuraPin),
                new TemporaryStagingSecurity(),
                reader ?? new FakeMsiReader(IdentityForPath));

        public WindowsOperationRollbackPayloadStager CreateOperationStager(
            Guid operationId, string sid, TemporaryStagingSecurity security) => new(
                operationId, sid, new WindowsRollbackPayloadSources(StructuraPath, PlatformPath), StagingRoot,
                PinsWithStructura(StructuraPin), security, new FakeMsiReader(IdentityForPath));

        public WindowsOperationRollbackPayloadOpener CreateOperationOpener(
            Guid operationId, string sid, TemporaryStagingSecurity security) => new(
                operationId, sid, StagingRoot, PinsWithStructura(StructuraPin), security,
                new FakeMsiReader(IdentityForPath));

        public IReadOnlyDictionary<LegacyApplicationKind, LegacyUpgradePin> PinsWithStructura(
            LegacyUpgradePin structura) => new Dictionary<LegacyApplicationKind, LegacyUpgradePin>
            {
                [LegacyApplicationKind.StructuraConnector] = structura,
                [LegacyApplicationKind.PlatformConnector] = PlatformPin,
            };

        public LegacyRollbackPayload ToPayload(LegacyUpgradePin pin) => new(
            pin.Identity,
            pin.PackageId,
            pin.InstallerName,
            pin.Version,
            pin.SizeBytes,
            pin.Sha256,
            Verified: true);

        public MsiPackageIdentity IdentityForPath(string path) =>
            Path.GetFileName(path) == StructuraPin.InstallerName ? StructuraIdentity : PlatformIdentity;

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        private static LegacyUpgradePin CreatePin(
            LegacyApplicationKind kind,
            string packageId,
            string path,
            MsiPackageIdentity identity)
        {
            var bytes = File.ReadAllBytes(path);
            return new LegacyUpgradePin(
                kind,
                packageId,
                Path.GetFileName(path),
                identity.Version,
                new LegacyApplicationIdentity(kind, identity.ProductCode, identity.UpgradeCode),
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)));
        }
    }

    private sealed class FakeMsiReader(
        Func<string, MsiPackageIdentity> identity,
        Action<string>? beforeRead = null) : IMsiPackageIdentityReader
    {
        public MsiPackageIdentity Read(string path)
        {
            beforeRead?.Invoke(path);
            return identity(path);
        }
    }

    private sealed class TemporaryStagingSecurity : IWindowsMachineStagingSecurity
    {
        private readonly Dictionary<string, string> _rollbackDirectories = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _rollbackFiles = new(StringComparer.OrdinalIgnoreCase);
        public int RollbackFileProtectionCalls { get; private set; }
        public int PrepareRootCalls { get; private set; }

        public string PrepareRoot(string requestedRoot)
        {
            PrepareRootCalls++;
            var fullPath = Path.GetFullPath(requestedRoot);
            Directory.CreateDirectory(fullPath);
            return fullPath;
        }

        public void CreateProtectedDirectory(string path) => Directory.CreateDirectory(path);
        public void ProtectAndValidateFile(string path) { }
        public void ValidateSharedDirectory(string path) { }
        public void ValidateProtectedDirectory(string path) { }
        public void ValidateProtectedFile(string path) { }
        public void CreateRollbackBatchDirectory(string path, string initiatingUserSid)
        {
            Directory.CreateDirectory(path);
            _rollbackDirectories[Path.GetFullPath(path)] = initiatingUserSid;
        }

        public void ValidateRollbackBatchDirectory(string path, string initiatingUserSid)
        {
            if (!_rollbackDirectories.TryGetValue(Path.GetFullPath(path), out var sid) ||
                !string.Equals(sid, initiatingUserSid, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The rollback batch belongs to another SID.");
        }

        public void ProtectAndValidateRollbackFile(string path, string initiatingUserSid)
        {
            _rollbackFiles[Path.GetFullPath(path)] = initiatingUserSid;
            RollbackFileProtectionCalls++;
        }

        public void ValidateRollbackFile(string path, string initiatingUserSid)
        {
            if (!File.Exists(path) || !_rollbackFiles.TryGetValue(Path.GetFullPath(path), out var sid) ||
                !string.Equals(sid, initiatingUserSid, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The rollback MSI ACL does not match the initiating SID.");
        }
    }
}
