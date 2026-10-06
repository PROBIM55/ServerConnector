using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;

namespace Connector.Upgrade.MachinePipe;

/// <summary>Creates a one-client, local-only machine pipe bound to one initiating user SID.</summary>
public static class MachinePipeIdentity
{
    private const string PipePrefix = "Structura.Connector.Upgrade.MachinePipe";
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadModeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;
    private const uint SecurityDescriptorRevision = 1;
    private static readonly Regex SidPattern = new("^S-1-(?:[0-9]+-){1,14}[0-9]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The exact CreateNamedPipe open and mode flags, exposed for policy verification.</summary>
    internal const uint RequiredPipeOpenMode = PipeAccessDuplex | FileFlagFirstPipeInstance | FileFlagOverlapped;
    internal const uint RequiredPipeMode = PipeTypeByte | PipeReadModeByte | PipeWait | PipeRejectRemoteClients;
    internal const uint RequiredMaxInstances = 1;

    /// <summary>Builds a deterministic name from the operation and a validated account SID.</summary>
    public static string GetPipeName(Guid operationId, SecurityIdentifier initiatingSid)
    {
        ArgumentNullException.ThrowIfNull(initiatingSid);
        if (operationId == Guid.Empty)
            throw new ArgumentException("Operation id must not be empty.", nameof(operationId));
        ValidateInitiatingSid(initiatingSid);
        var sidHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(initiatingSid.Value!)))[..24];
        return $"{PipePrefix}.{operationId:N}.{sidHash}";
    }

    /// <summary>Creates a pipe with SYSTEM, Administrators, and the initiating SID as its only DACL principals.</summary>
    public static NamedPipeServerStream CreateServer(Guid operationId, SecurityIdentifier initiatingSid)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Machine named pipes require Windows.");
        var name = GetPipeName(operationId, initiatingSid);
        var sddl = BuildSecurityDescriptorSddl(initiatingSid);
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
                RequiredPipeOpenMode,
                RequiredPipeMode,
                RequiredMaxInstances,
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
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>
    /// Authenticates the connected peer using the kernel pipe token after at least one client byte has been read.
    /// Reverts thread impersonation in every path.
    /// </summary>
    public static void AssertConnectedClient(NamedPipeServerStream server, SecurityIdentifier initiatingSid)
    {
        AssertConnectedClient(server, initiatingSid, ImpersonateNamedPipeClient, RevertToSelf, Environment.FailFast);
    }

    internal static void AssertConnectedClient(
        NamedPipeServerStream server,
        SecurityIdentifier initiatingSid,
        Func<SafePipeHandle, bool> impersonate,
        Func<bool> revert,
        Action<string> failFast)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(initiatingSid);
        ArgumentNullException.ThrowIfNull(impersonate);
        ArgumentNullException.ThrowIfNull(revert);
        ArgumentNullException.ThrowIfNull(failFast);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Pipe client impersonation requires Windows.");
        ValidateInitiatingSid(initiatingSid);
        if (!server.IsConnected)
            throw new InvalidOperationException("Pipe has no connected client.");

        var impersonated = false;
        try
        {
            if (!impersonate(server.SafePipeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            impersonated = true;
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            AssertClientTokenIdentity(identity.User, identity.ImpersonationLevel, identity.Groups, initiatingSid);
        }
        finally
        {
            if (impersonated && !revert())
            {
                const string message = "RevertToSelf failed while a pipe-client token was impersonated; process termination is required.";
                failFast(message);
                throw new InvalidOperationException(message);
            }
        }
    }

    internal static void AssertClientTokenIdentity(
        SecurityIdentifier? userSid,
        TokenImpersonationLevel impersonationLevel,
        IEnumerable<IdentityReference>? groups,
        SecurityIdentifier initiatingSid)
    {
        if (impersonationLevel == TokenImpersonationLevel.Anonymous || userSid is null || !userSid.Equals(initiatingSid))
            throw new SecurityException("Pipe client token does not match the initiating SID.");

        var userValue = userSid.Value;
        if (userValue == WellKnownSidValue(WellKnownSidType.AnonymousSid) || IsGuestSid(userSid) ||
            groups?.Any(group => group is not null &&
                (group.Value == WellKnownSidValue(WellKnownSidType.NetworkSid) ||
                 group.Value == WellKnownSidValue(WellKnownSidType.BuiltinGuestsSid))) == true)
            throw new SecurityException("Anonymous, network, and guest pipe clients are rejected.");
    }

    /// <summary>
    /// Fails closed until an authenticated bootstrap binds the expected server publisher/service identity.
    /// A pipe name, local account membership, and ACL alone do not prove server origin.
    /// </summary>
    public static NamedPipeClientStream ConnectTrustedClient(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        throw new SecurityException("Trusted machine-pipe server origin is not bound to a verified publisher or bootstrap identity.");
    }

    internal static string BuildSecurityDescriptorSddl(SecurityIdentifier initiatingSid)
    {
        ArgumentNullException.ThrowIfNull(initiatingSid);
        ValidateInitiatingSid(initiatingSid);
        // Avoid generic write: on named pipes FILE_APPEND_DATA aliases FILE_CREATE_PIPE_INSTANCE.
        return $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x0012008B;;;{initiatingSid.Value})";
    }

    private static void ValidateInitiatingSid(SecurityIdentifier sid)
    {
        var value = sid.Value;
        if (string.IsNullOrEmpty(value) || !SidPattern.IsMatch(value) ||
            sid.IsWellKnown(WellKnownSidType.WorldSid) ||
            sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid) ||
            sid.IsWellKnown(WellKnownSidType.AnonymousSid) ||
            sid.IsWellKnown(WellKnownSidType.NetworkSid) ||
            sid.IsWellKnown(WellKnownSidType.BuiltinGuestsSid) ||
            value.StartsWith("S-1-5-80-", StringComparison.Ordinal) ||
            value.StartsWith("S-1-5-82-", StringComparison.Ordinal) ||
            !IsUserAccountSid(sid))
            throw new ArgumentException("Initiating SID must be a specific, non-anonymous account SID.", nameof(sid));
    }

    private static bool IsUserAccountSid(SecurityIdentifier sid)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Account SID validation requires Windows.");
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);
        var name = new StringBuilder(1024);
        var domain = new StringBuilder(1024);
        uint nameLength = (uint)name.Capacity;
        uint domainLength = (uint)domain.Capacity;
        return LookupAccountSid(IntPtr.Zero, sidBytes, name, ref nameLength, domain, ref domainLength, out var sidNameUse) &&
            sidNameUse == SidTypeUser;
    }

    private static bool IsGuestSid(SecurityIdentifier sid) =>
        sid.Value?.EndsWith("-501", StringComparison.Ordinal) == true;

    private const int SidTypeUser = 1;

    private static string WellKnownSidValue(WellKnownSidType type)
    {
        var sid = new SecurityIdentifier(type, null);
        return sid.Value!;
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
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string stringSecurityDescriptor, uint revision, out IntPtr securityDescriptor, out uint securityDescriptorSize);

    [DllImport("advapi32.dll", EntryPoint = "LookupAccountSidW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountSid(IntPtr systemName, byte[] sid, StringBuilder name, ref uint nameLength, StringBuilder referencedDomainName, ref uint referencedDomainNameLength, out int sidNameUse);

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes securityAttributes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImpersonateNamedPipeClient(SafePipeHandle pipe);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();
}
