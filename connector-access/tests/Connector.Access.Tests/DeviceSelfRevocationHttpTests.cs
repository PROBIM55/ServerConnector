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

public sealed class DeviceSelfRevocationHttpTests
{
    [Fact]
    public async Task MtlsSelfRevokeIsBoundToExactDeviceAndRequestAndWaitsForProviders()
    {
        var provider = new RecordingGrantProvider("vpn");
        await using var fixture = await AccessFixture.CreateAsync([provider], options =>
        {
            options.RequiredAccessProviders.Clear();
            options.RequiredAccessProviders.Add(provider.ProviderName);
            options.ProviderRetryBackoff = TimeSpan.FromSeconds(1);
        });
        using var first = await EnrollDeviceAsync(fixture, "First workstation");
        using var second = await EnrollDeviceAsync(fixture, "Second workstation");
        using var serverCertificate = CreateServerCertificate();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
        {
            https.ServerCertificate = serverCertificate;
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            var certificatePolicy = new ConnectorAccessTlsCertificatePolicy(
                fixture.Issuer.IssuerCertificate, fixture.Clock, new DeviceAccessOptions());
            https.ClientCertificateValidation = (certificate, _, _) => certificatePolicy.Validate(certificate);
        })));
        builder.Services.AddSingleton(fixture.Enrollment);
        builder.Services.AddSingleton(fixture.Authenticator);
        builder.Services.AddSingleton(fixture.Profiles);
        builder.Services.AddSingleton(fixture.Administration);
        builder.Services.AddSingleton(fixture.SelfRevocation);
        builder.Services.AddSingleton<IPlatformConnectorAdminIdentity>(new NoWebAdministrator());
        builder.Services.AddConnectorAccessHttp();
        await using var app = builder.Build();
        app.UseRateLimiter();
        app.MapConnectorAccess();
        await app.StartAsync();

        try
        {
            var address = new Uri(app.Urls.Single());
            using var anonymous = Client(address, serverCertificate, null);
            anonymous.DefaultRequestHeaders.Add("Cookie", "platform_admin=fake");
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(first.Path, null)).StatusCode);

            using var firstClient = Client(address, serverCertificate, first.Certificate);
            using var secondClient = Client(address, serverCertificate, second.Certificate);
            var active = await firstClient.GetAsync(first.Path);
            Assert.Equal(HttpStatusCode.Accepted, active.StatusCode);
            Assert.Equal("active", (await active.Content.ReadFromJsonAsync<DeviceSelfRevocationStatus>())!.State);

            Assert.Equal(HttpStatusCode.Unauthorized, (await firstClient.PostAsync(second.Path, null)).StatusCode);
            var wrongRequestPath = $"/api/platform/connector/access/v1/devices/{first.DeviceId}/enrollments/{Guid.NewGuid():D}/self-revoke";
            Assert.Equal(HttpStatusCode.Unauthorized, (await firstClient.PostAsync(wrongRequestPath, null)).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await secondClient.GetAsync(second.Path)).StatusCode);

            var revoke = await firstClient.PostAsync(first.Path, null);
            Assert.Equal(HttpStatusCode.Accepted, revoke.StatusCode);
            var pending = (await revoke.Content.ReadFromJsonAsync<DeviceSelfRevocationStatus>())!;
            Assert.Equal("pending", pending.State);
            Assert.False(pending.Completed);

            // The ordinary authenticated routes remain closed as soon as the
            // durable device row is revoked, while this exact self-revoke route
            // remains available for idempotent compensation retries.
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await firstClient.GetAsync("/api/platform/connector/access/v1/profile")).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await firstClient.PostAsync(first.Path, null)).StatusCode);

            provider.FailNext = true;
            var failedDispatch = await fixture.Outbox.ProcessOneAsync();
            Assert.NotNull(failedDispatch);
            Assert.False(failedDispatch!.Succeeded);
            Assert.Equal(HttpStatusCode.Accepted, (await firstClient.GetAsync(first.Path)).StatusCode);

            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            var appliedDispatch = await fixture.Outbox.ProcessOneAsync();
            Assert.NotNull(appliedDispatch);
            Assert.True(appliedDispatch!.Succeeded, appliedDispatch.Error);
            Assert.Equal(HttpStatusCode.NoContent, (await firstClient.GetAsync(first.Path)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await firstClient.PostAsync(first.Path, null)).StatusCode);

            var secondStillActive = await secondClient.GetAsync(second.Path);
            Assert.Equal(HttpStatusCode.Accepted, secondStillActive.StatusCode);
            Assert.Equal("active", (await secondStillActive.Content.ReadFromJsonAsync<DeviceSelfRevocationStatus>())!.State);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async ValueTask<EnrolledClient> EnrollDeviceAsync(AccessFixture fixture, string displayName)
    {
        var issued = await fixture.IssueAsync();
        var requestId = Guid.NewGuid();
        var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ignored-client-subject", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var enrollment = await fixture.Enrollment.EnrollAsync(new DeviceEnrollmentRequest(
            DeviceAccessProtocol.Version,
            requestId,
            issued.Token,
            request.CreateSigningRequestPem(),
            displayName));
        Assert.True(enrollment.IsSuccess, enrollment.Failure?.Code);
        var response = Assert.IsType<DeviceEnrollmentResponse>(enrollment.Value);
        using var publicCertificate = X509Certificate2.CreateFromPem(response.ClientCertificatePem);
        using var attachedCertificate = publicCertificate.CopyWithPrivateKey(key);
        var certificate = new X509Certificate2(attachedCertificate.Export(X509ContentType.Pfx));
        key.Dispose();
        return new EnrolledClient(response.DeviceId, requestId, certificate);
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return new X509Certificate2(generated.Export(X509ContentType.Pfx));
    }

    private static HttpClient Client(Uri address, X509Certificate2 server, X509Certificate2? client)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate?.Thumbprint == server.Thumbprint,
            ClientCertificateOptions = ClientCertificateOption.Manual,
        };
        if (client is not null) handler.ClientCertificates.Add(client);
        return new HttpClient(handler) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(15) };
    }

    private sealed record EnrolledClient(
        string DeviceId,
        Guid RequestId,
        X509Certificate2 Certificate) : IDisposable
    {
        public string Path =>
            $"/api/platform/connector/access/v1/devices/{DeviceId}/enrollments/{RequestId:D}/self-revoke";

        public void Dispose() => Certificate.Dispose();
    }

    private sealed class NoWebAdministrator : IPlatformConnectorAdminIdentity
    {
        public ValueTask<string?> GetAuthenticatedWebUserIdAsync(
            HttpContext context, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
    }
}
