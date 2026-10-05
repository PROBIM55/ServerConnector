using System.Net;

namespace Connector.Network;

public enum NetworkServiceStatus
{
    Unknown,
    NotInstalled,
    Disconnected,
    Connecting,
    Degraded,
    Ready,
    Revoked,
}

public sealed record NetworkOverlayBootstrap(Uri ManagementUri, string SetupKey);

public sealed record NetworkOverlaySnapshot(
    NetworkServiceStatus Status,
    bool StartupCheckPassed,
    bool ManagementConnected,
    bool SignalConnected,
    IReadOnlyList<IPAddress> AssignedInternalAddresses,
    DateTimeOffset ObservedAtUtc,
    string DiagnosticCode,
    Uri? ManagementUri = null);

public interface INetworkOverlayClient
{
    ValueTask<NetworkOverlaySnapshot> ConnectAsync(
        NetworkOverlayBootstrap bootstrap,
        CancellationToken cancellationToken = default);

    ValueTask<NetworkOverlaySnapshot> GetStatusAsync(CancellationToken cancellationToken = default);
}

public sealed record ProtectedServiceDefinition(
    string ServiceId,
    Uri BaseUri,
    IReadOnlyList<string> AllowedOverlayDestinations,
    string HealthPath = "health");

public sealed record ProtectedOverlayTransportIdentity(
    string DeviceId,
    long Revision,
    string PeerId,
    Uri ManagementUri,
    IReadOnlyList<IPAddress> AssignedInternalAddresses,
    CancellationToken AuthorizationLifetime);

public sealed class ProtectedServiceRoute : IDisposable
{
    private readonly IDisposable? _credential;
    internal ProtectedServiceRoute(string serviceId, Uri uri, NetworkOverlaySnapshot overlay, HttpClient client, IDisposable? credential = null)
    {
        ServiceId = serviceId;
        Uri = uri;
        Overlay = overlay;
        Client = client;
        _credential = credential;
    }

    public string ServiceId { get; }
    public Uri Uri { get; }
    public NetworkOverlaySnapshot Overlay { get; }
    public HttpClient Client { get; }
    public void Dispose()
    {
        Client.Dispose();
        _credential?.Dispose();
    }
}

public interface IProtectedServiceProbe
{
    ValueTask<bool> IsOnlineAsync(HttpClient client, Uri healthUri, CancellationToken cancellationToken);
}

public interface IProtectedServiceNetworkGate
{
    ValueTask<ProtectedServiceRoute> ResolveAndProbeAsync(
        ProtectedOverlayTransportIdentity identity,
        string serviceId,
        string relativeUri,
        CancellationToken cancellationToken = default);

    ValueTask<ProtectedServiceRoute> ResolveAndProbeAsync(
        string serviceId,
        string relativeUri,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ProtectedServiceRoute>(new NetworkGateException(
            "server_transport_identity_required",
            "A server-issued overlay transport identity is required."));
}

public sealed class NetworkGateException : Exception
{
    public NetworkGateException(string code, string message, Exception? innerException = null) : base(message, innerException) => Code = code;
    public string Code { get; }
}
