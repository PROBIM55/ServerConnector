using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Connector.Updates;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Connector.Updater;

internal sealed class SystemUpdaterClock : IUpdaterClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

internal sealed class SharedUpdaterRandom : IUpdaterRandom
{
    public double NextDouble() => Random.Shared.NextDouble();
}

/// <summary><c>HKLM\Software\Structura\PlatformConnector\UpdateChannel</c>; absent means <c>stable</c>, anything invalid is refused.</summary>
internal sealed class RegistryChannelSource : IChannelSource
{
    public string ReadChannel()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(UpdateLocations.ProductRegistryKey, writable: false);
        var value = key?.GetValue(UpdateLocations.ChannelValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return ReleaseCatalogContract.DefaultChannel;
        if (key!.GetValueKind(UpdateLocations.ChannelValueName) != RegistryValueKind.String || value is not string channel ||
            !ReleaseCatalogContract.IsValidChannel(channel))
            throw new UpdateRejectedException(UpdateErrorCodes.ChannelInvalid);
        return channel;
    }
}

/// <summary>
/// The installed product. The installation root is where this service runs from (<c>&lt;root&gt;\Updater\</c>);
/// HKLM <c>InstallRoot</c> must name the same folder, otherwise nothing is installed, stopped or launched.
/// </summary>
internal sealed class WindowsInstalledProduct : IInstalledProduct
{
    /// <summary>UpgradeCode of <c>Connector.Clean.Setup/Package.wxs</c>.</summary>
    internal const string UpgradeCode = "{67B43FCD-4FE8-4D29-9F64-6F190FCD6E51}";
    private readonly bool _rootConfirmed;

    public WindowsInstalledProduct(IUpdaterLog log)
    {
        var serviceDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var installRoot = Path.GetDirectoryName(serviceDirectory) ?? serviceDirectory;
        ApplicationExecutable = Path.Combine(installRoot, UpdateLocations.ApplicationExecutable);
        string? registered = null;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(UpdateLocations.ProductRegistryKey, writable: false);
            registered = key?.GetValue(UpdateLocations.InstallRootValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        _rootConfirmed = string.Equals(Path.GetFileName(serviceDirectory), "Updater", StringComparison.OrdinalIgnoreCase) &&
                         registered is not null && Path.IsPathFullyQualified(registered) &&
                         string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(registered)), installRoot, StringComparison.OrdinalIgnoreCase);
        if (!_rootConfirmed) log.Warning("Service location does not match the registered installation; updates are disabled.");
    }

    public string ApplicationExecutable { get; }

    public ReleaseVersion? InstalledVersion()
    {
        if (!_rootConfirmed) return null;
        var productCode = new char[39];
        if (NativeMethods.MsiEnumRelatedProductsW(UpgradeCode, 0, 0, productCode) != NativeMethods.ErrorSuccess) return null;
        // More than one registered product of this identity is not a state to update from.
        if (NativeMethods.MsiEnumRelatedProductsW(UpgradeCode, 0, 1, new char[39]) != NativeMethods.ErrorNoMoreItems) return null;
        var code = new string(productCode, 0, 38);
        var buffer = new char[32];
        var length = (uint)buffer.Length;
        if (NativeMethods.MsiGetProductInfoW(code, "VersionString", buffer, ref length) != NativeMethods.ErrorSuccess) return null;
        return ReleaseVersion.TryParse(new string(buffer, 0, (int)length), out var version) ? version : null;
    }
}

