using System.Net;
using Connector.Access.Client;
using Connector.Access.Contracts;
using Connector.Network;

namespace Connector.Network.Tests;

public sealed class CommonConnectorConnectionCoordinatorTests
{
    private static readonly Uri ManagementUri = new("https://netbird.test");

    [Fact]
    public async Task UnavailableOverlayPreflight_RejectsBeforeOneTimeEnrollment()
    {
        var events = new List<string>();
        var access = new FakeAccess(events, State("connecting"));
        var overlay = new FakeOverlay(events, ReadyOverlay() with
        {
            Status = NetworkServiceStatus.Unknown,
            DiagnosticCode = "netbird_preflight_msi_missing",
        });
        var coordinator = new CommonConnectorConnectionCoordinator(access, overlay, new FakeGate(events));

        var error = await Assert.ThrowsAsync<NetworkGateException>(() =>
            coordinator.EnsureOverlayAvailableAsync(ManagementUri).AsTask());

        Assert.Equal("netbird_preflight_msi_missing", error.Code);
        Assert.Equal(1, overlay.StatusCalls);
        Assert.Empty(events);
    }

    [Fact]
    public async Task ForeignManagementOverlayPreflight_RejectsBeforeOneTimeEnrollment()
    {
        var events = new List<string>();
        var access = new FakeAccess(events, State("connecting"));
        var overlay = new FakeOverlay(events, ReadyOverlay(new Uri("https://foreign-netbird.test")));
        var coordinator = new CommonConnectorConnectionCoordinator(access, overlay, new FakeGate(events));

        var error = await Assert.ThrowsAsync<NetworkGateException>(() =>
            coordinator.EnsureOverlayAvailableAsync(ManagementUri).AsTask());

        Assert.Equal("netbird_management_mismatch", error.Code);
        Assert.Equal(1, overlay.StatusCalls);
        Assert.Empty(events);
    }

    [Fact]
    public async Task LegacyUnpinnedDeployment_RejectsNewTokenOnRegisteredDaemon_ButAllowsResumePreflight()
    {
        var events = new List<string>();
        var overlay = new FakeOverlay(events, ReadyOverlay());
        var coordinator = new CommonConnectorConnectionCoordinator(
            new FakeAccess(events, State("ready")), overlay, new FakeGate(events));

        var error = await Assert.ThrowsAsync<NetworkGateException>(() =>
            coordinator.EnsureOverlayAvailableAsync(null).AsTask());
        Assert.Equal("netbird_management_mismatch", error.Code);
        await coordinator.EnsureOverlayAvailableAsync(null, allowUnpinnedRegistered: true);
        Assert.Empty(events);
    }

    [Fact]
    public async Task AmbiguousUnregisteredOverlayPreflight_RejectsBeforeOneTimeEnrollment()
    {
        var events = new List<string>();
        var access = new FakeAccess(events, State("connecting"));
        var overlay = new FakeOverlay(events, ReadyOverlay() with
        {
            Status = NetworkServiceStatus.Degraded,
            ManagementUri = null,
        });
        var coordinator = new CommonConnectorConnectionCoordinator(access, overlay, new FakeGate(events));

        var error = await Assert.ThrowsAsync<NetworkGateException>(() =>
            coordinator.EnsureOverlayAvailableAsync(ManagementUri).AsTask());

        Assert.Equal("netbird_pre_enrollment_status_unsafe", error.Code);
        Assert.Empty(events);
    }

    [Fact]
    public async Task Enrollment_BootstrapsBeforeProfile_AndReturnsPeerBoundReadyState()
    {
        var events = new List<string>();
        var access = new FakeAccess(events, State("connecting"), State("ready"));
        var overlay = new FakeOverlay(events, ReadyOverlay());
        var coordinator = new CommonConnectorConnectionCoordinator(access, overlay, new FakeGate(events));

        var result = await coordinator.EnrollAndConnectAsync("one-time-token", "Workstation");

        Assert.Equal(CommonConnectorConnectionStatus.Ready, result.Status);
        Assert.Equal(new[] { "enroll", "state", "bootstrap", "connect", "state", "profile" }, events);
        Assert.Equal("one-time-setup-key", overlay.LastBootstrap!.SetupKey);
        Assert.DoesNotContain("one-time-setup-key", result.ToString());
    }

