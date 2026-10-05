using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Connector.Upgrade.ReleaseManifestBuilder;

internal sealed record ReleaseBuildRequest(string HelperPath, string SetupPath, string StagingDirectory,
    string PrivateKeyPemPath, string Version, string SetupPackageId, string SetupVersion, string? PublisherName,
    string RepositoryRoot, int SchemaVersion = 2, string? CallerPath = null);

internal sealed record AuthenticodeInfo(string PublisherName, string Thumbprint, bool HasTimestamp);
internal interface IAuthenticodeInspector { AuthenticodeInfo Inspect(string path); }

/// <summary>Builds a detached ECDSA-signed release manifest in the selected supported schema.</summary>
public sealed class ReleaseManifestBuilder
{
    private const string HelperName = "Connector.Upgrade.MachineHelper.exe";
    private const string SetupName = "Setup.exe";
    private readonly IAuthenticodeInspector _authenticode;
    private readonly Action<string> _validateProtectedDirectory;

    public ReleaseManifestBuilder() : this(new PowerShellAuthenticodeInspector(), ValidateProtectedDirectory) { }

    internal ReleaseManifestBuilder(IAuthenticodeInspector authenticode, Action<string> validateProtectedDirectory)
    {
        _authenticode = authenticode ?? throw new ArgumentNullException(nameof(authenticode));
        _validateProtectedDirectory = validateProtectedDirectory ?? throw new ArgumentNullException(nameof(validateProtectedDirectory));
    }

