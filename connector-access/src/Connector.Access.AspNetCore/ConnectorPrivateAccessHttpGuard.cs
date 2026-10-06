using System.Net;
using System.Security.Cryptography.X509Certificates;
using Connector.Access.Api;
using Connector.Access.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Connector.Access.AspNetCore;

public sealed record ConnectorPrivateAccessOptions(bool ApiEnabled, int VpnStateMaximumAgeSeconds);

public sealed record ConnectorPrivateAccessContext(AuthenticatedDevice Identity, DeviceAccessProfile Profile);

/// <summary>Applies the shared certificate, grant-revision, and VPN transport checks for private device routes.</summary>
public static class ConnectorPrivateAccessHttpGuard
{
    /// <summary>Applies the shared HTTPS and client-certificate boundary for private device routes.</summary>
    public static async ValueTask<(AuthenticatedDevice? Identity, IResult? Error)> AuthenticateDeviceAsync(
        HttpContext context,
        Func<X509Certificate2, CancellationToken, ValueTask<DeviceAccessResult<AuthenticatedDevice>>> authenticate)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticate);

        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps)
            return (null, Failure("https_required", StatusCodes.Status400BadRequest));

        var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
        if (certificate is null)
            return (null, Failure("device_unauthorized", StatusCodes.Status401Unauthorized));

        var result = await authenticate(certificate, context.RequestAborted);
        return !result.IsSuccess || result.Value is null
            ? (null, Failure("device_unauthorized", StatusCodes.Status401Unauthorized))
            : (result.Value, null);
    }

    public static async ValueTask<(ConnectorPrivateAccessContext? Context, IResult? Error)> AuthorizeAsync(
        HttpContext context,
        ConnectorPrivateAccessOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var authentication = await AuthenticateDeviceAsync(context, (certificate, cancellationToken) =>
            context.RequestServices.GetRequiredService<DeviceCertificateAuthenticator>()
                .AuthenticateAsync(certificate, cancellationToken));
        if (authentication.Identity is null) return (null, authentication.Error);
        var identity = authentication.Identity;

        var apiReader = context.RequestServices.GetService<IConnectorApiAccessPolicyReader>();
        var vpnReader = context.RequestServices.GetService<IConnectorVpnBootstrapReader>();
        if (apiReader is null || vpnReader is null || !options.ApiEnabled)
            return (null, Failure("access_provider_unavailable", StatusCodes.Status503ServiceUnavailable));
        var api = await apiReader.GetCurrentAsync(identity, context.RequestAborted);
        var profile = await context.RequestServices.GetRequiredService<DeviceAccessProfileService>()
            .GetProfileAsync(identity, context.RequestAborted);
        if (!api.IsSuccess || api.Value is null || !profile.IsSuccess || profile.Value is null ||
            api.Value.Revision != profile.Value.AppliedRevision || !SameGrants(api.Value, profile.Value))
            return (null, Failure("access_pending_or_denied", StatusCodes.Status403Forbidden));

        var vpn = await vpnReader.GetStateAsync(identity, context.RequestAborted);
        var remote = Normalize(context.Connection.RemoteIpAddress);
        var maximumAge = TimeSpan.FromSeconds(options.VpnStateMaximumAgeSeconds);
        var now = DateTimeOffset.UtcNow;
        if (!vpn.IsSuccess || vpn.Value is null || vpn.Value.DeviceId != identity.DeviceId ||
            vpn.Value.Revision != profile.Value.AppliedRevision || vpn.Value.Status != "ready" ||
            options.VpnStateMaximumAgeSeconds is <= 0 or > 300 ||
            vpn.Value.ObservedAtUtc < now.Subtract(maximumAge) || vpn.Value.ObservedAtUtc > now.AddMinutes(2) ||
            remote is null || !vpn.Value.ExpectedAssignedAddresses.Select(Parse).Any(address => address?.Equals(remote) == true))
            return (null, Failure("vpn_transport_denied", StatusCodes.Status403Forbidden));

        return (new ConnectorPrivateAccessContext(identity, profile.Value), null);
    }

    private static bool SameGrants(ConnectorApiAccessPolicy policy, DeviceAccessProfile profile) =>
        Canonical(policy.Modules, policy.Resources) == Canonical(profile.Modules, profile.Resources);

    private static string Canonical(
        IReadOnlyList<ModuleGrant> modules,
        IReadOnlyList<ResourceGrant> resources) => string.Join(';', modules
            .Select(module => $"m|{(int)module.Product}|{module.ModuleId}|{string.Join(',', module.Permissions.Order())}")
            .Concat(resources.Select(resource =>
                $"r|{resource.ResourceKind}|{resource.ResourceId}|{resource.ProjectId}|{string.Join(',', resource.Permissions.Order())}"))
            .Order(StringComparer.Ordinal));

    private static IPAddress? Parse(string value) => IPAddress.TryParse(value, out var address) ? Normalize(address) : null;
    private static IPAddress? Normalize(IPAddress? address) => address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;
    private static IResult Failure(string code, int status) => Results.Json(new { error = code }, statusCode: status);
}
