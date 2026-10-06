using System.Net;
using Connector.Access.Client;
using Connector.Access.Contracts;
using Connector.Network;
using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsEnvironment;
using Connector.Upgrade.WindowsHost;
using Xunit;

namespace Connector.Upgrade.PlatformAccess.Tests;

public sealed class PlatformAccessPortsTests
{
    private static readonly Uri Management = new("https://netbird.example.test/");
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Enroll_CompensationUnavailable_DoesNotDiscloseToken()
    {
        var client = new FakeAccessClient();
        var session = new FakeSession();
        var port = CreateEnrollmentPort(client, session, new FakeRevocation { Available = false });

        await Assert.ThrowsAsync<UpgradeInvariantException>(async () =>
            await port.EnrollAsync(new OneTimePlatformToken("cea1.secret"), default));

        Assert.Equal(0, session.PreflightCalls);
        Assert.Equal(0, session.EnrollCalls);
        Assert.Null(session.ObservedToken);
    }

    [Fact]
    public async Task Enroll_SchemaUnavailable_DoesNotRunAnyLaterPreflightOrDiscloseToken()
    {
        var client = new FakeAccessClient();
        var session = new FakeSession();
        var revocation = new FakeRevocation();
        var port = CreateEnrollmentPort(client, session, revocation, schemaAvailable: false);

        await Assert.ThrowsAsync<UpgradeInvariantException>(async () =>
            await port.EnrollAsync(new OneTimePlatformToken("cea1.secret"), default));

        Assert.Equal(0, revocation.InspectCalls);
        Assert.Equal(0, session.EnrollCalls);
        Assert.Null(session.ObservedToken);
    }

    [Fact]
    public async Task Enroll_ExistingEnrollment_DoesNotDiscloseNewToken()
    {
        var client = new FakeAccessClient { Receipt = Enrollment() };
        var session = new FakeSession();
        var port = CreateEnrollmentPort(client, session, new FakeRevocation());

        await Assert.ThrowsAsync<UpgradeInvariantException>(async () =>
            await port.EnrollAsync(new OneTimePlatformToken("cea1.secret"), default));

        Assert.Equal(1, session.PreflightCalls);
        Assert.Equal(0, session.EnrollCalls);
        Assert.Null(session.ObservedToken);
    }

    [Fact]
    public async Task Enroll_ReadyExactIdentity_ReturnsExactDeviceReceipt()
    {
        var client = new FakeAccessClient();
        var session = new FakeSession();
        session.OnEnroll = () =>
        {
            client.Receipt = Enrollment();
            return ReadySnapshot();
        };
        var port = CreateEnrollmentPort(client, session, new FakeRevocation());

        var receipt = await port.EnrollAsync(new OneTimePlatformToken("cea1.secret"), default);

        Assert.Equal(DeviceId, receipt.EnrollmentId);
        Assert.Equal("cea1.secret", session.ObservedToken);
    }

    [Fact]
    public async Task Enroll_PostMutationMismatch_RevokesExactDeviceBeforeFailing()
    {
        var client = new FakeAccessClient();
        var session = new FakeSession();
        session.OnEnroll = () =>
        {
            client.Receipt = Enrollment();
            return ReadySnapshot() with { DeviceId = OtherDeviceId };
        };
        var revocation = new FakeRevocation();
        var port = CreateEnrollmentPort(client, session, revocation);

        await Assert.ThrowsAsync<UpgradeInvariantException>(async () =>
            await port.EnrollAsync(new OneTimePlatformToken("cea1.secret"), default));

        Assert.Equal([DeviceId], revocation.RevokedDeviceIds);
        Assert.Equal(1, session.DisconnectCalls);
    }

    [Fact]
    public async Task Enroll_UncertainOutcomeWithoutRecoverableIdentity_RequiresManualRecovery()
    {
        var client = new FakeAccessClient();
        var session = new FakeSession
        {
            EnrollmentFailure = new IOException("lost response")
        };
        var revocation = new FakeRevocation();
        var port = CreateEnrollmentPort(client, session, revocation);

        await Assert.ThrowsAsync<WindowsHostManualRecoveryRequiredException>(async () =>
            await port.EnrollAsync(new OneTimePlatformToken("cea1.secret"), default));

        Assert.Empty(revocation.RevokedDeviceIds);
    }

