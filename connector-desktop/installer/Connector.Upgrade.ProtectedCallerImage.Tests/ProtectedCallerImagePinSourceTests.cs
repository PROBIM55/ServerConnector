using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Connector.Upgrade.ProtectedCallerImage;
using Connector.Upgrade.HelperReleaseTrust;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Connector.Upgrade.ProtectedCallerImage.Tests;

public sealed class ProtectedCallerImagePinSourceTests
{
    private const string Hash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string Signer = "0123456789ABCDEF0123456789ABCDEF01234567";
    private const string RelativeImage = "StructuraConnectorInstaller\\Bootstrapper\\Connector.Upgrade.Bootstrapper.exe";
    private static readonly SecurityIdentifier UserSid = new("S-1-5-21-111-222-333-1001");
    private static readonly string ExpectedPath = Path.GetFullPath(Path.Combine("C:\\Program Files", RelativeImage));

    [Fact]
    public void MatchingLiveCallerPathSidHashAndSignerPass()
    {
        var runtime = Runtime();
        Source(Manifest(), runtime).AssertTrustedCaller(ProcessHandle(), UserSid);
        Assert.Equal(ExpectedPath, runtime.LastExpectedPath);
        Assert.Equal(2, runtime.ProcessReads);
        Assert.Equal(1, runtime.ImageInspections);
        Assert.True(runtime.LastVerifyAuthenticode);
    }

    [Fact]
    public void Schema2AcceptsHashAndProtectedPathWithoutAuthenticode()
    {
        var runtime = Runtime();
        runtime.ImageSigner = null;
        Source(Manifest(schemaVersion: 2, signer: ""), runtime).AssertTrustedCaller(ProcessHandle(), UserSid);
        Assert.False(runtime.LastVerifyAuthenticode);
    }

    [Fact]
    public void VerifiedSchema4CallerPinUsesSizeHashProtectedImageAndRetainedProcessChecksWithoutAuthenticode()
    {
        var runtime = Runtime();
        runtime.ImageSize = 7654;
        Source(string.Empty, runtime, () => new ProtectedCallerImagePin(RelativeImage, 7654, Hash))
            .AssertTrustedCaller(ProcessHandle(), UserSid);
        Assert.Equal(2, runtime.ProcessReads);
        Assert.Equal(1, runtime.ImageInspections);
        Assert.False(runtime.LastVerifyAuthenticode);
    }

    [Fact]
    public void Schema4CallerSizeHashPathSidAndLivenessMismatchesFailClosed()
    {
        var wrongSize = Runtime();
        wrongSize.ImageSize = 10;
        Assert.Throws<SecurityException>(() => Source(string.Empty, wrongSize,
            () => new ProtectedCallerImagePin(RelativeImage, 7654, Hash)).AssertTrustedCaller(ProcessHandle(), UserSid));

        var wrongHash = Runtime();
        Assert.Throws<SecurityException>(() => Source(string.Empty, wrongHash,
            () => new ProtectedCallerImagePin(RelativeImage, 7654, new string('F', 64))).AssertTrustedCaller(ProcessHandle(), UserSid));

        var wrongPath = Runtime();
        Assert.Throws<SecurityException>(() => Source(string.Empty, wrongPath,
            () => new ProtectedCallerImagePin("StructuraConnectorInstaller\\Bootstrapper\\Other.exe", 7654, Hash)).AssertTrustedCaller(ProcessHandle(), UserSid));
    }

    [Fact]
    public void VerifiedLegacyReleaseMayUseEmbeddedCompatibilityPin()
    {
        var runtime = Runtime();
        Source(Manifest(), runtime, () => null).AssertTrustedCaller(ProcessHandle(), UserSid);
        Assert.Equal(2, runtime.ProcessReads);
        Assert.True(runtime.LastVerifyAuthenticode);
    }

    [Fact]
    public void ReleaseTrustFailureNeverFallsBackToEmbeddedCompatibilityPin()
    {
        var runtime = Runtime();
        var source = Source(Manifest(), runtime, () => throw new SecurityException("bad release signature or ACL"));
        Assert.Throws<SecurityException>(() => source.AssertTrustedCaller(ProcessHandle(), UserSid));
        Assert.Equal(0, runtime.ProcessReads);
        Assert.Equal(0, runtime.ImageInspections);
    }

    [Fact]
    public void Schema2RejectsAnUnexpectedAuthenticodePin()
    {
        var runtime = Runtime();
        Assert.Throws<SecurityException>(() => Source(Manifest(signer: Signer, schemaVersion: 2), runtime)
            .AssertTrustedCaller(ProcessHandle(), UserSid));
        Assert.Equal(0, runtime.ImageInspections);
    }

    [Fact]
    public void AnyOtherSafeImagePathIsRejected()
    {
        var runtime = Runtime();
        Assert.Throws<SecurityException>(() => Source(Manifest(path: "StructuraConnectorInstaller\\Bootstrapper\\Other.exe"), runtime)
            .AssertTrustedCaller(ProcessHandle(), UserSid));
        Assert.Equal(0, runtime.ProcessReads);
    }

