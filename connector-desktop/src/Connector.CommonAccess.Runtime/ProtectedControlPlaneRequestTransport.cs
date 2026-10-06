using System.Net.Http.Headers;
using System.Text.Json;
using Connector.Network;
using Platform.Connector.Core;

namespace Connector.CommonAccess.Runtime;

/// <summary>Bounded requests through the already authenticated private control-plane route.</summary>
public sealed class ProtectedControlPlaneRequestTransport(
    Func<string, string, CancellationToken, ValueTask<ProtectedServiceRoute>> openRoute)
    : ICommonConnectorRequestTransport
{
    private const int MaximumBodyBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativeUri, object? body,
        IReadOnlyDictionary<string, string> managedHeaders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openRoute);
        ArgumentNullException.ThrowIfNull(managedHeaders);
        foreach (var header in managedHeaders)
            if (header.Key != "X-Device-Session" || string.IsNullOrWhiteSpace(header.Value) ||
                header.Value.Length > 256 || header.Value.Any(char.IsControl))
                throw new InvalidOperationException("Unsupported managed control-plane header.");
        var content = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        if (content?.Length > MaximumBodyBytes) throw new InvalidDataException("Control-plane request is too large.");
        using var route = await openRoute("control-plane", relativeUri, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, route.Uri);
        if (content is not null)
        {
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        foreach (var header in managedHeaders) request.Headers.Add(header.Key, header.Value);
        using var response = await route.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaximumBodyBytes)
            throw new InvalidDataException("Control-plane response is too large.");
        using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaximumBodyBytes) throw new InvalidDataException("Control-plane response is too large.");
            buffer.Write(chunk, 0, count);
        }
        var detached = new HttpResponseMessage(response.StatusCode) { Content = new ByteArrayContent(buffer.ToArray()) };
        foreach (var header in response.Content.Headers)
            detached.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return detached;
    }
}
