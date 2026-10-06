using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Connector.Upgrade.Core;
using Microsoft.Win32.SafeHandles;

namespace Connector.Upgrade.WindowsPayload;

public sealed class WindowsRollbackPayloadStore : IWindowsRollbackPayloadStore
{
    private static readonly Regex HandlePattern = new(
        "^windows-msi-v1:([0-9a-f]{32}):([12])$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly WindowsRollbackPayloadSources _sources;
    private readonly string _stagingRoot;
    private readonly IReadOnlyDictionary<LegacyApplicationKind, LegacyUpgradePin> _pins;
    private readonly IWindowsMachineStagingSecurity _security;
    private readonly IMsiPackageIdentityReader _msiReader;
    private readonly Guid _batchId = Guid.NewGuid();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<LegacyApplicationKind> _acquired = [];
    private bool _batchDirectoryCreated;

    public WindowsRollbackPayloadStore(
        WindowsRollbackPayloadSources sources,
        string? machineStagingRoot = null)
        : this(
            sources,
            machineStagingRoot ?? GetDefaultMachineStagingRoot(),
            LegacyUpgradeLock.LoadEmbedded().Pins.ToDictionary(pin => pin.Kind),
            new WindowsMachineStagingSecurity(),
            new WindowsMsiPackageIdentityReader())
    {
    }

    internal WindowsRollbackPayloadStore(
        WindowsRollbackPayloadSources sources,
        string machineStagingRoot,
        IReadOnlyDictionary<LegacyApplicationKind, LegacyUpgradePin> pins,
        IWindowsMachineStagingSecurity security,
        IMsiPackageIdentityReader msiReader)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        ArgumentException.ThrowIfNullOrWhiteSpace(sources.StructuraConnectorMsiPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sources.PlatformConnectorMsiPath);
        if (string.Equals(
                Path.GetFullPath(sources.StructuraConnectorMsiPath),
                Path.GetFullPath(sources.PlatformConnectorMsiPath),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The two rollback sources must be distinct files.", nameof(sources));

        _pins = ValidatePins(pins);
        _security = security ?? throw new ArgumentNullException(nameof(security));
        _msiReader = msiReader ?? throw new ArgumentNullException(nameof(msiReader));
        _stagingRoot = _security.PrepareRoot(machineStagingRoot);
        WindowsPathSafety.AssertNoReparseComponents(_stagingRoot);
        _security.ValidateSharedDirectory(_stagingRoot);
    }

    public async ValueTask<IVerifiedRollbackPayloadLease> AcquireVerifiedRollbackPayloadAsync(
        LegacyRollbackPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var pin = GetPin(payload.Identity.Kind);
        ValidateRequestedPayload(payload, pin);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_acquired.Add(pin.Kind))
                throw new InvalidOperationException($"A rollback payload lease for {pin.Kind} was already acquired by this store.");

            try
            {
                EnsureBatchDirectory();
                return await StagePinnedPayloadAsync(pin, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _acquired.Remove(pin.Kind);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IVerifiedRollbackPayloadLease> ReacquireVerifiedRollbackPayloadAsync(
        ProtectedRollbackPayloadReceipt durableReceipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(durableReceipt);
        if (durableReceipt.Protection != RollbackPayloadProtection.ProtectedMachineStaging ||
            !Enum.IsDefined(durableReceipt.Kind) || string.IsNullOrWhiteSpace(durableReceipt.HandleId))
            throw new InvalidDataException("The durable rollback handle is incomplete or not machine protected.");
        var pin = GetPin(durableReceipt.Kind);
        var (batchId, kind) = ParseHandleId(durableReceipt.HandleId);
        if (kind != pin.Kind)
            throw new InvalidDataException("The durable rollback handle kind does not match its opaque receipt.");

        var path = GetStagedPath(batchId, pin);
        WindowsPathSafety.AssertNoReparseComponents(path);
        _security.ValidateProtectedDirectory(Path.GetDirectoryName(path)!);
        _security.ValidateProtectedFile(path);

        var stream = OpenPinnedReadStream(path);
        try
        {
            WindowsPathSafety.AssertHandleMatchesPath(stream.SafeFileHandle, path);
            WindowsPathSafety.AssertNoReparseComponents(path);
            _security.ValidateProtectedFile(path);
            var actual = await InspectPinnedStreamAsync(
                    stream,
                    path,
                    pin,
                    protectedDestination: true,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!actual.TrustedSource || actual.Identity != pin.Identity ||
                !string.Equals(actual.PackageId, pin.PackageId, StringComparison.Ordinal) ||
                !string.Equals(actual.InstallerName, pin.InstallerName, StringComparison.Ordinal) ||
                !string.Equals(actual.Version, pin.Version, StringComparison.Ordinal) ||
                actual.SizeBytes != pin.SizeBytes ||
                !string.Equals(actual.Sha256, pin.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("The protected rollback payload does not match the embedded pin.");
            return new WindowsVerifiedRollbackPayloadLease(durableReceipt.HandleId, path, actual, stream);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<IVerifiedRollbackPayloadLease> StagePinnedPayloadAsync(
        LegacyUpgradePin pin,
        CancellationToken cancellationToken)
    {
        var sourcePath = WindowsPathSafety.NormalizeExistingRegularFile(_sources.GetPath(pin.Kind));
        if (!string.Equals(Path.GetFileName(sourcePath), pin.InstallerName, StringComparison.Ordinal))
            throw new InvalidDataException($"Rollback source for {pin.Kind} does not have the locked installer name.");

        await using var source = OpenPinnedReadStream(sourcePath);
        WindowsPathSafety.AssertHandleMatchesPath(source.SafeFileHandle, sourcePath);
        WindowsPathSafety.AssertNoReparseComponents(sourcePath);
        await InspectPinnedStreamAsync(
                source,
                sourcePath,
                pin,
                protectedDestination: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var destinationPath = GetStagedPath(_batchId, pin);
        WindowsPathSafety.AssertNoReparseComponents(Path.GetDirectoryName(destinationPath)!);
        FileStream? destinationWriter = null;
        FileStream? pinnedDestination = null;
        try
        {
            destinationWriter = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            source.Position = 0;
            await source.CopyToAsync(destinationWriter, 128 * 1024, cancellationToken).ConfigureAwait(false);
            await destinationWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
            destinationWriter.Flush(flushToDisk: true);

            _security.ProtectAndValidateFile(destinationPath);
            WindowsPathSafety.AssertNoReparseComponents(destinationPath);
            WindowsPathSafety.AssertHandleMatchesPath(destinationWriter.SafeFileHandle, destinationPath);
            await destinationWriter.DisposeAsync().ConfigureAwait(false);
            destinationWriter = null;

            // The inherited protected ACL already covered the file while it was written. Once the
            // explicit ACL is verified, reopen read-only so the long-lived lease itself cannot
            // mutate bytes and denies every other writer/deleter through its share mode.
            pinnedDestination = OpenPinnedReadStream(destinationPath);
            WindowsPathSafety.AssertHandleMatchesPath(pinnedDestination.SafeFileHandle, destinationPath);
            WindowsPathSafety.AssertNoReparseComponents(destinationPath);
            _security.ValidateProtectedFile(destinationPath);
            var inspection = await InspectPinnedStreamAsync(
                    pinnedDestination,
                    destinationPath,
                    pin,
                    protectedDestination: true,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var handleId = FormatHandleId(_batchId, pin.Kind);
            var lease = new WindowsVerifiedRollbackPayloadLease(
                handleId,
                destinationPath,
                inspection,
                pinnedDestination);
            pinnedDestination = null;
            return lease;
        }
        catch
        {
            if (destinationWriter is not null)
                await destinationWriter.DisposeAsync().ConfigureAwait(false);
            if (pinnedDestination is not null)
                await pinnedDestination.DisposeAsync().ConfigureAwait(false);
            DeleteFailedDestination(destinationPath);
            throw;
        }
    }

    private async ValueTask<RollbackPayloadInspection> InspectPinnedStreamAsync(
        FileStream stream,
        string path,
        LegacyUpgradePin pin,
        bool protectedDestination,
        CancellationToken cancellationToken)
    {
        if (stream.Length != pin.SizeBytes)
            throw new InvalidDataException($"Rollback payload for {pin.Kind} has a different size from the repository lock.");
        stream.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(hash, pin.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Rollback payload for {pin.Kind} has a different SHA-256 from the repository lock.");

        var identity = _msiReader.Read(path);
        if (identity.ProductCode != pin.Identity.ProductCode ||
            identity.UpgradeCode != pin.Identity.UpgradeCode ||
            !string.Equals(identity.Version, pin.Version, StringComparison.Ordinal))
            throw new InvalidDataException($"Rollback payload for {pin.Kind} has different MSI identity properties from the repository lock.");

        stream.Position = 0;
        return new RollbackPayloadInspection(
            pin.Identity,
            pin.PackageId,
            pin.InstallerName,
            pin.Version,
            pin.SizeBytes,
            hash,
            // Core's field name predates this adapter. True describes authenticated bytes in the
            // protected destination, never an ACL trust claim about the user-owned source path.
            TrustedSource: protectedDestination);
    }

    private void EnsureBatchDirectory()
    {
        if (_batchDirectoryCreated)
            return;
        var batchDirectory = GetBatchDirectory(_batchId);
        _security.CreateProtectedDirectory(batchDirectory);
        WindowsPathSafety.AssertNoReparseComponents(batchDirectory);
        _security.ValidateProtectedDirectory(batchDirectory);
        _batchDirectoryCreated = true;
    }

    private void DeleteFailedDestination(string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            WindowsPathSafety.AssertNoReparseComponents(destinationPath);
            File.Delete(destinationPath);
        }

        var batchDirectory = Path.GetDirectoryName(destinationPath)!;
        if (Directory.Exists(batchDirectory) && !Directory.EnumerateFileSystemEntries(batchDirectory).Any())
        {
            WindowsPathSafety.AssertNoReparseComponents(batchDirectory);
            Directory.Delete(batchDirectory);
            _batchDirectoryCreated = false;
        }
    }

    private string GetStagedPath(Guid batchId, LegacyUpgradePin pin) =>
        Path.Combine(GetBatchDirectory(batchId), pin.InstallerName);

    private string GetBatchDirectory(Guid batchId) =>
        Path.Combine(_stagingRoot, "batch-" + batchId.ToString("N"));

    private static FileStream OpenPinnedReadStream(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 128 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private LegacyUpgradePin GetPin(LegacyApplicationKind kind) =>
        _pins.TryGetValue(kind, out var pin)
            ? pin
            : throw new InvalidDataException($"No embedded rollback pin exists for {kind}.");

    internal static IReadOnlyDictionary<LegacyApplicationKind, LegacyUpgradePin> ValidatePins(
        IReadOnlyDictionary<LegacyApplicationKind, LegacyUpgradePin> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        if (pins.Count != 2 ||
            !pins.ContainsKey(LegacyApplicationKind.StructuraConnector) ||
            !pins.ContainsKey(LegacyApplicationKind.PlatformConnector))
            throw new InvalidDataException("Windows rollback staging requires exactly the two embedded legacy MSI pins.");
        return pins;
    }

    private static void ValidateRequestedPayload(LegacyRollbackPayload payload, LegacyUpgradePin pin)
    {
        if (!payload.Verified || payload.Identity != pin.Identity ||
            !string.Equals(payload.PackageId, pin.PackageId, StringComparison.Ordinal) ||
            !string.Equals(payload.InstallerName, pin.InstallerName, StringComparison.Ordinal) ||
            !string.Equals(payload.Version, pin.Version, StringComparison.Ordinal) ||
            payload.SizeBytes != pin.SizeBytes ||
            !string.Equals(payload.Sha256, pin.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Requested rollback payload for {pin.Kind} does not match the embedded lock.");
    }

    private static string FormatHandleId(Guid batchId, LegacyApplicationKind kind) =>
        $"windows-msi-v1:{batchId:N}:{(int)kind}";

    private static (Guid BatchId, LegacyApplicationKind Kind) ParseHandleId(string handleId)
    {
        var match = HandlePattern.Match(handleId ?? string.Empty);
        if (!match.Success || !Guid.TryParseExact(match.Groups[1].Value, "N", out var batchId))
            throw new InvalidDataException("The durable rollback handle id is invalid.");
        return (batchId, (LegacyApplicationKind)int.Parse(match.Groups[2].Value));
    }

    private static string GetDefaultMachineStagingRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "StructuraConnectorInstaller",
        "RollbackPayloads");
}

internal sealed class WindowsVerifiedRollbackPayloadLease(
    string handleId,
    string stagedPath,
    RollbackPayloadInspection inspection,
    FileStream pinnedStream) : IWindowsVerifiedRollbackPayloadLease
{
    private FileStream? _pinnedStream = pinnedStream;

    public string HandleId { get; } = handleId;
    public RollbackPayloadProtection Protection => RollbackPayloadProtection.ProtectedMachineStaging;
    public RollbackPayloadInspection Inspection { get; } = inspection;
    public string StagedPath { get; } = stagedPath;

    public SafeFileHandle ContentHandle =>
        _pinnedStream?.SafeFileHandle ?? throw new ObjectDisposedException(nameof(WindowsVerifiedRollbackPayloadLease));

    public async ValueTask DisposeAsync()
    {
        var stream = Interlocked.Exchange(ref _pinnedStream, null);
        if (stream is not null)
            await stream.DisposeAsync().ConfigureAwait(false);
    }
}
