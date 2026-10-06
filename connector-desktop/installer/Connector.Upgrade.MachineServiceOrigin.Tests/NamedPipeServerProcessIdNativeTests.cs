using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Connector.Upgrade.MachineServiceOrigin.Tests;

public sealed class NamedPipeServerProcessIdNativeTests
{
    [Fact]
    public async Task GetNamedPipeServerProcessId_on_client_handle_reports_loopback_server_process()
    {
        var pipeName = "MachineServiceOrigin-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var waitForConnection = server.WaitForConnectionAsync();
        await client.ConnectAsync();
        await waitForConnection;

        var succeeded = GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverProcessId);

        Assert.True(succeeded, $"GetNamedPipeServerProcessId(client handle) failed with Win32 error {Marshal.GetLastWin32Error()}.");
        Assert.Equal((uint)Environment.ProcessId, serverProcessId);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
