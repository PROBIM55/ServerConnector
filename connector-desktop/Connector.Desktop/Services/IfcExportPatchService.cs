using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Connector.Desktop.Services;

// Patches Tekla Structures on this PC to extend native IFC export (62 entities + persist + property-set entities).
// Unlike ModelSharingProvisioningService (which IL-patches one DLL in memory), this service REPLACES five PREBUILT
// patched binaries plus one XSD edit, from a per-Tekla-build "patch set" staged on disk (delivered out-of-band).
//
//   bin\IFCExport4.dll
//   bin\Model.dll
//   bin\CommonObjects.dll
//   bin\Features\PropertyPaneFeature.dll
//   bin\Features\PropertySetsUIFeature.dll
//   Environments\common\inp\IfcPropertySetConfigurations.xsd   (text edit: +custom entities, idempotent)
//
// The patched DLLs are byte-exact per Tekla build (2024.0 SP5 vs 2025.0 SP7 are DIFFERENT sets). Applying a set to a
// mismatched build would corrupt the binary, so apply is HARD-GATED: it verifies the live TeklaStructures.exe build
// matches the staged manifest, and each staged file's SHA-256 matches the manifest, BEFORE touching bin. Idempotent
// (keeps a pristine .ifc-orig backup per file, detects a Tekla service-pack replacement), with one-click Rollback.
public sealed class IfcExportPatchService
{
    private const string MarkerDll = "IFCExport4.dll";      // anchor DLL that identifies a Tekla bin
    private const string BackupSuffix = ".ifc-orig";        // pristine backup (distinct from any manual *.bak)
    private const string StateFileName = ".structura-ifc.json";
    private const string ManifestFileName = "manifest.json";
    private const string XsdRelFromRoot = @"Environments\common\inp\IfcPropertySetConfigurations.xsd";
    private const string XsdMarker = "Structura-IFC-pset-entities";
    private const int FileReplaceMaxAttempts = 3;
    private readonly Action<IfcPatchIoPoint, string>? _faultInjector;
    private readonly Func<bool>? _teklaRunningProbe;

    public string LogFilePath { get; }

    public IfcExportPatchService(string? stateRoot = null)
        : this(stateRoot, null, null)
    {
    }

    internal IfcExportPatchService(
        string? stateRoot,
        Action<IfcPatchIoPoint, string>? faultInjector,
        Func<bool>? teklaRunningProbe = null)
    {
        var root = Path.GetFullPath(stateRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectorAgentDesktop"));
        Directory.CreateDirectory(root);
        LogFilePath = Path.Combine(root, "ifc-patcher.log");
        _faultInjector = faultInjector;
        _teklaRunningProbe = teklaRunningProbe;
    }

    public bool IsTeklaRunning()
    {
        if (_teklaRunningProbe is not null) return _teklaRunningProbe();
        try { return Process.GetProcessesByName("TeklaStructures").Length > 0; }
        catch { return false; }
    }

    // Find the Tekla bin folder (contains IFCExport4.dll). Order: configured -> derive from Extensions path -> scan.
    public string ResolveTeklaBin(string? configuredBin, string? extensionsLocalPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredBin) && File.Exists(Path.Combine(configuredBin, MarkerDll)))
            return configuredBin.Trim();

        if (!string.IsNullOrWhiteSpace(extensionsLocalPath))
        {
            var idx = extensionsLocalPath.IndexOf(@"\Environments", StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                var candidate = Path.Combine(extensionsLocalPath.Substring(0, idx), "bin");
                if (File.Exists(Path.Combine(candidate, MarkerDll))) return candidate;
            }
        }

        try
        {
            const string teklaRoot = @"C:\TeklaStructures";
            if (Directory.Exists(teklaRoot))
            {
                var match = Directory.GetDirectories(teklaRoot)
                    .Select(d => Path.Combine(d, "bin"))
                    .Where(b => File.Exists(Path.Combine(b, MarkerDll)))
                    .OrderByDescending(b => b, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(match)) return match;
            }
        }
        catch { /* fall through */ }

