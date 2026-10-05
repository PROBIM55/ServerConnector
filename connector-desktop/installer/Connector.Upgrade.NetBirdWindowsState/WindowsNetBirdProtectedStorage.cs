using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Connector.Upgrade.NetBirdWindowsState;

internal sealed class WindowsNetBirdProtectedStorage
{
    private static readonly SecurityIdentifier SystemSid = new("S-1-5-18");
    private static readonly SecurityIdentifier AdministratorsSid = new("S-1-5-32-544");
    private static readonly SecurityIdentifier TrustedInstallerSid = new(
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly HashSet<string> TrustedSids = new(StringComparer.Ordinal)
    {
        SystemSid.Value,
        AdministratorsSid.Value,
        TrustedInstallerSid.Value,
    };

    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;
    private const FileSystemRights MutationRights = GenericAll | GenericWrite |
        FileSystemRights.WriteData | FileSystemRights.AppendData |
        FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    private const FileSystemRights ReadRights = FileSystemRights.ReadData |
        FileSystemRights.ReadAttributes | FileSystemRights.ReadExtendedAttributes |
        FileSystemRights.ReadPermissions;

    private readonly string _programDataRoot;

    internal WindowsNetBirdProtectedStorage(string programDataRoot)
    {
        _programDataRoot = Path.GetFullPath(programDataRoot ?? throw new ArgumentNullException(nameof(programDataRoot)));
    }

    internal WindowsNetBirdProtectedFileSnapshot ReadProtectedFile(
        string path,
        string protectedRoot,
        int maximumBytes,
        bool denyUntrustedRead,
        bool includeContent)
    {
        try
        {
            var fullPath = RequireDescendant(path, protectedRoot);
            AssertNoReparseComponents(fullPath);
            if (!File.Exists(fullPath))
            {
                if (Directory.Exists(fullPath))
                    return new WindowsNetBirdProtectedFileSnapshot(NetBirdProtectedFileState.Unsafe);
                if (Directory.Exists(protectedRoot))
                    ValidateProtectedRoot(protectedRoot);
                return new WindowsNetBirdProtectedFileSnapshot(NetBirdProtectedFileState.Missing);
            }

            ValidateProtectedRoot(protectedRoot);
            ValidateEntry(fullPath, directory: false, requireProtectedDacl: true, denyUntrustedRead);
            var info = new FileInfo(fullPath);
            if (info.Length is < 0 || info.Length > maximumBytes)
                return new WindowsNetBirdProtectedFileSnapshot(NetBirdProtectedFileState.Unreadable);
            using var stream = OpenPinnedRead(fullPath);
            if (stream.Length != info.Length)
                return new WindowsNetBirdProtectedFileSnapshot(NetBirdProtectedFileState.Unreadable);
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.Length != info.Length)
                return new WindowsNetBirdProtectedFileSnapshot(NetBirdProtectedFileState.Unreadable);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            return new WindowsNetBirdProtectedFileSnapshot(
                NetBirdProtectedFileState.Present,
                includeContent ? bytes : null,
                hash);
        }
        catch (UnauthorizedAccessException)
        {
            return new WindowsNetBirdProtectedFileSnapshot(NetBirdProtectedFileState.Unsafe);
        }
        catch (InvalidDataException)
        {
            return new WindowsNetBirdProtectedFileSnapshot(NetBirdProtectedFileState.Unsafe);
        }
        catch
        {
            return new WindowsNetBirdProtectedFileSnapshot(NetBirdProtectedFileState.Unreadable);
        }
    }

