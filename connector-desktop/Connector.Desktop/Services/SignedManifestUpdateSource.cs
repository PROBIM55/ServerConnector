using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Connector.Desktop.Services;

/// <summary>Authenticates the release catalog and every package before Velopack can consume it.</summary>
internal sealed class SignedManifestUpdateSource : IUpdateSource, IDisposable
{
    internal const string ApplicationId = "Structura.Connector.Desktop";
    private readonly SimpleWebSource _webSource;
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly string _channel;
    private readonly ECDsa _publicKey;
    private SignedReleaseManifest? _manifest;

    internal SignedManifestUpdateSource(string feedUrl, string channel, string publicKeyPem, HttpMessageHandler? handler = null)
    {
        _baseUri = new Uri(feedUrl.TrimEnd('/') + "/", UriKind.Absolute);
        if (_baseUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(_baseUri.UserInfo) ||
            !RegexChannel.IsMatch(channel)) throw new InvalidDataException("Invalid signed update source configuration.");
        _channel = channel;
        _publicKey = ECDsa.Create();
        _publicKey.ImportFromPem(publicKeyPem);
        if (_publicKey.KeySize != 256) throw new InvalidDataException("Pinned update key must be P-256.");
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true);
        _http.Timeout = TimeSpan.FromSeconds(45);
        // SimpleWebSource parses the native Velopack index; the authenticated manifest gates its contents.
        _webSource = new SimpleWebSource(_baseUri, new NoRedirectFileDownloader(_http, _baseUri));
    }

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        if (!string.Equals(appId, ApplicationId, StringComparison.Ordinal) ||
            !string.Equals(channel, _channel, StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected update application or channel.");

        _manifest = await ReadAndVerifyManifestAsync();
        var feed = await _webSource.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
        var assets = feed.Assets ?? [];
        var listed = _manifest.Assets.ToDictionary(a => a.FileName, StringComparer.OrdinalIgnoreCase);
        if (assets.Length != listed.Count) throw new InvalidDataException("Velopack feed does not match its signed manifest.");
        foreach (var asset in assets)
        {
            if (!listed.TryGetValue(asset.FileName, out var signed) ||
                !string.Equals(asset.Version.ToString(), signed.Version, StringComparison.Ordinal) ||
                !string.Equals(asset.Type.ToString(), signed.Type, StringComparison.OrdinalIgnoreCase) ||
                asset.Size != signed.Size || !string.Equals(asset.SHA256, signed.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unexpected or modified asset in Velopack feed.");
        }
        return feed;
    }

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
        Action<int> progress, CancellationToken cancelToken)
    {
        var manifest = _manifest ?? throw new InvalidOperationException("Signed feed must be checked before downloading.");
        if (!TryGetSignedAsset(manifest, releaseEntry, out var signed))
            throw new InvalidDataException("Package is not present in the signed release manifest.");

        var uri = new Uri(_baseUri, signed.FileName);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != _baseUri.Host || uri.Port != _baseUri.Port ||
            uri.AbsolutePath != _baseUri.AbsolutePath + signed.FileName)
            throw new InvalidDataException("Package URL escaped the configured update feed.");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancelToken);
        if (response.StatusCode is >= HttpStatusCode.MovedPermanently and <= HttpStatusCode.TemporaryRedirect ||
            response.StatusCode == HttpStatusCode.PermanentRedirect)
            throw new InvalidDataException("Update source redirects are not allowed.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != signed.Size)
            throw new InvalidDataException("Package size does not match its signed manifest.");

        var directory = Path.GetDirectoryName(Path.GetFullPath(localFile))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".download");
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancelToken))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancelToken)) != 0)
                {
                    total += read;
                    if (total > signed.Size) throw new InvalidDataException("Package exceeds its signed size.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancelToken);
                }
                await output.FlushAsync(cancelToken);
                if (total != signed.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(signed.Sha256)))
                    throw new InvalidDataException("Package hash or size does not match its signed manifest.");
            }
            File.Move(temporary, localFile, overwrite: true);
            progress(100);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal async Task<SignedReleaseManifest> ReadAndVerifyManifestAsync()
    {
        var uri = new Uri(_baseUri, $"connector-release.{_channel}.json");
        var signatureUri = new Uri(uri.AbsoluteUri + ".sig");
        var bytes = await GetBytesNoRedirect(uri);
        var signature = await GetBytesNoRedirect(signatureUri);
        if (signature.Length != 64 || !_publicKey.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("Release manifest signature is missing or invalid.");
        using var document = JsonDocument.Parse(bytes);
        RejectDuplicateProperties(document.RootElement);
        RequireProperties(document.RootElement, "schemaVersion", "applicationId", "channel", "assets");
        if (document.RootElement.GetProperty("assets").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Release assets must be an array.");
        foreach (var assetElement in document.RootElement.GetProperty("assets").EnumerateArray())
            RequireProperties(assetElement, "fileName", "version", "type", "size", "sha256");
        var parsed = document.RootElement.Deserialize<SignedReleaseManifest>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                     ?? throw new InvalidDataException("Empty release manifest.");
        if (parsed.SchemaVersion != 1 || parsed.ApplicationId != ApplicationId || parsed.Channel != _channel || parsed.Assets is null || parsed.Assets.Length == 0)
            throw new InvalidDataException("Unexpected release manifest identity or schema.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in parsed.Assets)
        {
            if (!IsLeafFileName(asset.FileName) || !names.Add(asset.FileName) ||
                !System.Text.RegularExpressions.Regex.IsMatch(asset.Version, @"^\d+(\.\d+){1,3}([-.+][A-Za-z0-9.-]+)?$") ||
                asset.Type is not ("Full" or "Delta") || asset.Size <= 0 ||
                !System.Text.RegularExpressions.Regex.IsMatch(asset.Sha256, "^[A-Fa-f0-9]{64}$"))
                throw new InvalidDataException("Invalid or duplicate signed release asset.");
        }
        _manifest = parsed;
        return parsed;
    }

    private async Task<byte[]> GetBytesNoRedirect(Uri uri)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode is >= HttpStatusCode.MovedPermanently and <= HttpStatusCode.TemporaryRedirect || response.StatusCode == HttpStatusCode.PermanentRedirect)
            throw new InvalidDataException("Update source redirects are not allowed.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    private sealed class NoRedirectFileDownloader(HttpClient client, Uri baseUri) : IFileDownloader
    {
        public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            using var request = CreateRequest(url, headers ?? new Dictionary<string, string>());
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            RejectRedirect(response);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync();
        }

        public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            System.Text.Encoding.UTF8.GetString(await DownloadBytes(url, headers, timeout));

        public async Task DownloadFile(string url, string filePath, Action<int> progress,
            IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancellationToken = default)
        {
            using var request = CreateRequest(url, headers ?? new Dictionary<string, string>());
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            RejectRedirect(response);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);
            progress(100);
        }

        private HttpRequestMessage CreateRequest(string value, IDictionary<string, string> headers)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != baseUri.Port ||
                !uri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal) || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidDataException("Velopack requested a URL outside the configured HTTPS feed.");
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (headers is not null) foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return request;
        }

        private static void RejectRedirect(HttpResponseMessage response)
        {
            if (response.StatusCode is >= HttpStatusCode.MovedPermanently and <= HttpStatusCode.TemporaryRedirect ||
                response.StatusCode == HttpStatusCode.PermanentRedirect)
                throw new InvalidDataException("Update source redirects are not allowed.");
        }
    }

    private static bool TryGetSignedAsset(SignedReleaseManifest manifest, VelopackAsset asset, out SignedReleaseAsset signed) =>
        (signed = manifest.Assets.SingleOrDefault(item => string.Equals(item.FileName, asset.FileName, StringComparison.OrdinalIgnoreCase))!) is not null &&
        asset.Size == signed.Size && string.Equals(asset.Version.ToString(), signed.Version, StringComparison.Ordinal) &&
        string.Equals(asset.Type.ToString(), signed.Type, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(asset.SHA256, signed.Sha256, StringComparison.OrdinalIgnoreCase);

    private static bool IsLeafFileName(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 240 &&
        value is not ("." or "..") && !value.Contains('/') && !value.Contains('\\') && !value.Contains(':') &&
        !value.Contains('?') && !value.Contains('#') && !Path.IsPathRooted(value) && value.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase);

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property in signed manifest.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static void RequireProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid object in signed manifest.");
        var actual = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (actual.Count != expected.Length || expected.Any(name => !actual.Contains(name)))
            throw new InvalidDataException("Unexpected or missing property in signed manifest.");
    }

    public void Dispose()
    {
        _publicKey.Dispose();
        _http.Dispose();
    }

    private static readonly System.Text.RegularExpressions.Regex RegexChannel = new("^[A-Za-z0-9][A-Za-z0-9_-]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    internal sealed record SignedReleaseManifest(int SchemaVersion, string ApplicationId, string Channel, SignedReleaseAsset[] Assets);
    internal sealed record SignedReleaseAsset(string FileName, string Version, string Type, long Size, string Sha256);
}
