using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connector.Upgrade.NetBirdMachine;

public sealed record OfficialNetBirdPackagePin(
    string Version,
    string MinimumSecureVersion,
    Uri ReleaseUri,
    string InstallerName,
    long InstallerBytes,
    string InstallerSha256,
    string SignerSubjectPrefix,
    string SignerThumbprint)
{
    public static OfficialNetBirdPackagePin LoadEmbedded()
    {
        var assembly = typeof(OfficialNetBirdPackagePin).Assembly;
        var resourceName = assembly.GetManifestResourceNames().SingleOrDefault(
            name => name.EndsWith("v0.79.0-windows-x64.lock.json", StringComparison.Ordinal));
        if (resourceName is null)
            throw new InvalidDataException("The official NetBird package lock is not embedded.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException("The official NetBird package lock cannot be opened.");
        var document = JsonSerializer.Deserialize<NetBirdPackageLockDocument>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            MaxDepth = 16,
        }) ?? throw new InvalidDataException("The official NetBird package lock is empty.");

        if (document.SchemaVersion != 1 ||
            !System.Version.TryParse(document.Version, out var version) ||
            !System.Version.TryParse(document.MinimumSecureVersion, out var minimum) ||
            version < minimum ||
            !Uri.TryCreate(document.ReleaseUrl, UriKind.Absolute, out var releaseUri) ||
            releaseUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(releaseUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(document.InstallerName) ||
            !string.Equals(document.InstallerName, Path.GetFileName(document.InstallerName), StringComparison.Ordinal) ||
            document.InstallerBytes <= 0 ||
            !IsHex(document.InstallerSha256, 64) ||
            string.IsNullOrWhiteSpace(document.SignerSubjectPrefix) ||
            !IsHex(document.SignerThumbprint, 40))
            throw new InvalidDataException("The embedded official NetBird package lock is invalid.");

        return new OfficialNetBirdPackagePin(
            version.ToString(),
            minimum.ToString(),
            releaseUri,
            document.InstallerName,
            document.InstallerBytes,
            document.InstallerSha256.ToUpperInvariant(),
            document.SignerSubjectPrefix,
            document.SignerThumbprint.ToUpperInvariant());
    }

    private static bool IsHex(string? value, int length) =>
        value?.Length == length && value.All(Uri.IsHexDigit);

    private sealed record NetBirdPackageLockDocument(
        [property: JsonPropertyName("schemaVersion")]
        int SchemaVersion,
        [property: JsonPropertyName("version")]
        string Version,
        [property: JsonPropertyName("minimumSecureVersion")]
        string MinimumSecureVersion,
        [property: JsonPropertyName("releaseUrl")]
        string ReleaseUrl,
        [property: JsonPropertyName("installerName")]
        string InstallerName,
        [property: JsonPropertyName("installerBytes")]
        long InstallerBytes,
        [property: JsonPropertyName("installerSha256")]
        string InstallerSha256,
        [property: JsonPropertyName("signerSubjectPrefix")]
        string SignerSubjectPrefix,
        [property: JsonPropertyName("signerThumbprint")]
        string SignerThumbprint);
}
