using System.Net;
using System.Net.Sockets;
using Connector.Network;

namespace Connector.Network.Tests;

public sealed class BoundProtectedServiceTransportTests
{
    [Fact]
    public async Task PublicDnsAnswer_IsRejectedWithoutOpeningSocket()
    {
        var socket = new RecordingSocketConnector();
        var connector = CreateConnector([IPAddress.Parse("8.8.8.8")], [IPAddress.Parse("100.90.0.22")], socket, "100.64.0.0/10");

        var error = await Assert.ThrowsAsync<NetworkGateException>(() => Connect(connector, "service.internal"));

        Assert.Equal("service_destination_denied", error.Code);
        Assert.Null(socket.Remote);
    }

    [Fact]
    public async Task PrivateButUnlistedDnsAnswer_IsRejected()
    {
        var socket = new RecordingSocketConnector();
        var connector = CreateConnector([IPAddress.Parse("10.10.0.7")], [IPAddress.Parse("100.90.0.22")], socket, "100.64.0.0/10");

        await Assert.ThrowsAsync<NetworkGateException>(() => Connect(connector, "service.internal"));

        Assert.Null(socket.Remote);
    }

    [Fact]
    public async Task Ipv6Destination_BindsMatchingAssignedOverlayAddress()
    {
        var socket = new RecordingSocketConnector();
        var connector = CreateConnector(
            [IPAddress.Parse("fd00:4e42::5")],
            [IPAddress.Parse("100.90.0.22"), IPAddress.Parse("fd00:4e42::22")],
            socket,
            "fd00:4e42::/48");

        await using var stream = await connector.ConnectAsync(new DnsEndPoint("service.internal", 443), default);

        Assert.Equal("fd00:4e42::22", socket.Local!.ToString());
        Assert.Equal("fd00:4e42::5", socket.Remote!.ToString());
    }

    [Fact]
    public async Task MissingMatchingLocalAddressFamily_FailsClosed()
    {
        var socket = new RecordingSocketConnector();
        var connector = CreateConnector(
            [IPAddress.Parse("fd00:4e42::5")],
            [IPAddress.Parse("100.90.0.22")],
            socket,
            "fd00:4e42::/48");

        var error = await Assert.ThrowsAsync<NetworkGateException>(() => Connect(connector, "service.internal"));

        Assert.Equal("overlay_address_family_mismatch", error.Code);
        Assert.Null(socket.Remote);
    }

    [Fact]
    public async Task LocalOverlayBindFailure_HasNoUnboundSocketFallback()
    {
        var socket = new RecordingSocketConnector { Failure = new SocketException((int)SocketError.AddressNotAvailable) };
        var connector = CreateConnector(
            [IPAddress.Parse("100.90.0.5")],
            [IPAddress.Parse("100.90.0.22")],
            socket,
            "100.64.0.0/10");

        await Assert.ThrowsAsync<SocketException>(() => Connect(connector, "service.internal"));

        Assert.Equal("100.90.0.22", socket.Local!.ToString());
        Assert.Equal(1, socket.Attempts);
    }

    [Fact]
    public async Task OverlayIdentityChangeBeforeSocketBind_IsRejected()
    {
        var socket = new RecordingSocketConnector();
        var service = new ProtectedServiceNetworkGate.ValidatedProtectedService(
            "service",
            new Uri("https://service.internal/"),
            new Uri("https://service.internal/health"),
            [OverlayAddressRange.Parse("100.64.0.0/10")]);
        using var lifetime = new CancellationTokenSource();
        var identity = new ProtectedOverlayTransportIdentity(
            "device-1", 1, "peer-1", new Uri("https://netbird.test"),
            [IPAddress.Parse("100.90.0.22")], lifetime.Token);
        var changed = new NetworkOverlaySnapshot(
            NetworkServiceStatus.Ready, true, true, true, [IPAddress.Parse("100.90.0.99")],
            DateTimeOffset.UtcNow, "ready", new Uri("https://netbird.test"));
        var connector = new BoundOverlayConnector(
            service, identity, new FixedOverlay(changed),
            new FixedDnsResolver([IPAddress.Parse("100.90.0.5")]), socket);

        var error = await Assert.ThrowsAsync<NetworkGateException>(() => Connect(connector, "service.internal"));

        Assert.Equal("overlay_identity_mismatch", error.Code);
        Assert.Null(socket.Remote);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task HealthProbe_RejectsEveryRedirect(HttpStatusCode statusCode)
    {
        using var client = new HttpClient(new RedirectHandler(statusCode));
        var probe = new HttpProtectedServiceProbe();

        Assert.False(await probe.IsOnlineAsync(client, new Uri("https://service.internal/health"), default));
    }

    private static BoundOverlayConnector CreateConnector(
        IReadOnlyList<IPAddress> resolved,
        IReadOnlyList<IPAddress> assigned,
        RecordingSocketConnector socket,
        string allowedRange)
    {
        var service = new ProtectedServiceNetworkGate.ValidatedProtectedService(
            "service",
            new Uri("https://service.internal/"),
            new Uri("https://service.internal/health"),
            [OverlayAddressRange.Parse(allowedRange)]);
        var overlay = new NetworkOverlaySnapshot(
            NetworkServiceStatus.Ready, true, true, true, assigned, DateTimeOffset.UtcNow, "ready", new Uri("https://netbird.test"));
        var identity = new ProtectedOverlayTransportIdentity(
            "device-1", 1, "peer-1", new Uri("https://netbird.test"), assigned, new CancellationTokenSource().Token);
        return new BoundOverlayConnector(service, identity, new FixedOverlay(overlay), new FixedDnsResolver(resolved), socket);
    }

    private static async Task<Stream> Connect(BoundOverlayConnector connector, string host) =>
        await connector.ConnectAsync(new DnsEndPoint(host, 443), default);

    private sealed class FixedDnsResolver(IReadOnlyList<IPAddress> addresses) : IProtectedDnsResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            ValueTask.FromResult(addresses);
    }

    private sealed class FixedOverlay(NetworkOverlaySnapshot snapshot) : INetworkOverlayClient
    {
        public ValueTask<NetworkOverlaySnapshot> ConnectAsync(
            NetworkOverlayBootstrap bootstrap,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(snapshot);

        public ValueTask<NetworkOverlaySnapshot> GetStatusAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(snapshot);
    }

    private sealed class RecordingSocketConnector : IOverlaySocketConnector
    {
        public IPAddress? Local { get; private set; }
        public IPAddress? Remote { get; private set; }
        public Exception? Failure { get; init; }
        public int Attempts { get; private set; }
        public ValueTask<Stream> ConnectAsync(IPAddress localAddress, IPAddress remoteAddress, int port, CancellationToken cancellationToken)
        {
            Attempts++;
            Local = localAddress;
            Remote = remoteAddress;
            if (Failure is not null) return ValueTask.FromException<Stream>(Failure);
            return ValueTask.FromResult<Stream>(new MemoryStream());
        }
    }

    private sealed class RedirectHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Headers = { Location = new Uri("https://public.example.test/") },
            });
    }
}
