using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using Connector.Upgrade.MutualMachineChannel;
using Connector.Upgrade.HelperReleaseTrust;

namespace Connector.Upgrade.ProtectedCallerImage;

/// <summary>
/// Trust source for the installed, unelevated Connector caller. Current releases use a caller
/// pin from the verified detached helper-release manifest; embedded schema 1/2 pins remain for
/// older releases. Runtime configuration, command-line arguments and environment variables are
/// deliberately not consulted.
/// </summary>
public sealed class ProtectedCallerImagePinSource : ITrustedCallerImagePinSource
{
    private const string ManifestResourceName = "Connector.Upgrade.ProtectedCallerImage.TrustedCallerImagePinManifest.json";
    private readonly Func<Stream?> _manifestLoader;
    private readonly ICallerImageTrustRuntime _runtime;
    private readonly Func<ProtectedCallerImagePin?>? _verifiedCallerPinLoader;

    public ProtectedCallerImagePinSource() : this(LoadEmbeddedManifest, WindowsCallerImageTrustRuntime.Instance,
        () => new HelperReleasePinSource().GetCallerImagePin()) { }

    internal ProtectedCallerImagePinSource(Func<Stream?> manifestLoader, ICallerImageTrustRuntime runtime)
        : this(manifestLoader, runtime, null) { }

    internal ProtectedCallerImagePinSource(Func<Stream?> manifestLoader, ICallerImageTrustRuntime runtime,
        Func<ProtectedCallerImagePin?>? verifiedCallerPinLoader)
    {
        _manifestLoader = manifestLoader ?? throw new ArgumentNullException(nameof(manifestLoader));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _verifiedCallerPinLoader = verifiedCallerPinLoader;
    }

    public void AssertTrustedCaller(SafeProcessHandle retainedCaller, SecurityIdentifier expectedUserSid)
    {
        ArgumentNullException.ThrowIfNull(retainedCaller);
        ArgumentNullException.ThrowIfNull(expectedUserSid);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Caller image pin checks require Windows.");
        if (retainedCaller.IsClosed || retainedCaller.IsInvalid)
            throw new SecurityException("Retained caller process handle is unavailable.");

        var signedPin = _verifiedCallerPinLoader?.Invoke();
        var pin = signedPin is null
            ? ReadManifest()
            : new CallerImagePin(3, signedPin.RelativePath, signedPin.Size, signedPin.Sha256, string.Empty);
        pin.Validate();
        var expectedPath = _runtime.GetExpectedInstallationPath(pin.RelativeImagePath);
        var first = _runtime.ReadCallerProcess(retainedCaller);
        AssertProcess(first, expectedPath, expectedUserSid);
        var image = _runtime.InspectProtectedImage(expectedPath, pin.SchemaVersion == 1);
        if (!PathEquals(image.FinalPath, expectedPath))
            throw new SecurityException("Opened caller image does not resolve to the pinned installation path.");
        if (pin.SchemaVersion == 3 && image.Size != pin.Size)
            throw new SecurityException("Caller image size does not match the signed release manifest.");
        if (!string.Equals(image.Sha256, pin.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Caller image SHA-256 does not match its trusted release pin.");
        if (pin.SchemaVersion == 1 && !string.Equals(NormalizeThumbprint(image.SignerThumbprint ?? string.Empty), NormalizeThumbprint(pin.AuthenticodeSignerThumbprint), StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Caller image Authenticode signer does not match the embedded release pin.");

        // Catch exit or process replacement during file inspection using the retained process handle.
        var second = _runtime.ReadCallerProcess(retainedCaller);
        AssertProcess(second, expectedPath, expectedUserSid);
    }

    private CallerImagePin ReadManifest()
    {
        using var stream = _manifestLoader();
        if (stream is null) throw new SecurityException("Embedded caller image manifest is missing; caller trust fails closed.");
        try
        {
            var manifest = JsonSerializer.Deserialize<CallerImageManifest>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = false });
            if (manifest is null || manifest.SchemaVersion is not (1 or 2))
                throw new SecurityException("Embedded caller image manifest is invalid.");
            return new CallerImagePin(manifest.SchemaVersion, manifest.RelativeImagePath ?? string.Empty, 0, manifest.Sha256 ?? string.Empty,
                manifest.AuthenticodeSignerThumbprint ?? string.Empty);
        }
        catch (JsonException exception)
        {
            throw new SecurityException("Embedded caller image manifest cannot be parsed.", exception);
        }
    }

    private static Stream? LoadEmbeddedManifest() =>
        typeof(ProtectedCallerImagePinSource).Assembly.GetManifestResourceStream(ManifestResourceName);

    private static void AssertProcess(CallerProcessIdentity process, string expectedPath, SecurityIdentifier expectedSid)
    {
        if (!process.IsLive) throw new SecurityException("Original caller process is not live.");
        if (!PathEquals(process.ImagePath, expectedPath))
            throw new SecurityException("Original caller process image path does not match its trusted installation pin.");
        if (!process.UserSid.Equals(expectedSid))
            throw new SecurityException("Original caller process SID does not match the expected user SID.");
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeThumbprint(string value) => value.Replace(" ", string.Empty, StringComparison.Ordinal);

    private sealed class CallerImageManifest
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }
        [JsonPropertyName("relativeImagePath")]
        public string? RelativeImagePath { get; init; }
        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }
        [JsonPropertyName("authenticodeSignerThumbprint")]
        public string? AuthenticodeSignerThumbprint { get; init; }
    }
}

