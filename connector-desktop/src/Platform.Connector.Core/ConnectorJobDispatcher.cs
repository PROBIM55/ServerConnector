using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Platform.Connector.Core;

/// <summary>
/// One FIFO consumer for all accepted local and remote jobs. Admission leases are
/// acquired before enqueue and released only after terminal persistence/publication.
/// </summary>
public sealed class ConnectorJobDispatcher : IAsyncDisposable
{
    private readonly ConnectorJobRunner _runner;
    private readonly ConnectorExecutionGate _executionGate;
    private readonly IJobStatusPublisher _localPublisher;
    private readonly Channel<DispatchRequest> _queue;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, PendingDeliveryLease> _pendingDeliveries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _enqueuedLocalRecoveries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _localRecoveryGate = new(1, 1);
    private readonly Task _consumer;
    private int _disposed;
    private int _localRecoveryCompleted;

    public ConnectorJobDispatcher(
        ConnectorJobRunner runner,
        ConnectorExecutionGate executionGate,
        IJobStatusPublisher localPublisher)
    {
        _runner = runner;
        _executionGate = executionGate;
        _localPublisher = localPublisher;
        _queue = Channel.CreateUnbounded<DispatchRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _consumer = ConsumeAsync();
    }

    public Task<ConnectorJobStatusEnvelope> SubmitLocalAsync(
        string deviceId,
        ConnectorJobEnvelope job,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (job.Scope is null || job.Scope.ScopeKind != ConnectorScopeKind.DeviceLocal)
        {
            throw new ArgumentException("Local jobs require an explicit DeviceLocal execution scope.", nameof(job));
        }

        var lease = _executionGate.TryEnter()
            ?? throw new InvalidOperationException("Connector is draining and does not accept new jobs.");
        return PersistAndEnqueueLocalAsync(deviceId, job, lease, cancellationToken);
    }

