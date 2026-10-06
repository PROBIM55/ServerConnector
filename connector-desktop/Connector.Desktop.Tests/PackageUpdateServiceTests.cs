using Connector.Desktop.Services;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class PackageUpdateServiceTests
{
    [Fact]
    public void TryLoad_RejectsMissingOrNonHttpsFeed()
    {
        var root = Path.Combine(Path.GetTempPath(), "connector-update-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            Assert.False(PackageUpdateSettings.TryLoad(root, out _));
            File.WriteAllText(Path.Combine(root, "connector-update.json"), "{\"feedUrl\":\"http://example.test\"}");
            Assert.False(PackageUpdateSettings.TryLoad(root, out _));
            File.WriteAllText(Path.Combine(root, "connector-update.json"), "{\"feedUrl\":\"https://updates.example.test/path/\",\"channel\":\"stable\"}");
            Assert.True(PackageUpdateSettings.TryLoad(root, out var settings));
            Assert.Equal("https://updates.example.test/path", settings.FeedUrl);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ManagedInstall_WithBrokenFeed_RemainsManagedAndBlocksFallback()
    {
        var service = new PackageUpdateService(null, managedInstall: true, configurationError: "broken feed");
        Assert.True(service.IsManagedInstall);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdatesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancelledDownload_CannotApplyAndDoesNotCallBackend()
    {
        var backend = new FakeBackend();
        var service = new PackageUpdateService(backend);
        var candidate = (await service.CheckForUpdatesAsync(CancellationToken.None))!;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadUpdatesAsync(candidate, cancellation.Token));
        Assert.False(backend.Downloaded);
        Assert.Throws<InvalidOperationException>(() => service.ApplyUpdatesAndRestart(candidate));
        Assert.False(backend.Applied);
    }

    [Fact]
    public async Task DownloadedVersion_CannotApplyAnotherVersion()
    {
        var backend = new FakeBackend();
        var service = new PackageUpdateService(backend);
        var candidate = (await service.CheckForUpdatesAsync(CancellationToken.None))!;
        await service.DownloadUpdatesAsync(candidate, CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => service.ApplyUpdatesAndRestart(new PackageUpdateCandidate("10.0.0", null)));
        Assert.False(backend.Applied);
    }

    [Fact]
    public async Task ManagedService_DelegatesCheckDownloadAndApply()
    {
        var backend = new FakeBackend();
        var service = new PackageUpdateService(backend);
        var candidate = await service.CheckForUpdatesAsync(CancellationToken.None);
        Assert.NotNull(candidate);
        await service.DownloadUpdatesAsync(candidate!, CancellationToken.None);
        service.ApplyUpdatesAndRestart(candidate!);
        Assert.True(backend.Downloaded);
        Assert.True(backend.Applied);
    }

    [Fact]
    public async Task SignedSource_RejectsMissingOrModifiedSignature()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = ManifestBytes();
        var signature = signer.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        using var source = new SignedManifestUpdateSource("https://updates.example.test/feed", "stable", signer.ExportSubjectPublicKeyInfoPem(),
            new FakeHandler(path => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(path.EndsWith(".sig", StringComparison.Ordinal) ? signature : bytes) }));
        Assert.Equal("stable", (await source.ReadAndVerifyManifestAsync()).Channel);

        using var badSource = new SignedManifestUpdateSource("https://updates.example.test/feed", "stable", signer.ExportSubjectPublicKeyInfoPem(),
            new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[64]) }));
        await Assert.ThrowsAsync<CryptographicException>(() => badSource.ReadAndVerifyManifestAsync());
    }

    [Fact]
    public async Task SignedSource_RejectsDuplicatePropertiesAndRedirects()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var duplicate = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"schemaVersion\":1,\"applicationId\":\"Structura.Connector.Desktop\",\"channel\":\"stable\",\"assets\":[]}");
        var duplicateSignature = signer.SignData(duplicate, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        using var duplicateSource = new SignedManifestUpdateSource("https://updates.example.test/feed", "stable", signer.ExportSubjectPublicKeyInfoPem(),
            new FakeHandler(path => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(path.EndsWith(".sig", StringComparison.Ordinal) ? duplicateSignature : duplicate) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => duplicateSource.ReadAndVerifyManifestAsync());

        using var redirectSource = new SignedManifestUpdateSource("https://updates.example.test/feed", "stable", signer.ExportSubjectPublicKeyInfoPem(),
            new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example.test/feed") } }));
        await Assert.ThrowsAsync<InvalidDataException>(() => redirectSource.ReadAndVerifyManifestAsync());
    }

    [Fact]
    public async Task SignedSource_RejectsCorruptDownloadedPackageBeforeMovingItIntoPlace()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Encoding.UTF8.GetBytes("modified package");
        var manifest = ManifestBytes(size: 7, hash: new string('0', 64));
        var signature = signer.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        using var source = new SignedManifestUpdateSource("https://updates.example.test/feed", "stable", signer.ExportSubjectPublicKeyInfoPem(),
            new FakeHandler(path => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(path.EndsWith(".sig", StringComparison.Ordinal) ? signature :
                    path.EndsWith(".nupkg", StringComparison.Ordinal) ? package : manifest)
            }));
        await source.ReadAndVerifyManifestAsync();
        var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".nupkg");
        var asset = new Velopack.VelopackAsset { FileName = "Structura.Connector.Desktop-1.0.0-full.nupkg", Version = Velopack.SemanticVersion.Parse("1.0.0"), Type = Velopack.VelopackAssetType.Full, Size = 7, SHA256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => source.DownloadReleaseEntry(null!, asset, output, _ => { }, CancellationToken.None));
        Assert.False(File.Exists(output));
    }

    private static byte[] ManifestBytes(long size = 7, string hash = "0000000000000000000000000000000000000000000000000000000000000000") =>
        Encoding.UTF8.GetBytes($"{{\"schemaVersion\":1,\"applicationId\":\"Structura.Connector.Desktop\",\"channel\":\"stable\",\"assets\":[{{\"fileName\":\"Structura.Connector.Desktop-1.0.0-full.nupkg\",\"version\":\"1.0.0\",\"type\":\"Full\",\"size\":{size},\"sha256\":\"{hash}\"}}]}}");

    private sealed class FakeHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request.RequestUri!.AbsolutePath));
    }

    private sealed class FakeBackend : IPackageUpdateBackend
    {
        private readonly PackageUpdateCandidate _candidate = new("9.9.9", "notes");
        public bool Downloaded { get; private set; }
        public bool Applied { get; private set; }
        public Task<PackageUpdateCandidate?> CheckForUpdatesAsync(CancellationToken cancellationToken) => Task.FromResult<PackageUpdateCandidate?>(_candidate);
        public Task DownloadUpdatesAsync(PackageUpdateCandidate candidate, CancellationToken cancellationToken) { Downloaded = true; return Task.CompletedTask; }
        public void ApplyUpdatesAndRestart(PackageUpdateCandidate candidate) => Applied = true;
    }
}
