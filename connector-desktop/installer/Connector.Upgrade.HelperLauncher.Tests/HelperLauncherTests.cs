using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using MutualChannel = global::Connector.Upgrade.MutualMachineChannel.MutualMachineChannel;
using Xunit;

namespace Connector.Upgrade.HelperLauncher.Tests;

public sealed class HelperLauncherTests
{
    private static readonly string TestPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trusted-helper.exe"));
    private static readonly HelperImagePin ValidPin = new(TestPath, new string('A', 64), new string('B', 40));
    private static readonly Guid OperationId = Guid.Parse("d37fb975-8b65-4dc6-ae2d-91c4304429d8");
    private static string PipeName => $"Structura.Connector.Upgrade.MachinePipe.{OperationId:N}.0123456789abcdef01234567";
    private static SecurityIdentifier UserSid => WindowsIdentity.GetCurrent().User!;

    [Fact]
    public void MutualLaunchUsesExactAContractAndCurrentProcessPid()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pipe = MutualChannel.GetPipeName("A", OperationId, UserSid);
        var leases = new FakeLeaseFactory();
        var native = new FakeNativeLauncher { ReturnedImagePath = TestPath };

        using var process = new HelperLauncher(leases, native).LaunchMutual(ValidPin, pipe, OperationId, UserSid);

        Assert.Equal($"--pipe \"{pipe}\" --operation \"{OperationId:N}\" --caller-sid \"{UserSid.Value}\" --caller-pid \"{Environment.ProcessId}\"", native.Arguments);
        Assert.DoesNotContain("token", native.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.False(process.IsInvalid);
        Assert.Equal(2, leases.LastLease!.RevalidationCalls);
    }

    [Fact]
    public void MutualLaunchRejectsPipeFromDifferentSidBeforeLeaseOrLaunch()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pipe = MutualChannel.GetPipeName("A", OperationId, UserSid);
        pipe = pipe[..^1] + (pipe[^1] == '0' ? '1' : '0');
        var leases = new FakeLeaseFactory();
        var native = new FakeNativeLauncher();