internal sealed class WindowsApplicationProcesses(IInstalledProduct product) : IApplicationProcesses
{
    public IReadOnlyList<IApplicationProcess> List()
    {
        var expected = Path.GetFullPath(product.ApplicationExecutable);
        var installRoot = Path.GetDirectoryName(expected)! + Path.DirectorySeparatorChar;
        var result = new List<IApplicationProcess>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(UpdateLocations.ApplicationExecutable)))
        {
            try
            {
                // Opening the handle keeps the PID from being reused until this object is disposed.
                var handle = process.SafeHandle;
                if (!string.Equals(ImagePath(handle), expected, StringComparison.OrdinalIgnoreCase) || UserSid(handle) is not { } sid)
                {
                    process.Dispose();
                    continue;
                }
                result.Add(new WindowsApplicationProcess(process, process.SessionId, sid, installRoot));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                process.Dispose(); // exited meanwhile or not accessible
            }
        }
        return result;
    }

    internal static string? ImagePath(SafeProcessHandle handle)
    {
        var buffer = new char[1024];
        var size = buffer.Length;
        return NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? Path.GetFullPath(new string(buffer, 0, size)) : null;
    }

    private static string? UserSid(SafeProcessHandle handle)
    {
        if (!NativeMethods.OpenProcessToken(handle, NativeMethods.TokenQuery, out var token)) return null;
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User?.Value;
    }

    private sealed class WindowsApplicationProcess(Process process, int sessionId, string userSid, string installRoot) : IApplicationProcess
    {
        public int SessionId { get; } = sessionId;
        public string UserSid { get; } = userSid;
        public bool HasExited
        {
            get
            {
                try { return process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        /// <summary>
        /// Ends the application and only those of its descendants whose image lies in the installation folder
        /// (Git, workers) — never a browser, Excel, Explorer or anything else it happened to start.
        /// </summary>
        public void Kill()
        {
            var parentStarted = process.StartTime;
            foreach (var pid in ProcessTree.Descendants(process.Id))
            {
                try
                {
                    using var child = Process.GetProcessById(pid);
                    if (child.StartTime < parentStarted) continue; // a reused PID, not our descendant
                    if (ImagePath(child.SafeHandle) is { } image && image.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        child.Kill(entireProcessTree: false);
                        child.WaitForExit(5_000);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { }
            }
            process.Kill(entireProcessTree: false);
            process.WaitForExit(10_000);
        }

        public void Dispose() => process.Dispose();
    }
}

/// <summary>
/// Reads the application's flag files without trusting them: the reparse point itself is opened (never a
/// target), directories, links and hard-linked files are refused, the owner must be the user named in the
/// file name (or Administrators), at most 1 KiB is read and only one timestamp is accepted.
/// </summary>
internal sealed class WindowsSignalReader(string signalsDirectory) : ISignalReader
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public DateTimeOffset? ReadBusyUntil(string userSid) =>
        UpdateSignalFiles.IsSid(userSid) ? Read(UpdateSignalFiles.BusyFileName(userSid), userSid) : null;

    public DateTimeOffset? ReadCheckRequest(string userSid) =>
        UpdateSignalFiles.IsSid(userSid) ? Read(UpdateSignalFiles.CheckFileName(userSid), userSid) : null;

    private DateTimeOffset? Read(string fileName, string userSid)
    {
        var path = Path.Combine(signalsDirectory, fileName);
        using var handle = NativeMethods.CreateFileW(path, NativeMethods.GenericRead,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete, IntPtr.Zero,
            NativeMethods.OpenExisting, NativeMethods.FileFlagOpenReparsePoint | NativeMethods.FileFlagSequentialScan, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        if (!NativeMethods.GetFileInformationByHandle(handle, out var information) ||
            (information.FileAttributes & (NativeMethods.FileAttributeDirectory | NativeMethods.FileAttributeReparsePoint)) != 0 ||
            information.NumberOfLinks != 1 || information.FileSizeHigh != 0 || information.FileSizeLow > UpdateSignalFiles.MaximumBytes)
            return null;
        try
        {
            using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
            var owner = stream.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null || (owner.Value != userSid && owner != Administrators)) return null;
            var buffer = new byte[UpdateSignalFiles.MaximumBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
            return total > UpdateSignalFiles.MaximumBytes ? null : UpdateSignalFiles.TryDecode(buffer.AsSpan(0, total));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }
}

internal sealed class WindowsInstallerRunner : IInstallerRunner
{
    public IInstallerProcess Start(string installerPath, string logPath)
    {
        if (!Path.IsPathFullyQualified(installerPath) || !Path.IsPathFullyQualified(logPath) ||
            installerPath.Contains('"') || logPath.Contains('"'))
            throw new ArgumentException("Installer paths must be fully qualified.");
        var commandLine = new StringBuilder("\"" + installerPath + "\" /quiet /norestart /log \"" + logPath + "\"");
        var startup = new NativeMethods.StartupInfo { cb = Marshal.SizeOf<NativeMethods.StartupInfo>() };
        var flags = NativeMethods.CreateNoWindow | NativeMethods.CreateUnicodeEnvironment;
        var directory = Path.GetDirectoryName(installerPath)!;
        // No handle inheritance; out of any job of the service so that stopping the service (which the
        // installer itself does) cannot end the installer.
        if (!NativeMethods.CreateProcessW(installerPath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                flags | NativeMethods.CreateBreakawayFromJob, IntPtr.Zero, directory, ref startup, out var info))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ErrorAccessDenied ||
                !NativeMethods.CreateProcessW(installerPath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    flags, IntPtr.Zero, directory, ref startup, out info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        NativeMethods.CloseHandle(info.hThread);
        var process = new SafeProcessHandle(info.hProcess, ownsHandle: true);
        return new WindowsInstallerProcess(process, info.dwProcessId, StartTime(process));
    }

    public IInstallerProcess? TryAttach(int processId, DateTimeOffset startedAtUtc)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.Synchronize | NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        // A PID alone may have been reused: the creation time must be the one recorded at start.
        if (StartTime(handle) != startedAtUtc) { handle.Dispose(); return null; }
        return new WindowsInstallerProcess(handle, processId, startedAtUtc);
    }

    private static DateTimeOffset StartTime(SafeProcessHandle handle) =>
        NativeMethods.GetProcessTimes(handle, out var creation, out _, out _, out _)
            ? new DateTimeOffset(DateTime.FromFileTimeUtc(creation))
            : DateTimeOffset.MinValue;

    private sealed class WindowsInstallerProcess(SafeProcessHandle handle, int processId, DateTimeOffset startedAtUtc) : IInstallerProcess
    {
        public int ProcessId { get; } = processId;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public bool HasExited => NativeMethods.WaitForSingleObject(handle, 0) == 0;

        public async Task<int?> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var wait = new ProcessExitWait(handle);
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ThreadPool.RegisterWaitForSingleObject(wait, (_, timedOut) => exited.TrySetResult(!timedOut), null,
                timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout, executeOnlyOnce: true);
            try
            {
                using (cancellationToken.Register(() => exited.TrySetCanceled(cancellationToken)))
                    if (!await exited.Task.ConfigureAwait(false)) return null;
            }
            finally { registration.Unregister(null); }
            return NativeMethods.GetExitCodeProcess(handle, out var code) ? code : null;
        }

        public void Dispose() => handle.Dispose();
    }

    private sealed class ProcessExitWait : WaitHandle
    {
        public ProcessExitWait(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}

/// <summary>Starts the installed application in a user's session with that user's own (unelevated) token.</summary>
internal sealed class WtsSessionLauncher(IUpdaterLog log) : ISessionLauncher
{
    public bool TryLaunch(int sessionId, string userSid, string applicationExecutable)
    {
        if (!Path.IsPathFullyQualified(applicationExecutable) || applicationExecutable.Contains('"') ||
            !File.Exists(applicationExecutable) ||
            (File.GetAttributes(applicationExecutable) & FileAttributes.ReparsePoint) != 0)
            return false;
        if (!NativeMethods.WTSQueryUserToken(sessionId, out var sessionToken)) return false; // nobody signed in there now
        using (sessionToken)
        {
            // Session numbers are reused: start only for the same user who ran the application.
            using (var identity = new WindowsIdentity(sessionToken.DangerousGetHandle()))
                if (!string.Equals(identity.User?.Value, userSid, StringComparison.Ordinal)) return false;
            if (!NativeMethods.DuplicateTokenEx(sessionToken,
                    NativeMethods.TokenQuery | NativeMethods.TokenDuplicate | NativeMethods.TokenAssignPrimary |
                    NativeMethods.TokenAdjustDefault | NativeMethods.TokenAdjustSessionId,
                    IntPtr.Zero, NativeMethods.SecurityImpersonation, NativeMethods.TokenPrimary, out var primary))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (primary)
            {
                if (!NativeMethods.CreateEnvironmentBlock(out var environment, primary, inherit: false))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var startup = new NativeMethods.StartupInfo
                    {
                        cb = Marshal.SizeOf<NativeMethods.StartupInfo>(),
                        lpDesktop = @"winsta0\default"
                    };
                    var commandLine = new StringBuilder("\"" + applicationExecutable + "\"");
                    if (!NativeMethods.CreateProcessAsUserW(primary, applicationExecutable, commandLine, IntPtr.Zero, IntPtr.Zero,
                            false, NativeMethods.CreateUnicodeEnvironment, environment,
                            Path.GetDirectoryName(applicationExecutable)!, ref startup, out var info))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    NativeMethods.CloseHandle(info.hThread);
                    NativeMethods.CloseHandle(info.hProcess);
                    log.Info("Application restarted in session " + sessionId + ".");
                    return true;
                }
                finally
                {
                    NativeMethods.DestroyEnvironmentBlock(environment);
                }
            }
        }
    }
}

/// <summary>Parent/child relations from one Toolhelp snapshot.</summary>
internal static class ProcessTree
{
    public static IReadOnlyList<int> Descendants(int root)
    {
        var children = new Dictionary<int, List<int>>();
        using (var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0))
        {
            if (snapshot.IsInvalid) return Array.Empty<int>();
            var entry = new NativeMethods.ProcessEntry32 { dwSize = Marshal.SizeOf<NativeMethods.ProcessEntry32>() };
            for (var ok = NativeMethods.Process32FirstW(snapshot, ref entry); ok; ok = NativeMethods.Process32NextW(snapshot, ref entry))
            {
                if (entry.th32ProcessID == entry.th32ParentProcessID) continue;
                if (!children.TryGetValue(entry.th32ParentProcessID, out var list)) children[entry.th32ParentProcessID] = list = new List<int>();
                list.Add(entry.th32ProcessID);
            }
        }
        var result = new List<int>();
        var seen = new HashSet<int> { root };
        var queue = new Queue<int>();
        queue.Enqueue(root);
        while (queue.Count > 0 && result.Count < 512)
        {
            if (!children.TryGetValue(queue.Dequeue(), out var list)) continue;
            foreach (var child in list)
                if (seen.Add(child)) { result.Add(child); queue.Enqueue(child); }
        }
        return result;
    }
}

/// <summary>Where the service keeps its own files: below its own folder in Program Files, never in ProgramData.</summary>
internal sealed class ServicePaths
{
    public ServicePaths(string serviceDirectory)
    {
        StateRoot = Path.Combine(Path.GetFullPath(serviceDirectory), "state");
        Downloads = Path.Combine(StateRoot, "downloads");
        Logs = Path.Combine(StateRoot, "logs");
        StateFile = Path.Combine(StateRoot, "service-state.json");
    }

    public static ServicePaths ForThisService() =>
        new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)));

    public string StateRoot { get; }
    public string Downloads { get; }
    public string Logs { get; }
    public string StateFile { get; }
}

/// <summary>
/// A file or folder is trustworthy when only SYSTEM, Administrators or TrustedInstaller own it and may change it:
/// owner one of them, no allow entry giving anybody else write, delete, permission or ownership rights, no
/// reparse point, and (files) exactly one link.
/// </summary>
internal sealed class WindowsFileTrust : IFileTrust
{
    private static readonly HashSet<string> Trusted = new(StringComparer.Ordinal)
    {
        "S-1-5-18",                                                           // SYSTEM
        "S-1-5-32-544",                                                       // Administrators
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"      // TrustedInstaller
    };
    private const int WriteMask =
        (int)(FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
              FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
              FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership) |
        0x40000000 /* GENERIC_WRITE */ | 0x10000000 /* GENERIC_ALL */;

    public bool IsTrusted(FileStream stream)
    {
        try
        {
            if (!NativeMethods.GetFileInformationByHandle(stream.SafeFileHandle, out var information) ||
                (information.FileAttributes & (NativeMethods.FileAttributeDirectory | NativeMethods.FileAttributeReparsePoint)) != 0 ||
                information.NumberOfLinks != 1)
                return false;
            return IsTrusted(stream.GetAccessControl());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }

    public static bool IsTrustedDirectory(DirectoryInfo directory)
    {
        try
        {
            directory.Refresh();
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return IsTrusted(directory.GetAccessControl());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }

    internal static bool IsTrusted(FileSystemSecurity security)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted.Contains(owner.Value)) return false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            if (rule.IdentityReference is SecurityIdentifier sid && Trusted.Contains(sid.Value)) continue;
            if (((int)rule.FileSystemRights & WriteMask) != 0) return false;
        }
        return true;
    }
}

