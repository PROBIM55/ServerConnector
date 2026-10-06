namespace Connector.Desktop.Services;

public enum DesktopBackgroundAction
{
    Heartbeat,
    AppUpdate,
    TeklaSync
}

public readonly record struct DesktopBackgroundStopResult(bool IsDrained, Task Completion);

/// <summary>
/// Owns the desktop process' three periodic business actions. A single loop
/// maintains independent deadlines and one in-flight task per action, so a
/// slow tick cannot overlap its next tick or delay the other business cycles.
/// </summary>
public sealed class DesktopBackgroundAgent : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<bool> _isLegacyMode;
    private readonly Func<CancellationToken, Task> _heartbeat;
    private readonly Func<CancellationToken, Task> _appUpdate;
    private readonly Func<CancellationToken, Task> _teklaSync;
    private readonly Action<DesktopBackgroundAction, Exception>? _onError;
    private readonly IDesktopBackgroundClock _clock;
    private readonly SemaphoreSlim _scheduleChanged = new(0, 1);
    private readonly Dictionary<DesktopBackgroundAction, Task> _activeActions = new();

    private TimeSpan _heartbeatInterval;
    private readonly TimeSpan _appUpdateInterval;
    private readonly TimeSpan _teklaSyncInterval;
    private DateTimeOffset _nextHeartbeatUtc;
    private DateTimeOffset _nextAppUpdateUtc;
    private DateTimeOffset _nextTeklaSyncUtc;
    private bool _heartbeatEnabled;
    private bool _disposed;
    private CancellationTokenSource? _lifetimeCts;
    private Task? _runTask;
    private Task? _stopTask;

    public DesktopBackgroundAgent(
        TimeSpan heartbeatInterval,
        TimeSpan appUpdateInterval,
        TimeSpan teklaSyncInterval,
        Func<bool> isLegacyMode,
        Func<CancellationToken, Task> heartbeat,
        Func<CancellationToken, Task> appUpdate,
        Func<CancellationToken, Task> teklaSync,
        Action<DesktopBackgroundAction, Exception>? onError = null)
        : this(
            heartbeatInterval,
            appUpdateInterval,
            teklaSyncInterval,
            isLegacyMode,
            heartbeat,
            appUpdate,
            teklaSync,
            onError,
            SystemDesktopBackgroundClock.Instance)
    {
    }

    internal DesktopBackgroundAgent(
        TimeSpan heartbeatInterval,
        TimeSpan appUpdateInterval,
        TimeSpan teklaSyncInterval,
        Func<bool> isLegacyMode,
        Func<CancellationToken, Task> heartbeat,
        Func<CancellationToken, Task> appUpdate,
        Func<CancellationToken, Task> teklaSync,
        Action<DesktopBackgroundAction, Exception>? onError,
        IDesktopBackgroundClock clock)
    {
        _heartbeatInterval = ValidateInterval(heartbeatInterval, nameof(heartbeatInterval));
        _appUpdateInterval = ValidateInterval(appUpdateInterval, nameof(appUpdateInterval));
        _teklaSyncInterval = ValidateInterval(teklaSyncInterval, nameof(teklaSyncInterval));
        _isLegacyMode = isLegacyMode ?? throw new ArgumentNullException(nameof(isLegacyMode));
        _heartbeat = heartbeat ?? throw new ArgumentNullException(nameof(heartbeat));
        _appUpdate = appUpdate ?? throw new ArgumentNullException(nameof(appUpdate));
        _teklaSync = teklaSync ?? throw new ArgumentNullException(nameof(teklaSync));
        _onError = onError;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _runTask is not null && _stopTask is null;
            }
        }
    }

    /// <summary>
    /// Starts the one scheduler lifecycle, or updates the heartbeat state of an
    /// already running lifecycle. Periodic actions first run after their interval.
    /// </summary>
    public void Start(bool heartbeatEnabled)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_stopTask is not null)
                throw new InvalidOperationException("The background scheduler is stopping.");

            var now = _clock.UtcNow;
            _heartbeatEnabled = heartbeatEnabled;
            _nextHeartbeatUtc = now + _heartbeatInterval;

            if (_runTask is not null)
            {
                SignalScheduleChangedNoLock();
                return;
            }

            _nextAppUpdateUtc = now + _appUpdateInterval;
            _nextTeklaSyncUtc = now + _teklaSyncInterval;
            _lifetimeCts = new CancellationTokenSource();
            var cancellationToken = _lifetimeCts.Token;
            _runTask = Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);
        }
    }

    public void SetHeartbeatEnabled(bool enabled)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_heartbeatEnabled == enabled) return;

            _heartbeatEnabled = enabled;
            if (enabled) _nextHeartbeatUtc = _clock.UtcNow + _heartbeatInterval;
            SignalScheduleChangedNoLock();
        }
    }

    public void SetHeartbeatInterval(TimeSpan interval)
    {
        interval = ValidateInterval(interval, nameof(interval));
        lock (_gate)
        {
            ThrowIfDisposed();
            _heartbeatInterval = interval;
            _nextHeartbeatUtc = _clock.UtcNow + interval;
            SignalScheduleChangedNoLock();
        }
    }

    public Task StopAsync()
    {
        Task runTask;
        CancellationTokenSource lifetimeCts;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_runTask is null) return Task.CompletedTask;
            if (_stopTask is not null) return _stopTask;

            runTask = _runTask;
            lifetimeCts = _lifetimeCts!;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
        }

        // Cancellation can run continuations synchronously; never signal it
        // while holding _gate because completion removes in-flight actions.
        lifetimeCts.Cancel();
        SignalScheduleChangedNoLock();
        _ = CompleteStopAsync(runTask, lifetimeCts, completion);
        return completion.Task;
    }

    /// <summary>
    /// Requests cancellation and waits only for the supplied drain window.
    /// Completion remains the safety boundary for disposing callback-owned
    /// resources when a callback cannot cooperate with cancellation.
    /// </summary>
    public async Task<DesktopBackgroundStopResult> StopAsync(TimeSpan drainTimeout)
    {
        drainTimeout = ValidateInterval(drainTimeout, nameof(drainTimeout));
        var completion = StopAsync();
        if (completion.IsCompleted)
        {
            await completion.ConfigureAwait(false);
            return new DesktopBackgroundStopResult(true, completion);
        }

        using var timeoutCts = new CancellationTokenSource();
        var timeoutTask = _clock.DelayAsync(drainTimeout, timeoutCts.Token);
        if (await Task.WhenAny(completion, timeoutTask).ConfigureAwait(false) == completion)
        {
            timeoutCts.Cancel();
            try { await timeoutTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested) { }
            await completion.ConfigureAwait(false);
            return new DesktopBackgroundStopResult(true, completion);
        }

        return new DesktopBackgroundStopResult(false, completion);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
        _scheduleChanged.Dispose();
    }

    private async Task CompleteStopAsync(
        Task runTask,
        CancellationTokenSource lifetimeCts,
        TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await runTask.ConfigureAwait(false);
            Task[] activeActions;
            lock (_gate) activeActions = _activeActions.Values.ToArray();
            try
            {
                await Task.WhenAll(activeActions).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested)
            {
                // Scheduled callbacks observe the shared lifecycle cancellation.
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            lifetimeCts.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(_runTask, runTask))
                {
                    _runTask = null;
                    _lifetimeCts = null;
                    _stopTask = null;
                }
            }

            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = _clock.UtcNow;
                var due = GetDueActions(now, out var delay);
                if (due.Count == 0)
                {
                    await WaitForDeadlineOrScheduleChangeAsync(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var action in due)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (action != DesktopBackgroundAction.AppUpdate && !CanRunLegacyAction(action))
                        continue;

                    StartActionIfIdle(action, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal lifecycle stop.
        }
    }

    private IReadOnlyList<DesktopBackgroundAction> GetDueActions(DateTimeOffset now, out TimeSpan delay)
    {
        var due = new List<DesktopBackgroundAction>(3);
        lock (_gate)
        {
            if (_heartbeatEnabled && now >= _nextHeartbeatUtc)
            {
                due.Add(DesktopBackgroundAction.Heartbeat);
                _nextHeartbeatUtc = now + _heartbeatInterval;
            }

            if (now >= _nextAppUpdateUtc)
            {
                due.Add(DesktopBackgroundAction.AppUpdate);
                _nextAppUpdateUtc = now + _appUpdateInterval;
            }

            if (now >= _nextTeklaSyncUtc)
            {
                due.Add(DesktopBackgroundAction.TeklaSync);
                _nextTeklaSyncUtc = now + _teklaSyncInterval;
            }

            var next = _nextAppUpdateUtc <= _nextTeklaSyncUtc ? _nextAppUpdateUtc : _nextTeklaSyncUtc;
            if (_heartbeatEnabled && _nextHeartbeatUtc < next) next = _nextHeartbeatUtc;
            delay = next > now ? next - now : TimeSpan.Zero;
        }

        return due;
    }

    private bool CanRunLegacyAction(DesktopBackgroundAction action)
    {
        try
        {
            return _isLegacyMode();
        }
        catch (Exception ex)
        {
            ReportError(action, ex);
            return false;
        }
    }

    private async Task RunActionAsync(DesktopBackgroundAction action, CancellationToken cancellationToken)
    {
        try
        {
            var callback = action switch
            {
                DesktopBackgroundAction.Heartbeat => _heartbeat,
                DesktopBackgroundAction.AppUpdate => _appUpdate,
                DesktopBackgroundAction.TeklaSync => _teklaSync,
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
            };
            await callback(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportError(action, ex);
        }
    }

    private void StartActionIfIdle(DesktopBackgroundAction action, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_activeActions.ContainsKey(action)) return;
        }

        var actionTask = RunActionAsync(action, cancellationToken);
        lock (_gate)
        {
            _activeActions[action] = actionTask;
        }

        _ = ObserveActionAsync(action, actionTask, cancellationToken);
    }

    private async Task ObserveActionAsync(
        DesktopBackgroundAction action,
        Task actionTask,
        CancellationToken cancellationToken)
    {
        try
        {
            await actionTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal lifecycle stop.
        }
        finally
        {
            lock (_gate)
            {
                if (_activeActions.TryGetValue(action, out var tracked) && ReferenceEquals(tracked, actionTask))
                    _activeActions.Remove(action);
            }
        }
    }

    private async Task WaitForDeadlineOrScheduleChangeAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero) return;

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = _clock.DelayAsync(delay, waitCts.Token);
        var scheduleTask = _scheduleChanged.WaitAsync(waitCts.Token);
        await Task.WhenAny(delayTask, scheduleTask).ConfigureAwait(false);
        waitCts.Cancel();

        try
        {
            await Task.WhenAll(delayTask, scheduleTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waitCts.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private void ReportError(DesktopBackgroundAction action, Exception exception)
    {
        try { _onError?.Invoke(action, exception); }
        catch { }
    }

    private void SignalScheduleChangedNoLock()
    {
        try { _scheduleChanged.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static TimeSpan ValidateInterval(TimeSpan interval, string parameterName)
    {
        if (interval <= TimeSpan.Zero || interval == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(parameterName, "A positive finite interval is required.");
        return interval;
    }
}

internal interface IDesktopBackgroundClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemDesktopBackgroundClock : IDesktopBackgroundClock
{
    public static SystemDesktopBackgroundClock Instance { get; } = new();

    private SystemDesktopBackgroundClock()
    {
    }

    public DateTimeOffset UtcNow => TimeProvider.System.GetUtcNow();

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, TimeProvider.System, cancellationToken);
}
