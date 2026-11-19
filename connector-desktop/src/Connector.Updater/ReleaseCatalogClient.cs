using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Connector.Updates;

namespace Connector.Updater;

/// <summary>
/// HTTPS access to the update catalog of one fixed origin. No redirects are followed (any 3xx is refused),
/// no cookies, no decompression, bounded bodies; the installer is fetched with resumable <c>Range</c> requests
/// into the SYSTEM-only download directory and is kept only if its size and SHA-256 match the signed description.
/// </summary>
internal sealed class ReleaseCatalogClient : IUpdateSource, IDisposable
{
    /// <summary>The one catalog of the product. Not configurable.</summary>
    internal static readonly Uri ProductionBase = new("https://bimplatforma.ru/api/platform/connectors/unified/", UriKind.Absolute);

    private const int CopyBufferBytes = 128 * 1024;
    private static readonly TimeSpan DescriptionTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadInactivityTimeout = TimeSpan.FromSeconds(90);
    /// <summary>Any request must deliver its response headers within this time (ConnectTimeout covers TCP only).</summary>
    private static readonly TimeSpan DefaultHeadersTimeout = TimeSpan.FromSeconds(60);
    /// <summary>Room left for Burn and Windows Installer to unpack beside the downloaded installer.</summary>
    internal const long DiskReserveBytes = 2L * 1024 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly ReleaseDescriptionVerifier _verifier;
    private readonly InstallerFiles _files;
    private readonly TimeSpan _headersTimeout;

    public ReleaseCatalogClient(ReleaseDescriptionVerifier verifier, InstallerFiles files, string userAgentVersion)
        : this(CreateHandler(), ProductionBase, verifier, files, userAgentVersion)
    {
    }

    /// <summary>Tests pass an in-memory handler; the origin is still required to be HTTPS.</summary>
    internal ReleaseCatalogClient(HttpMessageHandler handler, Uri catalogBase, ReleaseDescriptionVerifier verifier, InstallerFiles files, string userAgentVersion,
        TimeSpan? headersTimeout = null)
    {
        _headersTimeout = headersTimeout ?? DefaultHeadersTimeout;
        if (!catalogBase.IsAbsoluteUri || catalogBase.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(catalogBase.UserInfo) ||
            !string.IsNullOrEmpty(catalogBase.Query) || !string.IsNullOrEmpty(catalogBase.Fragment) || !catalogBase.AbsolutePath.EndsWith('/'))
            throw new ArgumentException("The catalog base must be an HTTPS directory URI.", nameof(catalogBase));
        _base = catalogBase;
        _verifier = verifier;
        _files = files;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PlatformConnectorUpdater", userAgentVersion));
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        MaxResponseHeadersLength = 64,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        // Certificate validation is the platform default (system roots, revocation as configured);
        // there is deliberately no validation callback.
    };

