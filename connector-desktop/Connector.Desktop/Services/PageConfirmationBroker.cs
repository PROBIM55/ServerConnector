using System.Security.Cryptography;

namespace Connector.Desktop.Services;

/// <summary>Text of a confirmation the page shows in its own dialog (Main.dc.html dialogs).</summary>
public sealed record PageConfirmation(
    string Title,
    string Text,
    IReadOnlyList<string> Items,
    string? Note,
    string Primary,
    bool Danger,
    string Cancel = "Отмена");

/// <summary>A confirmation waiting for the user; the id is a fresh random value known only to the page.</summary>
public sealed record PendingPageConfirmation(string Id, PageConfirmation Request);

/// <summary>
/// Native-initiated confirmations answered in the page instead of native message boxes. The native action asks
/// and waits; the page shows exactly one dialog and answers with the explicit «confirm-answer» command carrying
/// the random id. Only the current request can be answered, once; a wrong or stale id changes nothing.
/// The answer travels through the same RPC channel as every command, which accepts messages only from the
/// application document. Requests are served one at a time in arrival order.
/// </summary>
public sealed class PageConfirmationBroker : IDisposable
{
    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _queue = new();
    private bool _disposed;

    public event Action? Changed;

    public PendingPageConfirmation? Current
    {
        get { lock (_gate) return _queue.First is { } first ? new PendingPageConfirmation(first.Value.Id, first.Value.Request) : null; }
    }

    public Task<bool> RequestAsync(PageConfirmation request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var waiter = new Waiter(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), request);
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(false);
            waiter.Node = _queue.AddLast(waiter);
        }
        if (cancellationToken.CanBeCanceled)
            waiter.Registration = cancellationToken.Register(() => Complete(waiter, false));
        Changed?.Invoke();
        return waiter.Completion.Task;
    }

    /// <summary>Answers the current confirmation. False when the id is not the one the page was shown.</summary>
    public bool Answer(string? id, bool accept)
    {
        Waiter? waiter;
        lock (_gate)
        {
            waiter = _queue.First?.Value;
            if (waiter is null || string.IsNullOrEmpty(id) ||
                !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(waiter.Id), System.Text.Encoding.ASCII.GetBytes(id)))
                return false;
        }
        return Complete(waiter, accept);
    }

    private bool Complete(Waiter waiter, bool accept)
    {
        lock (_gate)
        {
            if (waiter.Node?.List is null) return false;
            _queue.Remove(waiter.Node);
        }
        waiter.Registration.Dispose();
        waiter.Completion.TrySetResult(accept);
        Changed?.Invoke();
        return true;
    }

    public void Dispose()
    {
        Waiter[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _queue.ToArray();
        }
        foreach (var waiter in pending) Complete(waiter, false);
    }

    private sealed class Waiter(string id, PageConfirmation request)
    {
        public string Id { get; } = id;
        public PageConfirmation Request { get; } = request;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
    }
}