        return @"C:\TeklaStructures\2025.0\bin";
    }

    // Exact Tekla build from the (never-patched) TeklaStructures.exe, e.g. "2025.0.56843.0". Empty if not found.
    public string DetectBuild(string teklaBin)
    {
        try
        {
            var exe = Path.Combine(teklaBin, "TeklaStructures.exe");
            if (!File.Exists(exe)) return "";
            var fvi = FileVersionInfo.GetVersionInfo(exe);
            return fvi.FileVersion?.Trim() ?? "";
        }
        catch { return ""; }
    }

    public string ResolveXsdPath(string teklaBin)
    {
        var teklaRoot = Path.GetDirectoryName(teklaBin.TrimEnd('\\', '/')) ?? teklaBin;
        return Path.Combine(teklaRoot, XsdRelFromRoot);
    }

    public IfcPatchStatus GetStatus(string teklaBin)
    {
        var status = new IfcPatchStatus { TeklaBin = teklaBin, DetectedBuild = DetectBuild(teklaBin) };
        var anchor = Path.Combine(teklaBin, MarkerDll);
        status.TeklaFound = File.Exists(anchor);
        if (!status.TeklaFound) return status;

        var statePath = Path.Combine(teklaBin, StateFileName);
        var state = ReadState(statePath);
        if (state is null)
        {
            status.NeedsManualReview = File.Exists(statePath);
            return status;
        }

        status.SetVersion = state.SetVersion;
        status.AppliedBuild = state.TeklaBuild;
        status.AppliedUtc = state.AppliedUtc;
        if (!TryValidateTrackedFiles(teklaBin, state, out _))
            status.NeedsManualReview = true;
        else
        {
            status.Applied = state.Files.All(f => ShaEquals(
                ComputeSha(File.ReadAllBytes(Path.Combine(teklaBin, f.TargetRelpath))), f.PatchedSha));
            status.NeedsReapply = !status.Applied;
        }

        return status;
    }

    public IfcPatchResult Apply(IfcPatchRequest request)
    {
        var teklaBin = request.TeklaBin.Trim();
        var stagingDir = request.StagingDir.Trim();

        if (IsTeklaRunning())
            return IfcPatchResult.Fail("Сейчас запущена Tekla Structures. Закройте Tekla и повторите установку патча.");
        if (!File.Exists(Path.Combine(teklaBin, MarkerDll)))
            return IfcPatchResult.Fail("Не найдена папка bin Tekla (нет " + MarkerDll + ") по пути: " + teklaBin + ".");

        var statePath = Path.Combine(teklaBin, StateFileName);
        var previousStateRead = ReadStateForOperation(statePath);
        var previousState = previousStateRead.State;
        if (previousState is null && previousStateRead.Existed)
            return IfcPatchResult.Fail("Файл состояния IFC-патча повреждён или недоступен. Установка остановлена до ручной проверки.");
        if (previousState is not null && !TryValidateTrackedFiles(teklaBin, previousState, out var stateProblem))
            return IfcPatchResult.Fail("Файлы Tekla отличаются от сохранённого состояния патча. Установка остановлена до ручной проверки.", stateProblem);

        IfcPatchManifest manifest;
        try { manifest = LoadManifest(stagingDir); }
        catch (Exception ex) { return IfcPatchResult.Fail("Не удалось прочитать набор патча (manifest.json).", ex.Message); }

        // --- HARD GATE 1: Tekla build must match the staged set (byte offsets are build-specific) ---
        var build = DetectBuild(teklaBin);
        if (string.IsNullOrEmpty(build) || !BuildMatches(build, manifest.TeklaBuild))
        {
            return IfcPatchResult.Fail(
                $"Сборка Tekla ({(string.IsNullOrEmpty(build) ? "не определена" : build)}) не совпадает с набором патча ({manifest.TeklaBuild}). " +
                "Этот набор собран под другую версию Tekla — установка отменена во избежание повреждения файлов.");
        }

        // --- HARD GATE 2: every staged file's SHA-256 must match the manifest (integrity / not corrupt) ---
        foreach (var f in manifest.Files)
        {
            if (!TryResolveContainedPath(teklaBin, f.TargetRelpath, out _) ||
                !TryResolveContainedPath(stagingDir, f.TargetRelpath, out var staged))
                return IfcPatchResult.Fail("Набор патча содержит небезопасный путь файла: " + f.TargetRelpath);
            if (!File.Exists(staged))
                return IfcPatchResult.Fail("В наборе патча отсутствует файл: " + f.TargetRelpath + ".");
            var sha = ComputeSha(File.ReadAllBytes(staged));
            if (!string.Equals(sha, f.Sha256, StringComparison.OrdinalIgnoreCase))
                return IfcPatchResult.Fail("Контрольная сумма файла набора не совпадает (повреждён?): " + f.TargetRelpath + ".",
                    $"expected {f.Sha256}, got {sha}");
        }

        if (previousState is not null && !previousState.Files.Select(f => f.TargetRelpath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(manifest.Files.Select(f => f.TargetRelpath)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            return IfcPatchResult.Fail("Состав нового набора патча отличается от сохранённого. Требуется ручная проверка перед установкой.");
        if (previousState is null && manifest.Files.Any(f => File.Exists(Path.Combine(teklaBin, f.TargetRelpath) + BackupSuffix)))
            return IfcPatchResult.Fail("Найдена резервная копия патча без файла состояния. Требуется ручная проверка перед установкой.");
        var patchIncludesXsd = manifest.Xsd is { Entities.Count: > 0 };
        var safeXsdPath = "";
        if (patchIncludesXsd && !TryResolveSafeXsdPath(teklaBin, out safeXsdPath))
            return IfcPatchResult.Fail("Путь XSD проходит через ссылку или выходит из штатной папки Tekla. Установка остановлена до ручной проверки.");
        if (previousState is null && patchIncludesXsd && File.Exists(safeXsdPath + ".bak"))
            return IfcPatchResult.Fail("Найдена резервная копия XSD без файла состояния. Требуется ручная проверка перед установкой.");
        if (previousState is null && patchIncludesXsd)
        {
            try
            {
                if (File.Exists(safeXsdPath) && File.ReadAllText(safeXsdPath).Contains(XsdMarker, StringComparison.Ordinal))
                    return IfcPatchResult.Fail("XSD уже содержит маркер Structura, но файл состояния патча отсутствует. Требуется ручная проверка перед установкой.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return IfcPatchResult.Fail("Не удалось безопасно проверить XSD до установки патча. Файлы не изменены.", ex.Message);
            }
        }
        if (previousState is not null && previousState.XsdApplied != patchIncludesXsd)
            return IfcPatchResult.Fail("Состав XSD нового набора отличается от сохранённого. Требуется ручная проверка перед установкой.");

        var newState = new IfcPatchState { TeklaBuild = build, SetVersion = manifest.SetVersion, AppliedUtc = DateTimeOffset.UtcNow };
        ApplyTransaction transaction;
        try
        {
            transaction = CaptureApplyTransaction(teklaBin, statePath, manifest, previousState, previousStateRead.Sha);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return IfcPatchResult.Fail("Не удалось подготовить безопасную установку IFC-патча. Файлы не изменены.", ex.Message);
        }

        try
        {
            AppendLog($"Старт установки IFC-патча: bin='{teklaBin}', build='{build}', set='{manifest.SetVersion}'.");

            foreach (var f in manifest.Files)
            {
                var live = Path.Combine(teklaBin, f.TargetRelpath);
                var backup = live + BackupSuffix;
                if (!File.Exists(live))
                    throw new IOException("Не найден заменяемый файл Tekla: " + live);

                var preOperationLive = transaction.GetSnapshot(live).Content;
                byte[] pristine;
                if (previousState is null)
                {
                    WriteOwned(transaction, backup, preOperationLive, IfcPatchIoPoint.WriteBackup);
                    pristine = preOperationLive;
                }
                else
                {
                    if (!File.Exists(backup))
                        throw new IOException("Отсутствует резервная копия ранее установленного патча: " + f.TargetRelpath);
                    pristine = transaction.GetSnapshot(backup).Content;
                }
                var patched = File.ReadAllBytes(Path.Combine(stagingDir, f.TargetRelpath));
                WriteOwned(transaction, live, patched, IfcPatchIoPoint.WritePatchedFile);
                newState.Files.Add(new IfcPatchFileState
                {
                    TargetRelpath = f.TargetRelpath,
                    PristineSha = ComputeSha(pristine),
                    PatchedSha = ComputeSha(patched)
                });
                AppendLog("Заменён: " + f.TargetRelpath);
            }

            // XSD config edit (idempotent, makes its own .bak)
            if (manifest.Xsd is { Entities.Count: > 0 })
            {
                if (!TryResolveSafeXsdPath(teklaBin, out var xsdPath))
                    throw new IOException("Путь XSD стал небезопасным после подготовки операции.");
                var xsdBackup = xsdPath + ".bak";
                var preOperationXsd = transaction.GetSnapshot(xsdPath).Content;
                if (previousState is null)
                    WriteOwned(transaction, xsdBackup, preOperationXsd, IfcPatchIoPoint.WriteBackup);
                var (patchedXsd, added) = BuildPatchedXsd(preOperationXsd, manifest.Xsd.Entities);
                if (!patchedXsd.AsSpan().SequenceEqual(preOperationXsd))
                    WriteOwned(transaction, xsdPath, patchedXsd, IfcPatchIoPoint.WriteXsd);
                newState.XsdApplied = true;
                newState.XsdPath = xsdPath;
                newState.XsdPristineSha = ComputeSha(File.ReadAllBytes(xsdBackup));
                newState.XsdPatchedSha = ComputeSha(File.ReadAllBytes(xsdPath));
                AppendLog($"XSD обновлён ({xsdPath}): +{added} сущностей.");
            }

            // --- VERIFY: every live file now equals the staged patched SHA (+ XSD marker present) ---
            foreach (var f in manifest.Files)
            {
                var live = Path.Combine(teklaBin, f.TargetRelpath);
                InjectFault(IfcPatchIoPoint.VerifyPatchedFile, live);
                if (!string.Equals(ComputeSha(File.ReadAllBytes(live)), f.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Проверка после замены не прошла: " + f.TargetRelpath);
            }
            if (newState.XsdApplied)
            {
                InjectFault(IfcPatchIoPoint.VerifyXsd, newState.XsdPath);
                if (!File.ReadAllText(newState.XsdPath).Contains(XsdMarker))
                    throw new IOException("Проверка XSD не прошла (маркер не найден).");
            }

            WriteStateAtomically(transaction, statePath, newState);
            InjectFault(IfcPatchIoPoint.VerifyState, statePath);
            var committedState = ReadState(statePath);
            if (committedState is null)
                throw new IOException("Проверка файла состояния после сохранения не прошла: файл не читается.");
            if (!TryValidateTrackedFiles(teklaBin, committedState, out var committedProblem))
                throw new IOException("Проверка файла состояния после сохранения не прошла: " + committedProblem);
            if (!string.Equals(committedState.SetVersion, newState.SetVersion, StringComparison.Ordinal))
                throw new IOException("Проверка файла состояния после сохранения не прошла: версия набора отличается.");

            AppendLog("IFC-патч установлен успешно.");
            return IfcPatchResult.Success("Tekla на этом компьютере пропатчена (IFC-экспорт расширен). " +
                "Перезапустите Tekla, чтобы изменения вступили в силу.");
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка установки, выполняю авто-откат: " + ex.Message);
            var rollback = RestorePreOperationState(transaction);
            var hint = ex is UnauthorizedAccessException
                ? "Нет прав на запись в папку Tekla — запустите с правами администратора (UAC)."
                : "Закройте Tekla Structures и программы, открывшие папку bin, и повторите.";
            if (rollback.IsComplete)
                return IfcPatchResult.Fail("Не удалось установить IFC-патч (изменения полностью откатаны). " + hint, ex.Message);

            var rollbackDetails = string.Join("; ", rollback.Problems);
            AppendLog("Авто-откат завершён не полностью: " + rollbackDetails);
            return IfcPatchResult.Fail(
                "Не удалось установить IFC-патч, а автоматический откат завершён не полностью. Требуется ручная проверка файлов Tekla.",
                ex.Message + " | rollback: " + rollbackDetails);
        }
    }

    public IfcPatchResult Rollback(string teklaBin)
    {
        teklaBin = teklaBin.Trim();
        if (IsTeklaRunning())
            return IfcPatchResult.Fail("Сейчас запущена Tekla Structures. Закройте Tekla и повторите откат.");

        var statePath = Path.Combine(teklaBin, StateFileName);
        var stateRead = ReadStateForOperation(statePath);
        var state = stateRead.State;
        if (state is null || state.Files.Count == 0)
            return IfcPatchResult.Fail("Нет данных об установленном патче — откатывать нечего.");
        if (!TryValidateTrackedFiles(teklaBin, state, out var stateProblem))
            return IfcPatchResult.Fail("Файлы Tekla отличаются от сохранённого состояния патча. Откат остановлен до ручной проверки.", stateProblem);

        ApplyTransaction transaction;
        try
        {
            transaction = CaptureRollbackTransaction(teklaBin, statePath, state, stateRead.Sha);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return IfcPatchResult.Fail("Не удалось подготовить безопасный откат IFC-патча. Файлы не изменены.", ex.Message);
        }

        try
        {
            foreach (var f in state.Files)
            {
                var live = Path.Combine(teklaBin, f.TargetRelpath);
                var backup = live + BackupSuffix;
                transaction.AssertStillAtSnapshot(backup);
                if (!ShaEquals(transaction.GetSnapshot(live).Sha, f.PristineSha))
                {
                    WriteOwned(transaction, live, transaction.GetSnapshot(backup).Content, IfcPatchIoPoint.RollbackFile);
                    AppendLog("Восстановлен из эталона: " + f.TargetRelpath);
                }
            }
            if (state.XsdApplied && !string.IsNullOrEmpty(state.XsdPath))
            {
                if (!TryResolveSafeXsdPath(teklaBin, out var xsdPath))
                    throw new IOException("Путь XSD проходит через ссылку или выходит из штатной папки Tekla.");
                var xsdBackup = xsdPath + ".bak";
                transaction.AssertStillAtSnapshot(xsdBackup);
                if (!ShaEquals(transaction.GetSnapshot(xsdPath).Sha, state.XsdPristineSha))
                {
                    WriteOwned(transaction, xsdPath, transaction.GetSnapshot(xsdBackup).Content, IfcPatchIoPoint.RollbackXsd);
                    AppendLog("XSD восстановлен из .bak.");
                }
            }

            InjectFault(IfcPatchIoPoint.RollbackState, statePath);
            AssertRollbackCompleted(teklaBin, state);
            transaction.AssertStillAtSnapshot(statePath);
            File.Delete(statePath);
            AppendLog("Откат IFC-патча выполнен.");
            return IfcPatchResult.Success("Файлы Tekla восстановлены к исходному состоянию. Перезапустите Tekla.");
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка отката: " + ex.Message);
            var restore = RestorePreOperationState(transaction);
            if (restore.IsComplete)
                return IfcPatchResult.Fail("Не удалось откатить патч; состояние до попытки отката полностью восстановлено.", ex.Message);

            var restoreDetails = string.Join("; ", restore.Problems);
            AppendLog("Не удалось вернуть состояние до попытки отката: " + restoreDetails);
            return IfcPatchResult.Fail(
                "Откат остановлен из-за изменения файлов другим процессом. Требуется ручная проверка файлов Tekla.",
                ex.Message + " | restore: " + restoreDetails);
        }
    }

    // ---- internals ----

    private ApplyTransaction CaptureApplyTransaction(
        string teklaBin,
        string statePath,
        IfcPatchManifest manifest,
        IfcPatchState? previousState,
        string previousStateSha)
    {
        var transaction = new ApplyTransaction();
        foreach (var file in manifest.Files)
        {
            if (!TryResolveContainedPath(teklaBin, file.TargetRelpath, out var live))
                throw new IOException("Небезопасный путь файла набора: " + file.TargetRelpath);
            if (!File.Exists(live))
                throw new IOException("Не найден заменяемый файл Tekla: " + live);
            transaction.Capture(live);
            transaction.Capture(live + BackupSuffix);
        }

        if (manifest.Xsd is { Entities.Count: > 0 })
        {
            if (!TryResolveSafeXsdPath(teklaBin, out var xsdPath))
                throw new IOException("Путь XSD проходит через ссылку или выходит из штатной папки Tekla.");
            if (!File.Exists(xsdPath))
                throw new FileNotFoundException("Не найден XSD: " + xsdPath);
            transaction.Capture(xsdPath);
            transaction.Capture(xsdPath + ".bak");
        }

        transaction.Capture(statePath);
        AssertCapturedState(transaction, statePath, previousState is not null, previousStateSha);
        if (previousState is not null)
            AssertCapturedTrackedFiles(transaction, teklaBin, previousState);
        return transaction;
    }

    private ApplyTransaction CaptureRollbackTransaction(string teklaBin, string statePath, IfcPatchState state, string stateSha)
    {
        var transaction = new ApplyTransaction();
        foreach (var file in state.Files)
        {
            if (!TryResolveContainedPath(teklaBin, file.TargetRelpath, out var live))
                throw new IOException("Небезопасный путь файла состояния: " + file.TargetRelpath);
            transaction.Capture(live);
            transaction.Capture(live + BackupSuffix);
        }
        if (state.XsdApplied)
        {
            if (!TryResolveSafeXsdPath(teklaBin, out var xsdPath))
                throw new IOException("Путь XSD проходит через ссылку или выходит из штатной папки Tekla.");
            transaction.Capture(xsdPath);
            transaction.Capture(xsdPath + ".bak");
        }
        transaction.Capture(statePath);
        AssertCapturedState(transaction, statePath, expectedToExist: true, stateSha);
        AssertCapturedTrackedFiles(transaction, teklaBin, state);
        return transaction;
    }

    private static void AssertCapturedState(
        ApplyTransaction transaction,
        string statePath,
        bool expectedToExist,
        string expectedSha)
    {
        var snapshot = transaction.GetSnapshot(statePath);
        if (snapshot.Existed != expectedToExist ||
            (expectedToExist && !ShaEquals(snapshot.Sha, expectedSha)))
            throw new IOException("Файл состояния изменён другим процессом после начальной проверки.");
    }

    private static void AssertCapturedTrackedFiles(ApplyTransaction transaction, string teklaBin, IfcPatchState state)
    {
        foreach (var file in state.Files)
        {
            if (!TryResolveContainedPath(teklaBin, file.TargetRelpath, out var live))
                throw new IOException("Небезопасный путь файла состояния: " + file.TargetRelpath);
            var liveSha = transaction.GetSnapshot(live).Sha;
            var backupSha = transaction.GetSnapshot(live + BackupSuffix).Sha;
            if ((!ShaEquals(liveSha, file.PatchedSha) && !ShaEquals(liveSha, file.PristineSha)) ||
                !ShaEquals(backupSha, file.PristineSha))
                throw new IOException("Файл изменён другим процессом после начальной проверки: " + file.TargetRelpath);
        }
        if (state.XsdApplied)
        {
            if (!TryResolveSafeXsdPath(teklaBin, out var xsd))
                throw new IOException("Путь XSD проходит через ссылку или выходит из штатной папки Tekla.");
            var xsdSha = transaction.GetSnapshot(xsd).Sha;
            var backupSha = transaction.GetSnapshot(xsd + ".bak").Sha;
            if ((!ShaEquals(xsdSha, state.XsdPatchedSha) && !ShaEquals(xsdSha, state.XsdPristineSha)) ||
                !ShaEquals(backupSha, state.XsdPristineSha))
                throw new IOException("XSD изменён другим процессом после начальной проверки.");
        }
    }

    private static void AssertRollbackCompleted(string teklaBin, IfcPatchState state)
    {
        foreach (var file in state.Files)
        {
            if (!TryResolveContainedPath(teklaBin, file.TargetRelpath, out var live) || !File.Exists(live) ||
                !ShaEquals(ComputeSha(File.ReadAllBytes(live)), file.PristineSha) ||
                !File.Exists(live + BackupSuffix) ||
                !ShaEquals(ComputeSha(File.ReadAllBytes(live + BackupSuffix)), file.PristineSha))
                throw new IOException("Файл изменился до завершения отката: " + file.TargetRelpath);
        }
        if (state.XsdApplied)
        {
            if (!TryResolveSafeXsdPath(teklaBin, out var xsd))
                throw new IOException("Путь XSD проходит через ссылку или выходит из штатной папки Tekla.");
            if (!File.Exists(xsd) || !ShaEquals(ComputeSha(File.ReadAllBytes(xsd)), state.XsdPristineSha) ||
                !File.Exists(xsd + ".bak") || !ShaEquals(ComputeSha(File.ReadAllBytes(xsd + ".bak")), state.XsdPristineSha))
                throw new IOException("XSD изменился до завершения отката.");
        }
    }

    private void WriteOwned(ApplyTransaction transaction, string path, byte[] content, IfcPatchIoPoint point)
    {
        InjectFault(point, path);
        transaction.AssertStillAtSnapshot(path);
        var expectedSha = ComputeSha(content);
        try
        {
            ReplaceFile(path, content);
            transaction.RecordOwnedWrite(path, expectedSha);
            if (!File.Exists(path) || !ShaEquals(ComputeSha(File.ReadAllBytes(path)), expectedSha))
                throw new IOException("Записанный файл не прошёл проверку: " + path);
        }
        catch
        {
            // ReplaceFile can fail after the destination write (for example while cleaning its temp file).
            // Claim ownership only when the exact bytes requested by this operation are now present.
            if (File.Exists(path) && ShaEquals(ComputeSha(File.ReadAllBytes(path)), expectedSha))
                transaction.RecordOwnedWrite(path, expectedSha);
            throw;
        }
    }

    private void WriteStateAtomically(ApplyTransaction transaction, string statePath, IfcPatchState state)
    {
        InjectFault(IfcPatchIoPoint.WriteState, statePath);
        transaction.AssertStillAtSnapshot(statePath);
        var content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        var expectedSha = ComputeSha(content);
        var tempPath = statePath + ".structura-ifc-state-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(tempPath, content);
            File.Move(tempPath, statePath, overwrite: true);
            transaction.RecordOwnedWrite(statePath, expectedSha);
        }
        catch
        {
            if (File.Exists(statePath) && ShaEquals(ComputeSha(File.ReadAllBytes(statePath)), expectedSha))
                transaction.RecordOwnedWrite(statePath, expectedSha);
            throw;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private RestoreResult RestorePreOperationState(ApplyTransaction transaction)
    {
        var problems = new List<string>();
        foreach (var mutation in transaction.OwnedWrites.AsEnumerable().Reverse())
        {
            var snapshot = transaction.GetSnapshot(mutation.Path);
            try
            {
                if (!File.Exists(mutation.Path))
                {
                    if (snapshot.Existed)
                        problems.Add("Файл исчез до отката: " + mutation.Path);
                    continue;
                }

                var currentSha = ComputeSha(File.ReadAllBytes(mutation.Path));
                if (!ShaEquals(currentSha, mutation.OwnedSha))
                {
                    problems.Add("Файл изменён другим процессом, не перезаписан: " + mutation.Path);
                    continue;
                }

                if (snapshot.Existed)
                    ReplaceFile(mutation.Path, snapshot.Content);
                else
                    File.Delete(mutation.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add("Не удалось восстановить " + mutation.Path + ": " + ex.GetType().Name);
            }
        }

        foreach (var snapshot in transaction.Snapshots)
        {
            try
            {
                var exists = File.Exists(snapshot.Path);
                if (exists != snapshot.Existed ||
                    (exists && !ShaEquals(ComputeSha(File.ReadAllBytes(snapshot.Path)), snapshot.Sha)))
                    problems.Add("Итог не совпадает с состоянием до операции: " + snapshot.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add("Не удалось проверить откат " + snapshot.Path + ": " + ex.GetType().Name);
            }
        }

        return new RestoreResult(problems.Count == 0, problems.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private void InjectFault(IfcPatchIoPoint point, string path) => _faultInjector?.Invoke(point, path);

    private bool TryValidateTrackedFiles(string teklaBin, IfcPatchState state, out string problem)
    {
        problem = "";
        if (state.Files.Count == 0 || state.Files.Select(f => f.TargetRelpath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Files.Count)
        {
            problem = "Пустой или повторяющийся список файлов патча.";
            return false;
        }
        try
        {
            foreach (var file in state.Files)
            {
                if (!TryResolveContainedPath(teklaBin, file.TargetRelpath, out var live))
                {
                    problem = "Небезопасный путь файла в состоянии патча: " + file.TargetRelpath;
                    return false;
                }
                var backup = live + BackupSuffix;
                if (!File.Exists(live) || !File.Exists(backup) || string.IsNullOrWhiteSpace(file.PristineSha) || string.IsNullOrWhiteSpace(file.PatchedSha))
                {
                    problem = "Отсутствует файл, резервная копия или контрольная сумма: " + file.TargetRelpath;
                    return false;
                }
                if (!ShaEquals(ComputeSha(File.ReadAllBytes(backup)), file.PristineSha))
                {
                    problem = "Резервная копия отличается от сохранённой контрольной суммы: " + file.TargetRelpath;
                    return false;
                }
                var liveSha = ComputeSha(File.ReadAllBytes(live));
                if (!ShaEquals(liveSha, file.PatchedSha) && !ShaEquals(liveSha, file.PristineSha))
                {
                    problem = "Текущий файл изменён вне сохранённого патча: " + file.TargetRelpath;
                    return false;
                }
            }
            if (state.XsdApplied)
            {
                if (!TryResolveSafeXsdPath(teklaBin, out var expectedXsd))
                {
                    problem = "Путь XSD проходит через ссылку или выходит из штатной папки Tekla.";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(state.XsdPath) ||
                    !string.Equals(Path.GetFullPath(state.XsdPath), expectedXsd, StringComparison.OrdinalIgnoreCase))
                {
                    problem = "Путь XSD отличается от штатного расположения Tekla.";
                    return false;
                }
                var backup = expectedXsd + ".bak";
                if (!File.Exists(expectedXsd) || !File.Exists(backup) ||
                    string.IsNullOrWhiteSpace(state.XsdPristineSha) || string.IsNullOrWhiteSpace(state.XsdPatchedSha))
                {
                    problem = "Отсутствует XSD, резервная копия или контрольная сумма XSD.";
                    return false;
                }
                if (!ShaEquals(ComputeSha(File.ReadAllBytes(backup)), state.XsdPristineSha))
                {
                    problem = "Резервная копия XSD отличается от сохранённого состояния.";
                    return false;
                }
                var liveXsdSha = ComputeSha(File.ReadAllBytes(expectedXsd));
                if (!ShaEquals(liveXsdSha, state.XsdPatchedSha) && !ShaEquals(liveXsdSha, state.XsdPristineSha))
                {
                    problem = "XSD изменён вне сохранённого патча.";
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problem = "Не удалось проверить файлы патча: " + ex.GetType().Name;
            return false;
        }
    }

    private static bool ShaEquals(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveSafeXsdPath(string teklaBin, out string resolved)
    {
        resolved = "";
        try
        {
            var fullBin = Path.GetFullPath(teklaBin.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var teklaRoot = Path.GetDirectoryName(fullBin);
            if (string.IsNullOrWhiteSpace(teklaRoot)) return false;
            var candidate = Path.GetFullPath(Path.Combine(teklaRoot, XsdRelFromRoot));
            var rootPrefix = teklaRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                             Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || ContainsReparsePoint(candidate))
                return false;
            resolved = candidate;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                    NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryResolveContainedPath(string root, string relative, out string resolved)
    {
        resolved = "";
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is ".." or "." || part.Length == 0))
            return false;
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
            if (!candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;
            if (ContainsReparsePoint(candidate))
                return false;
            resolved = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool ContainsReparsePoint(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(pathRoot)) return true;

        var current = pathRoot;
        var relative = fullPath[pathRoot.Length..];
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current) && !File.Exists(current))
                continue;
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return true;
            }
        }
        return false;
    }

    private static bool BuildMatches(string detected, string manifestBuild)
    {
        if (string.IsNullOrWhiteSpace(manifestBuild)) return false;
        var d = detected.Trim();
        var m = manifestBuild.Trim();
        return d.StartsWith(m, StringComparison.OrdinalIgnoreCase) || m.StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }

    // Port of tools/scripts/pset-entities-xsd.ps1: build the idempotent edit in memory.
    // Persistence is owned by the surrounding transaction so XSD and its backup roll back together.
    private static (byte[] Content, int Added) BuildPatchedXsd(byte[] original, IReadOnlyDictionary<string, string> entities)
    {
        var raw = Encoding.UTF8.GetString(original);
        if (raw.Contains(XsdMarker)) return (original, 0); // already applied

        var nl = raw.Contains("\r\n") ? "\r\n" : "\n";
        var lines = raw.Replace("\r\n", "\n").Split('\n').ToList();

        var missing = entities.Where(kv => !raw.Contains("value=\"" + kv.Key + "\"")).ToList();
        if (missing.Count == 0) return (original, 0);

        var iWin = lines.FindIndex(l => l.Contains("value=\"IfcWindow\""));
        if (iWin < 0) throw new InvalidOperationException("В XSD не найден enumeration IfcWindow — структура изменилась.");
        var iClose = iWin;
        while (iClose < lines.Count && !lines[iClose].Contains("</xs:enumeration>")) iClose++;
        if (iClose >= lines.Count) throw new InvalidOperationException("Не найден закрывающий тег enumeration для IfcWindow.");

        var indent = new string(lines[iWin].TakeWhile(c => c is ' ' or '\t').ToArray());
        var block = new List<string> { $"{indent}<!-- {XsdMarker}: custom IFC entities (mirror of «Объект IFC» dropdown) -->" };
        foreach (var (name, dom) in missing)
        {
            block.Add($"{indent}<xs:enumeration value=\"{name}\">");
            block.Add($"{indent}\t<xs:annotation>");
            block.Add($"{indent}\t\t<xs:documentation>{dom}</xs:documentation>");
            block.Add($"{indent}\t</xs:annotation>");
            block.Add($"{indent}</xs:enumeration>");
        }

        var outLines = new List<string>();
        outLines.AddRange(lines.Take(iClose + 1));
        outLines.AddRange(block);
        outLines.AddRange(lines.Skip(iClose + 1));
        return (new UTF8Encoding(false).GetBytes(string.Join(nl, outLines)), missing.Count);
    }

    private void RollbackXsd(string xsdPath)
    {
        var bak = xsdPath + ".bak";
        if (File.Exists(bak)) { File.Copy(bak, xsdPath, overwrite: true); AppendLog("XSD восстановлен из .bak."); }
    }

    private IfcPatchManifest LoadManifest(string stagingDir)
    {
        var path = Path.Combine(stagingDir, ManifestFileName);
        var m = JsonSerializer.Deserialize<IfcPatchManifest>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (m is null || m.Files.Count == 0) throw new InvalidOperationException("Пустой или некорректный manifest.json.");
        return m;
    }

    private static void ReplaceFile(string targetPath, byte[] content)
    {
        for (var attempt = 1; attempt <= FileReplaceMaxAttempts; attempt++)
        {
            var tempPath = targetPath + ".structura-ifc-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.WriteAllBytes(tempPath, content);
                if (File.Exists(targetPath))
                {
                    var info = new FileInfo(targetPath);
                    if ((info.Attributes & FileAttributes.ReadOnly) != 0) info.Attributes &= ~FileAttributes.ReadOnly;
                    File.Copy(tempPath, targetPath, overwrite: true);
                    File.Delete(tempPath);
                }
                else
                {
                    File.Move(tempPath, targetPath);
                }
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < FileReplaceMaxAttempts)
            {
                TryDelete(tempPath);
                Thread.Sleep(TimeSpan.FromMilliseconds(300 * attempt));
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    public void AppendLog(string message)
    {
        try { File.AppendAllText(LogFilePath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}"); }
        catch { /* logging must never break patching */ }
    }

    private static string ComputeSha(byte[] data)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data));
    }

    private static IfcPatchState? ReadState(string statePath)
    {
        try
        {
            return File.Exists(statePath)
                ? JsonSerializer.Deserialize<IfcPatchState>(File.ReadAllText(statePath))
                : null;
        }
        catch { return null; }
    }

    private static OperationStateRead ReadStateForOperation(string statePath)
    {
        if (!File.Exists(statePath)) return new OperationStateRead(false, null, "");
        try
        {
            var content = File.ReadAllBytes(statePath);
            var state = JsonSerializer.Deserialize<IfcPatchState>(content);
            return new OperationStateRead(true, state, ComputeSha(content));
        }
        catch
        {
            return new OperationStateRead(true, null, "");
        }
    }

}

internal enum IfcPatchIoPoint
{
    WriteBackup,
    WritePatchedFile,
    WriteXsd,
    VerifyPatchedFile,
    VerifyXsd,
    WriteState,
    VerifyState,
    RollbackFile,
    RollbackXsd,
    RollbackState
}

internal sealed class ApplyTransaction
{
    private readonly Dictionary<string, ApplySnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<OwnedMutation> _ownedWrites = new();

    public IReadOnlyCollection<ApplySnapshot> Snapshots => _snapshots.Values;
    public IReadOnlyList<OwnedMutation> OwnedWrites => _ownedWrites;

    public void Capture(string path)
    {
        path = Path.GetFullPath(path);
        if (_snapshots.ContainsKey(path)) return;
        var existed = File.Exists(path);
        var content = existed ? File.ReadAllBytes(path) : Array.Empty<byte>();
        _snapshots[path] = new ApplySnapshot(path, existed, content, existed ? ComputeSnapshotSha(content) : "");
    }

    public ApplySnapshot GetSnapshot(string path) => _snapshots[Path.GetFullPath(path)];

    public void AssertStillAtSnapshot(string path)
    {
        var snapshot = GetSnapshot(path);
        var exists = File.Exists(snapshot.Path);
        if (exists != snapshot.Existed ||
            (exists && !string.Equals(ComputeSnapshotSha(File.ReadAllBytes(snapshot.Path)), snapshot.Sha,
                StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Файл изменён другим процессом после подготовки операции: " + snapshot.Path);
    }

    public void RecordOwnedWrite(string path, string ownedSha)
    {
        path = Path.GetFullPath(path);
        var existing = _ownedWrites.FindIndex(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) _ownedWrites[existing] = new OwnedMutation(path, ownedSha);
        else _ownedWrites.Add(new OwnedMutation(path, ownedSha));
    }

    private static string ComputeSnapshotSha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}

internal sealed record ApplySnapshot(string Path, bool Existed, byte[] Content, string Sha);
internal sealed record OwnedMutation(string Path, string OwnedSha);
internal sealed record RestoreResult(bool IsComplete, IReadOnlyList<string> Problems);
internal sealed record OperationStateRead(bool Existed, IfcPatchState? State, string Sha);

// ---- manifest (shipped with each per-build patch set) ----
public sealed class IfcPatchManifest
{
    public string TeklaBuild { get; set; } = "";   // e.g. "2025.0.56843" (prefix-matched against TeklaStructures.exe)
    public string SetVersion { get; set; } = "";   // e.g. "2025.0-SP7-1"
    public List<IfcPatchFileEntry> Files { get; set; } = new();
    public IfcXsdSpec? Xsd { get; set; }
}

public sealed class IfcPatchFileEntry
{
    public string TargetRelpath { get; set; } = ""; // relative to Tekla bin, e.g. "Features\\PropertyPaneFeature.dll"
    public string Sha256 { get; set; } = "";
}

public sealed class IfcXsdSpec
{
    public Dictionary<string, string> Entities { get; set; } = new(); // entity name -> IFC domain (ARCH/MEP/INFRA/STRU)
}

// ---- request / result / status / per-bin state ----
public sealed class IfcPatchRequest
{
    public string TeklaBin { get; set; } = "";
    public string StagingDir { get; set; } = ""; // folder with manifest.json + the patched files mirrored by TargetRelpath
}

public sealed class IfcPatchResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = "";
    public string TechnicalDetails { get; init; } = "";
    public static IfcPatchResult Success(string message) => new() { IsSuccess = true, Message = message };
    public static IfcPatchResult Fail(string message, string technicalDetails = "") =>
        new() { IsSuccess = false, Message = message, TechnicalDetails = technicalDetails };
}

public sealed class IfcPatchStatus
{
    public string TeklaBin { get; set; } = "";
    public bool TeklaFound { get; set; }
    public string DetectedBuild { get; set; } = "";
    public bool Applied { get; set; }
    public bool NeedsReapply { get; set; }
    public bool NeedsManualReview { get; set; }
    public string SetVersion { get; set; } = "";
    public string AppliedBuild { get; set; } = "";
    public DateTimeOffset? AppliedUtc { get; set; }
}

internal sealed class IfcPatchState
{
    public string TeklaBuild { get; set; } = "";
    public string SetVersion { get; set; } = "";
    public List<IfcPatchFileState> Files { get; set; } = new();
    public bool XsdApplied { get; set; }
    public string XsdPath { get; set; } = "";
    public string XsdPristineSha { get; set; } = "";
    public string XsdPatchedSha { get; set; } = "";
    public DateTimeOffset? AppliedUtc { get; set; }
}

internal sealed class IfcPatchFileState
{
    public string TargetRelpath { get; set; } = "";
    public string PristineSha { get; set; } = "";
    public string PatchedSha { get; set; } = "";
}
