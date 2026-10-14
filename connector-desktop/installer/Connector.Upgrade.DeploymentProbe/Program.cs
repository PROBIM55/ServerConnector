using System.Net;
using System.Text.Json;
using Connector.Access.Client;
using Connector.CommonAccess.Runtime;

if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
    Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
    throw new InvalidOperationException("This read-only production preflight requires disposable hosted Windows.");

var root = Path.Combine(Path.GetTempPath(), "StructuraDeploymentProbe", Guid.NewGuid().ToString("N"));
var deployment = CommonAccessDeploymentFactory.OpenProduction(root);
var expected = new Uri("https://connector-access.structura-most.ru/api/platform/connector/access/v1/profile");
if (!deployment.IsConfigured || deployment.PublicProfileUri != expected || deployment.ControlPlaneBaseUri?.Host != "connector-gateway.structura-most.ru")
    throw new InvalidOperationException("The installed public and protected deployment routes differ.");
using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
using var response = await client.GetAsync(deployment.PublicProfileUri);
using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
if (response.StatusCode != HttpStatusCode.Unauthorized ||
    !payload.RootElement.TryGetProperty("code", out var code) || code.GetString() != "device_unauthorized")
    throw new InvalidOperationException("Public profile readiness failed.");
if (deployment.EnrollmentClient is not IConnectorExactDeviceRevocationClient revocation ||
    !await revocation.InspectExactDeviceRevocationAsync())
    throw new InvalidOperationException("The installed enrollment configuration cannot probe exact self-revocation.");
if (File.Exists(deployment.StateFilePath))
    throw new InvalidOperationException("A read-only preflight created an enrollment credential.");
Console.WriteLine("Production profile and exact self-revocation preflight succeeded with normal TLS and no token.");
