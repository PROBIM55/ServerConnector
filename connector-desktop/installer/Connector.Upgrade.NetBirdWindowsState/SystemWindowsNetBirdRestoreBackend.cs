using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;

namespace Connector.Upgrade.NetBirdWindowsState;

internal sealed class SystemWindowsNetBirdRestoreBackend : IWindowsNetBirdRestoreBackend
{
    private static readonly Regex HandlePattern = new(
        "^netbird-restore-v1:([0-9a-f]{32})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private const string InstallerFileName = "prior-netbird.msi";
    private const string ConfigurationFileName = "config.json";
    private const string OwnerMarkerFileName = "owner-v2.json";
    private const string ManifestFileName = "manifest-v1.json";

    private readonly string _restoreRoot;
    private readonly string _configurationPath;
    private readonly string _ownerRoot;
    private readonly string _ownerMarkerPath;
    private readonly WindowsNetBirdProtectedStorage _storage;
    private readonly SystemWindowsNetBirdStateEnvironment _stateEnvironment;
    private readonly WindowsNetBirdMachineStatePort _statePort;
    private readonly WindowsNetBirdRestoreFileInspector _fileInspector = new();

    internal SystemWindowsNetBirdRestoreBackend(string? machineRestoreRoot)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("NetBird Windows restore storage is available only on Windows.");
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
            throw new InvalidOperationException("The Windows ProgramData root could not be resolved.");
        _restoreRoot = Path.GetFullPath(machineRestoreRoot ?? Path.Combine(
            programData,
            "StructuraConnectorInstaller",
            "NetBirdRestorePoints"));
        _configurationPath = Path.GetFullPath(Path.Combine(programData, "Netbird", "config.json"));
        _ownerRoot = Path.GetFullPath(Path.Combine(
            programData,
            "StructuraConnectorInstaller",
            "NetBirdState"));
        _ownerMarkerPath = Path.Combine(_ownerRoot, "owner-v2.json");
        _storage = new WindowsNetBirdProtectedStorage(programData);
        _stateEnvironment = new SystemWindowsNetBirdStateEnvironment();
        _statePort = new WindowsNetBirdMachineStatePort(
            _stateEnvironment,
            OfficialNetBirdPackagePin.LoadEmbedded());
    }

