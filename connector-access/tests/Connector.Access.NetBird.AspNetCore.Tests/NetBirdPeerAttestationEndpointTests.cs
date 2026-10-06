using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Connector.Access;
using Connector.Access.Contracts;
using Connector.Access.NetBird;
using Connector.Access.NetBird.AspNetCore;

namespace Connector.Access.NetBird.AspNetCore.Tests;

public sealed class NetBirdPeerAttestationEndpointTests
{
    private const string Nonce = "AAECAwQFBgcICQoLDA0ODw";
    private const string PublicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    [Fact]
    public async Task ActualHttpsMtlsUsesRegistryIdentityAndSocketEndpointsOnly()
    {
        await using var access = await AccessTestFixture.CreateAsync();
        using var enrolled = await access.EnrollDeviceAsync();
        var service = new RecordingAttestationService(Accepted);
        await using var server = await AttestationServer.StartAsync(access.Authenticator, service);
        using var client = server.CreateClient(enrolled.Certificate);
        using var request = new HttpRequestMessage(HttpMethod.Post, NetBirdPeerAttestationEndpoints.Route)
        {
            Content = JsonContent.Create(new
            {
                nonce = Nonce,
                publicKey = PublicKey,
                deviceId = "spoofed-device",
                peerId = "spoofed-peer",
                observedLocalIp = "203.0.113.10",
                observedRemoteIp = "203.0.113.11"
            })
        };
        request.Headers.TryAddWithoutValidation("X-Client-Cert", "spoofed-certificate");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.12");
        request.Headers.TryAddWithoutValidation("Forwarded", "for=203.0.113.13;proto=http");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        var captured = Assert.Single(service.Requests);
        Assert.Equal(enrolled.Enrollment.DeviceId, captured.Device.DeviceId);
        Assert.Equal("user-1", captured.Device.UserId);
        Assert.Equal("company-1", captured.Device.CompanyId);
        Assert.Equal(IPAddress.Loopback.ToString(), captured.ObservedLocalIp);
        Assert.Equal(IPAddress.Loopback.ToString(), captured.ObservedRemoteIp);
        Assert.Equal(Nonce, captured.Nonce);
        Assert.Equal(PublicKey, captured.ClaimedWireGuardPublicKey);

        var attestation = await response.Content.ReadFromJsonAsync<NetBirdPeerAttestation>();
        Assert.NotNull(attestation);
        Assert.Equal(enrolled.Enrollment.DeviceId, attestation.DeviceId);
        Assert.Equal(IPAddress.Loopback.ToString(), attestation.ListenerIp);
        Assert.Equal(IPAddress.Loopback.ToString(), attestation.SourceIp);
    }

    [Fact]
    public async Task PlainHttpMissingCertificateAndUnregisteredCertificateFailClosed()
    {
        await using var access = await AccessTestFixture.CreateAsync();
        using var enrolled = await access.EnrollDeviceAsync();
        var service = new RecordingAttestationService(Accepted);
        await using var httpsServer = await AttestationServer.StartAsync(access.Authenticator, service);

        using (var anonymous = httpsServer.CreateClient())
        using (var request = Request())
        {
            request.Headers.TryAddWithoutValidation("X-Client-Cert", "forged-header-certificate");
            using var response = await anonymous.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore == true);
        }

