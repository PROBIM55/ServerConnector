using Connector.Upgrade.MachineDispatcher;
using Connector.Upgrade.MachineIpc;
using Connector.Upgrade.MutualMachineChannel;

namespace Connector.Upgrade.MachineCommandChannel;

/// <summary>Sequential command client over an already mutually authenticated A pipe.</summary>
public sealed class MachineCommandClient : IDisposable
{
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(2);
    private readonly MutualMachineAuthenticatedSession _session;
    private readonly HashSet<Guid> _correlations = [];
    private int _busy;
    private int _disposed;

    public MachineCommandClient(MutualMachineAuthenticatedSession authenticatedSession) =>
        _session = authenticatedSession ?? throw new ArgumentNullException(nameof(authenticatedSession));

    public async ValueTask<MachineDispatcherResult> SendAsync(MachineIpcRequest request, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        ValidateRequest(request);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("Concurrent machine command use is rejected.");
        try { ThrowIfDisposed(); _session.EnterCommandUse(); }
        catch { Volatile.Write(ref _busy, 0); throw; }
        try
        {
            if (!_correlations.Add(request.CorrelationId)) throw new InvalidOperationException("Replayed machine command correlation id is rejected.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            MachineDispatcherResult? response = null;
            await _session.RunWithPeerAsync(async token =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                await MachineIpcFrameCodec.WriteAsync(_session.Stream, request, linked.Token).ConfigureAwait(false);
                response = await MachineDispatcherResultFrameCodec.ReadAsync(_session.Stream, linked.Token).ConfigureAwait(false);
                if (response is null) throw new EndOfStreamException("Machine command peer closed before returning a response.");
                if (response.CorrelationId != request.CorrelationId || response.Operation != request.Operation)
                    throw new InvalidDataException("Machine command response correlation or operation does not match its request.");
            }, deadline.Token).ConfigureAwait(false);
            return response!;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { Dispose(); throw new TimeoutException("Timed out during machine command exchange."); }
        catch { Dispose(); throw; }
        finally { _session.ExitCommandUse(); Volatile.Write(ref _busy, 0); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _session.Dispose();
    }

    private void ThrowIfDisposed()
    { if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(MachineCommandClient)); }

    internal static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > MaximumTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout), $"Timeout must be positive and no greater than {MaximumTimeout}.");
    }

    internal static void ValidateRequest(MachineIpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Version != MachineIpcFrameCodec.CurrentVersion || request.CorrelationId == Guid.Empty ||
            request.Operation is not (MachineIpcOperation.Inspect or MachineIpcOperation.Prepare or MachineIpcOperation.Apply or
                MachineIpcOperation.Reconcile or MachineIpcOperation.Remove or MachineIpcOperation.Restore or
                MachineIpcOperation.StageVelopack or MachineIpcOperation.PrepareUserStateStage or
                MachineIpcOperation.StageStructuraRollback or MachineIpcOperation.StagePlatformRollback or
                MachineIpcOperation.Rollover))
            throw new InvalidDataException("Machine command request is invalid or outside the allowed operation set.");
    }
}

/// <summary>Sequential server loop. The dispatcher is supplied only after mutual authentication succeeds.</summary>
public static class MachineCommandServer
{
    internal const int MaximumCommandsPerSession = 32;

    public static ValueTask ServeAsync(MutualMachineAuthenticatedSession authenticatedSession,
        MachineUpgradeDispatcher dispatcher, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return ServeCoreAsync(authenticatedSession, dispatcher.DispatchAsync, timeout, cancellationToken);
    }

    internal static ValueTask ServeCoreAsync(MutualMachineAuthenticatedSession authenticatedSession,
        Func<MachineIpcRequest, CancellationToken, ValueTask<MachineDispatcherResult>> dispatch,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authenticatedSession);
        ArgumentNullException.ThrowIfNull(dispatch);
        MachineCommandClient.ValidateTimeout(timeout);
        return new ValueTask(RunAsync(authenticatedSession, dispatch, timeout, cancellationToken));
    }

    private static async Task RunAsync(MutualMachineAuthenticatedSession session,
        Func<MachineIpcRequest, CancellationToken, ValueTask<MachineDispatcherResult>> dispatch,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        session.EnterCommandUse();
        var correlations = new HashSet<Guid>();
        try
        {
            while (true)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(timeout);
                MachineIpcRequest? request = null;
                await session.RunWithPeerAsync(async peerToken =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(peerToken, deadline.Token);
                    request = await MachineIpcFrameCodec.ReadAsync(session.Stream, linked.Token).ConfigureAwait(false);
                    if (request is null) throw new EndOfStreamException("Machine command peer closed the authenticated session.");
                    MachineCommandClient.ValidateRequest(request);
                    if (correlations.Contains(request.CorrelationId))
                        throw new InvalidDataException("Replayed machine command correlation id is rejected.");
                    if (correlations.Count >= MaximumCommandsPerSession)
                        throw new InvalidDataException("Machine command session exceeded its maximum command count.");
                    correlations.Add(request.CorrelationId);
                    var result = await dispatch(request, linked.Token).ConfigureAwait(false);
                    if (result.CorrelationId != request.CorrelationId || result.Operation != request.Operation)
                        throw new InvalidDataException("Dispatcher returned a mismatched machine command response.");
                    await MachineDispatcherResultFrameCodec.WriteAsync(session.Stream, result, linked.Token).ConfigureAwait(false);
                }, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { session.Dispose(); throw new TimeoutException("Timed out while serving machine commands."); }
        catch { session.Dispose(); throw; }
        finally { session.ExitCommandUse(); session.Dispose(); }
    }
}
