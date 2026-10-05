namespace Platform.Connector.Core;

/// <summary>Почему остановился цикл worker'а — supervisor решает, что делать дальше.</summary>
public enum ConnectorWorkerExitReason
{
    /// <summary>Остановка по CancellationToken (штатный Stop).</summary>
    Cancelled,

    /// <summary>HTTP 409 — сессия вытеснена другим входом. Supervisor делает re-bootstrap.</summary>
    SessionConflict,

    /// <summary>HTTP 401/403 — токен отклонён сервером. Supervisor останавливается терминально.</summary>
    TokenRejected
}

public sealed class ConnectorWorker(
    IConnectorControlPlaneClient controlPlaneClient,
    ConnectorJobRunner runner,
    IJobStatusPublisher statusPublisher,
    IRequestIdempotencyStore idempotencyStore,
    IConnectorLogger logger,
    ConnectorExecutionGate? executionGate = null,
    ConnectorJobDispatcher? dispatcher = null)
{
    public async Task<ConnectorWorkerExitReason> RunAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
    {
        if (dispatcher is not null && executionGate is null)
        {
            throw new InvalidOperationException("A shared execution gate is required when the worker uses the dispatcher.");
        }

        var exitReason = ConnectorWorkerExitReason.Cancelled;
        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, options.PollIntervalSeconds));
        var maxBackoff = TimeSpan.FromSeconds(Math.Max(options.PollIntervalSeconds, options.PollBackoffMaxSeconds));
        var heartbeatInterval = TimeSpan.FromSeconds(Math.Max(10, options.HeartbeatSeconds));
        var nextHeartbeatAt = DateTime.UtcNow;
        var pollBackoff = pollInterval;
        var remoteAuthority = ConnectorRemoteAuthority.Normalize(options.ServerUrl);

        logger.Info("Connector worker started.");

        try
        {
            await ReplayPendingRemoteTerminalsAsync(options, remoteAuthority, cancellationToken);
        }
        catch (SessionConflictException ex)
        {
            logger.Error("Pending terminal replay rejected: session conflict. Supervisor will re-bootstrap.", exception: ex);
            return ConnectorWorkerExitReason.SessionConflict;
        }
        catch (TokenRejectedException ex)
        {
            logger.Error("Pending terminal replay rejected: device token is invalid.", exception: ex);
            return ConnectorWorkerExitReason.TokenRejected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ConnectorWorkerExitReason.Cancelled;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if ((options.AuthenticationMode == ConnectorAuthenticationMode.IssuedCertificate ||
                 !string.IsNullOrWhiteSpace(options.DeviceToken)) && DateTime.UtcNow >= nextHeartbeatAt)
            {
                try
                {
                    await controlPlaneClient.SendHeartbeatAsync(options, cancellationToken);
                    nextHeartbeatAt = DateTime.UtcNow.Add(heartbeatInterval);
                }
                catch (SessionConflictException ex)
                {
                    logger.Error("Heartbeat rejected: session conflict. Supervisor will re-bootstrap.", exception: ex);
                    exitReason = ConnectorWorkerExitReason.SessionConflict;
                    break;
                }
                catch (TokenRejectedException ex)
                {
                    logger.Error("Heartbeat rejected: device token is invalid.", exception: ex);
                    exitReason = ConnectorWorkerExitReason.TokenRejected;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.Warn("Heartbeat failed.", requestId: null, exception: ex);
                }
            }

            // Lease начинается до poll: сервер мог уже назначить задание,
            // хотя runner еще не получил его. Drain обязан дождаться и этого запроса.
            using var execution = new TransferableExecutionLease(executionGate?.TryEnter());
            if (executionGate is not null && !execution.HasLease)
            {
                try { await Task.Delay(pollInterval, cancellationToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            ConnectorJobEnvelope? job;
            try
            {
                job = await controlPlaneClient.TryPollJobAsync(options, cancellationToken);
                pollBackoff = pollInterval;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SessionConflictException ex)
            {
                logger.Error("Poll rejected: session conflict. Supervisor will re-bootstrap.", exception: ex);
                exitReason = ConnectorWorkerExitReason.SessionConflict;
                break;
            }
            catch (TokenRejectedException ex)
            {
                logger.Error("Poll rejected: device token is invalid.", exception: ex);
                exitReason = ConnectorWorkerExitReason.TokenRejected;
                break;
            }
            catch (Exception ex)
            {
                execution.Dispose();
                logger.Warn($"Poll failed. Backoff={pollBackoff.TotalSeconds:F0}s", exception: ex);
                try
                {
                    await Task.Delay(pollBackoff, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                pollBackoff = pollBackoff + pollBackoff;
                if (pollBackoff > maxBackoff)
                {
                    pollBackoff = maxBackoff;
                }

                continue;
            }

            if (job is null)
            {
                execution.Dispose();
                try
                {
                    await Task.Delay(pollInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            var stored = await idempotencyStore.TryGetAsync(job.RequestId, cancellationToken);
            if (stored is not null)
            {
                if (!stored.Published)
                {
                    if (dispatcher is not null
                        && !CanRoutePendingRemote(stored, options, remoteAuthority))
                    {
                        continue;
                    }

                    logger.Info("Duplicate delivery detected. Re-publishing terminal status.", job);
                    try
                    {
                        await statusPublisher.PublishAsync(stored.Envelope, cancellationToken);
                        await idempotencyStore.MarkPublishedAsync(job.RequestId, cancellationToken);
                        dispatcher?.CompletePendingDelivery(job.RequestId);
                    }
                    catch (SessionConflictException ex)
                    {
                        logger.Error("Re-publish rejected: session conflict. Supervisor will re-bootstrap.", job: job, exception: ex);
                        exitReason = ConnectorWorkerExitReason.SessionConflict;
                        break;
                    }
                    catch (TokenRejectedException ex)
                    {
                        logger.Error("Re-publish rejected: device token is invalid.", job: job, exception: ex);
                        exitReason = ConnectorWorkerExitReason.TokenRejected;
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger.Warn("Re-publish of duplicate terminal status failed.", requestId: job.RequestId, exception: ex);
                    }
                }
                else
                {
                    logger.Info("Duplicate delivery detected. Job already finalized and published.", job);
                    dispatcher?.CompletePendingDelivery(job.RequestId);
                }

                continue;
            }

            try
            {
                if (dispatcher is null)
                {
                    await runner.RunOnceAsync(
                        options.DeviceId,
                        job,
                        statusPublisher,
                        cancellationToken,
                        remoteAuthority);
                }
                else
                {
                    await dispatcher.DispatchAcceptedRemoteAsync(
                        options.DeviceId,
                        job,
                        statusPublisher,
                        execution.Transfer(),
                        cancellationToken,
                        remoteAuthority);
                }
            }
            catch (SessionConflictException ex)
            {
                logger.Error("Status publish rejected: session conflict. Supervisor will re-bootstrap.", job: job, exception: ex);
                exitReason = ConnectorWorkerExitReason.SessionConflict;
                break;
            }
            catch (TokenRejectedException ex)
            {
                logger.Error("Status publish rejected: device token is invalid.", job: job, exception: ex);
                exitReason = ConnectorWorkerExitReason.TokenRejected;
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.Error("Job execution failed unexpectedly.", job, exception: ex);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            exitReason = ConnectorWorkerExitReason.Cancelled;
        }

        logger.Info($"Connector worker stopped. Reason={exitReason}.");
        return exitReason;
    }

    private async Task ReplayPendingRemoteTerminalsAsync(
        ConnectorRuntimeOptions options,
        string remoteAuthority,
        CancellationToken cancellationToken)
    {
        if (dispatcher is null)
        {
            return;
        }

        var pending = await idempotencyStore.GetPendingAsync(cancellationToken);
        foreach (var stored in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = stored.Envelope;
            if (stored.Published || status.Scope?.ScopeKind == ConnectorScopeKind.DeviceLocal)
            {
                continue;
            }
            if (!CanRoutePendingRemote(stored, options, remoteAuthority))
            {
                continue;
            }

            logger.Info("Replaying persisted terminal status after bootstrap.", requestId: status.RequestId);
            await dispatcher.DispatchPendingRemoteTerminalAsync(
                status.RequestId,
                statusPublisher,
                cancellationToken);
        }
    }

    private bool CanRoutePendingRemote(
        StoredTerminalStatus stored,
        ConnectorRuntimeOptions options,
        string remoteAuthority)
    {
        var status = stored.Envelope;
        if (status.Scope?.ScopeKind == ConnectorScopeKind.DeviceLocal)
        {
            logger.Warn(
                "Device-local terminal status is not eligible for remote publication.",
                requestId: status.RequestId);
            return false;
        }
        if (string.IsNullOrWhiteSpace(status.DeviceId))
        {
            logger.Warn(
                "Pending terminal status has no device authority metadata and cannot be routed safely.",
                requestId: status.RequestId);
            return false;
        }
        if (string.IsNullOrWhiteSpace(stored.RemoteAuthority))
        {
            logger.Warn(
                "Pending remote terminal status has no server authority metadata and will not be sent.",
                requestId: status.RequestId);
            return false;
        }
        if (!string.Equals(stored.RemoteAuthority, remoteAuthority, StringComparison.Ordinal))
        {
            logger.Warn(
                "Pending remote terminal status belongs to a different server authority and will not be sent.",
                requestId: status.RequestId);
            return false;
        }
        if (!string.Equals(status.DeviceId, options.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            logger.Warn(
                $"Pending terminal status belongs to device '{status.DeviceId}' and cannot be routed by device '{options.DeviceId}'.",
                requestId: status.RequestId);
            return false;
        }

        return true;
    }

    private sealed class TransferableExecutionLease(IDisposable? lease) : IDisposable
    {
        private IDisposable? _lease = lease;

        public bool HasLease => Volatile.Read(ref _lease) is not null;

        public IDisposable Transfer()
            => Interlocked.Exchange(ref _lease, null)
               ?? throw new InvalidOperationException("Execution lease was already released or transferred.");

        public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
    }
}
