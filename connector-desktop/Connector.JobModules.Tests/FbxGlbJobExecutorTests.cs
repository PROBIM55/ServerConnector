using System.Security.AccessControl;
using System.Security.Principal;
using System.IO.Compression;
using System.Text.Json;
using Connector.JobModules;
using Platform.Connector.Core;

namespace Connector.JobModules.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresPinnedGltfpackAttribute : FactAttribute
{
    public RequiresPinnedGltfpackAttribute()
    {
        var path = Environment.GetEnvironmentVariable("AGR_GLTFPACK");
        if ((string.IsNullOrWhiteSpace(path) || !File.Exists(path)) &&
            !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            Skip = "AGR_GLTFPACK must point to the pinned gltfpack executable for this integration test.";
    }
}

public sealed class FbxGlbJobExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".connector-jobmodules-test-data-" + Guid.NewGuid().ToString("N"));
    private readonly string _workRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".connector-jobmodules-test-work-" + Guid.NewGuid().ToString("N"));

    [RequiresPinnedGltfpack]
    public async Task ExecuteAsync_UsesShortPrivateWorkRootAndPublishesInLongOutputDirectory()
    {
        var gltfpack = Environment.GetEnvironmentVariable("AGR_GLTFPACK");
        Assert.False(string.IsNullOrWhiteSpace(gltfpack));
        Assert.True(File.Exists(gltfpack));
        Directory.CreateDirectory(_root);
        var input = FindFixture();
        var output = _root;
        while (Path.Combine(output, new string('o', 45)).Length < 275)
            output = Path.Combine(output, new string('o', 45));
        Directory.CreateDirectory(output);
        var work = _workRoot + Path.DirectorySeparatorChar;
        Assert.True(Path.Combine(work, "job-" + new string('a', 32), "agr-conv-000000000000", "gltfpack-temp").Length < 240);

        var result = await new FbxGlbJobExecutor(gltfpack, work).ExecuteAsync(CreateJob(input, output), null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode ?? result.Message);
        var published = Assert.Single(Directory.EnumerateDirectories(output, "fbx-result-*"));
        var archive = Assert.Single(Directory.EnumerateFiles(published, "*.glb.zip"));
        Assert.True(new FileInfo(archive).Length > 0);
        Assert.True(archive.Length > 260, "The published archive should retain the caller's long output path.");
        var manifestFile = Assert.Single(Directory.EnumerateFiles(published, "*.manifest.json"));
        using (var zip = ZipFile.OpenRead(archive))
        {
            var names = zip.Entries.Select(entry => entry.FullName).ToArray();
            Assert.Contains("manifest.json", names);
            var glbEntries = zip.Entries.Where(entry => entry.FullName.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.NotEmpty(glbEntries);
            Assert.All(glbEntries, entry => Assert.True(entry.Length > 0, $"Empty GLB entry: {entry.FullName}"));
            var entry = zip.GetEntry("manifest.json")!;
            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream);
            Assert.Equal("agr:SM_TestPart_001", document.RootElement.GetProperty("parts")[0].GetProperty("code").GetString());
        }
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestFile)))
            Assert.Equal("agr:SM_TestPart_001", manifest.RootElement.GetProperty("parts")[0].GetProperty("code").GetString());
        Assert.True(result.Result!.Value.TryGetProperty("agrResult", out var agr));
        Assert.True(agr.TryGetProperty("Levels", out var levels));
        Assert.True(levels.GetArrayLength() >= 1);
        var report = agr.GetProperty("PartReportJson").GetString();
        Assert.False(string.IsNullOrWhiteSpace(report));
        using (var reportDocument = JsonDocument.Parse(report!))
        {
            Assert.Equal("SM_TestPart_001", reportDocument.RootElement.GetProperty("part").GetString());
            Assert.True(reportDocument.RootElement.GetProperty("triangles").GetProperty("model").GetInt64() > 0);
        }
        Assert.Empty(Directory.EnumerateDirectories(work, "job-*"));
        Assert.Empty(Directory.EnumerateDirectories(output, ".fbx-attempt-*"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenCanceledBeforeConverterStarts_CleansOnlyItsWorkspaceAndStaging()
    {
        Directory.CreateDirectory(_root);
        var work = _workRoot;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FbxGlbJobExecutor("unused", work).ExecuteAsync(CreateJob(_root, Path.Combine(_root, "out")), null, canceled.Token));

        Assert.Empty(Directory.EnumerateDirectories(work, "job-*"));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "out"), ".fbx-attempt-*"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenConverterFails_CleansItsWorkspaceWithoutPublishing()
    {
        Directory.CreateDirectory(_root);
        var input = Path.Combine(_root, "invalid.zip");
        await File.WriteAllTextAsync(input, "not an archive");
        var output = Path.Combine(_root, "out");
        var work = _workRoot;

        var result = await new FbxGlbJobExecutor("missing-gltfpack.exe", work).ExecuteAsync(CreateJob(input, output), null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("CONVERTER_FBX_FAILED", result.ErrorCode);
        Assert.Empty(Directory.EnumerateDirectories(work, "job-*"));
        Assert.Empty(Directory.EnumerateDirectories(output, ".fbx-attempt-*"));
        Assert.Empty(Directory.EnumerateDirectories(output, "fbx-result-*"));
    }

    [Fact]
    public async Task Workspace_RejectsAnUntrustedWriterOnExistingRoot()
    {
        Directory.CreateDirectory(_root);
        var root = _workRoot;
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new FbxGlbJobExecutor("unused", root).ExecuteAsync(
                    CreateJob(_root, Path.Combine(_root, "bootstrap-out")), null, cancellation.Token));
        }
        Assert.True(Directory.Exists(root));
        Assert.Empty(Directory.EnumerateDirectories(root, "job-*"));
        var acl = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(acl);

        var result = await new FbxGlbJobExecutor("unused", root).ExecuteAsync(
            CreateJob(_root, Path.Combine(_root, "out")), null, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("CONVERTER_FBX_FAILED", result.ErrorCode);
        Assert.Empty(Directory.EnumerateDirectories(root, "job-*"));
    }

    private static ConnectorJobEnvelope CreateJob(string input, string output) => new(1, Guid.NewGuid().ToString("N"), "converter",
        CadProvider.AutoCad, JobOperation.Export, DateTime.UtcNow,
        JsonSerializer.SerializeToElement(new { inputPath = input, outputDirectory = output, profile = "exact" }),
        ExecutorId: FbxGlbJobExecutor.Id);

    private static string FindFixture()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var fixture = Path.Combine(current.FullName, "Connector.AgrConversion.Tests", "Fixtures", "SM_TestPart_001");
            if (Directory.Exists(fixture)) return fixture;
        }
        throw new DirectoryNotFoundException("AGR test fixture is unavailable.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_workRoot)) Directory.Delete(_workRoot, recursive: true);
    }
}
