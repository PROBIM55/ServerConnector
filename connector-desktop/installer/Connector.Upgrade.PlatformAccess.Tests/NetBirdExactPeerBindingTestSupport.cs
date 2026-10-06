using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Connector.Network;
using Xunit;

namespace Connector.Upgrade.PlatformAccess.Tests;

internal sealed class ExactPeerProbeFixture : IDisposable
{
    public const string DeviceId = "dev_11111111111111111111111111111111";
    public const string PeerId = "peer-1";
    public const string PublicKey = "BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc=";
    public const string OtherPublicKey = "CAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg=";
    public static readonly IPAddress SourceIp = IPAddress.Parse("100.64.0.10");
    public static readonly IPAddress ListenerIp = IPAddress.Parse("100.64.0.1");
    public static readonly Uri ManagementUri = new("https://vpn.example.test");
    public static readonly Uri RouteUri = new("https://100.64.0.1/api/platform/connector/private/v1/peer-attestation");
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly X509Certificate2 _certificate;

    public ExactPeerProbeFixture()
    {
        Now = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        _certificate = CreateCertificate();
        CertificateSha256 = Convert.ToHexString(SHA256.HashData(_certificate.RawData)).ToLowerInvariant();
        Overlay = new NetworkOverlaySnapshot(
            NetworkServiceStatus.Ready,
            true,
            true,
            true,
            [SourceIp],
            Now,
            "ready",
            ManagementUri);
        Request = new ExactVpnPeerBindingProbeRequest(DeviceId, 7, PeerId, [SourceIp], Overlay);
        Identity = new TestIdentitySource(() => LocalIdentity());
        Certificates = new TestCertificateSource(DeviceId, 1, _certificate);
        Routes = new TestRouteSource(this);
        Probe = new NetBirdExactPeerBindingProbe(
            Identity,
            Routes,
            Certificates,
            new FixedNonceSource("AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE"),
            new FixedTimeProvider(Now),
            NetBirdExactPeerBindingOptions.Default);
    }

    public DateTimeOffset Now { get; }
    public string CertificateSha256 { get; }
    public NetworkOverlaySnapshot Overlay { get; }
    public ExactVpnPeerBindingProbeRequest Request { get; }
    public TestIdentitySource Identity { get; }
    public TestCertificateSource Certificates { get; }
    public TestRouteSource Routes { get; }
    public NetBirdExactPeerBindingProbe Probe { get; }
    public Func<TestReceipt, TestReceipt> MutateReceipt { get; set; } = receipt => receipt;
    public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;
    public string? RawResponse { get; set; }
    public string? LastPostedPublicKey { get; private set; }

    public NetBirdLocalIdentity LocalIdentity() =>
        new(PublicKey, ManagementUri, [SourceIp], Now);

    public CommonConnectorConnectionSnapshot ReadySession() =>
        new(CommonConnectorConnectionStatus.Ready, DeviceId, Request.Revision, "ready", Overlay);

    public async Task<HttpResponseMessage> RespondAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var requestDocument = JsonDocument.Parse(requestJson);
        var requestProperties = requestDocument.RootElement.EnumerateObject().ToArray();
        Assert.Equal(new[] { "nonce", "publicKey" }, requestProperties.Select(property => property.Name).Order().ToArray());
        var nonce = requestDocument.RootElement.GetProperty("nonce").GetString()!;
        LastPostedPublicKey = requestDocument.RootElement.GetProperty("publicKey").GetString();

        var receipt = MutateReceipt(new TestReceipt(
            nonce,
            DeviceId,
            Request.Revision,
            CertificateSha256,
            PeerId,
            SourceIp.ToString(),
            ListenerIp.ToString(),
            PublicKey,
            Now.AddSeconds(-10),
            Now,
            Now.AddSeconds(30)));
        HttpContent content = RawResponse is null
            ? new StringContent(JsonSerializer.Serialize(receipt, WebJson), Encoding.UTF8, "application/json")
            : new StringContent(RawResponse, Encoding.UTF8, "application/json");
        return new HttpResponseMessage(ResponseStatus)
        {
            Content = content,
            RequestMessage = request,
        };
    }

    public void Dispose() => _certificate.Dispose();

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=exact-peer-test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(10));
    }
}

