using System.ComponentModel;
using System.Diagnostics;

namespace Connector.Access.NetBird;

internal enum NetBirdDaemonCommandStatus
{
    Success,
    ExecutableUnavailable,
    Failed,
    TimedOut,
    OutputTooLarge
}

internal sealed record NetBirdDaemonCommand(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    int MaximumOutputBytes);

internal sealed record NetBirdDaemonCommandResult(
    NetBirdDaemonCommandStatus Status,
    byte[] StandardOutput)
{
    public static NetBirdDaemonCommandResult Failure(NetBirdDaemonCommandStatus status) => new(status, []);
}

internal interface INetBirdDaemonCommandRunner
{
    ValueTask<NetBirdDaemonCommandResult> RunAsync(
        NetBirdDaemonCommand command,
        CancellationToken cancellationToken);
}

internal sealed class ProcessNetBirdDaemonCommandRunner : INetBirdDaemonCommandRunner
{
    public static ProcessNetBirdDaemonCommandRunner Instance { get; } = new();

    private ProcessNetBirdDaemonCommandRunner()
    {
    }

    public async ValueTask<NetBirdDaemonCommandResult> RunAsync(
        NetBirdDaemonCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = command.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return NetBirdDaemonCommandResult.Failure(NetBirdDaemonCommandStatus.ExecutableUnavailable);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return NetBirdDaemonCommandResult.Failure(NetBirdDaemonCommandStatus.ExecutableUnavailable);
        }

        var output = new BoundedProcessOutput(command.MaximumOutputBytes);
        var outputTask = PumpAsync(process.StandardOutput.BaseStream, output, capture: true);
        var errorTask = PumpAsync(process.StandardError.BaseStream, output, capture: false);
        var exitTask = process.WaitForExitAsync();
        var timeoutTask = Task.Delay(command.Timeout, cancellationToken);

        var completed = await Task.WhenAny(exitTask, output.LimitReached, output.IoFailed, timeoutTask).ConfigureAwait(false);
        if (completed == output.LimitReached)
        {
            TryKill(process);
            await ObserveStoppedProcessAsync(exitTask, outputTask, errorTask).ConfigureAwait(false);
            return NetBirdDaemonCommandResult.Failure(NetBirdDaemonCommandStatus.OutputTooLarge);
        }
        if (completed == output.IoFailed)
        {
            TryKill(process);
            await ObserveStoppedProcessAsync(exitTask, outputTask, errorTask).ConfigureAwait(false);
            return NetBirdDaemonCommandResult.Failure(NetBirdDaemonCommandStatus.Failed);
        }
        if (completed == timeoutTask)
        {
            TryKill(process);
            await ObserveStoppedProcessAsync(exitTask, outputTask, errorTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return NetBirdDaemonCommandResult.Failure(NetBirdDaemonCommandStatus.TimedOut);
        }

        await ObserveStoppedProcessAsync(exitTask, outputTask, errorTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (output.WasTooLarge)
        {
            return NetBirdDaemonCommandResult.Failure(NetBirdDaemonCommandStatus.OutputTooLarge);
        }
        if (output.HadIoFailure || process.ExitCode != 0)
        {
            return NetBirdDaemonCommandResult.Failure(NetBirdDaemonCommandStatus.Failed);
        }

        return new NetBirdDaemonCommandResult(NetBirdDaemonCommandStatus.Success, output.GetStandardOutput());
    }

    private static async Task PumpAsync(Stream stream, BoundedProcessOutput output, bool capture)
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }
                if (!output.Add(buffer.AsSpan(0, read), capture))
                {
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            output.MarkIoFailure();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    private static async Task ObserveStoppedProcessAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or TimeoutException)
        {
        }
    }

    private sealed class BoundedProcessOutput
    {
        private readonly object _gate = new();
        private readonly int _maximumBytes;
        private readonly MemoryStream _standardOutput = new();
        private readonly TaskCompletionSource _limitReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _ioFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _totalBytes;

        public BoundedProcessOutput(int maximumBytes) => _maximumBytes = maximumBytes;

        public Task LimitReached => _limitReached.Task;
        public Task IoFailed => _ioFailed.Task;
        public bool WasTooLarge { get; private set; }
        public bool HadIoFailure { get; private set; }

        public bool Add(ReadOnlySpan<byte> bytes, bool capture)
        {
            lock (_gate)
            {
                if (WasTooLarge)
                {
                    return false;
                }
                if (bytes.Length > _maximumBytes - _totalBytes)
                {
                    WasTooLarge = true;
                    _limitReached.TrySetResult();
                    return false;
                }

                _totalBytes += bytes.Length;
                if (capture)
                {
                    _standardOutput.Write(bytes);
                }
                return true;
            }
        }

        public void MarkIoFailure()
        {
            lock (_gate)
            {
                HadIoFailure = true;
                _ioFailed.TrySetResult();
            }
        }

        public byte[] GetStandardOutput()
        {
            lock (_gate)
            {
                return _standardOutput.ToArray();
            }
        }
    }
}
