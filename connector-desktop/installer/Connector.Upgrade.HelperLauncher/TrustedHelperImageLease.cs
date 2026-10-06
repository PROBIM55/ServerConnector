using System.ComponentModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Connector.Upgrade.HelperLauncher;

public enum ReleaseArtifactTrustMode
{
    Authenticode = 1,
    SignedManifestHash = 2,
}

/// <summary>Exact deployment pins for the one installed helper executable.</summary>
public sealed record HelperImagePin(
    string AbsolutePath,
    long SizeBytes,
    string Sha256,
    ReleaseArtifactTrustMode TrustMode,
    string? SignerThumbprint)
{
    /// <summary>Compatibility constructor for existing v2 signed-helper callers.</summary>
    public HelperImagePin(string absolutePath, string sha256, string signerThumbprint)
        : this(absolutePath, 0, sha256, ReleaseArtifactTrustMode.Authenticode, signerThumbprint) { }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(AbsolutePath) || !Path.IsPathFullyQualified(AbsolutePath) ||
            !string.Equals(Path.GetFullPath(AbsolutePath), AbsolutePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Helper image path must be a normalized absolute path.", nameof(AbsolutePath));
        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Helper image requires an exact SHA-256 pin.", nameof(Sha256));
        if (TrustMode is not (ReleaseArtifactTrustMode.Authenticode or ReleaseArtifactTrustMode.SignedManifestHash))
            throw new ArgumentException("Helper image trust mode is unsupported.", nameof(TrustMode));
        if (TrustMode == ReleaseArtifactTrustMode.SignedManifestHash && SizeBytes <= 0)
            throw new ArgumentException("A signed-manifest helper pin requires an exact positive file length.", nameof(SizeBytes));
        if (SizeBytes < 0)
            throw new ArgumentException("Helper image length cannot be negative.", nameof(SizeBytes));
        if (TrustMode == ReleaseArtifactTrustMode.Authenticode &&
            (string.IsNullOrWhiteSpace(SignerThumbprint) ||
             SignerThumbprint.Replace(" ", string.Empty, StringComparison.Ordinal).Length != 40 ||
             !SignerThumbprint.Replace(" ", string.Empty, StringComparison.Ordinal).All(Uri.IsHexDigit)))
            throw new ArgumentException("Helper image requires an exact Authenticode signer thumbprint pin.", nameof(SignerThumbprint));
        if (TrustMode == ReleaseArtifactTrustMode.SignedManifestHash && SignerThumbprint is not null)
            throw new ArgumentException("A signed-manifest hash pin cannot contain signer evidence.", nameof(SignerThumbprint));
    }
}

/// <summary>
/// Holds a read handle that denies write/delete sharing for the lifetime of a launch attempt.
/// The image is rehashed and its final path rechecked immediately before ShellExecuteEx.
/// </summary>
public sealed class TrustedHelperImageLease : IHelperImageLease
{
    private readonly FileStream _stream;
    private readonly HelperImagePin _pin;

    private TrustedHelperImageLease(FileStream stream, HelperImagePin pin)
    {
        _stream = stream;
        _pin = pin;
    }

    public string ImagePath => _pin.AbsolutePath;

