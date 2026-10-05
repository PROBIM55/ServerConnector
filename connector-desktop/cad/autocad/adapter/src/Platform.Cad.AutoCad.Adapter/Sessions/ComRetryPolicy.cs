using System.Runtime.InteropServices;

namespace Platform.Cad.AutoCad.Adapter.Sessions;

/// <summary>
/// Retry-политика для «AutoCAD занят»: модальный диалог или активная команда
/// заставляют COM-маршаллер отвечать RPC_E_CALL_REJECTED / RPC_E_SERVERCALL_RETRYLATER.
/// Вместо регистрации IMessageFilter (CoRegisterMessageFilter) — простой ручной
/// retry с линейным backoff на STA-потоке: и проще, и предсказуемее в тестах.
/// </summary>
public static class ComRetryPolicy
{
    /// <summary>RPC_E_CALL_REJECTED (0x80010001) — фильтр сообщений сервера отклонил вызов.</summary>
    public const int RpcECallRejected = unchecked((int)0x80010001);

    /// <summary>RPC_E_SERVERCALL_RETRYLATER (0x8001010A) — сервер занят, повторить позже.</summary>
    public const int RpcEServerCallRetryLater = unchecked((int)0x8001010A);

    public const int DefaultMaxAttempts = 5;
    public const int DefaultBaseDelayMs = 400;

    public static bool IsBusyHResult(int hresult)
        => hresult is RpcECallRejected or RpcEServerCallRetryLater;

    public static bool IsBusyException(Exception exception)
        => exception is COMException com && IsBusyHResult(com.HResult);

    /// <summary>
    /// Выполняет action; на busy-HRESULT повторяет до maxAttempts с задержкой
    /// baseDelay * номер_попытки (sleep допустим — вызывается на выделенном STA-потоке).
    /// Не-busy исключения пробрасываются сразу. delay инжектится в тестах.
    /// </summary>
    public static T Execute<T>(
        Func<T> action,
        int maxAttempts = DefaultMaxAttempts,
        int baseDelayMs = DefaultBaseDelayMs,
        Action<int>? delay = null,
        CancellationToken cancellationToken = default)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "maxAttempts must be >= 1.");
        }

        var sleep = delay ?? Thread.Sleep;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return action();
            }
            catch (Exception ex) when (IsBusyException(ex) && attempt < maxAttempts)
            {
                sleep(baseDelayMs * attempt);
            }
        }
    }
}
