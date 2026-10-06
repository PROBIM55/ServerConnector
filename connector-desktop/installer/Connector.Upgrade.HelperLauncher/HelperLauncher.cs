using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Security.Principal;
using MutualChannel = global::Connector.Upgrade.MutualMachineChannel.MutualMachineChannel;
using Microsoft.Win32.SafeHandles;

namespace Connector.Upgrade.HelperLauncher;

/// <summary>Creates the image lease before invoking the one permitted elevated helper operation.</summary>
public sealed class HelperLauncher
{
    private static readonly Regex PipeNamePattern = new(
        "^Structura\\.Connector\\.Upgrade\\.MachinePipe\\.([0-9a-f]{32})\\.([0-9a-f]{24})$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly IHelperImageLeaseFactory _leases;
    private readonly IHelperNativeLauncher _native;

    public HelperLauncher() : this(TrustedHelperImageLeaseFactory.Instance, WindowsHelperNativeLauncher.Instance) { }

    internal HelperLauncher(IHelperImageLeaseFactory leases, IHelperNativeLauncher native)
    {
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    /// <summary>
    /// Launches only the pinned installed helper with the fixed machine-pipe bootstrap contract.
    /// The caller owns the returned process handle and must retain it while authenticating the peer.
    /// </summary>
    public SafeProcessHandle Launch(HelperImagePin imagePin, string pipeName, Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(imagePin);
        imagePin.Validate();
        ValidateOperation(pipeName, operationId);

        using var lease = _leases.Open(imagePin);
        lease.RevalidateForLaunch();
        var arguments = $"--pipe {Quote(pipeName)} --operation {Quote(operationId.ToString("N"))}";
        var process = _native.LaunchElevated(lease.ImagePath, arguments);
        if (process is null || process.IsInvalid || process.IsClosed)
        {
            process?.Dispose();
            throw new InvalidOperationException("Elevated helper launch returned no retained process handle.");
        }

        try
        {
            var launchedPath = _native.GetProcessImagePath(process);
            if (!string.Equals(NormalizeImagePath(launchedPath), NormalizeImagePath(lease.ImagePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Elevated process image path did not match the leased helper image.");
            lease.RevalidateForLaunch();
            GC.KeepAlive(lease);
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <summary>Launches the pinned helper for the initiating user's exact A-lane mutual channel.</summary>
    public SafeProcessHandle LaunchMutual(HelperImagePin imagePin, string pipeName, Guid operationId, SecurityIdentifier originalUserSid)
    {
        ArgumentNullException.ThrowIfNull(imagePin);
        ArgumentNullException.ThrowIfNull(originalUserSid);
        imagePin.Validate();
        if (operationId == Guid.Empty)
            throw new ArgumentException("Operation id must not be empty.", nameof(operationId));
        var expectedPipe = MutualChannel.GetPipeName("A", operationId, originalUserSid);
        if (!string.Equals(pipeName, expectedPipe, StringComparison.Ordinal))
            throw new ArgumentException("Pipe name must match the A lane, operation id, and original user SID.", nameof(pipeName));

        // Bind the bootstrap to this process; callers cannot supply a PID to impersonate.
        var callerPid = Environment.ProcessId;
        using var lease = _leases.Open(imagePin);
        lease.RevalidateForLaunch();
        var arguments = $"--pipe {Quote(expectedPipe)} --operation {Quote(operationId.ToString("N"))} --caller-sid {Quote(originalUserSid.Value!)} --caller-pid {Quote(callerPid.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
        return LaunchPinned(lease, arguments);
    }

    private SafeProcessHandle LaunchPinned(IHelperImageLease lease, string arguments)
    {
        var process = _native.LaunchElevated(lease.ImagePath, arguments);
        if (process is null || process.IsInvalid || process.IsClosed)
        {
            process?.Dispose();
            throw new InvalidOperationException("Elevated helper launch returned no retained process handle.");
        }
        try
        {
            var launchedPath = _native.GetProcessImagePath(process);
            if (!string.Equals(NormalizeImagePath(launchedPath), NormalizeImagePath(lease.ImagePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Elevated process image path did not match the leased helper image.");
            lease.RevalidateForLaunch();
            GC.KeepAlive(lease);
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static void ValidateOperation(string pipeName, Guid operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (operationId == Guid.Empty)
            throw new ArgumentException("Operation id must not be empty.", nameof(operationId));
        var match = PipeNamePattern.Match(pipeName);
        if (!match.Success || !string.Equals(match.Groups[1].Value, operationId.ToString("N"), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Pipe name must match the machine upgrade pipe and operation id.", nameof(pipeName));
    }

    private static string Quote(string value) => "\"" + value + "\"";
    private static string NormalizeImagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Elevated process image path is unavailable or relative.");
        var normalized = Path.GetFullPath(path);
        return normalized.StartsWith("\\\\?\\", StringComparison.Ordinal) ? normalized[4..] : normalized;
    }
}

internal interface IHelperImageLease : IDisposable
{
    string ImagePath { get; }
    void RevalidateForLaunch();
}

internal interface IHelperImageLeaseFactory
{
    IHelperImageLease Open(HelperImagePin pin);
}

internal sealed class TrustedHelperImageLeaseFactory : IHelperImageLeaseFactory
{
    internal static readonly TrustedHelperImageLeaseFactory Instance = new();
    private TrustedHelperImageLeaseFactory() { }
    public IHelperImageLease Open(HelperImagePin pin) => TrustedHelperImageLease.Open(pin);
}

internal interface IHelperNativeLauncher
{
    SafeProcessHandle LaunchElevated(string imagePath, string arguments);
    string GetProcessImagePath(SafeProcessHandle process);
}

internal sealed class WindowsHelperNativeLauncher : IHelperNativeLauncher
{
    internal static readonly WindowsHelperNativeLauncher Instance = new();
    private const uint SeeMaskNoCloseProcess = 0x00000040;
    private const uint SeeMaskNoAsync = 0x00000100;
    private const int ErrorCancelled = 1223;
    private WindowsHelperNativeLauncher() { }

    public SafeProcessHandle LaunchElevated(string imagePath, string arguments)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ShellExecuteEx elevation is available only on Windows.");
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = SeeMaskNoCloseProcess | SeeMaskNoAsync,
            Verb = "runas",
            File = imagePath,
            Parameters = arguments,
            Show = 0
        };
        if (!ShellExecuteEx(ref info))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorCancelled)
                throw new OperationCanceledException("User cancelled elevated helper launch.", new Win32Exception(error));
            throw new Win32Exception(error, "Could not launch the trusted helper with elevation.");
        }
        if (info.Process == IntPtr.Zero)
            throw new InvalidOperationException("ShellExecuteEx did not return the required process handle.");
        return new SafeProcessHandle(info.Process, ownsHandle: true);
    }

    public string GetProcessImagePath(SafeProcessHandle process)
    {
        var capacity = 32768;
        var path = new System.Text.StringBuilder(capacity);
        if (!QueryFullProcessImageName(process, 0, path, ref capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not bind the launched process to its image path.");
        return path.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Window;
        public string? Verb;
        public string? File;
        public string? Parameters;
        public string? Directory;
        public int Show;
        public IntPtr Instance;
        public IntPtr IdList;
        public string? Class;
        public IntPtr ClassKey;
        public uint HotKey;
        public IntPtr IconOrMonitor;
        public IntPtr Process;
    }

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellExecuteEx(ref ShellExecuteInfo executeInfo);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, System.Text.StringBuilder path, ref int size);
}
