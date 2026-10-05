using System.Text.Json;
using Connector.Access.Client;
using Connector.Access.Contracts;
using Connector.Network;
using Platform.Connector.Core;

namespace Connector.CommonAccess.Runtime;

public sealed record CommonAccessDeployment(
    string StateFilePath,
    CommonConnectorConnectionCoordinator? Coordinator,
    IConnectorEnrollmentClient? EnrollmentClient,
    INetworkOverlayClient? OverlayClient,
    IProtectedServiceNetworkGate? ProtectedNetworkGate,
    Uri? ControlPlaneBaseUri,
    Uri? NetBirdManagementUri,
    ManagedOverlayDestinationPolicy SmbDestinations)
{
    public bool IsConfigured => Coordinator is not null && EnrollmentClient is not null &&
        OverlayClient is not null && ProtectedNetworkGate is not null;

    public ICommonConnectorRequestTransport CreateRequestTransport()
    {
        if (Coordinator is null)
            throw new InvalidOperationException("The protected control plane is not configured.");
        return new ProtectedControlPlaneRequestTransport(Coordinator.OpenProtectedServiceAsync);
    }
}

/// <summary>
/// One source for the machine-controlled deployment identity used by the desktop and original-user
/// bootstrapper. Invalid or absent admin configuration yields unavailable ports, never a fallback
/// endpoint under the user's profile.
/// </summary>
public static class CommonAccessDeploymentFactory
{
    private const string ControlPlaneServiceId = "control-plane";

    public static CommonAccessDeployment OpenProduction(string runtimeRoot) => Open(runtimeRoot, null);

    internal static CommonAccessDeployment OpenFixture(string runtimeRoot, string applicationDirectory) =>
        Open(runtimeRoot, applicationDirectory);

    private static CommonAccessDeployment Open(string runtimeRoot, string? applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        var store = new FileConnectorEnrollmentStateStore(Path.Combine(runtimeRoot, "Access"));
        var configurationPath = Path.Combine(applicationDirectory ?? GetMachineNetworkRoot(), "connector-access.json");
        CommonConnectorConnectionCoordinator? coordinator = null;
        Uri? controlPlaneBaseUri = null;
        Uri? netBirdManagementUri = null;
        ManagedOverlayDestinationPolicy smbDestinations = new([]);
        IConnectorEnrollmentClient? enrollmentClient = null;
        INetworkOverlayClient? overlayClient = null;
        IProtectedServiceNetworkGate? protectedNetworkGate = null;
        try
        {
            if (File.Exists(configurationPath))
            {
                if (applicationDirectory is null &&
                    !new WindowsMachineProtectedPathTrustPolicy().AreTrusted([configurationPath]))
                    throw new UnauthorizedAccessException("The connector access deployment is not machine protected.");
                var file = new FileInfo(configurationPath);
                if (file.Length is <= 0 or > 65536) throw new InvalidDataException("Invalid access configuration size.");
                var configuration = JsonSerializer.Deserialize<DeploymentConfiguration>(File.ReadAllText(configurationPath),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing access configuration.");
                if (configuration.Enabled)
                {
                    Uri? expectedManagementUri = null;
                    if (!string.IsNullOrWhiteSpace(configuration.NetBirdManagementUri) &&
                        (!Uri.TryCreate(configuration.NetBirdManagementUri, UriKind.Absolute, out expectedManagementUri) ||
                         expectedManagementUri.Scheme != Uri.UriSchemeHttps ||
                         !string.IsNullOrEmpty(expectedManagementUri.UserInfo)))
                        throw new InvalidDataException("Invalid NetBird management URI.");
                    smbDestinations = new ManagedOverlayDestinationPolicy(configuration.SmbOverlayDestinations);
                    var controlPlane = configuration.Services.Single(service => service.ServiceId == ControlPlaneServiceId);
                    if (controlPlane.BaseUri is null || controlPlane.HealthPath != "jobs/health" ||
                        controlPlane.BaseUri.AbsolutePath.TrimEnd('/') != "/api/platform/connector/access/v1")
                        throw new InvalidDataException("Invalid common control plane route.");
                    var enrollment = new HttpConnectorEnrollmentClient(new ConnectorAccessClientOptions
                    {
                        ServiceBaseUri = new Uri(configuration.ServiceBaseUri, UriKind.Absolute),
                        TrustedIssuerCertificateSha256 = configuration.IssuerCertificateSha256
                    }, store);
                    var netBirdExecutable = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetBird", "netbird.exe");
                    if (!string.IsNullOrWhiteSpace(configuration.NetBirdExecutablePath) &&
                        !string.Equals(Path.GetFullPath(configuration.NetBirdExecutablePath), netBirdExecutable,
                            StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Only the pinned system NetBird executable is supported.");
                    var overlay = new NetBirdCliClient(new NetBirdCliOptions
                    {
                        ExecutablePath = netBirdExecutable,
                        WindowsInstallationOwnership = CreateNetBirdOwnershipOptions()
                    });
                    var gate = new ProtectedServiceNetworkGate(overlay, new HttpProtectedServiceProbe(), configuration.Services, enrollment);
                    enrollmentClient = enrollment;
                    overlayClient = overlay;
                    protectedNetworkGate = gate;
                    coordinator = new CommonConnectorConnectionCoordinator(enrollment, overlay, gate);
                    controlPlaneBaseUri = controlPlane.BaseUri;
                    netBirdManagementUri = expectedManagementUri;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            // A malformed or user-writable deployment cannot select an unprotected public route.
        }
        return new CommonAccessDeployment(store.StateFilePath, coordinator, enrollmentClient, overlayClient,
            protectedNetworkGate, controlPlaneBaseUri, netBirdManagementUri, smbDestinations);
    }

    private static WindowsNetBirdInstallationOwnershipOptions CreateNetBirdOwnershipOptions()
    {
        var root = GetMachineNetworkRoot();
        return new WindowsNetBirdInstallationOwnershipOptions
        {
            PreflightScriptPath = Path.Combine(root, "scripts", "netbird_windows_preflight.ps1"),
            VerifierScriptPath = Path.Combine(root, "scripts", "verify_netbird_installer.ps1"),
            IdentityLockPath = Path.Combine(root, "infra", "netbird", "v0.79.0-windows-x64.lock.json"),
            PinnedMsiPath = Path.Combine(root, "installer", "netbird_installer_0.79.0_windows_amd64.msi"),
            PreflightScriptSha256 = "8BF7CFB9C667374F7F165A37C42C2B47F9FB4476E89A36995358375CA59490C2",
            VerifierScriptSha256 = "DFFFA2EC3BBDFB57583F66AB651F12259AFFFBCF39AA014B11411F9C152EA834",
            IdentityLockSha256 = "EE48F195FAE2A08B83C0A44F238C237C79F05F27BCA8DBC995542B1FCD23FA96"
        };
    }

    private static string GetMachineNetworkRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Structura Connector", "NetBird");

    private sealed class DeploymentConfiguration
    {
        public bool Enabled { get; init; }
        public string ServiceBaseUri { get; init; } = "";
        public string IssuerCertificateSha256 { get; init; } = "";
        public string NetBirdExecutablePath { get; init; } = "";
        public string NetBirdManagementUri { get; init; } = "";
        public List<ProtectedServiceDefinition> Services { get; init; } = [];
        public List<string> SmbOverlayDestinations { get; init; } = [];
    }
}
