using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Connector.Network;
using Microsoft.Win32.SafeHandles;

namespace Connector.SmbAccess;

public interface IWindowsSmbMappingPort
{
    event Action? NetworkChanged;
    ValueTask<NetworkOverlaySnapshot> GetLiveOverlayAsync(CancellationToken cancellationToken);
    ValueTask<string?> GetTargetAsync(string drive, CancellationToken cancellationToken);
    ValueTask<bool> IsRouteAllowedAsync(IPAddress destination,
        IReadOnlyList<IPAddress> expectedOverlayAddresses, CancellationToken cancellationToken);
    ValueTask<bool> IsShareRouteAllowedAsync(string shareUnc, IPAddress destination,
        IReadOnlyList<IPAddress> expectedOverlayAddresses, CancellationToken cancellationToken) =>
        IsRouteAllowedAsync(destination, expectedOverlayAddresses, cancellationToken);
    ValueTask<bool> IsMappingSecurityAllowedAsync(string drive, string shareUnc, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);
    ValueTask MountAsync(string drive, string shareUnc, string userName, string password,
        IPAddress destination, IReadOnlyList<IPAddress> expectedOverlayAddresses, CancellationToken cancellationToken);
    ValueTask UnmountAsync(string drive, CancellationToken cancellationToken);
    ValueTask<SmbExactMappingRemoval> TryUnmountExactAsync(
        string drive, string expectedShareUnc, CancellationToken cancellationToken);
}

public enum SmbExactMappingRemoval
{
    Removed,
    Missing,
    TargetChanged,
}

internal interface IWindowsSmbNativeApi
{
    string? GetTarget(string drive);
    IPAddress GetBestRouteSource(IPAddress destination);
    int AddConnection(string drive, string shareUnc, string userName, string password);
    int AddEncryptedConnection(string drive, string shareUnc, string userName, string password) =>
        throw new PlatformNotSupportedException("Required SMB encryption is unavailable.");
    bool HasEncryptedIntegrity(string drive, string expectedShareUnc) => false;
    int CancelConnection(string drive);
}

internal interface IWindowsNetworkChangeMonitor : IDisposable
{
    event Action? Changed;
}

internal sealed class SmbRouteUnavailableException : InvalidOperationException
{
    public SmbRouteUnavailableException()
        : base("The OS-selected SMB route does not use the authorized overlay source.") { }
}

internal sealed class SmbMappingCleanupException : Exception
{
    public SmbMappingCleanupException(Exception primaryFailure, Exception cleanupFailure)
        : base("The SMB mapping failed and rollback could not be confirmed.", primaryFailure) =>
        CleanupFailure = cleanupFailure;

    public Exception CleanupFailure { get; }
}

/// <summary>
/// Creates temporary Windows drive mappings after the OS-selected route has
/// chosen one of the currently authorized overlay source addresses. The WNet
/// API cannot bind the SMB redirector socket; therefore the route is checked
/// immediately before and after mapping and again after Windows network-change
/// notifications. This is not a socket-binding proof: server-side SMB firewall
/// and listen-only-on-overlay enforcement remain mandatory.
/// </summary>
public sealed class WindowsSmbMappingPort : IWindowsSmbMappingPort, IDisposable
{
    private const int NoError = 0;
    private readonly IWindowsSmbNativeApi _native;
    private readonly IWindowsNetworkChangeMonitor? _networkChanges;
    private readonly Func<CancellationToken, ValueTask<NetworkOverlaySnapshot>>? _overlayLiveness;
    private readonly IReadOnlyList<string> _legacyEncryptedShares;
    private readonly Dictionary<string, string> _encryptedMappings = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public WindowsSmbMappingPort(Func<CancellationToken, ValueTask<NetworkOverlaySnapshot>> overlayLiveness,
        IEnumerable<string>? legacyEncryptedShares = null)
        : this(new WindowsSmbNativeApi(), new WindowsNetworkChangeMonitor(), overlayLiveness, legacyEncryptedShares) { }

