using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Connector.Desktop.Services;

public sealed class TeklaStandardService
{
    private const int FileCopyBufferBytes = 1024 * 1024;
    private const int FileCopyMaxAttempts = 3;
    private readonly HttpClient _http;
    private readonly string _managedSyncRoot;

    public TeklaStandardService(HttpClient http)
    {
        _http = http;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectorAgentDesktop");
        Directory.CreateDirectory(root);
        LogFilePath = Path.Combine(root, "tekla-standard.log");
        _managedSyncRoot = Path.Combine(root, "managed-sync");
        Directory.CreateDirectory(_managedSyncRoot);
    }

    public string LogFilePath { get; }

    public string ResolveGitExecutable()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "tools", "git", "bin", "git.exe"),
            Path.Combine(baseDir, "tools", "git", "cmd", "git.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    public bool CheckGitAvailability(out string gitPath, out string details)
    {
        gitPath = ResolveGitExecutable();
        details = string.Empty;

        if (string.IsNullOrWhiteSpace(gitPath))
        {
            details = "Встроенный git не найден. Ожидается tools\\git\\bin\\git.exe или tools\\git\\cmd\\git.exe";
            return false;
        }

        var probeWorkDir = Path.GetTempPath();
        var ok = TryRunGit(gitPath, "--version", probeWorkDir, out var stdout, out var stderr);
        if (ok)
        {
            details = string.IsNullOrWhiteSpace(stdout) ? "git доступен" : stdout.Trim();
            return true;
        }

        details = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
        return false;
    }

    public bool IsTeklaRunning()
    {
        try
        {
            return Process.GetProcessesByName("TeklaStructures").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<TeklaStandardManifest?> TryGetManifestAsync(string manifestUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out _))
        {
            return null;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var revision =
            GetString(root, "revision") ??
            GetString(root, "target_revision") ??
            GetString(root, "targetRevision") ??
            GetString(root, "version");

        if (string.IsNullOrWhiteSpace(revision))
        {
            return null;
        }

        return new TeklaStandardManifest
        {
            Target = GetString(root, "target") ?? string.Empty,
            Version = GetString(root, "version")?.Trim() ?? string.Empty,
            Revision = revision.Trim(),
            Notes = GetString(root, "notes") ?? string.Empty,
            RepoUrl = GetString(root, "repo_url") ?? string.Empty,
            RepoRef =
                GetString(root, "repo_ref") ??
                GetString(root, "git_ref") ??
                GetString(root, "revision") ??
                string.Empty,
            RepoSubdir = GetString(root, "repo_subdir") ?? string.Empty,
            TargetPath = GetString(root, "target_path") ?? string.Empty
        };
    }

    public bool IsUpdateAvailable(string installedRevision, string targetRevision)
    {
        if (string.IsNullOrWhiteSpace(targetRevision))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(installedRevision))
        {
            return true;
        }

        return !string.Equals(
            installedRevision.Trim(),
            targetRevision.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    public TeklaApplyResult ApplyPendingGitUpdate(TeklaManagedSyncRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TargetRevision))
        {
            return TeklaApplyResult.Fail("Нет целевой ревизии для применения.");
        }

        if (string.IsNullOrWhiteSpace(request.LocalPath))
        {
            return TeklaApplyResult.Fail("Не задан локальный путь для синхронизации.");
        }

        if (string.IsNullOrWhiteSpace(request.RepoUrl))
        {
            return TeklaApplyResult.Fail("В manifest отсутствует repo_url для обновления через git.");
        }

        if (string.IsNullOrWhiteSpace(request.RepoRef))
        {
            return TeklaApplyResult.Fail("В manifest отсутствует repo_ref для обновления через git.");
        }

        var gitExe = ResolveGitExecutable();
        if (string.IsNullOrWhiteSpace(gitExe))
        {
            return TeklaApplyResult.Fail("Встроенный git не найден в каталоге приложения (tools\\git). Обновите Connector.");
        }

        if (!TryRunGit(gitExe, "--version", Path.GetTempPath(), out var versionOut, out var versionErr))
        {
            var reason = string.IsNullOrWhiteSpace(versionErr) ? versionOut : versionErr;
            return TeklaApplyResult.Fail("Git недоступен: " + reason);
        }

        try
        {
            Directory.CreateDirectory(request.LocalPath);
            var worktreePath = EnsureManagedWorktree(gitExe, request);
            var sourcePath = ResolveManagedSourcePath(worktreePath, request.RepoSubdir);
            SyncDirectoryContents(sourcePath, request.LocalPath, request.Mode == TeklaManagedSyncMode.Strict);

            var message = request.Mode == TeklaManagedSyncMode.Strict
                ? $"{request.DisplayName}: применена ревизия {request.TargetRevision}."
                : $"{request.DisplayName}: добавлены и обновлены управляемые файлы до ревизии {request.TargetRevision}.";
            return TeklaApplyResult.Success(message, request.TargetRevision.Trim());
        }
        catch (Exception ex)
        {
            return TeklaApplyResult.Fail("Не удалось применить обновление: " + ex.Message);
        }
    }

    public void AppendLog(string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
        File.AppendAllText(LogFilePath, line);
    }

    private string EnsureManagedWorktree(string gitExe, TeklaManagedSyncRequest request)
    {
        var normalizedKey = NormalizeTargetKey(request.TargetKey);
        var worktreePath = Path.Combine(_managedSyncRoot, normalizedKey);
        var gitDir = Path.Combine(worktreePath, ".git");
        var worktreeExists = Directory.Exists(worktreePath);

        if (worktreeExists && !Directory.Exists(gitDir))
        {
            Directory.Delete(worktreePath, recursive: true);
            worktreeExists = false;
        }

        if (worktreeExists && !RepositoryOriginMatches(gitExe, worktreePath, request.RepoUrl))
        {
            Directory.Delete(worktreePath, recursive: true);
            worktreeExists = false;
        }

        if (!worktreeExists)
        {
            var parent = Directory.GetParent(worktreePath)?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
            {
                throw new InvalidOperationException("Некорректный staging-путь для git sync.");
            }

            Directory.CreateDirectory(parent);
            if (!TryRunGit(gitExe, $"clone {QuoteArgument(request.RepoUrl)} {QuoteArgument(worktreePath)}", parent, out var cloneOut, out var cloneErr))
            {
                var reason = string.IsNullOrWhiteSpace(cloneErr) ? cloneOut : cloneErr;
                throw new InvalidOperationException("Не удалось клонировать репозиторий: " + reason);
            }
        }

        if (!TryRunGit(gitExe, $"fetch origin {QuoteArgument(request.RepoRef)} --depth 1", worktreePath, out var fetchOut, out var fetchErr))
        {
            var reason = string.IsNullOrWhiteSpace(fetchErr) ? fetchOut : fetchErr;
            throw new InvalidOperationException("Не удалось получить обновление: " + reason);
        }

        if (!TryRunGit(gitExe, "checkout -f FETCH_HEAD", worktreePath, out var checkoutOut, out var checkoutErr))
        {
            var reason = string.IsNullOrWhiteSpace(checkoutErr) ? checkoutOut : checkoutErr;
            throw new InvalidOperationException("Не удалось применить checkout: " + reason);
        }

        if (!TryRunGit(gitExe, "clean -fd", worktreePath, out var cleanOut, out var cleanErr))
        {
            var reason = string.IsNullOrWhiteSpace(cleanErr) ? cleanOut : cleanErr;
            throw new InvalidOperationException("Не удалось очистить staging-репозиторий: " + reason);
        }

        return worktreePath;
    }

    private static string ResolveManagedSourcePath(string worktreePath, string repoSubdir)
    {
        var sourcePath = string.IsNullOrWhiteSpace(repoSubdir)
            ? worktreePath
            : Path.Combine(worktreePath, repoSubdir.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException("В staging-репозитории не найден ожидаемый подпуть: " + sourcePath);
        }

        return sourcePath;
    }

    private static void SyncDirectoryContents(string sourcePath, string destinationPath, bool deleteExtraneous)
    {
        var sourceInfo = new DirectoryInfo(sourcePath);
        var destinationInfo = new DirectoryInfo(destinationPath);
        destinationInfo.Create();
        var context = new TeklaSyncCopyContext(sourcePath, destinationPath);
        SyncDirectoryContentsRecursive(sourceInfo, destinationInfo, deleteExtraneous, context);
    }

    private static void SyncDirectoryContentsRecursive(
        DirectoryInfo source,
        DirectoryInfo destination,
        bool deleteExtraneous,
        TeklaSyncCopyContext context)
    {
        destination.Create();

        var sourceEntries = source
            .EnumerateFileSystemInfos()
            .Where(info => !string.Equals(info.Name, ".git", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(info => info.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in sourceEntries.Values)
        {
            var targetPath = Path.Combine(destination.FullName, entry.Name);
            if (entry is DirectoryInfo sourceDirectory)
            {
                SyncDirectoryContentsRecursive(sourceDirectory, new DirectoryInfo(targetPath), deleteExtraneous, context);
            }
            else
            {
                Directory.CreateDirectory(destination.FullName);
                CopyManagedFile((FileInfo)entry, targetPath, context);
            }
        }

        if (!deleteExtraneous)
        {
            return;
        }

        foreach (var destinationEntry in destination.EnumerateFileSystemInfos())
        {
            if (sourceEntries.ContainsKey(destinationEntry.Name))
            {
                continue;
            }

            if (destinationEntry is DirectoryInfo destinationDirectory)
            {
                destinationDirectory.Delete(recursive: true);
            }
            else
            {
                destinationEntry.Delete();
            }
        }
    }

    private static void CopyManagedFile(FileInfo sourceFile, string targetPath, TeklaSyncCopyContext context)
    {
        var relativePath = Path.GetRelativePath(context.DestinationRoot, targetPath);

        for (var attempt = 1; attempt <= FileCopyMaxAttempts; attempt++)
        {
            var tempPath = targetPath + ".structura-sync-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var source = new FileStream(
                           sourceFile.FullName,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read,
                           FileCopyBufferBytes,
                           FileOptions.SequentialScan))
                using (var destination = new FileStream(
                           tempPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           FileCopyBufferBytes,
                           FileOptions.SequentialScan))
                {
                    source.CopyTo(destination, FileCopyBufferBytes);
                }

                File.SetLastWriteTimeUtc(tempPath, sourceFile.LastWriteTimeUtc);
                ReplaceManagedFile(tempPath, targetPath);
                return;
            }
            catch (IOException ex) when (attempt < FileCopyMaxAttempts && IsTransientFileCopyError(ex))
            {
                TryDeleteTempFile(tempPath);
                Thread.Sleep(TimeSpan.FromMilliseconds(250 * attempt));
            }
            catch (UnauthorizedAccessException) when (attempt < FileCopyMaxAttempts)
            {
                TryDeleteTempFile(tempPath);
                Thread.Sleep(TimeSpan.FromMilliseconds(250 * attempt));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDeleteTempFile(tempPath);
                throw new IOException(
                    $"Не удалось обновить файл '{relativePath}'. Возможно, он открыт в Tekla, Grasshopper или другой программе. Закройте файл и повторите синхронизацию.",
                    ex);
            }
            finally
            {
                TryDeleteTempFile(tempPath);
            }
        }
    }

    private static void ReplaceManagedFile(string tempPath, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            File.Copy(tempPath, targetPath, overwrite: true);
            return;
        }

        File.Move(tempPath, targetPath);
    }

    private static bool IsTransientFileCopyError(IOException ex)
    {
        var hresult = ex.HResult & 0xFFFF;
        return hresult is 32 or 33 or 80;
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Best-effort cleanup; the next sync can ignore/delete stale temp files if they are not locked.
        }
    }

    private static bool RepositoryOriginMatches(string gitExe, string worktreePath, string repoUrl)
    {
        if (!TryRunGit(gitExe, "remote get-url origin", worktreePath, out var stdout, out _))
        {
            return false;
        }

        return string.Equals(
            (stdout ?? string.Empty).Trim(),
            repoUrl.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTargetKey(string value)
    {
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            .ToArray();
        return chars.Length == 0 ? "firm" : new string(chars);
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }

            if (property.Value.ValueKind == JsonValueKind.Number)
            {
                return property.Value.GetRawText();
            }
        }

        return null;
    }

    private static bool TryRunGit(string gitExe, string arguments, string workDir, out string stdout, out string stderr)
    {
        stdout = string.Empty;
        stderr = string.Empty;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = gitExe,
                Arguments = arguments,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                stderr = "Не удалось запустить процесс git.";
                return false;
            }

            stdout = process.StandardOutput.ReadToEnd();
            stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            stderr = ex.Message;
            return false;
        }
    }
}

