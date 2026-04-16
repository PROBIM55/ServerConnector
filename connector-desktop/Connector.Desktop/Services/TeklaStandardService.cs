using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace Connector.Desktop.Services;

public sealed class TeklaStandardService
{
    private const int FileCopyBufferBytes = 1024 * 1024;
    private const int FileCopyMaxAttempts = 3;
    private const string GitBundleFileName = "git-bundle.zip";
    private static readonly object GitBootstrapGate = new();
    private readonly HttpClient _http;
    private readonly string _bundledGitRoot;
    private readonly string _managedSyncRoot;

    public TeklaStandardService(HttpClient http)
    {
        _http = http;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectorAgentDesktop");
        Directory.CreateDirectory(root);
        LogFilePath = Path.Combine(root, "tekla-standard.log");
        _bundledGitRoot = Path.Combine(root, "bundled-tools", "git");
        _managedSyncRoot = Path.Combine(root, "managed-sync");
        Directory.CreateDirectory(_managedSyncRoot);
    }

    public string LogFilePath { get; }

    public string ResolveGitExecutable()
    {
        foreach (var candidate in EnumerateGitExecutableCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        TryEnsureBundledGitExtracted();

        foreach (var candidate in EnumerateGitExecutableCandidates())
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
            details = "Встроенный git не найден. Проверены папка приложения и локальный кэш восстановления. " +
                      "Ожидается архив " + Path.Combine(AppContext.BaseDirectory, GitBundleFileName);
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

    public bool IsRhinoRunning()
    {
        try
        {
            return Process.GetProcessesByName("Rhino").Length > 0 ||
                   Process.GetProcessesByName("Rhino7").Length > 0 ||
                   Process.GetProcessesByName("Rhino8").Length > 0 ||
                   Process.GetProcessesByName("RhinoWIP").Length > 0;
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

        if (!CheckGitAvailability(out var gitExe, out var gitDetails))
        {
            return TeklaApplyResult.Fail(
                "Не удалось подготовить встроенный git для синхронизации. Переустановите Connector.",
                gitDetails);
        }

        if (!TryRunGit(gitExe, "--version", Path.GetTempPath(), out var versionOut, out var versionErr))
        {
            var reason = string.IsNullOrWhiteSpace(versionErr) ? versionOut : versionErr;
            return TeklaApplyResult.Fail("Git недоступен: " + reason);
        }

        AppendLog($"{request.DisplayName}: начинаем применение ревизии {request.TargetRevision} в '{request.LocalPath}'.");

        try
        {
            Directory.CreateDirectory(request.LocalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var details = BuildTechnicalFailureDetails(request, "prepare-local-path", ex);
            AppendLog(details);
            return TeklaApplyResult.Fail(BuildFileAccessErrorMessage(request, ex), details);
        }
        catch (Exception ex)
        {
            var details = BuildTechnicalFailureDetails(request, "prepare-local-path", ex);
            AppendLog(details);
            return TeklaApplyResult.Fail("Не удалось подготовить локальную папку для синхронизации.", details);
        }

        string worktreePath;
        try
        {
            worktreePath = EnsureManagedWorktree(gitExe, request);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var details = BuildTechnicalFailureDetails(request, "prepare-staging", ex);
            AppendLog(details);
            return TeklaApplyResult.Fail(BuildFileAccessErrorMessage(request, ex), details);
        }
        catch (Exception ex)
        {
            var details = BuildTechnicalFailureDetails(request, "prepare-staging", ex);
            AppendLog(details);
            return TeklaApplyResult.Fail("Не удалось подготовить локальные данные синхронизации.", details);
        }

        string sourcePath;
        try
        {
            sourcePath = ResolveManagedSourcePath(worktreePath, request.RepoSubdir);
        }
        catch (Exception ex)
        {
            var details = BuildTechnicalFailureDetails(request, "resolve-source", ex);
            AppendLog(details);
            return TeklaApplyResult.Fail("Не удалось найти ожидаемые файлы обновления в локальном кэше.", details);
        }

        try
        {
            SyncDirectoryContents(sourcePath, request.LocalPath, request.Mode == TeklaManagedSyncMode.Strict);
            var message = request.Mode == TeklaManagedSyncMode.Strict
                ? $"{request.DisplayName}: применена ревизия {request.TargetRevision}."
                : $"{request.DisplayName}: добавлены и обновлены управляемые файлы до ревизии {request.TargetRevision}.";
            return TeklaApplyResult.Success(message, request.TargetRevision.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var details = BuildTechnicalFailureDetails(request, "sync-files", ex);
            AppendLog(details);
            return TeklaApplyResult.Fail(BuildFileAccessErrorMessage(request, ex), details);
        }
        catch (Exception ex)
        {
            var details = BuildTechnicalFailureDetails(request, "sync-files", ex);
            AppendLog(details);
            return TeklaApplyResult.Fail("Не удалось применить обновление на локальный компьютер.", details);
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
            DeleteDirectorySafely(worktreePath);
            worktreeExists = false;
        }

        if (worktreeExists && !RepositoryLooksHealthy(gitExe, worktreePath))
        {
            DeleteDirectorySafely(worktreePath);
            worktreeExists = false;
        }

        if (worktreeExists && !RepositoryOriginMatches(gitExe, worktreePath, request.RepoUrl))
        {
            DeleteDirectorySafely(worktreePath);
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
                if (Directory.Exists(worktreePath))
                {
                    DeleteDirectorySafely(worktreePath);
                }
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

            DeleteExtraneousEntry(destinationEntry);
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
                throw BuildManagedFileAccessException(relativePath, targetPath, ex);
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
            EnsureWritable(new FileInfo(targetPath));
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

    private static void DeleteExtraneousEntry(FileSystemInfo entry)
    {
        if (entry is DirectoryInfo directory)
        {
            ClearReadOnlyAttributesRecursive(directory);
            directory.Delete(recursive: true);
            return;
        }

        EnsureWritable(entry);
        entry.Delete();
    }

    private static void ClearReadOnlyAttributesRecursive(DirectoryInfo root)
    {
        foreach (var child in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            EnsureWritable(child);
        }

        EnsureWritable(root);
    }

    private static void EnsureWritable(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReadOnly) == 0)
        {
            return;
        }

        info.Attributes &= ~FileAttributes.ReadOnly;
    }

    private string BuildFileAccessErrorMessage(TeklaManagedSyncRequest request, Exception? exception = null)
    {
        if (exception is ManagedSyncFileAccessException fileAccessException)
        {
            var processPart = fileAccessException.LikelyBlockingProcesses.Count > 0
                ? " Вероятно, файл удерживает процесс: " + string.Join(", ", fileAccessException.LikelyBlockingProcesses) + "."
                : " Вероятно, файл удерживает другая запущенная программа.";

            return request.DisplayName +
                   ": не удалось обновить файл '" + fileAccessException.RelativePath + "'." +
                   Environment.NewLine +
                   "Путь: " + fileAccessException.FullPath + "." +
                   processPart +
                   " Закройте блокирующую программу и повторите синхронизацию.";
        }

        var teklaRunning = IsTeklaRunning();
        var rhinoRunning = IsRhinoRunning();
        var reason = request.TargetKey.Trim().ToLowerInvariant() switch
        {
            "libraries" when rhinoRunning =>
                "Вероятная причина: сейчас запущен Rhino, и он может удерживать файлы в Libraries.",
            "libraries" when teklaRunning && rhinoRunning =>
                "Вероятная причина: сейчас запущены Tekla и Rhino, один из них может удерживать файлы в Libraries.",
            "extensions" when teklaRunning && rhinoRunning =>
                "Вероятная причина: сейчас запущены Tekla и Rhino, один из них может удерживать файлы в Extensions.",
            "extensions" when teklaRunning =>
                "Вероятная причина: сейчас запущена Tekla, и она может удерживать файлы в Extensions.",
            "extensions" when rhinoRunning =>
                "Вероятная причина: сейчас запущен Rhino, и он может удерживать файлы в Extensions.",
            "firm" when teklaRunning && rhinoRunning =>
                "Вероятная причина: сейчас запущены Tekla и Rhino, либо один из файлов открыт вручную для редактирования.",
            "firm" when teklaRunning =>
                "Вероятная причина: сейчас запущена Tekla, либо один из файлов открыт вручную для редактирования.",
            "firm" when rhinoRunning =>
                "Вероятная причина: сейчас запущен Rhino, либо один из файлов открыт вручную для редактирования.",
            _ when teklaRunning && rhinoRunning =>
                "Вероятная причина: сейчас запущены Tekla и Rhino, либо файлы заняты другой программой.",
            _ when teklaRunning =>
                "Вероятная причина: сейчас запущена Tekla, либо файлы заняты другой программой.",
            _ when rhinoRunning =>
                "Вероятная причина: сейчас запущен Rhino, либо файлы заняты другой программой.",
            _ =>
                "Вероятная причина: один или несколько файлов заняты другой программой или открыты для редактирования."
        };

        return request.DisplayName + ": не удалось завершить обновление. " + reason + " Закройте блокирующую программу и повторите синхронизацию вручную.";
    }

    private static void DeleteDirectorySafely(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        ClearReadOnlyAttributesRecursive(new DirectoryInfo(path));
        Directory.Delete(path, recursive: true);
    }

    private static string BuildTechnicalFailureDetails(TeklaManagedSyncRequest request, string stage, Exception ex)
    {
        var fileAccessDetails = ex is ManagedSyncFileAccessException fileAccessException
            ? $" BlockedFile={fileAccessException.FullPath}; LikelyBlockingProcess={string.Join(", ", fileAccessException.LikelyBlockingProcesses)};"
            : string.Empty;
        return $"{request.DisplayName}: техническая ошибка на шаге '{stage}'. " +
               $"TargetKey={request.TargetKey}; LocalPath={request.LocalPath}; RepoUrl={request.RepoUrl}; RepoRef={request.RepoRef}; RepoSubdir={request.RepoSubdir}; " +
               $"{fileAccessDetails} {ex.GetType().Name}: {ex.Message}";
    }

    private static ManagedSyncFileAccessException BuildManagedFileAccessException(string relativePath, string targetPath, Exception innerException)
    {
        var likelyProcesses = DetectLikelyBlockingProcesses(relativePath, targetPath);
        var processHint = likelyProcesses.Count > 0
            ? " Вероятно, файл удерживает процесс: " + string.Join(", ", likelyProcesses) + "."
            : string.Empty;

        return new ManagedSyncFileAccessException(
            relativePath,
            targetPath,
            likelyProcesses,
            "Не удалось обновить файл '" + relativePath + "'." +
            " Путь: " + targetPath + "." +
            processHint +
            " Закройте блокирующую программу и повторите синхронизацию.",
            innerException);
    }

    private static IReadOnlyList<string> DetectLikelyBlockingProcesses(string relativePath, string targetPath)
    {
        var processes = new List<string>();
        var extension = Path.GetExtension(targetPath).ToLowerInvariant();
        var normalizedPath = targetPath.ToLowerInvariant();
        var relativeLower = relativePath.ToLowerInvariant();

        void AddIfRunning(string processName, string label)
        {
            try
            {
                if (Process.GetProcessesByName(processName).Length > 0 &&
                    !processes.Contains(label, StringComparer.OrdinalIgnoreCase))
                {
                    processes.Add(label);
                }
            }
            catch
            {
                // Ignore process detection failures and keep fallback messaging.
            }
        }

        var pointsToLibraries =
            normalizedPath.Contains(@"\grasshopper\libraries\") ||
            relativeLower.EndsWith(".gha", StringComparison.OrdinalIgnoreCase) ||
            relativeLower.EndsWith(".ghpy", StringComparison.OrdinalIgnoreCase);
        var pointsToExtensions = normalizedPath.Contains(@"\environments\common\extensions\");

        if (pointsToLibraries || extension is ".gha" or ".ghpy")
        {
            AddIfRunning("Rhino", "Rhino.exe");
            AddIfRunning("Rhino7", "Rhino7.exe");
            AddIfRunning("Rhino8", "Rhino8.exe");
            AddIfRunning("RhinoWIP", "RhinoWIP.exe");
        }

        if (pointsToExtensions || normalizedPath.Contains(@"\teklafirm\") || extension is ".dll" or ".inp" or ".uel")
        {
            AddIfRunning("TeklaStructures", "TeklaStructures.exe");
        }

        return processes;
    }

    private IEnumerable<string> EnumerateGitExecutableCandidates()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "tools", "git", "bin", "git.exe");
        yield return Path.Combine(baseDir, "tools", "git", "cmd", "git.exe");
        yield return Path.Combine(_bundledGitRoot, "bin", "git.exe");
        yield return Path.Combine(_bundledGitRoot, "cmd", "git.exe");
    }

    private void TryEnsureBundledGitExtracted()
    {
        var bundlePath = Path.Combine(AppContext.BaseDirectory, GitBundleFileName);
        if (!File.Exists(bundlePath))
        {
            return;
        }

        lock (GitBootstrapGate)
        {
            foreach (var candidate in EnumerateGitExecutableCandidates())
            {
                if (File.Exists(candidate))
                {
                    return;
                }
            }

            var parentDir = Path.GetDirectoryName(_bundledGitRoot);
            if (string.IsNullOrWhiteSpace(parentDir))
            {
                return;
            }

            var tempExtractRoot = Path.Combine(parentDir, "git.extract-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(parentDir);
                ZipFile.ExtractToDirectory(bundlePath, tempExtractRoot, overwriteFiles: true);

                var extractedBin = Path.Combine(tempExtractRoot, "bin", "git.exe");
                var extractedCmd = Path.Combine(tempExtractRoot, "cmd", "git.exe");
                if (!File.Exists(extractedBin) && !File.Exists(extractedCmd))
                {
                    throw new InvalidDataException("Архив встроенного git распакован, но git.exe не найден.");
                }

                if (Directory.Exists(_bundledGitRoot))
                {
                    DeleteDirectorySafely(_bundledGitRoot);
                }

                Directory.Move(tempExtractRoot, _bundledGitRoot);
                AppendLog("Встроенный git восстановлен из локального архива.");
            }
            catch (Exception ex)
            {
                AppendLog("Не удалось восстановить встроенный git из локального архива: " + ex.Message);
                try
                {
                    if (Directory.Exists(tempExtractRoot))
                    {
                        DeleteDirectorySafely(tempExtractRoot);
                    }
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }
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

    private static bool RepositoryLooksHealthy(string gitExe, string worktreePath)
    {
        if (!TryRunGit(gitExe, "rev-parse --is-inside-work-tree", worktreePath, out var stdout, out _))
        {
            return false;
        }

        return string.Equals(
            (stdout ?? string.Empty).Trim(),
            "true",
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

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            Task.WhenAll(stdoutTask, stderrTask).GetAwaiter().GetResult();
            stdout = stdoutTask.GetAwaiter().GetResult();
            stderr = stderrTask.GetAwaiter().GetResult();
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
    public string TechnicalDetails { get; init; } = "";

    public static TeklaApplyResult Success(string message, string installedRevision) => new()
    {
        IsSuccess = true,
        Message = message,
        InstalledRevision = installedRevision
    };

    public static TeklaApplyResult Fail(string message, string technicalDetails = "") => new()
    {
        IsSuccess = false,
        Message = message,
        TechnicalDetails = technicalDetails
    };
}

public sealed class ManagedSyncFileAccessException : IOException
{
    public ManagedSyncFileAccessException(
        string relativePath,
        string fullPath,
        IReadOnlyList<string> likelyBlockingProcesses,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        RelativePath = relativePath;
        FullPath = fullPath;
        LikelyBlockingProcesses = likelyBlockingProcesses;
    }

    public string RelativePath { get; }

    public string FullPath { get; }

    public IReadOnlyList<string> LikelyBlockingProcesses { get; }
}
