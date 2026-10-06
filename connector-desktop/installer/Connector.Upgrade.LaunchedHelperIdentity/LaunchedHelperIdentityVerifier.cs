using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Connector.Upgrade.LaunchedHelperIdentity;

/// <summary>
/// Checks a connected named-pipe client against the process handle retained by the launcher.
/// Image trust remains a separate, closed gate until a protected path and pinned digest are bound.
/// </summary>
public sealed class LaunchedHelperIdentityVerifier
{
    private readonly ILaunchedHelperNativeApi _native;

    public LaunchedHelperIdentityVerifier() : this(WindowsLaunchedHelperNativeApi.Instance) { }

    internal LaunchedHelperIdentityVerifier(ILaunchedHelperNativeApi native) =>
        _native = native ?? throw new ArgumentNullException(nameof(native));

    /// <summary>
    /// Verifies the connected pipe peer PID against the retained process handle and checks that
    /// process has an elevated token with the built-in Administrators SID enabled.
    /// This does not authorize the helper: image trust is deliberately unverified and closed.
    /// </summary>
    public LaunchedHelperIdentityResult Verify(
        SafePipeHandle connectedServerPipe,
        SafeProcessHandle launchedHelperProcess)
    {
        ArgumentNullException.ThrowIfNull(connectedServerPipe);
        ArgumentNullException.ThrowIfNull(launchedHelperProcess);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Launched-helper identity checks require Windows.");

        var pipeClientMatchesLaunchedProcess = false;
        var elevatedAdministratorToken = false;

        try
        {
            if (!connectedServerPipe.IsClosed && !connectedServerPipe.IsInvalid &&
                !launchedHelperProcess.IsClosed && !launchedHelperProcess.IsInvalid &&
                _native.IsProcessRunning(launchedHelperProcess))
            {
                var pipeClientId = _native.GetPipeClientProcessId(connectedServerPipe);
                var launchedProcessId = _native.GetProcessId(launchedHelperProcess);
                pipeClientMatchesLaunchedProcess = pipeClientId is > 0 &&
                    launchedProcessId is > 0 && pipeClientId == launchedProcessId &&
                    _native.IsProcessRunning(launchedHelperProcess);

                if (pipeClientMatchesLaunchedProcess)
                    elevatedAdministratorToken = _native.IsElevatedAdministrator(launchedHelperProcess) &&
                        _native.IsProcessRunning(launchedHelperProcess);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or SecurityException or
                                           UnauthorizedAccessException or ObjectDisposedException or
                                           InvalidOperationException)
        {
            // Any OS/API failure is a failed identity check.
        }

        return new LaunchedHelperIdentityResult(
            pipeClientMatchesLaunchedProcess,
            elevatedAdministratorToken,
            imageTrustVerified: false);
    }
}

/// <summary>All gates must pass before a caller may authorize a helper operation.</summary>
public sealed class LaunchedHelperIdentityResult
{
    internal LaunchedHelperIdentityResult(
        bool pipeClientMatchesLaunchedProcess,
        bool elevatedAdministratorToken,
        bool imageTrustVerified)
    {
        PipeClientMatchesLaunchedProcess = pipeClientMatchesLaunchedProcess;
        ElevatedAdministratorToken = elevatedAdministratorToken;
        ImageTrustVerified = imageTrustVerified;
    }

    public bool PipeClientMatchesLaunchedProcess { get; }
    public bool ElevatedAdministratorToken { get; }
    public bool ImageTrustVerified { get; }

    // Authorization stays closed until an independently implemented image-trust gate exists.
    public bool IsAuthorized => false;
}

internal interface ILaunchedHelperNativeApi
{
    uint GetPipeClientProcessId(SafePipeHandle pipe);
    uint GetProcessId(SafeProcessHandle process);
    bool IsProcessRunning(SafeProcessHandle process);
    bool IsElevatedAdministrator(SafeProcessHandle process);
}

internal sealed class WindowsLaunchedHelperNativeApi : ILaunchedHelperNativeApi
{
    internal static readonly WindowsLaunchedHelperNativeApi Instance = new();
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationInformation = 20;
    private const int SecurityImpersonation = 2;
    private const int TokenImpersonation = 2;
    private static readonly SecurityIdentifier AdministratorsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private WindowsLaunchedHelperNativeApi() { }

    public uint GetPipeClientProcessId(SafePipeHandle pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe, out var processId))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return processId;
    }

    public uint GetProcessId(SafeProcessHandle process)
    {
        var processId = NativeGetProcessId(process);
        if (processId == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return processId;
    }

    public bool IsProcessRunning(SafeProcessHandle process)
    {
        var waitResult = WaitForSingleObject(process, 0);
        if (waitResult == WaitTimeout)
            return true;
        if (waitResult == WaitObject0)
            return false;
        throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public bool IsElevatedAdministrator(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var primaryToken))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (primaryToken)
        {
            var elevationSize = Marshal.SizeOf<TokenElevation>();
            var elevationBuffer = Marshal.AllocHGlobal(elevationSize);
            try
            {
                if (!GetTokenInformation(primaryToken, TokenElevationInformation,
                        elevationBuffer, (uint)elevationSize, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (Marshal.PtrToStructure<TokenElevation>(elevationBuffer).TokenIsElevated == 0)
                    return false;
            }
            finally
            {
                Marshal.FreeHGlobal(elevationBuffer);
            }

            if (!DuplicateTokenEx(primaryToken, TokenQuery, IntPtr.Zero, SecurityImpersonation,
                    TokenImpersonation, out var impersonationToken))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (impersonationToken)
            {
                var sidBytes = new byte[AdministratorsSid.BinaryLength];
                AdministratorsSid.GetBinaryForm(sidBytes, 0);
                var sidBuffer = Marshal.AllocHGlobal(sidBytes.Length);
                try
                {
                    Marshal.Copy(sidBytes, 0, sidBuffer, sidBytes.Length);
                    if (!CheckTokenMembership(impersonationToken, sidBuffer, out var isMember))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    return isMember;
                }
                finally
                {
                    Marshal.FreeHGlobal(sidBuffer);
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public uint TokenIsElevated;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", EntryPoint = "GetProcessId", SetLastError = true)]
    private static extern uint NativeGetProcessId(SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength,
        out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existingToken,
        uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType,
        out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckTokenMembership(SafeAccessTokenHandle tokenHandle,
        IntPtr sidToCheck, [MarshalAs(UnmanagedType.Bool)] out bool isMember);
}
