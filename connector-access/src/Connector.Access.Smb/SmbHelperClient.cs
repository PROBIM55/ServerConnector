using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Connector.Access.Smb;

internal sealed class SmbHelperClient
{
    private const int MaximumResponseBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly ValidatedSmbProviderOptions _options;

    public SmbHelperClient(HttpClient http, ValidatedSmbProviderOptions options)
    {
        _http = http;
        _options = options;
    }

    public async ValueTask<SmbHelperResponse> ReconcileAsync(SmbHelperRequest payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.HelperBaseUri, "v1/smb/grants/reconcile"))
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new SmbHelperProtocolException("SMB helper rejected reconciliation.");
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new SmbHelperProtocolException("SMB helper response exceeded the allowed size.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (bounded.Length + read > MaximumResponseBytes)
                throw new SmbHelperProtocolException("SMB helper response exceeded the allowed size.");
            bounded.Write(buffer, 0, read);
        }
        try
        {
            return JsonSerializer.Deserialize<SmbHelperResponse>(bounded.ToArray(), JsonOptions)
                ?? throw new SmbHelperProtocolException("SMB helper returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new SmbHelperProtocolException("SMB helper returned invalid JSON.", exception);
        }
    }

    public static HttpClient CreateMutualTlsClient(SmbProviderOptions options)
    {
        var validated = options.Validate();
        var password = Environment.GetEnvironmentVariable(validated.ClientCertificatePasswordEnvironmentVariable);
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("SMB helper client certificate password environment variable is unavailable.");
#pragma warning disable SYSLIB0057
        var certificate = new X509Certificate2(
            validated.ClientCertificatePfxPath, password,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.MachineKeySet);
#pragma warning restore SYSLIB0057
        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException("SMB helper client certificate has no private key.");
        }
        var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(certificate);
        // Default platform server-certificate validation is intentionally retained.
        return new HttpClient(handler, disposeHandler: true) { Timeout = validated.RequestTimeout };
    }
}
