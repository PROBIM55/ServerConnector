using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Platform.Connector.Compute.Agr;

namespace Platform.Connector.Compute.Agr.Tests;

public sealed class AgrBlenderRuntimeInstallerTests
{
    [Theory]
    [InlineData("5.2.0")]
    [InlineData("5.2.0 LTS")]
    [InlineData("  5.2.0 LTS  ")]
    public void CompatibleBlenderVersion_AcceptsExactRuntimeWithDisplaySuffix(string actualVersion)
    {
        Assert.True(AgrManagedRuntime.IsCompatibleBlenderVersion(actualVersion));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("5.2")]
    [InlineData("5.2.1")]
    [InlineData("5.3.0 LTS")]
    [InlineData("not-a-version")]
    public void CompatibleBlenderVersion_RejectsDifferentOrInvalidRuntime(string? actualVersion)
    {
        Assert.False(AgrManagedRuntime.IsCompatibleBlenderVersion(actualVersion));
    }

    [Fact]
    public void PinnedManifest_LoadsOfficialBlender52Archive()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "agr-blender",
            "blender-runtime-5.2.0-win-x64.json");

        var manifest = AgrBlenderRuntimeManifest.Load(path);

        Assert.Equal("5.2.0", manifest.Version);
        Assert.Equal(404954661, manifest.ArchiveSizeBytes);
        Assert.Equal(
            "2d184b626c001692c362291911293b6a297179d618d95e9e9192c3a80318adc4",
            manifest.ArchiveSha256);
        Assert.Equal("download.blender.org", manifest.ArchiveUrl.Host);
    }

    [Fact]
    public async Task EnsureInstalledAsync_VerifiesAndAtomicallyActivatesArchive()
    {
        var archive = CreateRuntimeArchive("blender-5.2.0-windows-x64/blender.exe");
        var manifest = CreateManifest(archive);
        var root = NewTempRoot();
        using var httpClient = new HttpClient(new StaticResponseHandler(archive));
        var installer = new AgrBlenderRuntimeInstaller(httpClient, root);

        try
        {
            var first = await installer.EnsureInstalledAsync(manifest, null, CancellationToken.None);
            var second = await installer.EnsureInstalledAsync(manifest, null, CancellationToken.None);

            Assert.True(first.Installed);
            Assert.False(second.Installed);
            Assert.True(File.Exists(first.BlenderExecutablePath));
            Assert.Equal(first.BlenderExecutablePath, second.BlenderExecutablePath);
            Assert.Empty(Directory.EnumerateDirectories(root, ".install-*"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureInstalledAsync_RejectsArchiveTraversal()
    {
        var archive = CreateRuntimeArchive("../outside.exe");
        var manifest = CreateManifest(archive);
        var root = NewTempRoot();
        using var httpClient = new HttpClient(new StaticResponseHandler(archive));
        var installer = new AgrBlenderRuntimeInstaller(httpClient, root);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => installer.EnsureInstalledAsync(manifest, null, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(root, "outside.exe")));
            Assert.Empty(Directory.EnumerateDirectories(root, ".install-*"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureInstalledAsync_WhenPreflightFails_DoesNotActivateRuntime()
    {
        var archive = CreateRuntimeArchive("blender-5.2.0-windows-x64/blender.exe");
        var manifest = CreateManifest(archive);
        var root = NewTempRoot();
        using var httpClient = new HttpClient(new StaticResponseHandler(archive));
        var installer = new AgrBlenderRuntimeInstaller(httpClient, root);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => installer.EnsureInstalledAsync(
                    manifest,
                    progress: null,
                    CancellationToken.None,
                    (_, _) => throw new InvalidDataException("preflight rejected test runtime")));

            Assert.Contains("preflight rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(root, manifest.Version)));
            Assert.Empty(Directory.EnumerateDirectories(root, ".install-*"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static AgrBlenderRuntimeManifest CreateManifest(byte[] archive)
        => new(
            1,
            "blender/windows-x64/5.2.0",
            "5.2.0",
            "windows-x64",
            new Uri("https://download.blender.org/release/Blender5.2/test.zip"),
            "test.zip",
            archive.LongLength,
            Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(),
            "blender-5.2.0-windows-x64/blender.exe",
            AgrComputeOptions.WorkerProtocolVersion);

    private static byte[] CreateRuntimeArchive(string entryName)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write([0x4d, 0x5a]);
        }
        return buffer.ToArray();
    }

    private static string NewTempRoot()
        => Path.Combine(Path.GetTempPath(), "platform-agr-installer-tests", Guid.NewGuid().ToString("N"));

    private sealed class StaticResponseHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentLength = body.LongLength;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