    internal bool IsInstalledBinaryPathSecure(string path, string expectedRoot)
    {
        try
        {
            var full = RequireDescendant(path, expectedRoot);
            AssertNoReparseComponents(full);
            ValidateEntry(expectedRoot, directory: true, requireProtectedDacl: false, denyUntrustedRead: false);
            ValidateEntry(full, directory: false, requireProtectedDacl: false, denyUntrustedRead: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal void WriteProtectedFileAtomically(
        string path,
        string protectedRoot,
        byte[] content,
        bool denyUntrustedRead)
    {
        ArgumentNullException.ThrowIfNull(content);
        RequireTrustedElevatedCaller();
        var root = PrepareProtectedRoot(protectedRoot);
        var destination = RequireDescendant(path, root);
        if (File.Exists(destination))
            ValidateEntry(destination, directory: false, requireProtectedDacl: true, denyUntrustedRead);
        else if (Directory.Exists(destination))
            throw new InvalidDataException("The protected NetBird destination is a directory.");

        var temporary = Path.Combine(root, ".pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            ProtectFile(temporary, denyUntrustedRead);
            File.Move(temporary, destination, overwrite: true);
            ValidateEntry(destination, directory: false, requireProtectedDacl: true, denyUntrustedRead);
            var actual = File.ReadAllBytes(destination);
            if (!actual.AsSpan().SequenceEqual(content))
                throw new IOException("The protected NetBird file changed during atomic publication.");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                AssertNoReparseComponents(temporary);
                File.Delete(temporary);
            }
        }
    }

    internal void DeleteProtectedFile(string path, string protectedRoot, bool denyUntrustedRead)
    {
        RequireTrustedElevatedCaller();
        var destination = RequireDescendant(path, protectedRoot);
        AssertNoReparseComponents(destination);
        ValidateProtectedRoot(protectedRoot);
        ValidateEntry(destination, directory: false, requireProtectedDacl: true, denyUntrustedRead);
        File.Delete(destination);
    }

    internal void RestoreProtectedFileAtomically(
        string protectedSourcePath,
        string destinationPath,
        string destinationRoot,
        string expectedSha256,
        bool denyUntrustedRead)
    {
        if (!WindowsNetBirdMachineStatePort.IsSha256(expectedSha256))
            throw new InvalidDataException("The protected NetBird configuration hash is invalid.");
        var sourceRoot = Path.GetDirectoryName(Path.GetFullPath(protectedSourcePath))
            ?? throw new InvalidDataException("The protected NetBird source has no parent.");
        var source = ReadProtectedFile(
            protectedSourcePath,
            sourceRoot,
            8 * 1024 * 1024,
            denyUntrustedRead: true,
            includeContent: true);
        if (source.State != NetBirdProtectedFileState.Present || source.Content is null ||
            !string.Equals(source.Sha256, expectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The protected prior NetBird configuration is unavailable or changed.");
        WriteProtectedFileAtomically(destinationPath, destinationRoot, source.Content, denyUntrustedRead);
    }

    internal async ValueTask<WindowsNetBirdProtectedCopy> CopyToNewProtectedFileAsync(
        string sourcePath,
        string sourceRoot,
        string destinationPath,
        string destinationRoot,
        bool denyUntrustedRead,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        RequireTrustedElevatedCaller();
        var source = RequireDescendant(sourcePath, sourceRoot);
        var destination = RequireDescendant(destinationPath, destinationRoot);
        AssertNoReparseComponents(source);
        if (IsBelowProgramData(sourceRoot))
            ValidateProtectedRoot(sourceRoot);
        else
            ValidateEntry(sourceRoot, directory: true, requireProtectedDacl: false, denyUntrustedRead);
        ValidateEntry(source, directory: false, requireProtectedDacl: false, denyUntrustedRead);
        ValidateProtectedRoot(destinationRoot);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("The protected NetBird restore destination already exists.");

        await using var input = OpenPinnedRead(source);
        if (input.Length <= 0 || input.Length > maximumBytes)
            throw new InvalidDataException("The NetBird restore source size is outside the accepted bounds.");
        FileStream? output = null;
        try
        {
            output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            if (output.Length != input.Length)
                throw new IOException("The protected NetBird restore copy is incomplete.");
            await output.DisposeAsync().ConfigureAwait(false);
            output = null;
            ProtectFile(destination, denyUntrustedRead);
            await using var verification = OpenProtectedPinnedRead(
                destination,
                destinationRoot,
                denyUntrustedRead);
            var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(verification, cancellationToken).ConfigureAwait(false));
            return new WindowsNetBirdProtectedCopy(destination, verification.Length, hash);
        }
        catch
        {
            if (output is not null)
                await output.DisposeAsync().ConfigureAwait(false);
            if (File.Exists(destination))
            {
                AssertNoReparseComponents(destination);
                File.Delete(destination);
            }
            throw;
        }
    }

    internal FileStream OpenProtectedPinnedRead(
        string path,
        string protectedRoot,
        bool denyUntrustedRead)
    {
        var full = RequireDescendant(path, protectedRoot);
        AssertNoReparseComponents(full);
        ValidateProtectedRoot(protectedRoot);
        ValidateEntry(full, directory: false, requireProtectedDacl: true, denyUntrustedRead);
        return OpenPinnedRead(full);
    }

    internal string PrepareProtectedRoot(string requestedRoot)
    {
        RequireTrustedElevatedCaller();
        var full = Path.GetFullPath(requestedRoot);
        if (!full.StartsWith(
                _programDataRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Protected NetBird state must be below ProgramData.");
        AssertNoReparseComponents(_programDataRoot);
        ValidateEntry(_programDataRoot, directory: true, requireProtectedDacl: false, denyUntrustedRead: false);
        var current = _programDataRoot;
        foreach (var component in Path.GetRelativePath(_programDataRoot, full).Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (Directory.Exists(current))
            {
                ValidateEntry(current, directory: true, requireProtectedDacl: true, denyUntrustedRead: false);
                continue;
            }
            if (File.Exists(current))
                throw new InvalidDataException("A protected NetBird path component is not a directory.");
            CreateProtectedDirectory(current);
        }
        return full;
    }

    internal string ValidateProtectedRoot(string requestedRoot)
    {
        var full = Path.GetFullPath(requestedRoot);
        if (!full.StartsWith(
                _programDataRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Protected NetBird state must be below ProgramData.");
        AssertNoReparseComponents(full);
        ValidateEntry(_programDataRoot, directory: true, requireProtectedDacl: false, denyUntrustedRead: false);
        var current = _programDataRoot;
        foreach (var component in Path.GetRelativePath(_programDataRoot, full).Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current))
                throw new DirectoryNotFoundException("A protected NetBird state directory is missing.");
            ValidateEntry(current, directory: true, requireProtectedDacl: true, denyUntrustedRead: false);
        }
        return full;
    }

    private static void CreateProtectedDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        AddTrustedRules(security, inherit: true);
        FileSystemAclExtensions.Create(new DirectoryInfo(path), security);
        ValidateEntry(path, directory: true, requireProtectedDacl: true, denyUntrustedRead: false);
    }

    private static void ProtectFile(string path, bool denyUntrustedRead)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        AddTrustedRules(security, inherit: false);
        FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
        ValidateEntry(path, directory: false, requireProtectedDacl: true, denyUntrustedRead);
    }

    private static void AddTrustedRules(FileSystemSecurity security, bool inherit)
    {
        foreach (var sid in new[] { SystemSid, AdministratorsSid, TrustedInstallerSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                inherit ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
    }

    private static void ValidateEntry(
        string path,
        bool directory,
        bool requireProtectedDacl,
        bool denyUntrustedRead)
    {
        AssertNoReparseComponents(path);
        FileSystemSecurity security = directory
            ? FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(path),
                AccessControlSections.Owner | AccessControlSections.Access)
            : FileSystemAclExtensions.GetAccessControl(
                new FileInfo(path),
                AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !TrustedSids.Contains(owner.Value))
            throw new UnauthorizedAccessException("A NetBird machine path has an untrusted owner.");
        if (requireProtectedDacl && !security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("A protected NetBird path inherits its DACL.");

        var forbidden = MutationRights | (denyUntrustedRead ? ReadRights : 0);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     targetType: typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                !TrustedSids.Contains(rule.IdentityReference.Value) &&
                (rule.FileSystemRights & forbidden) != 0)
                throw new UnauthorizedAccessException("A NetBird machine path grants unsafe access to an untrusted principal.");
        }
    }

    private static void RequireTrustedElevatedCaller()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        if (identity.User is { Value: var sid } && TrustedSids.Contains(sid))
            return;
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException(
                "Protected NetBird state requires an elevated administrator, SYSTEM, or TrustedInstaller process.");
    }

    private static string RequireDescendant(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A NetBird path escapes its expected machine root.");
        return fullPath;
    }

    private bool IsBelowProgramData(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(
            _programDataRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNoReparseComponents(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)
            ?? throw new InvalidDataException("A NetBird path has no filesystem root.");
        var current = root;
        foreach (var component in full[root.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("NetBird state rejects reparse-point path components.");
        }
    }

    private static FileStream OpenPinnedRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        64 * 1024,
        FileOptions.SequentialScan);
}

internal sealed record WindowsNetBirdProtectedCopy(string Path, long SizeBytes, string Sha256);
