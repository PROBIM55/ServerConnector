using Connector.Desktop.Features.Attributes;
using Connector.Desktop.Features.Connector;
using Connector.Desktop.Features.Converter;
using Connector.Desktop.Features.Platform;
using Connector.Desktop.Features.Structura;
using Connector.Desktop.Features.Tekla.ModelSharing;
using Connector.Desktop.Features.Tekla.Patching;
using Connector.Desktop.Features.Tekla.Standard;
using Connector.Desktop.Features.Vpn;
using Connector.Desktop.Mvvm;
using Connector.Desktop.Services;
using Connector.Platform;
using Connector.AgrConversion.Service;

namespace Connector.Desktop.Shell;

// Composition root for the modular UI: builds the feature domains and their modules. The shell hosts domains
// (top-level tabs); each domain hosts feature modules (sub-tabs). New features register here — no MainWindow bloat.
//
// Migration state: only the new MVVM modules live here yet. The legacy Tekla features (Стандарт, Model Sharing)
// and the other top-level tabs (Коннектор, Structura, VPN) are still code-behind in MainWindow and will move
// into domains/modules one at a time. Future "Атрибуты" domain (mapping/validation/filling/KSI) plugs in here too.
public sealed class ShellViewModel
{
    public FeatureDomain Connector { get; }
    public FeatureDomain Tekla { get; }
    public FeatureDomain Structura { get; }
    public FeatureDomain Platform { get; }
    public FeatureDomain Vpn { get; }
    public FeatureDomain Attributes { get; }
    public FeatureDomain Converter { get; }

    public IReadOnlyList<FeatureDomain> Domains { get; }

    public ShellViewModel(IShellHost shellHost, IConnectorHost connectorHost,
        PlatformRuntimeFacade platformRuntime, IAgrPartConverter? partConverter = null,
        TeklaStandardService? teklaStandardService = null,
        VpnProvisioningService? vpnProvisioningService = null,
        ModelSharingProvisioningService? modelSharingProvisioningService = null,
        IfcExportPatchService? ifcExportPatchService = null)
    {
        ArgumentNullException.ThrowIfNull(platformRuntime);
        // Top-level "Коннектор" domain: the login/heartbeat FRONT-END (its single module hosts the lifted
        // ConnectorView, driven through the IConnectorHost seam). The connect/heartbeat ENGINE stays in MainWindow.
        Connector = new FeatureDomain("Коннектор", new IFeatureModule[]
        {
            new ConnectorModule(connectorHost),
        });

        Tekla = new FeatureDomain("Tekla", new IFeatureModule[]
        {
            new StandardModule(shellHost, teklaStandardService),
            new ModelSharingModule(modelSharingProvisioningService),
            new PatchingModule(ifcExportPatchService),
        });

        Structura = new FeatureDomain("Structura", new IFeatureModule[]
        {
            new StructuraModule(),
        });

        Platform = new FeatureDomain("Platform", new IFeatureModule[]
        {
            new PlatformModule(platformRuntime),
        });

        Vpn = new FeatureDomain("Общая папка (VPN)", new IFeatureModule[]
        {
            new VpnModule(vpnProvisioningService),
        });

        // Future domain: attribute mapping / validation / filling / IDS-КСИ. One placeholder module for now;
        // real features plug in here as sibling modules without touching MainWindow.
        Attributes = new FeatureDomain("Атрибуты", new IFeatureModule[]
        {
            new AttributesModule(),
        });

        // «Конвертер» (C2b): модели АГР → .glb.zip для Студии, локально (Р9). Две подвкладки над одной вид-моделью.
        var converter = new ConverterModule(partConverter: partConverter);
        Converter = new FeatureDomain("Конвертер", new IFeatureModule[]
        {
            converter,
            converter.History,
        });

        Domains = new[] { Connector, Tekla, Structura, Platform, Vpn, Attributes, Converter };
    }
}
