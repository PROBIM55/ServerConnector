using System.IO.Pipes;
using System.Security;
using System.Security.Principal;
using System.Runtime.InteropServices;
using Connector.Upgrade.MachinePipe;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Connector.Upgrade.MachinePipe.Tests;

public sealed class MachinePipeIdentityTests
{
    [Fact]
    public void Pipe_open_mode_rejects_remote_clients_and_allows_only_first_instance()
    {
        Assert.NotEqual(0u, MachinePipeIdentity.RequiredPipeMode & 0x00000008u); // PIPE_REJECT_REMOTE_CLIENTS
        Assert.NotEqual(0u, MachinePipeIdentity.RequiredPipeOpenMode & 0x00080000u); // FILE_FLAG_FIRST_PIPE_INSTANCE
        Assert.NotEqual(0u, MachinePipeIdentity.RequiredPipeOpenMode & 0x40000000u); // FILE_FLAG_OVERLAPPED
        Assert.Equal(1u, MachinePipeIdentity.RequiredMaxInstances);
    }

    [Fact]
    public void Dacl_contains_only_system_admins_and_exact_initiating_sid()
    {
        var sid = CurrentSid();
        var sddl = MachinePipeIdentity.BuildSecurityDescriptorSddl(sid);

        Assert.Equal($"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x0012008B;;;{sid.Value})", sddl);
        Assert.DoesNotContain("WD", sddl, StringComparison.Ordinal); // Everyone
        Assert.DoesNotContain("AU", sddl, StringComparison.Ordinal); // Authenticated Users
        Assert.DoesNotContain("AN", sddl, StringComparison.Ordinal); // Anonymous
    }

    [Theory]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-11")]
    [InlineData("S-1-5-7")]
    [InlineData("S-1-5-2")]
    [InlineData("S-1-5-32-555")]
    [InlineData("S-1-5-80-0-0-0-0-1")]
    public void Broad_or_special_sids_cannot_be_initiators(string value)
    {
        Assert.Throws<ArgumentException>(() => MachinePipeIdentity.GetPipeName(Guid.NewGuid(), new SecurityIdentifier(value)));
    }

    [Fact]
    public void Pipe_name_is_deterministic_and_bound_to_operation_and_sid()
    {
        var sid = CurrentSid();
        var operation = Guid.NewGuid();
        var name = MachinePipeIdentity.GetPipeName(operation, sid);

        Assert.Equal(name, MachinePipeIdentity.GetPipeName(operation, sid));
        Assert.Contains(operation.ToString("N"), name, StringComparison.Ordinal);
        Assert.NotEqual(name, MachinePipeIdentity.GetPipeName(Guid.NewGuid(), sid));
    }

    [Fact]
    public void Trusted_client_connect_is_fail_closed_until_server_origin_is_bound()
    {
        var error = Assert.Throws<SecurityException>(() => MachinePipeIdentity.ConnectTrustedClient("arbitrary-name"));
        Assert.Contains("publisher or bootstrap", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Kernel_token_identity_check_rejects_a_different_account_sid()
    {
        var initiatingSid = CurrentSid();
        var otherSid = new SecurityIdentifier("S-1-5-21-111-222-333-1001");

        Assert.Throws<SecurityException>(() => MachinePipeIdentity.AssertClientTokenIdentity(
            otherSid,
            TokenImpersonationLevel.Impersonation,
            Array.Empty<IdentityReference>(),
            initiatingSid));
    }

    [Fact]
    public async Task Native_loopback_authenticates_kernel_client_token_and_reverts()
    {
        EnsureWindows();
        var sid = CurrentSid();
        var operation = Guid.NewGuid();
        using var server = MachinePipeIdentity.CreateServer(operation, sid);
        var accept = server.WaitForConnectionAsync();
        using var client = OpenPipeClient(MachinePipeIdentity.GetPipeName(operation, sid));
        await accept.WaitAsync(TimeSpan.FromSeconds(5));
        client.WriteByte(0x7f);
        Assert.Equal(0x7f, server.ReadByte());

        MachinePipeIdentity.AssertConnectedClient(server, sid);
        using var after = WindowsIdentity.GetCurrent();
        Assert.Equal(sid, after.User);

        string? failFastMessage = null;
        var failFastBranch = Assert.Throws<InvalidOperationException>(() => MachinePipeIdentity.AssertConnectedClient(
            server,
            sid,
            _ => true,
            () => false,
            message => failFastMessage = message));
        Assert.Contains("process termination is required", failFastBranch.Message, StringComparison.Ordinal);
        Assert.Equal(failFastBranch.Message, failFastMessage);
    }

    [Fact]
    public async Task Native_pipe_read_cancels_when_connected_client_sends_no_bytes()
    {
        EnsureWindows();
        var sid = CurrentSid();
        var operation = Guid.NewGuid();
        using var server = MachinePipeIdentity.CreateServer(operation, sid);
        var accept = server.WaitForConnectionAsync();
        using var client = OpenPipeClient(MachinePipeIdentity.GetPipeName(operation, sid));
        await accept.WaitAsync(TimeSpan.FromSeconds(5));

        using var cancellation = new CancellationTokenSource();
        var read = server.ReadAsync(new byte[1], cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await read.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Native_pipe_allows_client_data_io_but_denies_user_creating_another_instance()
    {
        EnsureWindows();
        var sid = CurrentSid();
        var operation = Guid.NewGuid();
        var pipeName = MachinePipeIdentity.GetPipeName(operation, sid);
        using var server = MachinePipeIdentity.CreateServer(operation, sid);
        using var client = OpenPipeClient(pipeName);

        var denied = CreateNamedPipe(
            @"\\.\pipe\" + pipeName,
            0x00000003 | 0x40000000, // PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED; no first-instance flag
            0x00000008, // PIPE_REJECT_REMOTE_CLIENTS
            1,
            4096,
            4096,
            0,
            IntPtr.Zero);

        Assert.True(denied.IsInvalid);
        Assert.Equal(5, Marshal.GetLastWin32Error()); // ERROR_ACCESS_DENIED
    }

    private static NamedPipeClientStream OpenPipeClient(string pipeName)
    {
        var handle = CreateFile(
            @"\\.\pipe\" + pipeName,
            0x00000001 | 0x00000002 | 0x00100000, // FILE_READ_DATA | FILE_WRITE_DATA | SYNCHRONIZE
            0,
            IntPtr.Zero,
            3, // OPEN_EXISTING
            0x40000000, // FILE_FLAG_OVERLAPPED
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new System.ComponentModel.Win32Exception(error);
        }

        return new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outBufferSize,
        uint inBufferSize,
        uint defaultTimeout,
        IntPtr securityAttributes);

    private static SecurityIdentifier CurrentSid()
    {
        EnsureWindows();
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("Current process has no user SID.");
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("These native pipe tests require Windows.");
    }
}
