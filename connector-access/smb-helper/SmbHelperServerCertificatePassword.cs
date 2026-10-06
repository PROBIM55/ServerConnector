using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Connector.Access.Smb.Helper;

internal static class SmbHelperServerCertificatePassword
{
    internal const string DefaultSecretName = "CONNECTOR_SMB_HELPER_SERVER_PFX_PASSWORD";
    internal const string ProtectedFilePath = @"C:\Platform\runtime\connector-access\smb-helper-secrets.json";
    private const int MaximumFileBytes = 64 * 1024;
    private const int MaximumSecretCharacters = 8192;
    private static readonly HashSet<string> SupportedNames = new(StringComparer.Ordinal)
    {
        DefaultSecretName,
    };
    private static readonly HashSet<string> TrustedSids = new(StringComparer.Ordinal)
    {
        "S-1-5-18", "S-1-5-32-544",
    };
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const long WriteRightsMask =
        (long)FileSystemRights.Write | (long)FileSystemRights.Delete |
        (long)FileSystemRights.DeleteSubdirectoriesAndFiles | (long)FileSystemRights.ChangePermissions |
        (long)FileSystemRights.TakeOwnership | 0x10000000L | 0x40000000L;
    private const long DirectoryWriteRightsMask = WriteRightsMask | (long)FileSystemRights.CreateDirectories;
    private const long ReadRightsMask =
        (long)FileSystemRights.Read | (long)FileSystemRights.ReadData |
        (long)FileSystemRights.ReadAttributes | (long)FileSystemRights.ReadExtendedAttributes |
        (long)FileSystemRights.ReadPermissions | 0x80000000L;

    internal static string Load(
        SmbHelperHostOptions options,
        Func<string, string?>? environmentReader = null,
        Func<string, byte[]>? protectedFileReader = null,
        string expectedPath = ProtectedFilePath)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ServerCertificatePasswordFilePath))
        {
            if (string.IsNullOrWhiteSpace(options.ServerCertificatePasswordEnvironmentVariable))
                throw Unavailable();
            var value = (environmentReader ?? Environment.GetEnvironmentVariable)(options.ServerCertificatePasswordEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumSecretCharacters || value.Contains('\0'))
                throw Unavailable();
            return value;
        }

        var configuredPath = options.ServerCertificatePasswordFilePath;
        if (!IsCanonicalPath(configuredPath, expectedPath) ||
            !SupportedNames.Contains(options.ServerCertificatePasswordSecretName))
            throw Unavailable();

        try
        {
            var bytes = (protectedFileReader ?? ReadProtectedFile)(configuredPath);
            return ReadJsonSecret(bytes, options.ServerCertificatePasswordSecretName);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw Unavailable();
        }
    }

    internal static string ReadJsonSecret(ReadOnlySpan<byte> utf8Json, string requiredName)
    {
        if (utf8Json.Length is <= 0 or > MaximumFileBytes || !SupportedNames.Contains(requiredName))
            throw Unavailable();
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Unavailable();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? requiredValue = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!SupportedNames.Contains(property.Name) || !seen.Add(property.Name) ||
                    property.Value.ValueKind != JsonValueKind.String)
                    throw Unavailable();
                var value = property.Value.GetString();
                if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumSecretCharacters || value.Contains('\0'))
                    throw Unavailable();
                if (property.Name == requiredName) requiredValue = value;
            }
            return requiredValue ?? throw Unavailable();
        }
        catch (JsonException)
        {
            throw Unavailable();
        }
    }

    internal static bool IsCanonicalPath(string path, string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            var canonical = Path.GetFullPath(path);
            return string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), expectedPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(canonical, expectedPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    internal static void ValidateAcl(
        FileSystemSecurity acl,
        bool isFile,
        bool requireProtected = true,
        bool allowTrustedInstallerOwner = false,
        bool allowUntrustedCreateDirectories = false)
    {
        ArgumentNullException.ThrowIfNull(acl);
        var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !TrustedSids.Contains(owner.Value) &&
            !(allowTrustedInstallerOwner && owner.Value == TrustedInstallerSid)) throw Unavailable();
        var raw = new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0);
        if (raw.DiscretionaryAcl is null || requireProtected && !acl.AreAccessRulesProtected) throw Unavailable();
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            var sid = ((SecurityIdentifier)rule.IdentityReference).Value;
            var rights = unchecked((long)(int)rule.FileSystemRights);
            if (isFile)
            {
                if (!TrustedSids.Contains(sid) && (rights & (ReadRightsMask | WriteRightsMask)) != 0) throw Unavailable();
                continue;
            }
            var directoryWrites = rights & DirectoryWriteRightsMask;
            if (allowUntrustedCreateDirectories) directoryWrites &= ~(long)FileSystemRights.CreateDirectories;
            if (!TrustedSids.Contains(sid) && directoryWrites != 0)
                throw Unavailable();
        }
    }

    private static byte[] ReadProtectedFile(string path)
    {
        if (!OperatingSystem.IsWindows() || !IsCanonicalPath(path, ProtectedFilePath)) throw Unavailable();
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw Unavailable();
        var rootAttributes = File.GetAttributes(root);
        if ((rootAttributes & FileAttributes.Directory) == 0 || (rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw Unavailable();
        ValidateAcl(new DirectoryInfo(root).GetAccessControl(), isFile: false,
            allowTrustedInstallerOwner: true, allowUntrustedCreateDirectories: true);
        var pieces = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        foreach (var piece in pieces)
        {
            current = Path.Combine(current, piece);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw Unavailable();
            var isLeaf = string.Equals(current, full, StringComparison.OrdinalIgnoreCase);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (isLeaf == isDirectory) throw Unavailable();
            FileSystemSecurity acl = isDirectory
                ? new DirectoryInfo(current).GetAccessControl()
                : new FileInfo(current).GetAccessControl();
            var protectedParent = !isLeaf && string.Equals(current, Path.GetDirectoryName(full), StringComparison.OrdinalIgnoreCase);
            ValidateAcl(acl, isLeaf, requireProtected: isLeaf || protectedParent);
        }

        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumFileBytes) throw Unavailable();
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw Unavailable();
        var attributesAfterOpen = File.GetAttributes(full);
        if ((attributesAfterOpen & FileAttributes.ReparsePoint) != 0) throw Unavailable();
        return bytes;
    }

    private static InvalidOperationException Unavailable() =>
        new("SMB helper server certificate password is unavailable.");
}
