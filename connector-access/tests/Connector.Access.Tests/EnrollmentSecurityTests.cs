using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Connector.Access.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Connector.Access.Tests;

public sealed class EnrollmentSecurityTests
{
    [Fact]
    public async Task Expired_token_is_rejected_without_consuming_another_identity()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var issue = await fixture.IssueAsync(TimeSpan.FromSeconds(30));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        var result = await fixture.EnrollAsync(issue.Token, CreateCsr());

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_token", result.Failure!.Code);
        Assert.Equal(0, await fixture.CountDevicesAsync());
    }

    [Fact]
    public async Task Used_token_cannot_recover_or_create_a_second_device()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var issue = await fixture.IssueAsync();
        var csr = CreateCsr();
        var first = await fixture.EnrollAsync(issue.Token, csr);

        var replay = await fixture.EnrollAsync(issue.Token, csr);

        Assert.True(first.IsSuccess, Describe(first));
        Assert.False(replay.IsSuccess);
        Assert.Equal("invalid_token", replay.Failure!.Code);
        Assert.Equal(1, await fixture.CountDevicesAsync());
    }

    [Fact]
    public async Task Concurrent_token_consumers_create_exactly_one_device()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var issue = await fixture.IssueAsync();
        var firstRequest = fixture.EnrollAsync(issue.Token, CreateCsr());
        var secondRequest = fixture.EnrollAsync(issue.Token, CreateCsr());

        var results = await Task.WhenAll(firstRequest.AsTask(), secondRequest.AsTask());

        var resultSummary = string.Join("; ", results.Select(Describe));
        Assert.True(results.Count(result => result.IsSuccess) == 1, resultSummary);
        Assert.True(results.Count(result => !result.IsSuccess) == 1, resultSummary);
        Assert.Equal(1, await fixture.CountDevicesAsync());
    }

    [Fact]
    public async Task Directory_tenant_mismatch_fails_closed_on_issue_and_authentication()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        fixture.Directory.ReturnedCompanyId = "company-other";

        var issue = await fixture.Enrollment.IssueTokenAsync(
            new IssueEnrollmentTokenCommand("admin-1", "user-1", "company-1"));

        Assert.False(issue.IsSuccess);
        Assert.Equal("access_denied", issue.Failure!.Code);

        fixture.Directory.ReturnedCompanyId = null;
        var validIssue = await fixture.IssueAsync();
        var enrolled = await fixture.EnrollAsync(validIssue.Token, CreateCsr());
        fixture.Directory.ReturnedCompanyId = "company-other";
        using var certificate = X509Certificate2.CreateFromPem(enrolled.Value!.ClientCertificatePem);

        var authentication = await fixture.Authenticator.AuthenticateAsync(certificate);

        Assert.False(authentication.IsSuccess);
        Assert.Equal("access_denied", authentication.Failure!.Code);
    }

    [Fact]
    public async Task Inactive_membership_blocks_enrollment_without_consuming_token()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var issue = await fixture.IssueAsync();
        fixture.Directory.MembershipIsActive = false;

        var blocked = await fixture.EnrollAsync(issue.Token, CreateCsr());
        fixture.Directory.MembershipIsActive = true;
        var accepted = await fixture.EnrollAsync(issue.Token, CreateCsr());

        Assert.False(blocked.IsSuccess);
        Assert.Equal("access_denied", blocked.Failure!.Code);
        Assert.True(accepted.IsSuccess, Describe(accepted));
    }

    [Fact]
    public async Task Forged_csr_is_rejected_and_does_not_consume_token()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var issue = await fixture.IssueAsync();
        var validCsr = CreateCsr();
        var forgedCsr = CorruptSignature(validCsr);

        var forged = await fixture.EnrollAsync(issue.Token, forgedCsr);
        var accepted = await fixture.EnrollAsync(issue.Token, validCsr);

        Assert.False(forged.IsSuccess);
        Assert.Equal("invalid_csr", forged.Failure!.Code);
        Assert.True(accepted.IsSuccess, Describe(accepted));
    }

    [Fact]
    public async Task Revoked_registered_certificate_fails_authentication()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var issue = await fixture.IssueAsync();
        var enrolled = await fixture.EnrollAsync(issue.Token, CreateCsr());
        using var certificate = X509Certificate2.CreateFromPem(enrolled.Value!.ClientCertificatePem);

        var before = await fixture.Authenticator.AuthenticateAsync(certificate);
        var revoked = await fixture.Administration.RevokeAsync(
            new RevokeDeviceCommand("admin-1", "company-1", enrolled.Value.DeviceId));
        var after = await fixture.Authenticator.AuthenticateAsync(certificate);

        Assert.True(before.IsSuccess, Describe(before));
        Assert.True(revoked.IsSuccess, Describe(revoked));
        Assert.False(after.IsSuccess);
        Assert.Equal("certificate_not_registered", after.Failure!.Code);
    }

    [Fact]
    public async Task Grants_are_not_returned_until_every_required_provider_is_applied()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var issue = await fixture.IssueAsync();
        var enrolled = await fixture.EnrollAsync(issue.Token, CreateCsr());
        using var certificate = X509Certificate2.CreateFromPem(enrolled.Value!.ClientCertificatePem);
        var authentication = await fixture.Authenticator.AuthenticateAsync(certificate);

        var profile = await fixture.Profiles.GetProfileAsync(authentication.Value!);

        Assert.False(profile.IsSuccess);
        Assert.Equal("access_pending", profile.Failure!.Code);
        Assert.Null(profile.Value);
    }

    [Fact]
    public async Task Transient_issuer_failure_resumes_with_same_csr_key_and_device()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var issue = await fixture.IssueAsync();
        using var deviceKey = RSA.Create(2048);
        var csrRequest = new CertificateRequest("CN=ignored", deviceKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var requestId = Guid.NewGuid();
        fixture.Issuer.FailNextIssue = true;

        var failed = await fixture.Enrollment.EnrollAsync(new DeviceEnrollmentRequest(
            DeviceAccessProtocol.Version, requestId, issue.Token, csrRequest.CreateSigningRequestPem(), "Resume device"));
        using var proofCertificate = CreateEnrollmentProof(deviceKey, fixture.Clock.GetUtcNow());
        var proof = await fixture.Enrollment.AuthenticateEnrollmentKeyAsync(proofCertificate, requestId);
        Assert.True(proof.IsSuccess, Describe(proof));
        var resumed = await fixture.Enrollment.RecoverOrResumeAsync(proof.Value!);

        Assert.False(failed.IsSuccess);
        Assert.Equal("certificate_issue_failed", failed.Failure!.Code);
        Assert.True(resumed.IsSuccess, Describe(resumed));
        Assert.Equal(proof.Value!.DeviceId, resumed.Value!.DeviceId);
        Assert.Equal(2, fixture.Issuer.IssueCount);
        Assert.Equal(1, await fixture.CountDevicesAsync());
    }

    [Fact]
    public async Task Lost_success_response_is_recovered_only_with_same_enrollment_key()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        var issue = await fixture.IssueAsync();
        using var deviceKey = RSA.Create(2048);
        var csrRequest = new CertificateRequest("CN=ignored", deviceKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var requestId = Guid.NewGuid();
        var enrolled = await fixture.Enrollment.EnrollAsync(new DeviceEnrollmentRequest(
            DeviceAccessProtocol.Version, requestId, issue.Token, csrRequest.CreateSigningRequestPem(), "Lost response"));
        Assert.True(enrolled.IsSuccess, Describe(enrolled));

        using var proofCertificate = CreateEnrollmentProof(deviceKey, fixture.Clock.GetUtcNow());
        var proof = await fixture.Enrollment.AuthenticateEnrollmentKeyAsync(proofCertificate, requestId);
        Assert.True(proof.IsSuccess, Describe(proof));
        var recovered = await fixture.Enrollment.RecoverOrResumeAsync(proof.Value!);

        Assert.True(recovered.IsSuccess, Describe(recovered));
        Assert.Equal(enrolled.Value!.DeviceId, recovered.Value!.DeviceId);
        Assert.Equal(enrolled.Value.ClientCertificatePem, recovered.Value.ClientCertificatePem);
        Assert.Equal(1, fixture.Issuer.IssueCount);
    }

    [Fact]
    public async Task Revocation_blocks_auth_before_unavailable_provider_retry()
    {
        await using var fixture = await AccessFixture.CreateAsync(
            configure: options =>
            {
                options.RequiredAccessProviders.Clear();
                options.RequiredAccessProviders.Add("missing");
            });
        var issue = await fixture.IssueAsync();
        var enrolled = await fixture.EnrollAsync(issue.Token, CreateCsr());
        using var certificate = X509Certificate2.CreateFromPem(enrolled.Value!.ClientCertificatePem);

        var revoked = await fixture.Administration.RevokeAsync(
            new RevokeDeviceCommand("admin-1", "company-1", enrolled.Value.DeviceId));
        var authentication = await fixture.Authenticator.AuthenticateAsync(certificate);
        var firstDispatch = await fixture.Outbox.ProcessOneAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        var retryDispatch = await fixture.Outbox.ProcessOneAsync();
        Assert.NotNull(firstDispatch);
        Assert.NotNull(retryDispatch);
        var durable = await fixture.ReadOutboxAsync(firstDispatch!.CommandId);

        Assert.True(revoked.IsSuccess, Describe(revoked));
        Assert.False(authentication.IsSuccess);
        Assert.Equal("certificate_not_registered", authentication.Failure!.Code);
        Assert.False(firstDispatch!.Succeeded);
        Assert.Equal("provider_not_registered", firstDispatch.Error);
        Assert.Equal(firstDispatch.CommandId, retryDispatch!.CommandId);
        Assert.False(retryDispatch.Succeeded);
        Assert.Equal("error", durable.Status);
        Assert.Equal("provider_not_registered", durable.Error);
        Assert.Equal(2, durable.AttemptCount);
    }

    [Fact]
    public async Task Claimed_apply_is_superseded_by_revoke_before_provider_call()
    {
        var provider = new RecordingGrantProvider("recording");
        await using var fixture = await AccessFixture.CreateAsync(
            new[] { provider },
            options =>
            {
                options.RequiredAccessProviders.Clear();
                options.RequiredAccessProviders.Add(provider.ProviderName);
            });
        var issue = await fixture.IssueAsync();
        var enrolled = await fixture.EnrollAsync(issue.Token, CreateCsr());
        using var certificate = X509Certificate2.CreateFromPem(enrolled.Value!.ClientCertificatePem);
        var authentication = await fixture.Authenticator.AuthenticateAsync(certificate);
        var profile = await fixture.Profiles.GetProfileAsync(authentication.Value!);
        Assert.Equal("access_pending", profile.Failure!.Code);

        var claimedApply = await fixture.ProviderOutbox.TryClaimNextAsync(
            "delayed-apply-lease",
            fixture.Clock.GetUtcNow(),
            fixture.Clock.GetUtcNow().AddMinutes(2),
            default);
        Assert.NotNull(claimedApply);
        Assert.Equal(DeviceAccessProviderCommandKind.Apply, claimedApply!.Kind);
        var revoked = await fixture.Administration.RevokeAsync(
            new RevokeDeviceCommand("admin-1", "company-1", enrolled.Value.DeviceId));
        var staleResult = await fixture.Outbox.DispatchClaimedAsync(claimedApply);
        var revokeResult = await fixture.Outbox.ProcessOneAsync();

        Assert.True(revoked.IsSuccess, Describe(revoked));
        Assert.False(staleResult.Succeeded);
        Assert.Equal("superseded", staleResult.Error);
        Assert.Single(provider.Commands);
        Assert.Equal(DeviceAccessProviderCommandKind.Revoke, provider.Commands[0].Kind);
        Assert.True(revokeResult!.Succeeded, revokeResult.Error);
        Assert.True(provider.Commands[0].DesiredRevision > claimedApply.DesiredRevision);
    }

    [Fact]
    public async Task Failed_command_backoff_allows_another_provider_to_run()
    {
        var alpha = new RecordingGrantProvider("alpha") { FailNext = true };
        var beta = new RecordingGrantProvider("beta") { FailNext = true };
        await using var fixture = await AccessFixture.CreateAsync(
            new[] { alpha, beta },
            options =>
            {
                options.RequiredAccessProviders.Clear();
                options.RequiredAccessProviders.Add(alpha.ProviderName);
                options.RequiredAccessProviders.Add(beta.ProviderName);
            });
        var issue = await fixture.IssueAsync();
        var enrolled = await fixture.EnrollAsync(issue.Token, CreateCsr());
        using var certificate = X509Certificate2.CreateFromPem(enrolled.Value!.ClientCertificatePem);
        var authentication = await fixture.Authenticator.AuthenticateAsync(certificate);
        await fixture.Profiles.GetProfileAsync(authentication.Value!);

        var first = await fixture.Outbox.ProcessOneAsync();
        var second = await fixture.Outbox.ProcessOneAsync();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(first!.Succeeded);
        Assert.False(second!.Succeeded);
        Assert.NotEqual(first.CommandId, second.CommandId);
    }

    [Fact]
    public async Task Pending_apply_is_filtered_after_revoke_becomes_desired()
    {
        var provider = new RecordingGrantProvider("recording");
        await using var fixture = await AccessFixture.CreateAsync(
            new[] { provider },
            options =>
            {
                options.RequiredAccessProviders.Clear();
                options.RequiredAccessProviders.Add(provider.ProviderName);
            });
        var issue = await fixture.IssueAsync();
        var enrolled = await fixture.EnrollAsync(issue.Token, CreateCsr());
        using var certificate = X509Certificate2.CreateFromPem(enrolled.Value!.ClientCertificatePem);
        var authentication = await fixture.Authenticator.AuthenticateAsync(certificate);
        await fixture.Profiles.GetProfileAsync(authentication.Value!);
        await fixture.Administration.RevokeAsync(
            new RevokeDeviceCommand("admin-1", "company-1", enrolled.Value.DeviceId));

        var dispatch = await fixture.Outbox.ProcessOneAsync();

        Assert.True(dispatch!.Succeeded, dispatch.Error);
        Assert.Single(provider.Commands);
        Assert.Equal(DeviceAccessProviderCommandKind.Revoke, provider.Commands[0].Kind);
    }

    private static string CreateCsr()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=untrusted-client-value", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSigningRequestPem();
    }

    private static string CorruptSignature(string pem)
    {
        var fields = PemEncoding.Find(pem);
        var der = Convert.FromBase64String(pem[fields.Base64Data].ToString());
        der[^1] ^= 0x01;
        return PemEncoding.WriteString("CERTIFICATE REQUEST", der);
    }

    private static X509Certificate2 CreateEnrollmentProof(RSA key, DateTimeOffset now)
    {
        var request = new CertificateRequest("CN=connector-enrollment-proof", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        return request.CreateSelfSigned(now.AddMinutes(-1), now.AddMinutes(5));
    }

    private static string Describe<T>(DeviceAccessResult<T> result) =>
        result.IsSuccess
            ? "success"
            : $"failure {result.Failure?.Code ?? "<missing-code>"}: {result.Failure?.Message ?? "<missing-message>"}";
}

