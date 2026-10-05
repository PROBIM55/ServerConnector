using System.Data.Common;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Connector.Access;
using Connector.Access.Contracts;
using Connector.Access.NetBird;
using Connector.Access.NetBird.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Connector.Access.NetBird.AspNetCore.Tests;

internal sealed class AccessTestFixture : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _databasePath;
    private readonly TestCertificateIssuer _issuer;

    private AccessTestFixture(ServiceProvider services, string databasePath, TestCertificateIssuer issuer)
    {
        _services = services;
        _databasePath = databasePath;
        _issuer = issuer;
        Authenticator = services.GetRequiredService<DeviceCertificateAuthenticator>();
        Enrollment = services.GetRequiredService<DeviceEnrollmentService>();
    }

    public DeviceCertificateAuthenticator Authenticator { get; }
    private DeviceEnrollmentService Enrollment { get; }

    public static async ValueTask<AccessTestFixture> CreateAsync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"netbird-attestation-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 10,
            ForeignKeys = true,
            Pooling = false
        }.ToString();

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            foreach (var migration in Directory.GetFiles(
                         Path.Combine(AppContext.BaseDirectory, "migrations"),
                         "*.sql").Order())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = await File.ReadAllTextAsync(migration);
                await command.ExecuteNonQueryAsync();
            }
        }

        var issuer = new TestCertificateIssuer();
        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IPlatformAccessDirectory, TestPlatformDirectory>()
            .AddSingleton<IX509DeviceCertificateIssuer>(issuer)
            .AddSingleton<IDeviceAccessDbConnectionFactory>(new SqliteConnectionFactory(connectionString))
            .AddConnectorAccessCore()
            .BuildServiceProvider(validateScopes: false);

        return new AccessTestFixture(services, databasePath, issuer);
    }

    public async ValueTask<EnrolledDevice> EnrollDeviceAsync()
    {
        var issued = await Enrollment.IssueTokenAsync(
            new IssueEnrollmentTokenCommand("admin-1", "user-1", "company-1"));
        Assert.True(issued.IsSuccess, issued.Failure?.Code);

        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest(
            "CN=untrusted-client-value",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var enrolled = await Enrollment.EnrollAsync(new DeviceEnrollmentRequest(
            DeviceAccessProtocol.Version,
            Guid.NewGuid(),
            Assert.IsType<EnrollmentTokenIssue>(issued.Value).Token,
            certificateRequest.CreateSigningRequestPem(),
            "Attestation fixture"));
        Assert.True(enrolled.IsSuccess, enrolled.Failure?.Code);
        var response = Assert.IsType<DeviceEnrollmentResponse>(enrolled.Value);

        using var publicCertificate = X509Certificate2.CreateFromPem(response.ClientCertificatePem);
        using var withKey = publicCertificate.CopyWithPrivateKey(key);
        var certificate = new X509Certificate2(withKey.Export(X509ContentType.Pfx));
        return new EnrolledDevice(response, certificate);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _issuer.Dispose();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }
}

internal sealed record EnrolledDevice(DeviceEnrollmentResponse Enrollment, X509Certificate2 Certificate) : IDisposable
{
    public void Dispose() => Certificate.Dispose();
}

internal sealed class AttestationServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly X509Certificate2? _serverCertificate;

    private AttestationServer(WebApplication app, X509Certificate2? serverCertificate)
    {
        _app = app;
        _serverCertificate = serverCertificate;
        Address = new Uri(app.Urls.Single());
    }

    public Uri Address { get; }

    public static async ValueTask<AttestationServer> StartAsync(
        DeviceCertificateAuthenticator authenticator,
        INetBirdPeerAttestationService service,
        bool https = true)
    {
        var serverCertificate = https ? CreateServerCertificate() : null;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen =>
            {
                if (!https) return;
                listen.UseHttps(settings =>
                {
                    settings.ServerCertificate = serverCertificate;
                    settings.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                    // Let the application authenticator make the registry and
                    // device-policy decision so invalid-certificate HTTP behavior is observable.
                    settings.ClientCertificateValidation = (_, _, _) => true;
                });
            }));
        builder.Services.AddSingleton(authenticator);
        builder.Services.AddSingleton(service);

        var app = builder.Build();
        app.MapNetBirdPeerAttestation();
        await app.StartAsync();
        return new AttestationServer(app, serverCertificate);
    }

    public HttpClient CreateClient(X509Certificate2? clientCertificate = null)
    {
        var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual
        };
        if (_serverCertificate is not null)
        {
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate?.Thumbprint == _serverCertificate.Thumbprint;
        }
        if (clientCertificate is not null)
            handler.ClientCertificates.Add(clientCertificate);
        return new HttpClient(handler)
        {
            BaseAddress = Address,
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _serverCertificate?.Dispose();
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        return new X509Certificate2(generated.Export(X509ContentType.Pfx));
    }
}

internal sealed class RecordingAttestationService : INetBirdPeerAttestationService
{
    private readonly Func<NetBirdPeerAttestationRequest, NetBirdPeerAttestationResult> _handler;

    public RecordingAttestationService(Func<NetBirdPeerAttestationRequest, NetBirdPeerAttestationResult> handler) =>
        _handler = handler;

    public List<NetBirdPeerAttestationRequest> Requests { get; } = [];

    public ValueTask<NetBirdPeerAttestationResult> AttestAsync(
        NetBirdPeerAttestationRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return ValueTask.FromResult(_handler(request));
    }
}

internal sealed class SqliteConnectionFactory : IDeviceAccessDbConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string connectionString) => _connectionString = connectionString;

    public async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}

internal sealed class TestPlatformDirectory : IPlatformAccessDirectory
{
    public ValueTask<PlatformAccessSnapshot?> AuthorizeAsync(
        string actorUserId,
        string subjectUserId,
        string companyId,
        PlatformAccessOperation operation,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<PlatformAccessSnapshot?>(new PlatformAccessSnapshot(
            subjectUserId,
            companyId,
            true,
            true,
            true,
            true,
            7,
            [],
            []));
}

internal sealed class TestCertificateIssuer : IX509DeviceCertificateIssuer, IDisposable
{
    private readonly RSA _key = RSA.Create(2048);
    private readonly X509Certificate2 _certificateWithKey;

    public TestCertificateIssuer()
    {
        var request = new CertificateRequest(
            "CN=Connector Access Attestation Fixture CA",
            _key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        _certificateWithKey = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        IssuerCertificate = new X509Certificate2(_certificateWithKey.Export(X509ContentType.Cert));
    }

    public X509Certificate2 IssuerCertificate { get; }

    public ValueTask<X509Certificate2> IssueAsync(
        CertificateRequest certificateRequest,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(certificateRequest.Create(
            _certificateWithKey,
            notBefore,
            notAfter,
            RandomNumberGenerator.GetBytes(16)));

    public void Dispose()
    {
        IssuerCertificate.Dispose();
        _certificateWithKey.Dispose();
        _key.Dispose();
    }
}
