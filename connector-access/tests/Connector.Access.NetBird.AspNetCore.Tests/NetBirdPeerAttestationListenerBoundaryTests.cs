using System.Net;
using Connector.Access.NetBird.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Connector.Access.NetBird.AspNetCore.Tests;

public sealed class NetBirdPeerAttestationListenerBoundaryTests
{
    private const string V4Listener = "100.64.10.20";
    private const string V4Cidr = "100.64.0.0/10";
    private const string V6Listener = "fd42::20";
    private const string V6Cidr = "fd42::/16";
    private const int Port = 7444;

    [Theory]
    [InlineData(V4Listener, V4Cidr)]
    [InlineData("10.24.1.9", "10.24.1.0/24")]
    [InlineData("192.168.10.20", "192.168.0.0/16")]
    [InlineData(V6Listener, V6Cidr)]
    public void CreateValidated_AcceptsSafeOverlayBindings(string address, string cidr)
    {
        using var daemon = TemporaryDaemon.Create();

        var binding = NetBirdPeerAttestationListenerBinding.CreateValidated(address, cidr, Port, daemon.Path);

        Assert.Equal(IPAddress.Parse(address), binding.ListenerAddress);
        Assert.Equal(Port, binding.HttpsPort);
        Assert.Equal(Path.GetFullPath(daemon.Path), binding.DaemonExecutablePath);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void CreateValidated_AcceptsPortRangeBoundaries(int port)
    {
        using var daemon = TemporaryDaemon.Create();

        var binding = NetBirdPeerAttestationListenerBinding.CreateValidated(V4Listener, V4Cidr, port, daemon.Path);

        Assert.Equal(port, binding.HttpsPort);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("127.0.0.1")]
    [InlineData("100.64.0.1/32")]
    [InlineData(" 100.64.10.20")]
    [InlineData("100.64.10.20 ")]
    [InlineData("224.0.0.1")]
    [InlineData("ff02::1")]
    [InlineData("203.0.113.20")]
    public void CreateValidated_RejectsInvalidListenerAddress(string address)
    {
        using var daemon = TemporaryDaemon.Create();

        Assert.Throws<InvalidOperationException>(() =>
            NetBirdPeerAttestationListenerBinding.CreateValidated(address, V4Cidr, Port, daemon.Path));
    }

    [Theory]
    [InlineData("203.0.113.20", "203.0.113.0/24")]
    [InlineData("100.64.10.20", "100.0.0.0/8")]
    [InlineData("100.64.10.20", "100.64.10.1/10")]
    [InlineData("100.64.10.20", "100.64.0.0/+10")]
    [InlineData("100.64.10.20", "100.64.11.0/24")]
    [InlineData("100.64.10.20", "100.64.0.0 /10")]
    [InlineData("100.64.10.20", "100.64.0.0/ 10")]
    [InlineData("2001:db8::20", "2001:db8::/32")]
    [InlineData("fe80::20", "fe80::/10")]
    [InlineData("fd42::20", "fd42::1/16")]
    [InlineData("100.64.10.20", "::ffff:100.64.0.0/106")]
    public void CreateValidated_RejectsUnsafeOrNoncanonicalCidrs(string address, string cidr)
    {
        using var daemon = TemporaryDaemon.Create();

        Assert.Throws<InvalidOperationException>(() =>
            NetBirdPeerAttestationListenerBinding.CreateValidated(address, cidr, Port, daemon.Path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void CreateValidated_RejectsInvalidPorts(int port)
    {
        using var daemon = TemporaryDaemon.Create();

        Assert.Throws<InvalidOperationException>(() =>
            NetBirdPeerAttestationListenerBinding.CreateValidated(V4Listener, V4Cidr, port, daemon.Path));
    }

    [Fact]
    public void CreateValidated_RejectsRelativeAndMissingDaemonPaths()
    {
        Assert.Throws<InvalidOperationException>(() =>
            NetBirdPeerAttestationListenerBinding.CreateValidated(V4Listener, V4Cidr, Port, "netbird.exe"));

        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-netbird-{Guid.NewGuid():N}.exe");
        Assert.Throws<InvalidOperationException>(() =>
            NetBirdPeerAttestationListenerBinding.CreateValidated(V4Listener, V4Cidr, Port, missingPath));
    }

    [Fact]
    public async Task Boundary_AllowsPrivateRouteOnExactSocketIncludingMappedIpv4()
    {
        using var daemon = TemporaryDaemon.Create();
        var binding = CreateBinding(daemon.Path);

        var response = await InvokeBoundaryAsync(
            binding, NetBirdPeerAttestationEndpoints.Route, IPAddress.Parse("::ffff:100.64.10.20"), Port);

        Assert.Equal(StatusCodes.Status204NoContent, response.StatusCode);
        Assert.False(response.NoStore);
        Assert.Equal(1, response.ReachedHandler);
    }

    [Theory]
    [InlineData("100.64.10.21", Port)]
    [InlineData(V4Listener, Port + 1)]
    public async Task Boundary_HidesPrivateRouteFromOtherAddressesOrPorts(string localIp, int localPort)
    {
        using var daemon = TemporaryDaemon.Create();
        var binding = CreateBinding(daemon.Path);

        var response = await InvokeBoundaryAsync(
            binding, NetBirdPeerAttestationEndpoints.Route, IPAddress.Parse(localIp), localPort);

        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
        Assert.True(response.NoStore);
        Assert.Equal(0, response.ReachedHandler);
    }

    [Theory]
    [InlineData("/api/platform/connector/private/v1/peer-attestation")]
    [InlineData("/api/platform/connector/private/v1/peer-attestation/")]
    [InlineData("/API/PLATFORM/CONNECTOR/PRIVATE/V1/PEER-ATTESTATION")]
    [InlineData("/api/platform/connector%2Fprivate%2Fv1%2Fpeer-attestation")]
    [InlineData("/api/platform/connector\\private\\v1\\peer-attestation")]
    [InlineData("/api/platform/connector%5cprivate%5cv1%5cpeer-attestation")]
    [InlineData("/api/platform/connector%252Fprivate%252Fv1%252Fpeer-attestation")]
    [InlineData("/api/platform/connector%25252Fprivate%25252Fv1%25252Fpeer-attestation")]
    public async Task Boundary_HidesPrivatePathVariantsFromPublicSocket(string path)
    {
        using var daemon = TemporaryDaemon.Create();
        var binding = CreateBinding(daemon.Path);

        var response = await InvokeBoundaryAsync(binding, path, IPAddress.Parse("203.0.113.20"), Port);

        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
        Assert.True(response.NoStore);
        Assert.Equal(0, response.ReachedHandler);
    }

    [Fact]
    public async Task Boundary_LetsOrdinaryPublicPathsContinue()
    {
        using var daemon = TemporaryDaemon.Create();
        var binding = CreateBinding(daemon.Path);

        var response = await InvokeBoundaryAsync(binding, "/api/public/health", IPAddress.Parse("203.0.113.20"), Port);

        Assert.Equal(StatusCodes.Status204NoContent, response.StatusCode);
        Assert.False(response.NoStore);
        Assert.Equal(1, response.ReachedHandler);
    }

    [Fact]
    public async Task ListenerAddress_ReturnsDefensiveIpv6Copy()
    {
        using var daemon = TemporaryDaemon.Create();
        var binding = NetBirdPeerAttestationListenerBinding.CreateValidated(V6Listener, V6Cidr, Port, daemon.Path);
        var exposedAddress = binding.ListenerAddress;
        exposedAddress.ScopeId = 42;

        Assert.Equal(IPAddress.Parse(V6Listener), binding.ListenerAddress);
        var response = await InvokeBoundaryAsync(binding, NetBirdPeerAttestationEndpoints.Route,
            IPAddress.Parse(V6Listener), Port);
        Assert.Equal(StatusCodes.Status204NoContent, response.StatusCode);
        Assert.Equal(1, response.ReachedHandler);
    }

    private static NetBirdPeerAttestationListenerBinding CreateBinding(string daemonPath) =>
        NetBirdPeerAttestationListenerBinding.CreateValidated(V4Listener, V4Cidr, Port, daemonPath);

    private static async Task<BoundaryResult> InvokeBoundaryAsync(
        NetBirdPeerAttestationListenerBinding binding,
        string path,
        IPAddress localAddress,
        int localPort)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        var reachedHandler = 0;
        app.Use((context, next) =>
        {
            context.Connection.LocalIpAddress = localAddress;
            context.Connection.LocalPort = localPort;
            return next(context);
        });
        app.UseNetBirdPeerAttestationPrivateBoundary(binding);
        app.Run(context =>
        {
            reachedHandler++;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.RequestServices = services;
        context.Request.Path = new PathString(path);
        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        var pipeline = app.Build();
        await pipeline(context);
        return new BoundaryResult(context.Response.StatusCode,
            string.Equals(context.Response.Headers.CacheControl.ToString(), "no-store", StringComparison.OrdinalIgnoreCase),
            reachedHandler);
    }

    private sealed record BoundaryResult(int StatusCode, bool NoStore, int ReachedHandler);

    private sealed class TemporaryDaemon : IDisposable
    {
        private TemporaryDaemon(string directory, string path)
        {
            Directory = directory;
            Path = path;
        }

        public string Directory { get; }
        public string Path { get; }

        public static TemporaryDaemon Create()
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"netbird-listener-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "netbird.exe");
            File.WriteAllText(path, "test executable placeholder");
            return new TemporaryDaemon(directory, path);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