public sealed class TeklaStandardManifest
{
    public string Target { get; set; } = "";
    public string Version { get; set; } = "";
    public string Revision { get; set; } = "";
    public string Notes { get; set; } = "";
    public string RepoUrl { get; set; } = "";
    public string RepoRef { get; set; } = "";
    public string RepoSubdir { get; set; } = "";
    public string TargetPath { get; set; } = "";
}

public enum TeklaManagedSyncMode
{
    Strict,
    OverlayNoDelete
}

public sealed class TeklaSyncCopyContext
{
    public TeklaSyncCopyContext(string sourceRoot, string destinationRoot)
    {
        DestinationRoot = Path.GetFullPath(destinationRoot);
    }

    public string DestinationRoot { get; }
}

public sealed class TeklaManagedSyncRequest
{
    public string TargetKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string RepoUrl { get; set; } = "";
    public string RepoRef { get; set; } = "";
    public string RepoSubdir { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string TargetRevision { get; set; } = "";
    public TeklaManagedSyncMode Mode { get; set; }
}

public sealed class TeklaApplyResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = "";
    public string InstalledRevision { get; init; } = "";

    public static TeklaApplyResult Success(string message, string installedRevision) => new()
    {
        IsSuccess = true,
        Message = message,
        InstalledRevision = installedRevision
    };

    public static TeklaApplyResult Fail(string message) => new()
    {
        IsSuccess = false,
        Message = message
    };
}
