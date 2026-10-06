using System.Security.AccessControl;
using System.Security.Principal;

namespace Connector.JobModules;

/// <summary>
/// Gives native converters a short, private working directory independent of
/// user-selected output paths. A profile-owned local root bounds CreateProcess
/// working-directory length without trusting shared temp ACLs or shortening the
/// caller's output path. Only this executor uses this root; legacy AGR conversion
/// keeps its existing work-root contract.
/// </summary>
internal sealed class NativeConverterWorkspace : IDisposable
{
    private const int MaxWorkingDirectoryLength = 240;
    private readonly string _root;
    private readonly string _jobPath;
    private readonly SecurityIdentifier _user;
    private bool _disposed;

    public string WorkRoot => _jobPath;

    private NativeConverterWorkspace(string root, string jobPath, SecurityIdentifier user)
    {
        _root = root;
        _jobPath = jobPath;
        _user = user;
    }

    public static NativeConverterWorkspace Create(string? configuredRoot = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A private native converter workspace requires Windows ACLs.");

        var profile = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (string.IsNullOrWhiteSpace(profile)) throw new IOException("Native workspace profile is unavailable.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot ?? Path.Combine(profile, ".structura-connector", "work")));
        if (IsUnc(root) || !IsWithin(profile, root) || string.Equals(root, profile, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Native workspace root must be local to the user profile.");

        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("Native workspace user identity is unavailable.");
        EnsureDirectoryChain(root, user);

        var job = Path.Combine(root, "job-" + Guid.NewGuid().ToString("N"));
        if (job.Length + 48 > MaxWorkingDirectoryLength)
            throw new IOException("Native workspace path exceeds the safe working-directory budget.");
        CreatePrivateDirectory(job, user);
        try
        {
            AssertPrivateDirectory(job, user);
            return new NativeConverterWorkspace(root, job, user);
        }
        catch
        {
            // The path was just created by this call and has not been handed out.
            Directory.Delete(job, recursive: false);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!Directory.Exists(_jobPath)) return;
        AssertNoReparseChain(_root, _jobPath);
        AssertPrivateDirectory(_jobPath, _user);
        if (!string.Equals(Path.GetDirectoryName(_jobPath), _root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(_jobPath).StartsWith("job-", StringComparison.Ordinal))
            throw new IOException("Refusing to clean a workspace outside its owned job directory.");
        AssertNoReparseTree(_jobPath);
        Directory.Delete(_jobPath, recursive: true);
    }

    private static void EnsureDirectoryChain(string root, SecurityIdentifier user)
    {
        var profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        AssertSafeExistingAncestors(profile, user);

        var relative = Path.GetRelativePath(profile, root);
        var current = profile;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (component.Length == 0 || component == ".") continue;
            if (component == "..") throw new IOException("Native workspace escapes the user profile.");
            current = Path.Combine(current, component);
            if (Directory.Exists(current))
            {
                AssertNoReparse(current);
                AssertPrivateDirectory(current, user);
            }
            else
            {
                CreatePrivateDirectory(current, user);
                AssertPrivateDirectory(current, user);
            }
        }
    }

    private static void AssertSafeExistingAncestors(string profile, SecurityIdentifier user)
    {
        var current = Path.GetPathRoot(profile) ?? throw new IOException("Native workspace profile has no volume root.");
        AssertNoReparse(current);
        AssertNoUntrustedWriter(current, user);
        foreach (var component in profile[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            AssertNoReparse(current);
            AssertNoUntrustedWriter(current, user);
        }
    }

    private static void CreatePrivateDirectory(string path, SecurityIdentifier user)
    {
        if (File.Exists(path)) throw new IOException("Native workspace path is not a directory.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        AddFullControl(security, user);
        new DirectoryInfo(path).Create(security);
        AssertNoReparse(path);
    }

    private static void AddFullControl(DirectorySecurity security, SecurityIdentifier sid) =>
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));

    private static void AssertPrivateDirectory(string path, SecurityIdentifier user)
    {
        AssertNoReparse(path);
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!security.AreAccessRulesProtected) throw new IOException("Native workspace directory ACL is inheriting.");
        if (!user.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw new IOException("Native workspace directory owner is not the current user.");
        AssertNonNullDacl(security);
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value!,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value!,
            user.Value!
        };
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (rules.Length != 3 || rules.Any(r => r.AccessControlType != AccessControlType.Allow ||
                (r.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl ||
                r.InheritanceFlags != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) ||
                r.PropagationFlags != PropagationFlags.None || !expected.Remove(((SecurityIdentifier)r.IdentityReference).Value!)) || expected.Count != 0)
            throw new IOException("Native workspace directory ACL does not match the private policy.");
    }

    private static void AssertNoUntrustedWriter(string path, SecurityIdentifier user)
    {
        if (File.Exists(path)) throw new IOException("Native workspace ancestor is not a directory.");
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        AssertNonNullDacl(security);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new IOException("Native workspace ancestor owner is unavailable.");
        var trustedInstaller = new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
        var trustedOwner = owner.Equals(user) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.Equals(trustedInstaller);
        if (!trustedOwner) throw new IOException("Native workspace ancestor has an untrusted owner.");

        const FileSystemRights dangerousRights = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
            (FileSystemRights)0x40000000 | (FileSystemRights)0x10000000;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (sid.Equals(user) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) continue;
            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
            if ((rule.FileSystemRights & dangerousRights) != 0)
                throw new IOException("Native workspace ancestor grants delete or ACL-changing access to an untrusted identity.");
        }
    }

    private static void AssertNonNullDacl(DirectorySecurity security)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null) throw new IOException("Native workspace ACL is null.");
    }

    private static void AssertNoReparseChain(string root, string leaf)
    {
        var current = Path.GetPathRoot(leaf) ?? throw new IOException("Native workspace path has no volume root.");
        foreach (var component in leaf[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current)) break;
            AssertNoReparse(current);
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) return;
        }
    }

    private static void AssertNoReparseTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            AssertNoReparse(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Native workspace contains a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }

    private static void AssertNoReparse(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Native workspace path contains a reparse point.");
    }

    private static bool IsUnc(string path) => path.StartsWith("\\\\", StringComparison.Ordinal);
    private static bool IsWithin(string parent, string child)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(child));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
