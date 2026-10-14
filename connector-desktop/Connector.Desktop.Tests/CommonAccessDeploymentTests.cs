using Connector.CommonAccess.Runtime;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class CommonAccessDeploymentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "connector-deployment-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReadinessUsesPublicEnrollmentEndpointBeforeOverlayConnection()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "connector-access.json"), """
            {
              "enabled": true,
              "serviceBaseUri": "https://connector-access.test/api/platform/connector/access/v1/",
              "issuerCertificateSha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
              "netBirdManagementUri": "https://netbird.test/",
              "services": [{
                "serviceId": "control-plane",
                "baseUri": "https://connector-gateway.test:24443/api/platform/connector/access/v1/",
                "allowedOverlayDestinations": ["100.90.0.1"],
                "healthPath": "jobs/health"
              }]
            }
            """);

        var deployment = CommonAccessDeploymentFactory.OpenFixture(Path.Combine(_root, "Agent"), _root);

        Assert.True(deployment.IsConfigured);
        Assert.Equal(new Uri("https://connector-access.test/api/platform/connector/access/v1/profile"), deployment.PublicProfileUri);
        Assert.Equal(new Uri("https://connector-gateway.test:24443/api/platform/connector/access/v1/"), deployment.ControlPlaneBaseUri);
        Assert.NotNull(deployment.ProtectedNetworkGate);
        Assert.False(File.Exists(deployment.StateFilePath));
    }

    [Fact]
    public void MissingDeploymentCannotSelectPublicOrOverlayEndpoints()
    {
        var deployment = CommonAccessDeploymentFactory.OpenFixture(Path.Combine(_root, "Agent"), _root);

        Assert.False(deployment.IsConfigured);
        Assert.Null(deployment.PublicProfileUri);
        Assert.Null(deployment.ControlPlaneBaseUri);
        Assert.False(Directory.Exists(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
