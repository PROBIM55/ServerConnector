using System.Text.Json;
using Connector.Desktop.Services;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class SettingsUpgradeCompatibilityTests
{
    [Fact]
    public void Save_RoundTripsFutureProperties_SelectedPaths_AndCiphertext()
    {
        var root = Path.Combine(Path.GetTempPath(), "connector-settings-upgrade-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "AutoStart": false,
                  "TokenCipherBase64": "fixture-token-ciphertext",
                  "TeklaStandardLocalPath": "C:\\fixture\\firm",
                  "TeklaExtensionsLocalPath": "C:\\fixture\\extensions",
                  "TeklaLibrariesLocalPath": "C:\\fixture\\libraries",
                  "ModelSharingTeklaBin": "C:\\fixture\\sharing-bin",
                  "FutureGraphite": { "revision": 7, "enabled": true }
                }
                """);

            var service = new SettingsService(path);
            var settings = service.Load();
            settings.IfcPatchingTeklaBin = "C:\\fixture\\ifc-bin";
            settings.IfcPatchingStagingDir = "C:\\fixture\\patches";
            service.Save(settings);

            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            var rootJson = saved.RootElement;
            Assert.False(rootJson.GetProperty("AutoStart").GetBoolean());
            Assert.Equal("fixture-token-ciphertext", rootJson.GetProperty("TokenCipherBase64").GetString());
            Assert.Equal("C:\\fixture\\firm", rootJson.GetProperty("TeklaStandardLocalPath").GetString());
            Assert.Equal("C:\\fixture\\extensions", rootJson.GetProperty("TeklaExtensionsLocalPath").GetString());
            Assert.Equal("C:\\fixture\\libraries", rootJson.GetProperty("TeklaLibrariesLocalPath").GetString());
            Assert.Equal("C:\\fixture\\sharing-bin", rootJson.GetProperty("ModelSharingTeklaBin").GetString());
            Assert.Equal("C:\\fixture\\ifc-bin", rootJson.GetProperty("IfcPatchingTeklaBin").GetString());
            Assert.Equal("C:\\fixture\\patches", rootJson.GetProperty("IfcPatchingStagingDir").GetString());
            Assert.Equal(7, rootJson.GetProperty("FutureGraphite").GetProperty("revision").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
