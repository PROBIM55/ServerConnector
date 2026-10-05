using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Connector.Access.Contracts;
using Connector.Network;
using Platform.Connector.Core;

namespace Connector.SmbAccess;

public sealed record CommonSmbFolder(string ResourceId, string DisplayName, string? Drive);
public sealed record CommonSmbRouteInvalidation(long RouteEpoch);

public sealed class CommonSmbAccessException : Exception
{
    public CommonSmbAccessException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    public string Code { get; }
}

/// <summary>
/// Consumes short-lived, server-issued SMB access and exposes only safe folder metadata.
/// Credentials and UNC targets remain private and are never persisted.
/// </summary>
public sealed class CommonSmbAccessService : IDisposable
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan MaximumReceiptLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultMappingLivenessInterval = TimeSpan.FromSeconds(15);
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ReservedShareNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "admin$", "ipc$", "c$", "d$", "control", "credentials"
    };

    private readonly ICommonConnectorRequestTransport _transport;
    private readonly Func<DeviceAccessProfile?> _currentProfile;
    private readonly Func<IPAddress, bool> _destinationAllowed;
    private readonly IWindowsSmbMappingPort _port;
    private readonly TimeProvider _time;
    private readonly TimeSpan _mappingLivenessInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<string, CancellationToken, ValueTask<bool>> _authenticatedRead;
    private readonly CancellationTokenSource _monitorLifetime = new();
    private readonly Task _mappingMonitor;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<string, OwnedMapping> _ownedMappings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OwnedMapping> _pendingMappings = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<CommonSmbFolder> _folders = Array.Empty<CommonSmbFolder>();
    private ValidatedAccess? _authorization;
    private long _authorizationEpoch;
    private long _routeEpoch;
    private int _authorizationAdmission = 1;
    private int _disposed;

    public CommonSmbAccessService(
        ICommonConnectorRequestTransport transport,
        Func<DeviceAccessProfile?> currentProfile,
        Func<IPAddress, bool> destinationAllowed,
        IWindowsSmbMappingPort port,
        TimeProvider? timeProvider = null,
        TimeSpan? mappingLivenessInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<string, CancellationToken, ValueTask<bool>>? authenticatedRead = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _currentProfile = currentProfile ?? throw new ArgumentNullException(nameof(currentProfile));
        _destinationAllowed = destinationAllowed ?? throw new ArgumentNullException(nameof(destinationAllowed));
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _time = timeProvider ?? TimeProvider.System;
        _mappingLivenessInterval = mappingLivenessInterval ?? DefaultMappingLivenessInterval;
        if (_mappingLivenessInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(mappingLivenessInterval));
        _delay = delay ?? ((interval, cancellationToken) => Task.Delay(interval, _time, cancellationToken));
        _authenticatedRead = authenticatedRead ?? ReadDirectoryMetadataAsync;
        _port.NetworkChanged += OnNetworkChanged;
        _mappingMonitor = MonitorMappingsAsync(_monitorLifetime.Token);
    }

    public IReadOnlyList<CommonSmbFolder> Folders => Volatile.Read(ref _folders);
    public long RouteEpoch => Volatile.Read(ref _routeEpoch);
    public bool IsAuthorizationAdmissionOpen => Volatile.Read(ref _authorizationAdmission) != 0;
    public event Action<CommonSmbRouteInvalidation>? RouteInvalidated;

    public void EnableAuthorization(long expectedRouteEpoch)
    {
        if (expectedRouteEpoch != RouteEpoch)
            throw Error("smb_route_changed", "The network route changed while access was being established.");
        Interlocked.Increment(ref _authorizationEpoch);
        Volatile.Write(ref _authorizationAdmission, 1);
        if (expectedRouteEpoch == RouteEpoch) return;
        InvalidateAuthorization();
        throw Error("smb_route_changed", "The network route changed while access was being established.");
    }

    public void InvalidateAuthorization()
    {
        Volatile.Write(ref _authorizationAdmission, 0);
        Interlocked.Increment(ref _authorizationEpoch);
        Volatile.Write(ref _folders, Array.Empty<CommonSmbFolder>());
    }

    public async Task ClearAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        // Close admission and the UI snapshot before waiting for an already
        // accepted operation. Native mappings remain untouched.
        InvalidateAuthorization();
        // Once admission is closed, cancellation must not leave the previous
        // credential resident while an accepted native operation holds _serial.
        await _serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // Owned mapping metadata remains available only for an explicit,
            // target-checked unmount. The credential-bearing grant is dropped.
            ClearAuthorization();
        }
        finally
        {
            _serial.Release();
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Removes every mapping owned before a confirmed route invalidation. A recovered
    /// route cannot make the old WNet session reusable.
    /// The caller must first drain accepted work. Cancellation is reported only after cleanup.
    /// </summary>
    public Task CleanupUnsafeMappingsAfterDrainAsync(
        long routeEpoch, CancellationToken cancellationToken = default) =>
        CleanupMappingsAfterDrainAsync(onlyUnsafeRoutes: true, routeEpoch, cancellationToken);

    /// <summary>
    /// Removes connector-owned exact-target mappings after an overlay/access invalidation.
    /// Cached overlay state is deliberately not used as evidence that an existing mapping is safe.
    /// </summary>
    public Task CleanupOwnedMappingsAfterDrainAsync(CancellationToken cancellationToken = default) =>
        CleanupMappingsAfterDrainAsync(onlyUnsafeRoutes: false, routeEpoch: null, cancellationToken);

    public async Task<IReadOnlyList<CommonSmbFolder>> RefreshAndMountAsync(
        CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        long? admission = null;
        try
        {
            admission = RequireAuthorizationAdmission();
            EnsureNoPendingCleanup();
            var access = await RefreshAuthorizationCoreAsync(admission.Value, cancellationToken).ConfigureAwait(false);
            foreach (var resource in access.Resources)
            {
                var alreadyOwned = FindOwnedDrive(resource.ShareUnc);
                if (alreadyOwned is not null &&
                    SameTarget(await _port.GetTargetAsync(alreadyOwned, cancellationToken).ConfigureAwait(false), resource.ShareUnc))
                {
                    EnsureOverlayMatches(_ownedMappings[alreadyOwned], access.Overlay);
                    await EnsureLiveRouteAsync(_ownedMappings[alreadyOwned], cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var drive = await FindFreeDriveAsync(cancellationToken).ConfigureAwait(false);
                await MountCoreAsync(access, resource, drive, cancellationToken).ConfigureAwait(false);
            }

            await PublishFoldersAsync(access, admission.Value, cancellationToken).ConfigureAwait(false);
            return Folders;
        }
        catch
        {
            ClearAuthorization();
            throw;
        }
        finally
        {
            if (admission.HasValue && !IsAuthorizationAdmissionCurrent(admission.Value)) ClearAuthorization();
            _serial.Release();
        }
    }

    /// <summary>
    /// Acquires a fresh server-issued grant, maps exactly one readable resource, performs a native
    /// metadata read, then removes only the exact mapping created by this service. Credentials never
    /// leave this instance and cleanup ignores operation cancellation once ownership is established.
    /// </summary>
    public async Task<bool> ProbeAuthenticatedReadAsync(CancellationToken cancellationToken = default)
    {
        Exception? operationFailure = null;
        var verified = false;

        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        long? admission = null;
        try
        {
            admission = RequireAuthorizationAdmission();
            EnsureNoPendingCleanup();
            var access = await RefreshAuthorizationCoreAsync(admission.Value, cancellationToken).ConfigureAwait(false);
            var resource = access.Resources
                .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (resource is not null)
            {
                var drive = await FindFreeDriveAsync(cancellationToken).ConfigureAwait(false);
                await MountCoreAsync(access, resource, drive, cancellationToken).ConfigureAwait(false);
                EnsureAuthorizationCurrent(access);
                await EnsureLiveRouteAsync(_ownedMappings[drive], cancellationToken).ConfigureAwait(false);
                if (!SameTarget(await _port.GetTargetAsync(drive, cancellationToken).ConfigureAwait(false), resource.ShareUnc))
                    throw Error("smb_mapping_changed", "The temporary SMB mapping changed before the authenticated read.");

                verified = await _authenticatedRead(drive + Path.DirectorySeparatorChar, cancellationToken)
                    .ConfigureAwait(false);

                EnsureAuthorizationCurrent(access);
                await EnsureLiveRouteAsync(_ownedMappings[drive], cancellationToken).ConfigureAwait(false);
                if (!SameTarget(await _port.GetTargetAsync(drive, cancellationToken).ConfigureAwait(false), resource.ShareUnc))
                    throw Error("smb_mapping_changed", "The temporary SMB mapping changed during the authenticated read.");
            }
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }
        finally
        {
            if (admission.HasValue && !IsAuthorizationAdmissionCurrent(admission.Value)) ClearAuthorization();
            _serial.Release();
        }

        try
        {
            await CleanupOwnedMappingsAfterDrainAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
        {
            if (operationFailure is null) throw;
            throw new AggregateException(
                "The authenticated SMB read failed and exact temporary mapping cleanup was not confirmed.",
                operationFailure,
                cleanupFailure);
        }

        if (operationFailure is not null)
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        cancellationToken.ThrowIfCancellationRequested();
        return verified;
    }

    public async Task MountAsync(
        string resourceId,
        string drive,
        CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        long? admission = null;
        try
        {
            admission = RequireAuthorizationAdmission();
            EnsureNoPendingCleanup();
            var access = await RefreshAuthorizationCoreAsync(admission.Value, cancellationToken).ConfigureAwait(false);
            var resource = FindResource(access, resourceId);
            await MountCoreAsync(access, resource, NormalizeDrive(drive), cancellationToken).ConfigureAwait(false);
            await PublishFoldersAsync(access, admission.Value, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ClearAuthorization();
            throw;
        }
        finally
        {
            if (admission.HasValue && !IsAuthorizationAdmissionCurrent(admission.Value)) ClearAuthorization();
            _serial.Release();
        }
    }

    public async Task<string> GetFolderToOpenAsync(
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        long? admission = null;
        try
        {
            admission = RequireAuthorizationAdmission();
            EnsureNoPendingCleanup();
            var access = await RefreshAuthorizationCoreAsync(admission.Value, cancellationToken).ConfigureAwait(false);
            var resource = FindResource(access, resourceId);
            var drive = FindOwnedDrive(resource.ShareUnc) ??
                throw Error("smb_mapping_not_owned", "The folder is not mounted by this connector process.");
            if (!_ownedMappings.TryGetValue(drive, out var owned) ||
                !SameTarget(await _port.GetTargetAsync(drive, cancellationToken).ConfigureAwait(false), resource.ShareUnc))
                throw Error("smb_mapping_changed", "The mounted folder no longer matches the authorized target.");
            EnsureOverlayMatches(owned, access.Overlay);
            await EnsureLiveRouteAsync(owned, cancellationToken).ConfigureAwait(false);
            await PublishFoldersAsync(access, admission.Value, cancellationToken).ConfigureAwait(false);
            return drive + Path.DirectorySeparatorChar;
        }
        catch
        {
            ClearAuthorization();
            throw;
        }
        finally
        {
            if (admission.HasValue && !IsAuthorizationAdmissionCurrent(admission.Value)) ClearAuthorization();
            _serial.Release();
        }
    }

    public async Task UnmountAsync(
        string resourceId,
        string drive,
        Func<Task<bool>>? explicitNativeConfirmation = null,
        CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalizedDrive = NormalizeDrive(drive);
            var pending = false;
            if (!_ownedMappings.TryGetValue(normalizedDrive, out var owned) &&
                !(_pendingMappings.TryGetValue(normalizedDrive, out owned) && (pending = true)))
                throw Error("smb_mapping_not_owned", "The mapping is not owned by this connector process.");
            if (!string.Equals(owned.ResourceId, resourceId, StringComparison.Ordinal))
                throw Error("smb_resource_mismatch", "The mapping does not match the selected resource.");
            if (!SameTarget(await _port.GetTargetAsync(normalizedDrive, cancellationToken).ConfigureAwait(false), owned.ShareUnc))
            {
                if (pending) _pendingMappings.Remove(normalizedDrive);
                throw Error("smb_mapping_changed", "The mapping changed outside this connector process.");
            }
            if (explicitNativeConfirmation is null || !await explicitNativeConfirmation().ConfigureAwait(false))
                throw Error("smb_unmount_not_confirmed", "Unmount was not confirmed by the user.");
            cancellationToken.ThrowIfCancellationRequested();
            if (!SameTarget(await _port.GetTargetAsync(normalizedDrive, cancellationToken).ConfigureAwait(false), owned.ShareUnc))
                throw Error("smb_mapping_changed", "The mapping changed before unmount.");

            var removal = await _port.TryUnmountExactAsync(
                normalizedDrive, owned.ShareUnc, cancellationToken).ConfigureAwait(false);
            if (removal == SmbExactMappingRemoval.TargetChanged)
                throw Error("smb_mapping_changed", "The mapping changed before unmount.");
            _ownedMappings.Remove(normalizedDrive);
            _pendingMappings.Remove(normalizedDrive);
            if (_authorization is { } access && TryGetAuthorizationAdmission(out var admission))
                await PublishFoldersAsync(access, admission, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<ValidatedAccess> RefreshAuthorizationCoreAsync(long admission, CancellationToken cancellationToken)
    {
        ClearAuthorization();
        var profile = _currentProfile() ??
            throw Error("smb_profile_missing", "An applied device access profile is required.");
        var now = _time.GetUtcNow();
        ValidateProfile(profile, now);
        var overlay = await GetLiveReadyOverlayAsync(cancellationToken).ConfigureAwait(false);

        using var response = await _transport.SendAsync(
            HttpMethod.Get, "smb/access", body: null, EmptyHeaders, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw Error(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "smb_access_denied"
                : "smb_access_unavailable", "The SMB access service did not issue access.");

        var receipt = await ReadBoundedAsync<ConnectorSmbAccess>(response, cancellationToken).ConfigureAwait(false) ??
            throw Error("smb_receipt_invalid", "The SMB access response is empty.");
        var validated = ValidateReceipt(receipt, profile, overlay, now);
        _authorization = validated;
        await PublishFoldersAsync(validated, admission, cancellationToken).ConfigureAwait(false);
        return validated;
    }

    private ValidatedAccess ValidateReceipt(
        ConnectorSmbAccess receipt,
        DeviceAccessProfile profile,
        NetworkOverlaySnapshot overlay,
        DateTimeOffset now)
    {
        if (receipt.SchemaVersion != DeviceAccessProtocol.Version ||
            !string.Equals(receipt.DeviceId, profile.DeviceId, StringComparison.Ordinal) ||
            receipt.Revision != profile.AppliedRevision || receipt.Revision < 1 ||
            receipt.ExpiresAtUtc <= now || receipt.ExpiresAtUtc > profile.ExpiresAtUtc ||
            receipt.ExpiresAtUtc > now.Add(MaximumReceiptLifetime))
            throw Error("smb_receipt_invalid", "The SMB access response does not match the applied profile.");
        if (receipt.Resources is null || receipt.Resources.Count > 26)
            throw Error("smb_receipt_invalid", "The SMB access response contains an invalid resource list.");

        if (receipt.Resources.Count == 0)
        {
            if (!string.IsNullOrEmpty(receipt.UserName) || !string.IsNullOrEmpty(receipt.Password))
                throw Error("smb_receipt_invalid", "An empty SMB grant must not contain credentials.");
            return new ValidatedAccess(receipt.DeviceId, receipt.Revision, receipt.ExpiresAtUtc, null, null, [], overlay);
        }
        if (string.IsNullOrWhiteSpace(receipt.UserName) || receipt.UserName.Length > 256 || receipt.UserName.Any(char.IsControl) ||
            string.IsNullOrEmpty(receipt.Password) || receipt.Password.Length > 1024)
            throw Error("smb_receipt_invalid", "The SMB access response is missing bounded credentials.");

        var resources = new List<ValidatedResource>(receipt.Resources.Count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in receipt.Resources)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.ResourceId) || item.ResourceId.Length > 256 || item.ResourceId.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(item.ResourceKind) || item.ResourceKind.Length > 128 ||
                !seenIds.Add(item.ResourceId) || item.Permissions is null || item.Permissions.Count == 0 ||
                item.Permissions.Distinct().Count() != item.Permissions.Count ||
                item.Permissions.Any(permission => !Enum.IsDefined(permission)) ||
                !item.Permissions.Contains(ConnectorPermission.Read))
                throw Error("smb_receipt_invalid", "The SMB access response contains an invalid resource.");

            var candidates = profile.Resources.Where(grant =>
                string.Equals(grant.ResourceId, item.ResourceId, StringComparison.Ordinal) &&
                string.Equals(grant.ResourceKind, item.ResourceKind, StringComparison.Ordinal) &&
                string.Equals(grant.ProjectId, item.ProjectId, StringComparison.Ordinal)).ToArray();
            if (candidates.Length != 1 || candidates[0].Permissions is null ||
                item.Permissions.Any(permission => !candidates[0].Permissions.Contains(permission)))
                throw Error("smb_resource_not_granted", "The SMB resource is outside the applied access profile.");

            var (destination, shareName, normalizedUnc) = ParseUnc(item.ShareUnc);
            bool allowed;
            try { allowed = _destinationAllowed(destination); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Error("smb_destination_denied", "The SMB destination policy could not be evaluated.", exception);
            }
            if (!allowed || !seenTargets.Add(normalizedUnc))
                throw Error("smb_destination_denied", "The SMB destination is not an approved unique endpoint.");
            resources.Add(new ValidatedResource(item.ResourceId, shareName, normalizedUnc, destination));
        }

        return new ValidatedAccess(receipt.DeviceId, receipt.Revision, receipt.ExpiresAtUtc,
            receipt.UserName, receipt.Password, resources.AsReadOnly(), overlay);
    }

    private static void ValidateProfile(DeviceAccessProfile profile, DateTimeOffset now)
    {
        if (profile.SchemaVersion != DeviceAccessProtocol.Version || string.IsNullOrWhiteSpace(profile.DeviceId) ||
            profile.DesiredRevision < 1 || profile.DesiredRevision != profile.AppliedRevision ||
            profile.ExpiresAtUtc <= now || profile.Resources is null)
            throw Error("smb_profile_invalid", "A current fully-applied device access profile is required.");
    }

    private async ValueTask<NetworkOverlaySnapshot> GetLiveReadyOverlayAsync(CancellationToken cancellationToken)
    {
        try
        {
            var overlay = await _port.GetLiveOverlayAsync(cancellationToken).ConfigureAwait(false);
            if (overlay.Status != NetworkServiceStatus.Ready || !overlay.StartupCheckPassed ||
                !overlay.ManagementConnected || !overlay.SignalConnected ||
                overlay.ManagementUri is null || overlay.AssignedInternalAddresses is null)
                throw Error("smb_overlay_not_ready", "The managed overlay is not ready.");
            var addresses = overlay.AssignedInternalAddresses
                .Where(IsPrivateOverlayIpv4)
                .Distinct()
                .ToArray();
            if (addresses.Length == 0)
                throw Error("smb_overlay_not_ready", "The managed overlay has no private IPv4 address.");
            return overlay with { AssignedInternalAddresses = Array.AsReadOnly(addresses) };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (CommonSmbAccessException)
        {
            SignalRouteInvalidation();
            throw;
        }
        catch (Exception exception)
        {
            SignalRouteInvalidation();
            throw Error("smb_overlay_unavailable", "The live managed overlay state could not be verified.", exception);
        }
    }

    private async Task MountCoreAsync(
        ValidatedAccess access,
        ValidatedResource resource,
        string drive,
        CancellationToken cancellationToken)
    {
        EnsureAuthorizationCurrent(access);
        if (_pendingMappings.ContainsKey(drive) || _pendingMappings.Values.Any(mapping => SameTarget(mapping.ShareUnc, resource.ShareUnc)))
            throw Error("smb_cleanup_pending", "A previous SMB mapping cleanup is still unconfirmed.");
        var existingOwnedDrive = FindOwnedDrive(resource.ShareUnc);
        if (existingOwnedDrive is not null && !string.Equals(existingOwnedDrive, drive, StringComparison.OrdinalIgnoreCase))
            throw Error("smb_resource_already_mounted", "The resource is already mounted on another drive.");
        var existing = await _port.GetTargetAsync(drive, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (_ownedMappings.TryGetValue(drive, out var owned) && SameTarget(owned.ShareUnc, resource.ShareUnc) &&
                SameTarget(existing, resource.ShareUnc))
            {
                EnsureOverlayMatches(owned, access.Overlay);
                await EnsureLiveRouteAsync(owned, cancellationToken).ConfigureAwait(false);
                return;
            }
            throw Error("smb_drive_occupied", "The selected drive is already mapped.");
        }
        if (_ownedMappings.ContainsKey(drive))
            throw Error("smb_mapping_changed", "The connector's recorded mapping changed outside the process.");

        var userName = access.UserName!;
        var password = access.Password!;
        var mapping = new OwnedMapping(resource.ResourceId, resource.ShareName, resource.ShareUnc,
            resource.Destination, access.Overlay.ManagementUri!, access.Overlay.AssignedInternalAddresses.ToArray());
        await EnsureLiveRouteAsync(mapping, cancellationToken).ConfigureAwait(false);
        try
        {
            await _port.MountAsync(drive, resource.ShareUnc, userName, password,
                resource.Destination, access.Overlay.AssignedInternalAddresses, cancellationToken).ConfigureAwait(false);
        }
        catch (SmbRouteUnavailableException exception)
        {
            SignalRouteInvalidation();
            throw Error("smb_route_unavailable", "The live SMB route is outside the managed overlay.", exception);
        }
        catch (SmbMappingCleanupException)
        {
            _pendingMappings[drive] = mapping;
            throw;
        }
        _ownedMappings.Add(drive, mapping);
    }

    private void EnsureAuthorizationCurrent(ValidatedAccess access)
    {
        var now = _time.GetUtcNow();
        if (!ReferenceEquals(access, _authorization) || access.ExpiresAtUtc <= now)
            throw Error("smb_authorization_expired", "The SMB authorization is no longer current.");
        var profile = _currentProfile();
        if (profile is null || profile.DeviceId != access.DeviceId || profile.AppliedRevision != access.Revision ||
            profile.DesiredRevision != access.Revision || profile.ExpiresAtUtc <= now)
            throw Error("smb_authorization_changed", "The applied access profile changed.");
    }

    private async Task EnsureLiveRouteAsync(OwnedMapping mapping, CancellationToken cancellationToken)
    {
        bool allowed;
        try
        {
            allowed = await _port.IsRouteAllowedAsync(mapping.Destination,
                mapping.AuthorizedOverlayAddresses, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            SignalRouteInvalidation();
            throw Error("smb_route_unavailable", "The live SMB route could not be verified.", exception);
        }
        if (allowed) return;
        SignalRouteInvalidation();
        throw Error("smb_route_unavailable", "The live SMB route is outside the managed overlay.");
    }

    private void EnsureOverlayMatches(OwnedMapping mapping, NetworkOverlaySnapshot live)
    {
        if (SameManagementUri(live.ManagementUri!, mapping.AuthorizedManagementUri) &&
            live.AssignedInternalAddresses.ToHashSet().SetEquals(mapping.AuthorizedOverlayAddresses))
            return;
        SignalRouteInvalidation();
        throw Error("smb_overlay_changed", "The managed overlay identity changed after the SMB mapping was created.");
    }

    private async Task CleanupMappingsAfterDrainAsync(
        bool onlyUnsafeRoutes,
        long? routeEpoch,
        CancellationToken cancellationToken)
    {
        InvalidateAuthorization();
        if (onlyUnsafeRoutes && (!routeEpoch.HasValue || routeEpoch.Value > RouteEpoch))
            throw Error("smb_route_cleanup_invalid", "The route invalidation epoch is not current.");
        await _serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        var failures = new List<Exception>();
        try
        {
            ClearAuthorization();
            var mappings = _ownedMappings.Concat(_pendingMappings)
                .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            foreach (var (drive, mapping) in mappings)
            {
                // Cleanup owns this exact target from now on. Any uncertain or
                // busy native state remains pending and cannot be admitted again.
                _ownedMappings.Remove(drive);
                _pendingMappings[drive] = mapping;
                string? current;
                try { current = await _port.GetTargetAsync(drive, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception)
                {
                    failures.Add(exception);
                    continue;
                }

                if (current is null || !SameTarget(current, mapping.ShareUnc))
                {
                    ForgetMapping(drive);
                    continue;
                }

                try
                {
                    var removal = await _port.TryUnmountExactAsync(
                        drive, mapping.ShareUnc, CancellationToken.None).ConfigureAwait(false);
                    if (removal is SmbExactMappingRemoval.Removed or
                        SmbExactMappingRemoval.Missing or SmbExactMappingRemoval.TargetChanged)
                        ForgetMapping(drive);
                }
                catch (Exception exception)
                {
                    // WNet uses force=false. Open files or an uncertain native state remain
                    // tracked and fail closed for a later explicit retry.
                    failures.Add(exception);
                }
            }
        }
        finally
        {
            _serial.Release();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (failures.Count > 0)
            throw Error("smb_route_cleanup_incomplete",
                "One or more owned SMB mappings could not be safely removed.", new AggregateException(failures));
    }

    private void ForgetMapping(string drive)
    {
        _ownedMappings.Remove(drive);
        _pendingMappings.Remove(drive);
    }

    private void OnNetworkChanged() => SignalRouteInvalidation();

    private async Task MonitorMappingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await _delay(_mappingLivenessInterval, cancellationToken).ConfigureAwait(false);
                await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_ownedMappings.Count == 0 || !IsAuthorizationAdmissionOpen) continue;
                    try
                    {
                        var live = await GetLiveReadyOverlayAsync(cancellationToken).ConfigureAwait(false);
                        foreach (var mapping in _ownedMappings.Values)
                        {
                            EnsureOverlayMatches(mapping, live);
                            await EnsureLiveRouteAsync(mapping, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch
                    {
                        // Known probe/route failures already invalidated admission.
                        // Any unexpected uncertainty must fail closed as well.
                        if (IsAuthorizationAdmissionOpen) SignalRouteInvalidation();
                    }
                }
                finally { _serial.Release(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void SignalRouteInvalidation()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var routeEpoch = Interlocked.Increment(ref _routeEpoch);
        InvalidateAuthorization();
        var handlers = RouteInvalidated;
        if (handlers is null) return;
        var invalidation = new CommonSmbRouteInvalidation(routeEpoch);
        foreach (Action<CommonSmbRouteInvalidation> handler in handlers.GetInvocationList())
        {
            try { handler(invalidation); }
            catch { }
        }
    }

    private async Task PublishFoldersAsync(ValidatedAccess access, long admission, CancellationToken cancellationToken)
    {
        if (!IsAuthorizationAdmissionCurrent(admission)) return;
        var folders = new List<CommonSmbFolder>(access.Resources.Count);
        foreach (var resource in access.Resources)
        {
            var drive = FindOwnedDrive(resource.ShareUnc);
            if (drive is not null &&
                !SameTarget(await _port.GetTargetAsync(drive, cancellationToken).ConfigureAwait(false), resource.ShareUnc))
                drive = null;
            folders.Add(new CommonSmbFolder(resource.ResourceId, resource.ShareName, drive));
        }
        if (IsAuthorizationAdmissionCurrent(admission))
            Volatile.Write(ref _folders, Array.AsReadOnly(folders.ToArray()));
    }

    private async Task<string> FindFreeDriveAsync(CancellationToken cancellationToken)
    {
        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            var drive = $"{letter}:";
            if (_ownedMappings.ContainsKey(drive) || _pendingMappings.ContainsKey(drive)) continue;
            if (await _port.GetTargetAsync(drive, cancellationToken).ConfigureAwait(false) is null) return drive;
        }
        throw Error("smb_drive_unavailable", "No free drive letter is available.");
    }

    private string? FindOwnedDrive(string shareUnc) => _ownedMappings
        .Where(mapping => SameTarget(mapping.Value.ShareUnc, shareUnc))
        .Select(mapping => mapping.Key)
        .SingleOrDefault();

    private static ValidatedResource FindResource(ValidatedAccess access, string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
            throw Error("smb_resource_invalid", "A resource id is required.");
        return access.Resources.SingleOrDefault(item => string.Equals(item.ResourceId, resourceId, StringComparison.Ordinal)) ??
            throw Error("smb_resource_not_granted", "The resource is not present in the current SMB grant.");
    }

    private static string NormalizeDrive(string drive)
    {
        if (drive is null || drive.Length != 2 || drive[1] != ':' ||
            char.ToUpperInvariant(drive[0]) is < 'D' or > 'Z')
            throw Error("smb_drive_invalid", "A drive letter from D: through Z: is required.");
        return $"{char.ToUpperInvariant(drive[0])}:";
    }

    private static (IPAddress Address, string ShareName, string NormalizedUnc) ParseUnc(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 ||
            !value.StartsWith(@"\\", StringComparison.Ordinal) || value.EndsWith('\\'))
            throw Error("smb_unc_invalid", "The SMB target must be a fixed UNC share root.");
        var parts = value[2..].Split('\\');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork || !IsPrivateOverlayIpv4(address) ||
            string.IsNullOrWhiteSpace(parts[1]) || parts[1].Length > 80 ||
            parts[1] is "." or ".." || parts[1].Any(char.IsControl) ||
            parts[1].IndexOfAny(['/', ':', '$']) >= 0 || ReservedShareNames.Contains(parts[1]))
            throw Error("smb_unc_invalid", "The SMB target must use a literal private IPv4 address and one safe share name.");
        return (address, parts[1], $@"\\{address}\{parts[1]}");
    }

    private static bool IsPrivateOverlayIpv4(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168;
    }

    private static bool SameTarget(string? left, string right) =>
        string.Equals(left?.TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool SameManagementUri(Uri left, Uri right) => string.Equals(
        left.AbsoluteUri.TrimEnd('/'), right.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal);

    private void EnsureNoPendingCleanup()
    {
        if (_pendingMappings.Count > 0)
            throw Error("smb_cleanup_pending", "A previous SMB mapping cleanup is still unconfirmed.");
    }

    private void ClearAuthorization()
    {
        _authorization = null;
        Volatile.Write(ref _folders, Array.Empty<CommonSmbFolder>());
    }

    private long RequireAuthorizationAdmission()
    {
        if (!TryGetAuthorizationAdmission(out var admission))
            throw Error("smb_authorization_invalidated", "The common access session is no longer authorized.");
        return admission;
    }

    private bool TryGetAuthorizationAdmission(out long admission)
    {
        admission = Volatile.Read(ref _authorizationEpoch);
        return Volatile.Read(ref _authorizationAdmission) != 0 &&
               admission == Volatile.Read(ref _authorizationEpoch);
    }

    private bool IsAuthorizationAdmissionCurrent(long admission) =>
        Volatile.Read(ref _authorizationAdmission) != 0 &&
        admission == Volatile.Read(ref _authorizationEpoch);

    private static CommonSmbAccessException Error(string code, string message, Exception? inner = null) =>
        new(code, message, inner);

    private static ValueTask<bool> ReadDirectoryMetadataAsync(
        string mappedRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(mappedRoot);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult((attributes & FileAttributes.Directory) != 0);
    }

    private static async Task<T?> ReadBoundedAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw Error("smb_receipt_too_large", "The SMB access response is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > MaximumResponseBytes)
                throw Error("smb_receipt_too_large", "The SMB access response is too large.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        try { return await JsonSerializer.DeserializeAsync<T>(buffer, JsonOptions, cancellationToken).ConfigureAwait(false); }
        catch (JsonException exception)
        {
            throw Error("smb_receipt_invalid", "The SMB access response is invalid.", exception);
        }
    }

    private sealed record ValidatedAccess(
        string DeviceId,
        long Revision,
        DateTimeOffset ExpiresAtUtc,
        string? UserName,
        string? Password,
        IReadOnlyList<ValidatedResource> Resources,
        NetworkOverlaySnapshot Overlay);

    private sealed record ValidatedResource(
        string ResourceId,
        string ShareName,
        string ShareUnc,
        IPAddress Destination);

    private sealed record OwnedMapping(
        string ResourceId,
        string ShareName,
        string ShareUnc,
        IPAddress Destination,
        Uri AuthorizedManagementUri,
        IReadOnlyList<IPAddress> AuthorizedOverlayAddresses);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _monitorLifetime.Cancel();
        _port.NetworkChanged -= OnNetworkChanged;
        if (_port is IDisposable disposable) disposable.Dispose();
    }
}
