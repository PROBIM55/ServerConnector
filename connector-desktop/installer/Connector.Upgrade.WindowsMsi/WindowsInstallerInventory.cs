using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Connector.Upgrade.Core;

namespace Connector.Upgrade.WindowsMsi;

/// <summary>
/// Native Windows Installer inventory adapter. The API calls are read-only and scoped to the
/// locked UpgradeCode/ProductCode pair. Per-user enumeration is deliberately restricted to the
/// current user so inspection does not require elevation; an unexpected returned SID fails closed.
/// </summary>
public sealed class WindowsInstallerInventory : IWindowsMsiInventory
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoMoreItems = 259;
    private const string InstallPropertyVersionString = "VersionString";
    private readonly string _currentUserSid;

    public WindowsInstallerInventory()
        : this(WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows identity has no SID."))
    {
    }

    public WindowsInstallerInventory(string currentUserSid)
    {
        _currentUserSid = string.IsNullOrWhiteSpace(currentUserSid)
            ? throw new ArgumentException("The current Windows SID is required.", nameof(currentUserSid))
            : currentUserSid;
    }

    public WindowsMsiInventorySnapshot Inspect(LegacyUpgradePin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Installer inventory is available only on Windows.");

        var related = EnumerateRelatedProducts(pin.Identity.UpgradeCode);
        var candidates = related.Append(pin.Identity.ProductCode).Distinct().ToArray();
        var registrations = candidates
            .SelectMany(productCode => EnumerateProductInstances(
                productCode,
                pin.Identity.UpgradeCode,
                _currentUserSid))
            .Distinct()
            .ToArray();

        return new WindowsMsiInventorySnapshot(pin.Identity.UpgradeCode, related, registrations);
    }

    private static IReadOnlyList<Guid> EnumerateRelatedProducts(Guid upgradeCode)
    {
        var result = new List<Guid>();
        for (uint index = 0; ; index++)
        {
            var productCode = new StringBuilder(39);
            var status = MsiEnumRelatedProductsW(FormatGuid(upgradeCode), 0, index, productCode);
            if (status == ErrorNoMoreItems)
                return result;
            ThrowOnError(status, "MsiEnumRelatedProducts");
            if (!Guid.TryParse(productCode.ToString(), out var parsed) || parsed == Guid.Empty)
                throw new InvalidDataException("Windows Installer returned an invalid related ProductCode.");
            result.Add(parsed);
        }
    }

    private static IEnumerable<WindowsMsiRegistration> EnumerateProductInstances(
        Guid productCode,
        Guid upgradeCode,
        string currentUserSid)
    {
        const WindowsMsiRegistrationContext visibleContexts =
            WindowsMsiRegistrationContext.UserManaged |
            WindowsMsiRegistrationContext.UserUnmanaged |
            WindowsMsiRegistrationContext.Machine;
        foreach (var registration in EnumerateContext(
                     productCode,
                     upgradeCode,
                     visibleContexts,
                     currentUserSid))
            yield return registration;
    }

    private static IEnumerable<WindowsMsiRegistration> EnumerateContext(
        Guid productCode,
        Guid upgradeCode,
        WindowsMsiRegistrationContext contexts,
        string currentUserSid)
    {
        for (uint index = 0; ; index++)
        {
            var installedProductCode = new StringBuilder(39);
            var sidCapacity = 192u;
            var sid = new StringBuilder((int)sidCapacity);
            var status = MsiEnumProductsExW(
                FormatGuid(productCode),
                userSid: null,
                contexts,
                index,
                installedProductCode,
                out var installedContext,
                sid,
                ref sidCapacity);

            if (status == ErrorNoMoreItems)
                yield break;
            if (status == ErrorMoreData)
            {
                sidCapacity++;
                sid = new StringBuilder((int)sidCapacity);
                status = MsiEnumProductsExW(
                    FormatGuid(productCode),
                    userSid: null,
                    contexts,
                    index,
                    installedProductCode,
                    out installedContext,
                    sid,
                    ref sidCapacity);
            }
            ThrowOnError(status, "MsiEnumProductsEx");

            if (!Guid.TryParse(installedProductCode.ToString(), out var parsedProductCode) ||
                parsedProductCode == Guid.Empty)
                throw new InvalidDataException("Windows Installer returned an invalid registered ProductCode.");

            if (installedContext is not (
                    WindowsMsiRegistrationContext.Machine or
                    WindowsMsiRegistrationContext.UserManaged or
                    WindowsMsiRegistrationContext.UserUnmanaged))
                throw new InvalidDataException("Windows Installer returned an invalid registration context.");

            var returnedSid = sid.ToString();
            var instanceSid = installedContext == WindowsMsiRegistrationContext.Machine ? null : returnedSid;
            if (installedContext == WindowsMsiRegistrationContext.Machine && returnedSid.Length != 0)
                throw new InvalidDataException("Windows Installer returned a SID for a machine product.");
            if (installedContext != WindowsMsiRegistrationContext.Machine &&
                !string.Equals(instanceSid, currentUserSid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Current-user Windows Installer enumeration returned an unknown user SID.");

            var version = ReadProductInfo(
                parsedProductCode,
                installedContext,
                InstallPropertyVersionString);
            yield return new WindowsMsiRegistration(
                parsedProductCode,
                upgradeCode,
                version,
                installedContext,
                instanceSid);
        }
    }

    private static string ReadProductInfo(
        Guid productCode,
        WindowsMsiRegistrationContext context,
        string property)
    {
        // NULL means machine for machine context and the current user for per-user contexts.
        // This keeps the exact information query inside the same unelevated scope as enumeration.
        const string? querySid = null;
        uint length = 0;
        var status = MsiGetProductInfoExW(
            FormatGuid(productCode),
            querySid,
            context,
            property,
            null,
            ref length);
        if (status != ErrorMoreData && status != ErrorSuccess)
            ThrowOnError(status, "MsiGetProductInfoEx(length)");

        var value = new StringBuilder(checked((int)length + 1));
        length++;
        status = MsiGetProductInfoExW(
            FormatGuid(productCode),
            querySid,
            context,
            property,
            value,
            ref length);
        ThrowOnError(status, "MsiGetProductInfoEx(value)");
        return value.ToString();
    }

    private static string FormatGuid(Guid value) => $"{{{value:D}}}".ToUpperInvariant();

    private static void ThrowOnError(uint status, string operation)
    {
        if (status != ErrorSuccess)
            throw new Win32Exception(checked((int)status), $"{operation} failed with Windows Installer error {status}.");
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
        WindowsMsiRegistrationContext context,
        uint index,
        StringBuilder installedProductCode,
        out WindowsMsiRegistrationContext installedContext,
        StringBuilder sid,
        ref uint sidLength);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiGetProductInfoExW(
        string productCode,
        string? userSid,
        WindowsMsiRegistrationContext context,
        string property,
        StringBuilder? value,
        ref uint valueLength);
}
