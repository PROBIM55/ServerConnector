using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;
using Microsoft.Win32;

namespace Connector.Upgrade.NetBirdWindowsState;

internal sealed class WindowsNetBirdNativeInventory
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoMoreItems = 259;
    private const string EveryoneSid = "S-1-1-0";
    private const int MachineContext = 4;
    private const int UserContexts = 1 | 2;
    private const string ProductNameProperty = "ProductName";
    private const string PublisherProperty = "Publisher";
    private const string VersionProperty = "VersionString";
    private const string LocalPackageProperty = "LocalPackage";

    private readonly string _cliPath;
    private readonly string _configurationPath;
    private readonly WindowsNetBirdProtectedStorage _storage;

    internal WindowsNetBirdNativeInventory(
        string cliPath,
        string configurationPath,
        WindowsNetBirdProtectedStorage storage)
    {
        _cliPath = Path.GetFullPath(cliPath);
        _configurationPath = Path.GetFullPath(configurationPath);
        _storage = storage;
    }

    internal IReadOnlyList<WindowsNetBirdRegistration> ReadMsiRegistrations()
    {
        var officialProducts = EnumerateRelatedProducts(WindowsNetBirdMachineStatePort.OfficialUpgradeCode)
            .ToHashSet();
        var registrations = EnumerateProducts(MachineContext, userSid: null)
            .Concat(EnumerateProducts(UserContexts, EveryoneSid))
            .DistinctBy(value => (value.ProductCode, value.Context, value.UserSid), RegistrationKeyComparer.Instance)
            .ToArray();
        var result = new List<WindowsNetBirdRegistration>();
        foreach (var value in registrations)
        {
            var productName = ReadProductInfo(
                value.ProductCode,
                value.Context,
                value.UserSid,
                ProductNameProperty);
            if (!productName.StartsWith("NetBird", StringComparison.OrdinalIgnoreCase))
                continue;
            var manufacturer = ReadProductInfo(
                value.ProductCode,
                value.Context,
                value.UserSid,
                PublisherProperty);
            var version = ReadProductInfo(
                value.ProductCode,
                value.Context,
                value.UserSid,
                VersionProperty);
            var localPackage = ReadProductInfo(
                value.ProductCode,
                value.Context,
                value.UserSid,
                LocalPackageProperty,
                allowEmpty: true);
            result.Add(new WindowsNetBirdRegistration(
                new NetBirdMsiPackageIdentity(
                    value.ProductCode,
                    officialProducts.Contains(value.ProductCode)
                        ? WindowsNetBirdMachineStatePort.OfficialUpgradeCode
                        : Guid.Empty,
                    version,
                    manufacturer,
                    productName),
                value.Context == MachineContext ? "Machine" : value.Context == 1 ? "UserManaged" : "UserUnmanaged",
                value.Context == MachineContext ? null : value.UserSid,
                string.IsNullOrWhiteSpace(localPackage) ? null : Path.GetFullPath(localPackage)));
        }
        return result
            .OrderBy(value => value.Package.ProductCode)
            .ThenBy(value => value.RegistrationContext, StringComparer.Ordinal)
            .ThenBy(value => value.UserSid, StringComparer.Ordinal)
            .ToArray();
    }

    internal WindowsNetBirdServiceSnapshot ReadService()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\NetBird", writable: false);
        if (key is null)
            return new WindowsNetBirdServiceSnapshot(
                true, false, "NetBird", string.Empty, -1, string.Empty, false, string.Empty);

        var imagePath = key.GetValue("ImagePath", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
            ?? throw new InvalidDataException("The NetBird service ImagePath is not a string.");
        imagePath = Environment.ExpandEnvironmentVariables(imagePath);
        var startType = Convert.ToInt32(key.GetValue("Start", -1), System.Globalization.CultureInfo.InvariantCulture);
        var serviceType = Convert.ToInt32(key.GetValue("Type", -1), System.Globalization.CultureInfo.InvariantCulture);
        var errorControl = Convert.ToInt32(
            key.GetValue("ErrorControl", -1),
            System.Globalization.CultureInfo.InvariantCulture);
        var account = key.GetValue("ObjectName", string.Empty) as string
            ?? throw new InvalidDataException("The NetBird service account is not a string.");
        var arguments = ParseCommandLine(imagePath);
        var exact = arguments.Length == 7 &&
            PathsEqual(arguments[0], _cliPath) &&
            string.Equals(arguments[1], "service", StringComparison.Ordinal) &&
            string.Equals(arguments[2], "run", StringComparison.Ordinal) &&
            string.Equals(arguments[3], "config", StringComparison.Ordinal) &&
            PathsEqual(arguments[4], _configurationPath) &&
            string.Equals(arguments[5], "log-level", StringComparison.Ordinal) &&
            string.Equals(arguments[6], "info", StringComparison.Ordinal) &&
            startType == 2 &&
            serviceType == 16 &&
            errorControl == 1 &&
            string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase);
        var canonical = string.Join('\0', new[]
        {
            "NetBird",
            arguments.Length == 0 ? string.Empty : NormalizePathForEvidence(arguments[0]),
            string.Join("\u001f", arguments.Skip(1)),
            startType.ToString(System.Globalization.CultureInfo.InvariantCulture),
            serviceType.ToString(System.Globalization.CultureInfo.InvariantCulture),
            errorControl.ToString(System.Globalization.CultureInfo.InvariantCulture),
            account,
        });
        var identity = "netbird-service-v1:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new WindowsNetBirdServiceSnapshot(
            true, true, "NetBird", imagePath, startType, account, exact, identity, serviceType, errorControl);
    }

    internal WindowsNetBirdCliSnapshot ReadCli()
    {
        if (!File.Exists(_cliPath))
            return new WindowsNetBirdCliSnapshot(
                true, false, _cliPath, string.Empty, string.Empty, false, false, string.Empty, string.Empty);
        var info = new FileInfo(_cliPath);
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The NetBird CLI is not a regular file.");
        var installRoot = Path.GetDirectoryName(_cliPath)
            ?? throw new InvalidDataException("The NetBird CLI has no installation directory.");
        var pathSecure = _storage.IsInstalledBinaryPathSecure(_cliPath, installRoot);
        var versionInfo = FileVersionInfo.GetVersionInfo(_cliPath);
        var version = !string.IsNullOrWhiteSpace(versionInfo.ProductVersion)
            ? versionInfo.ProductVersion
            : versionInfo.FileVersion ?? string.Empty;
        string sha;
        using (var stream = new FileStream(
                   _cliPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   128 * 1024,
                   FileOptions.SequentialScan))
        {
            sha = Convert.ToHexString(SHA256.HashData(stream));
        }
        var signer = VerifyAuthenticode(_cliPath);
        var exactSigner = signer.Trusted &&
            signer.Subject.StartsWith("CN=NetBird GmbH,", StringComparison.Ordinal);
        return new WindowsNetBirdCliSnapshot(
            true,
            true,
            _cliPath,
            version,
            sha,
            pathSecure,
            exactSigner,
            signer.Subject,
            signer.Thumbprint);
    }

    private static IReadOnlyList<Guid> EnumerateRelatedProducts(Guid upgradeCode)
    {
        var result = new List<Guid>();
        for (uint index = 0; ; index++)
        {
            var text = new StringBuilder(39);
            var status = MsiEnumRelatedProductsW(FormatGuid(upgradeCode), 0, index, text);
            if (status == ErrorNoMoreItems)
                return result;
            ThrowOnMsiError(status, "MsiEnumRelatedProducts");
            if (!Guid.TryParse(text.ToString(), out var productCode) || productCode == Guid.Empty)
                throw new InvalidDataException("Windows Installer returned an invalid related ProductCode.");
            result.Add(productCode);
        }
    }

    private static IEnumerable<EnumeratedProduct> EnumerateProducts(int context, string? userSid)
    {
        for (uint index = 0; ; index++)
        {
            var productText = new StringBuilder(39);
            var sidLength = 0u;
            var status = MsiEnumProductsExW(
                null,
                userSid,
                context,
                index,
                productText,
                out var installedContext,
                null,
                ref sidLength);
            if (status == ErrorNoMoreItems)
                yield break;
            StringBuilder? sid = null;
            if (status == ErrorMoreData)
            {
                sidLength++;
                sid = new StringBuilder(checked((int)sidLength));
                status = MsiEnumProductsExW(
                    null,
                    userSid,
                    context,
                    index,
                    productText,
                    out installedContext,
                    sid,
                    ref sidLength);
            }
            ThrowOnMsiError(status, "MsiEnumProductsEx");
            if (!Guid.TryParse(productText.ToString(), out var productCode) || productCode == Guid.Empty ||
                installedContext is not (1 or 2 or 4))
                throw new InvalidDataException("Windows Installer returned an invalid product registration.");
            var returnedSid = sid?.ToString();
            if (installedContext == MachineContext && !string.IsNullOrEmpty(returnedSid))
                throw new InvalidDataException("Windows Installer returned a SID for a machine product.");
            if (installedContext != MachineContext && string.IsNullOrWhiteSpace(returnedSid))
                throw new InvalidDataException("Windows Installer omitted the user SID for a per-user product.");
            yield return new EnumeratedProduct(productCode, installedContext, returnedSid);
        }
    }

    private static string ReadProductInfo(
        Guid productCode,
        int context,
        string? userSid,
        string property,
        bool allowEmpty = false)
    {
        uint length = 0;
        var querySid = context == MachineContext ? null : userSid;
        var status = MsiGetProductInfoExW(
            FormatGuid(productCode),
            querySid,
            context,
            property,
            null,
            ref length);
        if (status != ErrorMoreData && status != ErrorSuccess)
            ThrowOnMsiError(status, "MsiGetProductInfoEx(length)");
        var text = new StringBuilder(checked((int)length + 1));
        length++;
        status = MsiGetProductInfoExW(
            FormatGuid(productCode),
            querySid,
            context,
            property,
            text,
            ref length);
        ThrowOnMsiError(status, "MsiGetProductInfoEx(value)");
        var value = text.ToString();
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Windows Installer returned an empty {property} value.");
        return value;
    }

    private static AuthenticodeResult VerifyAuthenticode(string path)
    {
        var info = new WinTrustFileInfo
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = path,
        };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(info, pointer, fDeleteOld: false);
            var data = new WinTrustData
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                UnionChoice = 1,
                FileInfo = pointer,
                ProviderFlags = 0x80,
            };
            var action = WinTrustAction;
            var trusted = WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            var subject = certificate.Subject ?? string.Empty;
            var thumbprint = NormalizeThumbprint(certificate.Thumbprint);
            return new AuthenticodeResult(
                trusted && certificate.Verify() &&
                subject.StartsWith("CN=NetBird GmbH,", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(thumbprint),
                subject,
                thumbprint);
        }
        catch (CryptographicException)
        {
            return new AuthenticodeResult(false, string.Empty, string.Empty);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static string[] ParseCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return [];
        var pointer = CommandLineToArgvW(commandLine, out var count);
        if (pointer == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not parse the NetBird service ImagePath.");
        try
        {
            var result = new string[count];
            for (var index = 0; index < count; index++)
            {
                var value = Marshal.ReadIntPtr(pointer, index * IntPtr.Size);
                result[index] = Marshal.PtrToStringUni(value)
                    ?? throw new InvalidDataException("The NetBird service ImagePath contains an invalid argument.");
            }
            return result;
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePathForEvidence(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();

    private static string NormalizeThumbprint(string? value) =>
        (value ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static string FormatGuid(Guid value) => $"{{{value:D}}}".ToUpperInvariant();

    private static void ThrowOnMsiError(uint status, string operation)
    {
        if (status != ErrorSuccess)
            throw new Win32Exception(checked((int)status), $"{operation} failed with Windows Installer error {status}.");
    }

    private sealed record EnumeratedProduct(Guid ProductCode, int Context, string? UserSid);
    private sealed record AuthenticodeResult(bool Trusted, string Subject, string Thumbprint);

    private sealed class RegistrationKeyComparer : IEqualityComparer<(Guid ProductCode, int Context, string? UserSid)>
    {
        internal static readonly RegistrationKeyComparer Instance = new();
        public bool Equals(
            (Guid ProductCode, int Context, string? UserSid) left,
            (Guid ProductCode, int Context, string? UserSid) right) =>
            left.ProductCode == right.ProductCode && left.Context == right.Context &&
            string.Equals(left.UserSid, right.UserSid, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((Guid ProductCode, int Context, string? UserSid) value) =>
            HashCode.Combine(
                value.ProductCode,
                value.Context,
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.UserSid ?? string.Empty));
    }

    private static readonly Guid WinTrustAction = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructureSize;
        public string? FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructureSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiEnumRelatedProductsW(
        string upgradeCode,
        uint reserved,
        uint index,
        StringBuilder productCode);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiEnumProductsExW(
        string? productCode,
        string? userSid,
        int context,
        uint index,
        StringBuilder installedProductCode,
        out int installedContext,
        StringBuilder? installedUserSid,
        ref uint userSidLength);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiGetProductInfoExW(
        string productCode,
        string? userSid,
        int context,
        string property,
        StringBuilder? value,
        ref uint valueLength);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid actionId, ref WinTrustData data);
}