    public async Task<ReleaseDescription> FetchVerifiedAsync(string channel, CancellationToken cancellationToken)
    {
        if (!ReleaseCatalogContract.IsValidChannel(channel)) throw new UpdateRejectedException(UpdateErrorCodes.ChannelInvalid);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DescriptionTimeout);
        var description = await GetBoundedAsync(ChannelUri(channel, "connector-release.json"), ReleaseCatalogContract.MaximumDescriptionBytes,
            UpdateErrorCodes.DescriptionTooLarge, timeout.Token).ConfigureAwait(false);
        var signature = await GetBoundedAsync(ChannelUri(channel, "connector-release.json.sig"), ReleaseCatalogContract.SignatureBytes,
            UpdateErrorCodes.SignatureInvalid, timeout.Token).ConfigureAwait(false);
        return _verifier.Verify(description, signature, channel);
    }

    public async Task DownloadAsync(ReleaseDescription release, IProgress<int> percent, CancellationToken cancellationToken)
    {
        var installer = release.Installer;
        if (installer.FileName != ReleaseCatalogContract.InstallerFileName(release.Version))
            throw new UpdateRejectedException(UpdateErrorCodes.DescriptionInvalid);
        if (_files.HasVerified(release.Version, installer)) { percent.Report(100); return; }

        var uri = ChannelUri(release.Channel, "files/" + installer.FileName);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var partial = _files.OpenPartial(release.Version, installer.Size);
            var offset = partial.Length;
            if (offset == installer.Size)
            {
                // Completed earlier but interrupted before the rename: verify instead of downloading again.
                if (partial.CompleteIfHashMatches(installer.Sha256)) { percent.Report(100); return; }
                continue;
            }
            // Never fill the system disk: the remaining bytes plus room for unpacking must be free.
            if (_files.AvailableBytes() < installer.Size - offset + DiskReserveBytes)
                throw new UpdateRejectedException(UpdateErrorCodes.DiskSpace);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var storedTag = offset > 0 ? partial.StoredETag : null;
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                // If the file behind the URL changed, the server answers 200 with the whole new file instead of a range.
                if (storedTag is not null) request.Headers.IfRange = new RangeConditionHeaderValue(storedTag);
            }
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                throw await BusyOrUnavailableAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
            {
                partial.Truncate();
                continue; // the partial file does not belong to this file any more: start over once
            }
            var tag = response.Headers.ETag is { IsWeak: false } strong ? strong : null;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (offset == 0 || range is null || !range.HasRange || !range.HasLength || range.Unit != "bytes" ||
                    range.From != offset || range.To != installer.Size - 1 || range.Length != installer.Size)
                {
                    partial.Truncate();
                    throw new UpdateRejectedException(UpdateErrorCodes.DownloadInvalidRange);
                }
                if (storedTag is not null && tag is not null && !storedTag.Equals(tag))
                {
                    partial.Truncate(); // a range of another file: start the file over
                    continue;
                }
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                if (offset > 0) partial.Truncate(); // the server ignored the range or the file changed: the body is the whole file
                offset = 0;
            }
            else if ((int)response.StatusCode >= 500) throw new UpdateRejectedException(UpdateErrorCodes.Network);
            else throw new UpdateRejectedException(UpdateErrorCodes.HttpStatus);
            if (tag is not null && (storedTag is null || !storedTag.Equals(tag))) partial.SaveETag(tag);

            if (response.Content.Headers.ContentLength is { } length && length != installer.Size - offset)
            {
                partial.Truncate();
                throw new UpdateRejectedException(UpdateErrorCodes.DownloadSizeMismatch);
            }

            await CopyBoundedAsync(response, partial, installer.Size, percent, cancellationToken).ConfigureAwait(false);
            if (partial.Length != installer.Size)
                throw new UpdateRejectedException(UpdateErrorCodes.Network); // connection ended early: resumed next time
            if (!partial.CompleteIfHashMatches(installer.Sha256))
                throw new UpdateRejectedException(UpdateErrorCodes.DownloadHashMismatch); // partial deleted
            percent.Report(100);
            return;
        }
        throw new UpdateRejectedException(UpdateErrorCodes.DownloadInvalidRange);
    }

    /// <summary>
    /// 503 of the catalog: «connector_download_busy» (at most four installer downloads at a time) is a request
    /// to come back after <c>Retry-After</c>; any other 503 is an ordinary transient server failure.
    /// </summary>
    private static async Task<Exception> BusyOrUnavailableAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = new byte[1024];
        var total = 0;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            int read;
            while (total < body.Length && (read = await stream.ReadAsync(body.AsMemory(total), cancellationToken).ConfigureAwait(false)) != 0)
                total += read;
        }
        catch (IOException) { }
        var text = System.Text.Encoding.UTF8.GetString(body, 0, total);
        if (!text.Contains("\"connector_download_busy\"", StringComparison.Ordinal))
            return new UpdateRejectedException(UpdateErrorCodes.Network);
        var retry = response.Headers.RetryAfter;
        TimeSpan? after = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return new DownloadBusyException(after ?? DownloadBusyException.DefaultRetryAfter);
    }

    private static async Task CopyBoundedAsync(HttpResponseMessage response, PartialInstaller partial, long size, IProgress<int> percent,
        CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[CopyBufferBytes];
        var lastPercent = -1;
        while (true)
        {
            using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            inactivity.CancelAfter(ReadInactivityTimeout);
            int read;
            try { read = await input.ReadAsync(buffer, inactivity.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new UpdateRejectedException(UpdateErrorCodes.Network);
            }
            if (read == 0) break;
            if (partial.Length + read > size)
            {
                partial.Truncate();
                throw new UpdateRejectedException(UpdateErrorCodes.DownloadSizeMismatch);
            }
            partial.Append(buffer.AsSpan(0, read));
            var now = (int)(partial.Length * 100 / size);
            if (now != lastPercent) { lastPercent = now; percent.Report(Math.Min(now, 99)); }
        }
    }

    private async Task<byte[]> GetBoundedAsync(Uri uri, int maximumBytes, string tooLargeCode, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new UpdateRejectedException(UpdateErrorCodes.HttpStatus);
        if (response.Content.Headers.ContentLength is { } length && length > maximumBytes) throw new UpdateRejectedException(tooLargeCode);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[maximumBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) != 0)
            total += read;
        if (total > maximumBytes) throw new UpdateRejectedException(tooLargeCode);
        return buffer.AsSpan(0, total).ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        using (var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            headers.CancelAfter(_headersTimeout);
            try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new UpdateRejectedException(UpdateErrorCodes.Network); // stalled before the headers
            }
        }
        var status = (int)response.StatusCode;
        var finalUri = response.RequestMessage?.RequestUri;
        if (status is >= 300 and < 400 || finalUri is null || !SameOrigin(finalUri))
        {
            response.Dispose();
            throw new UpdateRejectedException(UpdateErrorCodes.RedirectRefused);
        }
        return response;
    }

    private bool SameOrigin(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && string.Equals(uri.IdnHost, _base.IdnHost, StringComparison.OrdinalIgnoreCase) && uri.Port == _base.Port;

    private Uri ChannelUri(string channel, string leaf)
    {
        // Both parts are validated tokens ([a-z0-9-] channel, fixed or pattern-checked leaf), so no escaping can occur.
        var uri = new Uri(_base, channel + "/" + leaf);
        if (!SameOrigin(uri) || !uri.AbsolutePath.StartsWith(_base.AbsolutePath + channel + "/", StringComparison.Ordinal))
            throw new UpdateRejectedException(UpdateErrorCodes.DescriptionInvalid);
        return uri;
    }

    public void Dispose()
    {
        _http.Dispose();
        _verifier.Dispose();
    }
}

