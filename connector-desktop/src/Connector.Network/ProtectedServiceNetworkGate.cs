using Connector.Access.Client;
using System.Security.Cryptography.X509Certificates;

namespace Connector.Network;

public sealed class ProtectedServiceNetworkGate : IProtectedServiceNetworkGate
{
    private readonly INetworkOverlayClient _overlay;
    private readonly IProtectedServiceProbe _probe;
    private readonly IReadOnlyDictionary<string, ValidatedProtectedService> _services;
    private readonly IProtectedDnsResolver _dnsResolver;
    private readonly IOverlaySocketConnector _socketConnector;
    private readonly IConnectorIssuedCredentialSource? _credentialSource;

    public ProtectedServiceNetworkGate(
        INetworkOverlayClient overlay,
        IProtectedServiceProbe probe,
        IEnumerable<ProtectedServiceDefinition> services,
        IConnectorIssuedCredentialSource? credentialSource = null)
        : this(overlay, probe, services, new SystemProtectedDnsResolver(), new SystemOverlaySocketConnector(), credentialSource) { }

    internal ProtectedServiceNetworkGate(
        INetworkOverlayClient overlay,
        IProtectedServiceProbe probe,
        IEnumerable<ProtectedServiceDefinition> services,
        IProtectedDnsResolver dnsResolver,
        IOverlaySocketConnector socketConnector,
        IConnectorIssuedCredentialSource? credentialSource = null)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _dnsResolver = dnsResolver ?? throw new ArgumentNullException(nameof(dnsResolver));
        _socketConnector = socketConnector ?? throw new ArgumentNullException(nameof(socketConnector));
        _credentialSource = credentialSource;
        _services = (services ?? throw new ArgumentNullException(nameof(services)))
            .Select(Validate)
            .ToDictionary(service => service.ServiceId, StringComparer.Ordinal);
        if (_services.Count == 0) throw new ArgumentException("At least one protected service is required.", nameof(services));
    }

    public bool HasProtectedService(string serviceId) =>
        !string.IsNullOrWhiteSpace(serviceId) && _services.ContainsKey(serviceId);

    public async ValueTask<ProtectedServiceRoute> ResolveAndProbeAsync(
        ProtectedOverlayTransportIdentity identity,
        string serviceId,
        string relativeUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!_services.TryGetValue(serviceId, out var service))
            throw new NetworkGateException("service_not_allowed", "The protected service is not in the managed allowlist.");

        var snapshot = await _overlay.GetStatusAsync(cancellationToken);
        EnsureMatches(identity, snapshot);

        if (string.IsNullOrWhiteSpace(relativeUri) || Uri.TryCreate(relativeUri, UriKind.Absolute, out _) ||
            relativeUri.StartsWith("/", StringComparison.Ordinal) || relativeUri.Contains('\\'))
            throw new NetworkGateException("service_uri_denied", "Only a relative URI below the protected service base is allowed.");

        var target = new Uri(service.BaseUri, relativeUri);
        if (!SameOrigin(service.BaseUri, target) || !target.AbsolutePath.StartsWith(service.BaseUri.AbsolutePath, StringComparison.Ordinal))
            throw new NetworkGateException("service_uri_denied", "The resolved URI escaped the protected service allowlist.");

        var connector = new BoundOverlayConnector(service, identity, _overlay, _dnsResolver, _socketConnector);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = connector.ConnectAsync,
        };
        ConnectorIssuedCertificateLease? credential = null;
        try
        {
            if (_credentialSource is not null)
            {
                credential = await _credentialSource.AcquireIssuedCertificateAsync(cancellationToken);
                if (credential.DeviceId != identity.DeviceId || credential.EnrollmentRevision > identity.Revision)
                    throw new NetworkGateException("issued_identity_mismatch", "The issued TLS credential belongs to another device or revision.");
                handler.SslOptions.ClientCertificates = new X509CertificateCollection { credential.Certificate };
            }
        }
        catch
        {
            handler.Dispose();
            credential?.Dispose();
            throw;
        }
        var lifetimeHandler = new AuthorizationLifetimeHandler(identity.AuthorizationLifetime) { InnerHandler = handler };
        var client = new HttpClient(lifetimeHandler, disposeHandler: true) { BaseAddress = service.BaseUri };
        try
        {
            if (!await _probe.IsOnlineAsync(client, service.HealthUri, cancellationToken))
                throw new NetworkGateException("service_offline", "The protected service did not pass its overlay health check.");
            return new ProtectedServiceRoute(service.ServiceId, target, snapshot, client, credential);
        }
        catch
        {
            client.Dispose();
            credential?.Dispose();
            throw;
        }
    }

    public ValueTask<ProtectedServiceRoute> ResolveAndProbeAsync(
        string serviceId,
        string relativeUri,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ProtectedServiceRoute>(new NetworkGateException(
            "server_transport_identity_required",
            "A server-issued overlay transport identity is required."));

    internal static void EnsureMatches(ProtectedOverlayTransportIdentity identity, NetworkOverlaySnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(identity.DeviceId) || identity.Revision < 1 || string.IsNullOrWhiteSpace(identity.PeerId) ||
            !identity.AuthorizationLifetime.CanBeCanceled || identity.AuthorizationLifetime.IsCancellationRequested)
            throw new NetworkGateException("overlay_identity_mismatch", "The current overlay does not match the server-issued transport identity.");
        if (snapshot.Status != NetworkServiceStatus.Ready || !snapshot.StartupCheckPassed ||
            !snapshot.ManagementConnected || !snapshot.SignalConnected)
            throw new NetworkGateException("overlay_not_ready", "The managed overlay is not ready.");
        if (snapshot.ManagementUri is null || !SameManagementUri(identity.ManagementUri, snapshot.ManagementUri))
            throw new NetworkGateException("overlay_identity_mismatch", "The current overlay does not match the server-issued transport identity.");

        var expected = identity.AssignedInternalAddresses
            .Where(OverlayAddressRange.IsPrivateOverlayAddress)
            .ToHashSet();
        var actual = snapshot.AssignedInternalAddresses
            .Where(OverlayAddressRange.IsPrivateOverlayAddress)
            .ToHashSet();
        if (expected.Count == 0 || !expected.SetEquals(actual))
            throw new NetworkGateException("overlay_identity_mismatch", "The current overlay addresses do not match the server-issued peer.");
    }

    private static ValidatedProtectedService Validate(ProtectedServiceDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.ServiceId) || definition.ServiceId.Length > 128)
            throw new ArgumentException("Protected service id is required.");
        var uri = definition.BaseUri;
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Protected service base URI must be an HTTPS URI without credentials, query or fragment.");
        var baseUri = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        if (definition.AllowedOverlayDestinations is null || definition.AllowedOverlayDestinations.Count == 0)
            throw new ArgumentException("Protected service requires at least one overlay destination address or CIDR.");
        var allowedDestinations = definition.AllowedOverlayDestinations.Select(OverlayAddressRange.Parse).ToArray();
        if (string.IsNullOrWhiteSpace(definition.HealthPath) || Uri.TryCreate(definition.HealthPath, UriKind.Absolute, out _) ||
            definition.HealthPath.StartsWith("/", StringComparison.Ordinal) || definition.HealthPath.Contains('\\'))
            throw new ArgumentException("Protected service health path must be relative.");
        var health = new Uri(baseUri, definition.HealthPath);
        if (!SameOrigin(baseUri, health) || !health.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal))
            throw new ArgumentException("Protected service health path escapes its base URI.");
        return new ValidatedProtectedService(definition.ServiceId, baseUri, health, allowedDestinations);
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme == right.Scheme && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    private static bool SameManagementUri(Uri left, Uri right) =>
        left.IsAbsoluteUri && right.IsAbsoluteUri && left.Scheme == Uri.UriSchemeHttps && right.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(left.UserInfo) && string.IsNullOrEmpty(right.UserInfo) &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port &&
        string.Equals(left.AbsolutePath.TrimEnd('/'), right.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal) &&
        string.IsNullOrEmpty(left.Query) && string.IsNullOrEmpty(right.Query) &&
        string.IsNullOrEmpty(left.Fragment) && string.IsNullOrEmpty(right.Fragment);

    internal sealed record ValidatedProtectedService(
        string ServiceId,
        Uri BaseUri,
        Uri HealthUri,
        IReadOnlyList<OverlayAddressRange> AllowedDestinations);
}

internal sealed class AuthorizationLifetimeHandler(CancellationToken authorizationLifetime) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (authorizationLifetime.IsCancellationRequested)
            throw new NetworkGateException("route_authorization_expired", "The protected route authorization has expired.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationLifetime);
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (authorizationLifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new NetworkGateException("route_authorization_expired", "The protected route authorization expired during the request.");
        }
        if (!authorizationLifetime.IsCancellationRequested) return response;
        response.Dispose();
        throw new NetworkGateException("route_authorization_expired", "The protected route authorization expired during the request.");
    }
}

public sealed class HttpProtectedServiceProbe : IProtectedServiceProbe
{
    public async ValueTask<bool> IsOnlineAsync(HttpClient client, Uri healthUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, healthUri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return response.IsSuccessStatusCode && (int)response.StatusCode is >= 200 and < 300 &&
               response.RequestMessage?.RequestUri == healthUri;
    }
}