    [Fact]
    public async Task Enroll_LostResponse_ResumesSameRequestAndCompensatesExactDevice()
    {
        var client = new FakeAccessClient { ResumeReceipt = Enrollment() };
        var session = new FakeSession { EnrollmentFailure = new IOException("lost response") };
        var revocation = new FakeRevocation();
        var port = CreateEnrollmentPort(client, session, revocation);

        await Assert.ThrowsAsync<IOException>(async () =>
            await port.EnrollAsync(new OneTimePlatformToken("cea1.secret"), default));

        Assert.Equal([DeviceId], revocation.RevokedDeviceIds);
        Assert.Equal(1, session.DisconnectCalls);
    }

    [Fact]
    public async Task Remove_UsesIdempotentlyConfirmableExactRevocation()
    {
        var client = new FakeAccessClient();
        var session = new FakeSession();
        var revocation = new FakeRevocation();
        var port = CreateEnrollmentPort(client, session, revocation);

        await port.RemoveAsync(new PlatformEnrollmentReceipt(DeviceId), default);

        Assert.Equal([DeviceId], revocation.RevokedDeviceIds);
        Assert.Equal(1, session.DisconnectCalls);
    }

    [Fact]
    public async Task Verify_ExactApiVpnAndAuthenticatedSmb_ReturnsAllTrue()
    {
        var client = ReadyClient();
        var session = new FakeSession { CurrentValue = ReadySnapshot(), ProtectedServiceAvailable = true };
        var smb = new FakeSmbProbe { Result = true };
        var port = CreateVerificationPort(client, session, smb);

        var result = await port.VerifyAsync(new PlatformEnrollmentReceipt(DeviceId), default);

        Assert.True(result.IsSuccessful);
        Assert.True(result.SmbRequired);
        Assert.Equal(DeviceId, smb.LastRequest?.DeviceId);
        Assert.Equal(7, smb.LastRequest?.Revision);
    }

    [Fact]
    public async Task Verify_AppliedProfileWithoutResourceRead_DoesNotProbeSmbAndSucceeds()
    {
        var client = ReadyClient();
        client.Profile = client.Profile with { Resources = [] };
        var session = new FakeSession { CurrentValue = ReadySnapshot() with { AccessProfile = client.Profile }, ProtectedServiceAvailable = true };
        var smb = new FakeSmbProbe { Result = false };
        var port = CreateVerificationPort(client, session, smb);

        var result = await port.VerifyAsync(new PlatformEnrollmentReceipt(DeviceId), default);

        Assert.True(result.PlatformApi);
        Assert.True(result.VpnRoute);
        Assert.False(result.Smb);
        Assert.False(result.SmbRequired);
        Assert.True(result.IsSuccessful);
        Assert.Null(smb.LastRequest);
    }

    [Fact]
    public async Task Verify_AppliedResourceReadRequiresSuccessfulSmbRead()
    {
        var client = ReadyClient();
        var session = new FakeSession { CurrentValue = ReadySnapshot(), ProtectedServiceAvailable = true };
        var smb = new FakeSmbProbe { Result = false };
        var port = CreateVerificationPort(client, session, smb);

        var result = await port.VerifyAsync(new PlatformEnrollmentReceipt(DeviceId), default);

        Assert.True(result.PlatformApi);
        Assert.True(result.VpnRoute);
        Assert.False(result.Smb);
        Assert.True(result.SmbRequired);
        Assert.False(result.IsSuccessful);
        Assert.Equal(7, smb.LastRequest?.Revision);
    }

    [Fact]
    public async Task Verify_InvalidAppliedProfileFailsClosedBeforeProtectedRequest()
    {
        var client = ReadyClient();
        client.Profile = client.Profile with { Resources = null! };
        var session = new FakeSession { CurrentValue = ReadySnapshot() with { AccessProfile = client.Profile }, ProtectedServiceAvailable = true };
        var smb = new FakeSmbProbe { Result = true };
        var port = CreateVerificationPort(client, session, smb);

        var result = await port.VerifyAsync(new PlatformEnrollmentReceipt(DeviceId), default);

        Assert.False(result.IsSuccessful);
        Assert.Equal(0, session.ProtectedServiceCalls);
        Assert.Null(smb.LastRequest);
    }