    [Fact]
    public async Task ExistingReadyFromAnotherManagementPlane_IsRejectedWithoutProfileOrRoute()
    {
        var events = new List<string>();
        var access = new FakeAccess(events, State("ready"));
        var wrongOverlay = ReadyOverlay(new Uri("https://other-netbird.test"));
        var coordinator = new CommonConnectorConnectionCoordinator(access, new FakeOverlay(events, wrongOverlay), new FakeGate(events));

        var result = await coordinator.ReconnectAsync();

        Assert.Equal(CommonConnectorConnectionStatus.Unknown, result.Status);
        Assert.Equal("overlay_identity_mismatch", result.DiagnosticCode);
        Assert.Equal(0, access.ProfileCalls);
        Assert.Equal(0, access.BootstrapCalls);
    }

    [Fact]
    public async Task ExistingReadyWithExtraAssignedAddress_IsRejectedAsDifferentPeerIdentity()
    {
        var events = new List<string>();
        var access = new FakeAccess(events, State("ready"));
        var overlay = ReadyOverlay() with
        {
            AssignedInternalAddresses = [IPAddress.Parse("100.90.0.22"), IPAddress.Parse("100.90.0.99")],
        };
        var coordinator = new CommonConnectorConnectionCoordinator(access, new FakeOverlay(events, overlay), new FakeGate(events));

        var result = await coordinator.ReconnectAsync();

        Assert.Equal(CommonConnectorConnectionStatus.Unknown, result.Status);
        Assert.Equal(0, access.ProfileCalls);
    }

    [Fact]
    public async Task ServerConnecting_DoesNotTrustOldLocalReadyAndUsesIssuedBootstrap()
    {
        var events = new List<string>();
        var access = new FakeAccess(events, State("connecting"), State("connecting"));
        var overlay = new FakeOverlay(events, ReadyOverlay());
        var coordinator = new CommonConnectorConnectionCoordinator(access, overlay, new FakeGate(events));

        var result = await coordinator.ReconnectAsync();

        Assert.Equal(CommonConnectorConnectionStatus.AwaitingVpnAuthorization, result.Status);
        Assert.Equal(1, access.BootstrapCalls);
        Assert.Equal(1, overlay.ConnectCalls);
        Assert.Equal(0, access.ProfileCalls);
    }

    [Fact]
    public async Task ReconnectToExactServerIssuedPeer_IsTokenFree()
    {
        var events = new List<string>();
        var access = new FakeAccess(events, State("ready"));
        var overlay = new FakeOverlay(events, ReadyOverlay());
        var coordinator = new CommonConnectorConnectionCoordinator(access, overlay, new FakeGate(events));

        var result = await coordinator.ReconnectAsync();

        Assert.Equal(CommonConnectorConnectionStatus.Ready, result.Status);
        Assert.Equal(0, access.BootstrapCalls);
        Assert.Equal(0, overlay.ConnectCalls);
        Assert.Equal(1, access.ProfileCalls);
    }

    [Fact]
    public async Task ProtectedRoutesForBothModes_RevalidateServerPeerAndProfile()
    {
        var events = new List<string>();
        var access = new FakeAccess(events, State("ready"), State("ready"), State("ready"));
        var overlay = new FakeOverlay(events, ReadyOverlay());
        var gate = new FakeGate(events);
        var coordinator = new CommonConnectorConnectionCoordinator(access, overlay, gate);
        await coordinator.ReconnectAsync();

        using var structura = await coordinator.OpenProtectedServiceAsync("structura", "sync");
        using var platform = await coordinator.OpenProtectedServiceAsync("platform", "jobs");

        Assert.Equal(new[] { "structura", "platform" }, gate.Services);
        Assert.Equal(3, access.StateCalls);
        Assert.Equal(3, access.ProfileCalls);
    }

