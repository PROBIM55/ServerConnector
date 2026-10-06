namespace Platform.Connector.Core;

/// <summary>Параметры supervisor-цикла. Все значения подменяемы в тестах.</summary>
public sealed class ConnectorSupervisorOptions
{
    /// <summary>Первая пауза перед повтором bootstrap (дальше — экспоненциально).</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Потолок backoff — supervisor никогда не молотит сервер чаще.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Если worker проработал дольше этого порога до сбоя, серия неудач
    /// считается законченной и backoff сбрасывается к Initial.
    /// </summary>
    public TimeSpan StableRunThreshold { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Подменяемая задержка (в тестах — мгновенная с записью значений).</summary>
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;
}

/// <summary>
/// Супервизор runtime: держит коннектор живым без участия пользователя.
///
/// Раньше worker-цикл на 409 (session superseded) просто завершался и runtime
/// «умирал» до ручного перезапуска; падение сервера на старте тоже было фатальным.
/// Теперь:
///   - 409 → автоматический re-bootstrap + продолжение (фаза Reconnecting);
///   - сервер недоступен на старте → ретраи bootstrap с capped exponential
///     backoff (фаза WaitingForServer);
///   - 401/403 → терминальная остановка с фазой TokenInvalid (без бесконечных
///     ретраев по невалидному токену);
///   - неожиданное падение worker'а → re-bootstrap с backoff.
///
/// Останавливается только по CancellationToken (штатный Stop) или TokenInvalid.
/// </summary>
public sealed class ConnectorRuntimeSupervisor(
    IConnectorControlPlaneClient controlPlane,
    ConnectorWorker worker,
    ConnectorRuntimeState runtimeState,
    IConnectorLogger logger,
    ConnectorSupervisorOptions? supervisorOptions = null)
{
    private readonly ConnectorSupervisorOptions _supervisorOptions = supervisorOptions ?? new ConnectorSupervisorOptions();

    /// <summary>Возвращает финальную фазу (Stopped или TokenInvalid).</summary>
    public async Task<ConnectorRuntimePhase> RunAsync(
        ConnectorRuntimeOptions options,
        CancellationToken cancellationToken,
        Action<string>? log = null)
    {
        var consecutiveFailures = 0;
        var hasConnectedOnce = false;
        var tokenInvalid = false;

        runtimeState.MarkPhase(ConnectorRuntimePhase.Connecting);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // ── 1. Bootstrap (health → bootstrap → первый heartbeat) ──
                try
                {
                    await BootstrapAndPrimeAsync(options, cancellationToken, log);
                    runtimeState.MarkPhase(ConnectorRuntimePhase.Connected);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (TokenRejectedException ex)
                {
                    logger.Error("Bootstrap rejected: device token is invalid. Runtime stopped.", exception: ex);
                    log?.Invoke("Сервер отклонил токен устройства (401/403). Проверьте токен и подключитесь заново.");
                    runtimeState.MarkPhase(ConnectorRuntimePhase.TokenInvalid, ex.Message);
                    tokenInvalid = true;
                    break;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    var phase = hasConnectedOnce
                        ? ConnectorRuntimePhase.Reconnecting
                        : ConnectorRuntimePhase.WaitingForServer;
                    runtimeState.MarkPhase(phase, ex.Message);

                    var backoff = ComputeBackoff(consecutiveFailures);
                    runtimeState.MarkRetryScheduled(consecutiveFailures, DateTime.UtcNow.Add(backoff));
                    logger.Warn(
                        $"Bootstrap failed (attempt {consecutiveFailures}). Retry in {backoff.TotalSeconds:F0}s.",
                        exception: ex);
                    log?.Invoke($"Сервер недоступен: {ex.Message}. Повтор через {backoff.TotalSeconds:F0} с.");

                    if (!await TryDelayAsync(backoff, cancellationToken))
                    {
                        break;
                    }

                    continue;
                }

                hasConnectedOnce = true;

                // ── 2. Worker (heartbeat + poll + jobs) ──
                var workerStartedAtUtc = DateTime.UtcNow;
                ConnectorWorkerExitReason exitReason;
                runtimeState.MarkWorkerStarted();
                try
                {
                    exitReason = await worker.RunAsync(options, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.Error("Worker crashed unexpectedly. Supervisor will re-bootstrap.", exception: ex);
                    log?.Invoke($"Внутренний сбой runtime: {ex.Message}. Автоматический перезапуск…");
                    exitReason = ConnectorWorkerExitReason.SessionConflict; // retryable path
                }
                finally
                {
                    runtimeState.MarkWorkerStopped();
                }

                if (cancellationToken.IsCancellationRequested || exitReason == ConnectorWorkerExitReason.Cancelled)
                {
                    break;
                }

                if (exitReason == ConnectorWorkerExitReason.TokenRejected)
                {
                    log?.Invoke("Сервер отклонил токен устройства (401/403). Проверьте токен и подключитесь заново.");
                    runtimeState.MarkPhase(ConnectorRuntimePhase.TokenInvalid, "Device token rejected by server.");
                    tokenInvalid = true;
                    break;
                }

                // SessionConflict (или внутренний сбой) → re-bootstrap.
                var ranStable = DateTime.UtcNow - workerStartedAtUtc >= _supervisorOptions.StableRunThreshold;
                consecutiveFailures = ranStable ? 1 : consecutiveFailures + 1;

                runtimeState.MarkPhase(ConnectorRuntimePhase.Reconnecting, "Сессия вытеснена или потеряна — переподключение.");
                var reconnectBackoff = ComputeBackoff(consecutiveFailures);
                runtimeState.MarkRetryScheduled(consecutiveFailures, DateTime.UtcNow.Add(reconnectBackoff));
                logger.Warn($"Session lost (exit={exitReason}). Re-bootstrap in {reconnectBackoff.TotalSeconds:F0}s.");
                log?.Invoke($"Сессия потеряна. Переподключение через {reconnectBackoff.TotalSeconds:F0} с…");

                if (!await TryDelayAsync(reconnectBackoff, cancellationToken))
                {
                    break;
                }
            }
        }
        finally
        {
            if (!tokenInvalid)
            {
                runtimeState.MarkPhase(ConnectorRuntimePhase.Stopped);
            }
        }

        return tokenInvalid ? ConnectorRuntimePhase.TokenInvalid : ConnectorRuntimePhase.Stopped;
    }

    private async Task BootstrapAndPrimeAsync(
        ConnectorRuntimeOptions options,
        CancellationToken cancellationToken,
        Action<string>? log)
    {
        await controlPlane.CheckHealthAsync(options, cancellationToken);
        var bootstrap = await controlPlane.BootstrapAsync(options, cancellationToken);

        if (!string.IsNullOrWhiteSpace(bootstrap.SessionId))
        {
            options.SessionId = bootstrap.SessionId;
        }

        if (!string.IsNullOrWhiteSpace(bootstrap.DeviceId))
        {
            options.DeviceId = bootstrap.DeviceId;
        }

        if (bootstrap.HeartbeatSeconds >= 10)
        {
            options.HeartbeatSeconds = bootstrap.HeartbeatSeconds;
        }

        await controlPlane.SendHeartbeatAsync(options, cancellationToken);
        logger.Info($"Bootstrap OK. DeviceId={options.DeviceId}, SessionId={options.SessionId}.");
        log?.Invoke($"Bootstrap OK: DeviceId={options.DeviceId}, SessionId={options.SessionId}");
    }

    private TimeSpan ComputeBackoff(int consecutiveFailures)
    {
        var initial = Math.Max(0.05, _supervisorOptions.InitialBackoff.TotalSeconds);
        var max = Math.Max(initial, _supervisorOptions.MaxBackoff.TotalSeconds);
        var exponent = Math.Min(20, Math.Max(0, consecutiveFailures - 1));
        var seconds = initial * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, max));
    }

    private async Task<bool> TryDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await _supervisorOptions.DelayAsync(delay, cancellationToken);
            return !cancellationToken.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