    [Fact]
    public async Task Verify_MalformedResourceGrantFailsClosedBeforeProtectedRequest()
    {
        var client = ReadyClient();
        client.Profile = client.Profile with
        {
            Resources = [new ResourceGrant("bim", "smb", null, null!)]
        };
        var session = new FakeSession { CurrentValue = ReadySnapshot() with { AccessProfile = client.Profile }, ProtectedServiceAvailable = true };
        var smb = new FakeSmbProbe { Result = true };
        var port = CreateVerificationPort(client, session, smb);

        var result = await port.VerifyAsync(new PlatformEnrollmentReceipt(DeviceId), default);

        Assert.False(result.IsSuccessful);
        Assert.Equal(0, session.ProtectedServiceCalls);
        Assert.Null(smb.LastRequest);
    }

    [Fact]
    public async Task Verify_ExpiredAppliedProfileFailsClosedBeforeProtectedRequest()
    {
        var client = ReadyClient();
        client.Profile = client.Profile with { ExpiresAtUtc = Now };
        var session = new FakeSession { CurrentValue = ReadySnapshot() with { AccessProfile = client.Profile }, ProtectedServiceAvailable = true };
        var smb = new FakeSmbProbe { Result = true };
        var port = CreateVerificationPort(client, session, smb);

        var result = await port.VerifyAsync(new PlatformEnrollmentReceipt(DeviceId), default);

        Assert.False(result.IsSuccessful);
        Assert.Equal(0, session.ProtectedServiceCalls);
        Assert.Null(smb.LastRequest);
    }

    [Fact]
    public async Task Verify_AppliedRevisionChangesDuringApiRequest_SkipsSmbProbe()
    {
        var client = ReadyClient();
        var session = new FakeSession
        {
            CurrentValue = ReadySnapshot(),
            ProtectedServiceAvailable = true,
            OnProtectedService = value => value.CurrentValue = ReadySnapshot() with
            {
                Revision = 8,
                AccessProfile = Profile() with { DesiredRevision = 8, AppliedRevision = 8 }
            }
        };
        var smb = new FakeSmbProbe { Result = true };
        var port = CreateVerificationPort(client, session, smb);

        var result = await port.VerifyAsync(new PlatformEnrollmentReceipt(DeviceId), default);

        Assert.False(result.IsSuccessful);
        Assert.Null(smb.LastRequest);
    }

    [Fact]
    public async Task Verify_ServerAndLocalOverlayMismatch_NeverRunsApiOrSmbProbe()
    {
        var client = ReadyClient();
        client.Overlay = client.Overlay with
        {
            AssignedInternalAddresses = [IPAddress.Parse("100.64.0.99")]
        };
        var session = new FakeSession { CurrentValue = ReadySnapshot(), ProtectedServiceAvailable = true };
        var smb = new FakeSmbProbe { Result = true };
        var port = CreateVerificationPort(client, session, smb);

        var result = await port.VerifyAsync(new PlatformEnrollmentReceipt(DeviceId), default);

        Assert.Equal(new NewAccessVerification(false, false, false), result);
        Assert.Equal(0, session.ProtectedServiceCalls);
        Assert.Null(smb.LastRequest);
    }

    [Fact]
    public async Task Verify_NoNativeSmbProof_NeverReportsSuccessfulMigration()
    {
        var client = ReadyClient();
        var session = new FakeSession { CurrentValue = ReadySnapshot(), ProtectedServiceAvailable = true };
        var port = CreateVerificationPort(client, session, new UnavailableExactSmbAccessProbe());

        var result = await port.VerifyAsync(new PlatformEnrollmentReceipt(DeviceId), default);

        Assert.Equal(new NewAccessVerification(true, true, false), result);
    }