    [Fact]
    public async Task RevokedState_DeniesOverlayProfileAndRoute()
    {
        var events = new List<string>();
        var access = new FakeAccess(events, State("revoked"));
        var overlay = new FakeOverlay(events, ReadyOverlay());
        var gate = new FakeGate(events);
        var coordinator = new CommonConnectorConnectionCoordinator(access, overlay, gate);

        var result = await coordinator.ReconnectAsync();

        Assert.Equal(CommonConnectorConnectionStatus.Revoked, result.Status);
        Assert.Equal(0, overlay.StatusCalls);
        Assert.Equal(0, access.ProfileCalls);
        await Assert.ThrowsAsync<NetworkGateException>(() =>
            coordinator.OpenProtectedServiceAsync("platform", "jobs").AsTask());
        Assert.Empty(gate.Services);
    }

    [Fact]
    public async Task Disconnect_ClearsAuthorizationWithoutChangingSharedOverlay()
    {
        var events = new List<string>();
        var access = new FakeAccess(events, State("ready"));
        var overlay = new FakeOverlay(events, ReadyOverlay());
        var gate = new FakeGate(events);
        var coordinator = new CommonConnectorConnectionCoordinator(access, overlay, gate);
        await coordinator.ReconnectAsync();
        var overlayCallsBeforeDisconnect = overlay.StatusCalls;

        var disconnected = await coordinator.DisconnectAsync();

        Assert.Equal(CommonConnectorConnectionStatus.Disconnected, disconnected.Status);
        Assert.Null(disconnected.AccessProfile);
        Assert.Null(disconnected.Overlay);
        Assert.Equal(overlayCallsBeforeDisconnect, overlay.StatusCalls);
        Assert.Equal(0, overlay.ConnectCalls);
        await Assert.ThrowsAsync<NetworkGateException>(() =>
            coordinator.OpenProtectedServiceAsync("platform", "jobs").AsTask());
        Assert.Empty(gate.Services);
    }

    [Fact]
    public async Task OverlayChangeBetweenCoordinatorAndGate_IsRejectedBeforeRouteCreation()
    {
        var events = new List<string>();
        var access = new FakeAccess(events, State("ready"), State("ready"));
        var changed = ReadyOverlay(new Uri("https://other-netbird.test"));
        var overlay = new SequenceOverlay(ReadyOverlay(), ReadyOverlay(), changed);
        var gate = RealGate(overlay);
        var coordinator = new CommonConnectorConnectionCoordinator(access, overlay, gate);
        await coordinator.ReconnectAsync();

        var error = await Assert.ThrowsAsync<NetworkGateException>(() =>
            coordinator.OpenProtectedServiceAsync("platform", "jobs").AsTask());

        Assert.Equal("overlay_identity_mismatch", error.Code);
        Assert.Equal(CommonConnectorConnectionStatus.Failed, coordinator.Current.Status);
    }

    [Fact]
    public async Task RouteCreatedBeforeDisconnect_RejectsLaterRequests()
    {
        var events = new List<string>();
        var access = new FakeAccess(events, State("ready"), State("ready"));
        var overlay = new SequenceOverlay(ReadyOverlay(), ReadyOverlay(), ReadyOverlay());
        var coordinator = new CommonConnectorConnectionCoordinator(access, overlay, RealGate(overlay));
        await coordinator.ReconnectAsync();
        using var route = await coordinator.OpenProtectedServiceAsync("platform", "jobs");

        await coordinator.DisconnectAsync();
        var error = await Assert.ThrowsAsync<NetworkGateException>(() => route.Client.GetAsync(route.Uri));

        Assert.Equal("route_authorization_expired", error.Code);
    }

