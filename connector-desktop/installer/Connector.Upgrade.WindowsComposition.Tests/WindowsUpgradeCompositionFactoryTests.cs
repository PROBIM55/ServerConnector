using System.Net;
using Connector.Access.Client;
using Connector.Access.Contracts;
using Connector.Network;
using Connector.Upgrade.PlatformAccess;
using Connector.Upgrade.Velopack;
using Connector.Upgrade.VelopackPayload;
using Connector.Upgrade.WindowsComposition;
using Connector.Upgrade.WindowsHost;
using Connector.Upgrade.WindowsPayload;
using Platform.Connector.Core;
using Xunit;

namespace Connector.Upgrade.WindowsComposition.Tests;

public sealed class WindowsUpgradeCompositionFactoryTests
{
    [Fact]
    public async Task Production_surface_cannot_accept_arbitrary_success_peer_probe_and_factory_stays_blocked()
    {
        var arbitrarySuccess = new FixtureExactPeerProbe();
        var client = new ReadyRevocationClient();
        var preparation = await WindowsUpgradeCompositionFactory.PrepareForCurrentUserAsync(
            Options(),
            Bindings(client));

        Assert.False(preparation.IsReady);
        Assert.Null(preparation.Composition);
        Assert.Contains(preparation.Blockers,
            blocker => blocker.Code == WindowsUpgradeCompositionBlockerCode.ExactVpnPeerBindingUnavailable);
        Assert.DoesNotContain(
            typeof(WindowsUpgradeRuntimeBindings).GetConstructors().SelectMany(constructor => constructor.GetParameters()),
            parameter => typeof(IExactVpnPeerBindingProbe).IsAssignableFrom(parameter.ParameterType));
        Assert.DoesNotContain(
            typeof(WindowsUpgradeCompositionFactory).GetMethods()
                .Where(method => method.IsPublic)
                .SelectMany(method => method.GetParameters()),
            parameter => typeof(IExactVpnPeerBindingProbe).IsAssignableFrom(parameter.ParameterType));
        Assert.Equal(0, arbitrarySuccess.VerificationCalls);
        Assert.Equal(0, client.RevocationReadinessCalls);
        Assert.Equal(0, client.EnrollmentCalls);
    }

    [Fact]
    public async Task Production_factory_reports_missing_self_revoke_without_exposing_legacy_executor()
    {
        var client = new EnrollmentWithoutSelfRevocation();
        var preparation = await WindowsUpgradeCompositionFactory.PrepareForCurrentUserAsync(
            Options(),
            Bindings(client));

        Assert.False(preparation.IsReady);
        Assert.Null(preparation.Composition);
        Assert.Contains(preparation.Blockers,
            blocker => blocker.Code == WindowsUpgradeCompositionBlockerCode.ExactDeviceSelfRevocationUnavailable);
        Assert.Equal(0, client.EnrollmentCalls);
    }

