using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Connector.Upgrade.Core;

namespace Connector.Upgrade.WindowsPayload;

/// <summary>
/// Operation-scoped rollback payload staging and reopening. The elevated instance copies only
/// embedded-lock-pinned inputs into an immutable operation slot; the original-user instance never
/// creates or prepares protected paths and only opens that slot read-only.
/// </summary>
public sealed class WindowsOperationRollbackPayloadStager : IWindowsOperationRollbackPayloadStager
{
    private readonly Guid _operationId;
    private readonly string _initiatingUserSid;
    private readonly WindowsRollbackPayloadSources _sources;
    private readonly string _stagingRoot;
    private readonly IReadOnlyDictionary<LegacyApplicationKind, LegacyUpgradePin> _pins;
    private readonly IWindowsMachineStagingSecurity _security;
    private readonly IMsiPackageIdentityReader _msiReader;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Production entry point. The initiating SID is taken from a token-backed identity and
    /// compared with the SID authenticated by the helper session before any staging root is
    /// prepared. Do not replace these parameters with a caller-provided SID string.
    /// </summary>
    public WindowsOperationRollbackPayloadStager(
        Guid operationId,
        WindowsIdentity authenticatedInitiatingUser,
        SecurityIdentifier expectedInitiatingUserSid,
        WindowsRollbackPayloadSources sources,
        string? machineStagingRoot = null)
        : this(operationId, GetAuthenticatedInitiatingSid(authenticatedInitiatingUser, expectedInitiatingUserSid), sources,
            machineStagingRoot ?? GetDefaultMachineStagingRoot(),
            LegacyUpgradeLock.LoadEmbedded().Pins.ToDictionary(pin => pin.Kind),
            new WindowsMachineStagingSecurity(), new WindowsMsiPackageIdentityReader())
    {
    }

    internal WindowsOperationRollbackPayloadStager(
        Guid operationId,
        string initiatingUserSid,
        WindowsRollbackPayloadSources sources,
        string machineStagingRoot,
        IReadOnlyDictionary<LegacyApplicationKind, LegacyUpgradePin> pins,
        IWindowsMachineStagingSecurity security,
        IMsiPackageIdentityReader msiReader)
    {
        ValidateBinding(operationId, initiatingUserSid);
        _operationId = operationId;
        _initiatingUserSid = initiatingUserSid;
        _sources = ValidateSources(sources);
        _pins = WindowsRollbackPayloadStore.ValidatePins(pins);
        _security = security ?? throw new ArgumentNullException(nameof(security));
        _msiReader = msiReader ?? throw new ArgumentNullException(nameof(msiReader));
        _stagingRoot = _security.PrepareRoot(machineStagingRoot);
        WindowsPathSafety.AssertNoReparseComponents(_stagingRoot);
        _security.ValidateSharedDirectory(_stagingRoot);
    }

