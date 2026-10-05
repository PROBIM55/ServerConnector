using System.Security.Cryptography.X509Certificates;
using Connector.Access.Client;
using Connector.Network;

namespace Connector.Upgrade.PlatformAccess;

internal interface INetBirdExactPeerIdentitySource
{
    ValueTask<NetBirdLocalIdentityReadResult> ReadAsync(
        Uri expectedManagementUri,
        CancellationToken cancellationToken);
}

internal interface INetBirdExactPeerRouteSource
{
    CommonConnectorConnectionSnapshot Current { get; }

    ValueTask<IExactPeerAttestationRoute> OpenAsync(
        string serviceId,
        string relativeUri,
        CancellationToken cancellationToken);
}

internal interface IExactPeerAttestationRoute : IDisposable
{
    string ServiceId { get; }
    Uri Uri { get; }
    NetworkOverlaySnapshot Overlay { get; }
    HttpClient Client { get; }
}

internal interface INetBirdExactPeerCertificateSource
{
    ValueTask<IExactPeerCertificateLease> AcquireAsync(CancellationToken cancellationToken);
}

internal interface IExactPeerCertificateLease : IDisposable
{
    string DeviceId { get; }
    long EnrollmentRevision { get; }
    X509Certificate2 Certificate { get; }
}

internal interface IExactPeerNonceSource
{
    string CreateNonce();
}

internal sealed class NetBirdCliExactPeerIdentitySource(NetBirdCliClient client)
    : INetBirdExactPeerIdentitySource
{
    private readonly NetBirdCliClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public ValueTask<NetBirdLocalIdentityReadResult> ReadAsync(
        Uri expectedManagementUri,
        CancellationToken cancellationToken) =>
        _client.GetLocalIdentityAsync(expectedManagementUri, cancellationToken);
}

internal sealed class CoordinatorExactPeerRouteSource(CommonConnectorConnectionCoordinator coordinator)
    : INetBirdExactPeerRouteSource
{
    private readonly CommonConnectorConnectionCoordinator _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public CommonConnectorConnectionSnapshot Current => _coordinator.Current;

    public async ValueTask<IExactPeerAttestationRoute> OpenAsync(
        string serviceId,
        string relativeUri,
        CancellationToken cancellationToken) =>
        new CoordinatorExactPeerAttestationRoute(
            await _coordinator.OpenProtectedServiceAsync(serviceId, relativeUri, cancellationToken)
                .ConfigureAwait(false));
}

internal sealed class CoordinatorExactPeerAttestationRoute(ProtectedServiceRoute route)
    : IExactPeerAttestationRoute
{
    private readonly ProtectedServiceRoute _route = route ?? throw new ArgumentNullException(nameof(route));

    public string ServiceId => _route.ServiceId;
    public Uri Uri => _route.Uri;
    public NetworkOverlaySnapshot Overlay => _route.Overlay;
    public HttpClient Client => _route.Client;
    public void Dispose() => _route.Dispose();
}

internal sealed class EnrollmentExactPeerCertificateSource(HttpConnectorEnrollmentClient enrollment)
    : INetBirdExactPeerCertificateSource
{
    private readonly HttpConnectorEnrollmentClient _enrollment =
        enrollment ?? throw new ArgumentNullException(nameof(enrollment));

    public async ValueTask<IExactPeerCertificateLease> AcquireAsync(CancellationToken cancellationToken) =>
        new EnrollmentExactPeerCertificateLease(
            await _enrollment.AcquireIssuedCertificateAsync(cancellationToken).ConfigureAwait(false));
}

internal sealed class EnrollmentExactPeerCertificateLease(ConnectorIssuedCertificateLease lease)
    : IExactPeerCertificateLease
{
    private readonly ConnectorIssuedCertificateLease _lease =
        lease ?? throw new ArgumentNullException(nameof(lease));

    public string DeviceId => _lease.DeviceId;
    public long EnrollmentRevision => _lease.EnrollmentRevision;
    public X509Certificate2 Certificate => _lease.Certificate;
    public void Dispose() => _lease.Dispose();
}

internal sealed class CryptographicExactPeerNonceSource : IExactPeerNonceSource
{
    public string CreateNonce()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
