using System.Net;
using System.Reflection;
using System.Text;
using Connector.Access.Client;
using Connector.Access.Contracts;
using Connector.Desktop.Services;
using Connector.Network;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class CommonAccessRequestTransportTests
{
    private const int TwoMiB = 2 * 1024 * 1024;

    [Fact]
    public async Task Send_SerializesCamelCaseAndOnlyManagedSession_ThenDetachesResponse()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new UnknownLengthContent(Encoding.UTF8.GetBytes("detached-response"), "application/json"),
        });
        var fixture = await RuntimeFixture.CreateAsync(handler);

        using var response = await fixture.Runtime.SendAsync(
            HttpMethod.Post,
            "jobs/session",
            new { SchemaVersion = 1, AgentType = "platform-cad-connector" },
            new Dictionary<string, string> { ["X-Device-Session"] = "session-1" },
            default);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("detached-response", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, fixture.Gate.Calls);
        Assert.Equal("jobs/session", fixture.Gate.LastRelativeUri);
        Assert.Equal("https://control.internal/api/platform/connector/access/v1/jobs/session", handler.LastUri!.AbsoluteUri);
        Assert.Equal("session-1", handler.Headers["X-Device-Session"]);
        Assert.DoesNotContain(handler.Headers.Keys, key =>
            key.Contains("Authorization", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("\"schemaVersion\":1", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"agentType\":\"platform-cad-connector\"", handler.Body, StringComparison.Ordinal);
        Assert.True(fixture.Gate.LastLifetime!.IsDisposed);
    }

    [Fact]
    public async Task OversizedRequest_FailsBeforeOpeningProtectedRoute()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var fixture = await RuntimeFixture.CreateAsync(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Runtime.SendAsync(
            HttpMethod.Post,
            "jobs/session",
            new { Payload = new string('x', TwoMiB + 1) },
            new Dictionary<string, string>(),
            default));

        Assert.Equal(0, fixture.Gate.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Device-Token")]
    [InlineData("x-device-session")]
    public async Task UnsupportedManagedHeader_FailsBeforeOpeningProtectedRoute(string header)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var fixture = await RuntimeFixture.CreateAsync(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runtime.SendAsync(
            HttpMethod.Get,
            "jobs/health",
            body: null,
            new Dictionary<string, string> { [header] = "value" },
            default));

        Assert.Equal(0, fixture.Gate.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("https://public.example/jobs")]
    [InlineData("/api/platform/jobs")]
    [InlineData("../public")]
    public async Task AbsoluteOrEscapingUri_IsRejectedByProtectedGate(string relativeUri)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var fixture = await RuntimeFixture.CreateAsync(handler);

        var error = await Assert.ThrowsAsync<NetworkGateException>(() => fixture.Runtime.SendAsync(
            HttpMethod.Get, relativeUri, body: null, new Dictionary<string, string>(), default));

        Assert.Equal("service_uri_denied", error.Code);
        Assert.Equal(1, fixture.Gate.Calls);
        Assert.Equal(0, handler.Calls);
        Assert.Null(fixture.Gate.LastLifetime);
    }

    [Fact]
    public async Task UnknownLengthResponseOverTwoMiB_IsRejectedAndRouteDisposed()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[TwoMiB + 1], "application/octet-stream"),
        });
        var fixture = await RuntimeFixture.CreateAsync(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Runtime.SendAsync(
            HttpMethod.Get, "jobs/next", body: null, new Dictionary<string, string>(), default));

        Assert.Equal(1, handler.Calls);
        Assert.True(fixture.Gate.LastLifetime!.IsDisposed);
    }

    [Fact]
    public async Task CancellationDuringSend_DisposesProtectedRoute()
    {
        var handler = new RecordingHandler(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var fixture = await RuntimeFixture.CreateAsync(handler);
        using var cancellation = new CancellationTokenSource();

        var sending = fixture.Runtime.SendAsync(
            HttpMethod.Get, "jobs/next", body: null, new Dictionary<string, string>(), cancellation.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.True(fixture.Gate.LastLifetime!.IsDisposed);
    }

    [Fact]
    public async Task GateFailure_IsFailClosedWithoutHttpSend()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var fixture = await RuntimeFixture.CreateAsync(handler);
        fixture.Gate.Failure = new NetworkGateException("overlay_not_ready", "fixture overlay is unavailable");

        var error = await Assert.ThrowsAsync<NetworkGateException>(() => fixture.Runtime.SendAsync(
            HttpMethod.Get, "jobs/health", body: null, new Dictionary<string, string>(), default));

        Assert.Equal("overlay_not_ready", error.Code);
        Assert.Equal(1, fixture.Gate.Calls);
        Assert.Equal(0, handler.Calls);
        Assert.Null(fixture.Gate.LastLifetime);
    }

    [Fact]
    public async Task ReadyRevocation_RaisesOneInvalidationAndRejectsSubsequentPrivateRequests()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var fixture = await RuntimeFixture.CreateAsync(handler);
        var invalidations = new List<CommonAccessInvalidation>();
        fixture.Runtime.ReadyInvalidated += invalidations.Add;
        fixture.Access.VpnStatus = "revoked";

        await Assert.ThrowsAsync<NetworkGateException>(() => fixture.Runtime.SendAsync(
            HttpMethod.Get, "jobs/next", body: null, new Dictionary<string, string>(), default));
        await Assert.ThrowsAsync<NetworkGateException>(() => fixture.Runtime.SendAsync(
            HttpMethod.Get, "jobs/next", body: null, new Dictionary<string, string>(), default));

        var invalidation = Assert.Single(invalidations);
        Assert.Equal(CommonConnectorConnectionStatus.Revoked, invalidation.Snapshot.Status);
        Assert.False(fixture.Runtime.IsReady);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task DelayedOldProtectedRequest_RaisesItsOriginalReadySession_NotReplacementSession()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var fixture = await RuntimeFixture.CreateAsync(handler);
        var invalidations = new List<CommonAccessInvalidation>();
        fixture.Runtime.ReadyInvalidated += invalidations.Add;

        fixture.Runtime.BeginSession(41);
        await fixture.Runtime.SendAsync(HttpMethod.Get, "jobs/health", body: null,
            new Dictionary<string, string>(), default);

        fixture.Access.PauseNextVpnState = true;
        var oldRequest = fixture.Runtime.SendAsync(HttpMethod.Get, "jobs/next", body: null,
            new Dictionary<string, string>(), default);
        await fixture.Access.VpnStateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The old request is already admitted. The desktop now owns a
        // replacement session before that request receives a revoked state.
        fixture.Runtime.BeginSession(42);
        fixture.Access.VpnStatus = "revoked";
        fixture.Access.ReleaseVpnState.TrySetResult();

        await Assert.ThrowsAsync<NetworkGateException>(() => oldRequest);

        var invalidation = Assert.Single(invalidations);
        Assert.Equal(41, invalidation.SessionGeneration);
        Assert.Equal(CommonConnectorConnectionStatus.Revoked, invalidation.Snapshot.Status);
    }

    private sealed class RuntimeFixture
    {
        private RuntimeFixture(CommonAccessRuntime runtime, RouteGate gate, ReadyAccessClient access)
        {
            Runtime = runtime;
            Gate = gate;
            Access = access;
        }

        public CommonAccessRuntime Runtime { get; }
        public RouteGate Gate { get; }
        public ReadyAccessClient Access { get; }

        public static async Task<RuntimeFixture> CreateAsync(HttpMessageHandler handler)
        {
            var access = new ReadyAccessClient();
            var overlay = new ReadyOverlay();
            var gate = new RouteGate(handler, overlay.Snapshot);
            var coordinator = new CommonConnectorConnectionCoordinator(access, overlay, gate);
            var connected = await coordinator.ResumeAndConnectAsync();
            Assert.Equal(CommonConnectorConnectionStatus.Ready, connected.Status);

            var constructor = typeof(CommonAccessRuntime).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(string), typeof(CommonConnectorConnectionCoordinator), typeof(Uri), typeof(Uri),
                    typeof(IConnectorEnrollmentClient), typeof(INetworkOverlayClient), typeof(IProtectedServiceNetworkGate)],
                modifiers: null) ?? throw new InvalidOperationException("Common access runtime fixture constructor was not found.");
            var runtime = (CommonAccessRuntime)constructor.Invoke([
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.json"),
                coordinator,
                RouteGate.BaseUri,
                new Uri("https://netbird.test"),
                access,
                overlay,
                gate,
            ]);
            runtime.Select();
            return new RuntimeFixture(runtime, gate, access);
        }
    }

    private sealed class ReadyAccessClient : IConnectorEnrollmentClient, IConnectorDeviceAccessClient
    {
        private const string DeviceId = "device-1";
        private const long Revision = 7;
        public string VpnStatus { get; set; } = "ready";
        public bool PauseNextVpnState { get; set; }
        public TaskCompletionSource VpnStateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseVpnState { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<DeviceEnrollmentResponse> EnrollAsync(
            string enrollmentToken,
            string deviceDisplayName,
            CancellationToken cancellationToken = default) => ResumeAsync(cancellationToken);

        public ValueTask<DeviceEnrollmentResponse> ResumeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DeviceEnrollmentResponse(
                DeviceAccessProtocol.Version,
                Guid.NewGuid(),
                DeviceId,
                "fixture-certificate",
                "fixture-issuer",
                DateTimeOffset.UtcNow.AddHours(1),
                Revision));

        public ValueTask<DeviceEnrollmentResponse?> GetReceiptAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<DeviceEnrollmentResponse?>(null);

        public ValueTask<DeviceAccessProfile> GetAccessProfileAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DeviceAccessProfile(
                DeviceAccessProtocol.Version,
                DeviceId,
                "user-1",
                "company-1",
                Revision,
                Revision,
                DateTimeOffset.UtcNow.AddHours(1),
                [],
                []));

        public ValueTask<ConnectorVpnBootstrap> GetVpnBootstrapAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A ready fixture must not request VPN bootstrap.");

        public async ValueTask<ConnectorVpnTransportState> GetVpnStateAsync(CancellationToken cancellationToken = default)
        {
            if (PauseNextVpnState)
            {
                PauseNextVpnState = false;
                VpnStateStarted.TrySetResult();
                await ReleaseVpnState.Task.WaitAsync(cancellationToken);
            }
            return new ConnectorVpnTransportState(
                DeviceId,
                Revision,
                VpnStatus,
                "peer-1",
                "https://netbird.test",
                ["100.90.0.22"],
                DateTimeOffset.UtcNow);
        }
    }

    private sealed class ReadyOverlay : INetworkOverlayClient
    {
        public NetworkOverlaySnapshot Snapshot { get; } = new(
            NetworkServiceStatus.Ready,
            StartupCheckPassed: true,
            ManagementConnected: true,
            SignalConnected: true,
            AssignedInternalAddresses: [IPAddress.Parse("100.90.0.22")],
            ObservedAtUtc: DateTimeOffset.UtcNow,
            DiagnosticCode: "ready",
            ManagementUri: new Uri("https://netbird.test"));

        public ValueTask<NetworkOverlaySnapshot> ConnectAsync(
            NetworkOverlayBootstrap bootstrap,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);

        public ValueTask<NetworkOverlaySnapshot> GetStatusAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Snapshot);
    }

    private sealed class RouteGate(HttpMessageHandler handler, NetworkOverlaySnapshot overlay)
        : IProtectedServiceNetworkGate
    {
        public static Uri BaseUri { get; } = new("https://control.internal/api/platform/connector/access/v1/");
        public int Calls { get; private set; }
        public string? LastRelativeUri { get; private set; }
        public DisposalTracker? LastLifetime { get; private set; }
        public Exception? Failure { get; set; }

        public ValueTask<ProtectedServiceRoute> ResolveAndProbeAsync(
            ProtectedOverlayTransportIdentity identity,
            string serviceId,
            string relativeUri,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRelativeUri = relativeUri;
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) return ValueTask.FromException<ProtectedServiceRoute>(Failure);
            if (Uri.TryCreate(relativeUri, UriKind.Absolute, out _) || relativeUri.StartsWith('/') ||
                relativeUri.Contains("..", StringComparison.Ordinal) || relativeUri.Contains('\\'))
                return ValueTask.FromException<ProtectedServiceRoute>(
                    new NetworkGateException("service_uri_denied", "Fixture protected route rejected the URI."));

            LastLifetime = new DisposalTracker();
            var client = new HttpClient(handler, disposeHandler: false) { BaseAddress = BaseUri };
            var routeConstructor = typeof(ProtectedServiceRoute).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(string), typeof(Uri), typeof(NetworkOverlaySnapshot), typeof(HttpClient), typeof(IDisposable)],
                modifiers: null) ?? throw new InvalidOperationException("Protected service route fixture constructor was not found.");
            var route = (ProtectedServiceRoute)routeConstructor.Invoke([
                serviceId,
                new Uri(BaseUri, relativeUri),
                overlay,
                client,
                LastLifetime,
            ]);
            return ValueTask.FromResult(route);
        }
    }

    private sealed class DisposalTracker : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
            => _response = (request, _) => Task.FromResult(response(request));

        public RecordingHandler(Func<CancellationToken, Task<HttpResponseMessage>> response)
            => _response = (_, cancellationToken) => response(cancellationToken);

        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }
        public string Body { get; private set; } = string.Empty;
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            Headers.Clear();
            foreach (var header in request.Headers) Headers[header.Key] = string.Join(",", header.Value);
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestStarted.TrySetResult();
            return await _response(request, cancellationToken);
        }
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _bytes;

        public UnknownLengthContent(byte[] bytes, string contentType)
        {
            _bytes = bytes;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes, 0, _bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(_bytes, writable: false));

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
    }
}
