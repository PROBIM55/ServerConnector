using Connector.Platform;
using Platform.Connector.Core;
using System.Text.Json;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class PlatformSettingsPersistenceTests
{
    [Fact]
    public void ExplicitConnectState_SurvivesRestart_WithoutPlainTokenInSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "connector-platform-state-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var settings = new DesktopConnectorSettings
            {
                DeviceId = "pc-preserved-device",
                ServerUrl = "https://platform.example.test/api/platform",
                AutoCadSessionKey = "acad-42",
                ModuleScope = "bridge,agr-publication",
                SecureTokenPath = Path.Combine(root, "device-token.dat")
            };
            const string token = "synthetic-test-token";
            var persistence = new PlatformSettingsPersistence(root);
            persistence.Save(settings, token);
            var reloaded = persistence.Load();
            Assert.Equal(settings.DeviceId, reloaded.DeviceId);
            Assert.Equal(PlatformConnectionPolicy.AutomaticServerUrl, reloaded.ServerUrl);
            Assert.Equal(settings.AutoCadSessionKey, reloaded.AutoCadSessionKey);
            Assert.Equal(settings.ModuleScope, reloaded.ModuleScope);
            Assert.DoesNotContain(token, File.ReadAllText(persistence.SettingsPath));
            Assert.False(File.ReadAllText(persistence.SettingsPath).Contains(root, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(token, File.ReadAllText(reloaded.SecureTokenPath));
            Assert.True(persistence.TryLoadCredential(out var restored));
            Assert.Equal(token, restored);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an endpoint")]
    [InlineData("https://other.example.test/api/platform")]
    public void ImportLegacy_RejectsToken_WhenRawLegacyServerUrlIsNotAutomatic(string serverUrl)
    {
        var root = Path.Combine(Path.GetTempPath(), "connector-platform-import-test-" + Guid.NewGuid());
        var legacyRoot = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacyRoot);
        try
        {
            var legacyService = new DesktopSettingsService(legacyRoot);
            var legacyTokenPath = Path.Combine(legacyRoot, "device-token.dat");
            SecureTokenStore.Save(legacyTokenPath, "synthetic-legacy-token");
            File.WriteAllText(legacyService.SettingsPath, JsonSerializer.Serialize(new DesktopConnectorSettings
            {
                ServerUrl = serverUrl,
                SecureTokenPath = legacyTokenPath
            }));

            var persistence = new PlatformSettingsPersistence(Path.Combine(root, "platform"));
            var result = persistence.ImportLegacy(legacyService);

            Assert.False(result.Imported);
            Assert.Equal("PLATFORM_LEGACY_ENDPOINT_REJECTED", result.Code);
            Assert.False(persistence.HasStoredCredential);
            Assert.False(persistence.TryLoadCredential(out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ImportLegacy_ImportsToken_WhenRawLegacyServerUrlIsAutomatic()
    {
        var root = Path.Combine(Path.GetTempPath(), "connector-platform-import-test-" + Guid.NewGuid());
        var legacyRoot = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacyRoot);
        try
        {
            var legacyService = new DesktopSettingsService(legacyRoot);
            var legacyTokenPath = Path.Combine(legacyRoot, "device-token.dat");
            const string token = "synthetic-legacy-token";
            SecureTokenStore.Save(legacyTokenPath, token);
            legacyService.Save(new DesktopConnectorSettings
            {
                ServerUrl = PlatformConnectionPolicy.AutomaticServerUrl,
                SecureTokenPath = legacyTokenPath
            });

            var persistence = new PlatformSettingsPersistence(Path.Combine(root, "platform"));
            var result = persistence.ImportLegacy(legacyService);

            Assert.True(result.Imported);
            Assert.Equal("PLATFORM_LEGACY_IMPORTED", result.Code);
            Assert.True(persistence.TryLoadCredential(out var importedToken));
            Assert.Equal(token, importedToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"ServerUrl\":\"https://bimplatforma.ru/api/platform\",\"HeartbeatSeconds\":\"not-a-number\"}")]
    public void ImportLegacyIfFresh_RejectsMalformedOrInvalidTypedLegacySettings(string legacyJson)
    {
        var root = Path.Combine(Path.GetTempPath(), "connector-platform-import-test-" + Guid.NewGuid());
        var legacyRoot = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacyRoot);
        try
        {
            var legacyService = new DesktopSettingsService(legacyRoot);
            File.WriteAllText(legacyService.SettingsPath, legacyJson);

            var persistence = new PlatformSettingsPersistence(Path.Combine(root, "platform"));
            var result = persistence.ImportLegacyIfFresh(legacyService);

            Assert.False(result.Imported);
            Assert.Equal("PLATFORM_LEGACY_SETTINGS_INVALID", result.Code);
            Assert.False(File.Exists(persistence.SettingsPath));
            Assert.False(persistence.HasStoredCredential);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ImportLegacyIfFresh_RejectsNullCapabilitiesBeforeWritingTargetCredential()
    {
        var root = Path.Combine(Path.GetTempPath(), "connector-platform-import-test-" + Guid.NewGuid());
        var legacyRoot = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacyRoot);
        try
        {
            var legacyService = new DesktopSettingsService(legacyRoot);
            var legacyTokenPath = Path.Combine(legacyRoot, "device-token.dat");
            SecureTokenStore.Save(legacyTokenPath, "synthetic-legacy-token");
            File.WriteAllText(legacyService.SettingsPath, JsonSerializer.Serialize(new
            {
                ServerUrl = PlatformConnectionPolicy.AutomaticServerUrl,
                SecureTokenPath = legacyTokenPath,
                Capabilities = (List<string>?)null
            }));

            var persistence = new PlatformSettingsPersistence(Path.Combine(root, "platform"));
            var result = persistence.ImportLegacyIfFresh(legacyService);

            Assert.False(result.Imported);
            Assert.Equal("PLATFORM_LEGACY_SETTINGS_INVALID", result.Code);
            Assert.False(File.Exists(persistence.SettingsPath));
            Assert.False(persistence.TryLoadCredential(out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ImportLegacyIfFresh_DoesNotOverwriteExistingTarget_ButManualImportCanReplaceIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "connector-platform-import-test-" + Guid.NewGuid());
        var legacyRoot = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacyRoot);
        try
        {
            var legacyService = new DesktopSettingsService(legacyRoot);
            var legacyTokenPath = Path.Combine(legacyRoot, "device-token.dat");
            SecureTokenStore.Save(legacyTokenPath, "synthetic-legacy-token");
            legacyService.Save(new DesktopConnectorSettings
            {
                ServerUrl = PlatformConnectionPolicy.AutomaticServerUrl,
                DeviceId = "legacy-device",
                SecureTokenPath = legacyTokenPath
            });

            var persistence = new PlatformSettingsPersistence(Path.Combine(root, "platform"));
            persistence.Save(new DesktopConnectorSettings { DeviceId = "new-device" }, "synthetic-new-token");

            var freshOnly = persistence.ImportLegacyIfFresh(legacyService);

            Assert.False(freshOnly.Imported);
            Assert.Equal("PLATFORM_LEGACY_TARGET_EXISTS", freshOnly.Code);
            Assert.Equal("new-device", persistence.Load().DeviceId);
            Assert.True(persistence.TryLoadCredential(out var retainedToken));
            Assert.Equal("synthetic-new-token", retainedToken);

            var manual = persistence.ImportLegacy(legacyService);

            Assert.True(manual.Imported);
            Assert.Equal("PLATFORM_LEGACY_IMPORTED", manual.Code);
            Assert.Equal("legacy-device", persistence.Load().DeviceId);
            Assert.True(persistence.TryLoadCredential(out var replacedToken));
            Assert.Equal("synthetic-legacy-token", replacedToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ImportLegacyIfFresh_RetriesSettingsSave_UsingAlreadyImportedCredential()
    {
        var root = Path.Combine(Path.GetTempPath(), "connector-platform-import-test-" + Guid.NewGuid());
        var legacyRoot = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacyRoot);
        try
        {
            var legacyService = new DesktopSettingsService(legacyRoot);
            var legacyTokenPath = Path.Combine(legacyRoot, "device-token.dat");
            const string token = "synthetic-legacy-token";
            SecureTokenStore.Save(legacyTokenPath, token);
            legacyService.Save(new DesktopConnectorSettings
            {
                ServerUrl = PlatformConnectionPolicy.AutomaticServerUrl,
                DeviceId = "legacy-device",
                SecureTokenPath = legacyTokenPath
            });

            var persistence = new PlatformSettingsPersistence(Path.Combine(root, "platform"));
            Directory.CreateDirectory(persistence.SettingsPath);

            var failed = persistence.ImportLegacyIfFresh(legacyService);

            Assert.False(failed.Imported);
            Assert.Equal("PLATFORM_LEGACY_TARGET_SAVE_FAILED", failed.Code);
            Assert.True(persistence.TryLoadCredential(out var retainedToken));
            Assert.Equal(token, retainedToken);

            Directory.Delete(persistence.SettingsPath);
            var retried = persistence.ImportLegacyIfFresh(legacyService);

            Assert.True(retried.Imported);
            Assert.Equal("PLATFORM_LEGACY_IMPORTED", retried.Code);
            Assert.Equal("legacy-device", persistence.Load().DeviceId);
            Assert.True(persistence.TryLoadCredential(out var retriedToken));
            Assert.Equal(token, retriedToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ImportLegacyIfFresh_DoesNotTreatDifferentCredentialOnlyTargetAsInterruptedImport()
    {
        var root = Path.Combine(Path.GetTempPath(), "connector-platform-import-test-" + Guid.NewGuid());
        var legacyRoot = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacyRoot);
        try
        {
            var legacyService = new DesktopSettingsService(legacyRoot);
            var legacyTokenPath = Path.Combine(legacyRoot, "device-token.dat");
            SecureTokenStore.Save(legacyTokenPath, "legacy-token-a");
            legacyService.Save(new DesktopConnectorSettings
            {
                ServerUrl = PlatformConnectionPolicy.AutomaticServerUrl,
                DeviceId = "legacy-device",
                SecureTokenPath = legacyTokenPath
            });

            var persistence = new PlatformSettingsPersistence(Path.Combine(root, "platform"));
            persistence.Save(new DesktopConnectorSettings { DeviceId = "temporary-device" }, "target-token-b");
            File.Delete(persistence.SettingsPath);

            var result = persistence.ImportLegacyIfFresh(legacyService);

            Assert.False(result.Imported);
            Assert.Equal("PLATFORM_LEGACY_TARGET_EXISTS", result.Code);
            Assert.False(File.Exists(persistence.SettingsPath));
            Assert.True(persistence.TryLoadCredential(out var retainedToken));
            Assert.Equal("target-token-b", retainedToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
