namespace Connector.Network;

public static class CommonConnectorProvisioningWaiter
{
    public static async ValueTask<CommonConnectorConnectionSnapshot> WaitAsync(
        CommonConnectorConnectionSnapshot initial,
        Func<CancellationToken, ValueTask<CommonConnectorConnectionSnapshot>> resume,
        Func<CancellationToken, ValueTask> closeAuthorization,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(resume);
        ArgumentNullException.ThrowIfNull(closeAuthorization);
        var completed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsPending(initial)) { completed = true; return initial; }
            var clock = timeProvider ?? TimeProvider.System;
            using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2), clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
            var current = initial;
            try
            {
                while (IsPending(current))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), clock, linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    var next = await resume(linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    current = next;
                }
                completed = true;
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Deadline exhaustion leaves admission closed. It never turns a
                // pending state into Ready or hides explicit user cancellation.
            }
            return current;
        }
        finally
        {
            // Resume may have installed Ready just before its caller cancelled.
            // Cleanup cannot borrow that already-cancelled token.
            if (!completed) await closeAuthorization(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool IsPending(CommonConnectorConnectionSnapshot snapshot) =>
        snapshot.Status is CommonConnectorConnectionStatus.AccessPending or
            CommonConnectorConnectionStatus.AwaitingVpnAuthorization;
}
