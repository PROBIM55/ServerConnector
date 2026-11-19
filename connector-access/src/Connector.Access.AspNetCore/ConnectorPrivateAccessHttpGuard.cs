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
        var cache = context.RequestServices.GetService<ConnectorVpnStateCache>();
        if (!api.IsSuccess || api.Value is null || !profile.IsSuccess || profile.Value is null ||
            api.Value.Revision != profile.Value.AppliedRevision || !SameGrants(api.Value, profile.Value))
        {
            // Revoked, denied or changed access drops any cached observation.
            cache?.Invalidate(identity.DeviceId);
            return (null, Failure("access_pending_or_denied", StatusCodes.Status403Forbidden));
        }

        var remote = Normalize(context.Connection.RemoteIpAddress);
        var maximumAge = TimeSpan.FromSeconds(options.VpnStateMaximumAgeSeconds);
        var now = DateTimeOffset.UtcNow;
        // The live VPN check costs several management API calls. A confirmed
        // "ready" observation is reused only for the same certificate, user,
        // company and applied revision, for a short time that never exceeds
        // the accepted observation age; every other check above and below
        // still runs on each request.
        var vpn = cache?.TryGet(identity, profile.Value.AppliedRevision, now, maximumAge);
        if (vpn is null)
        {
            try
            {
                vpn = await vpnReader.GetStateAsync(identity, context.RequestAborted);
            }
            catch (DeviceAccessProviderBusyException busy)
            {
                return (null, ConnectorAccessHttpResults.Busy(context, busy, "vpn_provider_busy"));
            }
        }
        if (!vpn.IsSuccess || vpn.Value is null || vpn.Value.DeviceId != identity.DeviceId ||
            vpn.Value.Revision != profile.Value.AppliedRevision || vpn.Value.Status != "ready" ||
            options.VpnStateMaximumAgeSeconds is <= 0 or > 300 ||
            vpn.Value.ObservedAtUtc < now.Subtract(maximumAge) || vpn.Value.ObservedAtUtc > now.AddMinutes(2) ||
            remote is null || !vpn.Value.ExpectedAssignedAddresses.Select(Parse).Any(address => address?.Equals(remote) == true))
            return (null, Failure("vpn_transport_denied", StatusCodes.Status403Forbidden));

        cache?.Store(identity, profile.Value.AppliedRevision, vpn.Value, now, maximumAge);
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

// Short-lived per-device cache of a fully accepted "ready" VPN observation.
internal sealed class ConnectorVpnStateCache
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(15);
    private const int DefaultMaximumEntries = 4096;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _maximumEntries;

    public ConnectorVpnStateCache(TimeSpan? lifetime = null, int maximumEntries = DefaultMaximumEntries)
    {
        Lifetime = lifetime ?? DefaultLifetime;
        _maximumEntries = Math.Max(1, maximumEntries);
    }

    public TimeSpan Lifetime { get; }

    public int Count => _entries.Count;

    public DeviceAccessResult<ConnectorVpnTransportState>? TryGet(
        AuthenticatedDevice identity,
        long appliedRevision,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        if (!_entries.TryGetValue(identity.DeviceId, out var entry)) return null;
        if (!string.Equals(entry.CertificateSha256, identity.CertificateSha256, StringComparison.Ordinal) ||
            !string.Equals(entry.UserId, identity.UserId, StringComparison.Ordinal) ||
            !string.Equals(entry.CompanyId, identity.CompanyId, StringComparison.Ordinal) ||
            entry.AppliedRevision != appliedRevision ||
            entry.State.Revision != appliedRevision ||
            now < entry.CachedAtUtc ||
            now - entry.CachedAtUtc >= EffectiveLifetime(maximumAge))
        {
            _entries.TryRemove(new KeyValuePair<string, Entry>(identity.DeviceId, entry));
            return null;
        }

        return DeviceAccessResult<ConnectorVpnTransportState>.Success(entry.State);
    }

    public void Store(
        AuthenticatedDevice identity,
        long appliedRevision,
        ConnectorVpnTransportState state,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        if (state.Status != "ready" || EffectiveLifetime(maximumAge) <= TimeSpan.Zero) return;
        if (_entries.Count >= _maximumEntries && !_entries.ContainsKey(identity.DeviceId))
        {
            // Entries live seconds: drop expired ones, and everything if the
            // cache is still full (a miss only costs one live check).
            foreach (var stale in _entries.Where(pair => now - pair.Value.CachedAtUtc >= Lifetime || now < pair.Value.CachedAtUtc))
                _entries.TryRemove(stale);
            if (_entries.Count >= _maximumEntries) _entries.Clear();
        }
        _entries[identity.DeviceId] = new Entry(
            identity.CertificateSha256, identity.UserId, identity.CompanyId, appliedRevision, state, now);
    }

    public void Invalidate(string deviceId) => _entries.TryRemove(deviceId, out _);

    private TimeSpan EffectiveLifetime(TimeSpan maximumAge)
    {
        var half = TimeSpan.FromTicks(maximumAge.Ticks / 2);
        return half < Lifetime ? half : Lifetime;
    }

    private sealed record Entry(
        string CertificateSha256,
        string UserId,
        string CompanyId,
        long AppliedRevision,
        ConnectorVpnTransportState State,
        DateTimeOffset CachedAtUtc);
}

internal static class ConnectorAccessHttpResults
{
    public static IResult Busy(HttpContext context, DeviceAccessProviderBusyException busy, string code)
    {
        var seconds = (int)Math.Clamp(Math.Ceiling(busy.RetryAfter.TotalSeconds), 1, 60);
        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Json(new DeviceAccessFailure(code, "Сервис доступа занят. Повторите запрос."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
