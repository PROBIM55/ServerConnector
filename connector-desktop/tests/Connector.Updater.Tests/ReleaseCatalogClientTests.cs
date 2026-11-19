using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Connector.Updates;

namespace Connector.Updater.Tests;

public sealed class ReleaseCatalogClientTests : IDisposable
{
    private static readonly Uri Base = new("https://catalog.test/api/platform/connectors/unified/");
    private readonly TestRelease _release = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "k9-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHandler _handler = new();
    private readonly InstallerFiles _files;
    private readonly ReleaseCatalogClient _client;

    public ReleaseCatalogClientTests()
    {
        _files = new InstallerFiles(Path.Combine(_root, "downloads"), Path.Combine(_root, "logs"), new TrustAll(), () => Free);
        _client = new ReleaseCatalogClient(_handler, Base, _release.Verifier(), _files, "1.1.15", TimeSpan.FromMilliseconds(200));
    }

    private long Free { get; set; } = long.MaxValue;

    private static byte[] Payload(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 7 + 3)).ToArray();

    /// <summary>A response that announces the whole file but whose connection ends after <paramref name="sent"/>.</summary>
    private static HttpResponseMessage Truncated(byte[] sent, long announced)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(sent)) };
        response.Content.Headers.ContentLength = announced;
        return response;
    }

    private string FinalPath(ReleaseDescription release) => Path.Combine(_root, "downloads", release.Installer.FileName);

    [Fact]
    public async Task Description_is_fetched_from_the_channel_and_verified()
    {
        var (json, sig) = _release.Signed(TestRelease.Json());
        _handler.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/api/platform/connectors/unified/stable/connector-release.json" => FakeHandler.Ok(json),
            "/api/platform/connectors/unified/stable/connector-release.json.sig" => FakeHandler.Ok(sig),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
        var release = await _client.FetchVerifiedAsync("stable", CancellationToken.None);
        Assert.Equal("1.1.16", release.Version.ToString());
        Assert.All(_handler.Requests, request => Assert.Null(request.Headers.Authorization));
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Redirects_are_refused(HttpStatusCode status)
    {
        _handler.Respond = _ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri("https://evil.test/connector-release.json");
            return response;
        };
        var error = await Assert.ThrowsAsync<UpdateRejectedException>(() => _client.FetchVerifiedAsync("stable", CancellationToken.None));
        Assert.Equal(UpdateErrorCodes.RedirectRefused, error.Code);
    }

    [Fact]
    public async Task Oversized_description_is_refused_by_length_and_by_body()
    {
        _handler.Respond = _ => FakeHandler.Ok(new byte[70_000]);
        Assert.Equal(UpdateErrorCodes.DescriptionTooLarge,
            (await Assert.ThrowsAsync<UpdateRejectedException>(() => _client.FetchVerifiedAsync("stable", CancellationToken.None))).Code);
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[70_000])) };
        Assert.Equal(UpdateErrorCodes.DescriptionTooLarge,
            (await Assert.ThrowsAsync<UpdateRejectedException>(() => _client.FetchVerifiedAsync("stable", CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Fresh_download_lands_verified_under_its_version_name()
    {
        var payload = Payload(10_000);
        var release = TestRelease.Description(payload: payload);
        _handler.Respond = request =>
        {
            Assert.Equal("/api/platform/connectors/unified/stable/files/PlatformConnector-1.1.16.exe", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Range);
            return FakeHandler.Ok(payload);
        };
        var percents = new List<int>();
        await _client.DownloadAsync(release, new Collect(percents), CancellationToken.None);
        Assert.Equal(payload, File.ReadAllBytes(FinalPath(release)));
        Assert.False(File.Exists(FinalPath(release) + ".partial"));
        Assert.Equal(100, percents[^1]);
    }

    [Fact]
    public async Task Interrupted_download_resumes_with_range()
    {
        var payload = Payload(10_000);
        var release = TestRelease.Description(payload: payload);
        Directory.CreateDirectory(Path.Combine(_root, "downloads"));
        File.WriteAllBytes(FinalPath(release) + ".partial", payload[..4000]);
        _handler.Respond = request =>
        {
            Assert.Equal(4000, request.Headers.Range!.Ranges.Single().From);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(payload[4000..]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(4000, payload.Length - 1, payload.Length);
            return response;
        };
        await _client.DownloadAsync(release, new Collect([]), CancellationToken.None);
        Assert.Equal(payload, File.ReadAllBytes(FinalPath(release)));
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Server_ignoring_range_restarts_from_zero()
    {
        var payload = Payload(5000);
        var release = TestRelease.Description(payload: payload);
        Directory.CreateDirectory(Path.Combine(_root, "downloads"));
        File.WriteAllBytes(FinalPath(release) + ".partial", new byte[1000]); // wrong bytes from an earlier attempt
        _handler.Respond = _ => FakeHandler.Ok(payload);
        await _client.DownloadAsync(release, new Collect([]), CancellationToken.None);
        Assert.Equal(payload, File.ReadAllBytes(FinalPath(release)));
    }

    [Fact]
    public async Task Wrong_hash_deletes_the_file_and_keeps_nothing()
    {
        var payload = Payload(5000);
        var release = TestRelease.Description(payload: payload);
        var forged = (byte[])payload.Clone();
        forged[100] ^= 0xFF;
        _handler.Respond = _ => FakeHandler.Ok(forged);
        var error = await Assert.ThrowsAsync<UpdateRejectedException>(() => _client.DownloadAsync(release, new Collect([]), CancellationToken.None));
        Assert.Equal(UpdateErrorCodes.DownloadHashMismatch, error.Code);
        Assert.False(File.Exists(FinalPath(release)));
        Assert.False(File.Exists(FinalPath(release) + ".partial"));
    }

    [Fact]
    public async Task Body_longer_than_signed_size_is_cut_off()
    {
        var payload = Payload(5000);
        var release = TestRelease.Description(payload: payload);
        // No Content-Length: the bound is enforced while reading.
        _handler.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(payload.Concat(new byte[300_000]).ToArray())) };
            response.Content.Headers.ContentLength = null;
            return response;
        };
        var error = await Assert.ThrowsAsync<UpdateRejectedException>(() => _client.DownloadAsync(release, new Collect([]), CancellationToken.None));
        Assert.Equal(UpdateErrorCodes.DownloadSizeMismatch, error.Code);
        Assert.False(File.Exists(FinalPath(release)));
        // Declared length that differs is refused before reading.
        _handler.Respond = _ => FakeHandler.Ok(new byte[6000]);
        error = await Assert.ThrowsAsync<UpdateRejectedException>(() => _client.DownloadAsync(release, new Collect([]), CancellationToken.None));
        Assert.Equal(UpdateErrorCodes.DownloadSizeMismatch, error.Code);
    }

    [Fact]
    public async Task Content_range_must_continue_exactly_the_partial_file()
    {
        var payload = Payload(5000);
        var release = TestRelease.Description(payload: payload);
        Directory.CreateDirectory(Path.Combine(_root, "downloads"));
        File.WriteAllBytes(FinalPath(release) + ".partial", payload[..2000]);
        _handler.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(payload[1000..]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(1000, payload.Length - 1, payload.Length);
            return response;
        };
        var error = await Assert.ThrowsAsync<UpdateRejectedException>(() => _client.DownloadAsync(release, new Collect([]), CancellationToken.None));
        Assert.Equal(UpdateErrorCodes.DownloadInvalidRange, error.Code);
        Assert.False(File.Exists(FinalPath(release)));
    }

    [Fact]
    public async Task Connection_ending_early_keeps_the_partial_file_for_resume()
    {
        var payload = Payload(5000);
        var release = TestRelease.Description(payload: payload);
        _handler.Respond = _ => Truncated(payload[..3000], payload.Length);
        var error = await Assert.ThrowsAsync<UpdateRejectedException>(() => _client.DownloadAsync(release, new Collect([]), CancellationToken.None));
        Assert.Equal(UpdateErrorCodes.Network, error.Code);
        Assert.Equal(3000, new FileInfo(FinalPath(release) + ".partial").Length);
    }

    [Fact]
    public async Task Busy_catalog_then_wait_then_resume_from_the_same_offset()
    {
        var payload = Payload(9000);
        var release = TestRelease.Description(payload: payload);
        var tag = new EntityTagHeaderValue("\"v1116\"");
        // First connection delivers 3000 bytes and breaks; the file is kept with its ETag.
        _handler.Respond = _ =>
        {
            var response = Truncated(payload[..3000], payload.Length);
            response.Headers.ETag = tag;
            return response;
        };
        Assert.Equal(UpdateErrorCodes.Network,
            (await Assert.ThrowsAsync<UpdateRejectedException>(() => _client.DownloadAsync(release, new Collect([]), CancellationToken.None))).Code);

        // Then the catalog is at its limit of four downloads: 503 busy with Retry-After.
        _handler.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent("{\"ok\":false,\"error\":\"connector_download_busy\"}") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            return response;
        };
        var busy = await Assert.ThrowsAsync<DownloadBusyException>(() => _client.DownloadAsync(release, new Collect([]), CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(45), busy.RetryAfter);
        Assert.Equal(3000, new FileInfo(FinalPath(release) + ".partial").Length);

        // After the wait: Range from the same offset, If-Range with the stored ETag, 206 completes the file.
        _handler.Respond = request =>
        {
            Assert.Equal(3000, request.Headers.Range!.Ranges.Single().From);
            Assert.Equal(tag, request.Headers.IfRange!.EntityTag);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(payload[3000..]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3000, payload.Length - 1, payload.Length);
            response.Headers.ETag = tag;
            return response;
        };
        await _client.DownloadAsync(release, new Collect([]), CancellationToken.None);
        Assert.Equal(payload, File.ReadAllBytes(FinalPath(release)));
        Assert.False(File.Exists(FinalPath(release) + ".partial.etag"));
    }

    [Fact]
    public async Task Changed_etag_on_resume_starts_the_file_over()
    {
        var payload = Payload(6000);
        var release = TestRelease.Description(payload: payload);
        _handler.Respond = _ =>
        {
            var response = Truncated(new byte[2000], payload.Length);
            response.Headers.ETag = new EntityTagHeaderValue("\"old\"");
            return response;
        };
        await Assert.ThrowsAsync<UpdateRejectedException>(() => _client.DownloadAsync(release, new Collect([]), CancellationToken.None));
        var calls = 0;
        _handler.Respond = request =>
        {
            calls++;
            if (request.Headers.Range is not null)
            {
                // A server that answers the range although the tag differs: the client refuses to splice files.
                var partialResponse = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(payload[2000..]) };
                partialResponse.Content.Headers.ContentRange = new ContentRangeHeaderValue(2000, payload.Length - 1, payload.Length);
                partialResponse.Headers.ETag = new EntityTagHeaderValue("\"new\"");
                return partialResponse;
            }
            var whole = FakeHandler.Ok(payload);
            whole.Headers.ETag = new EntityTagHeaderValue("\"new\"");
            return whole;
        };
        await _client.DownloadAsync(release, new Collect([]), CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Equal(payload, File.ReadAllBytes(FinalPath(release)));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Other_server_failures_are_transient_network_errors(HttpStatusCode status)
    {
        var release = TestRelease.Description(payload: Payload(100));
        _handler.Respond = _ => new HttpResponseMessage(status) { Content = new StringContent("{\"ok\":false}") };
        var error = await Assert.ThrowsAsync<UpdateRejectedException>(() => _client.DownloadAsync(release, new Collect([]), CancellationToken.None));
        Assert.Equal(UpdateErrorCodes.Network, error.Code);
    }

    [Fact]
    public void Lease_reverifies_and_blocks_writes_while_held()
    {
        var payload = Payload(4000);
        var release = TestRelease.Description(payload: payload);
        Directory.CreateDirectory(Path.Combine(_root, "downloads"));
        File.WriteAllBytes(FinalPath(release), payload);
        using (var lease = _files.AcquireVerified(release.Version, release.Installer))
        {
            Assert.NotNull(lease);
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(FinalPath(release), new byte[4000]));
            Assert.ThrowsAny<IOException>(() => File.Delete(FinalPath(release)));
        }
        // Altered after download: the lease is refused and the file removed.
        File.WriteAllBytes(FinalPath(release), new byte[4000]);
        Assert.Null(_files.AcquireVerified(release.Version, release.Installer));
        Assert.False(File.Exists(FinalPath(release)));
    }

    [Fact]
    public async Task Response_headers_that_never_arrive_end_the_request()
    {
        _handler.Delay = true;
        var error = await Assert.ThrowsAsync<UpdateRejectedException>(() =>
            _client.DownloadAsync(TestRelease.Description(payload: Payload(100)), new Collect([]), CancellationToken.None));
        Assert.Equal(UpdateErrorCodes.Network, error.Code);
    }

    [Fact]
    public async Task No_download_without_room_for_the_file_and_two_gigabytes()
    {
        var release = TestRelease.Description(payload: Payload(1000));
        Free = 1000 + ReleaseCatalogClient.DiskReserveBytes - 1;
        _handler.Respond = _ => throw new InvalidOperationException("must not be requested");
        var error = await Assert.ThrowsAsync<UpdateRejectedException>(() => _client.DownloadAsync(release, new Collect([]), CancellationToken.None));
        Assert.Equal(UpdateErrorCodes.DiskSpace, error.Code);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public void Untrusted_files_are_neither_leased_nor_resumed()
    {
        var payload = Payload(4000);
        var release = TestRelease.Description(payload: payload);
        var downloads = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(downloads);
        File.WriteAllBytes(Path.Combine(downloads, release.Installer.FileName), payload); // right bytes, planted
        File.WriteAllBytes(Path.Combine(downloads, release.Installer.FileName + ".partial"), payload[..100]);
        var suspicious = new InstallerFiles(downloads, Path.Combine(_root, "logs"), new TrustNone());
        Assert.Null(suspicious.AcquireVerified(release.Version, release.Installer));
        Assert.False(File.Exists(Path.Combine(downloads, release.Installer.FileName)));
        using (var partial = suspicious.OpenPartial(release.Version, release.Installer.Size)) Assert.Equal(0, partial.Length);
    }

    [Fact]
    public void Planted_state_file_that_is_not_trustworthy_is_deleted_and_never_read()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "service-state.json");
        var planted = new UpdaterState
        {
            Target = new UpdateTarget { Version = new ReleaseVersion(9, 9, 9), Channel = "stable", ReadyAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
                Installer = new ReleaseInstaller("PlatformConnector-9.9.9.exe", 10, new string('c', 64)), Notes = [] }
        };
        File.WriteAllBytes(path, UpdaterStateCodec.Serialize(planted));
        var log = new FakeLog();
        var state = new FileStateStore(path, log, new TrustNone()).Load();
        Assert.Null(state.Target);
        Assert.False(File.Exists(path));
        Assert.Contains(log.Lines, line => line.Contains("state-reset"));
        // The same file written by the service itself is read back.
        File.WriteAllBytes(path, UpdaterStateCodec.Serialize(planted));
        Assert.Equal(new ReleaseVersion(9, 9, 9), new FileStateStore(path, log, new TrustAll()).Load().Target!.Version);
    }

    [Fact]
    public void A_file_owned_by_an_ordinary_user_is_not_trusted()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "mine.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var owner = stream.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier))!.Value;
        var expected = owner is "S-1-5-18" or "S-1-5-32-544";
        Assert.Equal(expected, new WindowsFileTrust().IsTrusted(stream));
        // An explicit write grant for Everyone makes even an administrators' file untrusted.
        var security = new System.Security.AccessControl.FileSecurity();
        security.SetOwner(new System.Security.Principal.SecurityIdentifier("S-1-5-32-544"));
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier("S-1-1-0"), System.Security.AccessControl.FileSystemRights.Write,
            System.Security.AccessControl.AccessControlType.Allow));
        Assert.False(WindowsFileTrust.IsTrusted(security));
    }

    public void Dispose()
    {
        _client.Dispose();
        _release.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class Collect(List<int> values) : IProgress<int>
    {
        public void Report(int value) => values.Add(value);
    }
}

internal sealed class FakeHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
    /// <summary>Never answers: the headers do not arrive until the request is cancelled.</summary>
    public bool Delay { get; set; }
    public List<HttpRequestMessage> Requests { get; } = new();

    public static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Delay) await Task.Delay(Timeout.Infinite, cancellationToken);
        Requests.Add(request);
        var response = Respond(request);
        response.RequestMessage ??= request;
        return response;
    }
}
