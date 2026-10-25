using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Connector.Network;
using Microsoft.Win32;

namespace Connector.SmbAccess;

/// <summary>State of one read-only signal of the previous Structura Connector 1.0.x.</summary>
public enum LegacySignal
{
    Absent,
    Present,
    Unknown,
}

/// <summary>One Windows SMB use of the current logon session. The account is never read.</summary>
public readonly record struct SmbConnectionEntry(string? LocalName, string RemoteName);

/// <summary>
/// Read-only evidence that the previous Structura Connector still owns this PC's connection to
/// the legacy server. Holds no account names, paths or credentials and is safe to log.
/// </summary>
public sealed record LegacyConnectorSnapshot(
    LegacySignal Process,
    LegacySignal AutoStart,
    LegacySignal TunnelService,
    LegacySignal ForeignSmbConnection,
    LegacySignal TransferMarker,
    IReadOnlyList<string> UnknownReasons)
{
    public bool IsTransferred => TransferMarker == LegacySignal.Present;

    /// <summary>
    /// Owner decision 25.10: the old client is alive if its process runs, or a foreign SMB
    /// connection to the legacy host exists, or its autostart remains without a transfer marker.
    /// The AmneziaWG tunnel service is diagnostic only.
    /// </summary>
    public LegacySignal Alive
    {
        get
        {
            var signals = new[] { Process, ForeignSmbConnection, IsTransferred ? LegacySignal.Absent : AutoStart };
            if (signals.Contains(LegacySignal.Present)) return LegacySignal.Present;
            return signals.Contains(LegacySignal.Unknown) ? LegacySignal.Unknown : LegacySignal.Absent;
        }
    }

    /// <summary>
    /// Neighbour mode is the default: only an explicit transfer (K5) or a confirmed absence of
    /// the old client allows this connector to touch the legacy share.
    /// </summary>
    public bool NeighbourModeActive => !IsTransferred && Alive != LegacySignal.Absent;

    public static LegacyConnectorSnapshot Failed(Exception exception) => new(
        LegacySignal.Unknown, LegacySignal.Unknown, LegacySignal.Unknown, LegacySignal.Unknown, LegacySignal.Unknown,
        [$"detector:{exception.GetType().Name}"]);

    public string ToSafeDiagnostic() =>
        $"process={Process}, autostart={AutoStart}, tunnel={TunnelService}, foreignSmb={ForeignSmbConnection}, " +
        $"transfer={TransferMarker}, neighbour={NeighbourModeActive}" +
        (UnknownReasons.Count == 0 ? string.Empty : $", unknown={string.Join('/', UnknownReasons)}");
}

public interface ILegacyConnectorDetector
{
    LegacyConnectorSnapshot Detect();
}

/// <summary>
/// Per-user marker of an explicit «перевод ПК» to Платформа Коннектор. Written by the transfer
/// action (K5); its presence turns neighbour mode off. This class only reads it.
/// </summary>
public static class LegacyConnectorTransferMarker
{
    // Same root as UnifiedUserStatePaths.Root of the unified desktop application.
    public const string DirectoryName = "Platform Connector";
    public const string FileName = "legacy-connector-transferred.marker";
    /// <summary>Transfer journal and saved autostart value of K5; the marker is written only on success.</summary>
    public const string StateFileName = "legacy-connector-transfer.json";

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DirectoryName, FileName);

    public static string DefaultStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DirectoryName, StateFileName);

    public static bool IsTransferred(string? path = null) => File.Exists(path ?? DefaultPath);
}

internal sealed record LegacyProcessImage(int ProcessId, int? SessionId, string? ImagePath);

internal interface ILegacyConnectorProbes
{
    int OwnProcessId { get; }
    int OwnSessionId { get; }
    string? OwnProcessPath { get; }
    string LegacyDefaultExecutable { get; }
    string? ReadAutoStartCommand();
    bool FileExists(string path);
    IReadOnlyList<LegacyProcessImage> ReadProcessImages(string processName);
    bool IsServiceRunning(string serviceName);
    IReadOnlyList<SmbConnectionEntry> EnumerateSmbConnections();
    bool TransferMarkerExists();
}

