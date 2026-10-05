using Platform.Connector.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Connector.Platform;

/// <summary>
/// Native Platform state owned by the unified Structura Connector. The persisted
/// JSON never contains the credential or an absolute credential path.
/// </summary>
public sealed class PlatformSettingsPersistence
{
    private readonly DesktopSettingsService _settingsService;
    private readonly string _credentialPath;
    private int _hasStoredCredential;

    public PlatformSettingsPersistence(string? rootDirectory = null)
    {
        var root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Structura Connector",
            "Platform");
        _settingsService = new DesktopSettingsService(root);
        _credentialPath = Path.Combine(root, "secrets", "device-token.dat");
        _hasStoredCredential = File.Exists(_credentialPath) ? 1 : 0;
    }

    public string SettingsPath => _settingsService.SettingsPath;

    public DesktopConnectorSettings Load()
    {
        var settings = _settingsService.Load();
        settings.ServerUrl = PlatformConnectionPolicy.AutomaticServerUrl;
        settings.SecureTokenPath = _credentialPath;
        return settings;
    }

    public bool HasStoredCredential => Volatile.Read(ref _hasStoredCredential) == 1;

    public bool TryLoadCredential(out string token)
    {
        var loaded = SecureTokenStore.TryLoad(_credentialPath, out token);
        Volatile.Write(ref _hasStoredCredential, loaded ? 1 : 0);
        return loaded;
    }

    public void Save(DesktopConnectorSettings settings, string token)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SecureTokenStore.Save(_credentialPath, token);
        Volatile.Write(ref _hasStoredCredential, 1);
        SaveSettings(settings);
    }

    public void SaveSettings(DesktopConnectorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var persisted = CopyForPersistence(settings);
        _settingsService.Save(persisted);
        settings.ServerUrl = PlatformConnectionPolicy.AutomaticServerUrl;
        settings.SecureTokenPath = _credentialPath;
    }

    /// <summary>
    /// Imports a legacy credential when the caller explicitly requested replacement.
    /// The manual Platform UI uses this path.
    /// </summary>
    public PlatformLegacyImportResult ImportLegacy(DesktopSettingsService? legacyService = null) =>
        ImportLegacyCore(legacyService, requireFreshTarget: false);

    /// <summary>
    /// Imports a legacy credential only into a target that has never been initialized.
    /// Intended for installer and first-run migration.
    /// </summary>
    public PlatformLegacyImportResult ImportLegacyIfFresh(DesktopSettingsService? legacyService = null) =>
        ImportLegacyCore(legacyService, requireFreshTarget: true);

    private PlatformLegacyImportResult ImportLegacyCore(
        DesktopSettingsService? legacyService,
        bool requireFreshTarget)
    {
        legacyService ??= new DesktopSettingsService();
        if (requireFreshTarget && File.Exists(SettingsPath))
        {
            return new(false, "PLATFORM_LEGACY_TARGET_EXISTS",
                "Новые настройки Platform уже существуют; прежние данные не импортированы.");
        }

        if (!TryLoadValidLegacySettings(legacyService, out var legacy, out var endpointRejected))
        {
            return endpointRejected
                ? new(false, "PLATFORM_LEGACY_ENDPOINT_REJECTED",
                    "Прежние настройки относятся к другому серверу Platform; токен не импортирован.")
                : new(false, "PLATFORM_LEGACY_SETTINGS_INVALID",
                    "Прежние настройки Platform повреждены или содержат недопустимые значения; токен не импортирован.");
        }

        if (!SecureTokenStore.TryLoad(legacy.SecureTokenPath, out var legacyToken))
        {
            return new(false, "PLATFORM_LEGACY_CREDENTIAL_NOT_FOUND",
                "В прежних настройках не найден доступный токен Platform.");
        }

        var targetCredentialExists = File.Exists(_credentialPath);
        string targetToken = string.Empty;
        var targetCredentialIsUsable = targetCredentialExists && TryLoadCredential(out targetToken);
        if (requireFreshTarget && targetCredentialExists && !targetCredentialIsUsable)
        {
            return new(false, "PLATFORM_LEGACY_TARGET_EXISTS",
                "В новых настройках Platform уже есть недоступный токен; прежние данные не импортированы.");
        }

        var reusePartialImportCredential = requireFreshTarget && targetCredentialIsUsable;
        if (reusePartialImportCredential && !CredentialsMatch(targetToken, legacyToken))
        {
            return new(false, "PLATFORM_LEGACY_TARGET_EXISTS",
                "В новых настройках Platform уже есть другой токен; прежние данные не импортированы.");
        }

        legacy.ServerUrl = PlatformConnectionPolicy.AutomaticServerUrl;
        var persisted = CopyForPersistence(legacy);
        try
        {
            // The credential intentionally precedes settings: a completed settings
            // file is the fresh-import marker. If settings persistence fails, the
            // next first run reuses this same-user DPAPI credential and retries
            // only the settings write without replacing a token.
            if (!reusePartialImportCredential)
            {
                SecureTokenStore.Save(_credentialPath, legacyToken);
                Volatile.Write(ref _hasStoredCredential, 1);
            }

            if (requireFreshTarget)
                PublishFreshSettingsAtomically(persisted);
            else
                _settingsService.Save(persisted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return new(false, "PLATFORM_LEGACY_TARGET_SAVE_FAILED",
                "Не удалось сохранить импортированные настройки Platform; импорт будет повторен при следующем запуске.");
        }

        legacy.SecureTokenPath = _credentialPath;
        return new(true, "PLATFORM_LEGACY_IMPORTED", "Прежние настройки Platform импортированы.");
    }

    private static bool CredentialsMatch(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        try
        {
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private void PublishFreshSettingsAtomically(DesktopConnectorSettings settings)
    {
        // A failed direct WriteAllText can leave settings.json present but truncated.
        // The existence of settings.json is the completion marker for a fresh
        // import, so write in the same directory and publish only after flush.
        DesktopSettingsService.Normalize(settings);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidDataException("Platform settings directory is missing.");
        var temporary = Path.Combine(directory, ".settings-import-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 16 * 1024, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // Another initialized target wins; never overwrite its settings.
            File.Move(temporary, SettingsPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool TryLoadValidLegacySettings(
        DesktopSettingsService legacyService,
        out DesktopConnectorSettings settings,
        out bool endpointRejected)
    {
        settings = new DesktopConnectorSettings();
        endpointRejected = false;
        try
        {
            var json = File.ReadAllText(legacyService.SettingsPath);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(nameof(DesktopConnectorSettings.ServerUrl), out var serverUrl) ||
                serverUrl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            endpointRejected = !PlatformConnectionPolicy.IsAutomaticServerUrl(serverUrl.GetString());
            if (endpointRejected) return false;

            settings = JsonSerializer.Deserialize<DesktopConnectorSettings>(json) ?? throw new JsonException();
            return settings.Capabilities is not null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DesktopConnectorSettings CopyForPersistence(DesktopConnectorSettings source) => new()
    {
        ServerUrl = PlatformConnectionPolicy.AutomaticServerUrl,
        DeviceId = source.DeviceId,
        SecureTokenPath = string.Empty,
        HeartbeatSeconds = source.HeartbeatSeconds,
        PollIntervalSeconds = source.PollIntervalSeconds,
        PollBackoffMaxSeconds = source.PollBackoffMaxSeconds,
        AgentType = source.AgentType,
        ModuleScope = source.ModuleScope,
        Capabilities = [.. source.Capabilities],
        UpdateManifestUrl = source.UpdateManifestUrl,
        AutoStart = source.AutoStart,
        AutoCadSessionKey = source.AutoCadSessionKey
    };
}

public sealed record PlatformLegacyImportResult(bool Imported, string Code, string Message);