    [Fact]
    public async Task Verify_NoCryptographicPeerBinding_NeverReportsVpnOrRunsLaterProbes()
    {
        var client = ReadyClient();
        var session = new FakeSession { CurrentValue = ReadySnapshot(), ProtectedServiceAvailable = true };
        var smb = new FakeSmbProbe { Result = true };
        var port = new CommonConnectorLiveAccessVerificationPort(
            client,
            client,
            session,
            new UnavailableExactVpnPeerBindingProbe(),
            smb,
            new FixedTimeProvider(Now));

        var result = await port.VerifyAsync(new PlatformEnrollmentReceipt(DeviceId), default);

        Assert.Equal(new NewAccessVerification(false, false, false), result);
        Assert.Equal(0, session.ProtectedServiceCalls);
        Assert.Null(smb.LastRequest);
    }

    [Fact]
    public async Task Verify_ProtectedRequestSwitchesToAnotherDevice_ReturnsAllFalseAndSkipsSmb()
    {
        var client = ReadyClient();
        var session = new FakeSession
        {
            CurrentValue = ReadySnapshot(),
            ProtectedServiceAvailable = true,
            OnProtectedService = value => value.CurrentValue = ReadySnapshot() with
            {
                DeviceId = OtherDeviceId,
                AccessProfile = Profile() with { DeviceId = OtherDeviceId }
            }
        };
        var smb = new FakeSmbProbe { Result = true };
        var port = CreateVerificationPort(client, session, smb);

        var result = await port.VerifyAsync(new PlatformEnrollmentReceipt(DeviceId), default);

        Assert.Equal(new NewAccessVerification(false, false, false), result);
        Assert.Equal(1, session.ProtectedServiceCalls);
        Assert.Null(smb.LastRequest);
    }

    private static CompensatablePlatformEnrollmentPort CreateEnrollmentPort(
        FakeAccessClient client,
        FakeSession session,
        FakeRevocation revocation,
        bool schemaAvailable = true) =>
        new(
            new FakeSchemaProbe(schemaAvailable),
            revocation,
            client,
            session,
            Management,
            "TEST-PC");

    private static CommonConnectorLiveAccessVerificationPort CreateVerificationPort(
        FakeAccessClient client,
        FakeSession session,
        IExactSmbAccessProbe smb) =>
        new(
            client,
            client,
            session,
            new FakePeerBindingProbe { Result = true },
            smb,
            new FixedTimeProvider(Now));

    private static FakeAccessClient ReadyClient() => new()
    {
        Receipt = Enrollment(),
        VpnState = new ConnectorVpnTransportState(
            DeviceId, 7, "ready", "peer-1", Management.AbsoluteUri,
            ["100.64.0.10/32"], Now),
        Profile = Profile(),
        Overlay = Overlay(),
    };

    private static DeviceEnrollmentResponse Enrollment() => new(
        DeviceAccessProtocol.Version,
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        DeviceId,
        "certificate",
        "issuer",
        Now.AddHours(1),
        7);

    private static DeviceAccessProfile Profile() => new(
        DeviceAccessProtocol.Version,
        DeviceId,
        "user-1",
        "company-1",
        7,
        7,
        Now.AddMinutes(30),
        [new ModuleGrant(ConnectorProduct.Platform, "services", [ConnectorPermission.Read])],
        [new ResourceGrant("bim", "smb", null, [ConnectorPermission.Read])]);

    private static NetworkOverlaySnapshot Overlay() => new(
        NetworkServiceStatus.Ready,
        true,
        true,
        true,
        [IPAddress.Parse("100.64.0.10")],
        Now,
        "ready",
        Management);

    private static CommonConnectorConnectionSnapshot ReadySnapshot() => new(
        CommonConnectorConnectionStatus.Ready,
        DeviceId,
        7,
        "ready",
        Overlay(),
        Profile());

