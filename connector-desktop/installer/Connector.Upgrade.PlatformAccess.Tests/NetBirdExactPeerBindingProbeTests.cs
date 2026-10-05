using System.Net;
using Connector.Network;
using Xunit;

namespace Connector.Upgrade.PlatformAccess.Tests;

public sealed class NetBirdExactPeerBindingProbeTests
{
    [Fact]
    public void ProductionConstructorRequiresConcreteOwners()
    {
        var constructor = Assert.Single(typeof(NetBirdExactPeerBindingProbe).GetConstructors());
        Assert.Equal(
            new[] { typeof(NetBirdCliClient), typeof(CommonConnectorConnectionCoordinator), typeof(Connector.Access.Client.HttpConnectorEnrollmentClient) },
            constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    [Fact]
    public async Task HealthyReceiptBindsRouteCertificatePeerAndStableLocalIdentity()
    {
        using var fixture = new ExactPeerProbeFixture();

        Assert.True(await fixture.Probe.VerifyAsync(fixture.Request, CancellationToken.None));

        Assert.Equal(NetBirdExactPeerBindingProbe.ServiceId, fixture.Routes.LastServiceId);
        Assert.Equal(NetBirdExactPeerBindingProbe.RelativeRoute, fixture.Routes.LastRelativeRoute);
        Assert.Equal(2, fixture.Identity.Reads);
        Assert.Equal(1, fixture.Certificates.Acquisitions);
        Assert.Equal(ExactPeerProbeFixture.PublicKey, fixture.LastPostedPublicKey);
    }

    [Theory]
    [InlineData(ReceiptMismatch.Nonce)]
    [InlineData(ReceiptMismatch.Device)]
    [InlineData(ReceiptMismatch.Revision)]
    [InlineData(ReceiptMismatch.Peer)]
    [InlineData(ReceiptMismatch.Certificate)]
    [InlineData(ReceiptMismatch.SourceIp)]
    [InlineData(ReceiptMismatch.ListenerIp)]
    [InlineData(ReceiptMismatch.WireGuardKey)]
    public async Task AdversarialReceiptMismatchIsRejected(ReceiptMismatch mismatch)
    {
        using var fixture = new ExactPeerProbeFixture
        {
            MutateReceipt = receipt => mismatch switch
            {
                ReceiptMismatch.Nonce => receipt with { Nonce = receipt.Nonce[..^1] + "A" },
                ReceiptMismatch.Device => receipt with { DeviceId = "dev_other" },
                ReceiptMismatch.Revision => receipt with { AccessRevision = receipt.AccessRevision + 1 },
                ReceiptMismatch.Peer => receipt with { PeerId = "peer-other" },
                ReceiptMismatch.Certificate => receipt with { CertificateSha256 = new string('0', 64) },
                ReceiptMismatch.SourceIp => receipt with { SourceIp = "100.64.0.99" },
                ReceiptMismatch.ListenerIp => receipt with { ListenerIp = "100.64.0.2" },
                ReceiptMismatch.WireGuardKey => receipt with { WireGuardPublicKey = ExactPeerProbeFixture.OtherPublicKey },
                _ => receipt,
            }
        };

        Assert.False(await fixture.Probe.VerifyAsync(fixture.Request, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredOrFutureDatedReceiptIsRejected(bool future)
    {
        using var fixture = new ExactPeerProbeFixture();
        fixture.MutateReceipt = receipt => future
            ? receipt with
            {
                TransportStateObservedAtUtc = fixture.Now.AddSeconds(6),
                IssuedAtUtc = fixture.Now.AddSeconds(6),
                ExpiresAtUtc = fixture.Now.AddSeconds(30),
            }
            : receipt with { ExpiresAtUtc = fixture.Now.AddMilliseconds(-1) };

        Assert.False(await fixture.Probe.VerifyAsync(fixture.Request, CancellationToken.None));
    }

    [Fact]
    public async Task ReplayedAcceptedNonceIsRejected()
    {
        using var fixture = new ExactPeerProbeFixture();

        Assert.True(await fixture.Probe.VerifyAsync(fixture.Request, CancellationToken.None));
        Assert.False(await fixture.Probe.VerifyAsync(fixture.Request, CancellationToken.None));
    }

    [Fact]
    public async Task RouteFailureAndRedirectAreRejected()
    {
        using var failed = new ExactPeerProbeFixture();
        failed.Routes.OpenException = new IOException("route unavailable");
        Assert.False(await failed.Probe.VerifyAsync(failed.Request, CancellationToken.None));

        using var redirected = new ExactPeerProbeFixture
        {
            ResponseStatus = HttpStatusCode.Redirect,
        };
        Assert.False(await redirected.Probe.VerifyAsync(redirected.Request, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedOrMalformedReceiptIsRejected()
    {
        using var oversized = new ExactPeerProbeFixture
        {
            RawResponse = new string('x', 8 * 1024 + 1),
        };
        Assert.False(await oversized.Probe.VerifyAsync(oversized.Request, CancellationToken.None));

        using var malformed = new ExactPeerProbeFixture
        {
            RawResponse = "{\"nonce\":\"duplicate\",\"nonce\":\"duplicate\"}",
        };
        Assert.False(await malformed.Probe.VerifyAsync(malformed.Request, CancellationToken.None));
    }

    [Fact]
    public async Task LocalIdentityRotationAfterResponseIsRejected()
    {
        using var fixture = new ExactPeerProbeFixture();
        fixture.Identity.ReadAt = call => call == 1
            ? fixture.LocalIdentity()
            : fixture.LocalIdentity() with { WireGuardPublicKey = ExactPeerProbeFixture.OtherPublicKey };

        Assert.False(await fixture.Probe.VerifyAsync(fixture.Request, CancellationToken.None));
    }

    [Fact]
    public async Task CoordinatorSessionRotationAfterResponseIsRejected()
    {
        using var fixture = new ExactPeerProbeFixture();
        fixture.Routes.CurrentOverride = fixture.ReadySession() with { Revision = fixture.Request.Revision + 1 };

        Assert.False(await fixture.Probe.VerifyAsync(fixture.Request, CancellationToken.None));
    }

    [Fact]
    public async Task DegradedOverlayIsRejectedBeforeOpeningRoute()
    {
        using var fixture = new ExactPeerProbeFixture();
        var degraded = fixture.Request with
        {
            Overlay = fixture.Request.Overlay with { Status = NetworkServiceStatus.Degraded }
        };

        Assert.False(await fixture.Probe.VerifyAsync(degraded, CancellationToken.None));
        Assert.Equal(0, fixture.Routes.Opens);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var fixture = new ExactPeerProbeFixture();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Probe.VerifyAsync(fixture.Request, canceled.Token).AsTask());
    }
}

public enum ReceiptMismatch
{
    Nonce,
    Device,
    Revision,
    Peer,
    Certificate,
    SourceIp,
    ListenerIp,
    WireGuardKey,
}
