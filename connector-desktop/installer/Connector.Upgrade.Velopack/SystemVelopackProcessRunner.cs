using System.Diagnostics;

namespace Connector.Upgrade.Velopack;

/// <summary>
/// Cancellation stops waiting but deliberately never kills Setup.exe or Update.exe. Their exact
/// outcome must be established by the caller's post-operation probe.
/// </summary>
public sealed class SystemVelopackProcessRunner : IVelopackProcessRunner
{
    public async ValueTask<VelopackProcessResult> RunAsync(
        VelopackProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Path.IsPathFullyQualified(request.FileName))
            throw new ArgumentException("The Velopack process path must be absolute.", nameof(request));
        if (request.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "The process timeout must be positive.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = request.FileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            },
        };
        foreach (var argument in request.Arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start())
                return new VelopackProcessResult(
                    VelopackProcessCompletion.StartFailed,
                    Failure: "Process.Start returned false.");
        }
        catch (Exception error) when (error is SystemException or InvalidOperationException)
        {
            return new VelopackProcessResult(
                VelopackProcessCompletion.StartFailed,
                Failure: error.GetType().Name);
        }

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            return new VelopackProcessResult(VelopackProcessCompletion.Exited, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            return new VelopackProcessResult(
                cancellationToken.IsCancellationRequested
                    ? VelopackProcessCompletion.Cancelled
                    : VelopackProcessCompletion.TimedOut,
                Failure: "The process may still be running; an exact status probe is required.");
        }
    }
}