/// <summary>The catalog limits concurrent installer downloads; come back after <see cref="RetryAfter"/>. Not an installation attempt.</summary>
internal sealed class DownloadBusyException : Exception
{
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Minimum = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Maximum = TimeSpan.FromMinutes(10);

    public DownloadBusyException(TimeSpan retryAfter) : base(UpdateErrorCodes.DownloadBusy) =>
        RetryAfter = retryAfter < Minimum ? Minimum : retryAfter > Maximum ? Maximum : retryAfter;

    public TimeSpan RetryAfter { get; }
}

/// <summary>
/// Installer files in the SYSTEM-only download directory: <c>PlatformConnector-&lt;v&gt;.exe</c> (complete and
/// verified) and <c>…exe.partial</c> (being downloaded). Names come only from the verified version.
/// </summary>
internal sealed class InstallerFiles : IInstallerFiles
{
    private const int MaximumInstallerLogs = 6;
    private readonly string _downloads;
    private readonly string _logs;
    private readonly IFileTrust _trust;
    private readonly Func<long> _availableBytes;

    public InstallerFiles(string downloads, string logs, IFileTrust trust, Func<long>? availableBytes = null)
    {
        _downloads = Path.GetFullPath(downloads);
        _logs = Path.GetFullPath(logs);
        _trust = trust;
        _availableBytes = availableBytes ?? (() => new DriveInfo(Path.GetPathRoot(_downloads)!).AvailableFreeSpace);
    }

    public long AvailableBytes() => _availableBytes();

    private string FinalPath(ReleaseVersion version) => Path.Combine(_downloads, ReleaseCatalogContract.InstallerFileName(version));
    private string PartialPath(ReleaseVersion version) => FinalPath(version) + ".partial";

    public bool HasVerified(ReleaseVersion version, ReleaseInstaller installer)
    {
        using var lease = AcquireVerified(version, installer);
        return lease is not null;
    }

