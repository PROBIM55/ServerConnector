using System.Security.AccessControl;
using System.Security.Principal;
using Connector.Upgrade.MachineAcl;
using Connector.Upgrade.UserState;

namespace Connector.Upgrade.WindowsUserStage;

public sealed class WindowsProtectedUserStateStageFactory
{
    private readonly IWindowsUserStagePlatform _platform;

    public WindowsProtectedUserStateStageFactory()
        : this(new WindowsUserStagePlatform())
    {
    }

    internal WindowsProtectedUserStateStageFactory(IWindowsUserStagePlatform platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    /// <summary>Creates an empty stage for the initiating SID supplied by the authenticated helper session.</summary>
    public WindowsProtectedUserStateStage CreateForCurrentUser(string initiatingUserSid, string operationId) =>
        CreateForInitiatingUser(initiatingUserSid, operationId);

    /// <summary>
    /// Creates a new empty stage for an authenticated initiating SID. The helper must already be
    /// elevated; that helper token may belong to another account after over-the-shoulder UAC.
    /// This API accepts no user-selected paths or snapshot data.
    /// </summary>
    public WindowsProtectedUserStateStage CreateForInitiatingUser(string initiatingUserSid, string operationId)
    {
        var ownerSid = WindowsUserStageLayout.RequireCurrentUserSid(initiatingUserSid);
        _platform.AssertElevatedAdministrator();
        var layout = WindowsUserStageLayout.CreateForMachineStage(_platform, ownerSid, operationId);
        layout.AssertSafeMachineStage(_platform);

        var stageExists = _platform.DirectoryExists(layout.StageRoot);
        if (_platform.FileExists(layout.StageRoot))
            throw new UserStateStageNeedsManualRecoveryException(layout.StageRoot,
                new InvalidDataException("The user-state stage path exists as a file."));
        ValidateExistingAncestors(layout, ownerSid);
        EnsureSharedDirectory(layout.InstallerRoot);
        EnsureSharedDirectory(layout.ModuleRoot);

        if (stageExists)
        {
            try
            {
                var existing = new WindowsProtectedUserStateStage(_platform, ownerSid, layout.OperationId, layout.StageRoot);
                existing.AssertProtectedForMachine();
                if (!_platform.IsDirectoryEmpty(layout.StageRoot))
                    throw new InvalidDataException("An existing user-state stage is not empty and cannot be replaced or resumed as empty.");
                return existing;
            }
            catch (Exception error)
            {
                throw new UserStateStageNeedsManualRecoveryException(layout.StageRoot, error);
            }
        }

        EnsureUserDirectory(layout.UserRoot, ownerSid);

        _platform.CreateDirectory(layout.StageRoot, WindowsUserStageSecurity.CreateUserAcl(ownerSid));
        try
        {
            var stage = new WindowsProtectedUserStateStage(_platform, ownerSid, layout.OperationId, layout.StageRoot);
            stage.AssertProtectedForMachine();
            return stage;
        }
        catch (Exception error)
        {
            // Do not delete a path whose post-create owner, ACL or reparse identity could not be
            // attested. It contains no snapshot yet; a trusted installer may inspect/recover it.
            throw new UserStateStageNeedsManualRecoveryException(layout.StageRoot, error);
        }
    }

    /// <summary>Opens an existing stage for the initiating SID without changing the filesystem.</summary>
    public WindowsProtectedUserStateStage OpenExistingForCurrentUser(string initiatingUserSid, string operationId)
    {
        var expectedSid = WindowsUserStageLayout.RequireCurrentUserSid(initiatingUserSid);
        var ownerSid = WindowsUserStageLayout.RequireCurrentUserSid(_platform.GetCurrentUserSid());
        if (!string.Equals(expectedSid, ownerSid, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The elevated Windows user is not the initiating user.");
        var layout = WindowsUserStageLayout.Create(_platform, ownerSid, operationId);
        var stage = new WindowsProtectedUserStateStage(_platform, ownerSid, layout.OperationId, layout.StageRoot);
        stage.AssertProtectedForUserAsync(ownerSid, CancellationToken.None).GetAwaiter().GetResult();
        return stage;
    }

    private void EnsureSharedDirectory(string path)
    {
        if (_platform.FileExists(path))
            throw new InvalidDataException($"Protected stage ancestor '{path}' is not a directory.");
        if (!_platform.DirectoryExists(path))
            _platform.CreateDirectory(path, WindowsUserStageSecurity.CreateSharedAcl());

        _platform.AssertNoReparseAncestors(path);
        var security = _platform.ReadDirectorySecurity(path);
        if (!WindowsUserStageSecurity.IsCanonicalSharedDirectory(security))
        {
            WindowsUserStageSecurity.ValidateMigratableSharedDirectory(path, security);
            _platform.ProtectSharedDirectory(path);
            security = _platform.ReadDirectorySecurity(path);
        }
        WindowsUserStageSecurity.ValidateSharedDirectory(path, security);
    }

    private void EnsureUserDirectory(string path, string ownerSid)
    {
        if (_platform.FileExists(path))
            throw new InvalidDataException($"Protected user stage ancestor '{path}' is not a directory.");
        if (!_platform.DirectoryExists(path))
            _platform.CreateDirectory(path, WindowsUserStageSecurity.CreateUserAcl(ownerSid));

        _platform.AssertNoReparseAncestors(path);
        WindowsUserStageSecurity.ValidateUserDirectory(path, ownerSid, _platform.ReadDirectorySecurity(path));
    }

    private void ValidateExistingAncestors(WindowsUserStageLayout layout, string ownerSid)
    {
        foreach (var path in new[] { layout.InstallerRoot, layout.ModuleRoot })
        {
            if (_platform.FileExists(path))
                throw new InvalidDataException($"Protected stage ancestor '{path}' is not a directory.");
            if (!_platform.DirectoryExists(path)) continue;
            _platform.AssertNoReparseAncestors(path);
            WindowsUserStageSecurity.ValidateMigratableSharedDirectory(path, _platform.ReadDirectorySecurity(path));
        }

        if (_platform.FileExists(layout.UserRoot))
            throw new InvalidDataException($"Protected user stage ancestor '{layout.UserRoot}' is not a directory.");
        if (_platform.DirectoryExists(layout.UserRoot))
        {
            _platform.AssertNoReparseAncestors(layout.UserRoot);
            WindowsUserStageSecurity.ValidateUserDirectory(
                layout.UserRoot, ownerSid, _platform.ReadDirectorySecurity(layout.UserRoot));
        }
    }
}

/// <summary>
/// A newly-created empty stage failed post-create attestation. Its path must be retained for
/// explicit trusted inspection; deleting an unverified path could follow a replacement/reparse.
/// </summary>
public sealed class UserStateStageNeedsManualRecoveryException : IOException
{
    public UserStateStageNeedsManualRecoveryException(string stageRoot, Exception innerException)
        : base("New user-state stage failed attestation and was retained for manual recovery.", innerException)
    {
        StageRoot = stageRoot;
    }

    public string StageRoot { get; }
}

public sealed class WindowsProtectedUserStateStage : IProtectedUserStateStage
{
    private readonly IWindowsUserStagePlatform _platform;
    private readonly string _ownerSid;
    private readonly string _operationId;

    internal WindowsProtectedUserStateStage(
        IWindowsUserStagePlatform platform,
        string ownerSid,
        string operationId,
        string rootPath)
    {
        _platform = platform;
        _ownerSid = ownerSid;
        _operationId = operationId;
        RootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath { get; }

    public ValueTask AssertProtectedForUserAsync(string expectedUserSid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedExpectedSid = WindowsUserStageLayout.RequireCurrentUserSid(expectedUserSid);
        if (!string.Equals(normalizedExpectedSid, _ownerSid, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The protected stage belongs to a different Windows user SID.");

        var currentSid = WindowsUserStageLayout.RequireCurrentUserSid(_platform.GetCurrentUserSid());
        if (!string.Equals(currentSid, _ownerSid, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The current Windows token does not own this protected stage.");

        var currentLayout = WindowsUserStageLayout.Create(_platform, currentSid, _operationId);
        if (!WindowsUserStageLayout.PathEquals(RootPath, currentLayout.StageRoot))
            throw new UnauthorizedAccessException("The protected stage path no longer belongs to the current user and operation.");

        currentLayout.AssertSafeLocation(_platform);
        WindowsUserStageSecurity.ValidateSharedDirectory(
            currentLayout.InstallerRoot, _platform.ReadDirectorySecurity(currentLayout.InstallerRoot));
        WindowsUserStageSecurity.ValidateSharedDirectory(
            currentLayout.ModuleRoot, _platform.ReadDirectorySecurity(currentLayout.ModuleRoot));
        WindowsUserStageSecurity.ValidateUserDirectory(
            currentLayout.UserRoot, currentSid, _platform.ReadDirectorySecurity(currentLayout.UserRoot));
        if (!_platform.DirectoryExists(RootPath) || _platform.FileExists(RootPath))
            throw new DirectoryNotFoundException("The protected user-state stage does not exist as a directory.");

        _platform.AssertNoReparseAncestors(RootPath);
        WindowsUserStageSecurity.ValidateUserDirectory(
            RootPath,
            currentSid,
            _platform.ReadDirectorySecurity(RootPath));
        return ValueTask.CompletedTask;
    }

    internal void AssertProtectedForMachine()
    {
        var layout = WindowsUserStageLayout.CreateForMachineStage(_platform, _ownerSid, _operationId);
        layout.AssertSafeMachineStage(_platform);
        WindowsUserStageSecurity.ValidateSharedDirectory(
            layout.InstallerRoot, _platform.ReadDirectorySecurity(layout.InstallerRoot));
        WindowsUserStageSecurity.ValidateSharedDirectory(
            layout.ModuleRoot, _platform.ReadDirectorySecurity(layout.ModuleRoot));
        WindowsUserStageSecurity.ValidateUserDirectory(
            layout.UserRoot, _ownerSid, _platform.ReadDirectorySecurity(layout.UserRoot));
        if (!_platform.DirectoryExists(RootPath) || _platform.FileExists(RootPath))
            throw new DirectoryNotFoundException("The protected user-state stage does not exist as a directory.");
        _platform.AssertNoReparseAncestors(RootPath);
        WindowsUserStageSecurity.ValidateUserDirectory(
            RootPath, _ownerSid, _platform.ReadDirectorySecurity(RootPath));
    }
}

internal sealed record WindowsUserStageAccessRule(
    string Sid,
    FileSystemRights Rights,
    AccessControlType AccessType,
    bool IsInherited,
    InheritanceFlags InheritanceFlags,
    PropagationFlags PropagationFlags);

internal sealed record WindowsUserStageDirectorySecurity(
    string OwnerSid,
    bool IsDaclProtected,
    IReadOnlyList<WindowsUserStageAccessRule> Rules);

internal static class WindowsUserStageSecurity
{
    internal const string SystemSid = "S-1-5-18";
    internal const string AdministratorsSid = "S-1-5-32-544";
    internal const string BuiltinUsersSid = "S-1-5-32-545";
    private const string TrustedInstallerSid =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;
    private const FileSystemRights MutationRights =
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

    internal static WindowsUserStageDirectorySecurity CreateMachineAcl() =>
        CreateAcl([SystemSid, AdministratorsSid]);

    internal static WindowsUserStageDirectorySecurity CreateSharedAcl() =>
        new(
            AdministratorsSid,
            IsDaclProtected: true,
            [
                new WindowsUserStageAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow,
                    IsInherited: false, InheritanceFlags.None, PropagationFlags.None),
                new WindowsUserStageAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow,
                    IsInherited: false, InheritanceFlags.None, PropagationFlags.None),
                new WindowsUserStageAccessRule(BuiltinUsersSid, FileSystemRights.ReadAndExecute, AccessControlType.Allow,
                    IsInherited: false, InheritanceFlags.None, PropagationFlags.None),
            ]);

    internal static WindowsUserStageDirectorySecurity CreateUserAcl(string ownerSid) =>
        CreateAcl([ownerSid, SystemSid, AdministratorsSid]);

    internal static void ValidateMachineDirectory(string path, WindowsUserStageDirectorySecurity security)
    {
        var trusted = new HashSet<string>(StringComparer.Ordinal)
        {
            SystemSid,
            AdministratorsSid,
            TrustedInstallerSid,
        };
        Validate(path, security, trusted, [SystemSid, AdministratorsSid], exactPrincipalsOnly: false);
    }

    internal static void ValidateSharedDirectory(
        string path,
        WindowsUserStageDirectorySecurity security)
    {
        ValidateSharedModel(path, security, allowLegacyAccountReaders: false);
    }

    internal static void ValidateMigratableSharedDirectory(
        string path,
        WindowsUserStageDirectorySecurity security) =>
        ValidateSharedModel(path, security, allowLegacyAccountReaders: true);

    internal static bool IsCanonicalSharedDirectory(WindowsUserStageDirectorySecurity security)
    {
        try { ValidateSharedDirectory("shared path", security); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void ValidateSharedModel(
        string path,
        WindowsUserStageDirectorySecurity security,
        bool allowLegacyAccountReaders)
    {
        ValidateOwnerAndDacl(path, security);
        var fullControl = new HashSet<string>(StringComparer.Ordinal);
        var usersReadExecute = false;
        foreach (var rule in security.Rules)
        {
            if (rule.IsInherited || rule.InheritanceFlags != InheritanceFlags.None || rule.PropagationFlags != PropagationFlags.None ||
                rule.AccessType != AccessControlType.Allow)
                throw new UnauthorizedAccessException($"Shared protected path '{path}' contains an inherited, deny, or non-canonical rule.");

            if (rule.Sid is SystemSid or AdministratorsSid)
            {
                if ((rule.Rights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
                    throw new UnauthorizedAccessException($"Shared protected path '{path}' lacks machine full-control access.");
                fullControl.Add(rule.Sid);
                continue;
            }
            if (rule.Sid == TrustedInstallerSid)
            {
                if ((rule.Rights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
                    throw new UnauthorizedAccessException($"Shared protected path '{path}' grants an incomplete TrustedInstaller rule.");
                continue;
            }
            if (rule.Sid == BuiltinUsersSid)
            {
                if (!IsReadExecuteOnly(rule.Rights))
                    throw new UnauthorizedAccessException($"Shared protected path '{path}' grants BUILTIN\\Users more than read and execute.");
                usersReadExecute = true;
                continue;
            }
            if (allowLegacyAccountReaders && IsAccountUserSid(rule.Sid) && IsReadExecuteOnly(rule.Rights))
                continue;
            throw new UnauthorizedAccessException($"Shared protected path '{path}' grants access to an unexpected principal.");
        }

        if (!fullControl.Contains(SystemSid) || !fullControl.Contains(AdministratorsSid))
            throw new UnauthorizedAccessException($"Shared protected path '{path}' lacks SYSTEM or Administrators full control.");
        if (!allowLegacyAccountReaders && !usersReadExecute)
            throw new UnauthorizedAccessException($"Shared protected path '{path}' lacks BUILTIN\\Users read and execute.");
    }

    private static bool IsReadExecuteOnly(FileSystemRights rights) =>
        (rights & MutationRights) == 0 &&
        (rights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute &&
        (rights & ~(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)) == 0;

    private static bool IsAccountUserSid(string value)
    {
        try
        {
            var sid = new SecurityIdentifier(value);
            return sid.AccountDomainSid is not null || value.StartsWith("S-1-12-1-", StringComparison.Ordinal);
        }
        catch (ArgumentException) { return false; }
    }

    internal static void ValidateUserDirectory(
        string path,
        string ownerSid,
        WindowsUserStageDirectorySecurity security)
    {
        var trusted = new HashSet<string>(StringComparer.Ordinal)
        {
            ownerSid,
            SystemSid,
            AdministratorsSid,
        };
        Validate(path, security, trusted, trusted, exactPrincipalsOnly: true);
    }

    private static WindowsUserStageDirectorySecurity CreateAcl(IReadOnlyList<string> sids) =>
        new(
            AdministratorsSid,
            IsDaclProtected: true,
            sids.Select(sid => new WindowsUserStageAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow,
                    IsInherited: false,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None))
                .ToArray());

    private static void Validate(
        string path,
        WindowsUserStageDirectorySecurity security,
        IReadOnlySet<string> trustedSids,
        IReadOnlyCollection<string> requiredFullControlSids,
        bool exactPrincipalsOnly)
    {
        ValidateOwnerAndDacl(path, security);

        var fullControl = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in security.Rules)
        {
            if (rule.IsInherited)
                throw new UnauthorizedAccessException($"Protected user-state path '{path}' contains an inherited access rule.");
            if (rule.AccessType != AccessControlType.Allow)
                throw new UnauthorizedAccessException($"Protected user-state path '{path}' contains a non-canonical deny rule.");

            var trusted = trustedSids.Contains(rule.Sid);
            if ((!trusted && exactPrincipalsOnly) || (!trusted && (rule.Rights & MutationRights) != 0))
                throw new UnauthorizedAccessException($"Protected user-state path '{path}' grants access to an untrusted principal.");
            if (!trusted)
                continue;

            var appliesToSelf = (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0;
            var inheritsToChildren =
                (rule.InheritanceFlags & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)) ==
                (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
            if (appliesToSelf && inheritsToChildren &&
                (rule.Rights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
            {
                fullControl.Add(rule.Sid);
            }
        }

        if (requiredFullControlSids.Any(sid => !fullControl.Contains(sid)))
            throw new UnauthorizedAccessException($"Protected user-state path '{path}' lacks required full-control rules.");
    }

    private static void ValidateOwnerAndDacl(string path, WindowsUserStageDirectorySecurity security)
    {
        if (!string.Equals(security.OwnerSid, AdministratorsSid, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Protected user-state path '{path}' has an unexpected owner.");
        if (!security.IsDaclProtected)
            throw new UnauthorizedAccessException($"Protected user-state path '{path}' inherits its DACL.");
    }

}

internal sealed record WindowsUserStageLayout(
    string OperationId,
    string ProgramDataRoot,
    string InstallerRoot,
    string ModuleRoot,
    string UserRoot,
    string StageRoot,
    IReadOnlyList<UserStateRoot> LegacyRoots)
{
    public static WindowsUserStageLayout Create(
        IWindowsUserStagePlatform platform,
        string ownerSid,
        string operationId) => CreateCore(platform, ownerSid, operationId, includeUserRoots: true);

    public static WindowsUserStageLayout CreateForMachineStage(
        IWindowsUserStagePlatform platform,
        string ownerSid,
        string operationId) => CreateCore(platform, ownerSid, operationId, includeUserRoots: false);

    private static WindowsUserStageLayout CreateCore(
        IWindowsUserStagePlatform platform,
        string ownerSid,
        string operationId,
        bool includeUserRoots)
    {
        if (!Guid.TryParseExact(operationId, "N", out var operationGuid))
            throw new ArgumentException("Operation identifier must be a 32-character GUID without separators.", nameof(operationId));

        var normalizedOperationId = operationGuid.ToString("N");
        var programData = NormalizeAbsolute(platform.GetProgramDataPath(), "ProgramData");
        var roots = includeUserRoots
            ? LegacyUserStateRoots.FromLocalApplicationData(
                NormalizeAbsolute(platform.GetLocalApplicationDataPath(), "LocalApplicationData"))
            : Array.Empty<UserStateRoot>();
        var installerRoot = Path.Combine(programData, "StructuraConnectorInstaller");
        var moduleRoot = Path.Combine(installerRoot, "UserState");
        var userRoot = Path.Combine(moduleRoot, ownerSid);
        var stageRoot = Path.Combine(userRoot, normalizedOperationId);
        return new(
            normalizedOperationId,
            programData,
            installerRoot,
            moduleRoot,
            userRoot,
            stageRoot,
            roots);
    }

    public void AssertSafeLocation(IWindowsUserStagePlatform platform)
    {
        platform.AssertNoReparseAncestors(ProgramDataRoot);
        foreach (var root in LegacyRoots)
            platform.AssertNoReparseAncestors(root.Path);

        if (!IsStrictDescendant(StageRoot, ProgramDataRoot))
            throw new InvalidDataException("The protected stage escaped ProgramData.");
        if (LegacyRoots.Any(root => PathsOverlap(StageRoot, root.Path)))
            throw new InvalidDataException("The protected stage overlaps a legacy user-state root.");

        var stageVolume = platform.GetVolumeId(StageRoot);
        if (string.IsNullOrWhiteSpace(stageVolume) ||
            LegacyRoots.Any(root => !string.Equals(stageVolume, platform.GetVolumeId(root.Path), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("ProgramData stage and all upgrade user-state roots must be on the same local volume.");
        }
    }

    public void AssertSafeMachineStage(IWindowsUserStagePlatform platform)
    {
        platform.AssertNoReparseAncestors(ProgramDataRoot);
        platform.AssertNoReparseAncestors(StageRoot);
        if (!IsStrictDescendant(StageRoot, ProgramDataRoot))
            throw new InvalidDataException("The protected stage escaped ProgramData.");
    }

    public static string RequireCurrentUserSid(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid))
            throw new ArgumentException("A Windows user SID is required.", nameof(sid));

        SecurityIdentifier parsed;
        try
        {
            parsed = new SecurityIdentifier(sid);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The Windows user SID is invalid.", nameof(sid), exception);
        }

        if (parsed.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            parsed.IsWellKnown(WellKnownSidType.LocalServiceSid) ||
            parsed.IsWellKnown(WellKnownSidType.NetworkServiceSid) ||
            parsed.IsWellKnown(WellKnownSidType.AnonymousSid))
        {
            throw new UnauthorizedAccessException("User-state staging requires an interactive user SID, not a service SID.");
        }

        return parsed.Value;
    }

    public static bool PathEquals(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static bool PathsOverlap(string left, string right) =>
        PathEquals(left, right) || IsStrictDescendant(left, right) || IsStrictDescendant(right, left);

    private static bool IsStrictDescendant(string candidate, string root) =>
        NormalizePath(candidate).StartsWith(
            NormalizePath(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string NormalizeAbsolute(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException($"{name} must be an absolute Windows path.");
        return NormalizePath(path);
    }
}

internal interface IWindowsUserStagePlatform
{
    string GetCurrentUserSid();
    void AssertElevatedAdministrator();
    string GetProgramDataPath();
    string GetLocalApplicationDataPath();
    string GetVolumeId(string path);
    bool DirectoryExists(string path);
    bool FileExists(string path);
    void CreateDirectory(string path, WindowsUserStageDirectorySecurity security);
    void ProtectSharedDirectory(string path);
    WindowsUserStageDirectorySecurity ReadDirectorySecurity(string path);
    bool IsDirectoryEmpty(string path);
    void AssertNoReparseAncestors(string path);
}
