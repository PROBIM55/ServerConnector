using System.Security.AccessControl;
using System.Security.Principal;
using Connector.Upgrade.MachineAcl;

namespace Connector.Upgrade.WindowsPayload;

internal sealed class WindowsMachineStagingSecurity : IWindowsMachineStagingSecurity
{
    private static readonly SecurityIdentifier SystemSid = new("S-1-5-18");
    private static readonly SecurityIdentifier AdministratorsSid = new("S-1-5-32-544");
    private static readonly SecurityIdentifier TrustedInstallerSid =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private static readonly HashSet<string> TrustedSids = new(StringComparer.Ordinal)
    {
        SystemSid.Value,
        AdministratorsSid.Value,
        TrustedInstallerSid.Value,
    };

    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;
    private const FileSystemRights ProtectedEntryMutationRights =
        GenericAll |
        GenericWrite |
        FileSystemRights.WriteData |
        FileSystemRights.AppendData |
        FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

    private const FileSystemRights AncestorReplacementRights =
        GenericAll |
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

    private const InheritanceFlags TrustedDirectoryInheritance =
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    public string PrepareRoot(string requestedRoot)
    {
        var fullPath = Path.GetFullPath(requestedRoot);
        var machineRoot = ResolveMachineRoot(fullPath);
        EnsureElevatedTrustedInstallerContext();
        WindowsPathSafety.AssertNoReparseComponents(machineRoot);
        ValidateEntry(machineRoot, isDirectory: true, requireProtectedDacl: false, AncestorReplacementRights);

        var relative = Path.GetRelativePath(machineRoot, fullPath);
        if (relative is "." or ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Machine staging must be a strict descendant of an allowed machine root.");
        return WindowsSharedMachineDirectoryAcl.PreparePath(machineRoot, fullPath);
    }

    public void CreateProtectedDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Directory.GetParent(fullPath)?.FullName
            ?? throw new InvalidDataException("Protected staging directory has no parent.");
        ValidateProtectedDirectory(parent);
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
            throw new IOException($"Protected staging directory '{fullPath}' already exists.");
        CreateDirectoryWithProtectedAcl(fullPath);
        WindowsPathSafety.AssertNoReparseComponents(fullPath);
        ValidateProtectedDirectory(fullPath);
    }

    public void ProtectAndValidateFile(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        AddTrustedFileRules(security);
        FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
        ValidateProtectedFile(path);
    }

    public void ValidateProtectedDirectory(string path)
    {
        WindowsPathSafety.AssertNoReparseComponents(path);
        ValidateEntry(path, isDirectory: true, requireProtectedDacl: true, ProtectedEntryMutationRights);
    }

