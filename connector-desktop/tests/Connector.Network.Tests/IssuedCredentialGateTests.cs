using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Connector.Access.Client;
using Connector.Network;

namespace Connector.Network.Tests;

public sealed class IssuedCredentialGateTests
{
    [Theory]
    [InlineData("other-device", 4)]
    [InlineData("device-1", 5)]
    public async Task MismatchedDeviceOrFutureRevision_FailsBeforeProbe_AndDisposesLease(
        string leaseDeviceId,
        long leaseRevision)
    {
        using var authorization = new CancellationTokenSource();
        var source = new TestCredentialSource(leaseDeviceId, leaseRevision);
        var probe = new RecordingProbe(_ => ValueTask.FromResult(true));
        var gate = Gate(source, probe);

        var error = await Assert.ThrowsAsync<NetworkGateException>(() =>
            gate.ResolveAndProbeAsync(Identity(authorization.Token), "platform", "jobs/health").AsTask());

        Assert.Equal("issued_identity_mismatch", error.Code);
        Assert.Equal(0, probe.Calls);
        Assert.Equal(1, source.Acquisitions);
        Assert.Equal(IntPtr.Zero, source.Certificate!.Handle);
    }

    [Fact]
    public async Task SuccessfulRoute_KeepsCredentialAliveUntilRouteDisposal()
    {
        using var authorization = new CancellationTokenSource();
        var source = new TestCredentialSource("device-1", 4);
        var probe = new RecordingProbe(_ => ValueTask.FromResult(true));
        var gate = Gate(source, probe);

        var route = await gate.ResolveAndProbeAsync(Identity(authorization.Token), "platform", "jobs/session");

        Assert.Equal(1, probe.Calls);
        Assert.NotEqual(IntPtr.Zero, source.Certificate!.Handle);
        route.Dispose();
        Assert.Equal(IntPtr.Zero, source.Certificate.Handle);
    }

    [Fact]
    public async Task FalseProbe_DisposesIssuedCredential()
    {
        using var authorization = new CancellationTokenSource();
        var source = new TestCredentialSource("device-1", 4);
        var probe = new RecordingProbe(_ => ValueTask.FromResult(false));
        var gate = Gate(source, probe);

        var error = await Assert.ThrowsAsync<NetworkGateException>(() =>
            gate.ResolveAndProbeAsync(Identity(authorization.Token), "platform", "jobs/session").AsTask());

        Assert.Equal("service_offline", error.Code);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(IntPtr.Zero, source.Certificate!.Handle);
    }

    [Fact]
    public async Task ProbeException_DisposesIssuedCredential()
    {
        using var authorization = new CancellationTokenSource();
        var source = new TestCredentialSource("device-1", 4);
        var probe = new RecordingProbe(_ => ValueTask.FromException<bool>(new InvalidOperationException("probe failed")));
        var gate = Gate(source, probe);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.ResolveAndProbeAsync(Identity(authorization.Token), "platform", "jobs/session").AsTask());

        Assert.Equal("probe failed", error.Message);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(IntPtr.Zero, source.Certificate!.Handle);
    }

    private static ProtectedServiceNetworkGate Gate(
        IConnectorIssuedCredentialSource credentialSource,
        IProtectedServiceProbe probe) => new(
        new FixedOverlay(Ready()),
        probe,
        [new ProtectedServiceDefinition(
            "platform",
            new Uri("https://platform.internal/api/platform/connector/access/v1/"),
            ["100.64.0.10"],
            "jobs/health")],
        credentialSource);

    private static NetworkOverlaySnapshot Ready() => new(
        NetworkServiceStatus.Ready,
        StartupCheckPassed: true,
        ManagementConnected: true,
        SignalConnected: true,
        AssignedInternalAddresses: [IPAddress.Parse("100.90.0.22")],
        ObservedAtUtc: DateTimeOffset.UtcNow,
        DiagnosticCode: "ready",
        ManagementUri: new Uri("https://netbird.test"));

    private static ProtectedOverlayTransportIdentity Identity(CancellationToken authorizationLifetime) => new(
        "device-1",
        Revision: 4,
        "peer-1",
        new Uri("https://netbird.test"),
        [IPAddress.Parse("100.90.0.22")],
        authorizationLifetime);

    private sealed class FixedOverlay(NetworkOverlaySnapshot snapshot) : INetworkOverlayClient
    {
        public ValueTask<NetworkOverlaySnapshot> ConnectAsync(
            NetworkOverlayBootstrap bootstrap,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(snapshot);

        public ValueTask<NetworkOverlaySnapshot> GetStatusAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(snapshot);
    }

    private sealed class RecordingProbe(
        Func<HttpClient, ValueTask<bool>> behavior) : IProtectedServiceProbe
    {
        public int Calls { get; private set; }

        public ValueTask<bool> IsOnlineAsync(
            HttpClient client,
            Uri healthUri,
            CancellationToken cancellationToken)
        {
            Calls++;
            return behavior(client);
        }
    }

    private sealed class TestCredentialSource(string deviceId, long enrollmentRevision)
        : IConnectorIssuedCredentialSource
    {
        public int Acquisitions { get; private set; }
        public X509Certificate2? Certificate { get; private set; }

        public ValueTask<ConnectorIssuedCertificateLease> AcquireIssuedCertificateAsync(
            CancellationToken cancellationToken = default)
        {
            Acquisitions++;
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=connector-issued-test",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            Certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddMinutes(5));
            var constructor = typeof(ConnectorIssuedCertificateLease).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(string), typeof(long), typeof(X509Certificate2)],
                modifiers: null) ?? throw new InvalidOperationException("Internal issued-credential lease constructor was not found.");
            return ValueTask.FromResult((ConnectorIssuedCertificateLease)constructor.Invoke(
                [deviceId, enrollmentRevision, Certificate]));
        }
    }
}