    [Fact]
    public async Task ConcreteNetBirdClientAndAttestationRoutePassStructuralGateBeforeAnyToken()
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), $"upgrade-staging-{Guid.NewGuid():N}");
        var enrollment = new HttpConnectorEnrollmentClient(
            new ConnectorAccessClientOptions
            {
                ServiceBaseUri = new Uri("https://connector.example.test/api/platform/connector/access/v1/"),
                TrustedIssuerCertificateSha256 = new string('a', 64)
            },
            new FileConnectorEnrollmentStateStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        var overlay = new NetBirdCliClient(new NetBirdCliOptions
        {
            ExecutablePath = @"C:\Program Files\NetBird\netbird.exe"
        });
        var gate = new ProtectedServiceNetworkGate(
            overlay,
            new HttpProtectedServiceProbe(),
            [new ProtectedServiceDefinition(
                "peer-attestation",
                new Uri("https://connector.example.test/"),
                ["100.64.0.1"],
                "api/platform/connector/access/v1/jobs/health")],
            enrollment);
        var bindings = new WindowsUpgradeRuntimeBindings(
            enrollment, overlay, gate, new FixtureTransport(),
            new ManagedOverlayDestinationPolicy(["100.64.0.10"]));
        var preparation = await WindowsUpgradeCompositionFactory.PrepareForCurrentUserAsync(
            Options() with { NetBirdPackageStagingRoot = stagingRoot }, bindings);

        Assert.False(preparation.IsReady);
        Assert.Null(preparation.Composition);
        Assert.Contains(preparation.Blockers, blocker => OperatingSystem.IsWindows()
            ? blocker.Code == WindowsUpgradeCompositionBlockerCode.MachineBoundaryNotReady &&
              blocker.EvidenceId == "original-user-session-sid-mismatch"
            : blocker.Code == WindowsUpgradeCompositionBlockerCode.WindowsHostRequired);
        Assert.False(Directory.Exists(stagingRoot));
    }

    private static WindowsUpgradeCompositionOptions Options() => new(
        new WindowsHostProductionOptions(
            new WindowsHostSession(Guid.NewGuid().ToString("N"), "S-1-5-21-1000"),
            new WindowsRollbackPayloadSources(
                @"C:\fixture\StructuraConnector.msi",
                @"C:\fixture\PlatformConnector.msi"),
            @"C:\fixture\NetBird.msi",
            new VelopackSetupPin(
                UnifiedVelopackApplication.PackId,
                "1.0.0",
                1,
                new string('a', 64)),
            TimeSpan.FromSeconds(30)),
        new Uri("https://platform.example/api/platform/connector/access/v1/profile"),
        new Uri("https://netbird.example"),
        "fixture-device",
        new VelopackSetupSource(@"C:\fixture\Setup.exe"),
        new VelopackSetupSignaturePolicy(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AA" }));

    private static WindowsUpgradeRuntimeBindings Bindings(IConnectorEnrollmentClient client) => new(
        client,
        new FixtureOverlay(),
        new FixtureNetworkGate(),
        new FixtureTransport(),
        new ManagedOverlayDestinationPolicy(["100.64.0.10"]));

    private class EnrollmentWithoutSelfRevocation : IConnectorEnrollmentClient, IConnectorDeviceAccessClient
    {
        public int EnrollmentCalls { get; private set; }

        public ValueTask<DeviceEnrollmentResponse> EnrollAsync(
            string enrollmentToken,
            string deviceDisplayName,
            CancellationToken cancellationToken = default)
        {
            EnrollmentCalls++;
            throw new InvalidOperationException("A blocked factory must not consume a token.");
        }

        public ValueTask<DeviceEnrollmentResponse> ResumeAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A blocked factory must remain read-only.");

        public ValueTask<DeviceEnrollmentResponse?> GetReceiptAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A blocked factory must remain read-only.");

        public ValueTask<DeviceAccessProfile> GetAccessProfileAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A blocked factory must remain read-only.");

        public ValueTask<ConnectorVpnBootstrap> GetVpnBootstrapAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A blocked factory must remain read-only.");

        public ValueTask<ConnectorVpnTransportState> GetVpnStateAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A blocked factory must remain read-only.");
    }

    private sealed class ReadyRevocationClient : EnrollmentWithoutSelfRevocation,
        IConnectorExactDeviceRevocationClient
    {
        public int RevocationReadinessCalls { get; private set; }

        public ValueTask<bool> InspectExactDeviceRevocationAsync(CancellationToken cancellationToken = default)
        {
            RevocationReadinessCalls++;
            return ValueTask.FromResult(true);
        }

        public ValueTask RevokeAndConfirmExactAsync(
            string deviceId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A blocked factory must not mutate enrollment state.");
    }

    private sealed class FixtureExactPeerProbe : IExactVpnPeerBindingProbe
    {
        public int VerificationCalls { get; private set; }

        public ValueTask<bool> VerifyAsync(
            ExactVpnPeerBindingProbeRequest request,
            CancellationToken cancellationToken)
        {
            VerificationCalls++;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class FixtureOverlay : INetworkOverlayClient
    {
        public ValueTask<NetworkOverlaySnapshot> ConnectAsync(
            NetworkOverlayBootstrap bootstrap,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A blocked factory must not connect NetBird.");

        public ValueTask<NetworkOverlaySnapshot> GetStatusAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A blocked factory must not inspect runtime overlay state.");
    }

    private sealed class FixtureNetworkGate : IProtectedServiceNetworkGate
    {
        public ValueTask<ProtectedServiceRoute> ResolveAndProbeAsync(
            ProtectedOverlayTransportIdentity identity,
            string serviceId,
            string relativeUri,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A blocked factory must not open protected routes.");
    }

    private sealed class FixtureTransport : ICommonConnectorRequestTransport
    {
        public Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string relativeUri,
            object? body,
            IReadOnlyDictionary<string, string> managedHeaders,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A blocked factory must not probe SMB.");
    }
}
