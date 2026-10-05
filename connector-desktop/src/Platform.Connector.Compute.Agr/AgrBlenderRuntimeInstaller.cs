using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Platform.Connector.Compute.Agr;

public sealed class AgrBlenderRuntimeInstaller
{
    private const string ActivationMarkerName = ".platform-runtime.json";
    private readonly HttpClient _httpClient;
    private readonly string _runtimeRoot;

    public AgrBlenderRuntimeInstaller(HttpClient httpClient, string? runtimeRoot = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _runtimeRoot = Path.GetFullPath(runtimeRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Platform",
            "Runtimes",
            "Blender"));
    }

    public async Task<AgrRuntimeInstallResult> EnsureInstalledAsync(
        AgrBlenderRuntimeManifest manifest,
        IProgress<AgrRuntimeInstallProgress>? progress,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? validateRuntime = null)
    {
        manifest.Validate();
        Directory.CreateDirectory(_runtimeRoot);
        await using var installLock = await AcquireInstallLockAsync(manifest.Version, cancellationToken);

        var finalRoot = ResolveInside(_runtimeRoot, manifest.Version);
        var executablePath = ResolveInside(finalRoot, manifest.ExecutableRelativePath);
        var activationMarker = Path.Combine(finalRoot, ActivationMarkerName);
        if (File.Exists(executablePath) && IsActivatedManifest(activationMarker, manifest))
        {
            return new AgrRuntimeInstallResult(false, finalRoot, executablePath);
        }
        if (Directory.Exists(finalRoot))
        {
            throw new InvalidOperationException(
                $"Blender runtime directory exists but is not a valid activated {manifest.Version} runtime: {finalRoot}");
        }

        var stagingRoot = ResolveInside(_runtimeRoot, $".install-{manifest.Version}-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(stagingRoot, manifest.ArchiveFileName);
        var extractedRoot = Path.Combine(stagingRoot, "extracted");
        try
        {
            Directory.CreateDirectory(stagingRoot);
            progress?.Report(new("download", 0, manifest.ArchiveSizeBytes));
            await DownloadAndVerifyAsync(manifest, archivePath, progress, cancellationToken);

            progress?.Report(new("extract", 0, null));
            Directory.CreateDirectory(extractedRoot);
            ExtractSafely(archivePath, extractedRoot, cancellationToken);
            var stagedExecutable = ResolveInside(extractedRoot, manifest.ExecutableRelativePath);
            if (!File.Exists(stagedExecutable))
            {
                throw new InvalidDataException(
                    $"Blender archive does not contain {manifest.ExecutableRelativePath}.");
            }

            if (validateRuntime is not null)
            {
                progress?.Report(new("preflight", manifest.ArchiveSizeBytes, manifest.ArchiveSizeBytes));
                await validateRuntime(stagedExecutable, cancellationToken);
            }

            await File.WriteAllTextAsync(
                Path.Combine(extractedRoot, ActivationMarkerName),
                JsonSerializer.Serialize(new
                {
                    runtimeId = manifest.RuntimeId,
                    version = manifest.Version,
                    platform = manifest.Platform,
                    archiveSha256 = manifest.ArchiveSha256,
                    workerProtocolVersion = manifest.WorkerProtocolVersion,
                    activatedUtc = DateTimeOffset.UtcNow,
                }),
                cancellationToken);

            Directory.Move(extractedRoot, finalRoot);
            progress?.Report(new("ready", manifest.ArchiveSizeBytes, manifest.ArchiveSizeBytes));
            return new AgrRuntimeInstallResult(true, finalRoot, executablePath);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
            }
            catch (IOException)
            {
                // Cleanup is best-effort; an antivirus may briefly hold an extracted file.
            }
            catch (UnauthorizedAccessException)
            {
                // The inactive staging directory can be collected on the next maintenance pass.
            }
        }
    }

    private async Task<FileStream> AcquireInstallLockAsync(string version, CancellationToken cancellationToken)
    {
        var lockPath = ResolveInside(_runtimeRoot, $".install-{version}.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
    }

    private async Task DownloadAndVerifyAsync(
        AgrBlenderRuntimeManifest manifest,
        string archivePath,
        IProgress<AgrRuntimeInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            manifest.ArchiveUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } advertisedLength
            && advertisedLength != manifest.ArchiveSizeBytes)
        {
            throw new InvalidDataException(
                $"Blender archive length mismatch: expected {manifest.ArchiveSizeBytes}, got {advertisedLength}.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > manifest.ArchiveSizeBytes)
            {
                throw new InvalidDataException("Blender archive exceeds the pinned size.");
            }
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report(new("download", total, manifest.ArchiveSizeBytes));
        }
        await target.FlushAsync(cancellationToken);

        if (total != manifest.ArchiveSizeBytes)
        {
            throw new InvalidDataException(
                $"Blender archive length mismatch: expected {manifest.ArchiveSizeBytes}, got {total}.");
        }
        var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actualHash, manifest.ArchiveSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Blender archive SHA-256 mismatch: expected {manifest.ArchiveSha256}, got {actualHash}.");
        }
    }

    private static void ExtractSafely(string archivePath, string destinationRoot, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(destinationRoot);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsafe path in Blender archive: {entry.FullName}");
            }
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static bool IsActivatedManifest(string markerPath, AgrBlenderRuntimeManifest expected)
    {
        try
        {
            using var stream = File.OpenRead(markerPath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            return root.GetProperty("runtimeId").GetString() == expected.RuntimeId
                && root.GetProperty("version").GetString() == expected.Version
                && root.GetProperty("archiveSha256").GetString() == expected.ArchiveSha256
                && root.GetProperty("workerProtocolVersion").GetString() == expected.WorkerProtocolVersion;
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static string ResolveInside(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("Runtime path must be relative.");
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Runtime path escapes the managed runtime root.");
        }
        return candidate;
    }
}

public sealed record AgrRuntimeInstallProgress(string Phase, long CompletedBytes, long? TotalBytes);

public sealed record AgrRuntimeInstallResult(bool Installed, string RuntimeRoot, string BlenderExecutablePath);
