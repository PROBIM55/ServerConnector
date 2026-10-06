using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Connector.Access.AspNetCore;
using Connector.Access.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Connector.Access.Tests;

public sealed class DeviceAccessHttpTests
{
    [Fact]
    public async Task ActualHttpsEnrollmentAndRecoveryRequireTlsPrivateKeyAndCurrentAccess()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        using var serverKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var generatedServerCertificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // Schannel requires a reimported key handle on Windows. This fixture
        // does not install a certificate or add trust to a Windows store.
        using var serverCertificate = new X509Certificate2(generatedServerCertificate.Export(X509ContentType.Pfx));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
        {
            https.ServerCertificate = serverCertificate;
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            // Exercise the same restricted policy supplied to the real host;
            // neither issuer nor fixture server cert is installed in Windows.
            var certificatePolicy = new ConnectorAccessTlsCertificatePolicy(
                fixture.Issuer.IssuerCertificate, fixture.Clock, new DeviceAccessOptions());
            https.ClientCertificateValidation = (certificate, _, _) => certificatePolicy.Validate(certificate);
        })));
        builder.Services.AddSingleton(fixture.Enrollment);
        builder.Services.AddSingleton(fixture.Authenticator);
        builder.Services.AddSingleton(fixture.Profiles);
        builder.Services.AddSingleton(fixture.Administration);
        builder.Services.AddSingleton<IPlatformConnectorAdminIdentity>(new NoWebAdministrator());
        builder.Services.AddSingleton<IConnectorVpnBootstrapReader>(new FixtureVpnReader());
        builder.Services.AddConnectorAccessHttp();
        await using var app = builder.Build();
        app.UseRateLimiter();
        app.MapConnectorAccess();
        await app.StartAsync();
        try
        {
            var address = new Uri(app.Urls.Single());
            using var anonymous = Client(address, serverCertificate, null);
            var issued = await fixture.IssueAsync();
            using var deviceKey = RSA.Create(2048);
            var deviceRequest = new CertificateRequest("CN=ignored-client-subject", deviceKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var enrollment = new DeviceEnrollmentRequest(1, Guid.NewGuid(), issued.Token,
                deviceRequest.CreateSigningRequestPem(), "Fixture computer");
            var result = await anonymous.PostAsJsonAsync("/api/platform/connector/access/v1/enroll", enrollment);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            var enrolled = (await result.Content.ReadFromJsonAsync<DeviceEnrollmentResponse>())!;
            var replay = await anonymous.PostAsJsonAsync("/api/platform/connector/access/v1/enroll", enrollment);
            Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
            var recovery = $"/api/platform/connector/access/v1/enrollments/{enrollment.RequestId}";
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(recovery)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/platform/connector/access/v1/vpn/state")).StatusCode);
            // Simulate losing the enrollment response: the client knows only
            // requestId and its CSR key, and has not received the issued cert.
            var proofRequest = new CertificateRequest("CN=enrollment-recovery", deviceKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            proofRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            proofRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            proofRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(DeviceAccessProtocol.ClientAuthenticationOid) }, true));
            using var generatedProof = proofRequest.CreateSelfSigned(fixture.Clock.GetUtcNow().AddMinutes(-1), fixture.Clock.GetUtcNow().AddMinutes(5));
            using var proofCertificate = new X509Certificate2(generatedProof.Export(X509ContentType.Pfx));
            using var proofClient = Client(address, serverCertificate, proofCertificate);
            Assert.Equal(HttpStatusCode.Unauthorized, (await proofClient.GetAsync(recovery)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await proofClient.GetAsync("/api/platform/connector/access/v1/profile")).StatusCode);
            var resumed = await proofClient.PostAsync(recovery + "/resume", null);
            Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
            var resumedEnrollment = (await resumed.Content.ReadFromJsonAsync<DeviceEnrollmentResponse>())!;
            Assert.Equal(enrolled.ClientCertificatePem, resumedEnrollment.ClientCertificatePem);
            Assert.Equal(1, fixture.Issuer.IssueCount);
            Assert.Equal(1, await fixture.CountDevicesAsync());
            anonymous.DefaultRequestHeaders.Add("X-Client-Cert", enrolled.ClientCertificatePem.ReplaceLineEndings(""));
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(recovery)).StatusCode);
            using var publicCertificate = X509Certificate2.CreateFromPem(enrolled.ClientCertificatePem);
            using var attachedDeviceCertificate = publicCertificate.CopyWithPrivateKey(deviceKey);
            using var deviceCertificate = new X509Certificate2(attachedDeviceCertificate.Export(X509ContentType.Pfx));
            using var deviceClient = Client(address, serverCertificate, deviceCertificate);
            var recovered = await deviceClient.GetAsync(recovery);
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            Assert.Equal(enrolled.DeviceId, (await recovered.Content.ReadFromJsonAsync<DeviceEnrollmentResponse>())!.DeviceId);
            Assert.Equal(HttpStatusCode.Forbidden, (await deviceClient.GetAsync("/api/platform/connector/access/v1/profile")).StatusCode);
            var vpn = await deviceClient.GetAsync("/api/platform/connector/access/v1/vpn/state");
            Assert.Equal(HttpStatusCode.OK, vpn.StatusCode);
            Assert.Equal("no-store", vpn.Headers.CacheControl?.ToString());
            Assert.Equal(enrolled.DeviceId, (await vpn.Content.ReadFromJsonAsync<ConnectorVpnTransportState>())!.DeviceId);
            var revoked = await fixture.Administration.RevokeAsync(new RevokeDeviceCommand("admin-1", "company-1", enrolled.DeviceId));
            Assert.True(revoked.IsSuccess, revoked.Failure?.Code);
            Assert.Equal(HttpStatusCode.Unauthorized, (await deviceClient.GetAsync(recovery)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await deviceClient.GetAsync("/api/platform/connector/access/v1/vpn/state")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await proofClient.PostAsync(recovery + "/resume", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostAsJsonAsync(
                "/api/platform/admin/connector/access/v1/enrollment-tokens", new IssueTokenRequest("user-1", "company-1"))).StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    private static HttpClient Client(Uri address, X509Certificate2 server, X509Certificate2? client)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate?.Thumbprint == server.Thumbprint,
            ClientCertificateOptions = ClientCertificateOption.Manual
        };
        if (client is not null) handler.ClientCertificates.Add(client);
        return new HttpClient(handler) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(15) };
    }

    private sealed class FixtureVpnReader : IConnectorVpnBootstrapReader
    {
        public ValueTask<DeviceAccessResult<ConnectorVpnBootstrap>> GetAsync(AuthenticatedDevice device, CancellationToken cancellationToken)
            => ValueTask.FromResult(DeviceAccessResult<ConnectorVpnBootstrap>.Fail("unavailable", "Fixture state only."));

        public ValueTask<DeviceAccessResult<ConnectorVpnTransportState>> GetStateAsync(AuthenticatedDevice device, CancellationToken cancellationToken)
            => ValueTask.FromResult(DeviceAccessResult<ConnectorVpnTransportState>.Success(new(
                device.DeviceId, device.AccessRevision, "connecting", null, "https://vpn.example.test", [], DateTimeOffset.UtcNow)));
    }

    private sealed class NoWebAdministrator : IPlatformConnectorAdminIdentity
    {
        public ValueTask<string?> GetAuthenticatedWebUserIdAsync(HttpContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>(null);
    }
}
