using System.Security;
using System.Security.Cryptography;
using System.Text;
using Connector.Upgrade.HelperReleaseTrust;
using Connector.Upgrade.HelperLauncher;
using Xunit;

namespace Connector.Upgrade.HelperReleaseTrust.Tests;

public sealed class HelperReleasePinSourceTests
{
    private const string HelperDirectory = @"C:\ProgramData\StructuraConnectorInstaller\Helper";
    private const string HelperPath = HelperDirectory + @"\Connector.Upgrade.Helper.exe";
    private const string SetupPath = HelperDirectory + @"\Setup.exe";
    private const string Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Signer = "0123456789abcdef0123456789abcdef01234567";
    private const string SetupSha256 = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string SetupSigner = "abcdef0123456789abcdef0123456789abcdef01";
    private const long SetupSize = 4321;

    [Fact]
    public void SignedManifestProducesExactHelperImagePin()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Manifest();
        var signature = Sign(key, manifest);
        var runtime = new FakeRuntime();

        var pin = Source(key, manifest, signature, runtime).GetPin();

        Assert.Equal(HelperPath, pin.AbsolutePath);
        Assert.Equal(Sha256, pin.Sha256);
        Assert.Equal(Signer, pin.SignerThumbprint);
        Assert.Equal(1, runtime.InspectionCalls);
    }

    [Fact]
    public void SignedManifestProducesExactVelopackSetupPin()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Manifest();
        var runtime = new FakeRuntime { ImageSize = SetupSize, ImageSha256 = SetupSha256, ImageSigner = SetupSigner };

        var pin = Source(key, manifest, Sign(key, manifest), runtime).GetVelopackSetupPin();

        Assert.Equal(SetupPath, pin.AbsolutePath);
        Assert.Equal(SetupSize, pin.Size);
        Assert.Equal(SetupSha256, pin.Sha256);
        Assert.Equal("StructuraConnector", pin.PackageId);
        Assert.Equal("1.2.3", pin.Version);
        Assert.Equal(SetupSigner, pin.SignerThumbprint);
        Assert.Equal(1, runtime.InspectionCalls);
    }

    [Fact]
    public void SchemaV3ProducesCertificateFreeHashAndLengthPins()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = SchemaV3Manifest();
        var runtime = new FakeRuntime { ImageSize = 4321, ImageSigner = null };
        var source = Source(key, manifest, Sign(key, manifest), runtime);

        var helper = source.GetPin();
        Assert.Equal(ReleaseArtifactTrustMode.SignedManifestHash, helper.TrustMode);
        Assert.Equal(4321, helper.SizeBytes);
        Assert.Null(helper.SignerThumbprint);
        Assert.False(runtime.LastVerifyAuthenticode);

        runtime.ImageSize = SetupSize;
        runtime.ImageSha256 = SetupSha256;
        var setup = source.GetVelopackSetupPin();
        Assert.Equal(ReleaseArtifactTrustMode.SignedManifestHash, setup.TrustMode);
        Assert.Null(setup.SignerThumbprint);
        Assert.False(runtime.LastVerifyAuthenticode);
    }

    [Fact]
    public void SchemaV4ReturnsExactSignedBootstrapperPinAlongsideHashOnlyHelperAndSetup()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = SchemaV4Manifest();
        var runtime = new FakeRuntime { ImageSize = 4321, ImageSigner = null };
        var source = Source(key, manifest, Sign(key, manifest), runtime);

        var pin = source.GetCallerImagePin();

        Assert.NotNull(pin);
        Assert.Equal(ProtectedCallerImagePin.ExpectedRelativePath, pin.RelativePath);
        Assert.Equal(9876, pin.Size);
        Assert.Equal(new string('C', 64), pin.Sha256);
        Assert.Equal(0, runtime.InspectionCalls);
        Assert.Equal(ReleaseArtifactTrustMode.SignedManifestHash, source.GetPin().TrustMode);
    }

    [Theory]
    [InlineData("relativePath", "StructuraConnectorInstaller\\Bootstrapper\\Other.exe")]
    [InlineData("sha256", "bad")]
    public void SchemaV4RejectsInvalidCallerPinBeforeAnyHelperInspection(string property, string value)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var text = Encoding.UTF8.GetString(SchemaV4Manifest()).Replace(
            property == "relativePath" ? "Connector.Upgrade.Bootstrapper.exe" : new string('C', 64), value, StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(text);
        var runtime = new FakeRuntime();
        Assert.Throws<SecurityException>(() => Source(key, bytes, Sign(key, bytes), runtime).GetCallerImagePin());
        Assert.Equal(0, runtime.InspectionCalls);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("hash")]
    public void SchemaV3RejectsHelperSizeOrHashMismatch(string mismatch)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var runtime = new FakeRuntime { ImageSize = 4321, ImageSigner = null };
        if (mismatch == "size") runtime.ImageSize++;
        else runtime.ImageSha256 = new string('F', 64);
        var manifest = SchemaV3Manifest();
        Assert.Throws<SecurityException>(() => Source(key, manifest, Sign(key, manifest), runtime).GetPin());
    }

    [Fact]
    public void DefaultSourceFailsClosedWhenNoBuildTimeReleasePublicKeyIsEmbedded()
    {
        var source = new HelperReleasePinSource();
        Assert.Throws<SecurityException>(() => source.GetPin());
        Assert.Throws<SecurityException>(() => source.GetVelopackSetupPin());
    }

    [Fact]
    public void ModifiedManifestAndWrongKeySignaturesFailBeforeHelperInspection()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Manifest();
        var runtime = new FakeRuntime();

        Assert.Throws<SecurityException>(() => Source(key, manifest, Sign(otherKey, manifest), runtime).GetPin());
        var altered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace(Sha256, new string('F', 64), StringComparison.Ordinal));
        Assert.Throws<SecurityException>(() => Source(key, altered, Sign(key, manifest), runtime).GetPin());
        Assert.Equal(0, runtime.InspectionCalls);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"version\":\"1.2.3\",\"helperRelativePath\":\"Connector.Upgrade.Helper.exe\",\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"authenticodeSignerThumbprint\":\"0123456789abcdef0123456789abcdef01234567\"}")]
    [InlineData("{\"schemaVersion\":1,\"version\":\"1.2.3\",\"helperRelativePath\":\"Connector.Upgrade.Helper.exe\",\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"authenticodeSignerThumbprint\":\"0123456789abcdef0123456789abcdef01234567\",\"extra\":true}")]
    [InlineData("{\"schemaVersion\":2,\"version\":\"1.2.3\",\"helperRelativePath\":\"Connector.Upgrade.Helper.exe\",\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"authenticodeSignerThumbprint\":\"0123456789abcdef0123456789abcdef01234567\"}")]
    [InlineData("{\"schemaVersion\":1,\"version\":\"1.2\",\"helperRelativePath\":\"Connector.Upgrade.Helper.exe\",\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"authenticodeSignerThumbprint\":\"0123456789abcdef0123456789abcdef01234567\"}")]
    [InlineData("{\"schemaVersion\":1,\"version\":\"1.2.3\",\"helperRelativePath\":\"..\\helper.exe\",\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"authenticodeSignerThumbprint\":\"0123456789abcdef0123456789abcdef01234567\"}")]
    [InlineData("{\"schemaVersion\":1,\"version\":\"1.2.3\",\"helperRelativePath\":\"sub\\\\helper.exe\",\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"authenticodeSignerThumbprint\":\"0123456789abcdef0123456789abcdef01234567\"}")]
    [InlineData("{\"schemaVersion\":1,\"version\":\"1.2.3\",\"helperRelativePath\":\"Connector.Upgrade.Helper.exe\",\"sha256\":\"bad\",\"authenticodeSignerThumbprint\":\"0123456789abcdef0123456789abcdef01234567\"}")]
    public void DuplicateUnknownUnsupportedAndInvalidFieldsFailClosed(string json)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var runtime = new FakeRuntime();
        Assert.Throws<SecurityException>(() => Source(key, Encoding.UTF8.GetBytes(json), Sign(key, Encoding.UTF8.GetBytes(json)), runtime).GetPin());
        Assert.Equal(0, runtime.InspectionCalls);
    }

    [Theory]
    [InlineData("{\"relativePath\":\"Setup.exe\",\"relativePath\":\"Setup.exe\",\"size\":4321,\"sha256\":\"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789\",\"packageId\":\"StructuraConnector\",\"version\":\"1.2.3\",\"authenticodeSignerThumbprint\":\"abcdef0123456789abcdef0123456789abcdef01\"}")]
    [InlineData("{\"relativePath\":\"..\\\\Setup.exe\",\"size\":4321,\"sha256\":\"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789\",\"packageId\":\"StructuraConnector\",\"version\":\"1.2.3\",\"authenticodeSignerThumbprint\":\"abcdef0123456789abcdef0123456789abcdef01\"}")]
    [InlineData("{\"relativePath\":\"other.exe\",\"size\":4321,\"sha256\":\"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789\",\"packageId\":\"StructuraConnector\",\"version\":\"1.2.3\",\"authenticodeSignerThumbprint\":\"abcdef0123456789abcdef0123456789abcdef01\"}")]
    public void VelopackSetupDuplicateTraversalAndNonFixedPathsFailClosed(string setupJson)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var json = Encoding.UTF8.GetString(Manifest()).Replace(SetupJson(), setupJson, StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(json);
        var runtime = new FakeRuntime();
        Assert.Throws<SecurityException>(() => Source(key, bytes, Sign(key, bytes), runtime).GetVelopackSetupPin());
        Assert.Equal(0, runtime.InspectionCalls);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("hash")]
    [InlineData("signer")]
    [InlineData("final-path")]
    public void VelopackSetupInspectionMustMatchEverySignedPin(string mismatch)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var runtime = new FakeRuntime { ImageSize = SetupSize, ImageSha256 = SetupSha256, ImageSigner = SetupSigner };
        if (mismatch == "size") runtime.ImageSize++;
        if (mismatch == "hash") runtime.ImageSha256 = new string('F', 64);
        if (mismatch == "signer") runtime.ImageSigner = new string('F', 40);
        if (mismatch == "final-path") runtime.FinalPath = @"C:\ProgramData\Other\Setup.exe";
        var manifest = Manifest();
        Assert.Throws<SecurityException>(() => Source(key, manifest, Sign(key, manifest), runtime).GetVelopackSetupPin());
    }

    [Fact]
    public void VelopackSetupRequiresTheSameValidDetachedSignatureAsHelperPin()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Manifest();
        var runtime = new FakeRuntime { ImageSize = SetupSize, ImageSha256 = SetupSha256, ImageSigner = SetupSigner };
        Assert.Throws<SecurityException>(() => Source(key, manifest, Sign(otherKey, manifest), runtime).GetVelopackSetupPin());
        Assert.Equal(0, runtime.InspectionCalls);
    }

    [Fact]
    public void IncorrectResolvedPathAndMissingOrWeakProtectedImageFailClosed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Manifest();
        var signature = Sign(key, manifest);
        var escaped = new FakeRuntime { ResolvedPath = @"C:\Users\Public\helper.exe" };
        Assert.Throws<SecurityException>(() => Source(key, manifest, signature, escaped).GetPin());

        var missing = new FakeRuntime { InspectionFailure = new FileNotFoundException() };
        Assert.Throws<FileNotFoundException>(() => Source(key, manifest, signature, missing).GetPin());
        var weakAclOrReparse = new FakeRuntime { InspectionFailure = new UnauthorizedAccessException("untrusted ACL or reparse path") };
        Assert.Throws<UnauthorizedAccessException>(() => Source(key, manifest, signature, weakAclOrReparse).GetPin());
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("signer")]
    [InlineData("final-path")]
    public void ImageInspectionMustMatchEverySignedPin(string mismatch)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var runtime = new FakeRuntime();
        if (mismatch == "hash") runtime.ImageSha256 = new string('F', 64);
        if (mismatch == "signer") runtime.ImageSigner = new string('F', 40);
        if (mismatch == "final-path") runtime.FinalPath = @"C:\ProgramData\Other\helper.exe";
        Assert.Throws<SecurityException>(() => Source(key, Manifest(), Sign(key, Manifest()), runtime).GetPin());
    }

    [Fact]
    public void OversizedManifestAndMalformedDetachedSignatureFailClosed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var runtime = new FakeRuntime();
        Assert.Throws<SecurityException>(() => Source(key, new byte[16 * 1024 + 1], Sign(key, []), runtime).GetPin());
        var manifest = Manifest();
        Assert.Throws<SecurityException>(() => Source(key, manifest, new byte[63], runtime).GetPin());
        Assert.Equal(0, runtime.InspectionCalls);
    }

    private static HelperReleasePinSource Source(ECDsa key, byte[] manifest, byte[] signature, FakeRuntime runtime) =>
        new(runtime, () => (manifest, signature), key.ExportSubjectPublicKeyInfoPem());

    private static byte[] Manifest() => Encoding.UTF8.GetBytes(
        $"{{\"schemaVersion\":2,\"version\":\"1.2.3\",\"helperRelativePath\":\"Connector.Upgrade.Helper.exe\",\"sha256\":\"{Sha256}\",\"authenticodeSignerThumbprint\":\"{Signer}\",\"velopackSetup\":{SetupJson()}}}");

    private static byte[] SchemaV3Manifest() => Encoding.UTF8.GetBytes(
        $"{{\"schemaVersion\":3,\"version\":\"1.2.3\",\"helperRelativePath\":\"Connector.Upgrade.Helper.exe\",\"size\":4321,\"sha256\":\"{Sha256}\",\"velopackSetup\":{{\"relativePath\":\"Setup.exe\",\"size\":{SetupSize},\"sha256\":\"{SetupSha256}\",\"packageId\":\"StructuraConnector\",\"version\":\"1.2.3\"}}}}");

    private static byte[] SchemaV4Manifest() => Encoding.UTF8.GetBytes(
        $"{{\"schemaVersion\":4,\"version\":\"1.2.3\",\"helperRelativePath\":\"Connector.Upgrade.Helper.exe\",\"size\":4321,\"sha256\":\"{Sha256}\",\"velopackSetup\":{{\"relativePath\":\"Setup.exe\",\"size\":{SetupSize},\"sha256\":\"{SetupSha256}\",\"packageId\":\"StructuraConnector\",\"version\":\"1.2.3\"}},\"callerImage\":{{\"relativePath\":\"StructuraConnectorInstaller\\\\Bootstrapper\\\\Connector.Upgrade.Bootstrapper.exe\",\"size\":9876,\"sha256\":\"{new string('C', 64)}\"}}}}");

    private static string SetupJson() =>
        $"{{\"relativePath\":\"Setup.exe\",\"size\":{SetupSize},\"sha256\":\"{SetupSha256}\",\"packageId\":\"StructuraConnector\",\"version\":\"1.2.3\",\"authenticodeSignerThumbprint\":\"{SetupSigner}\"}}";

    private static byte[] Sign(ECDsa key, byte[] bytes) =>
        key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private sealed class FakeRuntime : IHelperReleaseTrustRuntime
    {
        public string? ResolvedPath { get; init; }
        public string? FinalPath { get; set; }
        public string ImageSha256 { get; set; } = Sha256;
        public string? ImageSigner { get; set; } = Signer;
        public long ImageSize { get; set; }
        public bool LastVerifyAuthenticode { get; private set; }
        public Exception? InspectionFailure { get; init; }
        public int InspectionCalls { get; private set; }
        public string GetProtectedHelperDirectory() => HelperDirectory;
        public (byte[] Manifest, byte[] Signature) ReadInstalledManifest(int maximumManifestBytes, int signatureBytes) => throw new NotSupportedException();
        public string ResolveProtectedHelperPath(string relativePath) => ResolvedPath ?? Path.Combine(HelperDirectory, relativePath);
        public ProtectedFileInspection InspectProtectedFile(string expectedPath, bool verifyAuthenticode)
        {
            InspectionCalls++;
            LastVerifyAuthenticode = verifyAuthenticode;
            if (InspectionFailure is not null) throw InspectionFailure;
            return new ProtectedFileInspection(FinalPath ?? expectedPath, ImageSize, ImageSha256, ImageSigner);
        }
    }
}
