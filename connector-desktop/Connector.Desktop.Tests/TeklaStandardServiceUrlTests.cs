using System.Net;
using Connector.Desktop.Services;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class TeklaStandardServiceUrlTests
{
    [Theory]
    [InlineData("https://server.structura-most.ru/updates/tekla/firm/latest.json")]
    [InlineData("https://server.structura-most.ru/updates/tekla/extensions/latest.json")]
    [InlineData("https://SERVER.structura-most.ru/updates/tekla/libraries/latest.json")]
    public void PublicManifestUrlIsAllowed(string url) =>
        Assert.True(TeklaStandardService.IsAllowedManifestUrl(url));

    [Theory]
    [InlineData("http://server.structura-most.ru/updates/tekla/firm/latest.json")]
    [InlineData("https://user:secret@server.structura-most.ru/updates/tekla/firm/latest.json")]
    [InlineData("https://token@server.structura-most.ru/updates/tekla/firm/latest.json")]
    [InlineData("https://server.structura-most.ru:8443/updates/tekla/firm/latest.json")]
    [InlineData("https://evil.example/updates/tekla/firm/latest.json")]
    [InlineData("https://server.structura-most.ru.evil.example/latest.json")]
    [InlineData("https://server.structura-most.ru/updates/latest.json")]
    [InlineData("https://server.structura-most.ru/updates/tekla/firm/latest.json?x=1")]
    [InlineData("https://ｓｅｒｖｅｒ.structura-most.ru/updates/tekla/firm/latest.json")]
    [InlineData("file:///C:/manifest.json")]
    [InlineData("updates/tekla/firm/latest.json")]
    [InlineData("")]
    [InlineData(null)]
    public void ManifestUrlOutsidePublicHttpsIsRejected(string? url) =>
        Assert.False(TeklaStandardService.IsAllowedManifestUrl(url));

    [Theory]
    [InlineData("https://github.com/PROBIM55/tekla-firm-standard.git")]
    [InlineData("https://GitHub.com/probim55/tekla-firm-standard.git")]
    public void PinnedRepoUrlIsAllowedAndNormalized(string url) =>
        Assert.Equal(new Uri(url).AbsoluteUri, TeklaStandardService.NormalizeAllowedRepoUrl(url));

    [Theory]
    [InlineData("http://github.com/PROBIM55/tekla-firm-standard.git")]
    [InlineData("https://x-access-token:secret@github.com/PROBIM55/tekla-firm-standard.git")]
    [InlineData("git@github.com:PROBIM55/tekla-firm-standard.git")]
    [InlineData("ssh://git@github.com/PROBIM55/tekla-firm-standard.git")]
    [InlineData("https://gitlab.com/PROBIM55/tekla-firm-standard.git")]
    [InlineData("https://github.com/other/tekla-firm-standard.git")]
    [InlineData("https://github.com/PROBIM55/tekla-firm-standard.git/../evil.git")]
    [InlineData("https://github.com:443@evil.example/PROBIM55/tekla-firm-standard.git")]
    [InlineData("https://ｇｉｔｈｕｂ.com/PROBIM55/tekla-firm-standard.git")]
    [InlineData("https://github.com/PROBIM55/tekla-firm-standard.git?x=1")]
    [InlineData("file:///C:/repo")]
    [InlineData("C:\\repo")]
    public void RepoUrlOutsidePublicHttpsIsRejected(string url) =>
        Assert.False(TeklaStandardService.IsAllowedRepoUrl(url));

    [Theory]
    [InlineData("http://server.structura-most.ru/updates/tekla/firm/latest.json")]
    [InlineData("https://user:secret@server.structura-most.ru/updates/tekla/firm/latest.json")]
    public async Task RejectedManifestUrlSendsNoRequest(string url)
    {
        var handler = new RecordingHandler();
        var service = new TeklaStandardService(new HttpClient(handler), NewStateRoot());

        Assert.Null(await service.TryGetManifestAsync(url, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ManifestRedirectedToAnotherHostIsIgnored()
    {
        var handler = new RecordingHandler { FinalUri = new Uri("https://evil.example/latest.json") };
        var service = new TeklaStandardService(new HttpClient(handler), NewStateRoot());

        Assert.Null(await service.TryGetManifestAsync(TeklaStandardService.FirmManifestUrl, CancellationToken.None));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("r1")]
    [InlineData("release/2026.10")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    public void RepoRefInAllowlistIsAccepted(string value) =>
        Assert.True(TeklaStandardService.IsAllowedRepoRef(value));

    [Theory]
    [InlineData("--upload-pack=calc.exe")]
    [InlineData("-c")]
    [InlineData("main..evil")]
    [InlineData("/main")]
    [InlineData("main evil")]
    [InlineData("main;evil")]
    [InlineData("")]
    [InlineData(null)]
    public void RepoRefOutsideAllowlistIsRejected(string? value) =>
        Assert.False(TeklaStandardService.IsAllowedRepoRef(value));

    [Theory]
    [InlineData("")]
    [InlineData("XS_FIRM")]
    [InlineData("Extensions/Sub folder")]
    public void SafeRepoSubdirIsAccepted(string value) =>
        Assert.True(TeklaStandardService.IsSafeRepoSubdir(value));

    [Theory]
    [InlineData("..")]
    [InlineData("XS_FIRM/../..")]
    [InlineData(@"..\outside")]
    [InlineData(@"C:\Company")]
    [InlineData("C:Company")]
    [InlineData(@"\\server\share")]
    [InlineData("/etc")]
    [InlineData(".git")]
    [InlineData("XS_FIRM/.GIT/hooks")]
    [InlineData("XS_FIRM:stream")]
    public void UnsafeRepoSubdirIsRejected(string value)
    {
        Assert.False(TeklaStandardService.IsSafeRepoSubdir(value));
        Assert.Throws<InvalidOperationException>(() =>
            TeklaStandardService.ResolveManagedSourcePath(Path.Combine(NewStateRoot(), "firm"), value));
    }

    [Fact]
    public void RepoSubdirResolvesInsideClone()
    {
        var clone = Path.Combine(NewStateRoot(), "firm");
        Directory.CreateDirectory(Path.Combine(clone, "XS_FIRM"));

        Assert.Equal(Path.Combine(clone, "XS_FIRM"), TeklaStandardService.ResolveManagedSourcePath(clone, "XS_FIRM"));
    }

    [Fact]
    public void GitNeverUsesStoredCredentialsOrPrompts()
    {
        var startInfo = TeklaStandardService.CreateGitStartInfo(
            "git.exe", new[] { "fetch", "--end-of-options", "origin", "main;calc" }, Path.GetTempPath());

        Assert.Equal(
            new[] { "-c", "credential.helper=", "-c", "credential.interactive=never", "fetch", "--end-of-options", "origin", "main;calc" },
            startInfo.ArgumentList);
        Assert.True(string.IsNullOrEmpty(startInfo.Arguments));
        Assert.Equal("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("never", startInfo.Environment["GCM_INTERACTIVE"]);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public async Task PublicManifestIsReadAnonymously()
    {
        var handler = new RecordingHandler();
        var service = new TeklaStandardService(new HttpClient(handler), NewStateRoot());

        var manifest = await service.TryGetManifestAsync(
            "https://server.structura-most.ru/updates/tekla/firm/latest.json", CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal("abc123", manifest!.Revision);
        Assert.Equal(1, handler.Calls);
        Assert.Null(handler.LastAuthorization);
    }

    [Fact]
    public void RepoUrlWithCredentialsFailsBeforeGit()
    {
        var service = new TeklaStandardService(new HttpClient(new RecordingHandler()), NewStateRoot());

        var result = service.ApplyPendingGitUpdate(new TeklaManagedSyncRequest
        {
            TargetKey = "firm",
            DisplayName = "Папка фирмы Tekla",
            RepoUrl = "https://x-access-token:secret@github.com/PROBIM55/tekla-firm-standard.git",
            RepoRef = "main",
            LocalPath = Path.Combine(NewStateRoot(), "target"),
            TargetRevision = "abc123"
        });

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("secret", result.Message + result.TechnicalDetails);
    }

    private static string NewStateRoot() =>
        Path.Combine(Path.GetTempPath(), "tekla-standard-url-tests", Guid.NewGuid().ToString("N"));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastAuthorization { get; private set; }
        public Uri? FinalUri { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = FinalUri is null ? request : new HttpRequestMessage(HttpMethod.Get, FinalUri),
                Content = new StringContent("{\"version\":\"1.0\",\"revision\":\"abc123\",\"repo_url\":\"https://github.com/PROBIM55/tekla-firm-standard.git\",\"repo_ref\":\"main\"}")
            });
        }
    }
}
