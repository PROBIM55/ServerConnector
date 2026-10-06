using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Connector.Desktop.Services;

// Provisions Tekla Structures on this PC for self-hosted Model Sharing:
//   (1) IL-patches bin\Features\SharingUIFeature.dll (license gate -> Ok + local token, identity -> synthetic
//       local user, so NOTHING calls Trimble Identity). Per-PC identity is baked from the connected device.
//   (2) writes bin\SharingConfiguration.xml redirecting the client to our on-prem coordinator (VPS net.tcp).
// Nothing else in the Tekla install is touched. Re-runnable (idempotent): always patches from the pristine
// backup. Unknown changes (including a possible Tekla service pack) require review before touching the installation.
public sealed class ModelSharingProvisioningService
{
    private const string FeatureDllName = "SharingUIFeature.dll";
    private const string PristineBackupSuffix = ".trimble-orig";
    private const string StateSuffix = ".structura-ms.json";
    private readonly Func<byte[], byte[]>? _assemblyPatcher;
    private readonly Action<ModelSharingProvisionStage>? _stageHook;
    private readonly Action<ModelSharingWriteStage>? _writeHook;

    public string LogFilePath { get; }

    public ModelSharingProvisioningService(string? stateRoot = null) : this(stateRoot, null, null, null) { }

    internal ModelSharingProvisioningService(string? stateRoot, Func<byte[], byte[]>? assemblyPatcher,
        Action<ModelSharingProvisionStage>? stageHook, Action<ModelSharingWriteStage>? writeHook = null)
    {
        _assemblyPatcher = assemblyPatcher;
        _stageHook = stageHook;
        _writeHook = writeHook;
        var root = Path.GetFullPath(stateRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectorAgentDesktop"));
        Directory.CreateDirectory(root);
        LogFilePath = Path.Combine(root, "model-sharing.log");
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

    // Best-effort resolution of the Tekla "bin" folder that contains Features\SharingUIFeature.dll.
    // Order: explicit configured value -> derive from the Extensions local path -> scan C:\TeklaStructures\* -> default.
    public string ResolveTeklaBin(string? configuredBin, string? extensionsLocalPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredBin) && File.Exists(Path.Combine(configuredBin, "Features", FeatureDllName)))
        {
            return configuredBin.Trim();
        }

        // ...\<ver>\Environments\common\Extensions  ->  ...\<ver>\bin
        if (!string.IsNullOrWhiteSpace(extensionsLocalPath))
        {
            var idx = extensionsLocalPath.IndexOf(@"\Environments", StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                var candidate = Path.Combine(extensionsLocalPath.Substring(0, idx), "bin");
                if (File.Exists(Path.Combine(candidate, "Features", FeatureDllName)))
                {
                    return candidate;
                }
            }
        }

        try
        {
            const string teklaRoot = @"C:\TeklaStructures";
            if (Directory.Exists(teklaRoot))
            {
                var match = Directory.GetDirectories(teklaRoot)
                    .Select(d => Path.Combine(d, "bin"))
                    .Where(b => File.Exists(Path.Combine(b, "Features", FeatureDllName)))
                    .OrderByDescending(b => b, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(match))
                {
                    return match;
                }
            }
        }
        catch
        {
            // Ignore scan failures and fall back to the default path.
        }

        return @"C:\TeklaStructures\2025.0\bin";
    }

    public ModelSharingStatus GetStatus(string teklaBin)
    {
        var status = new ModelSharingStatus { TeklaBin = teklaBin };
        var dll = Path.Combine(teklaBin, "Features", FeatureDllName);
        status.FeatureDllExists = File.Exists(dll);
        status.ConfigExists = File.Exists(Path.Combine(teklaBin, "SharingConfiguration.xml"));

        if (!status.FeatureDllExists)
        {
            return status;
        }

        var state = ReadState(dll + StateSuffix);
        if (state is null)
        {
            status.NeedsManualReview = PathOccupied(dll + StateSuffix) ||
                PathOccupied(dll + PristineBackupSuffix) ||
                PathOccupied(Path.Combine(teklaBin, "SharingConfiguration.xml"));
            return status;
        }

        try
        {
            if (!TryValidateExistingInstallation(teklaBin, dll, dll + PristineBackupSuffix,
                    dll + StateSuffix, state, out _))
            {
                status.NeedsManualReview = true;
                return status;
            }
            status.Provisioned = string.Equals(ComputeSha(File.ReadAllBytes(dll)), state.PatchedSha, StringComparison.OrdinalIgnoreCase) && status.ConfigExists;
            status.NeedsReapply = !status.Provisioned; // a Tekla service pack likely replaced our patched DLL
            status.IdentityEmail = state.IdentityEmail;
            status.IdentityName = state.IdentityName;
            status.ServerHost = state.ServerHost;
            status.ServerPort = state.ServerPort;
            status.AppliedUtc = state.AppliedUtc;
        }
        catch
        {
            status.NeedsManualReview = true;
        }

        return status;
    }

