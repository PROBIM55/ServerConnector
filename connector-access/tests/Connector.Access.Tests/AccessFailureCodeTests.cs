using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Connector.Access.AspNetCore;
using Connector.Access.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Connector.Access.Tests;

// Deployed clients (up to 1.1.14) keep waiting only on the exact legacy 403
// codes. Distinct codes are returned only to clients that send
// X-Connector-Protocol: 2; every refusal keeps HTTP 403.
public sealed class AccessFailureCodeTests
{
    [Fact]
    public async Task Profile_reports_pending_while_providers_apply_grants()
    {
        await using var fixture = await CreateFixtureAsync();
        var device = await EnrollAsync(fixture);
        var result = await fixture.Profiles.GetProfileAsync(device);

        Assert.Equal((403, "access_pending"), await ExecuteAsync(c => ConnectorAccessEndpoints.ProfileResult(c, result), distinct: true));
        Assert.Equal((403, "access_pending_or_denied"), await ExecuteAsync(c => ConnectorAccessEndpoints.ProfileResult(c, result), distinct: false));
    }

    [Fact]
    public async Task Profile_reports_denied_when_platform_grants_deny_access()
    {
        await using var fixture = await CreateFixtureAsync();
        var device = await EnrollAsync(fixture);
        fixture.Directory.IsAuthorized = false;
        var result = await fixture.Profiles.GetProfileAsync(device);

        Assert.Equal((403, "access_denied"), await ExecuteAsync(c => ConnectorAccessEndpoints.ProfileResult(c, result), distinct: true));
        Assert.Equal((403, "access_pending_or_denied"), await ExecuteAsync(c => ConnectorAccessEndpoints.ProfileResult(c, result), distinct: false));
    }

    [Fact]
    public async Task Profile_reports_inactive_device_after_revocation()
    {
        await using var fixture = await CreateFixtureAsync();
        var device = await EnrollAsync(fixture);
        await fixture.Administration.RevokeAsync(new RevokeDeviceCommand("admin-1", "company-1", device.DeviceId));
        var result = await fixture.Profiles.GetProfileAsync(device);

        Assert.Equal((403, "device_not_active"), await ExecuteAsync(c => ConnectorAccessEndpoints.ProfileResult(c, result), distinct: true));
    }

