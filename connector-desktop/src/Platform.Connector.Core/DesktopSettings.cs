using System.Text;
using System.Text.Json;

namespace Platform.Connector.Core;

/// <summary>
/// Пользовательские настройки Desktop UI коннектора
/// (%LOCALAPPDATA%\Platform\Connector\desktop\settings.json).
/// Живут в Core, чтобы нормализация и round-trip были покрыты unit-тестами.
/// </summary>
public sealed class DesktopConnectorSettings
{
    public string ServerUrl { get; set; } = DesktopSettingsService.DefaultServerUrl;
    public string DeviceId { get; set; } = $"pc-{Environment.MachineName.ToLowerInvariant()}";
    public string SecureTokenPath { get; set; } = string.Empty;
    public int HeartbeatSeconds { get; set; } = 60;
    public int PollIntervalSeconds { get; set; } = 3;
    public int PollBackoffMaxSeconds { get; set; } = 30;
    public string AgentType { get; set; } = "platform-cad-connector";
    public string ModuleScope { get; set; } = "shze,bridge,bns-piles,agr-publication";
    public List<string> Capabilities { get; set; } = ["autocad.build", "tekla.apply"];
    public string UpdateManifestUrl { get; set; } = string.Empty;
    public bool AutoStart { get; set; } = true;

    /// <summary>
    /// Выбранная пользователем сессия AutoCAD («acad-PID»). PID транзитен:
    /// после перезапуска AutoCAD ключ протухает, UI перевыбирает (единственную)
    /// живую сессию и перезаписывает значение.
    /// </summary>
    public string AutoCadSessionKey { get; set; } = string.Empty;
}

public sealed class DesktopSettingsService
{
    public const string DefaultServerUrl = "https://bimplatforma.ru/api/platform";
    public const string DefaultUpdateManifestUrl = "https://bimplatforma.ru/api/platform/connectors/desktop/latest.json";
    private const string LegacyDefaultServerUrl = "https://server.structura-most.ru/api/platform";
    private const string LegacyDefaultUpdateManifestUrl = "https://server.structura-most.ru/api/platform/connectors/desktop/latest.json";

    private readonly string _settingsPath;

    public DesktopSettingsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Platform",
            "Connector",
            "desktop"))
    {
    }

    /// <summary>Каталог хранения подменяем в тестах.</summary>
    public DesktopSettingsService(string rootDirectory)
    {
        Directory.CreateDirectory(rootDirectory);
        _settingsPath = Path.Combine(rootDirectory, "settings.json");
    }

    public string SettingsPath => _settingsPath;

    public DesktopConnectorSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            var fresh = new DesktopConnectorSettings();
            Normalize(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<DesktopConnectorSettings>(json) ?? new DesktopConnectorSettings();
            Normalize(settings);
            return settings;
        }
        catch
        {
            var fallback = new DesktopConnectorSettings();
            Normalize(fallback);
            return fallback;
        }
    }

    public void Save(DesktopConnectorSettings settings)
    {
        Normalize(settings);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(_settingsPath, json, Encoding.UTF8);
    }

    public static void Normalize(DesktopConnectorSettings settings)
    {
        if (string.Equals(settings.ServerUrl?.Trim().TrimEnd('/'), LegacyDefaultServerUrl, StringComparison.OrdinalIgnoreCase))
        {
            settings.ServerUrl = DefaultServerUrl;
        }
        else if (string.IsNullOrWhiteSpace(settings.ServerUrl) ||
            !Uri.TryCreate(settings.ServerUrl.Trim(), UriKind.Absolute, out _))
        {
            settings.ServerUrl = DefaultServerUrl;
        }
        else
        {
            settings.ServerUrl = settings.ServerUrl.Trim().TrimEnd('/');
        }

        if (string.IsNullOrWhiteSpace(settings.UpdateManifestUrl) ||
            string.Equals(settings.UpdateManifestUrl.Trim(), LegacyDefaultUpdateManifestUrl, StringComparison.OrdinalIgnoreCase))
        {
            settings.UpdateManifestUrl = DefaultUpdateManifestUrl;
        }

        if (string.IsNullOrWhiteSpace(settings.DeviceId))
        {
            settings.DeviceId = $"pc-{Environment.MachineName.ToLowerInvariant()}";
        }
        else
        {
            settings.DeviceId = settings.DeviceId.Trim();
        }

        if (settings.HeartbeatSeconds < 10)
        {
            settings.HeartbeatSeconds = 60;
        }

        if (settings.PollIntervalSeconds < 1)
        {
            settings.PollIntervalSeconds = 3;
        }

        if (settings.PollBackoffMaxSeconds < settings.PollIntervalSeconds)
        {
            settings.PollBackoffMaxSeconds = Math.Max(settings.PollIntervalSeconds, 30);
        }

        if (string.IsNullOrWhiteSpace(settings.AgentType))
        {
            settings.AgentType = "platform-cad-connector";
        }

        if (string.IsNullOrWhiteSpace(settings.ModuleScope))
        {
            settings.ModuleScope = "shze,bridge,bns-piles,agr-publication";
        }
        else
        {
            var modules = settings.ModuleScope
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (modules.SetEquals(["shze", "bridge", "bns-piles"]))
            {
                settings.ModuleScope = "shze,bridge,bns-piles,agr-publication";
            }
        }

        settings.Capabilities = settings.Capabilities?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? ["autocad.build", "tekla.apply"];

        if (settings.Capabilities.Count == 0)
        {
            settings.Capabilities = ["autocad.build", "tekla.apply"];
        }

        settings.AutoCadSessionKey = settings.AutoCadSessionKey?.Trim() ?? string.Empty;
    }
}
