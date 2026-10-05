using System.ComponentModel;
using System.Net;
using Connector.Access.Contracts;
using Connector.Network;
using Connector.SmbAccess;
using Platform.Connector.Core;

namespace Connector.Upgrade.PlatformAccess;

/// <summary>
/// Verifies the issued SMB grant with a real temporary WNet mapping and a metadata read.
/// The shared SMB service owns receipt validation, live OS-route checks and exact cleanup.
/// </summary>
public sealed class WindowsSmbAccessProbe : IExactSmbAccessProbe
{
    private readonly ICommonConnectorRequestTransport _transport;
    private readonly Func<IPAddress, bool> _destinationAllowed;
    private readonly TimeProvider _time;
    private readonly Func<CancellationToken, ValueTask<NetworkOverlaySnapshot>> _liveOverlay;
    private readonly Func<Func<CancellationToken, ValueTask<NetworkOverlaySnapshot>>, IWindowsSmbMappingPort> _mappingFactory;
    private readonly Func<string, CancellationToken, ValueTask<bool>> _authenticatedRead;

    public WindowsSmbAccessProbe(
        ICommonConnectorRequestTransport transport,
        Func<IPAddress, bool> destinationAllowed,
        INetworkOverlayClient overlay,
        TimeProvider? timeProvider = null)
        : this(
            transport,
            destinationAllowed,
            timeProvider ?? TimeProvider.System,
            overlay is null
                ? throw new ArgumentNullException(nameof(overlay))
                : overlay.GetStatusAsync,
            liveness => new WindowsSmbMappingPort(liveness),
            ReadDirectoryMetadataAsync)
    {
    }

    internal WindowsSmbAccessProbe(
        ICommonConnectorRequestTransport transport,
        Func<IPAddress, bool> destinationAllowed,
        TimeProvider timeProvider,
        Func<CancellationToken, ValueTask<NetworkOverlaySnapshot>> liveOverlay,
        Func<Func<CancellationToken, ValueTask<NetworkOverlaySnapshot>>, IWindowsSmbMappingPort> mappingFactory,
        Func<string, CancellationToken, ValueTask<bool>> authenticatedRead)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _destinationAllowed = destinationAllowed ?? throw new ArgumentNullException(nameof(destinationAllowed));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _liveOverlay = liveOverlay ?? throw new ArgumentNullException(nameof(liveOverlay));
        _mappingFactory = mappingFactory ?? throw new ArgumentNullException(nameof(mappingFactory));
        _authenticatedRead = authenticatedRead ?? throw new ArgumentNullException(nameof(authenticatedRead));
    }

    public async ValueTask<bool> VerifyAuthenticatedReadAsync(
        ExactSmbAccessProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ExactRequest(request)) return false;

        async ValueTask<NetworkOverlaySnapshot> ReadExactLiveOverlayAsync(CancellationToken token)
        {
            var live = await _liveOverlay(token).ConfigureAwait(false);
            if (!ExactLiveOverlay(request.Overlay, live))
                throw new InvalidOperationException("The managed overlay changed during the authenticated SMB probe.");
            return live;
        }

        async ValueTask<bool> ReadWithLiveOverlayFenceAsync(string mappedRoot, CancellationToken token)
        {
            await ReadExactLiveOverlayAsync(token).ConfigureAwait(false);
            var read = await _authenticatedRead(mappedRoot, token).ConfigureAwait(false);
            await ReadExactLiveOverlayAsync(token).ConfigureAwait(false);
            return read;
        }

        var port = _mappingFactory(ReadExactLiveOverlayAsync);
        ArgumentNullException.ThrowIfNull(port);
        using var service = new CommonSmbAccessService(
            _transport,
            () => request.Profile,
            _destinationAllowed,
            port,
            _time,
            authenticatedRead: ReadWithLiveOverlayFenceAsync);

        try
        {
            return await service.ProbeAuthenticatedReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CommonSmbAccessException exception) when (
            !string.Equals(exception.Code, "smb_route_cleanup_incomplete", StringComparison.Ordinal))
        {
            return false;
        }
        catch (Exception exception) when (IsClosedFailure(exception))
        {
            return false;
        }
    }

    private bool ExactRequest(ExactSmbAccessProbeRequest request)
    {
        var now = _time.GetUtcNow();
        return request.Revision >= 1 &&
            request.Profile.SchemaVersion == DeviceAccessProtocol.Version &&
            string.Equals(request.DeviceId, request.Profile.DeviceId, StringComparison.Ordinal) &&
            request.Profile.DesiredRevision == request.Revision &&
            request.Profile.AppliedRevision == request.Revision &&
            request.Profile.ExpiresAtUtc > now &&
            request.Profile.Resources is not null &&
            request.Profile.Resources.Any(resource =>
                resource.Permissions is not null && resource.Permissions.Contains(ConnectorPermission.Read)) &&
            request.Overlay.Status == NetworkServiceStatus.Ready &&
            request.Overlay.StartupCheckPassed &&
            request.Overlay.ManagementConnected &&
            request.Overlay.SignalConnected &&
            request.Overlay.ManagementUri is not null &&
            request.Overlay.ObservedAtUtc >= now - TimeSpan.FromMinutes(2) &&
            request.Overlay.ObservedAtUtc <= now + TimeSpan.FromMinutes(2) &&
            request.Overlay.AssignedInternalAddresses is { Count: > 0 };
    }

    private bool ExactLiveOverlay(NetworkOverlaySnapshot expected, NetworkOverlaySnapshot actual)
    {
        var now = _time.GetUtcNow();
        return actual.Status == NetworkServiceStatus.Ready &&
            actual.StartupCheckPassed &&
            actual.ManagementConnected &&
            actual.SignalConnected &&
            actual.ManagementUri is not null &&
            expected.ManagementUri is not null &&
            string.Equals(
                actual.ManagementUri.AbsoluteUri.TrimEnd('/'),
                expected.ManagementUri.AbsoluteUri.TrimEnd('/'),
                StringComparison.Ordinal) &&
            actual.ObservedAtUtc >= now - TimeSpan.FromMinutes(2) &&
            actual.ObservedAtUtc <= now + TimeSpan.FromMinutes(2) &&
            actual.AssignedInternalAddresses is { Count: > 0 } &&
            actual.AssignedInternalAddresses.ToHashSet().SetEquals(expected.AssignedInternalAddresses);
    }

    private static ValueTask<bool> ReadDirectoryMetadataAsync(
        string mappedRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(mappedRoot);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult((attributes & FileAttributes.Directory) != 0);
    }

    private static bool IsClosedFailure(Exception exception) => exception is
        Win32Exception or
        IOException or
        UnauthorizedAccessException or
        PlatformNotSupportedException or
        InvalidOperationException;
}
