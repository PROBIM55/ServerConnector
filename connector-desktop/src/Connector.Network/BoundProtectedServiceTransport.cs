using System.Net;
using System.Net.Sockets;

namespace Connector.Network;

internal sealed class BoundOverlayConnector
{
    private readonly ProtectedServiceNetworkGate.ValidatedProtectedService _service;
    private readonly ProtectedOverlayTransportIdentity _identity;
    private readonly INetworkOverlayClient _overlay;
    private readonly IProtectedDnsResolver _dnsResolver;
    private readonly IOverlaySocketConnector _socketConnector;

    public BoundOverlayConnector(
        ProtectedServiceNetworkGate.ValidatedProtectedService service,
        ProtectedOverlayTransportIdentity identity,
        INetworkOverlayClient overlay,
        IProtectedDnsResolver dnsResolver,
        IOverlaySocketConnector socketConnector)
    {
        _service = service;
        _identity = identity;
        _overlay = overlay;
        _dnsResolver = dnsResolver;
        _socketConnector = socketConnector;
    }

    public async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken) =>
        await ConnectAsync(context.DnsEndPoint, cancellationToken);

    internal async ValueTask<Stream> ConnectAsync(
        DnsEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(endpoint.Host, _service.BaseUri.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            endpoint.Port != _service.BaseUri.Port)
            throw new NetworkGateException("service_destination_denied", "The HTTP connection target is outside the protected service origin.");

        var resolved = await _dnsResolver.ResolveAsync(endpoint.Host, cancellationToken);
        var overlay = await _overlay.GetStatusAsync(cancellationToken);
        ProtectedServiceNetworkGate.EnsureMatches(_identity, overlay);
        var permitted = resolved
            .Where(OverlayAddressRange.IsPrivateOverlayAddress)
            .Where(address => _service.AllowedDestinations.Any(range => range.Contains(address)))
            .Distinct()
            .ToArray();
        if (permitted.Length == 0)
            throw new NetworkGateException("service_destination_denied", "Protected service DNS did not resolve to an allowed overlay destination.");

        foreach (var remote in permitted)
        {
            var local = overlay.AssignedInternalAddresses.FirstOrDefault(address =>
                address.AddressFamily == remote.AddressFamily && OverlayAddressRange.IsPrivateOverlayAddress(address));
            if (local is null) continue;
            var stream = await _socketConnector.ConnectAsync(local, remote, endpoint.Port, cancellationToken);
            if (!_identity.AuthorizationLifetime.IsCancellationRequested) return stream;
            await stream.DisposeAsync();
            throw new NetworkGateException("route_authorization_expired", "The protected route authorization expired while connecting.");
        }
        throw new NetworkGateException("overlay_address_family_mismatch", "No assigned overlay address matches an allowed destination address family.");
    }
}

internal interface IProtectedDnsResolver
{
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal sealed class SystemProtectedDnsResolver : IProtectedDnsResolver
{
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken);
}

internal interface IOverlaySocketConnector
{
    ValueTask<Stream> ConnectAsync(IPAddress localAddress, IPAddress remoteAddress, int port, CancellationToken cancellationToken);
}

internal sealed class SystemOverlaySocketConnector : IOverlaySocketConnector
{
    public async ValueTask<Stream> ConnectAsync(
        IPAddress localAddress,
        IPAddress remoteAddress,
        int port,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(remoteAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(localAddress, 0));
            await socket.ConnectAsync(new IPEndPoint(remoteAddress, port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

internal sealed class OverlayAddressRange
{
    private readonly byte[] _network;
    private readonly int _prefixLength;

    private OverlayAddressRange(IPAddress network, int prefixLength)
    {
        AddressFamily = network.AddressFamily;
        _network = network.GetAddressBytes();
        _prefixLength = prefixLength;
    }

    public AddressFamily AddressFamily { get; }

    public static OverlayAddressRange Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Overlay destination address is empty.");
        var parts = value.Split('/', 2);
        if (!IPAddress.TryParse(parts[0], out var address) || !IsPrivateOverlayAddress(address))
            throw new ArgumentException("Overlay destinations must be private, CGNAT, or ULA addresses.");
        var maximum = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = parts.Length == 1 ? maximum :
            int.TryParse(parts[1], out var parsed) && parsed is >= 0 && parsed <= 128 ? parsed : -1;
        if (prefix < 0 || prefix > maximum) throw new ArgumentException("Overlay destination CIDR prefix is invalid.");
        return new OverlayAddressRange(address, prefix);
    }

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily || !IsPrivateOverlayAddress(address)) return false;
        var candidate = address.GetAddressBytes();
        var wholeBytes = _prefixLength / 8;
        var remainingBits = _prefixLength % 8;
        for (var index = 0; index < wholeBytes; index++)
            if (_network[index] != candidate[index]) return false;
        if (remainingBits == 0) return true;
        var mask = (byte)(0xff << (8 - remainingBits));
        return (_network[wholeBytes] & mask) == (candidate[wholeBytes] & mask);
    }

    public static bool IsPrivateOverlayAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 100 && (bytes[1] & 0xc0) == 0x40;
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xfe) == 0xfc;
    }
}
