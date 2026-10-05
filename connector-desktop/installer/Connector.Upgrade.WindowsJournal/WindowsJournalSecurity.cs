using System.Security.AccessControl;
using System.Security.Principal;

namespace Connector.Upgrade.WindowsJournal;

internal static class WindowsJournalSecurity
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TrustedInstallerSid =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    internal static WindowsJournalSecurityProfile ProductionProfile { get; } = new(
        AdministratorsSid,
        [SystemSid.Value, AdministratorsSid.Value],
        [SystemSid.Value, AdministratorsSid.Value, TrustedInstallerSid.Value],
        [SystemSid.Value, AdministratorsSid.Value],
        RequireElevatedCaller: true);

    public static void RequireTrustedElevatedCaller(WindowsJournalSecurityProfile profile)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The protected upgrade journal requires Windows.");

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        if (identity.User is { Value: var sid } && profile.TrustedOwnerSids.Contains(sid))
            return;
        if (profile.RequireElevatedCaller && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException(
                "The protected upgrade journal requires an elevated administrator or SYSTEM process.");
    }

    public static void ValidateProtectedDirectory(string path, WindowsJournalSecurityProfile profile)
    {
        AssertNoReparseComponents(path);
        ValidateSecurity(
            FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(path),
                AccessControlSections.Owner | AccessControlSections.Access),
            path,
            directory: true,
            profile);
    }

    public static void ValidateProtectedFile(string path, WindowsJournalSecurityProfile profile)
    {
        AssertNoReparseComponents(path);
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
            throw new FileNotFoundException("A protected upgrade journal file is missing.", path);
        ValidateSecurity(
            FileSystemAclExtensions.GetAccessControl(
                info,
                AccessControlSections.Owner | AccessControlSections.Access),
            path,
            directory: false,
            profile);
    }

    public static FileStream CreateProtectedFile(
        string path,
        FileAccess access,
        FileShare share,
        FileOptions options,
        WindowsJournalSecurityProfile profile)
    {
        var security = CreateProtectedFileAcl(profile);
        var rights = access switch
        {
            FileAccess.Read => FileSystemRights.Read,
            FileAccess.Write => FileSystemRights.Write,
            FileAccess.ReadWrite => FileSystemRights.Read | FileSystemRights.Write,
            _ => throw new ArgumentOutOfRangeException(nameof(access)),
        };
        return FileSystemAclExtensions.Create(
            new FileInfo(path),
            FileMode.CreateNew,
            rights,
            share,
            16 * 1024,
            options,
            security);
    }

    internal static FileSecurity CreateProtectedFileAcl(WindowsJournalSecurityProfile? profile = null)
    {
        profile ??= ProductionProfile;
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(profile.CreationOwnerSid);
        foreach (var sidValue in profile.RequiredFullControlSids)
        {
            var sid = new SecurityIdentifier(sidValue);
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }
        return security;
    }

    internal static DirectorySecurity CreateProtectedDirectoryAcl(WindowsJournalSecurityProfile? profile = null)
    {
        profile ??= ProductionProfile;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(profile.CreationOwnerSid);
        foreach (var sidValue in profile.RequiredFullControlSids)
        {
            var sid = new SecurityIdentifier(sidValue);
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        return security;
    }

    private static void ValidateSecurity(
        FileSystemSecurity security,
        string path,
        bool directory,
        WindowsJournalSecurityProfile profile)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !profile.TrustedOwnerSids.Contains(owner.Value))
            throw new UnauthorizedAccessException($"Protected upgrade journal path '{path}' has an untrusted owner.");
        if (!security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException($"Protected upgrade journal path '{path}' inherits its DACL.");

        var fullControlSids = new HashSet<string>(StringComparer.Ordinal);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     targetType: typeof(SecurityIdentifier)))
        {
            if (rule.IsInherited || rule.AccessControlType != AccessControlType.Allow ||
                !profile.TrustedAclSids.Contains(rule.IdentityReference.Value))
            {
                throw new UnauthorizedAccessException(
                    $"Protected upgrade journal path '{path}' has a non-canonical access rule.");
            }

            if (directory && (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
                throw new UnauthorizedAccessException(
                    $"Protected upgrade journal directory '{path}' has an inherit-only access rule.");

            var hasFullControl = (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
            if (hasFullControl)
                fullControlSids.Add(rule.IdentityReference.Value);
        }

        if (!profile.RequiredFullControlSids.All(fullControlSids.Contains))
            throw new UnauthorizedAccessException(
                $"Protected upgrade journal path '{path}' lacks a required trusted full-control rule.");
    }

    private static void AssertNoReparseComponents(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)
            ?? throw new InvalidDataException("The protected upgrade journal path has no filesystem root.");
        var current = root;
        foreach (var component in full[root.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;
            var info = File.Exists(current) ? (FileSystemInfo)new FileInfo(current) : new DirectoryInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
                throw new InvalidDataException("Protected upgrade journal paths cannot contain reparse points.");
        }
    }
}

internal sealed record WindowsJournalSecurityProfile(
    SecurityIdentifier CreationOwnerSid,
    HashSet<string> TrustedOwnerSids,
    HashSet<string> TrustedAclSids,
    HashSet<string> RequiredFullControlSids,
    bool RequireElevatedCaller)
{
    public WindowsJournalSecurityProfile(
        SecurityIdentifier creationOwnerSid,
        IEnumerable<string> trustedOwnerSids,
        IEnumerable<string> trustedAclSids,
        IEnumerable<string> requiredFullControlSids,
        bool RequireElevatedCaller)
        : this(
            creationOwnerSid,
            new HashSet<string>(trustedOwnerSids, StringComparer.Ordinal),
            new HashSet<string>(trustedAclSids, StringComparer.Ordinal),
            new HashSet<string>(requiredFullControlSids, StringComparer.Ordinal),
            RequireElevatedCaller)
    {
    }
}
