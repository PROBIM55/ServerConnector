using Platform.Connector.Core;

namespace Platform.Connector.Core.Tests;

public sealed class PhasePresentationAndSettingsTests
{
    // ════════ Маппинг фаз → бейдж UI ════════

    [Theory]
    [InlineData(ConnectorRuntimePhase.Stopped, "Остановлен", ConnectorPhaseSeverity.Idle)]
    [InlineData(ConnectorRuntimePhase.Connecting, "Подключение…", ConnectorPhaseSeverity.Busy)]
    [InlineData(ConnectorRuntimePhase.WaitingForServer, "Ожидание сервера…", ConnectorPhaseSeverity.Busy)]
    [InlineData(ConnectorRuntimePhase.Connected, "Подключён", ConnectorPhaseSeverity.Ok)]
    [InlineData(ConnectorRuntimePhase.Reconnecting, "Переподключение…", ConnectorPhaseSeverity.Busy)]
    [InlineData(ConnectorRuntimePhase.TokenInvalid, "Токен недействителен", ConnectorPhaseSeverity.Error)]
    public void PhasePresentation_MapsLabelAndSeverity(
        ConnectorRuntimePhase phase, string expectedLabel, ConnectorPhaseSeverity expectedSeverity)
    {
        Assert.Equal(expectedLabel, ConnectorPhasePresentation.GetRussianLabel(phase));
        Assert.Equal(expectedSeverity, ConnectorPhasePresentation.GetSeverity(phase));
    }

    [Fact]
    public void RuntimeState_MarkPhase_TracksPhaseAndClearsErrorOnConnected()
    {
        var state = new ConnectorRuntimeState();
        Assert.Equal(ConnectorRuntimePhase.Stopped, state.GetSnapshot().Phase);

        state.MarkPhase(ConnectorRuntimePhase.Connecting);
        state.MarkPhase(ConnectorRuntimePhase.WaitingForServer, "connection refused");
        var waiting = state.GetSnapshot();
        Assert.Equal(ConnectorRuntimePhase.WaitingForServer, waiting.Phase);
        Assert.Equal("connection refused", waiting.LastError);

        state.MarkRetryScheduled(3, DateTime.UtcNow.AddSeconds(8));
        Assert.Equal(3, state.GetSnapshot().ReconnectAttempts);
        Assert.NotEqual(default, state.GetSnapshot().NextRetryAtUtc);

        state.MarkPhase(ConnectorRuntimePhase.Connected);
        var connected = state.GetSnapshot();
        Assert.Equal(ConnectorRuntimePhase.Connected, connected.Phase);
        Assert.Null(connected.LastError);
        Assert.Equal(0, connected.ReconnectAttempts);
        Assert.Equal(default, connected.NextRetryAtUtc);
    }

    // ════════ Настройки Desktop UI: round-trip + нормализация ════════

    [Fact]
    public void DesktopSettings_RoundTrip_PreservesValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "platform-connector-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new DesktopSettingsService(root);
            var settings = new DesktopConnectorSettings
            {
                ServerUrl = "http://localhost:5068/",
                DeviceId = " pc-test-1 ",
                HeartbeatSeconds = 45,
                PollIntervalSeconds = 2,
                PollBackoffMaxSeconds = 20,
                AgentType = "platform-cad-connector",
                ModuleScope = "bridge",
                Capabilities = ["tekla.apply", "tekla.apply", " autocad.build "],
                AutoStart = false
            };

            service.Save(settings);
            var loaded = service.Load();

            Assert.Equal("http://localhost:5068", loaded.ServerUrl); // trailing slash убран
            Assert.Equal("pc-test-1", loaded.DeviceId);              // trim
            Assert.Equal(45, loaded.HeartbeatSeconds);
            Assert.Equal(2, loaded.PollIntervalSeconds);
            Assert.Equal(20, loaded.PollBackoffMaxSeconds);
            Assert.Equal("bridge", loaded.ModuleScope);
            Assert.Equal(2, loaded.Capabilities.Count);              // дедупликация + trim
            Assert.Contains("tekla.apply", loaded.Capabilities);
            Assert.Contains("autocad.build", loaded.Capabilities);
            Assert.False(loaded.AutoStart);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void DesktopSettings_Normalize_FixesInvalidValues()
    {
        var settings = new DesktopConnectorSettings
        {
            ServerUrl = "not a url",
            DeviceId = "  ",
            HeartbeatSeconds = 1,
            PollIntervalSeconds = 0,
            PollBackoffMaxSeconds = -5,
            AgentType = "",
            ModuleScope = "",
            Capabilities = ["", "  "]
        };

        DesktopSettingsService.Normalize(settings);

        Assert.Equal(DesktopSettingsService.DefaultServerUrl, settings.ServerUrl);
        Assert.StartsWith("pc-", settings.DeviceId);
        Assert.Equal(60, settings.HeartbeatSeconds);
        Assert.Equal(3, settings.PollIntervalSeconds);
        Assert.True(settings.PollBackoffMaxSeconds >= settings.PollIntervalSeconds);
        Assert.Equal("platform-cad-connector", settings.AgentType);
        Assert.False(string.IsNullOrWhiteSpace(settings.ModuleScope));
        Assert.NotEmpty(settings.Capabilities);
    }

    [Fact]
    public void DesktopSettings_Normalize_MigratesLegacyDefaultModuleScopeToAgr()
    {
        var settings = new DesktopConnectorSettings
        {
            ModuleScope = "shze, bridge, bns-piles"
        };

        DesktopSettingsService.Normalize(settings);

        Assert.Equal("shze,bridge,bns-piles,agr-publication", settings.ModuleScope);
    }

    [Fact]
    public void DesktopSettings_Normalize_MigratesLegacyPlatformDomain()
    {
        var settings = new DesktopConnectorSettings
        {
            ServerUrl = "https://server.structura-most.ru/api/platform/",
            UpdateManifestUrl = "https://server.structura-most.ru/api/platform/connectors/desktop/latest.json"
        };

        DesktopSettingsService.Normalize(settings);

        Assert.Equal(DesktopSettingsService.DefaultServerUrl, settings.ServerUrl);
        Assert.Equal(DesktopSettingsService.DefaultUpdateManifestUrl, settings.UpdateManifestUrl);
    }

    [Fact]
    public void DesktopSettings_LoadFromCorruptFile_FallsBackToDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "platform-connector-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new DesktopSettingsService(root);
            File.WriteAllText(service.SettingsPath, "{ this is not json ");

            var loaded = service.Load();

            Assert.Equal(DesktopSettingsService.DefaultServerUrl, loaded.ServerUrl);
            Assert.True(loaded.AutoStart);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
