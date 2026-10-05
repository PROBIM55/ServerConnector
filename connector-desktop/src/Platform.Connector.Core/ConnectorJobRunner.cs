namespace Platform.Connector.Core;

public sealed class ConnectorJobRunner
{
    private const int TerminalPublishAttempts = 3;
    private const string ProviderMissingErrorCode = "CONNECTOR_PROVIDER_MISSING";
    private const string ExecutorMissingErrorCode = "CONNECTOR_EXECUTOR_MISSING";
    private const string UnhandledErrorCode = "UNHANDLED_EXCEPTION";
    private const string InterruptedErrorCode = "CONNECTOR_EXECUTION_INTERRUPTED_REVIEW_REQUIRED";

    private readonly Dictionary<string, IConnectorJobExecutor> _executors;
    private readonly IJobStatusPublisher _publisher;
    private readonly IRequestIdempotencyStore _idempotencyStore;
    private readonly IConnectorLogger _logger;

    public ConnectorJobRunner(
        IEnumerable<IProviderAdapter> providers,
        IJobStatusPublisher publisher,
        IRequestIdempotencyStore? idempotencyStore = null,
        IConnectorLogger? logger = null,
        IEnumerable<IConnectorJobExecutor>? executors = null)
    {
        _executors = new Dictionary<string, IConnectorJobExecutor>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            RegisterExecutor(new ProviderAdapterExecutor(provider));
        }
        foreach (var executor in executors ?? [])
        {
            RegisterExecutor(executor);
        }
        _publisher = publisher;
        _idempotencyStore = idempotencyStore ?? NullRequestIdempotencyStore.Instance;
        _logger = logger ?? new JsonLineConnectorLogger(null);
    }

    public async Task<ConnectorJobStatusEnvelope> RunOnceAsync(string deviceId, ConnectorJobEnvelope job, CancellationToken cancellationToken = default)
        => await RunOnceAsync(deviceId, job, _publisher, cancellationToken).ConfigureAwait(false);

    internal async Task<ConnectorJobStatusEnvelope> RunOnceAsync(
        string deviceId,
        ConnectorJobEnvelope job,
        IJobStatusPublisher publisher,
        CancellationToken cancellationToken = default,
        string? remoteAuthority = null,
        Func<bool>? interruptionRequested = null)
    {
        await PublishAsync(publisher, deviceId, job, JobStatus.Picked, "Job picked by connector.", cancellationToken: cancellationToken);
        _logger.Info("Job picked.", job);

        if (!_executors.TryGetValue(job.EffectiveExecutorId, out var executor))
        {
            var isLegacyProviderJob = string.IsNullOrWhiteSpace(job.ExecutorId);
            var terminal = BuildStatusEnvelope(
                deviceId,
                job,
                JobStatus.Error,
                isLegacyProviderJob
                    ? "Provider adapter is not registered."
                    : $"Executor '{job.EffectiveExecutorId}' is not registered.",
                errorCode: isLegacyProviderJob ? ProviderMissingErrorCode : ExecutorMissingErrorCode);

            await PersistAndPublishTerminalAsync(publisher, terminal, job, cancellationToken, remoteAuthority);
            _logger.Warn("Job executor is not registered.", job);
            return terminal;
        }

        await PublishAsync(publisher, deviceId, job, JobStatus.Running, "Job execution started.", progress: 5, cancellationToken: cancellationToken);
        _logger.Info("Job execution started.", job);

        ConnectorExecutionResult result;
        try
        {
            result = await executor.ExecuteAsync(
                job,
                async (update, progressCancellationToken) =>
                {
                    await PublishAsync(
                        publisher,
                        deviceId,
                        job,
                        JobStatus.Running,
                        update.Message,
                        progress: Math.Clamp(update.Progress, 5, 99),
                        result: update.Details,
                        cancellationToken: progressCancellationToken);
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var interrupted = interruptionRequested?.Invoke() == true;
            var cancelled = BuildStatusEnvelope(
                deviceId,
                job,
                interrupted ? JobStatus.Interrupted : JobStatus.Cancelled,
                interrupted
                    ? "Execution was interrupted by connector shutdown; verify external effects before retrying."
                    : "Execution was cancelled.",
                errorCode: interrupted ? InterruptedErrorCode : null);

            await PersistAndPublishTerminalAsync(publisher, cancelled, job, cancellationToken, remoteAuthority);
            _logger.Warn(interrupted ? "Job was interrupted by connector shutdown." : "Job was cancelled by cancellation token.", job);
            return cancelled;
        }
        catch (Exception ex)
        {
            var terminal = BuildStatusEnvelope(
                deviceId,
                job,
                JobStatus.Error,
                $"Unhandled executor exception: {ex.Message}",
                errorCode: UnhandledErrorCode);

            await PersistAndPublishTerminalAsync(publisher, terminal, job, cancellationToken, remoteAuthority);
            _logger.Error("Unhandled executor exception.", job, exception: ex);
            return terminal;
        }

        if (result.IsSuccess)
        {
            var terminal = BuildStatusEnvelope(
                deviceId,
                job,
                JobStatus.Success,
                result.Message ?? "Execution finished successfully.",
                progress: 100,
                result: result.Result);

            await PersistAndPublishTerminalAsync(publisher, terminal, job, cancellationToken, remoteAuthority);
            _logger.Info("Job completed successfully.", job);
            return terminal;
        }

        var failedTerminal = BuildStatusEnvelope(
            deviceId,
            job,
            JobStatus.Error,
            result.Message ?? "Execution failed.",
            errorCode: result.ErrorCode,
            result: result.Result);

        await PersistAndPublishTerminalAsync(publisher, failedTerminal, job, cancellationToken, remoteAuthority);
        _logger.Warn("Job completed with executor error.", job);
        return failedTerminal;
    }

    internal async Task<ConnectorJobStatusEnvelope> CancelBeforeExecutionAsync(
        string deviceId,
        ConnectorJobEnvelope job,
        IJobStatusPublisher publisher,
        string message,
        CancellationToken cancellationToken,
        string? remoteAuthority = null)
    {
        var cancelled = BuildStatusEnvelope(deviceId, job, JobStatus.Cancelled, message);
        await PersistAndPublishTerminalAsync(publisher, cancelled, job, cancellationToken, remoteAuthority).ConfigureAwait(false);
        _logger.Warn("Queued job was cancelled before execution.", job);
        return cancelled;
    }

    internal async Task<ConnectorJobStatusEnvelope> InterruptRecoveredLocalAsync(
        string deviceId,
        ConnectorJobEnvelope job,
        IJobStatusPublisher publisher,
        CancellationToken cancellationToken)
    {
        var interrupted = BuildInterruptedLocalStatus(deviceId, job);
        await PersistAndPublishTerminalAsync(
            publisher,
            interrupted,
            job,
            cancellationToken,
            remoteAuthority: null).ConfigureAwait(false);
        _logger.Warn("Recovered running local job requires manual review and was not executed again.", job);
        return interrupted;
    }

    internal LocalJobSnapshot BuildStoredLocalSnapshot(StoredLocalJob stored)
    {
        var status = stored.State switch
        {
            StoredLocalJobState.Queued => BuildStatusEnvelope(
                stored.DeviceId,
                stored.Job,
                JobStatus.Queued,
                "Job is queued for recovery.",
                progress: 0),
            StoredLocalJobState.Running => BuildInterruptedLocalStatus(stored.DeviceId, stored.Job),
            _ => throw new InvalidDataException(
                $"Durable local request '{stored.Job.RequestId}' has unsupported state '{stored.State}'.")
        };
        return new LocalJobSnapshot(stored.Job, status, stored.State, stored.UpdatedAtUtc);
    }

    private Task PublishAsync(
        IJobStatusPublisher publisher,
        string deviceId,
        ConnectorJobEnvelope job,
        JobStatus status,
        string message,
        int? progress = null,
        string? errorCode = null,
        System.Text.Json.JsonElement? result = null,
        CancellationToken cancellationToken = default)
    {
        var envelope = BuildStatusEnvelope(deviceId, job, status, message, progress, errorCode, result);
        return publisher.PublishAsync(envelope, cancellationToken);
    }

    private ConnectorJobStatusEnvelope BuildStatusEnvelope(
        string deviceId,
        ConnectorJobEnvelope job,
        JobStatus status,
        string message,
        int? progress = null,
        string? errorCode = null,
        System.Text.Json.JsonElement? result = null)
    {
        return new ConnectorJobStatusEnvelope(
            SchemaVersion: job.SchemaVersion,
            RequestId: job.RequestId,
            DeviceId: deviceId,
            ModuleId: job.ModuleId,
            Provider: job.Provider,
            Status: status,
            UpdatedAtUtc: DateTime.UtcNow,
            Message: message,
            Progress: progress,
            ErrorCode: errorCode,
            Result: result,
            CorrelationId: job.CorrelationId,
            ExecutorId: job.EffectiveExecutorId,
            Scope: job.Scope);
    }

    private ConnectorJobStatusEnvelope BuildInterruptedLocalStatus(
        string deviceId,
        ConnectorJobEnvelope job)
        => BuildStatusEnvelope(
            deviceId,
            job,
            JobStatus.Interrupted,
            "Connector restarted while this operation was Running. Verify external effects before retrying.",
            errorCode: InterruptedErrorCode);

    private async Task PersistAndPublishTerminalAsync(
        IJobStatusPublisher publisher,
        ConnectorJobStatusEnvelope terminal,
        ConnectorJobEnvelope sourceJob,
        CancellationToken cancellationToken,
        string? remoteAuthority)
    {
        // Once execution has produced a terminal result, caller cancellation cannot turn it
        // into a different outcome. Persist/publish is the serialized completion boundary.
        await _idempotencyStore.SaveTerminalAsync(
            terminal,
            published: false,
            CancellationToken.None,
            remoteAuthority,
            terminal.Scope?.ScopeKind == ConnectorScopeKind.DeviceLocal ? sourceJob : null);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await publisher.PublishAsync(terminal, cancellationToken);
                await _idempotencyStore.MarkPublishedAsync(terminal.RequestId, CancellationToken.None);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < TerminalPublishAttempts
                                       && ex is not SessionConflictException
                                       && ex is not TokenRejectedException)
            {
                var delay = TimeSpan.FromMilliseconds(100 * (1 << (attempt - 1)));
                _logger.Warn(
                    $"Terminal status publish failed. Retry={attempt}/{TerminalPublishAttempts}, backoff={delay.TotalMilliseconds:F0}ms.",
                    requestId: terminal.RequestId,
                    exception: ex);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    internal Task<StoredTerminalStatus?> TryGetStoredTerminalAsync(string requestId)
        => _idempotencyStore.TryGetAsync(requestId, CancellationToken.None);

    internal Task<IReadOnlyList<StoredLocalJob>> GetStoredLocalJobsAsync()
        => _idempotencyStore.GetLocalJobsAsync(CancellationToken.None);

    internal Task SaveLocalQueuedAsync(string deviceId, ConnectorJobEnvelope job)
        => _idempotencyStore.SaveLocalQueuedAsync(deviceId, job, CancellationToken.None);

    internal Task MarkLocalRunningAsync(string requestId)
        => _idempotencyStore.MarkLocalRunningAsync(requestId, CancellationToken.None);

    internal void ValidateReusableLocalTerminal(
        StoredTerminalStatus stored,
        string deviceId,
        ConnectorJobEnvelope requestedJob)
    {
        var source = stored.SourceJob;
        if (source is null
            || !string.Equals(stored.Envelope.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
            || !JobsMatch(source, requestedJob))
        {
            throw new InvalidOperationException(
                $"Request '{requestedJob.RequestId}' already exists with different or unverifiable durable identity.");
        }
    }

    internal void WarnForeignLocalRecovery(StoredLocalJob stored, string expectedDeviceId)
        => _logger.Warn(
            $"Durable local request belongs to device '{stored.DeviceId}' and cannot be recovered by '{expectedDeviceId}'.",
            stored.Job);

    internal void WarnLocalRecoveryFailure(string requestId, Exception exception)
        => _logger.Warn("Recovered local request did not complete.", requestId: requestId, exception: exception);

    internal async Task<ConnectorJobStatusEnvelope> RetryStoredTerminalUntilPublishedAsync(
        string requestId,
        IJobStatusPublisher publisher,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(250);
        var maxDelay = TimeSpan.FromSeconds(2);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stored = await _idempotencyStore.TryGetAsync(requestId, CancellationToken.None)
                ?? throw new InvalidOperationException($"Stored terminal status '{requestId}' was not found.");
            if (stored.Published)
            {
                return stored.Envelope;
            }

            try
            {
                await publisher.PublishAsync(stored.Envelope, cancellationToken);
                await _idempotencyStore.MarkPublishedAsync(requestId, CancellationToken.None);
                return stored.Envelope;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not SessionConflictException && ex is not TokenRejectedException)
            {
                _logger.Warn(
                    $"Stored terminal status is still pending. Backoff={delay.TotalMilliseconds:F0}ms.",
                    requestId: requestId,
                    exception: ex);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, maxDelay.TotalMilliseconds));
            }
        }
    }

    private void RegisterExecutor(IConnectorJobExecutor executor)
    {
        var executorId = (executor.ExecutorId ?? string.Empty).Trim().ToLowerInvariant();
        if (executorId.Length == 0)
        {
            throw new ArgumentException("Connector executor id is required.", nameof(executor));
        }
        if (!_executors.TryAdd(executorId, executor))
        {
            throw new InvalidOperationException($"Connector executor '{executorId}' is registered more than once.");
        }
    }

    private static bool JobsMatch(ConnectorJobEnvelope left, ConnectorJobEnvelope right)
        => left.SchemaVersion == right.SchemaVersion
           && string.Equals(left.RequestId, right.RequestId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.ModuleId, right.ModuleId, StringComparison.Ordinal)
           && left.Provider == right.Provider
           && left.Operation == right.Operation
           && left.CreatedAtUtc == right.CreatedAtUtc
           && string.Equals(left.CorrelationId, right.CorrelationId, StringComparison.Ordinal)
           && string.Equals(left.EffectiveExecutorId, right.EffectiveExecutorId, StringComparison.Ordinal)
           && left.Scope == right.Scope
           && left.Payload.ValueKind == right.Payload.ValueKind
           && (left.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined
               || string.Equals(left.Payload.GetRawText(), right.Payload.GetRawText(), StringComparison.Ordinal));

    private sealed class ProviderAdapterExecutor(IProviderAdapter provider) : IConnectorJobExecutor
    {
        public string ExecutorId => ConnectorExecutorIds.FromProvider(provider.Provider);

        public async Task<ConnectorExecutionResult> ExecuteAsync(
            ConnectorJobEnvelope job,
            Func<ConnectorExecutionProgress, CancellationToken, Task>? progress,
            CancellationToken cancellationToken)
        {
            var result = await provider.ExecuteAsync(job, cancellationToken);
            return new ConnectorExecutionResult(
                result.IsSuccess,
                result.Message,
                result.ErrorCode,
                result.Result);
        }
    }
}
