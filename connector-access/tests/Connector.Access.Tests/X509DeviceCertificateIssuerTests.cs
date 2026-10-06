using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Connector.Access.Tests;

public sealed class X509DeviceCertificateIssuerTests
{
    [Fact]
    public async Task Load_ValidCaIssuer_CreatesDeviceCertificateChainedToIssuer()
    {
        using var temp = new TemporaryDirectory();
        var issuerPath = Path.Combine(temp.Path, "issuer.pfx");
        using (var certificateAuthority = CreateCertificateAuthority())
            File.WriteAllBytes(issuerPath, certificateAuthority.Export(X509ContentType.Pfx));

        using var issuer = X509DeviceCertificateIssuer.Load(issuerPath);
        using var deviceKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=device-01", deviceKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;

        using var deviceCertificate = await issuer.IssueAsync(
            request,
            now.AddMinutes(-1),
            now.AddHours(1),
            CancellationToken.None);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.CustomTrustStore.Add(issuer.IssuerCertificate);

        Assert.Equal(issuer.IssuerCertificate.SubjectName.Name, deviceCertificate.IssuerName.Name);
        Assert.True(chain.Build(deviceCertificate), string.Join("; ", chain.ChainStatus.Select(status => status.StatusInformation)));
    }

    [Fact]
    public void Load_RejectsCertificateWithoutPrivateKey()
    {
        using var temp = new TemporaryDirectory();
        var issuerPath = Path.Combine(temp.Path, "public-only.pfx");
        using var certificateAuthority = CreateCertificateAuthority();
        using var publicOnly = new X509Certificate2(certificateAuthority.Export(X509ContentType.Cert));
        File.WriteAllBytes(issuerPath, publicOnly.Export(X509ContentType.Pfx));

        var exception = Assert.Throws<InvalidOperationException>(() => X509DeviceCertificateIssuer.Load(issuerPath));

        Assert.Contains("private key", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_RejectsCertificateThatIsNotACa()
    {
        using var temp = new TemporaryDirectory();
        var issuerPath = Path.Combine(temp.Path, "leaf.pfx");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=not-a-ca", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllBytes(issuerPath, certificate.Export(X509ContentType.Pfx));

        var exception = Assert.Throws<InvalidOperationException>(() => X509DeviceCertificateIssuer.Load(issuerPath));

        Assert.Contains("not a CA", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_ReportsMissingPasswordEnvironmentVariable()
    {
        using var temp = new TemporaryDirectory();
        var issuerPath = Path.Combine(temp.Path, "issuer.pfx");
        using (var issuer = CreateCertificateAuthority())
            File.WriteAllBytes(issuerPath, issuer.Export(X509ContentType.Pfx));

        var variableName = $"CONNECTOR_ACCESS_TEST_MISSING_{Guid.NewGuid():N}";
        var exception = Assert.Throws<InvalidOperationException>(() =>
            X509DeviceCertificateIssuer.Load(issuerPath, variableName));

        Assert.Contains("environment variable is missing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_AcceptsPasswordProtectedIssuer()
    {
        using var temp = new TemporaryDirectory();
        var issuerPath = Path.Combine(temp.Path, "issuer.pfx");
        const string password = "issuer-test-password";
        using (var issuer = CreateCertificateAuthority())
            File.WriteAllBytes(issuerPath, issuer.Export(X509ContentType.Pfx, password));

        var variableName = $"CONNECTOR_ACCESS_TEST_PASSWORD_{Guid.NewGuid():N}";
        var previous = Environment.GetEnvironmentVariable(variableName);
        try
        {
            Environment.SetEnvironmentVariable(variableName, password);
            using var loaded = X509DeviceCertificateIssuer.Load(issuerPath, variableName);
            Assert.True(loaded.IssuerCertificate.HasPrivateKey is false);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previous);
        }
    }

    [Theory]
    [InlineData(-30, -1)]
    [InlineData(1, 30)]
    public void Load_RejectsIssuerOutsideValidityPeriod(int notBeforeDays, int notAfterDays)
    {
        using var temp = new TemporaryDirectory();
        var issuerPath = Path.Combine(temp.Path, "issuer.pfx");
        using (var issuer = CreateCertificateAuthority(DateTimeOffset.UtcNow.AddDays(notBeforeDays), DateTimeOffset.UtcNow.AddDays(notAfterDays)))
            File.WriteAllBytes(issuerPath, issuer.Export(X509ContentType.Pfx));

        var exception = Assert.Throws<InvalidOperationException>(() => X509DeviceCertificateIssuer.Load(issuerPath));

        Assert.Contains("validity period", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_RejectsCaWithoutCertificateSigningUsage()
    {
        using var temp = new TemporaryDirectory();
        var issuerPath = Path.Combine(temp.Path, "issuer.pfx");
        using (var issuer = CreateCertificateAuthority(includeCertificateSigning: false))
            File.WriteAllBytes(issuerPath, issuer.Export(X509ContentType.Pfx));

        var exception = Assert.Throws<InvalidOperationException>(() => X509DeviceCertificateIssuer.Load(issuerPath));

        Assert.Contains("certificate signing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static X509Certificate2 CreateCertificateAuthority(
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        bool includeCertificateSigning = true)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Connector Access Test Issuer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            includeCertificateSigning ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign : X509KeyUsageFlags.CrlSign,
            critical: true));
        return request.CreateSelfSigned(notBefore ?? DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddDays(30));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"connector-access-issuer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
