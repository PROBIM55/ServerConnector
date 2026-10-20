namespace Connector.Desktop.Services;

public enum CommonConnectionOperation { Restore, SignIn, SignOut }

public sealed record CommonConnectionProgress(long Generation, CommonConnectionOperation? Operation, string Message);

/// <summary>
/// One owner for startup restore, browser handoff and explicit account changes.
/// Repeated connects join the owner; sign-out cancels it and waits for cleanup
/// before revoking the durable device. The runtime's CAD drain remains intact.
/// </summary>
public sealed class CommonConnectionOperations : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CommonConnectionAttempt? _active;
    private CommonConnectionProgress _progress = new(0, null, string.Empty);
    private bool _disposed;

    public event Action? Changed;
    public CommonConnectionProgress Progress { get { lock (_sync) return _progress; } }
    public bool IsLatest(long generation) { lock (_sync) return _progress.Generation == generation; }

    public CommonConnectionAttempt Start(CommonConnectionOperation operation,
        Func<CommonConnectionAttempt, CancellationToken, Task> execute, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);
        cancellationToken.ThrowIfCancellationRequested();
        CommonConnectionAttempt attempt;
        CommonConnectionAttempt? previous;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _active;
            if (previous is not null &&
                (operation == CommonConnectionOperation.Restore || previous.Operation == operation))
                return previous; // Never silently queue another restore/browser/revoke.
            if (previous is { Operation: CommonConnectionOperation.SignOut })
                return previous; // A closing account cannot start another browser handoff.
            attempt = new(this, _progress.Generation + 1, operation,
                CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken));
            _active = attempt;
            _progress = new(attempt.Generation, operation, operation switch
            {
                CommonConnectionOperation.SignIn => "Ожидаем подтверждения в браузере",
                CommonConnectionOperation.SignOut => "Выполняем выход…",
                _ => "Восстанавливаем подключение…"
            });
        }
        previous?.Cancel();
        Notify();
        _ = ExecuteAsync(attempt, previous?.Completion, execute);
        return attempt;
    }

    internal void Report(CommonConnectionAttempt attempt, string message)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_active, attempt) || attempt.Token.IsCancellationRequested) return;
            _progress = _progress with { Message = message };
        }
        Notify();
    }

    private async Task ExecuteAsync(CommonConnectionAttempt attempt, Task? previous,
        Func<CommonConnectionAttempt, CancellationToken, Task> execute)
    {
        Exception? error = null;
        try
        {
            if (previous is not null)
            {
                try { await previous; }
                catch { /* The predecessor's caller observes its own failure. */ }
            }
            attempt.Token.ThrowIfCancellationRequested();
            await execute(attempt, attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_active, attempt))
                {
                    _active = null;
                    _progress = _progress with { Operation = null, Message = string.Empty };
                }
            }
            attempt.Complete(error);
            Notify();
        }
    }

    private void Notify()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch { /* Observers cannot strand the operation. */ }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pending;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _active?.Completion;
        }
        _lifetime.Cancel();
        if (pending is not null) try { await pending; } catch { }
        _lifetime.Dispose();
    }
}

public sealed class CommonConnectionAttempt
{
    private readonly CommonConnectionOperations _owner;
    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CommonConnectionAttempt(CommonConnectionOperations owner, long generation,
        CommonConnectionOperation operation, CancellationTokenSource cancellation)
    {
        _owner = owner; Generation = generation; Operation = operation; _cancellation = cancellation;
        Token = cancellation.Token;
    }
    public long Generation { get; }
    public CommonConnectionOperation Operation { get; }
    public Task Completion => _completion.Task;
    public CancellationToken Token { get; }
    public void Report(string message) => _owner.Report(this, message);
    internal void Cancel() { try { _cancellation.Cancel(); } catch (ObjectDisposedException) { } }
    internal void Complete(Exception? error)
    {
        if (error is OperationCanceledException) _completion.TrySetCanceled(Token);
        else if (error is not null) _completion.TrySetException(error);
        else _completion.TrySetResult();
        _cancellation.Dispose();
    }
}
