using System.Net.Http;

namespace Connector.Desktop.Services;

/// <summary>
/// Explicit composition boundary for the native window. Production keeps the
/// existing defaults; native hosts can provide an isolated settings/runtime root
/// and keep the real tray icon hidden without touching user state.
/// </summary>
public sealed class MainWindowServices
{
    public MainWindowServices(
        SettingsService settings,
        IAutoStartService autoStart,
        ConnectorRuntimeServices runtime,
        TeklaStandardService windowTeklaStandard,
        TeklaStandardService shellTeklaStandard,
        VpnProvisioningService vpnProvisioning,
        ModelSharingProvisioningService modelSharingProvisioning,
        IfcExportPatchService ifcExportPatch,
        PackageUpdateService packageUpdates,
        bool trayIconVisible = true,
        bool allowOwnedFixtureClose = false,
        bool runLoadedExternalActions = true,
        string? webViewUserDataDirectory = null,
        Action? loadedInitializationCompleted = null)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        AutoStart = autoStart ?? throw new ArgumentNullException(nameof(autoStart));
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        WindowTeklaStandard = windowTeklaStandard ?? throw new ArgumentNullException(nameof(windowTeklaStandard));
        ShellTeklaStandard = shellTeklaStandard ?? throw new ArgumentNullException(nameof(shellTeklaStandard));
        VpnProvisioning = vpnProvisioning ?? throw new ArgumentNullException(nameof(vpnProvisioning));
        ModelSharingProvisioning = modelSharingProvisioning ?? throw new ArgumentNullException(nameof(modelSharingProvisioning));
        IfcExportPatch = ifcExportPatch ?? throw new ArgumentNullException(nameof(ifcExportPatch));
        PackageUpdates = packageUpdates ?? throw new ArgumentNullException(nameof(packageUpdates));
        TrayIconVisible = trayIconVisible;
        AllowOwnedFixtureClose = allowOwnedFixtureClose;
        RunLoadedExternalActions = runLoadedExternalActions;
        WebViewUserDataDirectory = webViewUserDataDirectory;
        LoadedInitializationCompleted = loadedInitializationCompleted;
    }

    public SettingsService Settings { get; }
    public IAutoStartService AutoStart { get; }
    public ConnectorRuntimeServices Runtime { get; }
    public TeklaStandardService WindowTeklaStandard { get; }
    public TeklaStandardService ShellTeklaStandard { get; }
    public VpnProvisioningService VpnProvisioning { get; }
    public ModelSharingProvisioningService ModelSharingProvisioning { get; }
    public IfcExportPatchService IfcExportPatch { get; }
    public PackageUpdateService PackageUpdates { get; }
    public bool TrayIconVisible { get; }
    public bool AllowOwnedFixtureClose { get; }
    public bool RunLoadedExternalActions { get; }
    public string? WebViewUserDataDirectory { get; }
    public Action? LoadedInitializationCompleted { get; }

    public static MainWindowServices CreateDefault() => new(
        new SettingsService(),
        new AutoStartService(),
        new ConnectorRuntimeServices(),
        new TeklaStandardService(new HttpClient { Timeout = TimeSpan.FromSeconds(25) }),
        new TeklaStandardService(new HttpClient { Timeout = TimeSpan.FromSeconds(25) }),
        new VpnProvisioningService(),
        new ModelSharingProvisioningService(),
        new IfcExportPatchService(),
        PackageUpdateService.CreateDefault());
}