        Assert.Throws<ArgumentException>(() => { new HelperLauncher(leases, native).LaunchMutual(ValidPin, pipe, OperationId, UserSid); });
        Assert.Equal(0, leases.OpenCalls);
        Assert.Equal(0, native.LaunchCalls);
    }

    [Fact]
    public void MutualLaunchRejectsBroadSidBeforeLeaseOrLaunch()
    {
        if (!OperatingSystem.IsWindows()) return;
        var leases = new FakeLeaseFactory();
        var native = new FakeNativeLauncher();

        Assert.Throws<ArgumentException>(() => { new HelperLauncher(leases, native).LaunchMutual(
            ValidPin, "ignored", OperationId, new SecurityIdentifier(WellKnownSidType.WorldSid, null)); });
        Assert.Equal(0, leases.OpenCalls);
        Assert.Equal(0, native.LaunchCalls);
    }

    [Fact]
    public void MutualLaunchRejectsNonAOrMismatchedOperationPipe()
    {
        if (!OperatingSystem.IsWindows()) return;
        var leases = new FakeLeaseFactory();
        var native = new FakeNativeLauncher();
        var pipe = MutualChannel.GetPipeName("B", OperationId, UserSid);

        Assert.Throws<ArgumentException>(() => { new HelperLauncher(leases, native).LaunchMutual(ValidPin, pipe, OperationId, UserSid); });
        Assert.Equal(0, leases.OpenCalls);
        Assert.Equal(0, native.LaunchCalls);
    }

    [Fact]
    public void UntrustedImageIsRejectedBeforeNativeLaunch()
    {
        var leases = new FakeLeaseFactory { OpenFailure = new InvalidDataException("pin mismatch") };
        var native = new FakeNativeLauncher();

        Assert.Throws<InvalidDataException>(() => new HelperLauncher(leases, native).Launch(ValidPin, PipeName, OperationId));

        Assert.Equal(0, native.LaunchCalls);
    }

    [Fact]
    public void TemporaryUnprotectedImageNeverReachesNativeLaunch()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"connector-helper-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, [0x4d, 0x5a, 0x00, 0x01]);
        try
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            var pin = new HelperImagePin(Path.GetFullPath(path), hash, new string('B', 40));
            var native = new FakeNativeLauncher();

            Assert.Throws<InvalidDataException>(() => new HelperLauncher(TrustedHelperImageLeaseFactory.Instance, native)
                .Launch(pin, PipeName, OperationId));

            Assert.Equal(0, native.LaunchCalls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ChangedLeaseIdentityIsRejectedBeforeNativeLaunch()
    {
        var leases = new FakeLeaseFactory { RevalidateFailure = new InvalidDataException("changed image") };
        var native = new FakeNativeLauncher();

        Assert.Throws<InvalidDataException>(() => new HelperLauncher(leases, native).Launch(ValidPin, PipeName, OperationId));

        Assert.Equal(0, native.LaunchCalls);
    }

    [Theory]
    [InlineData("C:\\temp\\helper.exe", "not-a-hash", "not-a-signer")]
    [InlineData("relative.exe", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    public void InvalidPinsFailBeforeLeaseOrLaunch(string path, string hash, string signer)
    {
        var leases = new FakeLeaseFactory();
        var native = new FakeNativeLauncher();
        var pin = new HelperImagePin(path, hash, signer);

        Assert.Throws<ArgumentException>(() => new HelperLauncher(leases, native).Launch(pin, PipeName, OperationId));
        Assert.Equal(0, leases.OpenCalls);
        Assert.Equal(0, native.LaunchCalls);
    }

    [Fact]
    public void PipeOperationMismatchNeverReachesLeaseOrNativeLaunch()
    {
        var leases = new FakeLeaseFactory();
        var native = new FakeNativeLauncher();

        Assert.Throws<ArgumentException>(() => new HelperLauncher(leases, native).Launch(ValidPin, PipeName, Guid.NewGuid()));

        Assert.Equal(0, leases.OpenCalls);
        Assert.Equal(0, native.LaunchCalls);
    }

    [Fact]
    public void CertificateFreeHelperPinRequiresSignedLengthAndCannotCarrySignerEvidence()
    {
        var valid = new HelperImagePin(TestPath, 123, new string('A', 64),
            ReleaseArtifactTrustMode.SignedManifestHash, null);
        valid.Validate();

        Assert.Throws<ArgumentException>(() => new HelperImagePin(TestPath, 0, new string('A', 64),
            ReleaseArtifactTrustMode.SignedManifestHash, null).Validate());
        Assert.Throws<ArgumentException>(() => new HelperImagePin(TestPath, 123, new string('A', 64),
            ReleaseArtifactTrustMode.SignedManifestHash, new string('B', 40)).Validate());
    }

    [Fact]
    public void OnlyFixedBootstrapArgumentsArePassedAndProcessHandleIsRetained()
    {
        var leases = new FakeLeaseFactory();
        var native = new FakeNativeLauncher { ReturnedImagePath = TestPath };

        using var process = new HelperLauncher(leases, native).Launch(ValidPin, PipeName, OperationId);

        Assert.Equal("runas", native.Verb);
        Assert.Equal(TestPath, native.ImagePath);
        Assert.Equal($"--pipe \"{PipeName}\" --operation \"{OperationId:N}\"", native.Arguments);
        Assert.DoesNotContain("token", native.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.False(process.IsInvalid);
        Assert.Equal(2, leases.LastLease!.RevalidationCalls);
        Assert.True(leases.LastLease.Disposed);
    }

    [Fact]
    public void LaunchedImagePathMismatchDisposesProcessAndFailsClosed()
    {
        var leases = new FakeLeaseFactory();
        var native = new FakeNativeLauncher { ReturnedImagePath = Path.Combine(Path.GetTempPath(), "other.exe") };
        var process = Assert.Throws<InvalidDataException>(() => new HelperLauncher(leases, native).Launch(ValidPin, PipeName, OperationId));
        Assert.NotNull(process);
        Assert.True(native.LastProcess!.IsClosed);
    }

    [Fact]
    public void HelperPathAclRejectsUntrustedOwner()
    {
        var descriptor = CreateProtectedDirectoryAcl(new SecurityIdentifier(WellKnownSidType.WorldSid, null));

        Assert.Throws<UnauthorizedAccessException>(() => ProtectedHelperPath.AssertAclTrusted(descriptor));
    }

    [Theory]
    [InlineData(0x10000000)] // FILE_GENERIC_ALL
    [InlineData(0x40000000)] // FILE_GENERIC_WRITE
    public void GenericAclMutationMasksAreRejected(int rights)
    {
        Assert.True(ProtectedHelperPath.HasMutationRights((FileSystemRights)rights));
    }

    [Fact]
    public void HelperPathAclRejectsUntrustedChildMutationRights()
    {
        var descriptor = CreateProtectedDirectoryAcl(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        descriptor.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.DeleteSubdirectoriesAndFiles,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));

        Assert.Throws<UnauthorizedAccessException>(() => ProtectedHelperPath.AssertAclTrusted(descriptor));
    }

    private static DirectorySecurity CreateProtectedDirectoryAcl(SecurityIdentifier owner)
    {
        var descriptor = new DirectorySecurity();
        descriptor.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        descriptor.SetOwner(owner);
        return descriptor;
    }

    private sealed class FakeLeaseFactory : IHelperImageLeaseFactory
    {
        public Exception? OpenFailure { get; init; }
        public Exception? RevalidateFailure { get; init; }
        public int OpenCalls { get; private set; }
        public FakeLease? LastLease { get; private set; }
        public IHelperImageLease Open(HelperImagePin pin)
        {
            OpenCalls++;
            if (OpenFailure is not null) throw OpenFailure;
            LastLease = new FakeLease(pin.AbsolutePath, RevalidateFailure);
            return LastLease;
        }
    }

    private sealed class FakeLease(string imagePath, Exception? revalidateFailure) : IHelperImageLease
    {
        public string ImagePath { get; } = imagePath;
        public int RevalidationCalls { get; private set; }
        public bool Disposed { get; private set; }
        public void RevalidateForLaunch()
        {
            RevalidationCalls++;
            if (revalidateFailure is not null) throw revalidateFailure;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeNativeLauncher : IHelperNativeLauncher
    {
        public int LaunchCalls { get; private set; }
        public string? ImagePath { get; private set; }
        public string? Arguments { get; private set; }
        public string Verb => "runas";
        public string? ReturnedImagePath { get; init; }
        public SafeProcessHandle? LastProcess { get; private set; }
        public SafeProcessHandle LaunchElevated(string imagePath, string arguments)
        {
            LaunchCalls++;
            ImagePath = imagePath;
            Arguments = arguments;
            LastProcess = new SafeProcessHandle(new IntPtr(123), ownsHandle: false);
            return LastProcess;
        }
        public string GetProcessImagePath(SafeProcessHandle process) => ReturnedImagePath ?? ImagePath!;
    }
}
