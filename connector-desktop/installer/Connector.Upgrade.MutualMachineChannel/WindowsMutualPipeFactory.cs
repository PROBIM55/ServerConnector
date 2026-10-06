using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Connector.Upgrade.MutualMachineChannel;

internal static class WindowsMutualPipeFactory
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint SecurityDescriptorRevision = 1;

    internal static NamedPipeServerStream Create(string name, SecurityIdentifier userSid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Mutual machine channels require Windows.");
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains('/'))
            throw new ArgumentException("Pipe name must be a local pipe name.", nameof(name));
        MutualMachineChannel.ValidateInitiatingSid(userSid);
        var sddl = $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x0012008B;;;{userSid.Value})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, SecurityDescriptorRevision, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = false
            };
            var handle = CreateNamedPipe(
                @"\\.\pipe\" + name,
                PipeAccessDuplex | FileFlagFirstPipeInstance | FileFlagOverlapped,
                PipeRejectRemoteClients,
                1,
                4096,
                4096,
                0,
                ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally { LocalFree(descriptor); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes securityAttributes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal static class WindowsMutualNative
{
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint TokenQuery = 0x0008;
    private const uint ProcessSynchronize = 0x00100000;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const int TokenElevationInformation = 20;
    private const int SecurityImpersonation = 2;
    private const int TokenImpersonation = 2;
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    internal static uint GetPipeClientPid(SafePipeHandle serverPipe)
    {
        if (!GetNamedPipeClientProcessId(serverPipe, out var pid) || pid == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the connected pipe client PID.");
        return pid;
    }

    internal static SafeProcessHandle OpenCurrentProcessHandle()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Process lifetime checks require Windows.");
        var process = OpenProcess(ProcessSynchronize | ProcessQueryLimitedInformation, false, (uint)Environment.ProcessId);
        if (process.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            process.Dispose();
            throw new Win32Exception(error, "Could not retain the current process handle.");
        }
        return process;
    }

    internal static SecurityIdentifier GetProcessUserSid(SafeProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.IsClosed || process.IsInvalid) throw new SecurityException("Retained current process handle is unavailable.");
        if (!OpenProcessToken(process, TokenQuery, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            GetTokenInformation(token, 1, IntPtr.Zero, 0, out var required);
            if (required == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var buffer = Marshal.AllocHGlobal((int)required);
            try
            {
                if (!GetTokenInformation(token, 1, buffer, required, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var sidPointer = Marshal.ReadIntPtr(buffer);
                if (sidPointer == IntPtr.Zero) throw new SecurityException("Current process token has no user SID.");
                return new SecurityIdentifier(sidPointer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    internal static bool IsSameRunningProcess(uint pipeClientPid, SafeProcessHandle expected)
    {
        if (pipeClientPid == 0 || expected.IsClosed || expected.IsInvalid) return false;
        var wait = WaitForSingleObject(expected, 0);
        if (wait != WaitTimeout) return false;
        var expectedPid = NativeGetProcessId(expected);
        if (expectedPid == 0 || expectedPid != pipeClientPid) return false;
        return WaitForSingleObject(expected, 0) == WaitTimeout;
    }

    internal static bool IsElevatedAdministrator(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var primaryToken)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (primaryToken)
        {
            var size = Marshal.SizeOf<TokenElevation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(primaryToken, TokenElevationInformation, buffer, (uint)size, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (Marshal.PtrToStructure<TokenElevation>(buffer).TokenIsElevated == 0) return false;
            }
            finally { Marshal.FreeHGlobal(buffer); }

            if (!DuplicateTokenEx(primaryToken, TokenQuery, IntPtr.Zero, SecurityImpersonation, TokenImpersonation, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            {
                var sidBytes = new byte[AdministratorsSid.BinaryLength];
                AdministratorsSid.GetBinaryForm(sidBytes, 0);
                var sid = Marshal.AllocHGlobal(sidBytes.Length);
                try
                {
                    Marshal.Copy(sidBytes, 0, sid, sidBytes.Length);
                    if (!CheckTokenMembership(token, sid, out var isMember)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    return isMember;
                }
                finally { Marshal.FreeHGlobal(sid); }
            }
        }
    }

    internal static void AssertImpersonatedSid(SafePipeHandle serverPipe, SecurityIdentifier expectedSid)
    {
        var impersonated = false;
        try
        {
            if (!ImpersonateNamedPipeClient(serverPipe)) throw new Win32Exception(Marshal.GetLastWin32Error());
            impersonated = true;
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            AssertClientTokenIdentity(identity.User, identity.ImpersonationLevel, identity.Groups, expectedSid);
        }
        finally
        {
            if (impersonated && !RevertToSelf())
            {
                const string message = "RevertToSelf failed after pipe-client impersonation; process termination is required.";
                Environment.FailFast(message);
                throw new InvalidOperationException(message);
            }
        }
    }

    internal static void AssertClientTokenIdentity(
        SecurityIdentifier? userSid,
        TokenImpersonationLevel impersonationLevel,
        IEnumerable<IdentityReference>? groups,
        SecurityIdentifier expectedSid)
    {
        if (impersonationLevel == TokenImpersonationLevel.Anonymous || userSid is null || !userSid.Equals(expectedSid))
            throw new SecurityException("B pipe client token does not match the original caller SID.");

        var anonymousSid = new SecurityIdentifier(WellKnownSidType.AnonymousSid, null).Value;
        var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null).Value;
        var guestsSid = new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null).Value;
        if (userSid.Value == anonymousSid || IsGuestSid(userSid) ||
            groups?.Any(group => group is not null &&
                (group.Value == networkSid || group.Value == guestsSid)) == true)
            throw new SecurityException("Anonymous, network, and guest pipe clients are rejected.");
    }

    private static bool IsGuestSid(SecurityIdentifier sid) =>
        sid.Value?.EndsWith("-501", StringComparison.Ordinal) == true;

    internal static async ValueTask WaitForProcessExitAsync(SafeProcessHandle process, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Process lifetime checks require Windows.");
        if (process.IsClosed || process.IsInvalid) throw new SecurityException("Retained peer process handle is unavailable.");
        using var waitHandle = new ProcessWaitHandle(process);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        var registration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            completion,
            Timeout.Infinite,
            executeOnlyOnce: true);
        try { await completion.Task.ConfigureAwait(false); }
        finally { registration.Unregister(null); }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        private readonly SafeProcessHandle _process;
        private bool _referenceAdded;

        internal ProcessWaitHandle(SafeProcessHandle process)
        {
            _process = process;
            process.DangerousAddRef(ref _referenceAdded);
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (_referenceAdded)
            {
                _referenceAdded = false;
                _process.DangerousRelease();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct TokenElevation { public uint TokenIsElevated; }
    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "GetProcessId", SetLastError = true)] private static extern uint NativeGetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr buffer, uint length, out uint returned);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, IntPtr attributes, int impersonation, int type, out SafeAccessTokenHandle duplicate);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckTokenMembership(SafeAccessTokenHandle token, IntPtr sid, [MarshalAs(UnmanagedType.Bool)] out bool isMember);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImpersonateNamedPipeClient(SafePipeHandle pipe);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RevertToSelf();
}
