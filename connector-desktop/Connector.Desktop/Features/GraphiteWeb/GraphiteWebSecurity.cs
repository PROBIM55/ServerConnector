namespace Connector.Desktop.Features.GraphiteWeb;

internal static class GraphiteWebSecurity
{
    internal const string VirtualHostName = "connector.local";
    internal const string DocumentPath = "/desktop.html";
    internal const string DocumentUri = "https://connector.local/desktop.html";

    internal static bool IsTrustedDocument(string? value)
        => TryGetTrustedUri(value, out Uri? uri) && uri is not null &&
           string.Equals(uri.AbsolutePath, DocumentPath, StringComparison.Ordinal);

    internal static bool IsTrustedResource(string? value)
        => TryGetTrustedUri(value, out _);

    private static bool TryGetTrustedUri(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? candidate))
        {
            return false;
        }

        if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.IdnHost, VirtualHostName, StringComparison.OrdinalIgnoreCase) ||
            candidate.Port != 443 ||
            !string.IsNullOrEmpty(candidate.UserInfo))
        {
            return false;
        }

        uri = candidate;
        return true;
    }
}
