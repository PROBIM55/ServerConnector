using System.Diagnostics;

namespace Connector.Upgrade.WindowsMsi;

/// <summary>
/// Starts an already-authorized MSI operation without shell execution or elevation. Cancellation
/// stops waiting but deliberately does not kill msiexec because the mutation outcome is then unknown.
/// </summary>
public sealed class SystemWindowsMsiProcessRunner : IWindowsMsiProcessRunner
{
    public async ValueTask<WindowsMsiProcessResult> RunAsync(
        WindowsMsiProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Path.IsPathFullyQualified(request.FileName))
            throw new ArgumentException("The MSI process path must be absolute.", nameof(request));
        if (request.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "The MSI process timeout must be positive.");

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
                return new WindowsMsiProcessResult(
                    WindowsMsiProcessCompletion.StartFailed,
                    Failure: "Process.Start returned false.");
        }
        catch (Exception error) when (error is SystemException or InvalidOperationException)
        {
            return new WindowsMsiProcessResult(
                WindowsMsiProcessCompletion.StartFailed,
                Failure: error.GetType().Name);
        }

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            return new WindowsMsiProcessResult(WindowsMsiProcessCompletion.Exited, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            return new WindowsMsiProcessResult(
                cancellationToken.IsCancellationRequested
                    ? WindowsMsiProcessCompletion.Cancelled
                    : WindowsMsiProcessCompletion.TimedOut,
                Failure: "msiexec may still be running; its outcome requires an exact status probe.");
        }
    }
}
