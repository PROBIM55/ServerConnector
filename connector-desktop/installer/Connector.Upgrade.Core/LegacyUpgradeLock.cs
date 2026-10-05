using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Connector.Upgrade.Core;

public sealed record LegacyUpgradePin(
    LegacyApplicationKind Kind,
    string PackageId,
    string InstallerName,
    string Version,
    LegacyApplicationIdentity Identity,
    long SizeBytes,
    string Sha256);

/// <summary>The repository lock embedded at build time is the only accepted legacy MSI identity.</summary>
public sealed class LegacyUpgradeLock
{
    private static readonly Regex Sha256Pattern = new("^[A-F0-9]{64}$", RegexOptions.CultureInvariant);
    private readonly IReadOnlyDictionary<LegacyApplicationKind, LegacyUpgradePin> _pins;

    private LegacyUpgradeLock(IEnumerable<LegacyUpgradePin> pins)
    {
        var dictionary = pins.ToDictionary(pin => pin.Kind);
        if (dictionary.Count != 2 ||
            !dictionary.ContainsKey(LegacyApplicationKind.StructuraConnector) ||
            !dictionary.ContainsKey(LegacyApplicationKind.PlatformConnector))
            throw new InvalidDataException("The legacy MSI lock must contain exactly the two supported applications.");
        _pins = new ReadOnlyDictionary<LegacyApplicationKind, LegacyUpgradePin>(dictionary);
    }

    public IReadOnlyCollection<LegacyUpgradePin> Pins => _pins.Values.ToArray();

    public LegacyUpgradePin Get(LegacyApplicationKind kind) =>
        _pins.TryGetValue(kind, out var pin)
            ? pin
            : throw new UpgradeInvariantException($"No legacy MSI lock pin exists for {kind}.");

    public static LegacyUpgradeLock LoadEmbedded()
    {
        var assembly = typeof(LegacyUpgradeLock).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith("legacy-msi.lock.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Embedded legacy MSI lock is missing.");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException("Embedded legacy MSI lock cannot be opened.");
        var source = JsonSerializer.Deserialize<LockSource>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Embedded legacy MSI lock is empty.");
        if (source.SchemaVersion != 1 || source.Assets is null)
            throw new InvalidDataException("Unsupported legacy MSI lock schema.");

        var pins = source.Assets.Select(ParsePin).ToArray();
        return new LegacyUpgradeLock(pins);
    }

    private static LegacyUpgradePin ParsePin(LockAsset asset)
    {
        var kind = asset.Id switch
        {
            "structura-connector" => LegacyApplicationKind.StructuraConnector,
            "platform-connector" => LegacyApplicationKind.PlatformConnector,
            _ => throw new InvalidDataException($"Unknown legacy MSI lock id '{asset.Id}'."),
        };
        if (!Guid.TryParse(asset.ProductCode, out var productCode) || productCode == Guid.Empty ||
            !Guid.TryParse(asset.UpgradeCode, out var upgradeCode) || upgradeCode == Guid.Empty ||
            string.IsNullOrWhiteSpace(asset.InstallerName) || string.IsNullOrWhiteSpace(asset.Version) ||
            asset.InstallerBytes <= 0 || string.IsNullOrWhiteSpace(asset.InstallerSha256))
            throw new InvalidDataException($"Incomplete legacy MSI lock entry '{asset.Id}'.");
        var sha256 = asset.InstallerSha256.ToUpperInvariant();
        if (!Sha256Pattern.IsMatch(sha256))
            throw new InvalidDataException($"Invalid SHA-256 in legacy MSI lock entry '{asset.Id}'.");
        var identity = new LegacyApplicationIdentity(kind, productCode, upgradeCode);
        return new LegacyUpgradePin(
            kind,
            asset.Id,
            asset.InstallerName,
            asset.Version,
            identity,
            asset.InstallerBytes,
            sha256);
    }

    private sealed record LockSource(int SchemaVersion, List<LockAsset>? Assets);

    private sealed record LockAsset(
        string Id,
        string InstallerName,
        string Version,
        string ProductCode,
        string UpgradeCode,
        string InstallerSha256,
        long InstallerBytes);
}
