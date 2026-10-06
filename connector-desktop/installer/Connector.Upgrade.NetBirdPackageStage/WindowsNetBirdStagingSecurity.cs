using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Connector.Upgrade.NetBirdPackageStage;

internal sealed class WindowsNetBirdMachineStagingSecurity : INetBirdMachineStagingSecurity
{
    private static readonly SecurityIdentifier SystemSid = new("S-1-5-18");
    private static readonly SecurityIdentifier AdministratorsSid = new("S-1-5-32-544");
    private static readonly SecurityIdentifier TrustedInstallerSid = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly HashSet<string> Trusted = new(StringComparer.Ordinal) { SystemSid.Value, AdministratorsSid.Value, TrustedInstallerSid.Value };
    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;
    private const FileSystemRights Mutation = GenericAll | GenericWrite | FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    private const FileSystemRights AncestorReplacement = GenericAll | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    public string PrepareRoot(string requestedRoot)
    {
        RequireElevatedTrustedCaller();
        var full = Path.GetFullPath(requestedRoot);
        var machineRoot = ResolveMachineRoot(full);
        WindowsNetBirdPathSafety.AssertNoReparseComponents(machineRoot);
        ValidateEntry(machineRoot, true, false, AncestorReplacement);
        var relative = Path.GetRelativePath(machineRoot, full);
        if (relative is "." or ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("NetBird staging must be below ProgramData or Program Files.");
        var current = machineRoot;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (Directory.Exists(current)) { ValidateProtectedDirectory(current); continue; }
            if (File.Exists(current)) throw new InvalidDataException("NetBird staging component is not a directory.");
            CreateDirectoryWithProtectedAcl(current);
            ValidateProtectedDirectory(current);
        }
        return full;
    }

    public void CreateProtectedDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var parent = Directory.GetParent(full)?.FullName ?? throw new InvalidDataException("NetBird staging directory has no parent.");
        ValidateProtectedDirectory(parent);
        if (Directory.Exists(full) || File.Exists(full)) throw new IOException("NetBird content staging already exists.");
        CreateDirectoryWithProtectedAcl(full);
        ValidateProtectedDirectory(full);
    }
    public void ProtectAndValidateFile(string path)
    {
        var security = new FileSecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(AdministratorsSid); AddRules(security, false);
        FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security); ValidateProtectedFile(path);
    }
    public void ValidateProtectedDirectory(string path) { WindowsNetBirdPathSafety.AssertNoReparseComponents(path); ValidateEntry(path, true, true, Mutation); }
    public void ValidateProtectedFile(string path) { WindowsNetBirdPathSafety.AssertNoReparseComponents(path); ValidateEntry(path, false, true, Mutation); }
    private static void RequireElevatedTrustedCaller()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        if (identity.User is { Value: var sid } && Trusted.Contains(sid)) return;
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("NetBird protected staging requires an elevated administrator, SYSTEM, or TrustedInstaller process; it never self-elevates.");
    }
    private static string ResolveMachineRoot(string candidate) => new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
        .Where(static path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(static path => path.Length)
        .FirstOrDefault(root => candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException("NetBird staging must be a strict machine-root descendant.");
    private static void CreateDirectoryWithProtectedAcl(string path) { var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(AdministratorsSid); AddRules(security, true); FileSystemAclExtensions.Create(new DirectoryInfo(path), security); }
    private static void AddRules(FileSystemSecurity security, bool inherit)
    {
        foreach (var sid in new[] { SystemSid, AdministratorsSid, TrustedInstallerSid }) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
    }
    internal static void ValidateEntry(string path, bool directory, bool requireProtectedDacl, FileSystemRights forbidden)
    {
        FileSystemSecurity security = directory ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path), AccessControlSections.Owner | AccessControlSections.Access) : FileSystemAclExtensions.GetAccessControl(new FileInfo(path), AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !Trusted.Contains(owner.Value)) throw new UnauthorizedAccessException("NetBird staging path has an untrusted owner.");
        if (requireProtectedDacl && !security.AreAccessRulesProtected) throw new UnauthorizedAccessException("NetBird staging path inherits its DACL.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 && !Trusted.Contains(rule.IdentityReference.Value) && (rule.FileSystemRights & forbidden) != 0)
                throw new UnauthorizedAccessException("NetBird staging path grants mutation to an untrusted principal.");
    }
}

internal static class WindowsNetBirdPathSafety
{
    public static void AssertNoReparseComponents(string path)
    {
        var full = Path.GetFullPath(path); var root = Path.GetPathRoot(full) ?? throw new InvalidDataException("NetBird path has no root."); var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) { current = Path.Combine(current, part); if (File.Exists(current) || Directory.Exists(current)) if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("NetBird staging rejects reparse-point components."); }
    }
    public static void AssertHandleMatchesPath(SafeFileHandle handle, string expected)
    {
        if (handle.IsInvalid || handle.IsClosed) throw new ObjectDisposedException(nameof(handle));
        var tag = new FileAttributeTagInfo();
        if (!GetFileInformationByHandleEx(handle, 9, ref tag, (uint)Marshal.SizeOf<FileAttributeTagInfo>())) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not inspect NetBird MSI handle.");
        if ((tag.Attributes & FileAttributes.ReparsePoint) != 0 || tag.ReparseTag != 0) throw new InvalidDataException("NetBird MSI handle is a reparse point.");
        var actual = GetFinalPath(handle).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar), actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("NetBird MSI handle resolves to a different path.");
    }
    private static string GetFinalPath(SafeFileHandle handle)
    {
        var capacity = 512; while (true) { var text = new StringBuilder(capacity); var length = GetFinalPathNameByHandle(handle, text, (uint)text.Capacity, 0); if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve NetBird MSI handle."); if (length < text.Capacity) { var value = text.ToString(); return value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + value[8..] : value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ? value[4..] : value; } capacity = checked((int)length + 1); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileAttributeTagInfo { public FileAttributes Attributes; public uint ReparseTag; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int type, ref FileAttributeTagInfo info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder text, uint length, uint flags);
}