    public ModelSharingProvisionResult Provision(ModelSharingProvisionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdentityEmail))
        {
            return ModelSharingProvisionResult.Fail("Не удалось определить пользователя. Сначала подключитесь по токену устройства.");
        }

        if (IsTeklaRunning())
        {
            return ModelSharingProvisionResult.Fail("Сейчас запущена Tekla Structures. Закройте Tekla и повторите настройку Model Sharing.");
        }

        var teklaBin = request.TeklaBin.Trim();
        var featuresDir = Path.Combine(teklaBin, "Features");
        var live = Path.Combine(featuresDir, FeatureDllName);
        var backup = live + PristineBackupSuffix;
        var statePath = live + StateSuffix;

        if (!File.Exists(live))
        {
            return ModelSharingProvisionResult.Fail(
                "Не найден файл " + live + ". Проверьте путь к папке bin Tekla и версию Tekla Structures.");
        }

        byte[]? previousStateBytes;
        try { previousStateBytes = File.Exists(statePath) ? File.ReadAllBytes(statePath) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ModelSharingProvisionResult.Fail("Не удалось проверить состояние Model Sharing. Требуется ручная проверка.", ex.Message);
        }
        ModelSharingState? previousState;
        try { previousState = previousStateBytes is null ? null : JsonSerializer.Deserialize<ModelSharingState>(previousStateBytes); }
        catch (JsonException) { previousState = null; }
        if (!TryValidateExistingInstallation(teklaBin, live, backup, statePath, previousState, out var stateProblem))
            return ModelSharingProvisionResult.Fail(
                "Файлы Model Sharing отличаются от сохранённого состояния. Настройка остановлена до ручной проверки.", stateProblem);

        var authToken = "local:" + request.IdentityEmail;
        var licenseToken = "local-license:" + request.IdentityEmail;
        var configPath = Path.Combine(teklaBin, "SharingConfiguration.xml");
        byte[]? previousLive = null;
        byte[]? previousConfig = null;
        byte[]? previousBackup = null;
        byte[]? patched = null;
        byte[]? newConfig = null;
        var backupExisted = File.Exists(backup);
        var attemptedLive = false;
        var attemptedConfig = false;

        try
        {
            AppendLog($"Старт настройки Model Sharing: bin='{teklaBin}', user='{request.IdentityEmail}' ({request.IdentityName}), server={request.ServerHost}:{request.ServerPort}.");
            previousLive = File.ReadAllBytes(live);
            if (File.Exists(configPath)) previousConfig = File.ReadAllBytes(configPath);
            if (File.Exists(backup)) previousBackup = File.ReadAllBytes(backup);

            // 1) Resolve only the verified pristine (un-patched) DLL bytes.
            var pristine = ResolvePristine(live, backup, previousState);
            if (previousBackup is null && !pristine.SequenceEqual(previousLive))
                throw new ModelSharingStateConflictException("DLL изменилась во время создания резервной копии.");

            // 2) Patch in memory.
            patched = _assemblyPatcher?.Invoke(pristine) ??
                PatchAssembly(pristine, teklaBin, request.IdentityEmail, request.IdentityName, authToken, licenseToken);

            EnsureFilesMatch(live, previousLive, backup, previousBackup ?? previousLive,
                configPath, previousConfig, statePath, previousStateBytes);

            // 3) Replace the live DLL atomically (temp -> replace, with retries for transient locks).
            attemptedLive = true;
            ReplaceFile(live, patched, previousLive, () => _writeHook?.Invoke(ModelSharingWriteStage.BeforeDllCommit));
            _stageHook?.Invoke(ModelSharingProvisionStage.AfterDll);
            AppendLog("Пропатченная SharingUIFeature.dll установлена.");

            // 4) Write the redirect config.
            newConfig = new UTF8Encoding(false).GetBytes(BuildSharingConfiguration(request.ServerHost, request.ServerPort));
            EnsureFilesMatch(live, patched, backup, previousBackup ?? previousLive,
                configPath, previousConfig, statePath, previousStateBytes);
            attemptedConfig = true;
            WriteGuardedFile(configPath, newConfig, previousConfig,
                () => _writeHook?.Invoke(ModelSharingWriteStage.BeforeConfigCommit));
            _stageHook?.Invoke(ModelSharingProvisionStage.AfterConfig);
            AppendLog($"Записан {configPath} -> {request.ServerHost}:{request.ServerPort}.");

            // 5) Persist state for status detection + service-pack-aware re-provisioning.
            var state = new ModelSharingState
            {
                PristineSha = ComputeSha(pristine),
                PatchedSha = ComputeSha(patched),
                ConfigSha = ComputeSha(newConfig),
                IdentityEmail = request.IdentityEmail,
                IdentityName = request.IdentityName,
                ServerHost = request.ServerHost,
                ServerPort = request.ServerPort,
                AppliedUtc = DateTimeOffset.UtcNow
            };
            EnsureFilesMatch(live, patched, backup, previousBackup ?? previousLive,
                configPath, newConfig, statePath, previousStateBytes);
            WriteState(statePath, state, previousStateBytes,
                () => _writeHook?.Invoke(ModelSharingWriteStage.BeforeStateCommit));

            AppendLog("Настройка Model Sharing завершена успешно.");
            return ModelSharingProvisionResult.Success(
                "Tekla на этом компьютере готова к Model Sharing. Пользователь: " + request.IdentityEmail +
                ". Откройте Tekla, затем File -> Sharing.");
        }
        catch (Exception ex)
        {
            var recovered = TryRestorePreviousState(live, backup, configPath, previousLive, previousConfig,
                patched, newConfig, attemptedLive, attemptedConfig, backupExisted, out var recoveryProblem);
            AppendLog("Ошибка настройки Model Sharing: " + ex.Message + "; восстановление=" + recovered + "; " + recoveryProblem);
            if (!recovered || ex is ModelSharingStateConflictException)
                return ModelSharingProvisionResult.Fail(
                    "Файлы Tekla требуют ручной проверки перед повторной настройкой.",
                    ex.Message + "; " + recoveryProblem);
            return ex switch
            {
                ModelSharingPatchException => ModelSharingProvisionResult.Fail(
                    "Не удалось пропатчить SharingUIFeature.dll. Возможно, версия Tekla отличается от поддерживаемой.", ex.Message),
                IOException or UnauthorizedAccessException => ModelSharingProvisionResult.Fail(
                    "Не удалось обновить файлы Tekla. Исходное состояние восстановлено.", ex.Message),
                _ => ModelSharingProvisionResult.Fail("Не удалось настроить Model Sharing. Исходное состояние восстановлено.", ex.Message)
            };
        }
    }

    private byte[] ResolvePristine(string live, string backup, ModelSharingState? state)
    {
        if (!File.Exists(backup))
        {
            if (state is not null)
                throw new IOException("Отсутствует резервная копия ранее настроенного Model Sharing.");
            // First ever provisioning on this PC: the live DLL is the genuine Trimble pristine.
            File.Copy(live, backup);
            return File.ReadAllBytes(backup);
        }
        if (state is null)
            throw new IOException("Резервная копия существует без файла состояния Model Sharing.");
        return File.ReadAllBytes(backup);
    }

    private static bool TryRestorePreviousState(string live, string backup, string config,
        byte[]? previousLive, byte[]? previousConfig, byte[]? patched, byte[]? newConfig,
        bool attemptedLive, bool attemptedConfig, bool backupExisted, out string problem)
    {
        var failures = new List<string>();
        try
        {
            if (attemptedConfig && newConfig is not null)
            {
                if (File.Exists(config))
                {
                    var current = File.ReadAllBytes(config);
                    if (previousConfig is not null && current.SequenceEqual(previousConfig)) { }
                    else if (current.SequenceEqual(newConfig))
                    {
                        if (previousConfig is null) DeleteGuardedFile(config, newConfig);
                        else WriteGuardedFile(config, previousConfig, newConfig);
                    }
                    else failures.Add("Конфигурация изменилась после записи коннектора.");
                }
                else if (previousConfig is not null)
                    failures.Add("Прежняя конфигурация отсутствует после ошибки.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ModelSharingStateConflictException)
        {
            failures.Add("Восстановление конфигурации: " + ex.GetType().Name);
        }

        try
        {
            if (attemptedLive && previousLive is not null && patched is not null)
            {
                if (!File.Exists(live)) failures.Add("Файл DLL отсутствует после ошибки.");
                else
                {
                    var current = File.ReadAllBytes(live);
                    if (current.SequenceEqual(previousLive)) { }
                    else if (current.SequenceEqual(patched)) ReplaceFile(live, previousLive, patched);
                    else failures.Add("DLL изменилась после записи коннектора.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ModelSharingStateConflictException)
        {
            failures.Add("Восстановление DLL: " + ex.GetType().Name);
        }

        try
        {
            if (!backupExisted && File.Exists(backup) && previousLive is not null)
            {
                if (File.Exists(live) && File.ReadAllBytes(live).SequenceEqual(previousLive) &&
                    File.ReadAllBytes(backup).SequenceEqual(previousLive))
                    DeleteGuardedFile(backup, previousLive);
                else failures.Add("Новая резервная копия осталась после неполного восстановления.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ModelSharingStateConflictException)
        {
            failures.Add("Очистка собственной резервной копии: " + ex.GetType().Name);
        }

        problem = string.Join(" ", failures);
        return failures.Count == 0;
    }

    private static bool TryValidateExistingInstallation(string teklaBin, string live, string backup,
        string statePath, ModelSharingState? state, out string problem)
    {
        problem = "";
        var config = Path.Combine(teklaBin, "SharingConfiguration.xml");
        if (state is null)
        {
            if (PathOccupied(statePath) || PathOccupied(backup) || PathOccupied(config))
            {
                problem = "Есть ранее изменённые файлы или конфигурация без проверяемого состояния.";
                return false;
            }
            return true;
        }
        try
        {
            if (!File.Exists(backup) || string.IsNullOrWhiteSpace(state.PristineSha) ||
                string.IsNullOrWhiteSpace(state.PatchedSha) ||
                !string.Equals(ComputeSha(File.ReadAllBytes(backup)), state.PristineSha, StringComparison.OrdinalIgnoreCase))
            {
                problem = "Резервная копия DLL отсутствует или отличается от сохранённой контрольной суммы.";
                return false;
            }
            var liveSha = ComputeSha(File.ReadAllBytes(live));
            if (!string.Equals(liveSha, state.PatchedSha, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(liveSha, state.PristineSha, StringComparison.OrdinalIgnoreCase))
            {
                problem = "Текущая DLL изменена вне сохранённого патча.";
                return false;
            }
            if (!File.Exists(config))
            {
                problem = "Конфигурация Model Sharing отсутствует при сохранённом состоянии.";
                return false;
            }
            var expectedConfigSha = string.IsNullOrWhiteSpace(state.ConfigSha)
                ? ComputeSha(new UTF8Encoding(false).GetBytes(BuildSharingConfiguration(state.ServerHost, state.ServerPort)))
                : state.ConfigSha;
            if (!string.Equals(ComputeSha(File.ReadAllBytes(config)), expectedConfigSha, StringComparison.OrdinalIgnoreCase))
            {
                problem = "Конфигурация Model Sharing изменена вне сохранённого состояния.";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = "Не удалось проверить файлы Model Sharing: " + ex.GetType().Name;
            return false;
        }
    }

    private static bool PathOccupied(string path) => File.Exists(path) || Directory.Exists(path);

    private static void EnsureFileMatches(string path, byte[]? expected)
    {
        if (expected is null)
        {
            if (PathOccupied(path)) throw new ModelSharingStateConflictException("Путь появился после проверки: " + path);
        }
        else if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(expected))
            throw new ModelSharingStateConflictException("Файл изменился после проверки: " + path);
    }

    private static void EnsureFilesMatch(string live, byte[] expectedLive, string backup, byte[] expectedBackup,
        string config, byte[]? expectedConfig, string statePath, byte[]? expectedState)
    {
        EnsureFileMatches(live, expectedLive);
        EnsureFileMatches(backup, expectedBackup);
        EnsureFileMatches(config, expectedConfig);
        EnsureFileMatches(statePath, expectedState);
    }

    // An exclusive Windows file handle keeps the expected-byte check and write in one
    // operation. A temp + File.Move or File.Copy after a separate check can overwrite
    // a foreign change made in the gap. An interrupted in-place write is detected by
    // the sidecar hash on the next run, and the pristine backup is never rewritten.
    private static void ReplaceFile(string targetPath, byte[] content, byte[] expected,
        Action? beforeCommit = null) => WriteGuardedFile(targetPath, content, expected, beforeCommit);

    private static void WriteGuardedFile(string path, byte[] content, byte[]? expected,
        Action? beforeCommit = null)
    {
        if (expected is null)
        {
            // CreateNew never overwrites a file that arrived after validation.
            using var created = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            beforeCommit?.Invoke();
            created.Write(content);
            created.Flush(flushToDisk: true);
            return;
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var current = new byte[checked((int)file.Length)];
        file.ReadExactly(current);
        if (!current.SequenceEqual(expected))
            throw new ModelSharingStateConflictException("Файл изменился после проверки: " + path);
        beforeCommit?.Invoke();
        try
        {
            file.Position = 0;
            file.SetLength(0);
            file.Write(content);
            file.Flush(flushToDisk: true);
        }
        catch
        {
            // Restore under the same exclusive handle if an in-process write fails.
            file.Position = 0;
            file.SetLength(0);
            file.Write(expected);
            file.Flush(flushToDisk: true);
            throw;
        }
    }

    private static void DeleteGuardedFile(string path, byte[] expected)
    {
        // FILE_SHARE_NONE + DELETE access: compare and mark the same handle for
        // deletion. File.Delete after a separate comparison would reopen a race.
        using var handle = CreateFileW(path, 0x80000000u | 0x00010000u, 0, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException("Не удалось открыть файл для безопасного удаления (Win32 " + Marshal.GetLastWin32Error() + "): " + path);
        using var stream = new FileStream(handle, FileAccess.Read);
        var current = new byte[checked((int)stream.Length)];
        stream.ReadExactly(current);
        if (!current.SequenceEqual(expected))
            throw new ModelSharingStateConflictException("Файл изменился перед удалением: " + path);
        var delete = 1;
        if (!SetFileInformationByHandle(stream.SafeFileHandle, 4, ref delete, sizeof(int)))
            throw new IOException("Не удалось безопасно удалить файл (Win32 " + Marshal.GetLastWin32Error() + "): " + path);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass,
        ref int information, int size);

    private static string BuildSharingConfiguration(string serverHost, int serverPort)
    {
        return
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<SharingConfiguration>\r\n" +
            "    <Parameter>\r\n" +
            "        <ServiceType>OnPremises</ServiceType>\r\n" +
            "        <ServerName>" + serverHost + "</ServerName>\r\n" +
            "        <ServerPort>" + serverPort.ToString() + "</ServerPort>\r\n" +
            "    </Parameter>\r\n" +
            "</SharingConfiguration>\r\n";
    }

    public void AppendLog(string message)
    {
        try
        {
            File.AppendAllText(LogFilePath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break provisioning.
        }
    }

    private static string ComputeSha(byte[] data)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data));
    }

    private static ModelSharingState? ReadState(string statePath)
    {
        try
        {
            if (!File.Exists(statePath))
            {
                return null;
            }
            return JsonSerializer.Deserialize<ModelSharingState>(File.ReadAllText(statePath));
        }
        catch
        {
            return null;
        }
    }

    private static void WriteState(string statePath, ModelSharingState state, byte[]? expected,
        Action? beforeCommit = null)
    {
        var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        WriteGuardedFile(statePath, bytes, expected, beforeCommit);
    }

    // ---- dnlib IL patch (ported from the standalone self-host patcher) ----

    private static byte[] PatchAssembly(byte[] pristine, string teklaBin, string email, string name, string authToken, string licenseToken)
    {
        var resolver = new AssemblyResolver { EnableTypeDefCache = true };
        resolver.PostSearchPaths.Add(teklaBin);
        var mod = ModuleDefMD.Load(pristine, new ModuleContext(resolver));
        resolver.AddToCache(mod);

        var users = mod.Find("Sharing.Users", true);
        if (users is null)
        {
            throw new ModelSharingPatchException("Тип Sharing.Users не найден.");
        }

        var userTd = ResolveType(mod, resolver, "SharingServiceInterface", "SharingServiceInterface.User");
        var luTd = ResolveType(mod, resolver, "Sharing", "Sharing.Interfaces.LoggedInUser");
        if (userTd is null || luTd is null)
        {
            throw new ModelSharingPatchException("Типы User/LoggedInUser не разрешились (проверьте папку bin Tekla).");
        }

        var userCtor = mod.Import(userTd.FindDefaultConstructor());
        var userSetEmail = mod.Import(userTd.FindMethod("set_Email"));
        var userSetName = mod.Import(userTd.FindMethod("set_Name"));
        var luCtor = mod.Import(luTd.FindDefaultConstructor());
        var luSetUser = mod.Import(luTd.FindMethod("set_User"));
        var luSetToken = mod.Import(luTd.FindMethod("set_Token"));

        var t1 = mod.GetTypeRefs().FirstOrDefault(t => t.Namespace == "System.Threading.Tasks" && t.Name == "Task`1");
        var t0 = mod.GetTypeRefs().FirstOrDefault(t => t.Namespace == "System.Threading.Tasks" && t.Name == "Task");
        IResolutionScope scope = t1 is not null ? t1.ResolutionScope : (t0 is not null ? t0.ResolutionScope : mod.CorLibTypes.AssemblyRef);
        ITypeDefOrRef task1TypeRef = t1 is not null ? t1 : new TypeRefUser(mod, "System.Threading.Tasks", "Task`1", scope);
        ITypeDefOrRef taskTypeRef = t0 is not null ? t0 : new TypeRefUser(mod, "System.Threading.Tasks", "Task", scope);

        var ctx = new PatchContext(mod, userCtor, userSetEmail, userSetName, luCtor, luSetUser, luSetToken, taskTypeRef, task1TypeRef, email, name, authToken);

        // (1) license: GetLicense(type) -> SharingLicense(type, Ok(1), token, default)
        var getLicense = users.Methods.FirstOrDefault(m => m.Name == "GetLicense" && m.MethodSig is not null && m.MethodSig.Params.Count == 2);
        if (getLicense is null)
        {
            throw new ModelSharingPatchException("Метод GetLicense не найден.");
        }

        IMethod? slCtor = null;
        TypeSig? nullableDt = null;
        foreach (var ins in getLicense.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Newobj && ins.Operand is IMethod im && im.Name == ".ctor" && im.DeclaringType?.Name == "SharingLicense")
            {
                slCtor = im;
                nullableDt = im.MethodSig.Params[3];
                break;
            }
        }
        if (slCtor is null || nullableDt is null)
        {
            throw new ModelSharingPatchException("Конструктор SharingLicense не найден.");
        }

        var lb = new CilBody { InitLocals = true };
        var loc = new Local(nullableDt);
        lb.Variables.Add(loc);
        lb.Instructions.Add(Instruction.Create(OpCodes.Ldloca, loc));
        lb.Instructions.Add(Instruction.Create(OpCodes.Initobj, nullableDt.ToTypeDefOrRef()));
        lb.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        lb.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
        lb.Instructions.Add(Instruction.Create(OpCodes.Ldstr, licenseToken));
        lb.Instructions.Add(Instruction.Create(OpCodes.Ldloc, loc));
        lb.Instructions.Add(Instruction.Create(OpCodes.Newobj, slCtor));
        lb.Instructions.Add(Instruction.Create(OpCodes.Ret));
        lb.MaxStack = 8;
        getLicense.Body = lb;

        // (2) identity: sync wrappers return a synthetic LoggedInUser
        foreach (var n in new[] { "Login", "GetCurrentLoggedInUser" })
        {
            PatchMethod(users, n, 0, b => { EmitLoggedInUser(b, ctx); b.Instructions.Add(Instruction.Create(OpCodes.Ret)); });
        }

        // async login paths -> Task.FromResult(syntheticLU)
        foreach (var n in new[] { "GetCurrentUserAsync", "LoginAsync", "Login" })
        {
            foreach (var m in users.Methods.Where(x => x.Name == n && IsTaskOf(x, "LoggedInUser")))
            {
                var b = new CilBody { InitLocals = true };
                EmitLoggedInUser(b, ctx);
                b.Instructions.Add(Instruction.Create(OpCodes.Call, FromResult(ctx, LoggedInUserSig(ctx))));
                b.Instructions.Add(Instruction.Create(OpCodes.Ret));
                b.MaxStack = 8;
                m.Body = b;
            }
        }

        PatchMethod(users, "GetCurrentUser", 0, b => { EmitUser(b, ctx); b.Instructions.Add(Instruction.Create(OpCodes.Ret)); });
        PatchMethod(users, "get_IsUserLogged", -1, b => { b.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1)); b.Instructions.Add(Instruction.Create(OpCodes.Ret)); });

        foreach (var m in users.Methods.Where(x => x.Name == "GetNewToken"))
        {
            var b = new CilBody();
            b.Instructions.Add(Instruction.Create(OpCodes.Ldstr, authToken));
            b.Instructions.Add(Instruction.Create(OpCodes.Ret));
            b.MaxStack = 1;
            m.Body = b;
        }

        foreach (var m in users.Methods.Where(x => x.Name == "ReleaseLicenseAsync" && IsTaskOf(x, "Boolean")))
        {
            var b = new CilBody();
            b.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
            b.Instructions.Add(Instruction.Create(OpCodes.Call, FromResult(ctx, mod.CorLibTypes.Boolean)));
            b.Instructions.Add(Instruction.Create(OpCodes.Ret));
            b.MaxStack = 1;
            m.Body = b;
        }

        using var ms = new MemoryStream();
        mod.Write(ms);
        return ms.ToArray();
    }

    private static bool IsTaskOf(MethodDef m, string innerName)
    {
        if (m.MethodSig?.RetType is not GenericInstSig gi || gi.GenericArguments.Count != 1)
        {
            return false;
        }
        return gi.GenericArguments[0].TypeName == innerName;
    }

    private static void PatchMethod(TypeDef t, string name, int realParamCount, Action<CilBody> emit)
    {
        var m = t.Methods.FirstOrDefault(x => x.Name == name &&
            (realParamCount < 0 || (x.MethodSig is not null && x.MethodSig.Params.Count == realParamCount)) &&
            !x.HasGenericParameters);
        if (m is null)
        {
            return;
        }
        var b = new CilBody { InitLocals = true };
        emit(b);
        b.MaxStack = 8;
        m.Body = b;
    }

    private static void EmitUser(CilBody b, PatchContext c)
    {
        b.Instructions.Add(Instruction.Create(OpCodes.Newobj, c.UserCtor));
        b.Instructions.Add(Instruction.Create(OpCodes.Dup));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldstr, c.Email));
        b.Instructions.Add(Instruction.Create(OpCodes.Callvirt, c.UserSetEmail));
        b.Instructions.Add(Instruction.Create(OpCodes.Dup));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldstr, c.Name));
        b.Instructions.Add(Instruction.Create(OpCodes.Callvirt, c.UserSetName));
    }

    private static void EmitLoggedInUser(CilBody b, PatchContext c)
    {
        b.Instructions.Add(Instruction.Create(OpCodes.Newobj, c.LuCtor));
        b.Instructions.Add(Instruction.Create(OpCodes.Dup));
        EmitUser(b, c);
        b.Instructions.Add(Instruction.Create(OpCodes.Callvirt, c.LuSetUser));
        b.Instructions.Add(Instruction.Create(OpCodes.Dup));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldstr, c.AuthToken));
        b.Instructions.Add(Instruction.Create(OpCodes.Callvirt, c.LuSetToken));
    }

    private static IMethod FromResult(PatchContext c, TypeSig argSig)
    {
        var frSig = MethodSig.CreateStaticGeneric(1, new GenericInstSig(new ClassSig(c.Task1TypeRef), new GenericMVar(0)), new GenericMVar(0));
        var frRef = new MemberRefUser(c.Module, "FromResult", frSig, c.TaskTypeRef);
        return new MethodSpecUser(frRef, new GenericInstMethodSig(argSig));
    }

    private static TypeSig LoggedInUserSig(PatchContext c) => c.LuCtor.DeclaringType.ToTypeSig();

    private static TypeDef? ResolveType(ModuleDefMD mod, AssemblyResolver resolver, string asmName, string fullName)
    {
        var aref = mod.GetAssemblyRefs().FirstOrDefault(a => a.Name == asmName);
        if (aref is null)
        {
            return null;
        }
        var asm = resolver.Resolve(aref, mod);
        if (asm is null)
        {
            return null;
        }
        foreach (var m in asm.Modules)
        {
            var t = m.Find(fullName, true);
            if (t is not null)
            {
                return t;
            }
        }
        return null;
    }

    private sealed class PatchContext
    {
        public PatchContext(ModuleDefMD module, IMethod userCtor, IMethod userSetEmail, IMethod userSetName,
            IMethod luCtor, IMethod luSetUser, IMethod luSetToken, ITypeDefOrRef taskTypeRef, ITypeDefOrRef task1TypeRef,
            string email, string name, string authToken)
        {
            Module = module;
            UserCtor = userCtor;
            UserSetEmail = userSetEmail;
            UserSetName = userSetName;
            LuCtor = luCtor;
            LuSetUser = luSetUser;
            LuSetToken = luSetToken;
            TaskTypeRef = taskTypeRef;
            Task1TypeRef = task1TypeRef;
            Email = email;
            Name = name;
            AuthToken = authToken;
        }

        public ModuleDefMD Module { get; }
        public IMethod UserCtor { get; }
        public IMethod UserSetEmail { get; }
        public IMethod UserSetName { get; }
        public IMethod LuCtor { get; }
        public IMethod LuSetUser { get; }
        public IMethod LuSetToken { get; }
        public ITypeDefOrRef TaskTypeRef { get; }
        public ITypeDefOrRef Task1TypeRef { get; }
        public string Email { get; }
        public string Name { get; }
        public string AuthToken { get; }
    }
}

internal enum ModelSharingProvisionStage { AfterDll, AfterConfig }
internal enum ModelSharingWriteStage { BeforeDllCommit, BeforeConfigCommit, BeforeStateCommit }

internal sealed class ModelSharingStateConflictException : Exception
{
    public ModelSharingStateConflictException(string message) : base(message) { }
}

public sealed class ModelSharingProvisionRequest
{
    public string TeklaBin { get; set; } = "";
    public string ServerHost { get; set; } = "";
    public int ServerPort { get; set; } = 9990;
    public string IdentityEmail { get; set; } = "";
    public string IdentityName { get; set; } = "";
}

public sealed class ModelSharingProvisionResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = "";
    public string TechnicalDetails { get; init; } = "";

    public static ModelSharingProvisionResult Success(string message) => new() { IsSuccess = true, Message = message };

    public static ModelSharingProvisionResult Fail(string message, string technicalDetails = "") =>
        new() { IsSuccess = false, Message = message, TechnicalDetails = technicalDetails };
}

public sealed class ModelSharingStatus
{
    public string TeklaBin { get; set; } = "";
    public bool FeatureDllExists { get; set; }
    public bool ConfigExists { get; set; }
    public bool Provisioned { get; set; }
    public bool NeedsReapply { get; set; }
    public bool NeedsManualReview { get; set; }
    public string IdentityEmail { get; set; } = "";
    public string IdentityName { get; set; } = "";
    public string ServerHost { get; set; } = "";
    public int ServerPort { get; set; }
    public DateTimeOffset? AppliedUtc { get; set; }
}

internal sealed class ModelSharingState
{
    public string PristineSha { get; set; } = "";
    public string PatchedSha { get; set; } = "";
    public string ConfigSha { get; set; } = "";
    public string IdentityEmail { get; set; } = "";
    public string IdentityName { get; set; } = "";
    public string ServerHost { get; set; } = "";
    public int ServerPort { get; set; }
    public DateTimeOffset? AppliedUtc { get; set; }
}

public sealed class ModelSharingPatchException : Exception
{
    public ModelSharingPatchException(string message) : base(message)
    {
    }
}
