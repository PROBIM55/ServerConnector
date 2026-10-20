namespace Connector.Desktop.Services;

internal sealed record CommonRecoveryIdentity(string DeviceId, string UserId, string CompanyId);
internal sealed record CommonRecoveryAttempt(long Epoch, CommonRecoveryIdentity Identity, CancellationToken Token);
internal sealed record CommonRecoveryResult(bool Connected, string DiagnosticCode);

/// <summary>Process-local connection intent. Never owns credentials or browser enrollment.</summary>
internal sealed class CommonConnectionRecovery : IAsyncDisposable
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)];
    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _retry;
    private Task _completion = Task.CompletedTask;
    private CommonRecoveryIdentity? _readyIdentity;
    private long _epoch;
    private bool _desired;
    private bool _disposed;
    private string _progress = "";

    public CommonConnectionRecovery(TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, _time, token));
    }

    public event Action? Changed;
    public string Progress { get { lock (_sync) return _progress; } }
    public Task Completion { get { lock (_sync) return _completion; } }

    public long BeginConnect()
    {
        CancellationTokenSource? previous;
        long epoch;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _retry; _retry = null; _desired = true; _readyIdentity = null;
            _progress = ""; epoch = ++_epoch;
        }
        Cancel(previous);
        Notify();
        return epoch;
    }

    public bool IsCurrent(long epoch)
    {
        lock (_sync) return !_disposed && _desired && epoch == _epoch;
    }

    public static void RequireIdentity(CommonRecoveryAttempt? attempt, CommonRecoveryIdentity actual)
    {
        if (attempt is not null && attempt.Identity != actual)
            throw new InvalidOperationException("The recovery account identity changed.");
    }

    public void MarkReady(long epoch, CommonRecoveryIdentity identity)
    {
        lock (_sync) if (!_disposed && _desired && epoch == _epoch) _readyIdentity = identity;
    }

    public async Task StartOwnedAsync(long epoch, Func<Task> start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent(epoch)) throw new OperationCanceledException(cancellationToken);
        await start().ConfigureAwait(false);
        // Host startup itself is not cancellable. Logout/manual replacement
        // must still fence its completion before Ready or any folder mount.
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent(epoch)) throw new OperationCanceledException(cancellationToken);
    }

    public void Stop()
    {
        CancellationTokenSource? previous;
        lock (_sync)
        {
            _desired = false; ++_epoch; _readyIdentity = null;
            previous = _retry; _retry = null; _progress = "";
        }
        Cancel(previous);
        Notify();
    }

    public void Request(string diagnosticCode, string? deviceId,
        Func<CommonRecoveryAttempt, Task<CommonRecoveryResult>> resume)
    {
        lock (_sync)
        {
            if (_disposed || !_desired || _retry is not null || _readyIdentity is not { } identity ||
                identity.DeviceId != deviceId || !IsRetryable(diagnosticCode)) return;
            var retry = new CancellationTokenSource(TimeSpan.FromSeconds(90), _time);
            _retry = retry;
            _progress = "Восстанавливаем подключение…";
            var current = RunAsync(new(_epoch, identity, retry.Token), retry, resume);
            _completion = _completion.IsCompleted ? current : Task.WhenAll(_completion, current);
        }
        Notify();
    }

    internal static bool IsRetryable(string code) => code is
        "overlay_not_ready" or "vpn_revision_advanced" or "timeout" or "service_temporarily_unavailable";

    private async Task RunAsync(CommonRecoveryAttempt attempt, CancellationTokenSource retry,
        Func<CommonRecoveryAttempt, Task<CommonRecoveryResult>> resume)
    {
        await Task.Yield();
        try
        {
            foreach (var delay in Backoff)
            {
                await _delay(delay, attempt.Token).ConfigureAwait(false);
                attempt.Token.ThrowIfCancellationRequested();
                if (!IsCurrent(attempt.Epoch)) return;
                var result = await resume(attempt).ConfigureAwait(false);
                attempt.Token.ThrowIfCancellationRequested();
                if (!IsCurrent(attempt.Epoch) || result.Connected || !IsRetryable(result.DiagnosticCode)) return;
            }
        }
        catch (OperationCanceledException) when (attempt.Token.IsCancellationRequested) { }
        catch { /* Unknown failures never become a retry policy. */ }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_retry, retry)) { _retry = null; _progress = ""; }
            }
            retry.Dispose();
            Notify();
        }
    }

    private static void Cancel(CancellationTokenSource? source)
    {
        try { source?.Cancel(); } catch (ObjectDisposedException) { }
    }
    private void Notify()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList()) try { handler(); } catch { }
    }
    public async ValueTask DisposeAsync()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        Stop();
        try { await Completion.ConfigureAwait(false); } catch { }
    }
}