    [Fact]
    public async Task DeviceUnauthorizedClosesAdmissionsWithoutCancelingAcceptedRequest()
    {
        var events = new List<string>();
        var access = new FakeAccess(events, State("ready"), State("ready"));
        var gate = new BlockingAuthorizedGate();
        var coordinator = new CommonConnectorConnectionCoordinator(
            access, new FakeOverlay(events, ReadyOverlay()), gate);
        await coordinator.ReconnectAsync();
        using var admittedRoute = await coordinator.OpenProtectedServiceAsync("platform", "accepted");
        var admittedRequest = admittedRoute.Client.GetAsync(admittedRoute.Uri);
        await gate.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        access.StateFailure = new ConnectorEnrollmentProtocolException(
            "issued device credential rejected",
            (int)HttpStatusCode.Unauthorized,
            "device_unauthorized");

        var error = await Assert.ThrowsAsync<ConnectorEnrollmentProtocolException>(() =>
            coordinator.OpenProtectedServiceAsync("platform", "new-admission").AsTask());

        Assert.Equal("device_unauthorized", error.ErrorCode);
        Assert.Equal(CommonConnectorConnectionStatus.EnrollmentRequired, coordinator.Current.Status);
        Assert.Equal("issued_credential_rejected", coordinator.Current.DiagnosticCode);
        Assert.Equal(1, gate.Admissions);
        Assert.False(admittedRequest.IsCompleted);

        gate.ReleaseRequest.TrySetResult();
        using var admittedResponse = await admittedRequest;
        Assert.Equal(HttpStatusCode.OK, admittedResponse.StatusCode);

        access.StateFailure = null;
        var denied = await Assert.ThrowsAsync<NetworkGateException>(() =>
            coordinator.OpenProtectedServiceAsync("platform", "jobs").AsTask());
        Assert.Equal("common_connection_not_ready", denied.Code);
        Assert.Equal(CommonConnectorConnectionStatus.EnrollmentRequired, coordinator.Current.Status);
        Assert.Equal("issued_credential_rejected", coordinator.Current.DiagnosticCode);
        Assert.Equal(3, access.StateCalls);
        Assert.Equal(1, gate.Admissions);
    }

    [Theory]
    [InlineData("revoked", CommonConnectorConnectionStatus.Revoked, "vpn_revoked")]
    [InlineData("denied", CommonConnectorConnectionStatus.Denied, "vpn_denied")]
    public async Task ReadyToTerminalStatePreservesStatusAndAcceptedRequest(
        string serverStatus,
        CommonConnectorConnectionStatus expectedStatus,
        string expectedDiagnostic)
    {
        var events = new List<string>();
        var access = new FakeAccess(events, State("ready"), State("ready"), State(serverStatus));
        var gate = new BlockingAuthorizedGate();
        var coordinator = new CommonConnectorConnectionCoordinator(
            access, new FakeOverlay(events, ReadyOverlay()), gate);
        Assert.Equal(CommonConnectorConnectionStatus.Ready, (await coordinator.ReconnectAsync()).Status);
        using var admittedRoute = await coordinator.OpenProtectedServiceAsync("platform", "accepted");
        var admittedRequest = admittedRoute.Client.GetAsync(admittedRoute.Uri);
        await gate.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<NetworkGateException>(() =>
            coordinator.OpenProtectedServiceAsync("platform", "new-admission").AsTask());

        Assert.Equal(expectedStatus, coordinator.Current.Status);
        Assert.Equal(expectedDiagnostic, coordinator.Current.DiagnosticCode);
        Assert.Equal(1, gate.Admissions);
        Assert.False(admittedRequest.IsCompleted);

        gate.ReleaseRequest.TrySetResult();
        using var admittedResponse = await admittedRequest;
        Assert.Equal(HttpStatusCode.OK, admittedResponse.StatusCode);

        var denied = await Assert.ThrowsAsync<NetworkGateException>(() =>
            coordinator.OpenProtectedServiceAsync("platform", "still-denied").AsTask());
        Assert.Equal("common_connection_not_ready", denied.Code);
        Assert.Equal(expectedStatus, coordinator.Current.Status);
        Assert.Equal(expectedDiagnostic, coordinator.Current.DiagnosticCode);
        Assert.Equal(3, access.StateCalls);
        Assert.Equal(1, gate.Admissions);
    }