    [Fact]
    public async Task Profile_reports_server_provider_configuration_error()
    {
        await using var fixture = await CreateFixtureAsync(options => options.RequiredAccessProviders.Add(" "));
        var device = await EnrollAsync(fixture);
        var result = await fixture.Profiles.GetProfileAsync(device);

        Assert.Equal((403, "provider_configuration"), await ExecuteAsync(c => ConnectorAccessEndpoints.ProfileResult(c, result), distinct: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("two")]
    public async Task Profile_without_protocol_2_keeps_the_legacy_code(string? header)
    {
        var result = DeviceAccessResult<DeviceAccessProfile>.Fail("access_revision_regressed", "fixture");

        var context = CreateContext(null);
        if (header is not null) context.Request.Headers[ConnectorAccessEndpoints.ProtocolHeader] = header;

        Assert.Equal((403, "access_pending_or_denied"), await ExecuteAsync(ConnectorAccessEndpoints.ProfileResult(context, result), context));
    }

    [Fact]
    public async Task Profile_keeps_the_legacy_code_for_unlisted_failures_even_with_protocol_2()
    {
        var result = DeviceAccessResult<DeviceAccessProfile>.Fail("internal_detail", "not for devices");

        Assert.Equal((403, "access_pending_or_denied"), await ExecuteAsync(c => ConnectorAccessEndpoints.ProfileResult(c, result), distinct: true));
    }

    [Theory]
    [InlineData("vpn_bootstrap_unavailable", true, "vpn_bootstrap_unavailable")]
    [InlineData("vpn_bootstrap_consumed", true, "vpn_bootstrap_consumed")]
    [InlineData("vpn_bootstrap_protection", true, "vpn_bootstrap_protection")]
    [InlineData("internal_detail", true, "vpn_bootstrap_unavailable")]
    [InlineData("vpn_bootstrap_consumed", false, "vpn_bootstrap_unavailable")]
    [InlineData("vpn_bootstrap_protection", false, "vpn_bootstrap_unavailable")]
    public async Task Vpn_bootstrap_codes_depend_on_the_declared_protocol(string providerCode, bool distinct, string expected)
    {
        var context = CreateContext(distinct);

        var result = await ConnectorAccessEndpoints.ReadVpnAsync(
            context,
            () => ValueTask.FromResult(DeviceAccessResult<ConnectorVpnBootstrap>.Fail(providerCode, "fixture")),
            "vpn_bootstrap_unavailable");

        Assert.Equal((403, expected), await ExecuteAsync(result, context));
    }

    [Theory]
    [InlineData(true, "vpn_state_not_supported")]
    [InlineData(false, "vpn_state_unavailable")]
    public async Task Vpn_state_distinguishes_unsupported_provider_only_for_protocol_2(bool distinct, string expected)
    {
        IConnectorVpnBootstrapReader reader = new StatelessVpnReader();
        var unsupported = await reader.GetStateAsync(
            new AuthenticatedDevice("device-1", "user-1", "company-1", new string('a', 64), 7), default);
        var context = CreateContext(distinct);

        var result = await ConnectorAccessEndpoints.ReadVpnAsync(context, () => ValueTask.FromResult(unsupported), "vpn_state_unavailable");

        Assert.Equal((403, expected), await ExecuteAsync(result, context));
    }

    [Fact]
    public async Task Busy_vpn_reader_is_503_for_protocol_2_and_a_waiting_403_for_legacy_clients()
    {
        var modern = CreateContext(true);
        var legacy = CreateContext(false);

        var modernResult = await ConnectorAccessEndpoints.ReadVpnAsync<ConnectorVpnTransportState>(
            modern, () => throw new DeviceAccessProviderBusyException("held"), "vpn_state_unavailable");
        var legacyResult = await ConnectorAccessEndpoints.ReadVpnAsync<ConnectorVpnTransportState>(
            legacy, () => throw new DeviceAccessProviderBusyException("held"), "vpn_state_unavailable");

        Assert.Equal((503, "vpn_provider_busy"), await ExecuteAsync(modernResult, modern));
        Assert.Equal("1", modern.Response.Headers.RetryAfter.ToString());
        Assert.Equal((403, "vpn_state_unavailable"), await ExecuteAsync(legacyResult, legacy));
    }

    private static async Task<AccessFixture> CreateFixtureAsync(Action<DeviceAccessOptions>? configure = null) =>
        await AccessFixture.CreateAsync(
            [],
            options =>
            {
                options.RequiredAccessProviders.Clear();
                options.RequiredAccessProviders.Add("vpn");
                configure?.Invoke(options);
            });

    private static async Task<AuthenticatedDevice> EnrollAsync(AccessFixture fixture)
    {
        var issue = await fixture.IssueAsync();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var enrolled = await fixture.EnrollAsync(issue.Token, request.CreateSigningRequestPem());
        Assert.True(enrolled.IsSuccess, enrolled.Failure?.Code);
        using var certificate = X509Certificate2.CreateFromPem(enrolled.Value!.ClientCertificatePem);
        var authentication = await fixture.Authenticator.AuthenticateAsync(certificate);
        Assert.True(authentication.IsSuccess, authentication.Failure?.Code);
        return authentication.Value!;
    }

    private static DefaultHttpContext CreateContext(bool? distinct)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureHttpJsonOptions(_ => { });
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (distinct == true) context.Request.Headers[ConnectorAccessEndpoints.ProtocolHeader] = "2";
        return context;
    }

    private static Task<(int Status, string? Code)> ExecuteAsync(Func<HttpContext, IResult> build, bool distinct)
    {
        var context = CreateContext(distinct);
        return ExecuteAsync(build(context), context);
    }

    private static async Task<(int Status, string? Code)> ExecuteAsync(IResult result, HttpContext context)
    {
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var body = await System.Text.Json.JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, body.RootElement.GetProperty("code").GetString());
    }

    private sealed class StatelessVpnReader : IConnectorVpnBootstrapReader
    {
        public ValueTask<DeviceAccessResult<ConnectorVpnBootstrap>> GetAsync(AuthenticatedDevice device, CancellationToken cancellationToken) =>
            ValueTask.FromResult(DeviceAccessResult<ConnectorVpnBootstrap>.Fail("vpn_bootstrap_unavailable", "fixture"));
    }
}
