using Connector.Desktop.Services;
using Connector.Access.Client;
using Connector.Network;
using Platform.Connector.Core;
using System.Net;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class CommonAccessRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "connector-common-access-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExistingDeploymentWithoutManagementPin_RemainsConfiguredForServerBoundResume()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "connector-access.json"), """
            {
              "enabled": true,
              "serviceBaseUri": "https://enrollment.test/",
              "issuerCertificateSha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
              "services": [{
                "serviceId": "control-plane",
                "baseUri": "https://control.test/api/platform/connector/access/v1/",
                "allowedOverlayDestinations": ["100.90.0.1"],
                "healthPath": "jobs/health"
              }]
            }
            """);

        var runtime = CommonAccessRuntime.CreateForFixture(_root, _root);

        Assert.True(runtime.IsConfigured);
    }

    [Fact]
    public async Task MissingDeploymentDoesNotConsumeNewTokenOrDowngradeToLegacy()
    {
        var runtime = CommonAccessRuntime.CreateForFixture(_root, _root);
        Assert.False(runtime.IsConfigured);
        Assert.False(runtime.ShouldUse("legacy-token"));
        Assert.True(runtime.ShouldUse("cea1.one-time-token"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ConnectAsync("cea1.one-time-token", false, CancellationToken.None));
        Assert.True(runtime.IsSelected);
        Assert.False(runtime.IsReady);
        Assert.False(runtime.HasStoredCredential);
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"enabled\":true,\"serviceBaseUri\":\"http://unprotected.invalid\"}")]
    public async Task InvalidDeploymentFailsClosedAndDoesNotPersistEnrollment(string configuration)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "connector-access.json"), configuration);
        var runtime = CommonAccessRuntime.CreateForFixture(_root, _root);
        Assert.False(runtime.IsConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ConnectAsync("cea1.fixture", false, CancellationToken.None));
        Assert.False(runtime.HasStoredCredential);
        Assert.Equal(configuration, File.ReadAllText(Path.Combine(_root, "connector-access.json")));
    }

    [Fact]
    public async Task ExistingEnrollmentCannotFallBackToLegacyWhenConfigurationIsMissing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Access"));
        var state = Path.Combine(_root, "Access", "connector-enrollment-v1.json");
        File.WriteAllText(state, "corrupt-existing-credential");
        var runtime = CommonAccessRuntime.CreateForFixture(_root, _root);
        Assert.True(runtime.ShouldUse());
        Assert.True(runtime.ShouldUse("legacy-token"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ConnectAsync(null, true, CancellationToken.None));
        Assert.False(runtime.IsReady);
        Assert.Equal("corrupt-existing-credential", File.ReadAllText(state));
    }

    [Fact]
    public void UpgradePorts_ReturnOnlyExistingOwnedComponentsAndPreserveTransportAndSmbPolicy()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "connector-access.json"), """
            {
              "enabled": true,
              "serviceBaseUri": "https://enrollment.test/",
              "issuerCertificateSha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
              "smbOverlayDestinations": ["100.90.0.5"],
              "services": [{
                "serviceId": "control-plane",
                "baseUri": "https://control.test/api/platform/connector/access/v1/",
                "allowedOverlayDestinations": ["100.90.0.1"],
                "healthPath": "jobs/health"
              }]
            }
            """);
        var runtime = CommonAccessRuntime.CreateForFixture(_root, _root);

        var result = runtime.TryGetUpgradeRuntimePorts();

        Assert.True(runtime.IsConfigured);
        Assert.True(result.IsAvailable);
        Assert.Null(result.Blocker);
        Assert.IsType<HttpConnectorEnrollmentClient>(result.Ports!.EnrollmentClient);
        Assert.IsType<NetBirdCliClient>(result.Ports.OverlayClient);
        Assert.IsType<ProtectedServiceNetworkGate>(result.Ports.ProtectedNetworkGate);
        Assert.Same(runtime, result.Ports.Transport);
        Assert.True(result.Ports.SmbDestinations.Contains(IPAddress.Parse("100.90.0.5")));

        var repeated = runtime.TryGetUpgradeRuntimePorts();
        Assert.Same(result.Ports.EnrollmentClient, repeated.Ports!.EnrollmentClient);
        Assert.Same(result.Ports.OverlayClient, repeated.Ports.OverlayClient);
        Assert.Same(result.Ports.ProtectedNetworkGate, repeated.Ports.ProtectedNetworkGate);
        Assert.Same(result.Ports.Transport, repeated.Ports.Transport);
        Assert.Same(result.Ports.SmbDestinations, repeated.Ports.SmbDestinations);
    }

    [Fact]
    public void UpgradePorts_WhenDeploymentIsMissing_ReturnsTypedUnavailableResult()
    {
        var runtime = CommonAccessRuntime.CreateForFixture(_root, _root);
        var result = runtime.TryGetUpgradeRuntimePorts();

        Assert.False(result.IsAvailable);
        Assert.Null(result.Ports);
        Assert.Equal(CommonAccessUpgradePortsBlocker.DeploymentNotConfigured, result.Blocker);
        Assert.False(runtime.HasStoredCredential);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
