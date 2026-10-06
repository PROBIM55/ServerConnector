using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Connector.Access.Smb.Tests;

public sealed class SmbPinnedTlsTests
{
    [Theory]
    [InlineData("correct", true)]
    [InlineData("wrong-pin", false)]
    [InlineData("default-trust", false)]
    [InlineData("wrong-name", false)]
    [InlineData("expired", false)]
    [InlineData("no-server-eku", false)]
    public async Task RealHttps_EnforcesCertificateIdentityNameValidityAndUsage(string scenario, bool accepted)
    {
        var root = Path.Combine(Path.GetTempPath(), "connector-smb-tls-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var passwordVariable = "CONNECTOR_SMB_TLS_FIXTURE_" + Guid.NewGuid().ToString("N");
        const string password = "harmless-test-pfx-password";
        Environment.SetEnvironmentVariable(passwordVariable, password);
        try
        {
            using var server = CreateCertificate(server: true, scenario);
            using var client = CreateCertificate(server: false, "correct");
            var pfx = Path.Combine(root, "client.pfx");
            await File.WriteAllBytesAsync(pfx, client.Export(X509ContentType.Pfx, password));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
                listen => listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = server,
                    ClientCertificateMode = ClientCertificateMode.RequireCertificate,
                    ClientCertificateValidation = (certificate, _, _) => certificate is not null &&
                        CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData),
                            SHA256.HashData(client.RawData)),
                })));
            await using var app = builder.Build();
            app.MapGet("/", () => "ok");
            await app.StartAsync();
            try
            {
                var address = app.Services.GetRequiredService<IServer>().Features
                    .Get<IServerAddressesFeature>()!.Addresses.Single();
                var options = CreateOptions(root, pfx, passwordVariable, new Uri(address),
                    scenario == "default-trust" ? null : scenario == "wrong-pin" ? new string('0', 64) :
                    Convert.ToHexString(SHA256.HashData(server.RawData)));
                using var services = new ServiceCollection().AddLogging().AddSmbConnectorAccess(options).BuildServiceProvider();
                var http = services.GetRequiredService<HttpClient>();
                if (accepted)
                    Assert.Equal("ok", await http.GetStringAsync(address));
                else
                    await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync(address));
            }
            finally { await app.StopAsync(); }
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, null);
            var parent = Directory.GetParent(Path.GetFullPath(root))!.FullName;
            if (!string.Equals(parent, Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup path is invalid.");
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://example.com/", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("https://localhost/", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("https://127.0.0.1/", "invalid")]
    public void PinCannotExpandTrustToRemoteOrDnsOrigins(string origin, string pin)
    {
        var root = Path.GetTempPath();
        var options = CreateOptions(root, Path.Combine(root, "absent.pfx"), "ABSENT_PASSWORD", new Uri(origin), pin);
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddSmbConnectorAccess(options));
    }

    private static SmbProviderOptions CreateOptions(string root, string pfx, string variable, Uri origin, string? pin) => new()
    {
        HelperBaseUri = origin,
        StateDirectory = Path.Combine(root, "state"),
        DataProtectionKeyDirectory = Path.Combine(root, "keys"),
        DataProtectionCertificatePfxPath = pfx,
        DataProtectionCertificatePasswordEnvironmentVariable = variable,
        ClientCertificatePfxPath = pfx,
        ClientCertificatePasswordEnvironmentVariable = variable,
        ServerCertificateSha256 = pin,
        ResourceBindings = [new SmbResourceBinding { ResourceId = "models", HelperResourceId = "company-models",
            ClientShareUnc = @"\\100.108.74.8\BIM_Models_VPN" }],
    };

    private static X509Certificate2 CreateCertificate(bool server, string scenario)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(server ? "CN=SMB helper fixture" : "CN=SMB backend fixture",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        if (scenario != "no-server-eku")
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, false));
        if (server)
        {
            var san = new SubjectAlternativeNameBuilder();
            if (scenario == "wrong-name") san.AddDnsName("incorrect.example.test");
            else san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
        }
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(scenario == "expired" ? now.AddDays(-2) : now.AddMinutes(-5),
            scenario == "expired" ? now.AddDays(-1) : now.AddDays(1));
#pragma warning disable SYSLIB0057
        return new X509Certificate2(certificate.Export(X509ContentType.Pfx), (string?)null,
            (server ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet) |
            X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
    }
}