    public async ValueTask<IWindowsVerifiedNetBirdRestorePointLease> CaptureAsync(
        NetBirdMachineInspection exactOwnedInstallation,
        CancellationToken cancellationToken)
    {
        var before = await _statePort.InspectAsync(cancellationToken).ConfigureAwait(false);
        RequireSameOwnedInspection(exactOwnedInstallation, before);
        var snapshot = _stateEnvironment.Capture();
        if (!snapshot.InventoryComplete || snapshot.Registrations.Count != 1 ||
            snapshot.Registrations[0].Package != exactOwnedInstallation.InstalledPackage ||
            string.IsNullOrWhiteSpace(snapshot.Registrations[0].LocalPackagePath))
            throw new NetBirdMachineInvariantException(
                "The exact owned NetBird Windows Installer cache path is unavailable.");
        var ownerMarker = ParseOwnerMarker(snapshot.OwnerMarker);
        RequireMarkerMatchesInspection(ownerMarker, exactOwnedInstallation);

        var batchId = Guid.NewGuid();
        var handleId = FormatHandleId(batchId);
        var root = _storage.PrepareProtectedRoot(_restoreRoot);
        var batchRoot = _storage.PrepareProtectedRoot(Path.Combine(root, "restore-" + batchId.ToString("N")));
        var installerPath = Path.Combine(batchRoot, InstallerFileName);
        var configurationPath = Path.Combine(batchRoot, ConfigurationFileName);
        var ownerMarkerPath = Path.Combine(batchRoot, OwnerMarkerFileName);

        var localPackagePath = Path.GetFullPath(snapshot.Registrations[0].LocalPackagePath!);
        var localPackageRoot = Path.GetDirectoryName(localPackagePath)
            ?? throw new InvalidDataException("The Windows Installer cache path has no parent.");
        var installer = await _storage.CopyToNewProtectedFileAsync(
            localPackagePath,
            localPackageRoot,
            installerPath,
            batchRoot,
            denyUntrustedRead: false,
            maximumBytes: 512L * 1024 * 1024,
            cancellationToken).ConfigureAwait(false);
        var configuration = await _storage.CopyToNewProtectedFileAsync(
            _configurationPath,
            Path.GetDirectoryName(_configurationPath)!,
            configurationPath,
            batchRoot,
            denyUntrustedRead: true,
            maximumBytes: 8L * 1024 * 1024,
            cancellationToken).ConfigureAwait(false);
        var markerCopy = await _storage.CopyToNewProtectedFileAsync(
            _ownerMarkerPath,
            _ownerRoot,
            ownerMarkerPath,
            batchRoot,
            denyUntrustedRead: false,
            maximumBytes: WindowsNetBirdOwnerMarker.MaximumBytes,
            cancellationToken).ConfigureAwait(false);

        var inspectedInstaller = _fileInspector.Inspect(installerPath);
        if (inspectedInstaller.Package != exactOwnedInstallation.InstalledPackage ||
            !string.Equals(installer.Sha256, exactOwnedInstallation.InstallerSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The protected Windows Installer cache does not match the exact owned NetBird installation.");
        if (!string.Equals(
                configuration.Sha256,
                exactOwnedInstallation.Assessment.OwnedState!.ConfigurationSha256,
                StringComparison.Ordinal))
            throw new InvalidDataException("The protected NetBird configuration changed during capture.");

        var afterCopy = await _statePort.InspectAsync(cancellationToken).ConfigureAwait(false);
        RequireSameOwnedInspection(exactOwnedInstallation, afterCopy);
        var manifest = new WindowsNetBirdRestoreManifest(
            WindowsNetBirdRestoreManifest.CurrentSchemaVersion,
            handleId,
            ownerMarker.OperationId,
            exactOwnedInstallation.Assessment.OwnedState.InstallationId,
            inspectedInstaller.Package.ProductCode,
            inspectedInstaller.Package.UpgradeCode,
            inspectedInstaller.Package.ProductVersion,
            inspectedInstaller.Package.Manufacturer,
            inspectedInstaller.Package.ProductName,
            installer.SizeBytes,
            installer.Sha256,
            inspectedInstaller.SignerSubject,
            inspectedInstaller.SignerThumbprint,
            configuration.SizeBytes,
            configuration.Sha256,
            exactOwnedInstallation.Assessment.OwnedState.ServiceIdentity,
            markerCopy.Sha256,
            DateTimeOffset.UtcNow);
        _storage.WriteProtectedFileAtomically(
            Path.Combine(batchRoot, ManifestFileName),
            batchRoot,
            manifest.Serialize(),
            denyUntrustedRead: false);
        return await ReacquireAsync(manifest.RestorePoint, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IWindowsVerifiedNetBirdRestorePointLease> ReacquireAsync(
        NetBirdOwnedRestorePoint restorePoint,
        CancellationToken cancellationToken)
    {
        var batchId = ParseHandleId(restorePoint.HandleId);
        _storage.ValidateProtectedRoot(_restoreRoot);
        var batchRoot = _storage.ValidateProtectedRoot(Path.Combine(
            _restoreRoot,
            "restore-" + batchId.ToString("N")));
        var manifestSnapshot = _storage.ReadProtectedFile(
            Path.Combine(batchRoot, ManifestFileName),
            batchRoot,
            WindowsNetBirdRestoreManifest.MaximumBytes,
            denyUntrustedRead: false,
            includeContent: true);
        if (manifestSnapshot.State != NetBirdProtectedFileState.Present || manifestSnapshot.Content is null)
            throw new InvalidDataException("The protected NetBird restore manifest is unavailable.");
        var manifest = WindowsNetBirdRestoreManifest.Parse(manifestSnapshot.Content);
        if (manifest.RestorePoint != restorePoint)
            throw new InvalidDataException("The durable NetBird restore point differs from its protected manifest.");

        var installerPath = Path.Combine(batchRoot, InstallerFileName);
        var configurationPath = Path.Combine(batchRoot, ConfigurationFileName);
        var ownerMarkerPath = Path.Combine(batchRoot, OwnerMarkerFileName);
        FileStream? installer = null;
        FileStream? configuration = null;
        FileStream? ownerMarker = null;
        try
        {
            installer = _storage.OpenProtectedPinnedRead(installerPath, batchRoot, denyUntrustedRead: false);
            configuration = _storage.OpenProtectedPinnedRead(configurationPath, batchRoot, denyUntrustedRead: true);
            ownerMarker = _storage.OpenProtectedPinnedRead(ownerMarkerPath, batchRoot, denyUntrustedRead: false);
            var installerHash = await HashAsync(installer, cancellationToken).ConfigureAwait(false);
            var configurationHash = await HashAsync(configuration, cancellationToken).ConfigureAwait(false);
            var markerHash = await HashAsync(ownerMarker, cancellationToken).ConfigureAwait(false);
            if (installer.Length != manifest.InstallerSizeBytes ||
                !string.Equals(installerHash, manifest.InstallerSha256, StringComparison.Ordinal) ||
                configuration.Length != manifest.ConfigurationSizeBytes ||
                !string.Equals(configurationHash, manifest.ConfigurationSha256, StringComparison.Ordinal) ||
                !string.Equals(markerHash, manifest.OwnerMarkerSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Protected NetBird restore content drifted from its manifest.");

            var fileInspection = _fileInspector.Inspect(installerPath);
            if (fileInspection.Package != manifest.Package ||
                !string.Equals(fileInspection.SignerSubject, manifest.SignerSubject, StringComparison.Ordinal) ||
                !string.Equals(fileInspection.SignerThumbprint, manifest.SignerThumbprint, StringComparison.Ordinal))
                throw new InvalidDataException("The protected prior NetBird MSI identity or signer changed.");
            ownerMarker.Position = 0;
            var markerBytes = new byte[checked((int)ownerMarker.Length)];
            await ownerMarker.ReadExactlyAsync(markerBytes, cancellationToken).ConfigureAwait(false);
            var priorMarker = WindowsNetBirdOwnerMarker.Parse(markerBytes);
            if (priorMarker.Package != manifest.Package || priorMarker.OwnedState != manifest.OwnedState ||
                !string.Equals(priorMarker.OperationId, manifest.PriorOwnerOperationId, StringComparison.Ordinal) ||
                !string.Equals(priorMarker.InstallerSha256, manifest.InstallerSha256, StringComparison.Ordinal))
                throw new InvalidDataException("The protected prior NetBird owner marker differs from the manifest.");

            installer.Position = 0;
            configuration.Position = 0;
            ownerMarker.Position = 0;
            var lease = new WindowsVerifiedNetBirdRestorePointLease(
                manifest,
                installerPath,
                configurationPath,
                installer,
                configuration,
                ownerMarker);
            installer = null;
            configuration = null;
            ownerMarker = null;
            return lease;
        }
        finally
        {
            if (installer is not null)
                await installer.DisposeAsync().ConfigureAwait(false);
            if (configuration is not null)
                await configuration.DisposeAsync().ConfigureAwait(false);
            if (ownerMarker is not null)
                await ownerMarker.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static bool IsHandleId(string? value) =>
        value is not null && HandlePattern.IsMatch(value);

    private static string FormatHandleId(Guid batchId) => $"netbird-restore-v1:{batchId:N}";

    private static Guid ParseHandleId(string handleId)
    {
        var match = HandlePattern.Match(handleId ?? string.Empty);
        if (!match.Success || !Guid.TryParseExact(match.Groups[1].Value, "N", out var batchId))
            throw new InvalidDataException("The NetBird restore handle id is invalid.");
        return batchId;
    }

    private static WindowsNetBirdOwnerMarker ParseOwnerMarker(
        WindowsNetBirdProtectedFileSnapshot snapshot)
    {
        if (snapshot.State != NetBirdProtectedFileState.Present || snapshot.Content is null)
            throw new NetBirdMachineInvariantException("The protected NetBird owner marker is unavailable.");
        return WindowsNetBirdOwnerMarker.Parse(snapshot.Content);
    }

    private static void RequireMarkerMatchesInspection(
        WindowsNetBirdOwnerMarker marker,
        NetBirdMachineInspection inspection)
    {
        if (marker.Package != inspection.InstalledPackage ||
            marker.OwnedState != inspection.Assessment.OwnedState ||
            !string.Equals(marker.OperationId, inspection.OwnerOperationId, StringComparison.Ordinal) ||
            !string.Equals(marker.InstallerSha256, inspection.InstallerSha256, StringComparison.Ordinal))
            throw new NetBirdMachineInvariantException(
                "The protected owner marker differs from the exact owned NetBird inspection.");
    }

    private static void RequireSameOwnedInspection(
        NetBirdMachineInspection expected,
        NetBirdMachineInspection actual)
    {
        if (!actual.InspectionComplete || actual.Assessment != expected.Assessment ||
            actual.InstalledPackage != expected.InstalledPackage ||
            !string.Equals(actual.InstallerSha256, expected.InstallerSha256, StringComparison.Ordinal) ||
            actual.ServiceIdentityVerified != expected.ServiceIdentityVerified ||
            !string.Equals(actual.OwnerOperationId, expected.OwnerOperationId, StringComparison.Ordinal))
            throw new NetBirdMachineInvariantException(
                "The Connector-owned NetBird installation changed during restore-point capture.");
    }

    private static async ValueTask<string> HashAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var hash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        stream.Position = 0;
        return hash;
    }
}

internal sealed class WindowsVerifiedNetBirdRestorePointLease(
    WindowsNetBirdRestoreManifest manifest,
    string stagedPath,
    string configurationPath,
    FileStream installer,
    FileStream configuration,
    FileStream ownerMarker) : IWindowsVerifiedNetBirdRestorePointLease
{
    private FileStream? _installer = installer;
    private FileStream? _configuration = configuration;
    private FileStream? _ownerMarker = ownerMarker;

    public NetBirdOwnedRestorePoint RestorePoint { get; } = manifest.RestorePoint;
    public NetBirdMsiPackageIdentity Package { get; } = manifest.Package;
    public string StagedPath { get; } = stagedPath;
    public long SizeBytes { get; } = manifest.InstallerSizeBytes;
    public string Sha256 { get; } = manifest.InstallerSha256;
    public bool AuthenticodeTrusted => true;
    public string SignerSubject { get; } = manifest.SignerSubject;
    public string SignerThumbprint { get; } = manifest.SignerThumbprint;
    public string ProtectedConfigurationPath { get; } = configurationPath;
    public string PriorOwnerOperationId { get; } = manifest.PriorOwnerOperationId;

    public async ValueTask DisposeAsync()
    {
        var installerStream = Interlocked.Exchange(ref _installer, null);
        var configurationStream = Interlocked.Exchange(ref _configuration, null);
        var ownerMarkerStream = Interlocked.Exchange(ref _ownerMarker, null);
        if (installerStream is not null)
            await installerStream.DisposeAsync().ConfigureAwait(false);
        if (configurationStream is not null)
            await configurationStream.DisposeAsync().ConfigureAwait(false);
        if (ownerMarkerStream is not null)
            await ownerMarkerStream.DisposeAsync().ConfigureAwait(false);
    }
}
