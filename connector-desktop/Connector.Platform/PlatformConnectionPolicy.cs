using Platform.Connector.Core;

namespace Connector.Platform;

/// <summary>Чистые правила адреса Platform и использования импортированного токена.</summary>
public static class PlatformConnectionPolicy
{
    public static string AutomaticServerUrl { get; } = NormalizeAutomaticServerUrl();

    public static bool RequiresVpn => true;

    public static bool TryNormalizeServerUrl(string? value, out string normalizedUrl, out string error)
    {
        normalizedUrl = string.Empty;
        error = string.Empty;

        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Укажите полный адрес API Platform без данных входа и фрагмента.";
            return false;
        }

        var isLoopback = uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && isLoopback))
        {
            error = "Для Platform нужен HTTPS; HTTP разрешён только для localhost при тестировании.";
            return false;
        }

        normalizedUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    public static bool CanUseImportedToken(string normalizedServerUrl, string? normalizedImportedServerUrl) =>
        !string.IsNullOrWhiteSpace(normalizedImportedServerUrl) &&
        string.Equals(normalizedServerUrl, normalizedImportedServerUrl, StringComparison.Ordinal);

    public static bool IsAutomaticServerUrl(string? value) =>
        TryNormalizeServerUrl(value, out var normalized, out _) &&
        string.Equals(normalized, AutomaticServerUrl, StringComparison.Ordinal);

    private static string NormalizeAutomaticServerUrl()
    {
        if (!TryNormalizeServerUrl(DesktopSettingsService.DefaultServerUrl, out var normalized, out var error))
        {
            throw new InvalidOperationException($"Некорректный штатный адрес Platform: {error}");
        }

        return normalized;
    }
}
