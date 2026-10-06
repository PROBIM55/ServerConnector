using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Connector.Upgrade.MachineAcl;

namespace Connector.Upgrade.WindowsUserStage;

internal sealed class WindowsUserStagePlatform : IWindowsUserStagePlatform
{
    public string GetCurrentUserSid()
    {
        AssertWindows();
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User?.Value
            ?? throw new UnauthorizedAccessException("The current Windows token has no user SID.");
    }

    public void AssertElevatedAdministrator()
    {
        AssertWindows();
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("An already elevated administrator token is required to create the user-state stage.");
    }

    public string GetProgramDataPath() =>
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public string GetLocalApplicationDataPath() =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string GetVolumeId(string path)
    {
        AssertWindows();
        var existingPath = FindNearestExistingDirectory(path);
        var mountPoint = new StringBuilder(512);
        if (!GetVolumePathName(existingPath, mountPoint, mountPoint.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not resolve the volume for '{path}'.");
        if (GetDriveType(mountPoint.ToString()) != DriveFixed)
            throw new InvalidDataException($"User-state path '{path}' is not on a fixed local volume.");

        var volumeName = new StringBuilder(128);
        if (!GetVolumeNameForVolumeMountPoint(mountPoint.ToString(), volumeName, volumeName.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not identify the volume for '{path}'.");
        if (volumeName.Length == 0)
            throw new InvalidDataException($"Windows returned an empty volume identity for '{path}'.");
        return volumeName.ToString();
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public void CreateDirectory(string path, WindowsUserStageDirectorySecurity security)
    {
        var descriptor = new DirectorySecurity();
        descriptor.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        descriptor.SetOwner(new SecurityIdentifier(security.OwnerSid));
        foreach (var rule in security.Rules)
        {
            descriptor.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(rule.Sid),
                rule.Rights,
                rule.InheritanceFlags,
                rule.PropagationFlags,
                rule.AccessType));
        }

        FileSystemAclExtensions.Create(new DirectoryInfo(path), descriptor);
    }

    public void ProtectSharedDirectory(string path)
    {
        AssertWindows();
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), WindowsSharedMachineDirectoryAcl.Create());
    }

    public WindowsUserStageDirectorySecurity ReadDirectorySecurity(string path)
    {
        var security = FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(path),
            AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new UnauthorizedAccessException($"Protected user-state path '{path}' has no SID owner.");
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => new WindowsUserStageAccessRule(
                rule.IdentityReference.Value,
                rule.FileSystemRights,
                rule.AccessControlType,
                rule.IsInherited,
                rule.InheritanceFlags,
                rule.PropagationFlags))
            .ToArray();
        return new(owner.Value, security.AreAccessRulesProtected, rules);
    }

    public bool IsDirectoryEmpty(string path) => !Directory.EnumerateFileSystemEntries(path).Any();

    public void AssertNoReparseAncestors(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidDataException($"User-state path '{path}' has no filesystem root.");

        var current = root;
        AssertNotReparseIfExisting(current, fullPath);
        foreach (var component in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            AssertNotReparseIfExisting(current, fullPath);
        }
    }

    private static void AssertNotReparseIfExisting(string path, string requestedPath)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
            return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"User-state path '{requestedPath}' contains a reparse-point ancestor.");
    }

    private static string FindNearestExistingDirectory(string path)
    {
        var current = Path.GetFullPath(path);
        if (File.Exists(current))
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidDataException($"User-state path '{path}' has no parent directory.");
        while (!Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current)
                ?? throw new DirectoryNotFoundException($"No existing ancestor could establish the volume for '{path}'.");
        }

        return current;
    }

    private static void AssertWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Protected user-state staging is available only on Windows.");
    }

    private const uint DriveFixed = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(
        string fileName,
        StringBuilder volumePathName,
        int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        StringBuilder volumeName,
        int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string rootPathName);
}
