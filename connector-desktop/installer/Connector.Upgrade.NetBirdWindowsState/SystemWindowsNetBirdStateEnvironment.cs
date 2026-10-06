using System.Security.Cryptography;
using System.Text;
using Connector.Upgrade.NetBirdMachine;

namespace Connector.Upgrade.NetBirdWindowsState;

internal sealed class SystemWindowsNetBirdStateEnvironment : IWindowsNetBirdStateEnvironment
{
    private readonly string _cliPath;
    private readonly string _configurationPath;
    private readonly string _ownerRoot;
    private readonly string _ownerMarkerPath;
    private readonly WindowsNetBirdNativeInventory _inventory;
    private readonly WindowsNetBirdProtectedStorage _storage;

    internal SystemWindowsNetBirdStateEnvironment()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("NetBird Windows state inventory is available only on Windows.");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programFiles) || string.IsNullOrWhiteSpace(programData))
            throw new InvalidOperationException("Windows machine roots could not be resolved.");

        _cliPath = Path.GetFullPath(Path.Combine(programFiles, "Netbird", "netbird.exe"));
        _configurationPath = Path.GetFullPath(Path.Combine(programData, "Netbird", "config.json"));
        _ownerRoot = Path.GetFullPath(Path.Combine(
            programData,
            "StructuraConnectorInstaller",
            "NetBirdState"));
        _ownerMarkerPath = Path.Combine(_ownerRoot, "owner-v2.json");
        _storage = new WindowsNetBirdProtectedStorage(programData);
        _inventory = new WindowsNetBirdNativeInventory(
            _cliPath,
            _configurationPath,
            _storage);
    }

    public string ConfigurationPath => _configurationPath;

    public WindowsNetBirdSnapshot Capture()
    {
        var inventoryComplete = true;
        IReadOnlyList<WindowsNetBirdRegistration> registrations;
        WindowsNetBirdServiceSnapshot service;
        WindowsNetBirdCliSnapshot cli;
        try
        {
            registrations = _inventory.ReadMsiRegistrations();
        }
        catch
        {
            inventoryComplete = false;
            registrations = [];
        }

        try
        {
            service = _inventory.ReadService();
        }
        catch
        {
            service = new WindowsNetBirdServiceSnapshot(
                false, false, "NetBird", string.Empty, -1, string.Empty, false, string.Empty);
        }

        try
        {
            cli = _inventory.ReadCli();
        }
        catch
        {
            cli = new WindowsNetBirdCliSnapshot(
                false, File.Exists(_cliPath), _cliPath, string.Empty, string.Empty,
                false, false, string.Empty, string.Empty);
        }

        var marker = _storage.ReadProtectedFile(
            _ownerMarkerPath,
            _ownerRoot,
            WindowsNetBirdOwnerMarker.MaximumBytes,
            denyUntrustedRead: false,
            includeContent: true);
        var configuration = _storage.ReadProtectedFile(
            _configurationPath,
            Path.GetDirectoryName(_configurationPath)!,
            maximumBytes: 8 * 1024 * 1024,
            denyUntrustedRead: true,
            includeContent: false);
        var evidence = CreateEvidenceId(
            inventoryComplete,
            registrations,
            service,
            cli,
            marker,
            configuration);
        return new WindowsNetBirdSnapshot(
            inventoryComplete,
            registrations,
            service,
            cli,
            marker,
            configuration,
            evidence);
    }

    public void WriteOwnerMarker(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _storage.WriteProtectedFileAtomically(
            _ownerMarkerPath,
            _ownerRoot,
            content,
            denyUntrustedRead: false);
    }

    public void DeleteOwnerMarker() =>
        _storage.DeleteProtectedFile(_ownerMarkerPath, _ownerRoot, denyUntrustedRead: false);

    public void RestoreConfiguration(string protectedSourcePath, string expectedSha256) =>
        _storage.RestoreProtectedFileAtomically(
            protectedSourcePath,
            _configurationPath,
            Path.GetDirectoryName(_configurationPath)!,
            expectedSha256,
            denyUntrustedRead: true);

    private static string CreateEvidenceId(
        bool inventoryComplete,
        IReadOnlyList<WindowsNetBirdRegistration> registrations,
        WindowsNetBirdServiceSnapshot service,
        WindowsNetBirdCliSnapshot cli,
        WindowsNetBirdProtectedFileSnapshot marker,
        WindowsNetBirdProtectedFileSnapshot configuration)
    {
        var text = new StringBuilder()
            .Append(inventoryComplete).Append('|')
            .Append(registrations.Count).Append('|');
        foreach (var registration in registrations.OrderBy(value => value.Package.ProductCode))
        {
            text.Append(registration.Package.ProductCode).Append('|')
                .Append(registration.Package.UpgradeCode).Append('|')
                .Append(registration.Package.ProductVersion).Append('|')
                .Append(registration.RegistrationContext).Append('|');
        }
        text.Append(service.QueryComplete).Append('|').Append(service.Present).Append('|')
            .Append(service.ServiceIdentity).Append('|')
            .Append(cli.QueryComplete).Append('|').Append(cli.Present).Append('|')
            .Append(cli.Sha256).Append('|').Append(marker.State).Append('|')
            .Append(marker.Content is null ? string.Empty : Convert.ToHexString(SHA256.HashData(marker.Content))).Append('|')
            .Append(configuration.State).Append('|').Append(configuration.Sha256);
        return "netbird-windows-v1:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