internal sealed record TestReceipt(
    string Nonce,
    string DeviceId,
    long AccessRevision,
    string CertificateSha256,
    string PeerId,
    string SourceIp,
    string ListenerIp,
    string WireGuardPublicKey,
    DateTimeOffset TransportStateObservedAtUtc,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

internal sealed class TestIdentitySource(Func<NetBirdLocalIdentity> defaultIdentity)
    : INetBirdExactPeerIdentitySource
{
    public int Reads { get; private set; }
    public Func<int, NetBirdLocalIdentity>? ReadAt { get; set; }

    public ValueTask<NetBirdLocalIdentityReadResult> ReadAsync(
        Uri expectedManagementUri,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(ExactPeerProbeFixture.ManagementUri, expectedManagementUri);
        Reads++;
        return ValueTask.FromResult(NetBirdLocalIdentityReadResult.Available(
            ReadAt?.Invoke(Reads) ?? defaultIdentity()));
    }
}

internal sealed class TestRouteSource(ExactPeerProbeFixture fixture) : INetBirdExactPeerRouteSource
{
    public int Opens { get; private set; }
    public string? LastServiceId { get; private set; }
    public string? LastRelativeRoute { get; private set; }
    public Exception? OpenException { get; set; }
    public CommonConnectorConnectionSnapshot? CurrentOverride { get; set; }
    public CommonConnectorConnectionSnapshot Current => CurrentOverride ?? fixture.ReadySession();

    public ValueTask<IExactPeerAttestationRoute> OpenAsync(
        string serviceId,
        string relativeUri,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Opens++;
        LastServiceId = serviceId;
        LastRelativeRoute = relativeUri;
        if (OpenException is not null)
            return ValueTask.FromException<IExactPeerAttestationRoute>(OpenException);
        var client = new HttpClient(new DelegateHandler(fixture.RespondAsync));
        return ValueTask.FromResult<IExactPeerAttestationRoute>(
            new TestRoute(NetBirdExactPeerBindingProbe.ServiceId, ExactPeerProbeFixture.RouteUri, fixture.Overlay, client));
    }
}

internal sealed class TestRoute(
    string serviceId,
    Uri uri,
    NetworkOverlaySnapshot overlay,
    HttpClient client) : IExactPeerAttestationRoute
{
    public string ServiceId { get; } = serviceId;
    public Uri Uri { get; } = uri;
    public NetworkOverlaySnapshot Overlay { get; } = overlay;
    public HttpClient Client { get; } = client;
    public void Dispose() => Client.Dispose();
}

internal sealed class TestCertificateSource(
    string deviceId,
    long enrollmentRevision,
    X509Certificate2 certificate) : INetBirdExactPeerCertificateSource
{
    private readonly byte[] _certificatePfx = certificate.Export(X509ContentType.Pfx);
    public int Acquisitions { get; private set; }

    public ValueTask<IExactPeerCertificateLease> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Acquisitions++;
        return ValueTask.FromResult<IExactPeerCertificateLease>(new TestCertificateLease(
            deviceId,
            enrollmentRevision,
            new X509Certificate2(_certificatePfx)));
    }
}

internal sealed class TestCertificateLease(
    string deviceId,
    long enrollmentRevision,
    X509Certificate2 certificate) : IExactPeerCertificateLease
{
    public string DeviceId { get; } = deviceId;
    public long EnrollmentRevision { get; } = enrollmentRevision;
    public X509Certificate2 Certificate { get; } = certificate;
    public void Dispose() => Certificate.Dispose();
}

internal sealed class FixedNonceSource(string nonce) : IExactPeerNonceSource
{
    public string CreateNonce() => nonce;
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class DelegateHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => handler(request, cancellationToken);
}
