namespace Connector.Updates;

/// <summary>
/// Fixed machine locations shared by the update service and the application.
/// <list type="bullet">
/// <item><c>%ProgramData%\Structura Connector\Updates</c> — holds only <c>update-status.json</c>; written by SYSTEM
/// and administrators, read by users.</item>
/// <item><c>%ProgramData%\Structura Connector\UpdateSignals</c> — users may create their own two flag files;
/// the service only ever reads them (never writes, renames or deletes anything there).</item>
/// </list>
/// The service's own state, downloads and journal are not here: they live in
/// <c>Program Files\Platform Connector\Updater\state</c>, where an ordinary user can never create anything
/// (see <c>ServicePaths</c> of the service).
/// </summary>
public sealed class UpdateLocations
{
    public const string ServiceName = "PlatformConnectorUpdater";
    public const string ProductRegistryKey = @"Software\Structura\PlatformConnector";
    public const string ChannelValueName = "UpdateChannel";
    public const string InstallRootValueName = "InstallRoot";
    public const string ApplicationExecutable = "Connector.Desktop.exe";
    public const string StatusFileName = "update-status.json";
    /// <summary>First manual installation, and the way out when the update service is broken.</summary>
    public const string ManualDownloadUrl = "https://bimplatforma.ru/api/platform/connectors/unified/stable/download";

    public UpdateLocations(string machineRoot)
    {
        if (string.IsNullOrWhiteSpace(machineRoot) || !Path.IsPathFullyQualified(machineRoot))
            throw new ArgumentException("A fully qualified root is required.", nameof(machineRoot));
        MachineRoot = Path.GetFullPath(machineRoot);
        UpdatesRoot = Path.Combine(MachineRoot, "Updates");
        Signals = Path.Combine(MachineRoot, "UpdateSignals");
        StatusFile = Path.Combine(UpdatesRoot, StatusFileName);
    }

    /// <summary>The production locations below <c>%ProgramData%\Structura Connector</c>.</summary>
    public static UpdateLocations Machine() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "Structura Connector"));

    public string MachineRoot { get; }
    public string UpdatesRoot { get; }
    public string Signals { get; }
    public string StatusFile { get; }
}
