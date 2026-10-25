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
    Uri? ManagementUri = null,
    string? DaemonVersion = null,
    IReadOnlyList<NetworkOverlayPeer>? Peers = null);

/// <summary>
/// A remote peer as the local NetBird daemon reports it (display facts only: overlay address, routed networks
/// and whether the tunnel is direct or relayed). Never carries keys or credentials.
/// </summary>
public sealed record NetworkOverlayPeer(IPAddress Address, bool Connected, bool Relayed, IReadOnlyList<string> Networks)
{
    /// <summary>True when the peer is the destination itself or routes a network that contains it.</summary>
    public bool Serves(IPAddress destination)
    {
        if (Address.Equals(destination)) return true;
        foreach (var network in Networks)
        {
            var parts = network.Split('/', 2);
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var prefix) || !int.TryParse(parts[1], out var bits) ||
                prefix.AddressFamily != destination.AddressFamily) continue;
            var a = prefix.GetAddressBytes();
            var b = destination.GetAddressBytes();
            if (bits < 0 || bits > a.Length * 8) continue;
            var matches = true;
            for (var bit = 0; bit < bits && matches; bit++)
            {
                var mask = (byte)(0x80 >> (bit % 8));
                matches = (a[bit / 8] & mask) == (b[bit / 8] & mask);
            }
            if (matches) return true;
        }
        return false;
    }
}

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

// Optional port for callers that need local route evidence from the same
// authenticated request. Metadata never comes from response headers or a server body.
public sealed record ProtectedConnectorResponse(HttpResponseMessage Response, NetworkOverlaySnapshot Overlay);

public interface IOverlayAwareConnectorRequestTransport
{
    Task<ProtectedConnectorResponse> SendWithOverlayAsync(HttpMethod method, string relativeUri, object? body,
        IReadOnlyDictionary<string, string> managedHeaders, CancellationToken cancellationToken);
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
