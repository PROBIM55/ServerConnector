namespace Platform.Connector.Core;

/// <summary>
/// Stable routing identity for durable remote results. It deliberately excludes
/// credentials, query and fragment while retaining the API base path.
/// </summary>
public static class ConnectorRemoteAuthority
{
    public static string Normalize(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("Connector server URL must be an absolute HTTP(S) URL.", nameof(serverUrl));
        }

        var host = uri.HostNameType == UriHostNameType.IPv6
            ? $"[{uri.Host.ToLowerInvariant()}]"
            : uri.IdnHost.ToLowerInvariant();
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme.ToLowerInvariant()}://{host}{port}{path}";
    }
}
