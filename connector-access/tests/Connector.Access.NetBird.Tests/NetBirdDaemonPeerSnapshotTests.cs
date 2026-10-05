using System.Text;
using System.Text.Json;
using Connector.Access.NetBird;

namespace Connector.Access.NetBird.Tests;

public sealed class NetBirdDaemonPeerSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly string ExpectedKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
    private static readonly string OtherKey = Convert.ToBase64String(Enumerable.Repeat((byte)0xa5, 32).ToArray());

    [Fact]
    public async Task MalformedDaemonJson_ReturnsTypedUnavailable()
    {
        var reader = CreateReader(Encoding.UTF8.GetBytes("{\"peers\":{\"details\":[{\"netbirdIp\":42}]}}"));

        var result = await reader.ReadAsync("100.90.0.22", ExpectedKey);

        var unavailable = Assert.IsType<NetBirdDaemonPeerUnavailable>(result);
        Assert.Equal(NetBirdDaemonPeerUnavailableReason.MalformedOutput, unavailable.Reason);
    }

    [Fact]
    public async Task OversizedDaemonJson_ReturnsTypedUnavailableBeforeParsing()
    {
        var runner = new StubCommandRunner(new NetBirdDaemonCommandResult(
            NetBirdDaemonCommandStatus.Success,
            new byte[65]));
        var reader = CreateReader(runner, maximumOutputBytes: 64);

        var result = await reader.ReadAsync("100.90.0.22", ExpectedKey);

        var unavailable = Assert.IsType<NetBirdDaemonPeerUnavailable>(result);
        Assert.Equal(NetBirdDaemonPeerUnavailableReason.OutputTooLarge, unavailable.Reason);
    }

    [Fact]
    public async Task DuplicateExactIpAndKey_ReturnsAmbiguous()
    {
        var peer = Peer("100.90.0.22", null, ExpectedKey, "Connected", Now.AddSeconds(-5));
        var reader = CreateReader(StatusJson(peer, peer));

        var result = await reader.ReadAsync("100.90.0.22", ExpectedKey);

        var ambiguous = Assert.IsType<NetBirdDaemonPeerAmbiguous>(result);
        Assert.Equal(2, ambiguous.ConflictingPeerCount);
    }

    [Fact]
    public async Task DuplicateIpv4WithDifferentKeys_ReturnsAmbiguous()
    {
        var reader = CreateReader(StatusJson(
            Peer("100.90.0.22", null, ExpectedKey, "Connected", Now.AddSeconds(-5)),
            Peer("100.90.0.22", null, OtherKey, "Connected", Now.AddSeconds(-5))));

        var result = await reader.ReadAsync("100.90.0.22", ExpectedKey);

        var ambiguous = Assert.IsType<NetBirdDaemonPeerAmbiguous>(result);
        Assert.Equal(2, ambiguous.ConflictingPeerCount);
    }

    [Fact]
    public async Task DuplicateIpv6WithDifferentKeys_ReturnsAmbiguous()
    {
        var reader = CreateReader(StatusJson(
            Peer("100.90.0.22", "fd00:1::22", ExpectedKey, "Connected", Now.AddSeconds(-5)),
            Peer("100.90.0.23", "fd00:1::22", OtherKey, "Connected", Now.AddSeconds(-5))));

        var result = await reader.ReadAsync("fd00:1::22", ExpectedKey);

        var ambiguous = Assert.IsType<NetBirdDaemonPeerAmbiguous>(result);
        Assert.Equal(2, ambiguous.ConflictingPeerCount);
    }

    [Fact]
    public async Task ExpectedKeyRepeatedOnAnotherIp_ReturnsAmbiguous()
    {
        var reader = CreateReader(StatusJson(
            Peer("100.90.0.22", null, ExpectedKey, "Connected", Now.AddSeconds(-5)),
            Peer("100.90.0.23", null, ExpectedKey, "Connected", Now.AddSeconds(-5))));

        var result = await reader.ReadAsync("100.90.0.22", ExpectedKey);

        var ambiguous = Assert.IsType<NetBirdDaemonPeerAmbiguous>(result);
        Assert.Equal(2, ambiguous.ConflictingPeerCount);
    }

    [Fact]
    public async Task IpAndKeyOnDifferentPeers_DoNotComposeEvidence()
    {
        var reader = CreateReader(StatusJson(
            Peer("100.90.0.22", null, OtherKey, "Connected", Now.AddSeconds(-5)),
            Peer("100.90.0.23", null, ExpectedKey, "Connected", Now.AddSeconds(-5))));

        var result = await reader.ReadAsync("100.90.0.22", ExpectedKey);

        var unavailable = Assert.IsType<NetBirdDaemonPeerUnavailable>(result);
        Assert.Equal(NetBirdDaemonPeerUnavailableReason.ExactPeerNotFound, unavailable.Reason);
    }

    [Fact]
    public async Task DisconnectedExactPeer_ReturnsStale()
    {
        var reader = CreateReader(StatusJson(
            Peer("100.90.0.22", null, ExpectedKey, "Disconnected", Now.AddSeconds(-5))));

        var result = await reader.ReadAsync("100.90.0.22", ExpectedKey);

        var stale = Assert.IsType<NetBirdDaemonPeerStale>(result);
        Assert.Equal(NetBirdDaemonPeerStaleReason.NotConnected, stale.Reason);
    }

    [Fact]
    public async Task ExpiredHandshake_ReturnsStale()
    {
        var reader = CreateReader(StatusJson(
            Peer("100.90.0.22", null, ExpectedKey, "Connected", Now.AddMinutes(-3))));

        var result = await reader.ReadAsync("100.90.0.22", ExpectedKey);

        var stale = Assert.IsType<NetBirdDaemonPeerStale>(result);
        Assert.Equal(NetBirdDaemonPeerStaleReason.ExpiredHandshake, stale.Reason);
    }

    [Fact]
    public async Task HealthyIpv4Peer_ReturnsExactReadOnlyEvidence()
    {
        var handshake = Now.AddSeconds(-5);
        var reader = CreateReader(StatusJson(
            Peer("100.90.0.22", "fd00:1::22", ExpectedKey, "Connected", handshake)));

        var result = await reader.ReadAsync("100.90.0.22", ExpectedKey);

        var exact = Assert.IsType<NetBirdDaemonPeerExact>(result);
        Assert.Equal("100.90.0.22", exact.Evidence.NetBirdIp);
        Assert.Equal("fd00:1::22", exact.Evidence.NetBirdIpv6);
        Assert.Equal(ExpectedKey, exact.Evidence.PublicKey);
        Assert.Equal("Connected", exact.Evidence.Status);
        Assert.Equal(handshake, exact.Evidence.LastWireguardHandshake);
    }

    [Fact]
    public async Task HealthyIpv6Peer_ReturnsSamePeerEvidence()
    {
        var reader = CreateReader(StatusJson(
            Peer("100.90.0.22", "fd00:1::22", ExpectedKey, "Connected", Now.AddSeconds(-5))));

        var result = await reader.ReadAsync("fd00:1:0:0:0:0:0:22", ExpectedKey);

        var exact = Assert.IsType<NetBirdDaemonPeerExact>(result);
        Assert.Equal("100.90.0.22", exact.Evidence.NetBirdIp);
        Assert.Equal("fd00:1::22", exact.Evidence.NetBirdIpv6);
    }

    [Fact]
    public async Task InvalidWireGuardKeyInDaemonJson_FailsClosed()
    {
        var reader = CreateReader(StatusJson(
            Peer("100.90.0.22", null, "not-a-wireguard-key", "Connected", Now.AddSeconds(-5))));

        var result = await reader.ReadAsync("100.90.0.22", ExpectedKey);

        var unavailable = Assert.IsType<NetBirdDaemonPeerUnavailable>(result);
        Assert.Equal(NetBirdDaemonPeerUnavailableReason.MalformedOutput, unavailable.Reason);
    }

    [Fact]
    public async Task ReaderPassesPinnedExecutableAndOfficialArgumentsToProcessBoundary()
    {
        var executable = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trusted-netbird", "netbird"));
        var runner = new StubCommandRunner(new NetBirdDaemonCommandResult(
            NetBirdDaemonCommandStatus.Success,
            StatusJson(Peer("100.90.0.22", null, ExpectedKey, "Connected", Now.AddSeconds(-5)))));
        var reader = CreateReader(runner, executablePath: executable);

        var result = await reader.ReadAsync("100.90.0.22", ExpectedKey);

        Assert.IsType<NetBirdDaemonPeerExact>(result);
        Assert.NotNull(runner.Command);
        Assert.Equal(executable, runner.Command!.ExecutablePath);
        Assert.Equal(new[] { "status", "--json" }, runner.Command.Arguments);
    }

    private static NetBirdDaemonPeerSnapshotReader CreateReader(byte[] json) =>
        CreateReader(new StubCommandRunner(new NetBirdDaemonCommandResult(NetBirdDaemonCommandStatus.Success, json)));

    private static NetBirdDaemonPeerSnapshotReader CreateReader(
        StubCommandRunner runner,
        int maximumOutputBytes = 1024 * 1024,
        string? executablePath = null) =>
        new(
            new NetBirdDaemonPeerSnapshotOptions
            {
                ExecutablePath = executablePath ?? Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trusted-netbird", "netbird")),
                MaximumOutputBytes = maximumOutputBytes,
                MaximumHandshakeAge = TimeSpan.FromMinutes(2),
                CommandTimeout = TimeSpan.FromSeconds(5)
            },
            new FixedTimeProvider(Now),
            runner);

    private static byte[] StatusJson(params object[] peers) =>
        JsonSerializer.SerializeToUtf8Bytes(new { peers = new { details = peers } });

    private static object Peer(
        string ipv4,
        string? ipv6,
        string publicKey,
        string status,
        DateTimeOffset handshake)
    {
        var peer = new Dictionary<string, object>
        {
            ["netbirdIp"] = ipv4,
            ["publicKey"] = publicKey,
            ["status"] = status,
            ["lastWireguardHandshake"] = handshake
        };
        if (ipv6 is not null)
        {
            peer["netbirdIpv6"] = ipv6;
        }
        return peer;
    }

    private sealed class StubCommandRunner(NetBirdDaemonCommandResult result) : INetBirdDaemonCommandRunner
    {
        public NetBirdDaemonCommand? Command { get; private set; }

        public ValueTask<NetBirdDaemonCommandResult> RunAsync(
            NetBirdDaemonCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Command = command;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
