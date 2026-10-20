using Connector.SmbAccess;

namespace Connector.Desktop.Services;

/// <summary>
/// Network changes while establishing the VPN are not changes to an admitted
/// session. Once admitted, every newer route/access invalidation keeps the
/// existing fail-closed drain and exact mapping cleanup.
/// </summary>
internal sealed class CommonConnectionActivation
{
    private readonly object _sync = new();
    private long _generation;
    private long _activeRouteEpoch;
    private bool _active;

    public void BeginSession(Action<long> bindSession)
    {
        lock (_sync)
        {
            _active = false;
            bindSession(checked(++_generation));
        }
    }

    public bool IsCurrent(long generation) => Volatile.Read(ref _generation) == generation;

    public async Task<bool> ActivateAsync(CommonSmbAccessService folders,
        Func<CancellationToken, Task> validateRoute, Func<bool> isReady,
        CancellationToken cancellationToken, Action? validateIdentity = null)
    {
        long generation;
        lock (_sync) generation = _generation;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Establishment may create/change the VPN. Validate its final live
            // identity AFTER taking the epoch, then admit only the same route.
            var routeEpoch = folders.RouteEpoch;
            await validateRoute(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                lock (_sync)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (generation != _generation || !isReady()) return false;
                    validateIdentity?.Invoke();
                    folders.EnableAuthorization(routeEpoch);
                    _activeRouteEpoch = routeEpoch;
                    _active = true;
                    return true;
                }
            }
            catch (CommonSmbAccessException exception) when
                (exception.Code == "smb_route_changed" && attempt == 0)
            {
                // One fresh proof handles a late OS notification during VPN
                // startup. Never re-enroll or reuse a browser token here.
            }
        }
    }

    public void InvalidateRoute(long routeEpoch, Action<long> invalidate)
    {
        lock (_sync)
        {
            // A notification may be delivered after activation even though its
            // epoch belongs to VPN establishment. It must not drain the new host.
            if (_active && routeEpoch > _activeRouteEpoch) invalidate(_generation);
        }
    }

    public void InvalidateAccess(long generation, Action<long> invalidate)
    {
        lock (_sync)
        {
            if (_active && generation == _generation) invalidate(generation);
        }
    }
}
