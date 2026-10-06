using System.Text.Json;
using System.Text.Json.Serialization;
using Connector.Upgrade.Core;

namespace Connector.Upgrade.NetBirdWindowsState;

internal sealed record WindowsNetBirdOwnerMarker(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("provisioner")] string Provisioner,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("installationId")] string InstallationId,
    [property: JsonPropertyName("productCode")] Guid ProductCode,
    [property: JsonPropertyName("upgradeCode")] Guid UpgradeCode,
    [property: JsonPropertyName("productVersion")] string ProductVersion,
    [property: JsonPropertyName("manufacturer")] string Manufacturer,
    [property: JsonPropertyName("productName")] string ProductName,
    [property: JsonPropertyName("installerSha256")] string InstallerSha256,
    [property: JsonPropertyName("configurationSha256")] string ConfigurationSha256,
    [property: JsonPropertyName("serviceIdentity")] string ServiceIdentity,
    [property: JsonPropertyName("cliSha256")] string CliSha256,
    [property: JsonPropertyName("cliSignerSubject")] string CliSignerSubject,
    [property: JsonPropertyName("cliSignerThumbprint")] string CliSignerThumbprint,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc)
{
    internal const int CurrentSchemaVersion = 2;
    internal const string ExpectedProvisioner = "StructuraConnector.NetBirdWindowsState";
    internal const int MaximumBytes = 16 * 1024;

    internal NetBirdMsiPackageIdentity Package => new(
        ProductCode,
        UpgradeCode,
        ProductVersion,
        Manufacturer,
        ProductName);

    internal NetBirdOwnedState OwnedState => new(
        InstallationId,
        ProductVersion,
        ConfigurationSha256,
        ServiceIdentity);

    internal static WindowsNetBirdOwnerMarker Parse(byte[] content)
    {
        if (content.Length is 0 or > MaximumBytes)
            throw new InvalidDataException("The NetBird owner marker size is invalid.");

        var value = JsonSerializer.Deserialize<WindowsNetBirdOwnerMarker>(content, JsonOptions)
            ?? throw new InvalidDataException("The NetBird owner marker is empty.");
        value.Validate();
        return value;
    }

    internal byte[] Serialize()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("The NetBird owner marker exceeds its size limit.");
        return bytes;
    }

    internal void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion ||
            !string.Equals(Provisioner, ExpectedProvisioner, StringComparison.Ordinal) ||
            !WindowsNetBirdMachineStatePort.IsOperationId(OperationId) ||
            !WindowsNetBirdMachineStatePort.IsOperationId(InstallationId) ||
            !string.Equals(OperationId, InstallationId, StringComparison.Ordinal) ||
            ProductCode == Guid.Empty || UpgradeCode == Guid.Empty ||
            string.IsNullOrWhiteSpace(ProductVersion) ||
            string.IsNullOrWhiteSpace(Manufacturer) ||
            string.IsNullOrWhiteSpace(ProductName) ||
            !WindowsNetBirdMachineStatePort.IsSha256(InstallerSha256) ||
            !WindowsNetBirdMachineStatePort.IsSha256(ConfigurationSha256) ||
            string.IsNullOrWhiteSpace(ServiceIdentity) ||
            !WindowsNetBirdMachineStatePort.IsSha256(CliSha256) ||
            !CliSignerSubject.StartsWith("CN=NetBird GmbH,", StringComparison.Ordinal) ||
            CliSignerThumbprint.Length != 40 || !CliSignerThumbprint.All(Uri.IsHexDigit) ||
            CreatedUtc == default)
            throw new InvalidDataException("The NetBird owner marker is invalid.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        MaxDepth = 8,
    };
}