    public IInstallerLease? AcquireVerified(ReleaseVersion version, ReleaseInstaller installer)
    {
        var path = FinalPath(version);
        FileStream stream;
        try
        {
            // Read access, others may only read: the file can be neither written, renamed nor deleted while held.
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBuffer, FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        try
        {
            // Defence in depth: only a file this service could have written (owner SYSTEM/Administrators, nobody else
            // may write, one link, no reparse point), then the hash of the description verified in this run.
            if (_trust.IsTrusted(stream) && stream.Length == installer.Size && HashMatches(stream, installer.Sha256))
                return new Lease(path, stream);
        }
        catch (IOException) { }
        stream.Dispose();
        TryDelete(path);
        return null;
    }

    public PartialInstaller OpenPartial(ReleaseVersion version, long expectedSize)
    {
        Directory.CreateDirectory(_downloads);
        DeleteIfUntrusted(PartialPath(version));
        DeleteIfUntrusted(PartialPath(version) + ".etag");
        var partial = new PartialInstaller(PartialPath(version), FinalPath(version));
        if (partial.Length > expectedSize) partial.Truncate();
        return partial;
    }

    public void DeleteAll()
    {
        foreach (var file in OwnFiles()) TryDelete(file);
    }

    public void DeleteAllExcept(ReleaseVersion keep)
    {
        var keepNames = new[] { FinalPath(keep), PartialPath(keep), PartialPath(keep) + ".etag" };
        foreach (var file in OwnFiles())
            if (!keepNames.Contains(file, StringComparer.OrdinalIgnoreCase))
                TryDelete(file);
    }

    public string InstallerLogPath(ReleaseVersion version, int attempt)
    {
        Directory.CreateDirectory(_logs);
        var logs = new DirectoryInfo(_logs).GetFiles("install-*.log").OrderByDescending(file => file.LastWriteTimeUtc).Skip(MaximumInstallerLogs - 1);
        foreach (var old in logs) TryDelete(old.FullName);
        return Path.Combine(_logs, "install-" + version + "-" + attempt + ".log");
    }

    private void DeleteIfUntrusted(string path)
    {
        if (!File.Exists(path)) return;
        bool trusted;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            trusted = _trust.IsTrusted(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { trusted = false; }
        if (!trusted) TryDelete(path);
    }

    private IEnumerable<string> OwnFiles()
    {
        if (!Directory.Exists(_downloads)) return Array.Empty<string>();
        return Directory.EnumerateFiles(_downloads, "PlatformConnector-*").ToArray();
    }

    internal static bool HashMatches(Stream stream, string expectedHex)
    {
        stream.Position = 0;
        var actual = SHA256.HashData(stream);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedHex));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const int CopyBuffer = 128 * 1024;

    private sealed class Lease(string path, FileStream stream) : IInstallerLease
    {
        public string Path { get; } = path;
        public void Dispose() => stream.Dispose();
    }
}

/// <summary>A partial download held exclusively while it is appended to.</summary>
internal sealed class PartialInstaller : IDisposable
{
    private readonly string _partialPath;
    private readonly string _finalPath;
    private FileStream? _stream;

    public PartialInstaller(string partialPath, string finalPath)
    {
        _partialPath = partialPath;
        _finalPath = finalPath;
        if (File.Exists(_partialPath) && (File.GetAttributes(_partialPath) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            File.Delete(_partialPath);
        _stream = new FileStream(_partialPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 128 * 1024);
        _stream.Seek(0, SeekOrigin.End);
    }

    public long Length => Stream.Length;

    private string ETagPath => _partialPath + ".etag";

    /// <summary>The strong ETag of the response the partial bytes came from (SYSTEM-only sidecar), or null.</summary>
    public EntityTagHeaderValue? StoredETag
    {
        get
        {
            try
            {
                var info = new FileInfo(ETagPath);
                if (!info.Exists || info.Length > 512 || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                return EntityTagHeaderValue.TryParse(File.ReadAllText(ETagPath), out var tag) && !tag.IsWeak ? tag : null;
            }
            catch (IOException) { return null; }
        }
    }

    public void SaveETag(EntityTagHeaderValue tag) => File.WriteAllText(ETagPath, tag.ToString());

    private void DeleteETag()
    {
        try { File.Delete(ETagPath); } catch (IOException) { }
    }

    private FileStream Stream => _stream ?? throw new ObjectDisposedException(nameof(PartialInstaller));

    public void Append(ReadOnlySpan<byte> data) => Stream.Write(data);

    public void Truncate()
    {
        DeleteETag();
        Stream.SetLength(0);
        Stream.Seek(0, SeekOrigin.Begin);
    }

    /// <summary>Hashes the complete file; on a match it becomes the final installer, otherwise it is deleted.</summary>
    public bool CompleteIfHashMatches(string expectedHex)
    {
        Stream.Flush(flushToDisk: true);
        var matches = InstallerFiles.HashMatches(Stream, expectedHex);
        Stream.Dispose();
        _stream = null;
        DeleteETag();
        if (!matches)
        {
            File.Delete(_partialPath);
            return false;
        }
        File.Move(_partialPath, _finalPath, overwrite: true);
        return true;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