    [Fact]
    public void ProductionEmbeddedManifestFailsClosedWhileReleasePinsAreBlank()
    {
        var source = new ProtectedCallerImagePinSource();
        Assert.Throws<SecurityException>(() => source.AssertTrustedCaller(ProcessHandle(), UserSid));
    }

    [Fact]
    public void MissingManifestFailsBeforeInspectingProcessOrFile()
    {
        var runtime = Runtime();
        var source = new ProtectedCallerImagePinSource(() => null, runtime);
        Assert.Throws<SecurityException>(() => source.AssertTrustedCaller(ProcessHandle(), UserSid));
        Assert.Equal(0, runtime.ProcessReads);
        Assert.Equal(0, runtime.ImageInspections);
    }

    [Theory]
    [InlineData("", "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", "0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("Structura Connector\\..\\other.exe", "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", "0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("Structura Connector\\Connector.Desktop.exe", "", "0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("Structura Connector\\Connector.Desktop.exe", "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", "")]
    public void InvalidEmbeddedPathOrMissingHashOrSignerFailsClosed(string path, string hash, string signer)
    {
        var runtime = Runtime();
        Assert.Throws<SecurityException>(() => Source(Manifest(path, hash, signer), runtime).AssertTrustedCaller(ProcessHandle(), UserSid));
        Assert.Equal(0, runtime.ImageInspections);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("hash")]
    [InlineData("signer")]
    [InlineData("sid")]
    [InlineData("exited")]
    public void ChangedCallerPathImagePinSidOrLivenessIsRejected(string mismatch)
    {
        var runtime = Runtime();
        if (mismatch == "path") runtime.ProcessPath = Path.Combine("C:\\Users\\Public", "Connector.Desktop.exe");
        if (mismatch == "hash") runtime.ImageHash = new string('F', 64);
        if (mismatch == "signer") runtime.ImageSigner = new string('F', 40);
        if (mismatch == "sid") runtime.ProcessSid = new SecurityIdentifier("S-1-5-21-111-222-333-1002");
        if (mismatch == "exited") runtime.ProcessLive = false;

        Assert.Throws<SecurityException>(() => Source(Manifest(), runtime).AssertTrustedCaller(ProcessHandle(), UserSid));
    }

    [Fact]
    public void SwappedOpenedImagePathIsRejectedEvenWhenProcessPathMatches()
    {
        var runtime = Runtime();
        runtime.ImageFinalPath = Path.Combine("C:\\Program Files", "Other", "Connector.Desktop.exe");
        Assert.Throws<SecurityException>(() => Source(Manifest(), runtime).AssertTrustedCaller(ProcessHandle(), UserSid));
    }

    private static ProtectedCallerImagePinSource Source(string manifest, FakeRuntime? runtime = null) =>
        new(() => new MemoryStream(Encoding.UTF8.GetBytes(manifest)), runtime ?? Runtime());

    private static ProtectedCallerImagePinSource Source(string manifest, FakeRuntime runtime, Func<ProtectedCallerImagePin?> signedPinLoader) =>
        new(() => new MemoryStream(Encoding.UTF8.GetBytes(manifest)), runtime, signedPinLoader);

    private static string Manifest(string path = RelativeImage, string hash = Hash, string signer = Signer, int schemaVersion = 1) =>
        JsonSerializer.Serialize(new { schemaVersion, relativeImagePath = path, sha256 = hash, authenticodeSignerThumbprint = signer });

    private static FakeRuntime Runtime() => new()
    {
        ExpectedPath = ExpectedPath,
        ProcessPath = ExpectedPath,
        ProcessSid = UserSid,
        ImageFinalPath = ExpectedPath,
        ImageHash = Hash,
        ImageSigner = Signer
    };

    private static SafeProcessHandle ProcessHandle() => new(new IntPtr(7), ownsHandle: false);

    private sealed class FakeRuntime : ICallerImageTrustRuntime
    {
        public string ExpectedPath { get; init; } = string.Empty;
        public string ProcessPath { get; set; } = string.Empty;
        public SecurityIdentifier ProcessSid { get; set; } = UserSid;
        public bool ProcessLive { get; set; } = true;
        public string ImageFinalPath { get; set; } = string.Empty;
        public string ImageHash { get; set; } = string.Empty;
        public long ImageSize { get; set; }
        public string? ImageSigner { get; set; } = string.Empty;
        public int ProcessReads { get; private set; }
        public int ImageInspections { get; private set; }
        public string? LastExpectedPath { get; private set; }
        public bool LastVerifyAuthenticode { get; private set; }

        public string GetExpectedInstallationPath(string relativeImagePath)
        {
            LastExpectedPath = ExpectedPath;
            return ExpectedPath;
        }

        public CallerProcessIdentity ReadCallerProcess(SafeProcessHandle process)
        {
            ProcessReads++;
            return new CallerProcessIdentity(ProcessLive, ProcessPath, ProcessSid);
        }

        public CallerImageInspection InspectProtectedImage(string expectedPath, bool verifyAuthenticode)
        {
            ImageInspections++;
            LastVerifyAuthenticode = verifyAuthenticode;
            return new CallerImageInspection(ImageFinalPath, ImageSize, ImageHash, ImageSigner);
        }
    }
}
