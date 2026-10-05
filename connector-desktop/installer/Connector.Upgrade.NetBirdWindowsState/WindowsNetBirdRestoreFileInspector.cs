using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Connector.Upgrade.Core;

namespace Connector.Upgrade.NetBirdWindowsState;

internal sealed record WindowsNetBirdRestoreFileInspection(
    NetBirdMsiPackageIdentity Package,
    string SignerSubject,
    string SignerThumbprint);

internal sealed class WindowsNetBirdRestoreFileInspector
{
    internal WindowsNetBirdRestoreFileInspection Inspect(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The protected NetBird restore MSI is not a regular file.");
        var package = ReadPackage(fullPath);
        if (package.ProductCode == Guid.Empty ||
            package.UpgradeCode != WindowsNetBirdMachineStatePort.OfficialUpgradeCode ||
            !string.Equals(package.Manufacturer, WindowsNetBirdMachineStatePort.OfficialManufacturer, StringComparison.Ordinal) ||
            !string.Equals(package.ProductName, WindowsNetBirdMachineStatePort.OfficialProductName, StringComparison.Ordinal))
            throw new InvalidDataException("The protected NetBird restore MSI is not an official package.");
        var signer = ReadTrustedSigner(fullPath);
        if (!signer.Subject.StartsWith("CN=NetBird GmbH,", StringComparison.Ordinal))
            throw new InvalidDataException("The protected NetBird restore MSI signer is not NetBird GmbH.");
        return new WindowsNetBirdRestoreFileInspection(package, signer.Subject, signer.Thumbprint);
    }

    private static NetBirdMsiPackageIdentity ReadPackage(string path)
    {
        uint database = 0;
        ThrowOnMsiError(MsiOpenDatabaseW(path, IntPtr.Zero, out database), "Could not open the prior NetBird MSI.");
        try
        {
            return new NetBirdMsiPackageIdentity(
                ParseGuid(ReadProperty(database, "ProductCode")),
                ParseGuid(ReadProperty(database, "UpgradeCode")),
                ReadProperty(database, "ProductVersion"),
                ReadProperty(database, "Manufacturer"),
                ReadProperty(database, "ProductName"));
        }
        finally
        {
            if (database != 0)
                MsiCloseHandle(database);
        }
    }

    private static string ReadProperty(uint database, string property)
    {
        uint view = 0;
        ThrowOnMsiError(
            MsiDatabaseOpenViewW(
                database,
                $"SELECT `Value` FROM `Property` WHERE `Property` = '{property}'",
                out view),
            "Could not query the prior NetBird MSI.");
        try
        {
            ThrowOnMsiError(MsiViewExecute(view, 0), "Could not execute the prior NetBird MSI query.");
            uint record = 0;
            ThrowOnMsiError(MsiViewFetch(view, out record), "A prior NetBird MSI property is missing.");
            try
            {
                uint length = 0;
                var measured = MsiRecordGetStringW(record, 1, null, ref length);
                if (measured is not (0 or 234))
                    ThrowOnMsiError(measured, "Could not measure a prior NetBird MSI property.");
                var value = new StringBuilder(checked((int)length + 1));
                var capacity = (uint)value.Capacity;
                ThrowOnMsiError(
                    MsiRecordGetStringW(record, 1, value, ref capacity),
                    "Could not read a prior NetBird MSI property.");
                if (string.IsNullOrWhiteSpace(value.ToString()))
                    throw new InvalidDataException("A prior NetBird MSI property is empty.");
                return value.ToString();
            }
            finally
            {
                if (record != 0)
                    MsiCloseHandle(record);
            }
        }
        finally
        {
            if (view != 0)
                MsiCloseHandle(view);
        }
    }

    private static AuthenticodeSigner ReadTrustedSigner(string path)
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
            if (WinVerifyTrust(IntPtr.Zero, ref action, ref data) != 0)
                throw new InvalidDataException("The prior NetBird MSI Authenticode signature is not trusted.");
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            if (!certificate.Verify() || string.IsNullOrWhiteSpace(certificate.Subject) ||
                string.IsNullOrWhiteSpace(certificate.Thumbprint))
                throw new InvalidDataException("The prior NetBird MSI signer certificate is not trusted.");
            return new AuthenticodeSigner(
                certificate.Subject,
                NormalizeThumbprint(certificate.Thumbprint));
        }
        catch (CryptographicException error)
        {
            throw new InvalidDataException("The prior NetBird MSI has no valid Authenticode signer.", error);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static Guid ParseGuid(string value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new InvalidDataException("The prior NetBird MSI identity contains an invalid GUID.");

    private static string NormalizeThumbprint(string value) =>
        value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static void ThrowOnMsiError(uint status, string message)
    {
        if (status != 0)
            throw new Win32Exception(unchecked((int)status), message);
    }

    private sealed record AuthenticodeSigner(string Subject, string Thumbprint);

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
    private static extern uint MsiOpenDatabaseW(string path, IntPtr persistence, out uint database);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiDatabaseOpenViewW(uint database, string query, out uint view);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewExecute(uint view, uint record);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewFetch(uint view, out uint record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiRecordGetStringW(
        uint record,
        uint field,
        StringBuilder? value,
        ref uint valueLength);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiCloseHandle(uint handle);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid actionId, ref WinTrustData data);
}
