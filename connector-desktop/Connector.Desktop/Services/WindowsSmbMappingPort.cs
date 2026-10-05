using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Connector.Network;

namespace Connector.SmbAccess;

public interface IWindowsSmbMappingPort
{
    event Action? NetworkChanged;
    ValueTask<NetworkOverlaySnapshot> GetLiveOverlayAsync(CancellationToken cancellationToken);
    ValueTask<string?> GetTargetAsync(string drive, CancellationToken cancellationToken);
    ValueTask<bool> IsRouteAllowedAsync(IPAddress destination,
        IReadOnlyList<IPAddress> expectedOverlayAddresses, CancellationToken cancellationToken);
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
    private int _disposed;

    public WindowsSmbMappingPort(Func<CancellationToken, ValueTask<NetworkOverlaySnapshot>> overlayLiveness)
        : this(new WindowsSmbNativeApi(), new WindowsNetworkChangeMonitor(), overlayLiveness) { }

    internal WindowsSmbMappingPort(
        IWindowsSmbNativeApi native,
        IWindowsNetworkChangeMonitor? networkChanges = null,
        Func<CancellationToken, ValueTask<NetworkOverlaySnapshot>>? overlayLiveness = null)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _networkChanges = networkChanges;
        _overlayLiveness = overlayLiveness;
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

    public ValueTask MountAsync(string drive, string shareUnc, string userName, string password,
        IPAddress destination, IReadOnlyList<IPAddress> expectedOverlayAddresses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedOverlayAddresses);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOverlayRoute(destination, expectedOverlayAddresses);
        if (_native.GetTarget(drive) is not null)
            throw new InvalidOperationException("The SMB drive is already mapped.");

        var result = _native.AddConnection(drive, shareUnc, userName, password);
        if (result != NoError)
            throw new Win32Exception(result, "Windows refused the temporary SMB drive mapping.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SameTarget(_native.GetTarget(drive), shareUnc))
                throw new InvalidOperationException("Windows did not confirm the requested SMB drive target.");
            EnsureOverlayRoute(destination, expectedOverlayAddresses);
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
        return ValueTask.CompletedTask;
    }

    public ValueTask<SmbExactMappingRemoval> TryUnmountExactAsync(
        string drive, string expectedShareUnc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = _native.GetTarget(drive);
        if (current is null) return ValueTask.FromResult(SmbExactMappingRemoval.Missing);
        if (!SameTarget(current, expectedShareUnc))
            return ValueTask.FromResult(SmbExactMappingRemoval.TargetChanged);

        cancellationToken.ThrowIfCancellationRequested();
        var result = _native.CancelConnection(drive); // WNet force=false: never close open handles.
        cancellationToken.ThrowIfCancellationRequested();
        if (result != NoError)
            throw new Win32Exception(result, "Windows refused to remove the exact SMB drive mapping.");

        current = _native.GetTarget(drive);
        if (current is null) return ValueTask.FromResult(SmbExactMappingRemoval.Removed);
        if (!SameTarget(current, expectedShareUnc))
            return ValueTask.FromResult(SmbExactMappingRemoval.TargetChanged);
        throw new InvalidOperationException("Windows still reports the exact SMB drive mapping after removal.");
    }

    private Exception? TryRollbackExactMapping(string drive, string shareUnc)
    {
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
