namespace Platform.Connector.Core;

public enum ConnectorDrainPhase
{
    Running,
    Draining,
    ReadyToApply,
    Blocked
}

public sealed record ConnectorDrainSnapshot(ConnectorDrainPhase Phase, int ActiveOperations);

/// <summary>
/// Атомарно закрывает прием работы и дожидается уже принятых операций.
/// Lease охватывает получение задания, выполнение и запись/публикацию результата.
/// Тайм-аут не отменяет работу и не разрешает обновление; для продолжения нужен Resume.
/// </summary>
public sealed class ConnectorExecutionGate
{
    private readonly object _sync = new();
    private ConnectorDrainPhase _phase = ConnectorDrainPhase.Running;
    private int _activeOperations;
    private TaskCompletionSource<ConnectorDrainSnapshot>? _drainCompletion;
    private int _drainBarrierCount;

    public ConnectorDrainSnapshot Snapshot
    {
        get { lock (_sync) { return SnapshotUnsafe(); } }
    }

    public IDisposable? TryEnter()
    {
        lock (_sync)
        {
            if (_phase != ConnectorDrainPhase.Running || _drainBarrierCount != 0) { return null; }
            _activeOperations++;
            return new ExecutionLease(this);
        }
    }

    public async Task<ConnectorDrainSnapshot> RequestDrainAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        TaskCompletionSource<ConnectorDrainSnapshot> completion;
        lock (_sync)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _phase = ConnectorDrainPhase.Blocked;
                var previous = _drainCompletion;
                _drainCompletion = null;
                previous?.TrySetResult(SnapshotUnsafe());
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (_phase == ConnectorDrainPhase.Draining)
            {
                throw new InvalidOperationException("A drain request is already in progress.");
            }
            if (_phase == ConnectorDrainPhase.ReadyToApply) { return SnapshotUnsafe(); }
            _drainCompletion = null;
            _phase = _activeOperations == 0
                ? ConnectorDrainPhase.ReadyToApply
                : ConnectorDrainPhase.Draining;
            if (_phase == ConnectorDrainPhase.ReadyToApply) { return SnapshotUnsafe(); }
            completion = new TaskCompletionSource<ConnectorDrainSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _drainCompletion = completion;
        }

        try
        {
            var result = await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (TimeoutException)
        {
            return BlockRequest(completion);
        }
        catch (OperationCanceledException)
        {
            BlockRequest(completion);
            throw;
        }
    }

    /// <summary>
    /// Closes admission synchronously and joins a drain which was already
    /// started by another lifecycle action. The returned barrier prevents an
    /// older caller from reopening admission with <see cref="Resume"/> while a
    /// security invalidation waits for accepted work to finish.
    /// </summary>
    public IDisposable BeginProtectedDrain()
    {
        lock (_sync)
        {
            _drainBarrierCount++;
            return new DrainBarrier(this);
        }
    }

    /// <summary>
    /// Waits for an already running drain, or starts one when the gate is
    /// running/blocked. Unlike <see cref="RequestDrainAsync"/>, it is safe for
    /// a security invalidation that races an ordinary update drain.
    /// </summary>
    public async Task<ConnectorDrainSnapshot> RequestDrainOrJoinAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Task<ConnectorDrainSnapshot>? existingDrain = null;
        lock (_sync)
        {
            if (_phase == ConnectorDrainPhase.Draining)
            {
                existingDrain = _drainCompletion?.Task
                    ?? Task.FromResult(SnapshotUnsafe());
            }
        }

        if (existingDrain is not null)
        {
            var result = await existingDrain.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            // The original caller may have timed out and changed the gate to
            // Blocked. A protected caller still owns the security boundary, so
            // it starts the replacement drain for the already accepted work.
            if (result.Phase == ConnectorDrainPhase.ReadyToApply) return result;
        }

        return await RequestDrainAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Отменяет подготовку к обновлению и снова разрешает прием работы.</summary>
    public void Resume()
    {
        lock (_sync)
        {
            if (_drainBarrierCount != 0) return;
            _phase = ConnectorDrainPhase.Running;
            var completion = _drainCompletion;
            _drainCompletion = null;
            completion?.TrySetResult(SnapshotUnsafe());
        }
    }

    private ConnectorDrainSnapshot BlockRequest(TaskCompletionSource<ConnectorDrainSnapshot> completion)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_drainCompletion, completion))
            {
                _phase = ConnectorDrainPhase.Blocked;
                completion.TrySetResult(SnapshotUnsafe());
            }
            // Истекший запрос не может получить Ready от более позднего drain.
            // Resume/new request при этом не затрагиваются старым completion.
            return new ConnectorDrainSnapshot(ConnectorDrainPhase.Blocked, _activeOperations);
        }
    }

    private void Exit()
    {
        lock (_sync)
        {
            _activeOperations--;
            if (_activeOperations == 0 && _phase == ConnectorDrainPhase.Draining)
            {
                _phase = ConnectorDrainPhase.ReadyToApply;
                _drainCompletion?.TrySetResult(SnapshotUnsafe());
            }
        }
    }

    private ConnectorDrainSnapshot SnapshotUnsafe() => new(_phase, _activeOperations);

    private void ReleaseDrainBarrier()
    {
        lock (_sync)
        {
            if (_drainBarrierCount <= 0) return;
            _drainBarrierCount--;
            // A failed protected drain must remain fail-closed until an
            // explicit later Resume. This also closes the tiny path where a
            // request failed before changing a running phase.
            if (_drainBarrierCount == 0 && _phase == ConnectorDrainPhase.Running)
            {
                _phase = ConnectorDrainPhase.Blocked;
            }
        }
    }

    private sealed class ExecutionLease(ConnectorExecutionGate owner) : IDisposable
    {
        private ConnectorExecutionGate? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
    }

    private sealed class DrainBarrier(ConnectorExecutionGate owner) : IDisposable
    {
        private ConnectorExecutionGate? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseDrainBarrier();
    }
}
