using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Connector.Access.Smb.Helper;

namespace Connector.Access.Smb.Tests;

public sealed class SmbHelperBackendCertificateValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("valid", true)]
    [InlineData("expired", false)]
    [InlineData("future", false)]
    [InlineData("server-eku", false)]
    [InlineData("missing-eku", false)]
    public void CertificateMustBeCurrentAndHaveClientAuthenticationEku(string scenario, bool expected)
    {
        using var certificate = CreateCertificate(scenario);
        var pin = SHA256.HashData(certificate.RawData);

        Assert.Equal(expected, SmbBackendCertificateValidator.IsAllowed(certificate, [pin], Now));
    }

    [Fact]
    public void CertificateMustMatchConfiguredFixedLengthSha256Pin()
    {
        using var certificate = CreateCertificate("valid");
        var wrongPin = new byte[32];
        Assert.False(SmbBackendCertificateValidator.IsAllowed(certificate, [wrongPin], Now));
        Assert.False(SmbBackendCertificateValidator.IsAllowed(certificate, [new byte[31]], Now));
    }

    private static X509Certificate2 CreateCertificate(string scenario)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SMB backend test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var ekuOid = scenario == "server-eku" ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2";
        if (scenario != "missing-eku")
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(ekuOid) }, false));
        var notBefore = scenario == "expired" ? Now.AddDays(-3) : scenario == "future" ? Now.AddDays(1) : Now.AddDays(-1);
        var notAfter = scenario == "expired" ? Now.AddDays(-2) : Now.AddDays(3);
        return request.CreateSelfSigned(notBefore, notAfter);
    }
}