    internal WindowsSmbMappingPort(
        IWindowsSmbNativeApi native,
        IWindowsNetworkChangeMonitor? networkChanges = null,
        Func<CancellationToken, ValueTask<NetworkOverlaySnapshot>>? overlayLiveness = null,
        IEnumerable<string>? legacyEncryptedShares = null)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _networkChanges = networkChanges;
        _overlayLiveness = overlayLiveness;
        _legacyEncryptedShares = LegacyEncryptedSmbPolicy.Validate(legacyEncryptedShares ?? Array.Empty<string>());
        if (_networkChanges is not null) _networkChanges.Changed += OnNetworkChanged;
    }

    public event Action? NetworkChanged;

    public ValueTask<NetworkOverlaySnapshot> GetLiveOverlayAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _overlayLiveness is null
            ? ValueTask.FromException<NetworkOverlaySnapshot>(new InvalidOperationException(
                "An active managed-overlay liveness probe is required for SMB access."))
            : _overlayLiveness(cancellationToken);
    }

    public ValueTask<string?> GetTargetAsync(string drive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = _native.GetTarget(drive);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(target);
    }

    public ValueTask<bool> IsRouteAllowedAsync(IPAddress destination,
        IReadOnlyList<IPAddress> expectedOverlayAddresses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedOverlayAddresses);
        cancellationToken.ThrowIfCancellationRequested();
        var allowed = HasOverlayRoute(destination, expectedOverlayAddresses);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(allowed);
    }

    public ValueTask<bool> IsShareRouteAllowedAsync(string shareUnc, IPAddress destination,
        IReadOnlyList<IPAddress> expectedOverlayAddresses, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLegacyEncrypted(shareUnc))
            return IsRouteAllowedAsync(destination, expectedOverlayAddresses, cancellationToken);
        // The exact legacy share uses mandatory SMB3 crypto. Overlay identity is
        // still checked by admission; this does not claim an overlay-bound socket.
        return ValueTask.FromResult(destination.Equals(IPAddress.Parse(LegacyEncryptedSmbPolicy.Destination)) &&
            expectedOverlayAddresses.Any(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
    }

    public ValueTask<bool> IsMappingSecurityAllowedAsync(string drive, string shareUnc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLegacyEncrypted(shareUnc)) return ValueTask.FromResult(true);
        // Never adopt an earlier-process or foreign mapping on UNC equality.
        var allowed = _encryptedMappings.TryGetValue(drive, out var created) && SameTarget(created, shareUnc) &&
            SameTarget(_native.GetTarget(drive), shareUnc) && _native.HasEncryptedIntegrity(drive, shareUnc) &&
            SameTarget(_native.GetTarget(drive), shareUnc);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(allowed);
    }

    public ValueTask MountAsync(string drive, string shareUnc, string userName, string password,
        IPAddress destination, IReadOnlyList<IPAddress> expectedOverlayAddresses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedOverlayAddresses);
        cancellationToken.ThrowIfCancellationRequested();
        var encrypted = IsLegacyEncrypted(shareUnc);
        if (encrypted)
        {
            if (!destination.Equals(IPAddress.Parse(LegacyEncryptedSmbPolicy.Destination)) || expectedOverlayAddresses.Count == 0)
                throw new SmbRouteUnavailableException();
        }
        else EnsureOverlayRoute(destination, expectedOverlayAddresses);
        if (_native.GetTarget(drive) is not null)
            throw new InvalidOperationException("The SMB drive is already mapped.");

        _encryptedMappings.Remove(drive);
        var result = encrypted ? _native.AddEncryptedConnection(drive, shareUnc, userName, password)
            : _native.AddConnection(drive, shareUnc, userName, password);
        if (result != NoError)
            throw new Win32Exception(result, "Windows refused the temporary SMB drive mapping.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SameTarget(_native.GetTarget(drive), shareUnc))
                throw new InvalidOperationException("Windows did not confirm the requested SMB drive target.");
            if (encrypted)
            {
                if (!_native.HasEncryptedIntegrity(drive, shareUnc) || !SameTarget(_native.GetTarget(drive), shareUnc))
                    throw new InvalidOperationException("Windows did not confirm encrypted and integrity-protected SMB.");
                cancellationToken.ThrowIfCancellationRequested();
                _encryptedMappings[drive] = shareUnc;
            }
            else EnsureOverlayRoute(destination, expectedOverlayAddresses);
            return ValueTask.CompletedTask;
        }
        catch (Exception primaryFailure)
        {
            var cleanupFailure = TryRollbackExactMapping(drive, shareUnc);
            if (cleanupFailure is not null)
                throw new SmbMappingCleanupException(primaryFailure, cleanupFailure);
            throw;
        }
    }

    public ValueTask UnmountAsync(string drive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _native.CancelConnection(drive);
        cancellationToken.ThrowIfCancellationRequested();
        if (result != NoError)
            throw new Win32Exception(result, "Windows refused to remove the SMB drive mapping.");
        if (_native.GetTarget(drive) is not null)
            throw new InvalidOperationException("Windows still reports the SMB drive mapping after removal.");
        _encryptedMappings.Remove(drive);
        return ValueTask.CompletedTask;
    }

    public ValueTask<SmbExactMappingRemoval> TryUnmountExactAsync(
        string drive, string expectedShareUnc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = _native.GetTarget(drive);
        if (current is null) { _encryptedMappings.Remove(drive); return ValueTask.FromResult(SmbExactMappingRemoval.Missing); }
        if (!SameTarget(current, expectedShareUnc))
            return ValueTask.FromResult(SmbExactMappingRemoval.TargetChanged);

        cancellationToken.ThrowIfCancellationRequested();
        var result = _native.CancelConnection(drive); // WNet force=false: never close open handles.
        cancellationToken.ThrowIfCancellationRequested();
        if (result != NoError)
            throw new Win32Exception(result, "Windows refused to remove the exact SMB drive mapping.");

        current = _native.GetTarget(drive);
        if (current is null) { _encryptedMappings.Remove(drive); return ValueTask.FromResult(SmbExactMappingRemoval.Removed); }
        if (!SameTarget(current, expectedShareUnc))
            return ValueTask.FromResult(SmbExactMappingRemoval.TargetChanged);
        throw new InvalidOperationException("Windows still reports the exact SMB drive mapping after removal.");
    }

    private Exception? TryRollbackExactMapping(string drive, string shareUnc)
    {
        _encryptedMappings.Remove(drive);
        try
        {
            var current = _native.GetTarget(drive);
            if (current is null || !SameTarget(current, shareUnc))
                return null;
            var result = _native.CancelConnection(drive);
            if (result != NoError)
                return new Win32Exception(result, "Windows refused to roll back the failed SMB mapping.");
            current = _native.GetTarget(drive);
            return current is null || !SameTarget(current, shareUnc)
                ? null
                : new InvalidOperationException("Windows still reports the failed SMB mapping after rollback.");
        }
        catch (Exception cleanupFailure)
        {
            return cleanupFailure;
        }
    }

    private void EnsureOverlayRoute(IPAddress destination, IReadOnlyList<IPAddress> expectedOverlayAddresses)
    {
        if (!HasOverlayRoute(destination, expectedOverlayAddresses))
            throw new SmbRouteUnavailableException();
    }

    private bool HasOverlayRoute(IPAddress destination, IReadOnlyList<IPAddress> expectedOverlayAddresses)
    {
        if (destination.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException("The SMB destination must be IPv4.");
        var expected = expectedOverlayAddresses
            .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .ToHashSet();
        if (expected.Count == 0)
            throw new InvalidOperationException("No authorized overlay source address is available.");
        return expected.Contains(_native.GetBestRouteSource(destination));
    }

    private void OnNetworkChanged()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var handlers = NetworkChanged;
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch { }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_networkChanges is null) return;
        _networkChanges.Changed -= OnNetworkChanged;
        _networkChanges.Dispose();
    }

    private static bool SameTarget(string? left, string right) =>
        string.Equals(left?.TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private bool IsLegacyEncrypted(string shareUnc) => _legacyEncryptedShares.Any(share =>
        string.Equals(share, shareUnc, StringComparison.OrdinalIgnoreCase));
}

