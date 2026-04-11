namespace Connector.Desktop.Models;

public sealed class AppSettings
{
    public string ServerUrl { get; set; } = "https://server.structura-most.ru";
    public string UpdateManifestUrl { get; set; } = "https://server.structura-most.ru/updates/latest.json";
    public string DeviceId { get; set; } = "";
    public string TokenCipherBase64 { get; set; } = "";
    public string SmbLogin { get; set; } = "";
    public string SmbPasswordCipherBase64 { get; set; } = "";
    public string SmbSharePath { get; set; } = @"\\62.113.36.107\BIM_Models";
    public int HeartbeatSeconds { get; set; } = 60;
    public bool AutoStart { get; set; } = true;
    public string TeklaStandardManifestUrl { get; set; } = "https://server.structura-most.ru/updates/tekla/firm/latest.json";
    public string TeklaStandardLocalPath { get; set; } = @"C:\Company\TeklaFirm";
    public string TeklaStandardInstalledVersion { get; set; } = "";
    public string TeklaStandardTargetVersion { get; set; } = "";
    public string TeklaStandardInstalledRevision { get; set; } = "";
    public string TeklaStandardTargetRevision { get; set; } = "";
    public DateTimeOffset? TeklaStandardLastCheckUtc { get; set; }
    public DateTimeOffset? TeklaStandardLastSuccessUtc { get; set; }
    public bool TeklaStandardPendingAfterClose { get; set; }
    public string TeklaStandardLastError { get; set; } = "";
    public string TeklaStandardRepoUrl { get; set; } = "";
    public string TeklaStandardRepoRef { get; set; } = "";
    public string TeklaStandardRepoSubdir { get; set; } = "";
    public string TeklaPublishSourcePath { get; set; } = @"\\62.113.36.107\BIM_Models\Tekla\02_ПАПКА ФИРМЫ\01_XS_FIRM";
    public string TeklaExtensionsManifestUrl { get; set; } = "https://server.structura-most.ru/updates/tekla/extensions/latest.json";
    public string TeklaExtensionsLocalPath { get; set; } = @"C:\TeklaStructures\2025.0\Environments\common\Extensions";
    public string TeklaExtensionsInstalledVersion { get; set; } = "";
    public string TeklaExtensionsTargetVersion { get; set; } = "";
    public string TeklaExtensionsInstalledRevision { get; set; } = "";
    public string TeklaExtensionsTargetRevision { get; set; } = "";
    public DateTimeOffset? TeklaExtensionsLastCheckUtc { get; set; }
    public DateTimeOffset? TeklaExtensionsLastSuccessUtc { get; set; }
    public bool TeklaExtensionsPendingAfterClose { get; set; }
    public string TeklaExtensionsLastError { get; set; } = "";
    public string TeklaExtensionsRepoUrl { get; set; } = "";
    public string TeklaExtensionsRepoRef { get; set; } = "";
    public string TeklaExtensionsRepoSubdir { get; set; } = "";
    public string TeklaExtensionsPublishSourcePath { get; set; } = @"\\62.113.36.107\BIM_Models\Tekla\02_ПАПКА ФИРМЫ\07_Extensions";
    public string TeklaLibrariesManifestUrl { get; set; } = "https://server.structura-most.ru/updates/tekla/libraries/latest.json";
    public string TeklaLibrariesLocalPath { get; set; } = "";
    public string TeklaLibrariesInstalledVersion { get; set; } = "";
    public string TeklaLibrariesTargetVersion { get; set; } = "";
    public string TeklaLibrariesInstalledRevision { get; set; } = "";
    public string TeklaLibrariesTargetRevision { get; set; } = "";
    public DateTimeOffset? TeklaLibrariesLastCheckUtc { get; set; }
    public DateTimeOffset? TeklaLibrariesLastSuccessUtc { get; set; }
    public bool TeklaLibrariesPendingAfterClose { get; set; }
    public string TeklaLibrariesLastError { get; set; } = "";
    public string TeklaLibrariesRepoUrl { get; set; } = "";
    public string TeklaLibrariesRepoRef { get; set; } = "";
    public string TeklaLibrariesRepoSubdir { get; set; } = "";
    public string TeklaLibrariesPublishSourcePath { get; set; } = @"\\62.113.36.107\BIM_Models\Tekla\02_ПАПКА ФИРМЫ\02_Grasshopper\Libraries\8";
    public string StructuraSpeckleUrl { get; set; } = "https://speckle.structura-most.ru";
    public string StructuraSpeckleLogin { get; set; } = "";
    public string StructuraSpecklePasswordCipherBase64 { get; set; } = "";
    public string StructuraNextcloudUrl { get; set; } = "https://cloud.structura-most.ru";
    public string StructuraNextcloudLogin { get; set; } = "";
    public string StructuraNextcloudPasswordCipherBase64 { get; set; } = "";
    public bool IsSystemAdmin { get; set; }
    public bool IsFirmAdmin { get; set; }
}
