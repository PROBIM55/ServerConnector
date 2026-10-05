using System.IO;
using System.Text.Json;
using Velopack;

namespace Connector.Desktop.Services;

public sealed record PackageUpdateCandidate(string Version, string? Notes);

public interface IPackageUpdateBackend
{
    Task<PackageUpdateCandidate?> CheckForUpdatesAsync(CancellationToken cancellationToken);
    Task DownloadUpdatesAsync(PackageUpdateCandidate candidate, CancellationToken cancellationToken);
    void ApplyUpdatesAndRestart(PackageUpdateCandidate candidate);
}

public sealed class PackageUpdateService
{
    private readonly IPackageUpdateBackend? _backend;
    private readonly bool _isManagedInstall;
    private readonly string? _configurationError;
    private PackageUpdateCandidate? _downloadedCandidate;

    public PackageUpdateService(IPackageUpdateBackend? backend, bool? managedInstall = null, string? configurationError = null)
    {
        _backend = backend;
        _isManagedInstall = managedInstall ?? backend is not null;
        _configurationError = configurationError;
    }

    public bool IsManagedInstall => _isManagedInstall;

    public static PackageUpdateService CreateDefault()
    {
        if (Velopack.Locators.VelopackLocator.Current.CurrentlyInstalledVersion is null)
        {
            return new PackageUpdateService(null);
        }

        if (!PackageUpdateSettings.TryLoad(AppContext.BaseDirectory, out var settings))
        {
            return new PackageUpdateService(null, managedInstall: true,
                configurationError: "Источник пакетных обновлений не настроен. Установка MSI поверх этой версии отключена.");
        }

        var key = ReadPinnedUpdateKey();
        return new PackageUpdateService(new VelopackPackageUpdateBackend(settings.FeedUrl, settings.Channel, key));
    }

    private static string ReadPinnedUpdateKey()
    {
        var assembly = typeof(PackageUpdateService).Assembly;
        var resource = assembly.GetManifestResourceNames().SingleOrDefault(name => name.EndsWith("Assets.update-public-key.pem", StringComparison.Ordinal));
        if (resource is null) throw new InvalidOperationException("В сборке отсутствует закрепленный ключ проверки обновлений.");
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("Не удалось прочитать ключ проверки обновлений.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public Task<PackageUpdateCandidate?> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (_configurationError is not null) throw new InvalidOperationException(_configurationError);
        return _backend?.CheckForUpdatesAsync(cancellationToken) ?? Task.FromResult<PackageUpdateCandidate?>(null);
    }

    public async Task DownloadUpdatesAsync(PackageUpdateCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        _downloadedCandidate = null;
        cancellationToken.ThrowIfCancellationRequested();
        var backend = _backend ?? throw new InvalidOperationException("Пакетное обновление недоступно для этой установки.");
        await backend.DownloadUpdatesAsync(candidate, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _downloadedCandidate = candidate;
    }

    public void ApplyUpdatesAndRestart(PackageUpdateCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (_downloadedCandidate != candidate)
        {
            throw new InvalidOperationException("Пакет этой версии еще не загружен. Повторите загрузку обновления.");
        }
        (_backend ?? throw new InvalidOperationException("Пакетное обновление недоступно для этой установки."))
            .ApplyUpdatesAndRestart(candidate);
    }
}

public sealed record PackageUpdateSettings(string FeedUrl, string Channel)
{
    public static bool TryLoad(string baseDirectory, out PackageUpdateSettings settings)
    {
        settings = new PackageUpdateSettings(string.Empty, string.Empty);
        var path = Path.Combine(baseDirectory, "connector-update.json");
        if (!File.Exists(path)) return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<PackageUpdateSettings>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is null || !Uri.TryCreate(parsed.FeedUrl, UriKind.Absolute, out var feed) ||
                !string.Equals(feed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(feed.UserInfo) || string.IsNullOrWhiteSpace(parsed.Channel) ||
                !System.Text.RegularExpressions.Regex.IsMatch(parsed.Channel, "^[A-Za-z0-9][A-Za-z0-9_-]*$"))
            {
                return false;
            }

            settings = parsed with { FeedUrl = feed.AbsoluteUri.TrimEnd('/') };
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

internal sealed class VelopackPackageUpdateBackend : IPackageUpdateBackend
{
    private readonly UpdateManager _manager;
    private UpdateInfo? _pending;

    public VelopackPackageUpdateBackend(string feedUrl, string channel, string publicKeyPem) =>
        _manager = new UpdateManager(new SignedManifestUpdateSource(feedUrl, channel, publicKeyPem), new UpdateOptions { ExplicitChannel = channel });

    public async Task<PackageUpdateCandidate?> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        _pending = await _manager.CheckForUpdatesAsync().WaitAsync(cancellationToken);
        return _pending is null ? null : new PackageUpdateCandidate(_pending.TargetFullRelease.Version.ToString(), null);
    }

    public Task DownloadUpdatesAsync(PackageUpdateCandidate candidate, CancellationToken cancellationToken)
    {
        var pending = RequireCandidate(candidate);
        return _manager.DownloadUpdatesAsync(pending, cancelToken: cancellationToken);
    }

    public void ApplyUpdatesAndRestart(PackageUpdateCandidate candidate) =>
        _manager.ApplyUpdatesAndRestart(RequireCandidate(candidate));

    private UpdateInfo RequireCandidate(PackageUpdateCandidate candidate) =>
        _pending is not null && string.Equals(_pending.TargetFullRelease.Version.ToString(), candidate.Version, StringComparison.Ordinal)
            ? _pending
            : throw new InvalidOperationException("Сведения о пакетном обновлении устарели. Проверьте обновления снова.");
}
