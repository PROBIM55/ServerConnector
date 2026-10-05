using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Connector.Upgrade.Velopack;

public interface IVelopackRegistrationReader
{
    VelopackRegistration? Read(string packId, string currentUserSid);
}

public sealed class CurrentUserVelopackRegistrationReader : IVelopackRegistrationReader
{
    public VelopackRegistration? Read(string packId, string currentUserSid)
    {
        var keyPath = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{packId}";
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        if (key is null)
            return null;

        return new VelopackRegistration(
            currentUserSid,
            keyPath,
            ReadString(key, "InstallLocation"),
            ReadString(key, "Publisher"),
            key.GetValue("DisplayVersion") as string,
            ReadString(key, "UninstallString"),
            ReadString(key, "QuietUninstallString"));
    }

    private static string ReadString(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
}

/// <summary>Read-only exact probe for the current user's standard Velopack layout.</summary>
public sealed class SystemVelopackInstallProbe : IVelopackInstallProbe
{
    private readonly string _currentUserSid;
    private readonly string _localApplicationData;
    private readonly IVelopackRegistrationReader _registrationReader;

    public SystemVelopackInstallProbe(
        string currentUserSid,
        string localApplicationData,
        IVelopackRegistrationReader? registrationReader = null)
    {
        _currentUserSid = RequireValue(currentUserSid, nameof(currentUserSid));
        _localApplicationData = NormalizeAbsoluteDirectory(localApplicationData, nameof(localApplicationData));
        _registrationReader = registrationReader ?? new CurrentUserVelopackRegistrationReader();
    }

    public VelopackInstallObservation Inspect(VelopackSetupPin pin, string targetUserSid)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (!string.Equals(targetUserSid, _currentUserSid, StringComparison.OrdinalIgnoreCase))
            throw new VelopackInstallInvariantException("The target SID is not the current Windows user.");

        var root = Path.Combine(_localApplicationData, pin.PackId);
        var registration = _registrationReader.Read(pin.PackId, _currentUserSid);
        var rootExists = Directory.Exists(root);
        if (!rootExists && registration is null)
            return Absent();
        if (!rootExists || registration is null)
            return Conflict("The install directory and current-user registration are not both present.");
        if (IsReparsePoint(root))
            return Conflict("The Velopack install root is a reparse point.");

        var current = Path.Combine(root, "current");
        if (!Directory.Exists(current) || IsReparsePoint(current))
            return Conflict("The Velopack current directory is missing or redirected.");

        var sqVersion = Path.Combine(current, "sq.version");
        var mainExecutable = Path.Combine(current, UnifiedVelopackApplication.MainExecutable);
        var updateExecutable = Path.Combine(root, "Update.exe");
        var rootLauncher = Path.Combine(root, UnifiedVelopackApplication.MainExecutable);
        foreach (var path in new[] { sqVersion, mainExecutable, updateExecutable, rootLauncher })
        {
            if (!File.Exists(path) || IsReparsePoint(path))
                return Conflict($"Required Velopack file is missing or redirected: {Path.GetFileName(path)}.");
        }

        var metadata = ReadSqVersion(sqVersion);
        if (!string.Equals(metadata.PackId, pin.PackId, StringComparison.Ordinal) ||
            !string.Equals(metadata.Version, pin.Version, StringComparison.Ordinal) ||
            !string.Equals(metadata.MainExecutable, UnifiedVelopackApplication.MainExecutable, StringComparison.Ordinal))
            return Conflict("sq.version does not match the pinned packId, version and main executable.");

        var expectedUninstall = $"\"{updateExecutable}\" --uninstall";
        var expectedQuietUninstall = expectedUninstall + " --silent";
        if (!string.Equals(registration.UserSid, _currentUserSid, StringComparison.OrdinalIgnoreCase) ||
            !PathsEqual(registration.InstallLocation, root) ||
            !string.Equals(registration.Publisher, pin.PackId, StringComparison.Ordinal) ||
            !string.Equals(registration.UninstallString, expectedUninstall, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(registration.QuietUninstallString, expectedQuietUninstall, StringComparison.OrdinalIgnoreCase))
            return Conflict("The current-user uninstall registration does not match the exact Velopack install.");

        var identity = new VelopackInstalledIdentity(
            _currentUserSid,
            pin.PackId,
            pin.Version,
            root,
            HashFile(sqVersion),
            HashFile(mainExecutable),
            HashFile(updateExecutable),
            HashFile(rootLauncher),
            registration);
        return new VelopackInstallObservation(
            VelopackInstallPresence.ExactInstalled,
            identity,
            Guid.NewGuid().ToString("N"),
            "The exact same-user Velopack installation is registered and complete.");
    }

    private static (string PackId, string Version, string MainExecutable) ReadSqVersion(string path)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(path, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var metadata = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "metadata")
            ?? throw new InvalidDataException("sq.version metadata is missing.");
        string Value(string name) => metadata.Elements()
            .FirstOrDefault(element => element.Name.LocalName == name)?.Value
            ?? string.Empty;
        return (Value("id"), Value("version"), Value("mainExe"));
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return false;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAbsoluteDirectory(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute directory is required.", parameterName);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string RequireValue(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value;

    private static VelopackInstallObservation Absent() => new(
        VelopackInstallPresence.Absent,
        null,
        Guid.NewGuid().ToString("N"),
        "No directory or current-user registration exists for the pinned packId.");

    private static VelopackInstallObservation Conflict(string detail) => new(
        VelopackInstallPresence.Conflicting,
        null,
        Guid.NewGuid().ToString("N"),
        detail);
}