    public void ValidateSharedDirectory(string path)
    {
        WindowsPathSafety.AssertNoReparseComponents(path);
        var security = FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(path), AccessControlSections.Owner | AccessControlSections.Access);
        ValidateSharedDirectoryAcl(security);
    }

    internal static void ValidateSharedDirectoryAcl(FileSystemSecurity security) =>
        WindowsSharedMachineDirectoryAcl.Validate(security);

    public void ValidateProtectedFile(string path)
    {
        WindowsPathSafety.AssertNoReparseComponents(path);
        ValidateEntry(path, isDirectory: false, requireProtectedDacl: true, ProtectedEntryMutationRights);
    }

    /// <summary>
    /// Creates one rollback batch slot. Trusted machine principals retain full control; only the
    /// authenticated initiating SID receives explicit read/execute access to this directory.
    /// This profile is separate from the machine-only protected directory profile above.
    /// </summary>
    public void CreateRollbackBatchDirectory(string path, string initiatingUserSid)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Directory.GetParent(fullPath)?.FullName
            ?? throw new InvalidDataException("Rollback batch directory has no parent.");
        _ = PrepareRoot(parent);
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
            throw new IOException("Rollback batch directory already exists.");

        var security = CreateRollbackBatchDirectorySecurity(initiatingUserSid);
        FileSystemAclExtensions.Create(new DirectoryInfo(fullPath), security);
        WindowsPathSafety.AssertNoReparseComponents(fullPath);
        ValidateRollbackBatchDirectory(fullPath, initiatingUserSid);
    }

    public void ValidateRollbackBatchDirectory(string path, string initiatingUserSid)
    {
        WindowsPathSafety.AssertNoReparseComponents(path);
        var security = FileSystemAclExtensions.GetAccessControl(
            new DirectoryInfo(path), AccessControlSections.Owner | AccessControlSections.Access);
        ValidateRollbackBatchDirectoryAcl(path, security, initiatingUserSid);
    }

    /// <summary>Replaces a staged MSI DACL with the operation-specific read-only profile.</summary>
    public void ProtectAndValidateRollbackFile(string path, string initiatingUserSid)
    {
        EnsureElevatedTrustedInstallerContext();
        WindowsPathSafety.AssertNoReparseComponents(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("Rollback MSI must be a regular non-reparse file.");
        FileSystemAclExtensions.SetAccessControl(
            new FileInfo(path), CreateRollbackPayloadFileSecurity(initiatingUserSid));
        ValidateRollbackFile(path, initiatingUserSid);
    }

    public void ValidateRollbackFile(string path, string initiatingUserSid)
    {
        WindowsPathSafety.AssertNoReparseComponents(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("Rollback MSI must be a regular non-reparse file.");
        var security = FileSystemAclExtensions.GetAccessControl(
            new FileInfo(path), AccessControlSections.Owner | AccessControlSections.Access);
        ValidateRollbackPayloadFileAcl(path, security, initiatingUserSid);
    }

    internal static DirectorySecurity CreateRollbackBatchDirectorySecurity(string initiatingUserSid)
    {
        var initiatingSid = ParseInitiatingSid(initiatingUserSid);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        AddTrustedDirectoryRules(security);
        security.AddAccessRule(new FileSystemAccessRule(
            initiatingSid,
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    internal static FileSecurity CreateRollbackPayloadFileSecurity(string initiatingUserSid)
    {
        var initiatingSid = ParseInitiatingSid(initiatingUserSid);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        AddTrustedFileRules(security);
        security.AddAccessRule(new FileSystemAccessRule(
            initiatingSid,
            FileSystemRights.Read,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    internal static void ValidateRollbackBatchDirectoryAcl(
        string path, DirectorySecurity security, string initiatingUserSid) =>
        ValidateRollbackAcl(path, security, initiatingUserSid, isDirectory: true);

    internal static void ValidateRollbackPayloadFileAcl(
        string path, FileSecurity security, string initiatingUserSid) =>
        ValidateRollbackAcl(path, security, initiatingUserSid, isDirectory: false);

    private static void ValidateRollbackAcl(
        string path, FileSystemSecurity security, string initiatingUserSid, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(security);
        var initiatingSid = ParseInitiatingSid(initiatingUserSid);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || owner.Value != AdministratorsSid.Value)
            throw new UnauthorizedAccessException($"Rollback path '{path}' has an untrusted owner.");
        if (!security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException($"Rollback path '{path}' inherits its DACL.");

        var expected = new Dictionary<string, (FileSystemRights Rights, InheritanceFlags Inheritance)>(StringComparer.Ordinal)
        {
            [SystemSid.Value] = (FileSystemRights.FullControl, isDirectory ? TrustedDirectoryInheritance : InheritanceFlags.None),
            [AdministratorsSid.Value] = (FileSystemRights.FullControl, isDirectory ? TrustedDirectoryInheritance : InheritanceFlags.None),
            [TrustedInstallerSid.Value] = (FileSystemRights.FullControl, isDirectory ? TrustedDirectoryInheritance : InheritanceFlags.None),
            [initiatingSid.Value] = (
                isDirectory
                    ? FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize
                    : FileSystemRights.Read | FileSystemRights.Synchronize,
                InheritanceFlags.None),
        };
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        if (rules.Length != expected.Count)
            throw new UnauthorizedAccessException($"Rollback path '{path}' has an unexpected ACL entry.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            var sid = ((SecurityIdentifier)rule.IdentityReference).Value;
            if (!expected.TryGetValue(sid, out var grant) || !seen.Add(sid) ||
                rule.AccessControlType != AccessControlType.Allow || rule.IsInherited ||
                rule.FileSystemRights != grant.Rights || rule.InheritanceFlags != grant.Inheritance ||
                rule.PropagationFlags != PropagationFlags.None)
                throw new UnauthorizedAccessException($"Rollback path '{path}' has an unsafe ACL entry.");
        }
    }

    private static SecurityIdentifier ParseInitiatingSid(string initiatingUserSid)
    {
        if (string.IsNullOrWhiteSpace(initiatingUserSid))
            throw new ArgumentException("An authenticated initiating user SID is required.", nameof(initiatingUserSid));
        SecurityIdentifier sid;
        try { sid = new SecurityIdentifier(initiatingUserSid); }
        catch (ArgumentException error)
        {
            throw new ArgumentException("The initiating user SID is invalid.", nameof(initiatingUserSid), error);
        }
        // Trust contract: callers must pass only the SID extracted from WindowsIdentity.User
        // by WindowsOperationRollbackPayloadStager after matching it to the SID authenticated
        // by the helper session. Never pass a SID string from IPC/request data directly. The
        // syntactic checks below are defense in depth; they cannot distinguish every domain
        // group from an account SID, so token-backed provenance is mandatory. Otherwise a group
        // could make the private MSI readable by its members.
        if (TrustedSids.Contains(sid.Value) ||
            (sid.AccountDomainSid is null && !sid.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal)))
            throw new ArgumentException("The initiating SID must identify an individual user account.", nameof(initiatingUserSid));
        return sid;
    }

    private static void EnsureElevatedTrustedInstallerContext()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Protected rollback staging is available only on Windows.");
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var userSid = identity.User?.Value;
        if (userSid is not null && TrustedSids.Contains(userSid))
            return;
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Protected rollback staging requires an elevated administrator, SYSTEM, or TrustedInstaller process.");
    }

    private static string ResolveMachineRoot(string candidate)
    {
        var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path.Length);

        return roots.FirstOrDefault(root => IsStrictDescendant(candidate, root))
            ?? throw new InvalidDataException("Rollback staging must be below ProgramData or Program Files.");
    }

    private static bool IsStrictDescendant(string candidate, string root) =>
        candidate.StartsWith(
            root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void CreateDirectoryWithProtectedAcl(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        AddTrustedDirectoryRules(security);
        FileSystemAclExtensions.Create(new DirectoryInfo(path), security);
    }

    private static void AddTrustedDirectoryRules(DirectorySecurity security)
    {
        foreach (var sid in new[] { SystemSid, AdministratorsSid, TrustedInstallerSid })
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                TrustedDirectoryInheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
    }

    private static void AddTrustedFileRules(FileSecurity security)
    {
        foreach (var sid in new[] { SystemSid, AdministratorsSid, TrustedInstallerSid })
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
    }

    internal static void ValidateEntry(
        string path,
        bool isDirectory,
        bool requireProtectedDacl,
        FileSystemRights forbiddenUntrustedRights)
    {
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(path),
                AccessControlSections.Owner | AccessControlSections.Access)
            : FileSystemAclExtensions.GetAccessControl(
                new FileInfo(path),
                AccessControlSections.Owner | AccessControlSections.Access);

        if (requireProtectedDacl)
        {
            ValidateProtectedAcl(path, security);
            return;
        }

        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !TrustedSids.Contains(owner.Value))
            throw new UnauthorizedAccessException($"Protected rollback path '{path}' has an untrusted owner.");
        if (requireProtectedDacl && !security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException($"Protected rollback path '{path}' inherits its DACL.");

        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     targetType: typeof(SecurityIdentifier)))
        {
            if (requireProtectedDacl && rule.AccessControlType != AccessControlType.Allow)
                throw new UnauthorizedAccessException($"Protected rollback path '{path}' contains a deny rule.");
            if (TrustedSids.Contains(rule.IdentityReference.Value))
                continue;
            if (requireProtectedDacl)
                throw new UnauthorizedAccessException($"Protected rollback path '{path}' grants an untrusted principal access.");
            if (rule.AccessControlType != AccessControlType.Allow ||
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
                continue;
            if ((rule.FileSystemRights & forbiddenUntrustedRights) != 0)
                throw new UnauthorizedAccessException($"Protected rollback path '{path}' grants mutation rights to an untrusted principal.");
        }
    }

    internal static void ValidateProtectedAcl(string path, FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !TrustedSids.Contains(owner.Value))
            throw new UnauthorizedAccessException($"Protected rollback path '{path}' has an untrusted owner.");
        if (!security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException($"Protected rollback path '{path}' inherits its DACL.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || !TrustedSids.Contains(rule.IdentityReference.Value))
                throw new UnauthorizedAccessException($"Protected rollback path '{path}' grants or denies an untrusted principal.");
        }
    }
}