internal sealed class WindowsNetworkChangeMonitor : IWindowsNetworkChangeMonitor
{
    private int _disposed;

    public WindowsNetworkChangeMonitor()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public event Action? Changed;

    private void OnNetworkAddressChanged(object? sender, EventArgs args) => RaiseChanged();
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => RaiseChanged();

    private void RaiseChanged()
    {
        if (Volatile.Read(ref _disposed) == 0) Changed?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }
}

internal sealed class WindowsSmbNativeApi : IWindowsSmbNativeApi
{
    private const int NoError = 0;
    private const int ErrorMoreData = 234;
    private const int ErrorConnectionUnavailable = 1201;
    private const int ErrorNotConnected = 2250;
    private const uint ResourceTypeDisk = 1;
    private const uint ConnectTemporary = 0x00000004;
    private const ushort AddressFamilyInet = 2;
    private const int BestRouteBufferBytes = 256;

    public string? GetTarget(string drive)
    {
        var length = 1024;
        var target = new StringBuilder(length);
        var result = WNetGetConnection(drive, target, ref length);
        if (result == ErrorMoreData && length is > 0 and <= 32768)
        {
            target = new StringBuilder(length);
            result = WNetGetConnection(drive, target, ref length);
        }
        return result switch
        {
            NoError or ErrorConnectionUnavailable => target.ToString(),
            ErrorNotConnected => IsLogicalDrivePresent(drive) ? string.Empty : null,
            _ => throw new Win32Exception(result, "Windows could not read the SMB drive mapping.")
        };
    }