    internal async Task RecoverLocalAsync(string deviceId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _localRecoveryCompleted) != 0)
        {
            return;
        }

        await _localRecoveryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Volatile.Read(ref _localRecoveryCompleted) != 0)
            {
                return;
            }

            var storedJobs = await _runner.GetStoredLocalJobsAsync().ConfigureAwait(false);
            foreach (var stored in storedJobs.OrderBy(item => item.Sequence))
            {
                if (!string.Equals(stored.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    _runner.WarnForeignLocalRecovery(stored, deviceId);
                    continue;
                }
                if (_enqueuedLocalRecoveries.ContainsKey(stored.Job.RequestId))
                {
                    continue;
                }

                var terminal = await _runner.TryGetStoredTerminalAsync(stored.Job.RequestId).ConfigureAwait(false);
                if (terminal is not null)
                {
                    throw new InvalidDataException(
                        $"Request '{stored.Job.RequestId}' cannot be both queued and terminal.");
                }

                var lease = _executionGate.TryEnter()
                    ?? throw new InvalidOperationException("Connector is draining and cannot recover accepted local jobs.");
                if (!_enqueuedLocalRecoveries.TryAdd(stored.Job.RequestId, 0))
                {
                    lease.Dispose();
                    continue;
                }

                var completion = Enqueue(
                    stored.DeviceId,
                    stored.Job,
                    _localPublisher,
                    lease,
                    CancellationToken.None,
                    remoteAuthority: null,
                    stored.State);
                _ = ObserveRecoveredAsync(completion, stored.Job.RequestId);
            }

            Volatile.Write(ref _localRecoveryCompleted, 1);
        }
        finally
        {
            _localRecoveryGate.Release();
        }
    }

    internal Task<ConnectorJobStatusEnvelope> DispatchAcceptedRemoteAsync(
        string deviceId,
        ConnectorJobEnvelope job,
        IJobStatusPublisher publisher,
        IDisposable admissionLease,
        CancellationToken cancellationToken,
        string? remoteAuthority = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            admissionLease.Dispose();
            throw new ObjectDisposedException(nameof(ConnectorJobDispatcher));
        }
        return Enqueue(deviceId, job, publisher, admissionLease, cancellationToken, remoteAuthority);
    }

    internal Task<ConnectorJobStatusEnvelope> DispatchPendingRemoteTerminalAsync(
        string requestId,
        IJobStatusPublisher publisher,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var lease = _executionGate.TryEnter()
            ?? throw new InvalidOperationException("Connector is draining and cannot replay pending terminal status.");
        return EnqueuePendingTerminal(requestId, publisher, lease, cancellationToken);
    }

    internal void CompletePendingDelivery(string requestId)
        => ReleasePendingDelivery(requestId, disposeRegistration: true);

    private void ReleasePendingDelivery(string requestId, bool disposeRegistration)
    {
        if (_pendingDeliveries.TryRemove(requestId, out var pending))
        {
            pending.Dispose(disposeRegistration);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        _lifetime.Cancel();
        await _consumer.ConfigureAwait(false);
        foreach (var requestId in _pendingDeliveries.Keys)
        {
            CompletePendingDelivery(requestId);
        }
        _lifetime.Dispose();
    }

    private Task<ConnectorJobStatusEnvelope> Enqueue(
        string deviceId,
        ConnectorJobEnvelope job,
        IJobStatusPublisher publisher,
        IDisposable admissionLease,
        CancellationToken cancellationToken,
        string? remoteAuthority = null,
        StoredLocalJobState? persistedLocalState = null)
    {
        var completion = new TaskCompletionSource<ConnectorJobStatusEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new DispatchRequest(
            deviceId,
            job,
            null,
            publisher,
            admissionLease,
            cancellationToken,
            remoteAuthority,
            persistedLocalState,
            completion);

        if (!_queue.Writer.TryWrite(request))
        {
            admissionLease.Dispose();
            completion.TrySetException(new ObjectDisposedException(nameof(ConnectorJobDispatcher)));
        }
        return completion.Task;
    }

    private Task<ConnectorJobStatusEnvelope> EnqueuePendingTerminal(
        string requestId,
        IJobStatusPublisher publisher,
        IDisposable admissionLease,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ConnectorJobStatusEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new DispatchRequest(
            null,
            null,
            requestId,
            publisher,
            admissionLease,
            cancellationToken,
            null,
            null,
            completion);

        if (!_queue.Writer.TryWrite(request))
        {
            admissionLease.Dispose();
            completion.TrySetException(new ObjectDisposedException(nameof(ConnectorJobDispatcher)));
        }
        return completion.Task;
    }

    private async Task ConsumeAsync()
    {
        await foreach (var request in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            ConnectorJobStatusEnvelope? terminal = null;
            Exception? failure = null;
            var retainedForDelivery = false;
            try
            {
                using var execution = CancellationTokenSource.CreateLinkedTokenSource(
                    request.CancellationToken,
                    _lifetime.Token);

                if (request.PendingTerminalRequestId is not null)
                {
                    terminal = await _runner.RetryStoredTerminalUntilPublishedAsync(
                        request.PendingTerminalRequestId,
                        request.Publisher,
                        execution.Token).ConfigureAwait(false);
                    CompletePendingDelivery(request.PendingTerminalRequestId);
                }
                else if (request.PersistedLocalState == StoredLocalJobState.Running)
                {
                    terminal = await _runner.InterruptRecoveredLocalAsync(
                        request.DeviceId!,
                        request.Job!,
                        request.Publisher,
                        execution.Token).ConfigureAwait(false);
                }
                else if (_lifetime.IsCancellationRequested
                         && request.PersistedLocalState == StoredLocalJobState.Queued
                         && !request.CancellationToken.IsCancellationRequested)
                {
                    failure = new OperationCanceledException(
                        "Connector stopped before the durable local job started; it remains queued for recovery.");
                }
                else if (execution.IsCancellationRequested)
                {
                    terminal = await _runner.CancelBeforeExecutionAsync(
                        request.DeviceId!,
                        request.Job!,
                        request.Publisher,
                        "Execution was cancelled while queued.",
                        execution.Token,
                        request.RemoteAuthority).ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        if (request.PersistedLocalState == StoredLocalJobState.Queued)
                        {
                            await _runner.MarkLocalRunningAsync(request.RequestId).ConfigureAwait(false);
                        }
                        terminal = await _runner.RunOnceAsync(
                            request.DeviceId!,
                            request.Job!,
                            request.Publisher,
                            execution.Token,
                            request.RemoteAuthority,
                            request.PersistedLocalState is not null
                                ? () => _lifetime.IsCancellationRequested
                                      && !request.CancellationToken.IsCancellationRequested
                                : null).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (execution.IsCancellationRequested)
                    {
                        var stored = await _runner.TryGetStoredTerminalAsync(request.RequestId).ConfigureAwait(false);
                        if (stored is not null)
                        {
                            // Publishing may be cancelled after execution has already produced and
                            // persisted its terminal. Never overwrite that outcome with Cancelled.
                            terminal = stored.Envelope;
                            throw;
                        }

                        terminal = await _runner.CancelBeforeExecutionAsync(
                            request.DeviceId!,
                            request.Job!,
                            request.Publisher,
                            "Execution was cancelled before the executor started.",
                            execution.Token,
                            request.RemoteAuthority).ConfigureAwait(false);
                    }
                }

            }
            catch (Exception ex)
            {
                failure = ex;
                try
                {
                    var stored = await _runner.TryGetStoredTerminalAsync(request.RequestId).ConfigureAwait(false);
                    if (stored is { Published: false }
                        && !request.CancellationToken.IsCancellationRequested
                        && !_lifetime.IsCancellationRequested)
                    {
                        try
                        {
                            using var retryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                                request.CancellationToken,
                                _lifetime.Token);
                            terminal = await _runner.RetryStoredTerminalUntilPublishedAsync(
                                request.RequestId,
                                request.Publisher,
                                retryCancellation.Token).ConfigureAwait(false);
                            failure = null;
                        }
                        catch (Exception retryError)
                        {
                            failure = retryError;
                            if (!request.CancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                            {
                                retainedForDelivery = TryRetainPendingDelivery(request);
                            }
                        }
                    }
                }
                catch (Exception stateError)
                {
                    failure = new AggregateException(ex, stateError);
                    if (!request.CancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                    {
                        retainedForDelivery = TryRetainPendingDelivery(request);
                    }
                }
            }
            finally
            {
                if (!retainedForDelivery)
                {
                    request.AdmissionLease.Dispose();
                }
            }

            if (failure is not null)
            {
                request.Completion.TrySetException(failure);
            }
            else
            {
                request.Completion.TrySetResult(terminal!);
            }
        }
    }

    private async Task<ConnectorJobStatusEnvelope> PersistAndEnqueueLocalAsync(
        string deviceId,
        ConnectorJobEnvelope job,
        IDisposable admissionLease,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await _runner.TryGetStoredTerminalAsync(job.RequestId).ConfigureAwait(false);
            if (existing is not null)
            {
                _runner.ValidateReusableLocalTerminal(existing, deviceId, job);
                admissionLease.Dispose();
                return existing.Envelope;
            }

            await _runner.SaveLocalQueuedAsync(deviceId, job).ConfigureAwait(false);
        }
        catch
        {
            admissionLease.Dispose();
            throw;
        }

        return await Enqueue(
            deviceId,
            job,
            _localPublisher,
            admissionLease,
            cancellationToken,
            remoteAuthority: null,
            StoredLocalJobState.Queued).ConfigureAwait(false);
    }

    private async Task ObserveRecoveredAsync(
        Task<ConnectorJobStatusEnvelope> completion,
        string requestId)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _runner.WarnLocalRecoveryFailure(requestId, ex);
        }
    }

    private bool TryRetainPendingDelivery(DispatchRequest request)
    {
        var pending = new PendingDeliveryLease(request.AdmissionLease);
        if (!_pendingDeliveries.TryAdd(request.RequestId, pending))
        {
            return false;
        }

        var registration = request.CancellationToken.Register(
            static state =>
            {
                var (owner, requestId) = ((ConnectorJobDispatcher Owner, string RequestId))state!;
                owner.ReleasePendingDelivery(requestId, disposeRegistration: false);
            },
            (this, request.RequestId));
        pending.SetRegistration(registration);
        return true;
    }

    private sealed record DispatchRequest(
        string? DeviceId,
        ConnectorJobEnvelope? Job,
        string? PendingTerminalRequestId,
        IJobStatusPublisher Publisher,
        IDisposable AdmissionLease,
        CancellationToken CancellationToken,
        string? RemoteAuthority,
        StoredLocalJobState? PersistedLocalState,
        TaskCompletionSource<ConnectorJobStatusEnvelope> Completion)
    {
        public string RequestId => Job?.RequestId ?? PendingTerminalRequestId!;
    }

    private sealed class PendingDeliveryLease(IDisposable lease)
    {
        private readonly object _sync = new();
        private IDisposable? _lease = lease;
        private CancellationTokenRegistration _registration;

        public void SetRegistration(CancellationTokenRegistration registration)
        {
            lock (_sync)
            {
                if (_lease is null)
                {
                    registration.Dispose();
                    return;
                }
                _registration = registration;
            }
        }

        public void Dispose(bool disposeRegistration)
        {
            IDisposable? current;
            CancellationTokenRegistration registration;
            lock (_sync)
            {
                current = _lease;
                if (current is null)
                {
                    return;
                }
                _lease = null;
                registration = _registration;
                _registration = default;
            }
            if (disposeRegistration)
            {
                registration.Dispose();
            }
            current.Dispose();
        }
    }
}