/// <summary>
/// Prepares every folder the service uses at each start and decides whether it may work at all:
/// <list type="bullet">
/// <item>its own folder and the installation root must be trustworthy, otherwise nothing is done;</item>
/// <item>the state tree (state, downloads, journal) is checked object by object without following reparse points;
/// anything not trustworthy wipes the whole tree, which is then recreated with a protected ACL (<c>state-reset</c>);</item>
/// <item><c>ProgramData\Structura Connector\Updates</c> gets its protected ACL again and keeps nothing but the
/// service's status file; <c>UpdateSignals</c> gets its ACL again.</item>
/// </list>
/// </summary>
internal static class SecureDirectories
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);

    /// <summary>
    /// Writes «failed / storage-untrusted» into the status file when only that is still trustworthy, so the reason of
    /// an idle service is visible to the application.
    /// </summary>
    public static void TryReportUntrusted(UpdateLocations machine)
    {
        try
        {
            if (!WindowsFileTrust.IsTrustedDirectory(new DirectoryInfo(machine.MachineRoot)) ||
                !WindowsFileTrust.IsTrustedDirectory(new DirectoryInfo(machine.UpdatesRoot))) return;
            var now = DateTimeOffset.UtcNow;
            AtomicFile.Write(machine.StatusFile, UpdateStatusCodec.Serialize(new UpdateStatus(null, null, UpdatePhase.Failed, null, null,
                UpdateErrorCodes.StorageUntrusted, null, null, Array.Empty<string>(), null, now)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>False when the service must not act (untrusted own folder, installation root or machine data folder).</summary>
    public static bool Ensure(UpdateLocations machine, ServicePaths paths, IFileTrust trust, Action<string> log)
    {
        var serviceDirectory = new DirectoryInfo(Path.GetDirectoryName(paths.StateRoot)!);
        if (!WindowsFileTrust.IsTrustedDirectory(serviceDirectory) || serviceDirectory.Parent is not { } installRoot ||
            !WindowsFileTrust.IsTrustedDirectory(installRoot))
        {
            log("Service folder or installation root is not trustworthy; updates are disabled.");
            return false;
        }

        var state = new DirectoryInfo(paths.StateRoot);
        if (state.Exists && !TreeTrusted(state, trust))
        {
            log("state-reset: the service state folder held objects that are not trustworthy; recreated.");
            Wipe(state);
        }
        Apply(paths.StateRoot, Protected(usersRead: false));
        Apply(paths.Downloads, Protected(usersRead: false));
        Apply(paths.Logs, Protected(usersRead: false));

        if (!WindowsFileTrust.IsTrustedDirectory(new DirectoryInfo(machine.MachineRoot)))
        {
            log("The machine data folder is missing or not trustworthy; updates are disabled.");
            return false;
        }
        Apply(machine.UpdatesRoot, Protected(usersRead: true));
        foreach (var entry in new DirectoryInfo(machine.UpdatesRoot).EnumerateFileSystemInfos())
        {
            // Nothing but the service's own status file belongs here; an untrusted status file is replaced.
            var keep = entry is FileInfo file && string.Equals(file.Name, UpdateLocations.StatusFileName, StringComparison.OrdinalIgnoreCase) &&
                       (file.Attributes & FileAttributes.ReparsePoint) == 0 && FileTrusted(file.FullName, trust);
            if (!keep) DeleteEntry(entry);
        }
        Apply(machine.Signals, Signals());
        return true;
    }

    private static bool TreeTrusted(DirectoryInfo directory, IFileTrust trust)
    {
        if (!WindowsFileTrust.IsTrustedDirectory(directory)) return false;
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (entry is DirectoryInfo child ? !TreeTrusted(child, trust) : !FileTrusted(entry.FullName, trust)) return false;
        }
        return true;
    }

    private static bool FileTrusted(string path, IFileTrust trust)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return trust.IsTrusted(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Deletes a tree; reparse points are removed as links, never followed.</summary>
    private static void Wipe(DirectoryInfo directory)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) { directory.Delete(); return; }
        foreach (var entry in directory.EnumerateFileSystemInfos()) DeleteEntry(entry);
        directory.Delete();
    }

    private static void DeleteEntry(FileSystemInfo entry)
    {
        entry.Attributes &= ~FileAttributes.ReadOnly;
        if (entry is DirectoryInfo directory) Wipe(directory);
        else entry.Delete();
    }

    private static void Apply(string path, DirectorySecurity security)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists)
        {
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { info.Delete(); info.Create(security); return; }
            info.SetAccessControl(security);
        }
        else info.Create(security);
    }

    private static DirectorySecurity Protected(bool usersRead)
    {
        var security = new DirectorySecurity();
        security.SetOwner(System);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (usersRead)
            security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Users may list the folder and create files (no subfolders); each user can read, write and delete only
    /// the files they created. Same rule as the installer's SDDL for this folder.
    /// </summary>
    private static DirectorySecurity Signals()
    {
        var security = Protected(usersRead: false);
        security.AddAccessRule(new FileSystemAccessRule(Users,
            FileSystemRights.ListDirectory | FileSystemRights.CreateFiles | FileSystemRights.ReadAttributes |
            FileSystemRights.Traverse | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(CreatorOwner,
            FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.ReadAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Delete |
            FileSystemRights.ReadPermissions | FileSystemRights.Synchronize,
            InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        return security;
    }
}