    public int AddConnection(string drive, string shareUnc, string userName, string password)
    {
        var resource = new NetResource
        {
            Type = ResourceTypeDisk,
            LocalName = drive,
            RemoteName = shareUnc,
        };
        return WNetAddConnection2(ref resource, password, userName, ConnectTemporary);
    }

    public int AddEncryptedConnection(string drive, string shareUnc, string userName, string password)
    {
        // USE_INFO_3: integrity=0x8, privacy=0x10. No global or persistent mapping.
        var secret = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var separator = userName.IndexOf('\\');
            var info = new UseInfo3
            {
                Info = new UseInfo2 { Local = drive, Remote = shareUnc, Password = secret, AssignmentType = 0,
                    UserName = separator < 0 ? userName : userName[(separator + 1)..],
                    DomainName = separator < 0 ? null : userName[..separator] },
                Flags = 0x18
            };
            return NetUseAdd(null, 3, ref info, out _);
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(secret); }
    }

    public bool HasEncryptedIntegrity(string drive, string expectedShareUnc) =>
        ReadEncryptedIntegrity(drive, expectedShareUnc, allowLoopback: false);

    internal bool ReadEncryptedIntegrity(string drive, string expectedShareUnc, bool allowLoopback)
    {
        // Query the actual mapped SMB handle, not server capability flags.
        using var handle = CreateFile(drive + @"\", 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify the SMB mapping security.");
        var finalPath = new StringBuilder(32768);
        var finalLength = GetFinalPathNameByHandle(handle, finalPath, (uint)finalPath.Capacity, 0);
        if (finalLength == 0 || finalLength >= finalPath.Capacity ||
            !string.Equals(finalPath.ToString().TrimEnd('\\'), @"\\?\UNC\" + expectedShareUnc[2..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The SMB handle does not resolve to the authorized share.");
        var info = new RemoteProtocolInfo { StructureVersion = 2, StructureSize = 116 };
        if (!GetFileInformationByHandleEx(handle, 13, ref info, 116))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the SMB mapping security.");
        // FILE_REMOTE_PROTOCOL_INFO has privacy=0x8, integrity=0x10 (unlike USE_INFO_3).
        return info.StructureVersion >= 2 && info.StructureSize >= 20 && info.Protocol == 0x20000 &&
            info.MajorVersion == 3 && (info.Flags & 2) == 0 && (allowLoopback || (info.Flags & 1) == 0) &&
            (info.Flags & 0x8) == 0x8; // SMB3 encryption authenticates every message (AEAD).
    }

    public int CancelConnection(string drive) => WNetCancelConnection2(drive, 0, force: false);

    public IPAddress GetBestRouteSource(IPAddress destination)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows SMB mapping requires Windows.");
        var destinationSocketAddress = SockaddrInet.From(destination);
        var result = GetBestInterfaceEx(ref destinationSocketAddress, out var interfaceIndex);
        if (result != NoError || interfaceIndex == 0)
            throw new Win32Exception(result, "Windows could not select an interface for the SMB destination.");

        var routeBuffer = Marshal.AllocHGlobal(BestRouteBufferBytes);
        try
        {
            Marshal.Copy(new byte[BestRouteBufferBytes], 0, routeBuffer, BestRouteBufferBytes);
            result = GetBestRoute2(IntPtr.Zero, interfaceIndex, IntPtr.Zero,
                ref destinationSocketAddress, 0, routeBuffer, out var bestSourceAddress);
            if (result != NoError)
                throw new Win32Exception(result, "Windows could not select a route for the SMB destination.");
            return bestSourceAddress.ToIPAddress();
        }
        finally
        {
            Marshal.FreeHGlobal(routeBuffer);
        }
    }

    private static bool IsLogicalDrivePresent(string drive) => Environment.GetLogicalDrives()
        .Any(root => root.StartsWith(drive, StringComparison.OrdinalIgnoreCase));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UseInfo2
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Local;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Remote;
        public IntPtr Password;
        public uint Status, AssignmentType, ReferenceCount, UseCount;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DomainName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UseInfo3 { public UseInfo2 Info; public uint Flags; }

    [StructLayout(LayoutKind.Explicit, Size = 116)]
    private struct RemoteProtocolInfo
    {
        [FieldOffset(0)] public ushort StructureVersion;
        [FieldOffset(2)] public ushort StructureSize;
        [FieldOffset(4)] public uint Protocol;
        [FieldOffset(8)] public ushort MajorVersion;
        [FieldOffset(16)] public uint Flags;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUseAdd(string? serverName, uint level, ref UseInfo3 info, out uint parameterError);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint shareMode, IntPtr security,
        uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        ref RemoteProtocolInfo info, uint size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public uint Scope;
        public uint Type;
        public uint DisplayType;
        public uint Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private struct SockaddrInet
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(2)] public ushort Port;
        [FieldOffset(4)] public uint IPv4Address;

        public static SockaddrInet From(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            if (bytes.Length != 4) throw new ArgumentException("Only IPv4 is supported.", nameof(address));
            return new SockaddrInet
            {
                Family = AddressFamilyInet,
                IPv4Address = BitConverter.ToUInt32(bytes, 0),
            };
        }

        public readonly IPAddress ToIPAddress()
        {
            if (Family != AddressFamilyInet)
                throw new InvalidOperationException("Windows selected a non-IPv4 SMB source address.");
            return new IPAddress(BitConverter.GetBytes(IPv4Address));
        }
    }

    [DllImport("mpr.dll", EntryPoint = "WNetGetConnectionW", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    [DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource netResource, string password, string userName, uint flags);

    [DllImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool force);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern int GetBestInterfaceEx(ref SockaddrInet destinationAddress, out uint bestInterfaceIndex);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern int GetBestRoute2(IntPtr interfaceLuid, uint interfaceIndex, IntPtr sourceAddress,
        ref SockaddrInet destinationAddress, uint addressSortOptions, IntPtr bestRoute, out SockaddrInet bestSourceAddress);
}
