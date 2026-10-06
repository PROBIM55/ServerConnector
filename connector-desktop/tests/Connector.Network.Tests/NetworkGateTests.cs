using System.Net;
using Connector.Network;

namespace Connector.Network.Tests;

public sealed class NetworkGateTests
{
    [Fact]
    public async Task ReadyOverlay_ResolvesOnlyConfiguredInternalService()
    {
        var overlay = new FixedOverlay(Ready());
        var probe = new RecordingProbe(true);
        var gate = new ProtectedServiceNetworkGate(
            overlay,
            probe,
            [new ProtectedServiceDefinition("platform", new Uri("https://platform.internal/api/"), ["10.64.0.10"], "health")]);

        using var route = await gate.ResolveAndProbeAsync(Identity(Ready()), "platform", "jobs/42");

        Assert.Equal("https://platform.internal/api/jobs/42", route.Uri.AbsoluteUri);
        Assert.Equal("https://platform.internal/api/health", probe.LastUri!.AbsoluteUri);
        await Assert.ThrowsAsync<NetworkGateException>(() =>
            gate.ResolveAndProbeAsync(Identity(Ready()), "platform", "../public").AsTask());
        await Assert.ThrowsAsync<NetworkGateException>(() =>
            gate.ResolveAndProbeAsync(Identity(Ready()), "unknown", "jobs/42").AsTask());
    }

    [Fact]
    public async Task UnknownOverlay_FailsClosedBeforeAnyServiceProbe()
    {
        var overlay = new FixedOverlay(new NetworkOverlaySnapshot(
            NetworkServiceStatus.Unknown, false, false, false, [], DateTimeOffset.UtcNow, "unknown"));
        var probe = new RecordingProbe(true);
        var gate = new ProtectedServiceNetworkGate(
            overlay,
            probe,
            [new ProtectedServiceDefinition("structura", new Uri("https://structura.internal/"), ["100.90.0.5"])]);

        var error = await Assert.ThrowsAsync<NetworkGateException>(() =>
            gate.ResolveAndProbeAsync(Identity(Ready()), "structura", "sync").AsTask());

        Assert.Equal("overlay_not_ready", error.Code);
        Assert.Null(probe.LastUri);
    }

    [Fact]
    public async Task OfflineProtectedService_HasNoDirectFallbackRoute()
    {
        var gate = new ProtectedServiceNetworkGate(
            new FixedOverlay(Ready()),
            new RecordingProbe(false),
            [new ProtectedServiceDefinition("structura", new Uri("https://structura.internal/"), ["100.90.0.5"])]);

        var error = await Assert.ThrowsAsync<NetworkGateException>(() =>
            gate.ResolveAndProbeAsync(Identity(Ready()), "structura", "sync").AsTask());

        Assert.Equal("service_offline", error.Code);
    }

    [Fact]
    public async Task RouteWithoutServerIssuedTransportIdentity_IsRejected()
    {
        var gate = new ProtectedServiceNetworkGate(
            new FixedOverlay(Ready()),
            new RecordingProbe(true),
            [new ProtectedServiceDefinition("platform", new Uri("https://platform.internal/"), ["100.64.0.10"])]);

        var error = await Assert.ThrowsAsync<NetworkGateException>(() =>
            gate.ResolveAndProbeAsync("platform", "jobs").AsTask());

        Assert.Equal("server_transport_identity_required", error.Code);
    }

    private static NetworkOverlaySnapshot Ready() => new(
        NetworkServiceStatus.Ready,
        true,
        true,
        true,
        [IPAddress.Parse("100.90.0.22")],
        DateTimeOffset.UtcNow,
        "ready",
        new Uri("https://netbird.test"));

    private static ProtectedOverlayTransportIdentity Identity(NetworkOverlaySnapshot snapshot) => new(
        "device-1",
        1,
        "peer-1",
        new Uri("https://netbird.test"),
        snapshot.AssignedInternalAddresses,
        new CancellationTokenSource().Token);

    private sealed class FixedOverlay(NetworkOverlaySnapshot snapshot) : INetworkOverlayClient
    {
        public ValueTask<NetworkOverlaySnapshot> ConnectAsync(NetworkOverlayBootstrap bootstrap, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(snapshot);
        public ValueTask<NetworkOverlaySnapshot> GetStatusAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(snapshot);
    }

    private sealed class RecordingProbe(bool result) : IProtectedServiceProbe
    {
        public Uri? LastUri { get; private set; }
        public ValueTask<bool> IsOnlineAsync(HttpClient client, Uri healthUri, CancellationToken cancellationToken)
        {
            LastUri = healthUri;
            return ValueTask.FromResult(result);
        }
    }
}