internal sealed record CallerImagePin(int SchemaVersion, string RelativeImagePath, long Size, string Sha256, string AuthenticodeSignerThumbprint)
{
    internal const string ExpectedRelativeImagePath = "StructuraConnectorInstaller\\Bootstrapper\\Connector.Upgrade.Bootstrapper.exe";

    internal void Validate()
    {
        if (!string.Equals(RelativeImagePath, ExpectedRelativeImagePath, StringComparison.OrdinalIgnoreCase) ||
            Path.IsPathRooted(RelativeImagePath) ||
            RelativeImagePath.Contains(':') || RelativeImagePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is ".." or "." or ""))
            throw new SecurityException("Embedded caller image path must be a safe relative installation path.");
        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
            throw new SecurityException("Embedded caller image SHA-256 pin is missing or invalid.");
        if (SchemaVersion == 3 && Size <= 0)
            throw new SecurityException("Signed caller image size pin is missing or invalid.");
        var signer = AuthenticodeSignerThumbprint.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (SchemaVersion == 1 && (signer.Length != 40 || !signer.All(Uri.IsHexDigit)))
            throw new SecurityException("Embedded caller image Authenticode signer pin is missing or invalid.");
        if (SchemaVersion == 2 && signer.Length != 0)
            throw new SecurityException("Schema 2 caller image pins must not specify an Authenticode signer.");
    }
}

internal sealed record CallerProcessIdentity(bool IsLive, string ImagePath, SecurityIdentifier UserSid);
internal sealed record CallerImageInspection(string FinalPath, long Size, string Sha256, string? SignerThumbprint);

internal interface ICallerImageTrustRuntime
{
    string GetExpectedInstallationPath(string relativeImagePath);
    CallerProcessIdentity ReadCallerProcess(SafeProcessHandle process);
    CallerImageInspection InspectProtectedImage(string expectedPath, bool verifyAuthenticode);
}

internal sealed class WindowsCallerImageTrustRuntime : ICallerImageTrustRuntime
{
    internal static readonly WindowsCallerImageTrustRuntime Instance = new();
    private static readonly HashSet<string> TrustedOwners = new(StringComparer.Ordinal)
    {
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
        new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464").Value
    };
    private const FileSystemRights MutationRights = (FileSystemRights)0x10000000 | (FileSystemRights)0x40000000 |
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    private const uint WaitTimeout = 258;
    private const uint WaitObject0 = 0;