/// <summary>
/// Detects the previous Structura Connector 1.0.x of the current Windows user. Cheap and
/// side-effect free: it reads HKCU, the process list, the SCM status and the logon session's
/// SMB uses; it never stops, cancels or changes anything. Any failure of one signal is
/// reported as <see cref="LegacySignal.Unknown"/> with the exception type only.
/// </summary>
public sealed class WindowsLegacyConnectorDetector : ILegacyConnectorDetector
{
    // v1.0.31 Connector.Desktop.csproj AssemblyName; the unified shell has the same image name.
    public const string LegacyProcessName = "Connector.Desktop";
    // v1.0.31 Connector.Desktop.Setup/Package.wxs: perUser, LocalAppData\Structura Connector.
    public const string LegacyInstallDirectoryName = "Structura Connector";
    // v1.0.31 Services/AutoStartService.cs (the unified shell uses "StructuraUnifiedConnector").
    public const string AutoStartRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string AutoStartValueName = "ConnectorAgentDesktop";
    // v1.0.31 Services/VpnProvisioningService.cs + VpnViewModel tunnel "structura-vpn".
    public const string TunnelServiceName = "AmneziaWGTunnel$structura-vpn";

    private readonly ILegacyConnectorProbes _probes;

    public WindowsLegacyConnectorDetector() : this(new WindowsLegacyConnectorProbes()) { }

    internal WindowsLegacyConnectorDetector(ILegacyConnectorProbes probes) =>
        _probes = probes ?? throw new ArgumentNullException(nameof(probes));

    public LegacyConnectorSnapshot Detect()
    {
        var unknown = new List<string>();
        var ownPath = _probes.OwnProcessPath;
        string? autoStartExecutable = null;
        var autoStart = Read(unknown, "autostart", () =>
        {
            var command = _probes.ReadAutoStartCommand();
            if (string.IsNullOrWhiteSpace(command)) return LegacySignal.Absent;
            autoStartExecutable = ParseExecutable(command);
            if (autoStartExecutable is null) return LegacySignal.Present;
            if (ownPath is not null && SamePath(autoStartExecutable, ownPath)) return LegacySignal.Absent;
            // A stale value left after the old client was removed cannot start it again.
            return _probes.FileExists(autoStartExecutable) ? LegacySignal.Present : LegacySignal.Absent;
        });
        var process = Read(unknown, "process", () => DetectProcess(ownPath, autoStartExecutable));
        var tunnel = Read(unknown, "tunnel", () =>
            _probes.IsServiceRunning(TunnelServiceName) ? LegacySignal.Present : LegacySignal.Absent);
        var foreign = Read(unknown, "smb", DetectDevicelessConnection);
        var transfer = Read(unknown, "transfer", () =>
            _probes.TransferMarkerExists() ? LegacySignal.Present : LegacySignal.Absent);
        return new LegacyConnectorSnapshot(process, autoStart, tunnel, foreign, transfer, unknown.AsReadOnly());
    }

    private LegacySignal DetectProcess(string? ownPath, string? autoStartExecutable)
    {
        var (matches, unreadable) = FindLegacyProcesses(_probes, ownPath, autoStartExecutable);
        if (matches.Count > 0) return LegacySignal.Present;
        return unreadable ? LegacySignal.Unknown : LegacySignal.Absent;
    }

    /// <summary>
    /// The one identification of the previous client's processes, shared by neighbour mode (K4)
    /// and the transfer (K5): same image name, this Windows session, the default perUser install
    /// or the executable of its autostart value, and never this process or its executable.
    /// </summary>
    internal static (IReadOnlyList<LegacyProcessImage> Matches, bool Unreadable) FindLegacyProcesses(
        ILegacyConnectorProbes probes, string? ownPath, string? autoStartExecutable)
    {
        var legacyExecutables = new[] { probes.LegacyDefaultExecutable, autoStartExecutable }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        var matches = new List<LegacyProcessImage>();
        var unreadable = false;
        foreach (var image in probes.ReadProcessImages(LegacyProcessName))
        {
            if (image.ProcessId == probes.OwnProcessId) continue;
            // Another Windows session (RDS, fast user switching) belongs to another logon.
            if (image.SessionId is { } session && session != probes.OwnSessionId) continue;
            if (string.IsNullOrWhiteSpace(image.ImagePath))
            {
                unreadable = true;
                continue;
            }
            // Never flag this connector, whichever directory it was started from.
            if (ownPath is not null && SamePath(image.ImagePath, ownPath)) continue;
            if (legacyExecutables.Any(path => SamePath(image.ImagePath, path!))) matches.Add(image);
        }
        return (matches.AsReadOnly(), unreadable);
    }

    /// <summary>v1.0.31 deviceless use of the legacy host (see <see cref="DetectDevicelessConnection"/>).</summary>
    internal static bool IsLegacyDevicelessConnection(SmbConnectionEntry connection) =>
        string.IsNullOrEmpty(connection.LocalName) && IsLegacyHost(connection.RemoteName);

