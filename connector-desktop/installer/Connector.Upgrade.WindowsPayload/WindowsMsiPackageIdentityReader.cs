using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Connector.Upgrade.WindowsPayload;

internal sealed class WindowsMsiPackageIdentityReader : IMsiPackageIdentityReader
{
    public MsiPackageIdentity Read(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Installer metadata is available only on Windows.");

        var result = MsiOpenDatabase(path, IntPtr.Zero, out var database);
        ThrowOnError(result, "Windows Installer could not open the rollback MSI read-only.");
        try
        {
            var productCodeText = ReadProperty(database, "ProductCode");
            var upgradeCodeText = ReadProperty(database, "UpgradeCode");
            var version = ReadProperty(database, "ProductVersion");
            if (!Guid.TryParse(productCodeText, out var productCode) || productCode == Guid.Empty ||
                !Guid.TryParse(upgradeCodeText, out var upgradeCode) || upgradeCode == Guid.Empty ||
                string.IsNullOrWhiteSpace(version))
                throw new InvalidDataException("The rollback MSI contains invalid identity properties.");
            return new MsiPackageIdentity(productCode, upgradeCode, version);
        }
        finally
        {
            MsiCloseHandle(database);
        }
    }

    private static string ReadProperty(uint database, string property)
    {
        var query = $"SELECT `Value` FROM `Property` WHERE `Property` = '{property}'";
        var result = MsiDatabaseOpenView(database, query, out var view);
        ThrowOnError(result, $"Could not query MSI property '{property}'.");
        try
        {
            ThrowOnError(MsiViewExecute(view, 0), $"Could not execute MSI property query '{property}'.");
            result = MsiViewFetch(view, out var record);
            ThrowOnError(result, $"MSI property '{property}' is missing.");
            try
            {
                uint length = 0;
                result = MsiRecordGetString(record, 1, null, ref length);
                if (result != ErrorMoreData && result != ErrorSuccess)
                    ThrowOnError(result, $"Could not measure MSI property '{property}'.");
                var value = new StringBuilder(checked((int)length + 1));
                var capacity = (uint)value.Capacity;
                ThrowOnError(
                    MsiRecordGetString(record, 1, value, ref capacity),
                    $"Could not read MSI property '{property}'.");
                return value.ToString();
            }
            finally
            {
                MsiCloseHandle(record);
            }
        }
        finally
        {
            MsiCloseHandle(view);
        }
    }

    private static void ThrowOnError(uint code, string message)
    {
        if (code != ErrorSuccess)
            throw new Win32Exception(unchecked((int)code), message);
    }

    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
    private static extern uint MsiOpenDatabase(string databasePath, IntPtr persist, out uint database);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
    private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);

    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(uint view, uint record);

    [DllImport("msi.dll")]
    private static extern uint MsiViewFetch(uint view, out uint record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordGetStringW")]
    private static extern uint MsiRecordGetString(uint record, uint field, StringBuilder? value, ref uint valueLength);

    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(uint handle);
}