internal sealed class AccessFixture : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _databasePath;
    private readonly FixtureCertificateIssuer _issuer;

    private AccessFixture(
        ServiceProvider services,
        string databasePath,
        FixtureCertificateIssuer issuer,
        FakeTimeProvider clock,
        TestPlatformDirectory directory)
    {
        _services = services;
        _databasePath = databasePath;
        _issuer = issuer;
        Clock = clock;
        Directory = directory;
        Enrollment = services.GetRequiredService<DeviceEnrollmentService>();
        Authenticator = services.GetRequiredService<DeviceCertificateAuthenticator>();
        Administration = services.GetRequiredService<DeviceAccessAdministrationService>();
        SelfRevocation = services.GetRequiredService<DeviceSelfRevocationService>();
        Profiles = services.GetRequiredService<DeviceAccessProfileService>();
        Outbox = services.GetRequiredService<DeviceAccessOutboxProcessor>();
        ProviderOutbox = services.GetRequiredService<DbProviderOutboxRepository>();
    }

    public FakeTimeProvider Clock { get; }
    public TestPlatformDirectory Directory { get; }
    public DeviceEnrollmentService Enrollment { get; }
    public DeviceCertificateAuthenticator Authenticator { get; }
    public DeviceAccessAdministrationService Administration { get; }
    public DeviceSelfRevocationService SelfRevocation { get; }
    public DeviceAccessProfileService Profiles { get; }
    public DeviceAccessOutboxProcessor Outbox { get; }
    internal DbProviderOutboxRepository ProviderOutbox { get; }
    public FixtureCertificateIssuer Issuer => _issuer;

    public static async ValueTask<AccessFixture> CreateAsync(
        IEnumerable<IDeviceAccessGrantProvider>? providers = null,
        Action<DeviceAccessOptions>? configure = null)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"connector-access-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 10,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            foreach (var migrationPath in System.IO.Directory.GetFiles(
                         Path.Combine(AppContext.BaseDirectory, "migrations"),
                         "*.sql").Order())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = await File.ReadAllTextAsync(migrationPath);
                await command.ExecuteNonQueryAsync();
            }
        }

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var directory = new TestPlatformDirectory();
        var issuer = new FixtureCertificateIssuer();
        var collection = new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<IPlatformAccessDirectory>(directory)
            .AddSingleton<IX509DeviceCertificateIssuer>(issuer)
            .AddSingleton<IDeviceAccessDbConnectionFactory>(new SqliteConnectionFactory(connectionString));
        foreach (var provider in providers ?? Array.Empty<IDeviceAccessGrantProvider>())
        {
            collection.AddSingleton<IDeviceAccessGrantProvider>(provider);
        }

        if (configure is null)
        {
            collection.AddConnectorAccessCore();
        }
        else
        {
            collection.AddConnectorAccessCore(configure);
        }

        var services = collection.BuildServiceProvider(validateScopes: false);
        return new AccessFixture(services, databasePath, issuer, clock, directory);
    }

    public async ValueTask<EnrollmentTokenIssue> IssueAsync(TimeSpan? lifetime = null)
    {
        var result = await Enrollment.IssueTokenAsync(
            new IssueEnrollmentTokenCommand("admin-1", "user-1", "company-1", lifetime));
        Assert.True(
            result.IsSuccess,
            result.Failure is null
                ? "token issue failed without DeviceAccessFailure"
                : $"token issue failure {result.Failure.Code}: {result.Failure.Message}");
        return Assert.IsType<EnrollmentTokenIssue>(result.Value);
    }

    public ValueTask<DeviceAccessResult<DeviceEnrollmentResponse>> EnrollAsync(string token, string csr) =>
        Enrollment.EnrollAsync(new DeviceEnrollmentRequest(
            DeviceAccessProtocol.Version,
            Guid.NewGuid(),
            token,
            csr,
            "Test workstation"));

    public async ValueTask<long> CountDevicesAsync()
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM connector_access_devices";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async ValueTask<(string Status, string? Error, long AttemptCount)> ReadOutboxAsync(string commandId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, last_error, attempt_count
            FROM connector_access_provider_outbox
            WHERE command_id = $command_id
            """;
        command.Parameters.AddWithValue("$command_id", commandId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"outbox command {commandId} was not persisted");
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2));
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _issuer.Dispose();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}

internal sealed class RecordingGrantProvider : IDeviceAccessGrantProvider
{
    private readonly Dictionary<string, (long Revision, DeviceAccessProviderCommandKind Kind)> _fences = new();

    public RecordingGrantProvider(string providerName) => ProviderName = providerName;

    public string ProviderName { get; }
    public bool FailNext { get; set; }
    public List<DeviceAccessProviderCommand> Commands { get; } = new();

    public ValueTask<DeviceAccessProviderReceipt> ApplyAsync(
        DeviceAccessProviderCommand command,
        CancellationToken cancellationToken) => ExecuteAsync(command);

    public ValueTask<DeviceAccessProviderReceipt> RevokeAsync(
        DeviceAccessProviderCommand command,
        CancellationToken cancellationToken) => ExecuteAsync(command);

    private ValueTask<DeviceAccessProviderReceipt> ExecuteAsync(DeviceAccessProviderCommand command)
    {
        if (FailNext)
        {
            FailNext = false;
            throw new InvalidOperationException("fixture provider unavailable");
        }

        if (_fences.TryGetValue(command.DeviceId, out var current) &&
            (command.DesiredRevision < current.Revision ||
             command.DesiredRevision == current.Revision && command.Kind != current.Kind))
        {
            throw new InvalidOperationException("stale provider command");
        }

        _fences[command.DeviceId] = (command.DesiredRevision, command.Kind);
        Commands.Add(command);
        return ValueTask.FromResult(new DeviceAccessProviderReceipt(
            command.CommandId,
            command.DesiredRevision,
            command.Kind));
    }
}

internal sealed class SqliteConnectionFactory : IDeviceAccessDbConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string connectionString) => _connectionString = connectionString;

    public async ValueTask<System.Data.Common.DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}

internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public FakeTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public void Advance(TimeSpan value) => _utcNow = _utcNow.Add(value);
}

internal sealed class TestPlatformDirectory : IPlatformAccessDirectory
{
    public bool UserIsActive { get; set; } = true;
    public bool MembershipIsActive { get; set; } = true;
    public bool CompanyIsActive { get; set; } = true;
    public bool IsAuthorized { get; set; } = true;
    public string? ReturnedCompanyId { get; set; }

    public ValueTask<PlatformAccessSnapshot?> AuthorizeAsync(
        string actorUserId,
        string subjectUserId,
        string companyId,
        PlatformAccessOperation operation,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<PlatformAccessSnapshot?>(new PlatformAccessSnapshot(
            subjectUserId,
            ReturnedCompanyId ?? companyId,
            UserIsActive,
            MembershipIsActive,
            CompanyIsActive,
            IsAuthorized,
            7,
            Array.Empty<ModuleGrant>(),
            Array.Empty<ResourceGrant>()));
}

internal sealed class FixtureCertificateIssuer : IX509DeviceCertificateIssuer, IDisposable
{
    private readonly RSA _key = RSA.Create(2048);
    private readonly X509Certificate2 _certificateWithKey;

    public FixtureCertificateIssuer()
    {
        var request = new CertificateRequest("CN=Connector Access Fixture CA", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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
    public bool FailNextIssue { get; set; }
    public int IssueCount { get; private set; }

    public ValueTask<X509Certificate2> IssueAsync(
        CertificateRequest certificateRequest,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        CancellationToken cancellationToken)
    {
        IssueCount++;
        if (FailNextIssue)
        {
            FailNextIssue = false;
            throw new CryptographicException("fixture issuer unavailable");
        }

        return ValueTask.FromResult(certificateRequest.Create(
            _certificateWithKey,
            notBefore,
            notAfter,
            RandomNumberGenerator.GetBytes(16)));
    }

    public void Dispose()
    {
        IssuerCertificate.Dispose();
        _certificateWithKey.Dispose();
        _key.Dispose();
    }
}
