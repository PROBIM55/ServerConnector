using System.Security.AccessControl;
using System.Security.Principal;

namespace Connector.Upgrade.MachineAcl;

/// <summary>Canonical ACL contract for shared machine staging ancestors.</summary>
public static class WindowsSharedMachineDirectoryAcl
{
    public static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    public static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    public static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    public static readonly SecurityIdentifier TrustedInstallerSid =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private const FileSystemRights MutationRights = (FileSystemRights)0x10000000 | (FileSystemRights)0x40000000 |
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    public static DirectorySecurity Create()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        AddMachineRule(security, SystemSid);
        AddMachineRule(security, AdministratorsSid);
        AddMachineRule(security, TrustedInstallerSid);
        security.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>Strictly validates the canonical ACL used after creation or trusted migration.</summary>
    public static void Validate(FileSystemSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        ValidateOwnerAndDacl(security);
        var rules = GetRules(security);
        var machine = new HashSet<string>(StringComparer.Ordinal) { SystemSid.Value, AdministratorsSid.Value };
        var full = new HashSet<string>(StringComparer.Ordinal);
        var usersRead = false;
        foreach (var rule in rules)
        {
            ValidateCanonicalRule(rule);
            var sid = ((SecurityIdentifier)rule.IdentityReference).Value;
            if (sid == UsersSid.Value)
            {
                if (rule.AccessControlType != AccessControlType.Allow || !OnlyReadExecute(rule.FileSystemRights))
                    throw new UnauthorizedAccessException("BUILTIN\\Users may only read and execute shared machine directories.");
                usersRead = true;
                continue;
            }
            if (sid == TrustedInstallerSid.Value)
            {
                if (rule.AccessControlType != AccessControlType.Allow ||
                    (rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
                    throw new UnauthorizedAccessException("TrustedInstaller may only have full control on shared machine directories.");
                continue;
            }
            if (!machine.Contains(sid) || rule.AccessControlType != AccessControlType.Allow ||
                (rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
                throw new UnauthorizedAccessException("Shared machine directory ACL contains an untrusted principal or grant.");
            full.Add(sid);
        }
        if (!full.SetEquals(machine) || !usersRead)
            throw new UnauthorizedAccessException("Shared machine directory ACL lacks SYSTEM, Administrators, or BUILTIN\\Users access.");
    }

    /// <summary>
    /// Validates a legacy shared ACL before migration. Only trusted machine principals and
    /// individual account SIDs with read/execute are accepted; migration then replaces all
    /// account-specific grants with BUILTIN\\Users read/execute.
    /// </summary>
    public static void ValidateMigratable(FileSystemSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        ValidateOwnerAndDacl(security);
        var machine = new HashSet<string>(StringComparer.Ordinal)
        {
            SystemSid.Value, AdministratorsSid.Value, TrustedInstallerSid.Value,
        };
        var full = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in GetRules(security))
        {
            ValidateCanonicalRule(rule);
            var sid = ((SecurityIdentifier)rule.IdentityReference).Value;
            if (rule.AccessControlType != AccessControlType.Allow)
                throw new UnauthorizedAccessException("A legacy shared directory contains a deny rule.");
            if (machine.Contains(sid))
            {
                if ((rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
                    throw new UnauthorizedAccessException("A trusted machine principal lacks full control on a shared directory.");
                full.Add(sid);
                continue;
            }
            if (sid == UsersSid.Value)
            {
                if (!OnlyReadExecute(rule.FileSystemRights))
                    throw new UnauthorizedAccessException("BUILTIN\\Users has a non-read grant on a shared directory.");
                continue;
            }
            var accountSid = new SecurityIdentifier(sid);
            if ((accountSid.AccountDomainSid is null && !sid.StartsWith("S-1-12-1-", StringComparison.Ordinal)) ||
                !OnlyReadExecute(rule.FileSystemRights))
                throw new UnauthorizedAccessException("A legacy shared directory contains an unsafe non-machine grant.");
        }
        if (!full.Contains(SystemSid.Value) || !full.Contains(AdministratorsSid.Value))
            throw new UnauthorizedAccessException("A legacy shared directory lacks SYSTEM or Administrators full control.");
    }

    /// <summary>
    /// Safely creates or migrates a shared path below machineRoot. It preflights every existing
    /// descendant and its ACL before changing any existing ACL or creating a missing component.
    /// </summary>
    public static string PreparePath(string machineRoot, string requestedPath)
    {
        var root = Path.GetFullPath(machineRoot);
        var full = Path.GetFullPath(requestedPath);
        var relative = Path.GetRelativePath(root, full);
        if (relative is "." or ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Shared machine staging must be a strict descendant of its trusted root.");
        AssertNoReparse(root);
        if (!Directory.Exists(root) || File.Exists(root)) throw new DirectoryNotFoundException("The trusted machine root is unavailable.");
        var paths = new List<string>();
        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (File.Exists(current)) throw new InvalidDataException("A shared machine path component is a file.");
            if (Directory.Exists(current))
            {
                AssertNoReparse(current);
                ValidateMigratable(new DirectoryInfo(current).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
            }
            paths.Add(current);
        }

        foreach (var path in paths)
        {
            AssertNoReparse(path);
            if (!Directory.Exists(path))
                FileSystemAclExtensions.Create(new DirectoryInfo(path), Create());
            else
            {
                var directory = new DirectoryInfo(path);
                var existingAcl = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
                try { Validate(existingAcl); }
                catch (UnauthorizedAccessException)
                {
                    // The entire existing path was already checked by the read-only preflight.
                    // Only a recognized legacy ACL reaches this replacement.
                    ValidateMigratable(existingAcl);
                    FileSystemAclExtensions.SetAccessControl(directory, Create());
                }
            }
            AssertNoReparse(path);
            Validate(new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
        }
        return full;
    }

    private static void AddMachineRule(DirectorySecurity security, SecurityIdentifier sid) =>
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));

    private static FileSystemAccessRule[] GetRules(FileSystemSecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToArray();

    private static void ValidateOwnerAndDacl(FileSystemSecurity security)
    {
        if (!security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("Shared machine directory inherits its DACL.");
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || owner.Value != AdministratorsSid.Value)
            throw new UnauthorizedAccessException("Shared machine directory owner must be BUILTIN\\Administrators.");
    }

    private static void ValidateCanonicalRule(FileSystemAccessRule rule)
    {
        if (rule.IsInherited || rule.InheritanceFlags != InheritanceFlags.None || rule.PropagationFlags != PropagationFlags.None)
            throw new UnauthorizedAccessException("Shared machine directory ACL contains inherited or propagating rules.");
    }

    private static bool OnlyReadExecute(FileSystemRights rights) =>
        (rights & MutationRights) == 0 &&
        (rights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute &&
        (rights & ~(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)) == 0;

    private static void AssertNoReparse(string path)
    {
        var full = Path.GetFullPath(path);
        var driveRoot = Path.GetPathRoot(full) ?? throw new InvalidDataException("Machine path has no volume root.");
        var current = driveRoot;
        foreach (var part in full[driveRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Directory.Exists(current) && !File.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Shared machine staging path contains a reparse point.");
        }
    }
}