        using var invalidCertificate = CreateUnregisteredClientCertificate();
        using (var invalidClient = httpsServer.CreateClient(invalidCertificate))
        using (var response = await invalidClient.SendAsync(Request()))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore == true);
        }

        await using var httpServer = await AttestationServer.StartAsync(access.Authenticator, service, https: false);
        using (var httpClient = httpServer.CreateClient())
        using (var request = Request())
        {
            request.Headers.TryAddWithoutValidation("X-Client-Cert", "forged-header-certificate");
            using var response = await httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore == true);
        }

        Assert.Empty(service.Requests);
    }

    [Fact]
    public async Task ContentLengthAndChunkedBodiesOverTwoKiBAreRejectedBeforeService()
    {
        await using var access = await AccessTestFixture.CreateAsync();
        using var enrolled = await access.EnrollDeviceAsync();
        var service = new RecordingAttestationService(Accepted);
        await using var server = await AttestationServer.StartAsync(access.Authenticator, service);
        using var client = server.CreateClient(enrolled.Certificate);
        var oversizedJson = "{\"nonce\":\"" + new string('A', 2300) + "\",\"publicKey\":\"" + PublicKey + "\"}";

        using (var declared = new HttpRequestMessage(HttpMethod.Post, NetBirdPeerAttestationEndpoints.Route)
               {
                   Content = new StringContent(oversizedJson, Encoding.UTF8, "application/json")
               })
        using (var response = await client.SendAsync(declared))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore == true);
        }

        using (var chunked = new HttpRequestMessage(HttpMethod.Post, NetBirdPeerAttestationEndpoints.Route)
               {
                   Version = HttpVersion.Version11,
                   VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                   Content = new ChunkedJsonContent(oversizedJson)
               })
        using (var response = await client.SendAsync(chunked))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore == true);
        }

        Assert.Empty(service.Requests);
    }

    [Fact]
    public async Task ServiceRejectionAndExceptionHaveSameGenericExternalResponse()
    {
        await using var access = await AccessTestFixture.CreateAsync();
        using var enrolled = await access.EnrollDeviceAsync();
        var calls = 0;
        var service = new RecordingAttestationService(_ => ++calls == 1
            ? new NetBirdPeerAttestationRejected(NetBirdPeerAttestationFailureReason.ListenerIpMismatch)
            : throw new InvalidOperationException("sensitive daemon failure"));
        await using var server = await AttestationServer.StartAsync(access.Authenticator, service);
        using var client = server.CreateClient(enrolled.Certificate);

        using var rejected = await client.SendAsync(Request());
        using var failed = await client.SendAsync(Request());
        var rejectedBody = await rejected.Content.ReadAsStringAsync();
        var failedBody = await failed.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, failed.StatusCode);
        Assert.Equal(rejectedBody, failedBody);
        Assert.DoesNotContain(nameof(NetBirdPeerAttestationFailureReason.ListenerIpMismatch), rejectedBody);
        Assert.DoesNotContain("sensitive", failedBody, StringComparison.OrdinalIgnoreCase);
        Assert.True(rejected.Headers.CacheControl?.NoStore == true);
        Assert.True(failed.Headers.CacheControl?.NoStore == true);
    }

    [Fact]
    public async Task NetBirdServiceRejectsWrongConfiguredListenerAndObservedPeerIp()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        const string listener = "100.90.0.1";
        const string peer = "100.90.0.22";
        var device = new AuthenticatedDevice(
            "device-1",
            "user-1",
            "company-1",
            new string('a', 64),
            7);
        var state = new ConnectorVpnTransportState(
            device.DeviceId,
            device.AccessRevision,
            "ready",
            "peer-1",
            "https://netbird.example.test",
            [peer],
            now);
        var service = new NetBirdPeerAttestationService(
            new NetBirdPeerAttestationOptions { ExpectedServerOverlayListenerIp = listener },
            new FixedVpnStateReader(state),
            new FixedPeerSnapshotReader(new NetBirdDaemonPeerExact(new NetBirdDaemonPeerEvidence(
                peer,
                null,
                PublicKey,
                "Connected",
                now.AddSeconds(-1)))),
            new FixedTimeProvider(now));

        var wrongLocal = await service.AttestAsync(new NetBirdPeerAttestationRequest(
            device, "100.90.0.2", peer, PublicKey, Nonce));
        var wrongRemote = await service.AttestAsync(new NetBirdPeerAttestationRequest(
            device, listener, "100.90.0.23", PublicKey, Nonce));

        Assert.Equal(
            NetBirdPeerAttestationFailureReason.ListenerIpMismatch,
            Assert.IsType<NetBirdPeerAttestationRejected>(wrongLocal).Reason);
        Assert.Equal(
            NetBirdPeerAttestationFailureReason.SourceIpMismatch,
            Assert.IsType<NetBirdPeerAttestationRejected>(wrongRemote).Reason);
    }

    private static HttpRequestMessage Request() => new(
        HttpMethod.Post,
        NetBirdPeerAttestationEndpoints.Route)
    {
        Content = JsonContent.Create(new { nonce = Nonce, publicKey = PublicKey })
    };

    private static NetBirdPeerAttestationResult Accepted(NetBirdPeerAttestationRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        return new NetBirdPeerAttestationAccepted(new NetBirdPeerAttestation(
            request.Nonce,
            request.Device.DeviceId,
            request.Device.AccessRevision,
            request.Device.CertificateSha256,
            "peer-from-service",
            request.ObservedRemoteIp,
            request.ObservedLocalIp,
            request.ClaimedWireGuardPublicKey,
            now,
            now,
            now.AddSeconds(30)));
    }

    private static X509Certificate2 CreateUnregisteredClientCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=unregistered-device",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(DeviceAccessProtocol.ClientAuthenticationOid) },
            true));
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(10));
        return new X509Certificate2(generated.Export(X509ContentType.Pfx));
    }

    private sealed class ChunkedJsonContent : HttpContent
    {
        private readonly byte[] _content;

        public ChunkedJsonContent(string content)
        {
            _content = Encoding.UTF8.GetBytes(content);
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var offset = 0; offset < _content.Length; offset += 257)
            {
                var count = Math.Min(257, _content.Length - offset);
                await stream.WriteAsync(_content.AsMemory(offset, count));
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FixedVpnStateReader(ConnectorVpnTransportState state) : IConnectorVpnBootstrapReader
    {
        public ValueTask<DeviceAccessResult<ConnectorVpnBootstrap>> GetAsync(
            AuthenticatedDevice device,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(DeviceAccessResult<ConnectorVpnBootstrap>.Fail("unused", "unused"));

        public ValueTask<DeviceAccessResult<ConnectorVpnTransportState>> GetStateAsync(
            AuthenticatedDevice device,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(DeviceAccessResult<ConnectorVpnTransportState>.Success(state));
    }

    private sealed class FixedPeerSnapshotReader(NetBirdDaemonPeerSnapshotResult result)
        : INetBirdDaemonPeerSnapshotReader
    {
        public ValueTask<NetBirdDaemonPeerSnapshotResult> ReadAsync(
            string expectedIp,
            string expectedPublicKey,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(result);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
