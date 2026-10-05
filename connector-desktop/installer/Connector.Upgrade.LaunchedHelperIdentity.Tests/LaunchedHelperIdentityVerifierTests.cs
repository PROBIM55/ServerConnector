using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Connector.Upgrade.LaunchedHelperIdentity.Tests;

public sealed class LaunchedHelperIdentityVerifierTests
{
    [Fact]
    public void ResultCannotBeConstructedOrMutatedByPublicCallers()
    {
        var resultType = typeof(LaunchedHelperIdentityResult);

        Assert.Empty(resultType.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.All(resultType.GetProperties(BindingFlags.Public | BindingFlags.Instance), property =>
            Assert.Null(property.SetMethod));
    }

    [Fact]
    public void IsAuthorizedStaysClosedEvenWhenResultFieldsAreFabricated()
    {
        var result = (LaunchedHelperIdentityResult)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(LaunchedHelperIdentityResult));
        foreach (var fieldName in new[]
                 {
                     "<PipeClientMatchesLaunchedProcess>k__BackingField",
                     "<ElevatedAdministratorToken>k__BackingField",
                     "<ImageTrustVerified>k__BackingField"
                 })
        {
            var field = typeof(LaunchedHelperIdentityResult).GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(result, true);
        }

        Assert.True(result.PipeClientMatchesLaunchedProcess);
        Assert.True(result.ElevatedAdministratorToken);
        Assert.True(result.ImageTrustVerified);
        Assert.False(result.IsAuthorized);
    }

    [Fact]
    public async Task ConnectedLoopbackClientMatchesRetainedCurrentProcessHandle()
    {
        Assert.True(OperatingSystem.IsWindows());
        var pipeName = $"helper-identity-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        // The server handle is created by CreateNamedPipe internally and remains connected for the query.
        var connect = client.ConnectAsync();
        await server.WaitForConnectionAsync();
        await connect;

        using var currentProcess = Process.GetCurrentProcess();
        var verifier = new LaunchedHelperIdentityVerifier();
        var result = verifier.Verify(server.SafePipeHandle, currentProcess.SafeHandle);

        Assert.True(result.PipeClientMatchesLaunchedProcess);
        Assert.False(result.IsAuthorized); // The independent image-trust gate has no binding yet.
        Assert.False(result.ImageTrustVerified);
    }

    [Fact]
    public async Task ConnectedLoopbackClientRejectsUnrelatedRetainedProcessHandle()
    {
        Assert.True(OperatingSystem.IsWindows());
        using var unrelated = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep -Seconds 30")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(unrelated);
        Assert.False(unrelated.HasExited);
        var pipeName = $"helper-identity-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        var connect = client.ConnectAsync();
        await server.WaitForConnectionAsync();
        await connect;

        var result = new LaunchedHelperIdentityVerifier().Verify(server.SafePipeHandle, unrelated.SafeHandle);

        Assert.False(result.PipeClientMatchesLaunchedProcess);
        Assert.False(result.IsAuthorized);
        unrelated.Kill(entireProcessTree: true);
        unrelated.WaitForExit();
    }

    [Fact]
    public async Task NativeVerifierRejectsClosedLaunchedProcessHandle()
    {
        Assert.True(OperatingSystem.IsWindows());
        var pipeName = $"helper-identity-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        var connect = client.ConnectAsync();
        await server.WaitForConnectionAsync();
        await connect;
        using var closedProcess = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);

        var result = new LaunchedHelperIdentityVerifier().Verify(server.SafePipeHandle, closedProcess);

        Assert.False(result.PipeClientMatchesLaunchedProcess);
        Assert.False(result.ElevatedAdministratorToken);
        Assert.False(result.IsAuthorized);
    }

    [Fact]
    public void UnrelatedProcessIdIsRejected()
    {
        var native = new FakeNativeApi { PipeClientId = 40, LaunchedProcessId = 41 };
        using var pipe = new SafePipeHandle(new IntPtr(1), ownsHandle: false);
        using var process = new SafeProcessHandle(new IntPtr(2), ownsHandle: false);

        var result = new LaunchedHelperIdentityVerifier(native).Verify(pipe, process);

        Assert.False(result.PipeClientMatchesLaunchedProcess);
        Assert.False(result.ElevatedAdministratorToken);
        Assert.False(result.IsAuthorized);
        Assert.Equal(0, native.TokenChecks);
    }

    [Fact]
    public void ClosedProcessHandleAndZeroClientIdAreRejected()
    {
        var native = new FakeNativeApi { PipeClientId = 0, LaunchedProcessId = 0 };
        using var pipe = new SafePipeHandle(new IntPtr(1), ownsHandle: false);
        using var closedProcess = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);

        var result = new LaunchedHelperIdentityVerifier(native).Verify(pipe, closedProcess);

        Assert.False(result.PipeClientMatchesLaunchedProcess);
        Assert.False(result.ElevatedAdministratorToken);
        Assert.Equal(0, native.ProcessIdChecks);
    }

    [Fact]
    public void EverySuccessfulIdentityGateStillFailsClosedWithoutImageTrust()
    {
        var native = new FakeNativeApi { PipeClientId = 50, LaunchedProcessId = 50, ElevatedAdmin = true };
        using var pipe = new SafePipeHandle(new IntPtr(1), ownsHandle: false);
        using var process = new SafeProcessHandle(new IntPtr(2), ownsHandle: false);

        var result = new LaunchedHelperIdentityVerifier(native).Verify(pipe, process);

        Assert.True(result.PipeClientMatchesLaunchedProcess);
        Assert.True(result.ElevatedAdministratorToken);
        Assert.False(result.ImageTrustVerified);
        Assert.False(result.IsAuthorized);
    }

    [Fact]
    public void NativeApiFailureFailsClosed()
    {
        var native = new FakeNativeApi { ThrowOnPipeLookup = true };
        using var pipe = new SafePipeHandle(new IntPtr(1), ownsHandle: false);
        using var process = new SafeProcessHandle(new IntPtr(2), ownsHandle: false);

        var result = new LaunchedHelperIdentityVerifier(native).Verify(pipe, process);

        Assert.False(result.PipeClientMatchesLaunchedProcess);
        Assert.False(result.IsAuthorized);
    }

    private sealed class FakeNativeApi : ILaunchedHelperNativeApi
    {
        public uint PipeClientId { get; init; }
        public uint LaunchedProcessId { get; init; }
        public bool ElevatedAdmin { get; init; }
        public bool ThrowOnPipeLookup { get; init; }
        public int TokenChecks { get; private set; }
        public int ProcessIdChecks { get; private set; }
        public uint GetPipeClientProcessId(SafePipeHandle pipe) =>
            ThrowOnPipeLookup ? throw new System.ComponentModel.Win32Exception() : PipeClientId;
        public uint GetProcessId(SafeProcessHandle process) { ProcessIdChecks++; return LaunchedProcessId; }
        public bool IsProcessRunning(SafeProcessHandle process) => true;
        public bool IsElevatedAdministrator(SafeProcessHandle process) { TokenChecks++; return ElevatedAdmin; }
    }
}
