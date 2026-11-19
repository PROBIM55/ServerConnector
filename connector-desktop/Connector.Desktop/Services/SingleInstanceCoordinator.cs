using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Connector.Desktop.Services;

/// <summary>
/// Owns the per-user desktop lifetime and carries a payload-free activation signal
/// from later launches to the first process. The open lock handle is the ownership
/// token, so Windows releases it even if the owner terminates unexpectedly.
/// </summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const byte ActivateCommand = 0x41;
    private const byte AcceptedResponse = 0x06;
    private static readonly TimeSpan DefaultStartupBudget = TimeSpan.FromSeconds(5);
    private readonly string _lockPath;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private FileStream? _ownership;
    private Task? _listener;
    private Action? _activationHandler;
    private int _activationPending;
    private int _disposed;

    private SingleInstanceCoordinator(string lockPath, string pipeName)
    {
        _lockPath = lockPath;
        _pipeName = pipeName;
    }

    public bool IsPrimary => _ownership is not null;
    public bool ExistingInstanceSignaled { get; private set; }

    public static SingleInstanceCoordinator AcquireOrSignal()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Platform Connector",
            "Agent");
        return AcquireOrSignal(directory, "production", DefaultStartupBudget);
    }

    internal static SingleInstanceCoordinator AcquireOrSignal(
        string lockDirectory,
        string instanceScope,
        TimeSpan startupBudget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceScope);
        if (startupBudget <= TimeSpan.Zero || startupBudget > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(startupBudget));

        var fullDirectory = Path.GetFullPath(lockDirectory);
        Directory.CreateDirectory(fullDirectory);
        var identity = CreateIdentity(instanceScope);
        var coordinator = new SingleInstanceCoordinator(
            Path.Combine(fullDirectory, $"desktop-{identity}.lock"),
            $"Platform.Connector.Desktop.{identity}");
        coordinator.AcquireOrNotify(startupBudget);
        return coordinator;
    }

    public void SetActivationHandler(Action handler)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(handler);
        if (!IsPrimary) throw new InvalidOperationException("Only the primary instance accepts activation.");
        if (Interlocked.CompareExchange(ref _activationHandler, handler, null) is not null)
            throw new InvalidOperationException("The activation handler is already registered.");
        DispatchPendingActivation();
    }

    private void AcquireOrNotify(TimeSpan startupBudget)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < startupBudget)
        {
            if (TryAcquireOwnership())
            {
                _listener = Task.Run(ListenAsync);
                return;
            }

            var remaining = startupBudget - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            if (TrySignalExisting(remaining < TimeSpan.FromMilliseconds(500)
                    ? remaining
                    : TimeSpan.FromMilliseconds(500)))
            {
                ExistingInstanceSignaled = true;
                return;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(25));
        }

        // A live owner that does not accept activation is still authoritative.
        // Exit the later launch instead of starting a competing desktop runtime.
    }

    private bool TryAcquireOwnership()
    {
        FileStream? candidate = null;
        try
        {
            candidate = new FileStream(
                _lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            _ownership = candidate;
            return true;
        }
        catch (IOException)
        {
            candidate?.Dispose();
            return false;
        }
    }

    private bool TrySignalExisting(TimeSpan timeout)
        => TrySignalExistingAsync(timeout).GetAwaiter().GetResult();

    private async Task<bool> TrySignalExistingAsync(TimeSpan timeout)
    {
        using var budget = new CancellationTokenSource(timeout);
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(budget.Token).ConfigureAwait(false);
            await client.WriteAsync(new[] { ActivateCommand }, budget.Token).ConfigureAwait(false);
            await client.FlushAsync(budget.Token).ConfigureAwait(false);
            var response = new byte[1];
            return await client.ReadAsync(response, budget.Token).ConfigureAwait(false) == 1 &&
                   response[0] == AcceptedResponse;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ListenAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                requestBudget.CancelAfter(TimeSpan.FromSeconds(2));
                var command = new byte[1];
                if (await server.ReadAsync(command, requestBudget.Token).ConfigureAwait(false) != 1 ||
                    command[0] != ActivateCommand) continue;
                QueueActivation();
                await server.WriteAsync(new[] { AcceptedResponse }, requestBudget.Token).ConfigureAwait(false);
                await server.FlushAsync(requestBudget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (!_shutdown.IsCancellationRequested)
            {
                // A caller may disappear after connecting. Recreate the server and
                // continue accepting activation from the next bounded launch.
            }
            catch (OperationCanceledException)
            {
                // A same-user caller connected but did not complete the fixed
                // one-byte exchange. Recreate the server for the next launch.
            }
        }
    }

    private void QueueActivation()
    {
        Interlocked.Exchange(ref _activationPending, 1);
        DispatchPendingActivation();
    }

    private void DispatchPendingActivation()
    {
        var handler = Volatile.Read(ref _activationHandler);
        if (handler is not null && Interlocked.Exchange(ref _activationPending, 0) != 0)
        {
            try { handler(); }
            catch { }
        }
    }

    private static string CreateIdentity(string instanceScope)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"Platform.Connector.Desktop|{sid}|{instanceScope}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _activationHandler = null;
        _shutdown.Cancel();
        try { _listener?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        finally
        {
            _ownership?.Dispose();
            _ownership = null;
            _shutdown.Dispose();
        }
    }
}