    /// <summary>
    /// v1.0.31 connects with a deviceless <c>net use \\host\share /persistent:no</c>
    /// (MainWindow.ConnectShareWithAnyLogin). This connector always maps a drive letter, so a
    /// drive-letter mapping is never old-client evidence; a foreign or leftover one is refused
    /// separately as <c>smb_mapping_not_owned</c>. Implicit deviceless connections left by UNC
    /// opens of other programs (e.g. Tekla, AutoCAD) are indistinguishable and also count.
    /// </summary>
    private LegacySignal DetectDevicelessConnection() =>
        _probes.EnumerateSmbConnections().Any(IsLegacyDevicelessConnection)
            ? LegacySignal.Present
            : LegacySignal.Absent;

    private static LegacySignal Read(List<string> unknown, string signal, Func<LegacySignal> read)
    {
        try
        {
            var value = read();
            if (value == LegacySignal.Unknown) unknown.Add($"{signal}:unreadable");
            return value;
        }
        catch (Exception exception)
        {
            unknown.Add($"{signal}:{exception.GetType().Name}");
            return LegacySignal.Unknown;
        }
    }

    internal static bool IsLegacyHost(string? remoteName)
    {
        if (remoteName is null || !remoteName.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var host = remoteName[2..];
        var separator = host.IndexOf('\\');
        if (separator >= 0) host = host[..separator];
        return string.Equals(host, LegacyEncryptedSmbPolicy.Destination, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ParseExecutable(string command)
    {
        var value = command.Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            value = end > 1 ? value[1..end] : string.Empty;
        }
        else
        {
            var extension = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (extension > 0) value = value[..(extension + 4)];
        }
        return string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) ? null : value;
    }

    internal static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

internal sealed class WindowsLegacyConnectorProbes : ILegacyConnectorProbes
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceRunning = 0x00000004;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int NoError = 0;
    private const int ErrorMoreData = 234;
    private const uint MaxPreferredLength = 0xFFFFFFFF;

    public int OwnProcessId => Environment.ProcessId;
    public int OwnSessionId { get; } = ReadOwnSessionId();
    public string? OwnProcessPath => Environment.ProcessPath;

    public string LegacyDefaultExecutable => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        WindowsLegacyConnectorDetector.LegacyInstallDirectoryName,
        WindowsLegacyConnectorDetector.LegacyProcessName + ".exe");

    public string? ReadAutoStartCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowsLegacyConnectorDetector.AutoStartRunKey, writable: false);
        return key?.GetValue(WindowsLegacyConnectorDetector.AutoStartValueName) as string;
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool TransferMarkerExists() => LegacyConnectorTransferMarker.IsTransferred();

    public IReadOnlyList<LegacyProcessImage> ReadProcessImages(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Select(process => new LegacyProcessImage(
                process.Id, TryReadSessionId(process), TryReadImagePath(process.Id))).ToArray();
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public bool IsServiceRunning(string serviceName)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceDoesNotExist) return false;
                throw new Win32Exception(error);
            }
            try
            {
                if (!QueryServiceStatus(service, out var status)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return status.CurrentState == ServiceRunning;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public IReadOnlyList<SmbConnectionEntry> EnumerateSmbConnections()
    {
        // USE_INFO_0 holds only the local device and the remote name; never the account.
        var result = new List<SmbConnectionEntry>();
        uint resume = 0;
        int status;
        do
        {
            status = NetUseEnum(null, 0, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
            try
            {
                if (status != NoError && status != ErrorMoreData) throw new Win32Exception(status);
                var size = Marshal.SizeOf<UseInfo0>();
                for (var index = 0; index < read; index++)
                {
                    var entry = Marshal.PtrToStructure<UseInfo0>(buffer + index * size);
                    if (!string.IsNullOrEmpty(entry.Remote))
                        result.Add(new SmbConnectionEntry(string.IsNullOrEmpty(entry.Local) ? null : entry.Local, entry.Remote));
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
            }
        }
        while (status == ErrorMoreData);
        return result;
    }

    private static int ReadOwnSessionId()
    {
        using var current = Process.GetCurrentProcess();
        return current.SessionId;
    }

    private static int? TryReadSessionId(Process process)
    {
        try { return process.SessionId; }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { return null; }
    }

    private static string? TryReadImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var capacity = 32768;
            var path = new StringBuilder(capacity);
            return QueryFullProcessImageName(handle, 0, path, ref capacity) ? path.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UseInfo0
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Local;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Remote;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUseEnum(string? serverName, uint level, out IntPtr buffer,
        uint preferredMaximumLength, out uint entriesRead, out uint totalEntries, ref uint resumeHandle);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