    internal void Build(ReleaseBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateVersion(request.Version, "version");
        ValidateVersion(request.SetupVersion, "Setup version");
        if (string.IsNullOrWhiteSpace(request.SetupPackageId) || request.SetupPackageId.Length > 128 ||
            !request.SetupPackageId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            throw new InvalidDataException("Setup package id is invalid.");
        if (request.SchemaVersion is not (2 or 3 or 4)) throw new InvalidDataException("Release manifest schema version must be 2, 3, or 4.");
        if (request.SchemaVersion == 2 && string.IsNullOrWhiteSpace(request.PublisherName))
            throw new InvalidDataException("Schema v2 requires an expected Authenticode publisher.");
        if (request.SchemaVersion == 4 && string.IsNullOrWhiteSpace(request.CallerPath))
            throw new InvalidDataException("Schema v4 requires the exact Bootstrapper --caller image.");
        if (request.SchemaVersion != 4 && !string.IsNullOrWhiteSpace(request.CallerPath))
            throw new InvalidDataException("--caller is supported only for schema v4.");

        var stage = FullExistingDirectory(request.StagingDirectory);
        _validateProtectedDirectory(stage);
        var helper = RequireExactFile(request.HelperPath, stage, HelperName);
        var setup = RequireExactFile(request.SetupPath, stage, SetupName);
        var caller = request.SchemaVersion == 4 ? RequireBootstrapper(request.CallerPath!) : null;
        if (caller is not null) _validateProtectedDirectory(Path.GetDirectoryName(caller)!);
        var repo = FullExistingDirectory(request.RepositoryRoot);
        var keyPath = FullExistingFile(request.PrivateKeyPemPath);
        if (IsWithin(repo, keyPath)) throw new InvalidDataException("Private signing key must be outside the repository checkout.");
        AssertNoReparseComponents(keyPath);
        var helperAuth = request.SchemaVersion == 2 ? ValidateAuthenticode(helper, request.PublisherName!) : null;
        var setupAuth = request.SchemaVersion == 2 ? ValidateAuthenticode(setup, request.PublisherName!) : null;
        var helperBytes = File.ReadAllBytes(helper);
        var setupBytes = File.ReadAllBytes(setup);
        var callerBytes = caller is null ? null : File.ReadAllBytes(caller);
        if (helperBytes.Length == 0) throw new InvalidDataException("Machine helper is empty.");
        if (request.SchemaVersion is 3 or 4 && helperBytes.LongLength > 1024L * 1024 * 1024)
            throw new InvalidDataException("Machine helper exceeds the supported signed-manifest size limit.");
        if (setupBytes.Length == 0 || setupBytes.LongLength > 1024L * 1024 * 1024) throw new InvalidDataException("Setup.exe has an unsupported size.");
        if (callerBytes is { Length: 0 } || callerBytes is { LongLength: > 1024L * 1024 * 1024 })
            throw new InvalidDataException("Bootstrapper exceeds the supported schema-v4 size limits.");

        using var signer = ECDsa.Create();
        var privatePem = File.ReadAllText(keyPath, Encoding.UTF8).Trim();
        if (!privatePem.StartsWith("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal) ||
            !privatePem.EndsWith("-----END PRIVATE KEY-----", StringComparison.Ordinal) ||
            privatePem.IndexOf("-----BEGIN ", 1, StringComparison.Ordinal) >= 0 ||
            privatePem.IndexOf("-----END ", StringComparison.Ordinal) != privatePem.LastIndexOf("-----END ", StringComparison.Ordinal))
            throw new InvalidDataException("Signing key must be exactly one P-256 private-key PEM.");
        signer.ImportFromPem(privatePem);
        if (signer.KeySize != 256) throw new InvalidDataException("Signing key must be exactly one P-256 private-key PEM.");

        var manifest = SerializeCanonical(request.SchemaVersion, request.Version, HelperName, helperBytes, helperAuth,
            setupBytes, setupAuth, request.SetupPackageId, request.SetupVersion, callerBytes);
        if (manifest.Length > 16 * 1024) throw new InvalidDataException("Generated release manifest exceeds the runtime limit.");
        var signature = signer.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (signature.Length != 64) throw new CryptographicException("Generated manifest signature is not 64-byte P1363.");

        var manifestPath = Path.Combine(stage, "helper-release.json");
        var signaturePath = Path.Combine(stage, "helper-release.sig");
        AssertNewRegularPath(manifestPath);
        AssertNewRegularPath(signaturePath);
        using (var output = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(manifest);
        try
        {
            using var output = new FileStream(signaturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(signature);
        }
        catch
        {
            // Do not remove the already-created manifest: preserving evidence is safer than a partial cleanup race.
            throw;
        }
    }

    private AuthenticodeInfo ValidateAuthenticode(string path, string expectedPublisher)
    {
        var info = _authenticode.Inspect(path);
        if (!string.Equals(info.PublisherName, expectedPublisher, StringComparison.Ordinal) ||
            info.Thumbprint.Length != 40 || !info.Thumbprint.All(Uri.IsHexDigit) || !info.HasTimestamp)
            throw new InvalidDataException($"Authenticode publisher, signer, or timestamp validation failed for {Path.GetFileName(path)}.");
        return info with { Thumbprint = info.Thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant() };
    }

    private static byte[] SerializeCanonical(int schemaVersion, string version, string helperName, byte[] helper, AuthenticodeInfo? helperAuth,
        byte[] setup, AuthenticodeInfo? setupAuth, string packageId, string setupVersion, byte[]? caller)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", schemaVersion);
            json.WriteString("version", version);
            json.WriteString("helperRelativePath", helperName);
            if (schemaVersion is 3 or 4) json.WriteNumber("size", helper.LongLength);
            json.WriteString("sha256", Convert.ToHexString(SHA256.HashData(helper)));
            if (schemaVersion == 2) json.WriteString("authenticodeSignerThumbprint", helperAuth!.Thumbprint);
            json.WritePropertyName("velopackSetup"); json.WriteStartObject();
            json.WriteString("relativePath", SetupName);
            json.WriteNumber("size", setup.LongLength);
            json.WriteString("sha256", Convert.ToHexString(SHA256.HashData(setup)));
            json.WriteString("packageId", packageId);
            json.WriteString("version", setupVersion);
            if (schemaVersion == 2) json.WriteString("authenticodeSignerThumbprint", setupAuth!.Thumbprint);
            json.WriteEndObject();
            if (schemaVersion == 4)
            {
                json.WritePropertyName("callerImage"); json.WriteStartObject();
                json.WriteString("relativePath", "StructuraConnectorInstaller\\Bootstrapper\\Connector.Upgrade.Bootstrapper.exe");
                json.WriteNumber("size", caller!.LongLength);
                json.WriteString("sha256", Convert.ToHexString(SHA256.HashData(caller)));
                json.WriteEndObject();
            }
            json.WriteEndObject(); json.Flush();
        }
        return stream.ToArray();
    }

    private static string RequireBootstrapper(string path)
    {
        var full = FullExistingFile(path);
        if (!string.Equals(Path.GetFileName(full), "Connector.Upgrade.Bootstrapper.exe", StringComparison.Ordinal))
            throw new InvalidDataException("Schema-v4 caller must be Connector.Upgrade.Bootstrapper.exe.");
        AssertNoReparseComponents(full);
        return full;
    }

    private static string RequireExactFile(string path, string directory, string expectedName)
    {
        var full = FullExistingFile(path);
        if (!string.Equals(Path.GetDirectoryName(full), directory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(full), expectedName, StringComparison.Ordinal))
            throw new InvalidDataException($"Input must be the fixed {expectedName} file directly in staging.");
        AssertNoReparseComponents(full);
        return full;
    }

    private static string FullExistingFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) || (File.GetAttributes(full) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("Input file is missing, a directory, or a reparse point.");
        return full;
    }

    private static string FullExistingDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Directory is missing or a reparse point.");
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static void AssertNoReparseComponents(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A release input path contains a reparse component.");
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent ?? string.Empty;
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static string FindRepositoryRoot(params string[] startDirectories)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var startDirectory in startDirectories)
        {
            var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                {
                    roots.Add(current.FullName);
                    break;
                }
                current = current.Parent;
            }
        }
        if (roots.Count == 1) return roots.Single();
        throw new InvalidDataException("Cannot prove the checkout root; private signing key location cannot be validated.");
    }

    private static void AssertNewRegularPath(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Release manifest output already exists; overwrite is forbidden.");
        var parent = Path.GetDirectoryName(path)!;
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Output directory is a reparse point.");
    }

    private static void ValidateVersion(string value, string label)
    {
        if (!Version.TryParse(value, out var version) || version.Build < 0 || version.Revision >= 0 ||
            !string.Equals(version.ToString(3), value, StringComparison.Ordinal))
            throw new InvalidDataException($"{label} must be an exact three-part numeric version.");
    }

    private static void ValidateProtectedDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Release staging ACL validation requires Windows.");
        var security = new DirectoryInfo(directory).GetAccessControl(System.Security.AccessControl.AccessControlSections.Owner | System.Security.AccessControl.AccessControlSections.Access);
        var trusted = new HashSet<string>(StringComparer.Ordinal)
        {
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null).Value,
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null).Value,
            System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? string.Empty
        };
        if (security.GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) is not System.Security.Principal.SecurityIdentifier owner || !trusted.Contains(owner.Value))
            throw new InvalidDataException("Release staging owner is not a trusted machine/build identity.");
        foreach (System.Security.AccessControl.FileSystemAccessRule ace in security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
        {
            if (GrantsMutationToUntrustedSid(ace, trusted))
                throw new InvalidDataException("Release staging is writable by an untrusted principal.");
        }
    }

    internal static bool GrantsMutationToUntrustedSid(
        System.Security.AccessControl.FileSystemAccessRule ace, ISet<string> trustedSidValues)
    {
        ArgumentNullException.ThrowIfNull(ace);
        ArgumentNullException.ThrowIfNull(trustedSidValues);
        const System.Security.AccessControl.FileSystemRights mutationRights =
            (System.Security.AccessControl.FileSystemRights)0x10000000 |
            (System.Security.AccessControl.FileSystemRights)0x40000000 |
            System.Security.AccessControl.FileSystemRights.Write |
            System.Security.AccessControl.FileSystemRights.Modify |
            System.Security.AccessControl.FileSystemRights.FullControl |
            System.Security.AccessControl.FileSystemRights.Delete |
            System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles |
            System.Security.AccessControl.FileSystemRights.ChangePermissions |
            System.Security.AccessControl.FileSystemRights.TakeOwnership |
            System.Security.AccessControl.FileSystemRights.WriteAttributes |
            System.Security.AccessControl.FileSystemRights.WriteExtendedAttributes |
            System.Security.AccessControl.FileSystemRights.AppendData |
            System.Security.AccessControl.FileSystemRights.WriteData;
        return ace.AccessControlType == System.Security.AccessControl.AccessControlType.Allow &&
               (ace.FileSystemRights & mutationRights) != 0 &&
               ace.IdentityReference is System.Security.Principal.SecurityIdentifier sid &&
               !trustedSidValues.Contains(sid.Value);
    }
}

internal sealed class PowerShellAuthenticodeInspector : IAuthenticodeInspector
{
    public AuthenticodeInfo Inspect(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Authenticode validation requires Windows.");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Path.GetFullPath(path)));
        var script = $"$p=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{encoded}')); $s=Get-AuthenticodeSignature -LiteralPath $p; if($s.Status -ne 'Valid' -or $null -eq $s.SignerCertificate -or $null -eq $s.TimeStamperCertificate){{exit 31}}; $n=$s.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName,$false); [Console]::Out.WriteLine($n); [Console]::Out.WriteLine($s.SignerCertificate.Thumbprint)";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add(script);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Authenticode verifier.");
        var stdout = process.StandardOutput.ReadToEnd(); _ = process.StandardError.ReadToEnd(); process.WaitForExit();
        var lines = stdout.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        if (process.ExitCode != 0 || lines.Length != 2) throw new InvalidDataException("Authenticode trust or timestamp verification failed.");
        return new AuthenticodeInfo(lines[0], lines[1], true);
    }
}
