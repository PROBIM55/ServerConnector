using Connector.Access.Contracts;

namespace Connector.Access.NetBird.Tests;

public sealed class NetBirdPeerAttestationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private const string ListenerIpv4 = "100.90.0.1";
    private const string RemoteIpv4 = "100.90.0.22";
    private const string ListenerIpv6 = "fd00:1::1";
    private const string RemoteIpv6 = "fd00:1::22";
    private const string PublicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string Nonce = "AAECAwQFBgcICQoLDA0ODw";
    private const string CertificateSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(ListenerIpv4, RemoteIpv4)]
    [InlineData(ListenerIpv6, RemoteIpv6)]
    public async Task HealthyExactPeer_ReturnsShortLivedBoundAttestation(string listenerIp, string remoteIp)
    {
        var state = ReadyState(addresses: [remoteIp]);
        var snapshot = new StubPeerSnapshotReader(Exact(remoteIp));
        var stateReader = new StubVpnStateReader(Success(state));
        var service = CreateService(listenerIp, stateReader, snapshot);

        var result = await service.AttestAsync(Request(listenerIp, remoteIp));

        var accepted = Assert.IsType<NetBirdPeerAttestationAccepted>(result);
        Assert.Equal(Nonce, accepted.Attestation.Nonce);
        Assert.Equal("device-1", accepted.Attestation.DeviceId);
        Assert.Equal(7, accepted.Attestation.AccessRevision);
        Assert.Equal(CertificateSha256, accepted.Attestation.CertificateSha256);
        Assert.Equal("peer-1", accepted.Attestation.PeerId);
        Assert.Equal(remoteIp, accepted.Attestation.SourceIp);
        Assert.Equal(listenerIp, accepted.Attestation.ListenerIp);
        Assert.Equal(PublicKey, accepted.Attestation.WireGuardPublicKey);
        Assert.Equal(Now, accepted.Attestation.IssuedAtUtc);
        Assert.Equal(Now.AddSeconds(30), accepted.Attestation.ExpiresAtUtc);
        Assert.Equal(2, stateReader.CallCount);
        Assert.Equal(remoteIp, snapshot.ExpectedIp);
        Assert.Equal(PublicKey, snapshot.ExpectedPublicKey);
    }

    [Fact]
    public async Task ListenerAndSourceMustMatchObservedSocketAndCurrentAssignedAddress()
    {
        var reader = new StubVpnStateReader(Success(ReadyState(addresses: [RemoteIpv4])));
        var snapshot = new StubPeerSnapshotReader(Exact(RemoteIpv4));
        var service = CreateService(ListenerIpv4, reader, snapshot);

        var wrongLocal = await service.AttestAsync(Request("100.90.0.2", RemoteIpv4));
        var wrongRemote = await service.AttestAsync(Request(ListenerIpv4, "100.90.0.23"));

        Assert.Equal(NetBirdPeerAttestationFailureReason.ListenerIpMismatch, Rejection(wrongLocal).Reason);
        Assert.Equal(NetBirdPeerAttestationFailureReason.SourceIpMismatch, Rejection(wrongRemote).Reason);
        Assert.Equal(0, snapshot.CallCount);
    }

    [Fact]
    public async Task DeviceRevisionAndReadyStateMustExactlyMatchAuthentication()
    {
        var states = new[]
        {
            ReadyState() with { DeviceId = "device-2" },
            ReadyState() with { Revision = 8 },
            ReadyState() with { Status = "connecting" }
        };

        foreach (var state in states)
        {
            var result = await CreateService(
                ListenerIpv4,
                new StubVpnStateReader(Success(state)),
                new StubPeerSnapshotReader(Exact(RemoteIpv4)))
                .AttestAsync(Request(ListenerIpv4, RemoteIpv4));

            Assert.Equal(NetBirdPeerAttestationFailureReason.TransportStateMismatch, Rejection(result).Reason);
        }
    }

    [Fact]
    public async Task NonExactSnapshotAndSnapshotExceptionFailClosed()
    {
        var stateReader = new StubVpnStateReader(Success(ReadyState()));
        var ambiguous = await CreateService(
            ListenerIpv4,
            stateReader,
            new StubPeerSnapshotReader(new NetBirdDaemonPeerAmbiguous(2)))
            .AttestAsync(Request(ListenerIpv4, RemoteIpv4));
        var exception = await CreateService(
            ListenerIpv4,
            stateReader,
            new StubPeerSnapshotReader(new InvalidOperationException("sensitive daemon error")))
            .AttestAsync(Request(ListenerIpv4, RemoteIpv4));

        Assert.Equal(NetBirdPeerAttestationFailureReason.PeerSnapshotNotExact, Rejection(ambiguous).Reason);
        Assert.Equal(NetBirdPeerAttestationFailureReason.DependencyUnavailable, Rejection(exception).Reason);
    }

    [Fact]
    public async Task ExactResultWithMismatchedOrStaleEvidenceFailsClosed()
    {
        var wrongKey = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=";
        var evidence = new[]
        {
            Exact("100.90.0.23"),
            Exact(RemoteIpv4, publicKey: wrongKey),
            Exact(RemoteIpv4, status: "Disconnected"),
            Exact(RemoteIpv4, handshake: Now.AddMinutes(-2)),
            Exact(RemoteIpv4, handshake: Now.AddSeconds(1))
        };

        foreach (var exact in evidence)
        {
            var result = await CreateService(
                ListenerIpv4,
                new StubVpnStateReader(Success(ReadyState())),
                new StubPeerSnapshotReader(exact))
                .AttestAsync(Request(ListenerIpv4, RemoteIpv4));

            Assert.Equal(NetBirdPeerAttestationFailureReason.PeerSnapshotNotExact, Rejection(result).Reason);
        }
    }

    [Fact]
    public async Task ProviderStateIsRecheckedAfterAwaitedSnapshot()
    {
        var revokedReader = new StubVpnStateReader(
            Success(ReadyState()),
            Success(ReadyState() with { Status = "revoked", PeerId = null, ExpectedAssignedAddresses = [] }));
        var revoked = await CreateService(
            ListenerIpv4,
            revokedReader,
            new AwaitedPeerSnapshotReader(Exact(RemoteIpv4)))
            .AttestAsync(Request(ListenerIpv4, RemoteIpv4));

        var changedReader = new StubVpnStateReader(
            Success(ReadyState()),
            Success(ReadyState(addresses: ["100.90.0.23"])));
        var changed = await CreateService(
            ListenerIpv4,
            changedReader,
            new AwaitedPeerSnapshotReader(Exact(RemoteIpv4)))
            .AttestAsync(Request(ListenerIpv4, RemoteIpv4));

        var exceptionReader = new StubVpnStateReader(
            Success(ReadyState()),
            new InvalidOperationException("recheck unavailable"));
        var exception = await CreateService(
            ListenerIpv4,
            exceptionReader,
            new AwaitedPeerSnapshotReader(Exact(RemoteIpv4)))
            .AttestAsync(Request(ListenerIpv4, RemoteIpv4));

        Assert.Equal(NetBirdPeerAttestationFailureReason.TransportStateMismatch, Rejection(revoked).Reason);
        Assert.Equal(NetBirdPeerAttestationFailureReason.TransportStateMismatch, Rejection(changed).Reason);
        Assert.Equal(NetBirdPeerAttestationFailureReason.DependencyUnavailable, Rejection(exception).Reason);
        Assert.Equal(2, revokedReader.CallCount);
        Assert.Equal(2, changedReader.CallCount);
        Assert.Equal(2, exceptionReader.CallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("AAECAwQFBgcICQoLDA0ODw==")]
    [InlineData("AAECAwQFBgcICQoLDA0OD+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task NonceMustBeBoundedCanonicalBase64Url(string nonce)
    {
        var snapshot = new StubPeerSnapshotReader(Exact(RemoteIpv4));
        var result = await CreateService(
            ListenerIpv4,
            new StubVpnStateReader(Success(ReadyState())),
            snapshot)
            .AttestAsync(Request(ListenerIpv4, RemoteIpv4) with { Nonce = nonce });

        Assert.Equal(NetBirdPeerAttestationFailureReason.InvalidRequest, Rejection(result).Reason);
        Assert.Equal(0, snapshot.CallCount);
    }

    [Fact]
    public async Task MalformedIpAndNonCanonicalKeyAreRejectedBeforeDependencies()
    {
        var stateReader = new StubVpnStateReader(Success(ReadyState()));
        var snapshot = new StubPeerSnapshotReader(Exact(RemoteIpv4));
        var service = CreateService(ListenerIpv4, stateReader, snapshot);

        var zonedIp = await service.AttestAsync(Request("fe80::1%4", RemoteIpv4));
        var cidrIp = await service.AttestAsync(Request(ListenerIpv4, "100.90.0.22/32"));
        var key = await service.AttestAsync(Request(ListenerIpv4, RemoteIpv4) with
        {
            ClaimedWireGuardPublicKey = "not-a-key"
        });

        Assert.Equal(NetBirdPeerAttestationFailureReason.InvalidRequest, Rejection(zonedIp).Reason);
        Assert.Equal(NetBirdPeerAttestationFailureReason.InvalidRequest, Rejection(cidrIp).Reason);
        Assert.Equal(NetBirdPeerAttestationFailureReason.InvalidRequest, Rejection(key).Reason);
        Assert.Equal(0, stateReader.CallCount);
        Assert.Equal(0, snapshot.CallCount);
    }

    [Fact]
    public async Task StaleAndFutureTransportProfilesFailClosed()
    {
        var stale = await CreateService(
            ListenerIpv4,
            new StubVpnStateReader(Success(ReadyState() with { ObservedAtUtc = Now.AddMinutes(-2) })),
            new StubPeerSnapshotReader(Exact(RemoteIpv4)))
            .AttestAsync(Request(ListenerIpv4, RemoteIpv4));
        var future = await CreateService(
            ListenerIpv4,
            new StubVpnStateReader(Success(ReadyState() with { ObservedAtUtc = Now.AddSeconds(6) })),
            new StubPeerSnapshotReader(Exact(RemoteIpv4)))
            .AttestAsync(Request(ListenerIpv4, RemoteIpv4));

        Assert.Equal(NetBirdPeerAttestationFailureReason.TransportStateStale, Rejection(stale).Reason);
        Assert.Equal(NetBirdPeerAttestationFailureReason.TransportStateStale, Rejection(future).Reason);
    }

    [Fact]
    public async Task DependencyFailureCannotProduceAttestation()
    {
        var result = await CreateService(
            ListenerIpv4,
            new StubVpnStateReader(new InvalidOperationException("provider unavailable")),
            new StubPeerSnapshotReader(Exact(RemoteIpv4)))
            .AttestAsync(Request(ListenerIpv4, RemoteIpv4));

        Assert.Equal(NetBirdPeerAttestationFailureReason.DependencyUnavailable, Rejection(result).Reason);
    }

    private static NetBirdPeerAttestationService CreateService(
        string listenerIp,
        IConnectorVpnBootstrapReader stateReader,
        INetBirdDaemonPeerSnapshotReader snapshotReader) =>
        new(
            new NetBirdPeerAttestationOptions
            {
                ExpectedServerOverlayListenerIp = listenerIp,
                MaximumTransportStateAge = TimeSpan.FromMinutes(2),
                MaximumPeerHandshakeAge = TimeSpan.FromMinutes(2),
                MaximumFutureClockSkew = TimeSpan.FromSeconds(5),
                AttestationLifetime = TimeSpan.FromSeconds(30)
            },
            stateReader,
            snapshotReader,
            new FixedTimeProvider(Now));

    private static NetBirdPeerAttestationRequest Request(string localIp, string remoteIp) => new(
        Device(),
        localIp,
        remoteIp,
        PublicKey,
        Nonce);

    private static AuthenticatedDevice Device() =>
        new("device-1", "user-1", "company-1", CertificateSha256, 7);

    private static ConnectorVpnTransportState ReadyState(IReadOnlyList<string>? addresses = null) =>
        new("device-1", 7, "ready", "peer-1", "https://netbird.example", addresses ?? [RemoteIpv4], Now);

    private static DeviceAccessResult<ConnectorVpnTransportState> Success(ConnectorVpnTransportState state) =>
        DeviceAccessResult<ConnectorVpnTransportState>.Success(state);

    private static NetBirdDaemonPeerExact Exact(
        string remoteIp,
        string? publicKey = null,
        string status = "Connected",
        DateTimeOffset? handshake = null) =>
        new(new NetBirdDaemonPeerEvidence(
            remoteIp.Contains(':') ? RemoteIpv4 : remoteIp,
            remoteIp.Contains(':') ? "fd00:1:0:0:0:0:0:22" : null,
            publicKey ?? PublicKey,
            status,
            handshake ?? Now.AddSeconds(-5)));

    private static NetBirdPeerAttestationRejected Rejection(NetBirdPeerAttestationResult result) =>
        Assert.IsType<NetBirdPeerAttestationRejected>(result);

    private sealed class StubVpnStateReader : IConnectorVpnBootstrapReader
    {
        private readonly object[] _steps;

        public StubVpnStateReader(params object[] steps) => _steps = steps;

        public int CallCount { get; private set; }

        public ValueTask<DeviceAccessResult<ConnectorVpnBootstrap>> GetAsync(
            AuthenticatedDevice device,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(DeviceAccessResult<ConnectorVpnBootstrap>.Fail("unused", "unused"));

        public ValueTask<DeviceAccessResult<ConnectorVpnTransportState>> GetStateAsync(
            AuthenticatedDevice device,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var step = _steps[Math.Min(CallCount - 1, _steps.Length - 1)];
            if (step is Exception exception)
                throw exception;
            return ValueTask.FromResult((DeviceAccessResult<ConnectorVpnTransportState>)step);
        }
    }

    private sealed class AwaitedPeerSnapshotReader(NetBirdDaemonPeerSnapshotResult result)
        : INetBirdDaemonPeerSnapshotReader
    {
        public async ValueTask<NetBirdDaemonPeerSnapshotResult> ReadAsync(
            string expectedIp,
            string expectedPublicKey,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }

    private sealed class StubPeerSnapshotReader : INetBirdDaemonPeerSnapshotReader
    {
        private readonly NetBirdDaemonPeerSnapshotResult? _result;
        private readonly Exception? _exception;

        public StubPeerSnapshotReader(NetBirdDaemonPeerSnapshotResult result) => _result = result;
        public StubPeerSnapshotReader(Exception exception) => _exception = exception;

        public int CallCount { get; private set; }
        public string? ExpectedIp { get; private set; }
        public string? ExpectedPublicKey { get; private set; }

        public ValueTask<NetBirdDaemonPeerSnapshotResult> ReadAsync(
            string expectedIp,
            string expectedPublicKey,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ExpectedIp = expectedIp;
            ExpectedPublicKey = expectedPublicKey;
            if (_exception is not null)
                throw _exception;
            return ValueTask.FromResult(_result!);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