    private const string DeviceId = "dev_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherDeviceId = "dev_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private sealed class FakeSchemaProbe(bool available) : IPlatformAccessSchemaProbe
    {
        public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(available);
        }
    }

    private sealed class FakeRevocation : IExactDeviceRevocationPort
    {
        public bool Available { get; init; } = true;
        public int InspectCalls { get; private set; }
        public List<string> RevokedDeviceIds { get; } = [];

        public ValueTask<ExactDeviceRevocationReadiness> InspectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCalls++;
            return ValueTask.FromResult(new ExactDeviceRevocationReadiness(Available, "revoke-proof"));
        }

        public ValueTask RevokeAndConfirmExactAsync(string deviceId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevokedDeviceIds.Add(deviceId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSession : IPlatformAccessSession
    {
        public int PreflightCalls { get; private set; }
        public int EnrollCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public int ProtectedServiceCalls { get; private set; }
        public string? ObservedToken { get; private set; }
        public Exception? EnrollmentFailure { get; init; }
        public Func<CommonConnectorConnectionSnapshot>? OnEnroll { get; set; }
        public Action<FakeSession>? OnProtectedService { get; init; }
        public CommonConnectorConnectionSnapshot CurrentValue { get; set; } =
            new(CommonConnectorConnectionStatus.Unknown, null, null, "not-started");
        public bool ProtectedServiceAvailable { get; init; }
        public CommonConnectorConnectionSnapshot Current => CurrentValue;

        public ValueTask EnsurePreTokenReadinessAsync(Uri expectedManagementUri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Management, expectedManagementUri);
            PreflightCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<CommonConnectorConnectionSnapshot> EnrollAndConnectAsync(
            string oneTimeToken,
            string deviceDisplayName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnrollCalls++;
            ObservedToken = oneTimeToken;
            if (EnrollmentFailure is not null)
                return ValueTask.FromException<CommonConnectorConnectionSnapshot>(EnrollmentFailure);
            CurrentValue = OnEnroll?.Invoke() ?? CurrentValue;
            return ValueTask.FromResult(CurrentValue);
        }

        public ValueTask<bool> VerifyProtectedServiceAsync(
            string serviceId,
            string relativeUri,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("control-plane", serviceId);
            Assert.Equal("jobs/health", relativeUri);
            ProtectedServiceCalls++;
            OnProtectedService?.Invoke(this);
            return ValueTask.FromResult(ProtectedServiceAvailable);
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisconnectCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAccessClient : IConnectorEnrollmentClient, IConnectorDeviceAccessClient, INetworkOverlayClient
    {
        public DeviceEnrollmentResponse? Receipt { get; set; }
        public DeviceEnrollmentResponse? ResumeReceipt { get; set; }
        public ConnectorVpnTransportState VpnState { get; set; } = new(
            DeviceId, 7, "unknown", null, Management.AbsoluteUri, [], Now);
        public DeviceAccessProfile Profile { get; set; } = PlatformAccessPortsTests.Profile();
        public NetworkOverlaySnapshot Overlay { get; set; } = PlatformAccessPortsTests.Overlay();

        public ValueTask<DeviceEnrollmentResponse> EnrollAsync(string enrollmentToken, string deviceDisplayName, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<DeviceEnrollmentResponse>(new NotSupportedException());
        public ValueTask<DeviceEnrollmentResponse> ResumeAsync(CancellationToken cancellationToken = default) =>
            ResumeReceipt is null
                ? ValueTask.FromException<DeviceEnrollmentResponse>(new NotSupportedException())
                : ValueTask.FromResult(ResumeReceipt);
        public ValueTask<DeviceEnrollmentResponse?> GetReceiptAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Receipt);
        public ValueTask<DeviceAccessProfile> GetAccessProfileAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Profile);
        public ValueTask<ConnectorVpnBootstrap> GetVpnBootstrapAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ConnectorVpnBootstrap>(new NotSupportedException());
        public ValueTask<ConnectorVpnTransportState> GetVpnStateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(VpnState);
        public ValueTask<NetworkOverlaySnapshot> ConnectAsync(NetworkOverlayBootstrap bootstrap, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<NetworkOverlaySnapshot>(new NotSupportedException());
        public ValueTask<NetworkOverlaySnapshot> GetStatusAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Overlay);
    }

    private sealed class FakeSmbProbe : IExactSmbAccessProbe
    {
        public bool Result { get; init; }
        public ExactSmbAccessProbeRequest? LastRequest { get; private set; }
        public ValueTask<bool> VerifyAuthenticatedReadAsync(ExactSmbAccessProbeRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class FakePeerBindingProbe : IExactVpnPeerBindingProbe
    {
        public bool Result { get; init; }
        public ValueTask<bool> VerifyAsync(ExactVpnPeerBindingProbeRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(DeviceId, request.DeviceId);
            Assert.Equal("peer-1", request.PeerId);
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