    public string GetExpectedInstallationPath(string relativeImagePath)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles)) throw new SecurityException("Machine Program Files location is unavailable.");
        var root = Path.GetFullPath(programFiles);
        var expected = Path.GetFullPath(Path.Combine(root, relativeImagePath));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!expected.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Caller image path escapes the machine installation root.");
        return expected;
    }

    public CallerProcessIdentity ReadCallerProcess(SafeProcessHandle process)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Caller process inspection requires Windows.");
        if (process.IsClosed || process.IsInvalid || WaitForSingleObject(process, 0) != WaitTimeout)
            return new CallerProcessIdentity(false, string.Empty, new SecurityIdentifier(WellKnownSidType.NullSid, null));
        var capacity = 32768;
        var path = new System.Text.StringBuilder(capacity);
        var length = (uint)capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!OpenProcessToken(process, TokenQuery, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var required);
            if (required == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var buffer = Marshal.AllocHGlobal((int)required);
            try
            {
                if (!GetTokenInformation(token, TokenUser, buffer, required, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var tokenUser = Marshal.PtrToStructure<TokenUserBuffer>(buffer);
                if (!ConvertSidToStringSid(tokenUser.User.Sid, out var sidText)) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var sid = new SecurityIdentifier(Marshal.PtrToStringUni(sidText)!);
                    if (WaitForSingleObject(process, 0) != WaitTimeout) throw new SecurityException("Original caller process exited during identity inspection.");
                    return new CallerProcessIdentity(true, path.ToString(), sid);
                }
                finally { LocalFree(sidText); }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    public CallerImageInspection InspectProtectedImage(string expectedPath, bool verifyAuthenticode)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Caller image inspection requires Windows.");
        AssertProtectedPath(expectedPath);
        using var stream = new FileStream(expectedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        var finalPath = GetFinalPath(stream.SafeFileHandle);
        if (!PathEquals(finalPath, expectedPath)) throw new SecurityException("Caller image handle path does not match its pinned path.");
        stream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        var signer = verifyAuthenticode ? VerifyAuthenticode(stream.SafeFileHandle, expectedPath) : null;
        AssertProtectedPath(expectedPath);
        return new CallerImageInspection(finalPath, stream.Length, hash, signer);
    }

    private static void AssertProtectedPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !PathEquals(path, Path.GetFullPath(path)))
            throw new SecurityException("Caller image path is not normalized and absolute.");
        var programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var prefix = programFiles.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new SecurityException("Caller image is outside Program Files.");
        var current = programFiles;
        foreach (var segment in Path.GetRelativePath(programFiles, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new SecurityException("Caller image path contains a reparse point.");
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (!isDirectory && !string.Equals(current, path, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException("Caller image installation path contains a non-directory component.");
            FileSystemSecurity security = isDirectory
                ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(current), AccessControlSections.Owner | AccessControlSections.Access)
                : FileSystemAclExtensions.GetAccessControl(new FileInfo(current), AccessControlSections.Owner | AccessControlSections.Access);
            AssertAcl(security);
        }
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new SecurityException("Caller image is not a regular file.");
    }

    private static void AssertAcl(FileSystemSecurity security)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !TrustedOwners.Contains(owner.Value))
            throw new UnauthorizedAccessException("Caller image installation path has an untrusted owner.");
        if (!security.AreAccessRulesProtected) throw new UnauthorizedAccessException("Caller image installation path inherits its DACL.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                !TrustedOwners.Contains(rule.IdentityReference.Value) && (rule.FileSystemRights & MutationRights) != 0)
                throw new UnauthorizedAccessException("Caller image installation path grants mutation rights to an untrusted principal.");
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var path = new System.Text.StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        var value = path.ToString();
        return value.StartsWith("\\\\?\\", StringComparison.Ordinal) ? value[4..] : value;
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
            if (WinVerifyTrust(IntPtr.Zero, ref action, ref data) != 0) throw new SecurityException("Caller image Authenticode verification failed.");
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            if (!certificate.Verify() || string.IsNullOrWhiteSpace(certificate.Thumbprint)) throw new SecurityException("Caller image signer certificate is untrusted.");
            return certificate.Thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenUserBuffer { public SidAndAttributes User; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WinTrustFileInfo { public uint Size; public string? Path; public IntPtr File; public IntPtr KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct WinTrustData { public uint Size; public IntPtr PolicyCallbackData; public IntPtr SipClientData; public uint UiChoice; public uint RevocationChecks; public uint UnionChoice; public IntPtr FileInfo; public uint StateAction; public IntPtr StateData; public IntPtr UrlReference; public uint ProviderFlags; public uint UiContext; public IntPtr SignatureSettings; }

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, System.Text.StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr buffer, uint length, out uint returned);
    [DllImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr text);
    [DllImport("kernel32.dll", EntryPoint = "LocalFree")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, System.Text.StringBuilder path, uint length, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
}