    public static TrustedHelperImageLease Open(HelperImagePin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        pin.Validate();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Elevated helper launch is available only on Windows.");

        ProtectedHelperPath.AssertTrusted(pin.AbsolutePath);
        var stream = new FileStream(pin.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.SequentialScan);
        try
        {
            AssertHandlePath(stream.SafeFileHandle, pin.AbsolutePath);
            VerifyImage(stream, pin);
            ProtectedHelperPath.AssertTrusted(pin.AbsolutePath);
            return new TrustedHelperImageLease(stream, pin);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void RevalidateForLaunch()
    {
        if (_stream.SafeFileHandle.IsClosed || _stream.SafeFileHandle.IsInvalid)
            throw new InvalidOperationException("Trusted helper image lease is closed.");
        ProtectedHelperPath.AssertTrusted(_pin.AbsolutePath);
        AssertHandlePath(_stream.SafeFileHandle, _pin.AbsolutePath);
        VerifyImage(_stream, _pin);
    }

    public void Dispose() => _stream.Dispose();

    private static void VerifyImage(FileStream stream, HelperImagePin pin)
    {
        if (pin.SizeBytes > 0 && stream.Length != pin.SizeBytes)
            throw new InvalidDataException("Helper image length did not match the configured pin.");
        stream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(hash, pin.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Helper image SHA-256 did not match the configured pin.");

        if (pin.TrustMode == ReleaseArtifactTrustMode.Authenticode)
        {
            var signer = WindowsAuthenticode.Verify(stream.SafeFileHandle, pin.AbsolutePath);
            var expectedThumbprint = pin.SignerThumbprint!.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (!string.Equals(signer, expectedThumbprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Helper image Authenticode signer did not match the configured pin.");
        }
        stream.Position = 0;
    }

    private static void AssertHandlePath(SafeFileHandle handle, string expected)
    {
        var buffer = new System.Text.StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var final = buffer.ToString();
        if (final.StartsWith("\\\\?\\", StringComparison.Ordinal)) final = final[4..];
        if (!string.Equals(Path.GetFullPath(final), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Helper image handle no longer resolves to its pinned path.");
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, System.Text.StringBuilder path, uint length, uint flags);
}

internal static class ProtectedHelperPath
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TrustedInstallerSid = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly HashSet<string> TrustedOwners = new(StringComparer.Ordinal)
    {
        SystemSid.Value, AdminsSid.Value, TrustedInstallerSid.Value
    };
    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;
    private const FileSystemRights Mutation = GenericAll | GenericWrite | FileSystemRights.WriteData |
        FileSystemRights.AppendData | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    internal static void AssertTrusted(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Helper image must be at a normalized absolute path.");
        var programData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        var requiredRoot = Path.Combine(programData, "StructuraConnectorInstaller", "Helper");
        var rootPrefix = requiredRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(path), requiredRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Helper image must be directly inside the protected machine helper directory.");

        var current = programData;
        foreach (var segment in new[] { "StructuraConnectorInstaller", "Helper" })
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
                throw new DirectoryNotFoundException("Protected helper directory is missing.");
            RejectReparsePoint(current);
            AssertProtectedAcl(current, isDirectory: true);
        }
        RejectReparsePoint(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
            throw new InvalidDataException("Helper image is not a regular file.");
        AssertProtectedAcl(path, isDirectory: false);
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Helper image path cannot contain reparse points.");
    }

    private static void AssertProtectedAcl(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path), AccessControlSections.Owner | AccessControlSections.Access)
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path), AccessControlSections.Owner | AccessControlSections.Access);
        AssertAclTrusted(security);
    }

    internal static void AssertAclTrusted(FileSystemSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !TrustedOwners.Contains(owner.Value))
            throw new UnauthorizedAccessException("Helper image path has an untrusted owner.");
        if (!security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("Helper image path inherits its DACL.");

        AuthorizationRuleCollection rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                !TrustedOwners.Contains(rule.IdentityReference.Value) && HasMutationRights(rule.FileSystemRights))
                throw new UnauthorizedAccessException("Helper image path grants mutation rights to an untrusted principal.");
        }
    }

    internal static bool HasMutationRights(FileSystemRights rights) => (rights & Mutation) != 0;
}

internal static class WindowsAuthenticode
{
    private static readonly Guid VerifyAction = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    internal static string Verify(SafeFileHandle image, string path)
    {
        var fileInfo = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), Path = path, File = image.DangerousGetHandle() };
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var data = new WinTrustData { Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, UnionChoice = 1, FileInfo = fileInfoPointer, ProviderFlags = 0x80 };
            var action = VerifyAction;
            if (WinVerifyTrust(IntPtr.Zero, ref action, ref data) != 0)
                throw new InvalidDataException("Helper image Authenticode verification failed.");
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            if (!certificate.Verify() || string.IsNullOrWhiteSpace(certificate.Thumbprint))
                throw new InvalidDataException("Helper image signer certificate is not trusted.");
            return certificate.Thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal);
        }
        finally { Marshal.FreeHGlobal(fileInfoPointer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo { public uint Size; public string? Path; public IntPtr File; public IntPtr KnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData { public uint Size; public IntPtr PolicyCallbackData; public IntPtr SipClientData; public uint UiChoice; public uint RevocationChecks; public uint UnionChoice; public IntPtr FileInfo; public uint StateAction; public IntPtr StateData; public IntPtr UrlReference; public uint ProviderFlags; public uint UiContext; public IntPtr SignatureSettings; }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
}