    [Fact]
    public async Task CanceledWaitingOperation_DoesNotInterruptActiveSerializedEnrollment()
    {
        var events = new List<string>();
        var access = new FakeAccess(events, State("ready")) { BlockEnrollment = true };
        var coordinator = new CommonConnectorConnectionCoordinator(
            access, new FakeOverlay(events, ReadyOverlay()), new FakeGate(events));
        var active = coordinator.EnrollAndConnectAsync("token", "Workstation").AsTask();
        await access.EnrollmentStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ReconnectAsync(canceled.Token).AsTask());
        Assert.Equal(CommonConnectorConnectionStatus.Enrolling, coordinator.Current.Status);
        Assert.Equal(0, access.ReceiptCalls);

        access.ReleaseEnrollment.TrySetResult();
        Assert.Equal(CommonConnectorConnectionStatus.Ready, (await active).Status);
    }

    private static ConnectorVpnTransportState State(string status) => new(
        "device-1",
        1,
        status,
        string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase) ? "peer-1" : null,
        ManagementUri.AbsoluteUri.TrimEnd('/'),
        string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase) ? ["100.90.0.22"] : [],
        DateTimeOffset.UtcNow);

    private static NetworkOverlaySnapshot ReadyOverlay(Uri? managementUri = null) => new(
        NetworkServiceStatus.Ready,
        true,
        true,
        true,
        [IPAddress.Parse("100.90.0.22")],
        DateTimeOffset.UtcNow,
        "ready",
        managementUri ?? ManagementUri);

    private static ProtectedServiceNetworkGate RealGate(INetworkOverlayClient overlay) => new(
        overlay,
        new TrueProbe(),
        [new ProtectedServiceDefinition("platform", new Uri("https://platform.internal/"), ["100.64.0.10"])],
        new NeverDnsResolver(),
        new NeverSocketConnector());

    private sealed class FakeAccess(List<string> events, params ConnectorVpnTransportState[] states)
        : IConnectorEnrollmentClient, IConnectorDeviceAccessClient
    {
        private readonly Queue<ConnectorVpnTransportState> _states = new(states);
        public int BootstrapCalls { get; private set; }
        public int ProfileCalls { get; private set; }
        public int StateCalls { get; private set; }
        public int ReceiptCalls { get; private set; }
        public bool BlockEnrollment { get; init; }
        public Exception? StateFailure { get; set; }
        public TaskCompletionSource EnrollmentStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseEnrollment { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<DeviceEnrollmentResponse> EnrollAsync(
            string enrollmentToken,
            string deviceDisplayName,
            CancellationToken cancellationToken = default)
        {
            events.Add("enroll");
            EnrollmentStarted.TrySetResult();
            if (BlockEnrollment) await ReleaseEnrollment.Task.WaitAsync(cancellationToken);
            return Receipt();
        }

        public ValueTask<DeviceEnrollmentResponse> ResumeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Receipt());

        public ValueTask<DeviceEnrollmentResponse?> GetReceiptAsync(CancellationToken cancellationToken = default)
        {
            ReceiptCalls++;
            return ValueTask.FromResult<DeviceEnrollmentResponse?>(Receipt());
        }

        public ValueTask<DeviceAccessProfile> GetAccessProfileAsync(CancellationToken cancellationToken = default)
        {
            events.Add("profile");
            ProfileCalls++;
            return ValueTask.FromResult(new DeviceAccessProfile(
                DeviceAccessProtocol.Version, "device-1", "user-1", "company-1", 1, 1,
                DateTimeOffset.UtcNow.AddHours(1),
                [new ModuleGrant(ConnectorProduct.Structura, "sync", [ConnectorPermission.Read])], []));
        }

        public ValueTask<ConnectorVpnBootstrap> GetVpnBootstrapAsync(CancellationToken cancellationToken = default)
        {
            events.Add("bootstrap");
            BootstrapCalls++;
            return ValueTask.FromResult(new ConnectorVpnBootstrap(
                "one-time-setup-key", ManagementUri.AbsoluteUri, 1, DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public ValueTask<ConnectorVpnTransportState> GetVpnStateAsync(CancellationToken cancellationToken = default)
        {
            events.Add("state");
            StateCalls++;
            if (StateFailure is not null)
                return ValueTask.FromException<ConnectorVpnTransportState>(StateFailure);
            return ValueTask.FromResult(_states.Dequeue());
        }

        private static DeviceEnrollmentResponse Receipt() => new(
            DeviceAccessProtocol.Version, Guid.NewGuid(), "device-1", "certificate", "issuer",
            DateTimeOffset.UtcNow.AddHours(1), 1);
    }

    private sealed class FakeOverlay(List<string> events, NetworkOverlaySnapshot snapshot) : INetworkOverlayClient
    {
        public int ConnectCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public NetworkOverlayBootstrap? LastBootstrap { get; private set; }

        public ValueTask<NetworkOverlaySnapshot> ConnectAsync(
            NetworkOverlayBootstrap bootstrap,
            CancellationToken cancellationToken = default)
        {
            events.Add("connect");
            ConnectCalls++;
            LastBootstrap = bootstrap;
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask<NetworkOverlaySnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            StatusCalls++;
            return ValueTask.FromResult(snapshot);
        }
    }

    private sealed class SequenceOverlay(params NetworkOverlaySnapshot[] snapshots) : INetworkOverlayClient
    {
        private readonly Queue<NetworkOverlaySnapshot> _snapshots = new(snapshots);
        private NetworkOverlaySnapshot _last = snapshots[^1];

        public ValueTask<NetworkOverlaySnapshot> ConnectAsync(
            NetworkOverlayBootstrap bootstrap,
            CancellationToken cancellationToken = default) => GetStatusAsync(cancellationToken);

        public ValueTask<NetworkOverlaySnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            if (_snapshots.Count > 0) _last = _snapshots.Dequeue();
            return ValueTask.FromResult(_last);
        }
    }

    private sealed class TrueProbe : IProtectedServiceProbe
    {
        public ValueTask<bool> IsOnlineAsync(HttpClient client, Uri healthUri, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }

    private sealed class NeverDnsResolver : IProtectedDnsResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            ValueTask.FromException<IReadOnlyList<IPAddress>>(new InvalidOperationException("DNS must not be reached."));
    }

    private sealed class NeverSocketConnector : IOverlaySocketConnector
    {
        public ValueTask<Stream> ConnectAsync(
            IPAddress localAddress,
            IPAddress remoteAddress,
            int port,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new InvalidOperationException("Socket must not be reached."));
    }

    private sealed class BlockingAuthorizedGate : IProtectedServiceNetworkGate
    {
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Admissions { get; private set; }

        public ValueTask<ProtectedServiceRoute> ResolveAndProbeAsync(
            ProtectedOverlayTransportIdentity identity,
            string serviceId,
            string relativeUri,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Admissions++;
            var inner = new BlockingResponseHandler(RequestStarted, ReleaseRequest);
            var lifetime = new AuthorizationLifetimeHandler(identity.AuthorizationLifetime) { InnerHandler = inner };
            var client = new HttpClient(lifetime, disposeHandler: true);
            return ValueTask.FromResult(new ProtectedServiceRoute(
                serviceId,
                new Uri("https://platform.internal/" + relativeUri),
                ReadyOverlay(),
                client));
        }
    }

    private sealed class BlockingResponseHandler(
        TaskCompletionSource started,
        TaskCompletionSource release) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        }
    }

    private sealed class FakeGate(List<string> events) : IProtectedServiceNetworkGate
    {
        public List<string> Services { get; } = [];

        public ValueTask<ProtectedServiceRoute> ResolveAndProbeAsync(
            ProtectedOverlayTransportIdentity identity,
            string serviceId,
            string relativeUri,
            CancellationToken cancellationToken = default)
        {
            events.Add("route:" + serviceId);
            Services.Add(serviceId);
            return ValueTask.FromResult(new ProtectedServiceRoute(
                serviceId,
                new Uri("https://" + serviceId + ".internal/" + relativeUri),
                ReadyOverlay(),
                new HttpClient()));
        }
    }
}
