using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Platform.Connector.Core;

namespace Platform.Connector.Compute.Agr;

/// <summary>
/// Downloads an immutable AGR input package from Platform.Server into the
/// connector-local job store. Blender only ever receives local paths.
/// </summary>
public sealed class AgrJobInputMaterializer(AgrComputeOptions computeOptions, HttpClient? httpClient = null)
{
    private readonly string _jobStoreRoot = Path.GetFullPath(computeOptions.JobStorePath);
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private ConnectorRuntimeOptions? _runtimeOptions;

    public void Configure(ConnectorRuntimeOptions options)
        => _runtimeOptions = options ?? throw new ArgumentNullException(nameof(options));

    public async Task MaterializeAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!payload.TryGetProperty("inputFiles", out var files)
            || files.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }
        if (files.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("AGR payload.inputFiles must be an array.");
        }

        var options = _runtimeOptions
            ?? throw new InvalidOperationException("AGR input transfer is not configured for the connector runtime.");
        foreach (var file in files.EnumerateArray())
        {
            await MaterializeFileAsync(options, file, cancellationToken);
        }
    }

    private async Task MaterializeFileAsync(
        ConnectorRuntimeOptions options,
        JsonElement descriptor,
        CancellationToken cancellationToken)
    {
        var relativePath = ReadRequiredString(descriptor, "relativePath");
        var downloadPath = ReadRequiredString(descriptor, "downloadPath");
        var expectedSha256 = ReadRequiredString(descriptor, "sha256").ToLowerInvariant();
        var expectedLength = descriptor.TryGetProperty("byteLength", out var lengthElement)
            && lengthElement.TryGetInt64(out var length)
            ? length
            : throw new InvalidOperationException("AGR input file byteLength is required.");
        if (expectedLength < 0 || expectedSha256.Length != 64 || expectedSha256.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new InvalidOperationException("AGR input file integrity descriptor is invalid.");
        }

        var destination = ResolveJobFilePath(relativePath);
        if (await MatchesAsync(destination, expectedLength, expectedSha256, cancellationToken))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".download-" + Guid.NewGuid().ToString("N");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ResolveDownloadUri(options.ServerUrl, downloadPath));
            request.Headers.Add("X-Device-Token", options.DeviceToken);
            if (!string.IsNullOrWhiteSpace(options.SessionId))
            {
                request.Headers.Add("X-Device-Session", options.SessionId);
            }
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            long written;
            string actualHash;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 128,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 128];
                written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    written += read;
                    if (written > expectedLength)
                    {
                        throw new InvalidOperationException("AGR input file is larger than declared.");
                    }
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await target.FlushAsync(cancellationToken);
                actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            if (written != expectedLength || !string.Equals(actualHash, expectedSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AGR input file failed the SHA-256/length integrity check.");
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public string ResolveJobFilePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("AGR input relativePath must be a non-empty relative path.");
        }
        var candidate = Path.GetFullPath(Path.Combine(
            _jobStoreRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = _jobStoreRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _jobStoreRoot
            : _jobStoreRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("AGR input path escapes the configured job store.");
        }
        return candidate;
    }

    private static Uri ResolveDownloadUri(string serverUrl, string downloadPath)
    {
        var baseUri = new Uri(serverUrl.TrimEnd('/') + "/", UriKind.Absolute);
        if (Uri.TryCreate(downloadPath, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("AGR downloadPath must be relative to Platform.Server.");
        }
        return new Uri(baseUri, downloadPath);
    }

    private static async Task<bool> MatchesAsync(
        string path,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expectedLength) return false;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        return string.Equals(actual, expectedSha256, StringComparison.Ordinal);
    }

    private static string ReadRequiredString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : throw new InvalidOperationException($"AGR input file {property} is required.");
}