    public async ValueTask<IWindowsVerifiedRollbackPayloadLease> StageVerifiedRollbackPayloadAsync(
        LegacyApplicationKind kind,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind) || !_pins.TryGetValue(kind, out var pin))
            throw new InvalidDataException("The requested legacy MSI kind is not embedded in the upgrade lock.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var batchPath = GetBatchPath();
            EnsureBatchDirectory(batchPath, kind);
            var handleId = FormatHandleId(_operationId, kind);
            var destinationPath = Path.Combine(batchPath, pin.InstallerName);

            if (Directory.Exists(destinationPath))
                throw new WindowsRollbackPayloadNeedsManualRecoveryException(kind);
            if (File.Exists(destinationPath))
                return await ReopenExistingForMachineAsync(handleId, pin, batchPath, destinationPath, cancellationToken)
                    .ConfigureAwait(false);

            var sourcePath = WindowsPathSafety.NormalizeExistingRegularFile(_sources.GetPath(kind));
            if (!string.Equals(Path.GetFileName(sourcePath), pin.InstallerName, StringComparison.Ordinal))
                throw new InvalidDataException("The rollback source name does not match the embedded MSI lock.");

            await using var source = OpenRead(sourcePath);
            WindowsPathSafety.AssertHandleMatchesPath(source.SafeFileHandle, sourcePath);
            WindowsPathSafety.AssertNoReparseComponents(sourcePath);
            await InspectPinnedStreamAsync(source, sourcePath, pin, protectedDestination: false, cancellationToken)
                .ConfigureAwait(false);

            FileStream? writer = null;
            var createdDestination = false;
            try
            {
                writer = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
                createdDestination = true;
                source.Position = 0;
                await source.CopyToAsync(writer, 128 * 1024, cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                writer.Flush(flushToDisk: true);
                WindowsPathSafety.AssertHandleMatchesPath(writer.SafeFileHandle, destinationPath);
                await writer.DisposeAsync().ConfigureAwait(false);
                writer = null;
                _security.ProtectAndValidateRollbackFile(destinationPath, _initiatingUserSid);
                return await ReopenExistingForMachineAsync(handleId, pin, batchPath, destinationPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!createdDestination)
            {
                throw;
            }
            catch (Exception) when (createdDestination)
            {
                // Never delete or overwrite a deterministic slot after creation. A retry may
                // adopt exact pinned bytes; a partial/corrupt slot requires explicit recovery.
                throw new WindowsRollbackPayloadNeedsManualRecoveryException(kind);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                // A concurrent/replayed creator won CreateNew. Reopen only after full attestation.
                return await ReopenExistingForMachineAsync(handleId, pin, batchPath, destinationPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (writer is not null)
                    await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<IWindowsVerifiedRollbackPayloadLease> ReopenExistingForMachineAsync(
        string handleId, LegacyUpgradePin pin, string batchPath, string path, CancellationToken cancellationToken)
    {
        try
        {
            _security.ValidateRollbackBatchDirectory(batchPath, _initiatingUserSid);
            _security.ValidateRollbackFile(path, _initiatingUserSid);
            var stream = OpenRead(path);
            try
            {
                WindowsPathSafety.AssertHandleMatchesPath(stream.SafeFileHandle, path);
                WindowsPathSafety.AssertNoReparseComponents(path);
                _security.ValidateRollbackFile(path, _initiatingUserSid);
                var inspection = await InspectPinnedStreamAsync(stream, path, pin, true, cancellationToken)
                    .ConfigureAwait(false);
                return new WindowsVerifiedRollbackPayloadLease(handleId, path, inspection, stream);
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (WindowsRollbackPayloadNeedsManualRecoveryException) { throw; }
        catch (Exception)
        {
            throw new WindowsRollbackPayloadNeedsManualRecoveryException(pin.Kind);
        }
    }

    private void EnsureBatchDirectory(string batchPath, LegacyApplicationKind kind)
    {
        if (File.Exists(batchPath))
            throw new WindowsRollbackPayloadNeedsManualRecoveryException(kind);
        if (Directory.Exists(batchPath))
        {
            try { _security.ValidateRollbackBatchDirectory(batchPath, _initiatingUserSid); }
            catch (Exception) { throw new WindowsRollbackPayloadNeedsManualRecoveryException(kind); }
            return;
        }

        try
        {
            _security.CreateRollbackBatchDirectory(batchPath, _initiatingUserSid);
        }
        catch (IOException) when (Directory.Exists(batchPath))
        {
            try { _security.ValidateRollbackBatchDirectory(batchPath, _initiatingUserSid); }
            catch (Exception) { throw new WindowsRollbackPayloadNeedsManualRecoveryException(kind); }
        }
    }

    private string GetBatchPath() => Path.Combine(_stagingRoot, "operation-" + _operationId.ToString("N"));

    private async ValueTask<RollbackPayloadInspection> InspectPinnedStreamAsync(
        FileStream stream, string path, LegacyUpgradePin pin, bool protectedDestination,
        CancellationToken cancellationToken)
    {
        if (stream.Length != pin.SizeBytes)
            throw new InvalidDataException("Rollback MSI size differs from the embedded lock.");
        stream.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(hash, pin.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Rollback MSI hash differs from the embedded lock.");
        var identity = _msiReader.Read(path);
        if (identity.ProductCode != pin.Identity.ProductCode || identity.UpgradeCode != pin.Identity.UpgradeCode ||
            !string.Equals(identity.Version, pin.Version, StringComparison.Ordinal))
            throw new InvalidDataException("Rollback MSI identity differs from the embedded lock.");
        stream.Position = 0;
        return new RollbackPayloadInspection(pin.Identity, pin.PackageId, pin.InstallerName, pin.Version,
            pin.SizeBytes, hash, TrustedSource: protectedDestination);
    }

    internal static string FormatHandleId(Guid operationId, LegacyApplicationKind kind) =>
        $"windows-msi-op-v1:{operationId:N}:{(int)kind}";

    internal static string GetDefaultMachineStagingRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "StructuraConnectorInstaller", "RollbackPayloads");

    internal static (Guid OperationId, LegacyApplicationKind Kind) ParseHandleId(string handleId)
    {
        var match = HandlePattern.Match(handleId ?? string.Empty);
        if (!match.Success || !Guid.TryParseExact(match.Groups[1].Value, "N", out var operationId))
            throw new InvalidDataException("The operation-scoped rollback handle is invalid.");
        var kind = (LegacyApplicationKind)int.Parse(match.Groups[2].Value);
        if (!Enum.IsDefined(kind)) throw new InvalidDataException("The rollback handle kind is invalid.");
        return (operationId, kind);
    }

    internal static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    internal static void ValidateBinding(Guid operationId, string initiatingUserSid)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A trusted operation ID is required.", nameof(operationId));
        ValidateInitiatingSid(initiatingUserSid);
    }

    private static void ValidateInitiatingSid(string initiatingUserSid)
    {
        if (string.IsNullOrWhiteSpace(initiatingUserSid))
            throw new ArgumentException("An authenticated initiating user SID is required.", nameof(initiatingUserSid));
        SecurityIdentifier sid;
        try { sid = new SecurityIdentifier(initiatingUserSid); }
        catch (ArgumentException error)
        {
            throw new ArgumentException("The initiating user SID is invalid.", nameof(initiatingUserSid), error);
        }
        // Do this before an elevated stager calls PrepareRoot. Well-known and non-account
        // principals must never receive the per-user read grant.
        if (sid.AccountDomainSid is null && !sid.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal))
            throw new ArgumentException("The initiating SID must identify an individual user account.", nameof(initiatingUserSid));
    }

    private static string GetAuthenticatedInitiatingSid(
        WindowsIdentity authenticatedInitiatingUser, SecurityIdentifier expectedInitiatingUserSid)
    {
        ArgumentNullException.ThrowIfNull(authenticatedInitiatingUser);
        ArgumentNullException.ThrowIfNull(expectedInitiatingUserSid);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Authenticated Windows token validation requires Windows.");
        var token = authenticatedInitiatingUser.AccessToken;
        if (token is null || token.IsInvalid || token.IsClosed)
            throw new UnauthorizedAccessException("The initiating Windows identity has no live token.");
        var tokenSid = authenticatedInitiatingUser.User;
        if (tokenSid is null || !tokenSid.Equals(expectedInitiatingUserSid))
            throw new UnauthorizedAccessException("The initiating Windows token does not match the authenticated session SID.");
        var sid = tokenSid.Value;
        // Unlike the internal test seam, this value was read from WindowsIdentity.User and
        // matched to the helper-authenticated SID before any ACL/path mutation.
        ValidateInitiatingSid(sid);
        return sid;
    }

    internal static WindowsRollbackPayloadSources ValidateSources(WindowsRollbackPayloadSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(sources.StructuraConnectorMsiPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sources.PlatformConnectorMsiPath);
        if (string.Equals(Path.GetFullPath(sources.StructuraConnectorMsiPath),
                Path.GetFullPath(sources.PlatformConnectorMsiPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The two rollback sources must be distinct files.", nameof(sources));
        return sources;
    }

    private static readonly Regex HandlePattern = new(
        "^windows-msi-op-v1:([0-9a-f]{32}):([12])$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
}

/// <summary>Original-user opener. It never prepares, creates, or writes a machine staging path.</summary>
public sealed class WindowsOperationRollbackPayloadOpener : IWindowsOperationRollbackPayloadOpener
{
    private readonly Guid _operationId;
    private readonly string _initiatingUserSid;
    private readonly string _stagingRoot;
    private readonly IReadOnlyDictionary<LegacyApplicationKind, LegacyUpgradePin> _pins;
    private readonly IWindowsMachineStagingSecurity _security;
    private readonly IMsiPackageIdentityReader _msiReader;

    public WindowsOperationRollbackPayloadOpener(
        Guid operationId, string initiatingUserSid, string? machineStagingRoot = null)
        : this(operationId, initiatingUserSid, machineStagingRoot ?? WindowsOperationRollbackPayloadStager.GetDefaultMachineStagingRoot(),
            LegacyUpgradeLock.LoadEmbedded().Pins.ToDictionary(pin => pin.Kind),
            new WindowsMachineStagingSecurity(), new WindowsMsiPackageIdentityReader())
    {
    }

    internal WindowsOperationRollbackPayloadOpener(
        Guid operationId, string initiatingUserSid, string machineStagingRoot,
        IReadOnlyDictionary<LegacyApplicationKind, LegacyUpgradePin> pins,
        IWindowsMachineStagingSecurity security, IMsiPackageIdentityReader msiReader)
    {
        WindowsOperationRollbackPayloadStager.ValidateBinding(operationId, initiatingUserSid);
        _operationId = operationId;
        _initiatingUserSid = initiatingUserSid;
        _stagingRoot = Path.GetFullPath(machineStagingRoot ?? throw new ArgumentNullException(nameof(machineStagingRoot)));
        _pins = WindowsRollbackPayloadStore.ValidatePins(pins);
        _security = security ?? throw new ArgumentNullException(nameof(security));
        _msiReader = msiReader ?? throw new ArgumentNullException(nameof(msiReader));
    }

    public async ValueTask<IWindowsVerifiedRollbackPayloadLease> ReopenVerifiedRollbackPayloadAsync(
        ProtectedRollbackPayloadReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        AssertCurrentUser();
        if (receipt.Protection != RollbackPayloadProtection.ProtectedMachineStaging ||
            !Enum.IsDefined(receipt.Kind) || !_pins.TryGetValue(receipt.Kind, out var pin))
            throw new InvalidDataException("The rollback receipt is incomplete or unsupported.");

        var parsed = WindowsOperationRollbackPayloadStager.ParseHandleId(receipt.HandleId);
        if (parsed.OperationId != _operationId || parsed.Kind != receipt.Kind)
            throw new InvalidDataException("The rollback handle is not bound to this operation and MSI kind.");

        var batchPath = Path.Combine(_stagingRoot, "operation-" + _operationId.ToString("N"));
        var path = Path.Combine(batchPath, pin.InstallerName);
        _security.ValidateSharedDirectory(_stagingRoot);
        _security.ValidateRollbackBatchDirectory(batchPath, _initiatingUserSid);
        _security.ValidateRollbackFile(path, _initiatingUserSid);

        var stream = WindowsOperationRollbackPayloadStager.OpenRead(path);
        try
        {
            WindowsPathSafety.AssertHandleMatchesPath(stream.SafeFileHandle, path);
            WindowsPathSafety.AssertNoReparseComponents(path);
            _security.ValidateRollbackBatchDirectory(batchPath, _initiatingUserSid);
            _security.ValidateRollbackFile(path, _initiatingUserSid);
            if (stream.Length != pin.SizeBytes)
                throw new InvalidDataException("Rollback MSI size differs from the embedded lock.");
            stream.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(hash, pin.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("Rollback MSI hash differs from the embedded lock.");
            var identity = _msiReader.Read(path);
            if (identity.ProductCode != pin.Identity.ProductCode || identity.UpgradeCode != pin.Identity.UpgradeCode ||
                !string.Equals(identity.Version, pin.Version, StringComparison.Ordinal))
                throw new InvalidDataException("Rollback MSI identity differs from the embedded lock.");
            stream.Position = 0;
            return new WindowsVerifiedRollbackPayloadLease(receipt.HandleId, path,
                new RollbackPayloadInspection(pin.Identity, pin.PackageId, pin.InstallerName, pin.Version,
                    pin.SizeBytes, hash, TrustedSource: true), stream);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void AssertCurrentUser()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Rollback payload reopening requires Windows.");
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var currentSid = identity.User?.Value;
        if (!string.Equals(currentSid, _initiatingUserSid, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Only the initiating Windows user may reopen this rollback MSI slot.");
    }
}
