using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Connector.Upgrade.HelperLauncher;

namespace Connector.Upgrade.HelperReleaseTrust;

/// <summary>Loads pins from a detached-signature verified machine release manifest.</summary>
public sealed class HelperReleasePinSource
{
    private const int MaxManifestBytes = 16 * 1024;
    private const int SignatureBytes = 64;
    private const string ReleasePublicKeyResourceName = "Connector.Upgrade.HelperReleaseTrust.ReleasePublicKey.pem";
    private readonly IHelperReleaseTrustRuntime _runtime;
    private readonly Func<(byte[] Manifest, byte[] Signature)> _readManifest;
    private readonly string _publicKeyPem;

    public HelperReleasePinSource()
    {
        _runtime = WindowsHelperReleaseTrustRuntime.Instance;
        _readManifest = () => _runtime.ReadInstalledManifest(MaxManifestBytes, SignatureBytes);
        _publicKeyPem = ReadEmbeddedReleasePublicKey();
    }

    internal HelperReleasePinSource(IHelperReleaseTrustRuntime runtime,
        Func<(byte[] Manifest, byte[] Signature)> readManifest, string publicKeyPem)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _readManifest = readManifest ?? throw new ArgumentNullException(nameof(readManifest));
        _publicKeyPem = publicKeyPem ?? throw new ArgumentNullException(nameof(publicKeyPem));
    }

    public HelperImagePin GetPin()
    {
        var release = LoadVerifiedManifest();
        var expectedPath = _runtime.ResolveProtectedHelperPath(release.HelperRelativePath);
        ValidateResolvedPath(expectedPath, release.HelperRelativePath, _runtime.GetProtectedHelperDirectory(), "helper image");
        var inspection = _runtime.InspectProtectedFile(expectedPath, release.TrustMode == ReleaseArtifactTrustMode.Authenticode);
        ValidateInspectionPath(inspection, expectedPath, "helper image");
        if (release.Size > 0 && inspection.Size != release.Size)
            throw new SecurityException("Helper image size does not match the signed release manifest.");
        if (!string.Equals(inspection.Sha256, release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Helper image SHA-256 does not match the signed release manifest.");
        if (release.TrustMode == ReleaseArtifactTrustMode.Authenticode &&
            !string.Equals(NormalizeThumbprint(inspection.SignerThumbprint!), NormalizeThumbprint(release.SignerThumbprint!), StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Helper Authenticode signer does not match the signed release manifest.");

        return new HelperImagePin(expectedPath, release.Size, release.Sha256, release.TrustMode, release.SignerThumbprint);
    }

    /// <summary>Returns the exact Velopack Setup.exe pin from the same protected, signed release manifest.</summary>
    public VelopackSetupPin GetVelopackSetupPin()
    {
        var release = LoadVerifiedManifest();
        var setup = release.VelopackSetup;
        var expectedPath = _runtime.ResolveProtectedHelperPath(setup.RelativePath);
        ValidateResolvedPath(expectedPath, setup.RelativePath, _runtime.GetProtectedHelperDirectory(), "Velopack Setup.exe");
        var inspection = _runtime.InspectProtectedFile(expectedPath, setup.TrustMode == ReleaseArtifactTrustMode.Authenticode);
        ValidateInspectionPath(inspection, expectedPath, "Velopack Setup.exe");
        if (inspection.Size != setup.Size)
            throw new SecurityException("Velopack Setup.exe size does not match the signed release manifest.");
        if (!string.Equals(inspection.Sha256, setup.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Velopack Setup.exe SHA-256 does not match the signed release manifest.");
        if (setup.TrustMode == ReleaseArtifactTrustMode.Authenticode &&
            !string.Equals(NormalizeThumbprint(inspection.SignerThumbprint!), NormalizeThumbprint(setup.SignerThumbprint!), StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Velopack Setup.exe Authenticode signer does not match the signed release manifest.");

        return new VelopackSetupPin(expectedPath, setup.Size, setup.Sha256, setup.PackageId, setup.Version, setup.TrustMode, setup.SignerThumbprint);
    }

    /// <summary>Returns the Bootstrapper pin from a verified schema-v4 release manifest; older schemas have no caller pin.</summary>
    public ProtectedCallerImagePin? GetCallerImagePin()
    {
        var release = LoadVerifiedManifest();
        return release.CallerImage is null ? null : new ProtectedCallerImagePin(
            release.CallerImage.RelativePath, release.CallerImage.Size, release.CallerImage.Sha256);
    }

    private ReleaseManifest LoadVerifiedManifest()
    {
        if (string.IsNullOrWhiteSpace(_publicKeyPem))
            throw new SecurityException("Helper release verification key is not provisioned; release pins fail closed.");

        var (manifestBytes, signature) = _readManifest();
        if (manifestBytes is null || manifestBytes.Length is 0 or > MaxManifestBytes)
            throw new SecurityException("Helper release manifest is missing or exceeds its size limit.");
        if (signature is null || signature.Length != SignatureBytes)
            throw new SecurityException("Helper release manifest signature is missing or malformed.");

        using var verifier = ECDsa.Create();
        try { verifier.ImportFromPem(_publicKeyPem); }
        catch (CryptographicException ex) { throw new SecurityException("Embedded helper release public key is invalid.", ex); }
        if (!verifier.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new SecurityException("Helper release manifest signature verification failed.");

        return ParseManifest(manifestBytes);
    }

    private static string ReadEmbeddedReleasePublicKey()
    {
        using var stream = typeof(HelperReleasePinSource).Assembly
            .GetManifestResourceStream(ReleasePublicKeyResourceName);
        if (stream is null) return string.Empty;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static ReleaseManifest ParseManifest(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new SecurityException("Helper release manifest must be a JSON object.");
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!fields.TryAdd(property.Name, property.Value))
                    throw new SecurityException("Helper release manifest contains a duplicate field.");
            if (!fields.TryGetValue("schemaVersion", out var schemaElement) || schemaElement.ValueKind != JsonValueKind.Number ||
                !schemaElement.TryGetInt32(out var schema) || schema is not (2 or 3 or 4))
                throw new SecurityException("Helper release manifest schema version is unsupported.");
            string[] expected = schema == 2
                ? ["schemaVersion", "version", "helperRelativePath", "sha256", "authenticodeSignerThumbprint", "velopackSetup"]
                : schema == 3
                    ? ["schemaVersion", "version", "helperRelativePath", "size", "sha256", "velopackSetup"]
                    : ["schemaVersion", "version", "helperRelativePath", "size", "sha256", "velopackSetup", "callerImage"];
            if (fields.Count != expected.Length || expected.Any(name => !fields.ContainsKey(name)))
                throw new SecurityException("Helper release manifest schema does not match the supported version.");
            var version = RequiredString(fields["version"], "version");
            if (!Version.TryParse(version, out var parsedVersion) || parsedVersion.Major < 0 || parsedVersion.Minor < 0 ||
                parsedVersion.Build < 0 || parsedVersion.Revision >= 0 || !string.Equals(parsedVersion.ToString(3), version, StringComparison.Ordinal))
                throw new SecurityException("Helper release version must be an exact three-part numeric version.");
            var relativePath = RequiredString(fields["helperRelativePath"], "helperRelativePath");
            if (Path.IsPathRooted(relativePath) || relativePath.Contains(':') || relativePath.Contains('/') ||
                relativePath.Contains('\\') || relativePath is "." or ".." || !relativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new SecurityException("Helper relative path must name one direct-child executable.");
            var hash = RequiredString(fields["sha256"], "sha256");
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new SecurityException("Helper SHA-256 pin is invalid.");
            var size = schema is 3 or 4 ? RequiredSize(fields["size"], "size", 1024L * 1024 * 1024) : 0;
            var signer = schema == 2 ? ParseSigner(fields["authenticodeSignerThumbprint"], "authenticodeSignerThumbprint") : null;
            var setup = ParseSetupPin(fields["velopackSetup"], schema);
            var caller = schema == 4 ? ParseCallerImagePin(fields["callerImage"]) : null;
            return new ReleaseManifest(version, relativePath, size, hash,
                schema == 2 ? ReleaseArtifactTrustMode.Authenticode : ReleaseArtifactTrustMode.SignedManifestHash, signer, setup, caller);
        }
        catch (JsonException ex) { throw new SecurityException("Helper release manifest JSON is malformed.", ex); }
    }

    private static CallerImageManifest ParseCallerImagePin(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new SecurityException("Caller image metadata must be a JSON object.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!fields.TryAdd(property.Name, property.Value)) throw new SecurityException("Caller image metadata contains a duplicate field.");
        string[] expected = ["relativePath", "size", "sha256"];
        if (fields.Count != expected.Length || expected.Any(name => !fields.ContainsKey(name)))
            throw new SecurityException("Caller image metadata schema does not match schema v4.");
        var relativePath = RequiredString(fields["relativePath"], "callerImage.relativePath");
        if (!string.Equals(relativePath, ProtectedCallerImagePin.ExpectedRelativePath, StringComparison.Ordinal))
            throw new SecurityException("Caller image path must be the fixed installed Bootstrapper path.");
        var size = RequiredSize(fields["size"], "callerImage.size", 1024L * 1024 * 1024);
        var hash = RequiredString(fields["sha256"], "callerImage.sha256");
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new SecurityException("Caller image SHA-256 pin is invalid.");
        return new CallerImageManifest(relativePath, size, hash);
    }

    private static VelopackSetupManifest ParseSetupPin(JsonElement value, int schema)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new SecurityException("Velopack Setup metadata must be a JSON object.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!fields.TryAdd(property.Name, property.Value))
                throw new SecurityException("Velopack Setup metadata contains a duplicate field.");
        string[] expected = schema == 2
            ? ["relativePath", "size", "sha256", "packageId", "version", "authenticodeSignerThumbprint"]
            : ["relativePath", "size", "sha256", "packageId", "version"];
        if (fields.Count != expected.Length || expected.Any(name => !fields.ContainsKey(name)))
            throw new SecurityException("Velopack Setup metadata schema does not match the supported version.");
        var relativePath = RequiredString(fields["relativePath"], "velopackSetup.relativePath");
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':') || relativePath.Contains('/') || relativePath.Contains('\\') ||
            !string.Equals(relativePath, "Setup.exe", StringComparison.Ordinal))
            throw new SecurityException("Velopack Setup path must be the fixed Setup.exe direct child of the protected helper bundle.");
        var size = RequiredSize(fields["size"], "velopackSetup.size", 1024L * 1024 * 1024);
        var hash = RequiredString(fields["sha256"], "velopackSetup.sha256");
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new SecurityException("Velopack Setup SHA-256 pin is invalid.");
        var packageId = RequiredString(fields["packageId"], "velopackSetup.packageId");
        if (packageId.Length > 128 || !packageId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            throw new SecurityException("Velopack package id is invalid.");
        var version = RequiredString(fields["version"], "velopackSetup.version");
        if (!Version.TryParse(version, out var parsedVersion) || parsedVersion.Major < 0 || parsedVersion.Minor < 0 ||
            parsedVersion.Build < 0 || parsedVersion.Revision >= 0 || !string.Equals(parsedVersion.ToString(3), version, StringComparison.Ordinal))
            throw new SecurityException("Velopack package version must be an exact three-part numeric version.");
        var signer = schema == 2 ? ParseSigner(fields["authenticodeSignerThumbprint"], "velopackSetup.authenticodeSignerThumbprint") : null;
        return new VelopackSetupManifest(relativePath, size, hash, packageId, version,
            schema == 2 ? ReleaseArtifactTrustMode.Authenticode : ReleaseArtifactTrustMode.SignedManifestHash, signer);
    }

    private static long RequiredSize(JsonElement value, string name, long maximum)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var size) || size <= 0 || size > maximum)
            throw new SecurityException($"Helper release manifest field '{name}' has an invalid size.");
        return size;
    }

    private static string ParseSigner(JsonElement value, string name)
    {
        var signer = RequiredString(value, name).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (signer.Length != 40 || !signer.All(Uri.IsHexDigit)) throw new SecurityException($"Authenticode thumbprint '{name}' is invalid.");
        return signer;
    }

    private static void ValidateInspectionPath(ProtectedFileInspection inspection, string expectedPath, string description)
    {
        if (!PathEquals(inspection.FinalPath, expectedPath))
            throw new SecurityException($"Opened {description} does not resolve to the protected machine path.");
    }

    private static string RequiredString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new SecurityException($"Helper release manifest field '{name}' is missing or invalid.");
        return value.GetString()!;
    }

    private static void ValidateResolvedPath(string path, string leafName, string directory, string description)
    {
        var expected = Path.GetFullPath(Path.Combine(directory, leafName));
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase) || !PathEquals(path, expected) ||
            !string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            throw new SecurityException($"Resolved {description} is not a direct child of the protected machine helper directory.");
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static string NormalizeThumbprint(string value) => value.Replace(" ", string.Empty, StringComparison.Ordinal);
    private sealed record ReleaseManifest(string Version, string HelperRelativePath, long Size, string Sha256, ReleaseArtifactTrustMode TrustMode, string? SignerThumbprint, VelopackSetupManifest VelopackSetup, CallerImageManifest? CallerImage);
    private sealed record VelopackSetupManifest(string RelativePath, long Size, string Sha256, string PackageId, string Version, ReleaseArtifactTrustMode TrustMode, string? SignerThumbprint);
    private sealed record CallerImageManifest(string RelativePath, long Size, string Sha256);
}

/// <summary>Exact installed Bootstrapper pin carried by a detached-signature verified schema-v4 manifest.</summary>
public sealed record ProtectedCallerImagePin(string RelativePath, long Size, string Sha256)
{
    public const string ExpectedRelativePath = "StructuraConnectorInstaller\\Bootstrapper\\Connector.Upgrade.Bootstrapper.exe";
}

/// <summary>An exact machine-owned Velopack Setup.exe, verified against the signed release manifest.</summary>
public sealed record VelopackSetupPin(string AbsolutePath, long Size, string Sha256, string PackageId, string Version, ReleaseArtifactTrustMode TrustMode, string? SignerThumbprint)
{
    public VelopackSetupPin(string absolutePath, long size, string sha256, string packageId, string version, string signerThumbprint)
        : this(absolutePath, size, sha256, packageId, version, ReleaseArtifactTrustMode.Authenticode, signerThumbprint) { }
}

internal sealed record ProtectedFileInspection(string FinalPath, long Size, string Sha256, string? SignerThumbprint);

internal interface IHelperReleaseTrustRuntime
{
    string GetProtectedHelperDirectory();
    (byte[] Manifest, byte[] Signature) ReadInstalledManifest(int maximumManifestBytes, int signatureBytes);
    string ResolveProtectedHelperPath(string relativePath);
    ProtectedFileInspection InspectProtectedFile(string expectedPath, bool verifyAuthenticode);
}

internal sealed class WindowsHelperReleaseTrustRuntime : IHelperReleaseTrustRuntime
{
    internal static readonly WindowsHelperReleaseTrustRuntime Instance = new();
    private static readonly HashSet<string> TrustedOwners = new(StringComparer.Ordinal)
    {
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
        new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464").Value
    };
    private const FileSystemRights MutationRights = (FileSystemRights)0x10000000 | (FileSystemRights)0x40000000 |
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    private const string ManifestName = "helper-release.json";
    private const string SignatureName = "helper-release.sig";

    public string GetProtectedHelperDirectory()
    {
        EnsureWindows();
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData)) throw new SecurityException("Machine ProgramData location is unavailable.");
        return Path.GetFullPath(Path.Combine(programData, "StructuraConnectorInstaller", "Helper"));
    }

    public (byte[] Manifest, byte[] Signature) ReadInstalledManifest(int maximumManifestBytes, int signatureBytes)
    {
        EnsureWindows();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles)) throw new SecurityException("Machine Program Files location is unavailable.");
        var root = Path.GetFullPath(programFiles);
        var directory = Path.Combine(root, "StructuraConnectorInstaller", "HelperRelease");
        AssertProtectedPath(directory, root, isDirectory: true);
        var manifest = ReadProtectedFile(Path.Combine(directory, ManifestName), root, maximumManifestBytes);
        var signature = ReadProtectedFile(Path.Combine(directory, SignatureName), root, signatureBytes);
        if (signature.Length != signatureBytes) throw new SecurityException("Helper release signature has an invalid size.");
        return (manifest, signature);
    }

    public string ResolveProtectedHelperPath(string relativePath)
    {
        var directory = GetProtectedHelperDirectory();
        var path = Path.GetFullPath(Path.Combine(directory, relativePath));
        AssertProtectedPath(path, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), isDirectory: false);
        return path;
    }

    public ProtectedFileInspection InspectProtectedFile(string expectedPath, bool verifyAuthenticode)
    {
        EnsureWindows();
        var directory = GetProtectedHelperDirectory();
        if (!string.Equals(Path.GetDirectoryName(expectedPath), directory, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Protected release image is not a direct child of the protected helper directory.");
        AssertProtectedPath(expectedPath, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), isDirectory: false);
        using var stream = new FileStream(expectedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        var finalPath = GetFinalPath(stream.SafeFileHandle);
        if (!PathEquals(finalPath, expectedPath)) throw new SecurityException("Protected release image handle resolved to another path.");
        stream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        var signer = verifyAuthenticode ? VerifyAuthenticode(stream.SafeFileHandle, expectedPath) : null;
        AssertProtectedPath(expectedPath, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), isDirectory: false);
        return new ProtectedFileInspection(finalPath, stream.Length, hash, signer);
    }

    private static byte[] ReadProtectedFile(string path, string root, int maximumBytes)
    {
        AssertProtectedPath(path, root, isDirectory: false);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumBytes) throw new SecurityException("Protected helper release file has an invalid size.");
        if (!PathEquals(GetFinalPath(stream.SafeFileHandle), path)) throw new SecurityException("Protected helper release file handle resolved to another path.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        AssertProtectedPath(path, root, isDirectory: false);
        return bytes;
    }

    private static void AssertProtectedPath(string path, string trustedRoot, bool isDirectory)
    {
        var root = Path.GetFullPath(trustedRoot);
        var full = Path.GetFullPath(path);
        if (!PathEquals(full, path)) throw new SecurityException("Protected path is not normalized.");
        var relative = Path.GetRelativePath(root, full);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new SecurityException("Protected path escapes its machine root.");
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new SecurityException("Protected path must be below its machine root.");
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            var final = i == parts.Length - 1;
            if (final && !isDirectory)
            {
                if (!File.Exists(current)) throw new FileNotFoundException("Protected machine file is missing.", current);
                if ((File.GetAttributes(current) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new SecurityException("Protected machine file is a directory or reparse point.");
            }
            else
            {
                if (!Directory.Exists(current)) throw new DirectoryNotFoundException("Protected machine directory is missing.");
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new SecurityException("Protected machine path contains a reparse point.");
            }
            AssertAcl(current);
        }
    }

    private static void AssertAcl(string path)
    {
        FileSystemSecurity security = Directory.Exists(path)
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path), AccessControlSections.Owner | AccessControlSections.Access)
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path), AccessControlSections.Owner | AccessControlSections.Access);
        if (!security.AreAccessRulesProtected) throw new UnauthorizedAccessException("Protected machine path inherits its DACL.");
        var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
        if (owner is null || !TrustedOwners.Contains(owner)) throw new UnauthorizedAccessException("Protected machine path has an untrusted owner.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                !TrustedOwners.Contains(rule.IdentityReference.Value) && (rule.FileSystemRights & MutationRights) != 0)
                throw new UnauthorizedAccessException("Protected machine path grants mutation rights to an untrusted principal.");
    }

    private static string VerifyAuthenticode(SafeFileHandle handle, string path)
    {
        var info = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), Path = path, File = handle.DangerousGetHandle() };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            var data = new WinTrustData { Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, UnionChoice = 1, FileInfo = pointer, ProviderFlags = 0x80 };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(IntPtr.Zero, ref action, ref data) != 0) throw new SecurityException("Helper Authenticode verification failed.");
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            if (!certificate.Verify() || string.IsNullOrWhiteSpace(certificate.Thumbprint)) throw new SecurityException("Helper signer certificate is untrusted.");
            return certificate.Thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var path = new System.Text.StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        var value = path.ToString();
        return value.StartsWith("\\\\?\\", StringComparison.Ordinal) ? value[4..] : value;
    }
    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static void EnsureWindows() { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Helper release trust requires Windows."); }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WinTrustFileInfo { public uint Size; public string? Path; public IntPtr File; public IntPtr KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct WinTrustData { public uint Size; public IntPtr PolicyCallbackData; public IntPtr SipClientData; public uint UiChoice; public uint RevocationChecks; public uint UnionChoice; public IntPtr FileInfo; public uint StateAction; public IntPtr StateData; public IntPtr UrlReference; public uint ProviderFlags; public uint UiContext; public IntPtr SignatureSettings; }
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, System.Text.StringBuilder path, uint length, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
}
