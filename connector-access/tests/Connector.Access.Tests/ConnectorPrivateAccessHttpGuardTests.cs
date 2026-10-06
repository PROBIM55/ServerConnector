using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Connector.Access.AspNetCore;
using Connector.Access.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Connector.Access.Tests;

public sealed class ConnectorPrivateAccessHttpGuardTests
{
    [Fact]
    public async Task RequiresHttpsBeforeReadingOrAuthenticatingCertificate()
    {
        var context = new DefaultHttpContext();
        context.RequestServices = CreateServices();
        var authenticationCalled = false;

        var result = await ConnectorPrivateAccessHttpGuard.AuthenticateDeviceAsync(context, (_, _) =>
        {
            authenticationCalled = true;
            return ValueTask.FromResult(DeviceAccessResult<AuthenticatedDevice>.Fail("unexpected", "unexpected"));
        });

        Assert.Null(result.Identity);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.False(authenticationCalled);
        var (status, error) = await ReadErrorAsync(result.Error);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("https_required", error);
    }

    [Fact]
    public async Task RejectsMissingCertificateWithoutCallingAuthenticator()
    {
        var context = new DefaultHttpContext();
        context.RequestServices = CreateServices();
        context.Request.Scheme = "https";
        var authenticationCalled = false;

        var result = await ConnectorPrivateAccessHttpGuard.AuthenticateDeviceAsync(context, (_, _) =>
        {
            authenticationCalled = true;
            return ValueTask.FromResult(DeviceAccessResult<AuthenticatedDevice>.Fail("unexpected", "unexpected"));
        });

        Assert.Null(result.Identity);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.False(authenticationCalled);
        var (status, error) = await ReadErrorAsync(result.Error);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Equal("device_unauthorized", error);
    }

    [Fact]
    public async Task AuthenticatesPresentedCertificateAndReturnsDeviceIdentity()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=device", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.RequestServices = CreateServices();
        context.Features.Set<ITlsConnectionFeature>(new FixtureTlsConnectionFeature(certificate));
        var identity = new AuthenticatedDevice("device-1", "user-1", "company-1", new string('a', 64), 7);
        X509Certificate2? authenticatedCertificate = null;

        var result = await ConnectorPrivateAccessHttpGuard.AuthenticateDeviceAsync(context, (presented, _) =>
        {
            authenticatedCertificate = presented;
            return ValueTask.FromResult(DeviceAccessResult<AuthenticatedDevice>.Success(identity));
        });

        Assert.Same(identity, result.Identity);
        Assert.Null(result.Error);
        Assert.Same(certificate, authenticatedCertificate);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task MapsRejectedCertificateToUnauthorized()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=unregistered-device", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.RequestServices = CreateServices();
        context.Features.Set<ITlsConnectionFeature>(new FixtureTlsConnectionFeature(certificate));

        var result = await ConnectorPrivateAccessHttpGuard.AuthenticateDeviceAsync(context, (_, _) =>
            ValueTask.FromResult(DeviceAccessResult<AuthenticatedDevice>.Fail("certificate_not_registered", "rejected")));

        Assert.Null(result.Identity);
        var (status, error) = await ReadErrorAsync(result.Error);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Equal("device_unauthorized", error);
    }

    private static async Task<(int Status, string? Error)> ReadErrorAsync(IResult? result)
    {
        Assert.NotNull(result);
        var context = new DefaultHttpContext();
        context.RequestServices = CreateServices();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        context.Response.Body.Position = 0;
        using var document = await System.Text.Json.JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, document.RootElement.GetProperty("error").GetString());
    }

    private static IServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureHttpJsonOptions(_ => { });
        return services.BuildServiceProvider();
    }

    private sealed class FixtureTlsConnectionFeature(X509Certificate2 certificate) : ITlsConnectionFeature
    {
        public X509Certificate2? ClientCertificate { get; set; } = certificate;
        public Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken) => Task.FromResult(ClientCertificate);
    }
}
