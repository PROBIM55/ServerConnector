using System.Text.Json;
using Connector.Access.Smb;

namespace Connector.Access.Smb.Helper;

internal static class SmbHelperRequestReader
{
    private const int MaximumRequestBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async ValueTask<SmbHelperRequest?> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        // JsonContent may stream without Content-Length. Bound the actual bytes too,
        // including when a test host or proxy does not enforce Kestrel's limit.
        if (request.ContentLength is long length && (length <= 0 || length > MaximumRequestBytes)) return null;

        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (body.Length + read > MaximumRequestBytes) return null;
            body.Write(buffer, 0, read);
        }
        if (body.Length == 0) return null;

        try { return JsonSerializer.Deserialize<SmbHelperRequest>(body.ToArray(), JsonOptions); }
        catch (JsonException) { return null; }
    }
}
