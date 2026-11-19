using System.Collections.Concurrent;
using System.Data.Common;
using System.Threading.Channels;

namespace Connector.Access;

// A provider throws this when the command is valid but an external party has
// not finished its part yet (for example a VPN peer that has not connected).
// The outbox retries it on a short schedule instead of the generic backoff.
public class DeviceAccessProviderNotReadyException : Exception
{
    public DeviceAccessProviderNotReadyException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

// A provider throws this when its local per-device state is temporarily held
// by another reconciler. HTTP readers map it to 503 with Retry-After.
public class DeviceAccessProviderBusyException : Exception
{
    public DeviceAccessProviderBusyException(string message, Exception? innerException = null)
        : base(message, innerException) { }

    public virtual TimeSpan RetryAfter => TimeSpan.FromSeconds(1);
}

// In-process wake-up for provider dispatch lanes. Durable eligibility still
// belongs to the outbox tables; a signal only shortens the idle wait, so a lost
// or extra signal can never change which command is dispatched.
public sealed class ConnectorAccessDispatchSignal
{
    private readonly ConcurrentDictionary<string, Channel<bool>> _lanes = new(StringComparer.OrdinalIgnoreCase);

    public void Notify(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return;
        Lane(providerName).Writer.TryWrite(true);
    }

    public void Notify(IEnumerable<string> providerNames)
    {
        foreach (var providerName in providerNames) Notify(providerName);
    }

    // Returns true when woken by a signal, false when the timeout elapsed.
    public async ValueTask<bool> WaitAsync(
        string providerName,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var reader = Lane(providerName).Reader;
        if (reader.TryRead(out _)) return true;
        if (timeout <= TimeSpan.Zero) return false;

        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await reader.WaitToReadAsync(linked.Token);
            return reader.TryRead(out _);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private Channel<bool> Lane(string providerName) => _lanes.GetOrAdd(providerName, static _ =>
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false,
        }));
}

internal static class ProviderRetryPolicy
{
    public const string DeadLetterPrefix = "dead_letter:";

    // "Not ready" (VPN peer still connecting) and "busy" are waits, not
    // failures: they never count toward parking and their delay grows with
    // the time since the command was (re-)armed, so a waiting device is served
    // quickly and an abandoned one costs one attempt per minute.
    private static readonly (TimeSpan UntilAge, TimeSpan Delay)[] NotReadySchedule =
    [
        (TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2)),
        (TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3)),
        (TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5)),
        (TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10)),
        (TimeSpan.FromHours(1), TimeSpan.FromSeconds(30)),
    ];

    // A wait that has lasted this long is parked like a failed grant and is
    // revived only by the device's own vpn reads.
    public static readonly TimeSpan WaitCeiling = TimeSpan.FromHours(6);

    // A polling client may pull a waiting apply forward, but not more often
    // than this since the last attempt, so the wait still grows towards 60 s.
    public static readonly TimeSpan WaitRearmGap = TimeSpan.FromSeconds(30);

    public static bool IsWait(Exception? failure) =>
        failure is DeviceAccessProviderNotReadyException or DeviceAccessProviderBusyException;

    // command.AttemptCount counts generic failures of the current arming,
    // including this claim; waits are refunded by the repository.
    public static (DateTimeOffset RetryAt, bool DeadLetter) Next(
        DeviceAccessOptions options,
        StoredProviderCommand command,
        Exception? failure,
        DateTimeOffset now)
    {
        if (IsWait(failure))
        {
            var age = command.CreatedAtUtc == default ? TimeSpan.Zero : now - command.CreatedAtUtc;
            var parked = age >= WaitCeiling && command.Kind == DeviceAccessProviderCommandKind.Apply;
            return (now.Add(NotReadyDelay(age)), parked);
        }

        var attempt = Math.Max(1, command.AttemptCount);
        return (now.Add(ExponentialDelay(options, attempt)), IsDeadLetter(options, command, attempt));
    }

    internal static TimeSpan NotReadyDelay(TimeSpan age)
    {
        foreach (var (untilAge, delay) in NotReadySchedule)
        {
            if (age < untilAge) return delay;
        }
        return TimeSpan.FromMinutes(1);
    }

    internal static TimeSpan ExponentialDelay(DeviceAccessOptions options, int attempt)
    {
        var initial = options.ProviderRetryBackoff > TimeSpan.Zero ? options.ProviderRetryBackoff : TimeSpan.FromSeconds(1);
        var maximum = options.ProviderRetryBackoffMaximum >= initial ? options.ProviderRetryBackoffMaximum : initial;
        var ticks = initial.Ticks * Math.Pow(2, Math.Min(attempt - 1, 30));
        return ticks >= maximum.Ticks ? maximum : TimeSpan.FromTicks((long)ticks);
    }

    // Only grants are parked. A revoke is a security action and keeps retrying
    // on the capped exponential schedule until the provider confirms it.
    private static bool IsDeadLetter(DeviceAccessOptions options, StoredProviderCommand command, int attempt) =>
        command.Kind == DeviceAccessProviderCommandKind.Apply &&
        options.ProviderDeadLetterAttempts > 0 &&
        attempt >= options.ProviderDeadLetterAttempts;

    public static bool IsTransientDatabaseConflict(Exception exception) =>
        exception is DbException { SqlState: "40001" or "40P01" };
}
