using System.IO;
using System.Text.Json;
using Platform.Connector.Core;

namespace Connector.Desktop.Services;

/// <summary>One step of a journal entry. State: ok, run, err or wait.</summary>
public sealed record JobJournalStep(string Label, string State, string? Meta = null);

/// <summary>
/// One job of the unified journal: a Platform job executed by this connector (Tekla, AutoCAD, АГР) or a local one
/// (converters, Tekla standard, Model Sharing, IFC patch). Display facts only — never a payload, path of a
/// server resource, token or exception text. Kind: tekla, autocad, agr, ifc, fbx, standard, sharing, patch, other.
/// Source: platform or local. Status: queued, running, ok, error, cancelled.
/// </summary>
public sealed record JobJournalEntry(
    string Id,
    string Kind,
    string Title,
    string? Subject,
    string Source,
    string? Target,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string Status,
    int? Progress,
    string? Error,
    IReadOnlyList<JobJournalStep> Steps,
    string? Operation = null)
{
    public bool IsFinished => Status is "ok" or "error" or "cancelled";
}

/// <summary>Progress of a local operation recorded in the journal (replaces the native progress windows).</summary>
public interface ILocalJobProgress
{
    void Step(string label, string? meta = null, int? progress = null);
    /// <summary>Replaces the detail line of the running step.</summary>
    void Detail(string meta);
    void Succeed(string? meta = null);
    void Fail(string message);
    void Cancel();
    /// <summary>Removes the entry: the operation turned out not to start (for example, declined by the user).</summary>
    void Discard();
}

/// <summary>
/// Recent history of the jobs this connector ran, kept in the user's profile (bounded, newest first).
/// Platform jobs are read from the shared runner's status stream; local operations are recorded by their owners.
/// </summary>
public sealed class ConnectorJobJournal
{
    public const int Capacity = 100;
    private const int MaximumSteps = 12;
    private const int MaximumText = 240;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly Dictionary<string, string> ModuleLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bridge"] = "Мосты", ["pos"] = "ПОС", ["retaining-walls"] = "Подпорные стены",
        ["engineering-networks"] = "Инженерные сети", ["federated-networks"] = "Сети", ["axis-split"] = "Разбивка по осям",
        ["analysis"] = "Расчёты", ["element-library"] = "Библиотека элементов", ["project-package"] = "Пакет проекта",
        ["converters"] = "Конвертеры", ["agr"] = "АГР"
    };
    private static readonly Dictionary<string, string> ErrorTexts = new(StringComparer.Ordinal)
    {
        ["BRIDGE_DESKTOP_UNREACHABLE"] = "Tekla не отвечает. Откройте модель в Tekla Structures и повторите задание.",
        ["BRIDGE_TOKEN_UNAVAILABLE"] = "Мост к Tekla ещё ни разу не запускался под этой учётной записью Windows.",
        ["BRIDGE_STALE_TEKLA"] = "Tekla перестала отвечать во время задания. Проверьте открытую модель и повторите.",
        ["BRIDGE_HTTP_TIMEOUT"] = "Tekla не ответила вовремя. Повторите задание, когда Tekla освободится.",
        ["BRIDGE_HTTP_INTERACTIVE_TIMEOUT"] = "Tekla ждала действия пользователя слишком долго. Повторите задание.",
        ["AUTOCAD_NO_RUNNING_SESSIONS"] = "AutoCAD не запущен. Откройте чертёж и повторите задание.",
        ["AUTOCAD_SESSION_NOT_SELECTED"] = "Не выбран чертёж AutoCAD. Выберите его в разделе AutoCAD.",
        ["AUTOCAD_SESSION_BUSY"] = "AutoCAD занят командой или диалогом. Задание можно повторить позже.",
        ["AUTOCAD_NO_ACTIVE_DOCUMENT"] = "В AutoCAD нет открытого чертежа.",
        ["AGR_TIMEOUT"] = "Blender не завершил публикацию вовремя.",
        ["AGR_INPUT_NOT_FOUND"] = "Исходные файлы публикации не найдены.",
        ["AGR_INPUT_TRANSFER_FAILED"] = "Не удалось получить исходные файлы публикации.",
        ["CONNECTOR_EXECUTOR_MISSING"] = "Этот коннектор не умеет выполнять такие задания. Обновите Платформа Коннектор.",
        ["CONNECTOR_PROVIDER_MISSING"] = "Этот коннектор не умеет выполнять такие задания. Обновите Платформа Коннектор.",
        ["CONNECTOR_EXECUTION_INTERRUPTED_REVIEW_REQUIRED"] = "Задание прервано при закрытии коннектора. Проверьте результат перед повтором."
    };

    // Jobs a user starts or waits for. Read, selection, pick, scan and health/capabilities requests are
    // technical traffic of the Platform and are not journaled (they would also evict real jobs from the cap).
    private static readonly Dictionary<string, string> TeklaEndpoints = new(StringComparer.Ordinal)
    {
        ["/constructive/tekla-plan/prepare"] = "Подготовка плана Tekla",
        ["/constructive/tekla-plan/apply"] = "Применение плана Tekla",
        ["/model/changes/apply"] = "Изменения модели Tekla",
        ["/component/upsert"] = "Построение компонента Tekla",
        ["/component/modify"] = "Изменение компонента Tekla",
        ["/component/delete"] = "Удаление компонента Tekla",
        ["/uda/files/apply"] = "Атрибуты Tekla",
        ["/expert-attributes/ifc-attribution/apply"] = "Атрибуты IFC в Tekla"
    };
    private static readonly Dictionary<JobOperation, string> AutoCadOperations = new()
    {
        [JobOperation.Build] = "Построение в AutoCAD",
        [JobOperation.Apply] = "Применение в AutoCAD",
        [JobOperation.Rebuild] = "Перестроение в AutoCAD",
        [JobOperation.Export] = "Выгрузка из AutoCAD"
    };
    // Latin words a user text may contain; any other Latin word marks a technical message (exception, key, path).
    private static readonly HashSet<string> LatinWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tekla", "Structures", "Model", "Sharing", "IFC", "AutoCAD", "BIM", "Models", "NetBird", "Blender", "LTS",
        "FBX", "GLB", "ZIP", "Grasshopper", "Libraries", "Structura", "Connector", "Windows", "VPN", "SMB", "JSON", "MultiUser"
    };
    private const string FailureText = "Действие не выполнено. Подробности доступны в журнале коннектора.";

    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly Dictionary<string, (string Kind, string Title, string? Target)> _admitted = new(StringComparer.Ordinal);
    private readonly string? _filePath;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly List<JobJournalEntry> _entries = new();

    public ConnectorJobJournal(string? filePath, TimeProvider? time = null, Action<string>? log = null)
    {
        _filePath = filePath;
        _time = time ?? TimeProvider.System;
        _log = log;
        Load();
    }

    public static string DefaultPath => Path.Combine(UnifiedUserStatePaths.Root, "Journal", "jobs.json");

    public event Action? Changed;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<JobJournalEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    /// <summary>
    /// What a leased job is for the user, from its envelope (executor, operation, payload endpoint). Null means
    /// technical traffic that is not journaled. The payload itself is never kept.
    /// </summary>
    public static (string Kind, string Title, string? Target)? Classify(ConnectorJobEnvelope job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var executor = job.EffectiveExecutorId;
        if (executor.StartsWith("converter.", StringComparison.Ordinal)) return null; // journaled from their own statuses
        if (executor.StartsWith("compute.agr", StringComparison.Ordinal)) return ("agr", "Публикация АГР", "АГР");
        if (executor == ConnectorExecutorIds.Tekla)
        {
            var endpoint = job.Payload.ValueKind == JsonValueKind.Object && job.Payload.TryGetProperty("endpoint", out var value) &&
                           value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return endpoint is not null && TeklaEndpoints.TryGetValue(endpoint, out var title) ? ("tekla", title, "Tekla") : null;
        }
        if (executor == ConnectorExecutorIds.AutoCad)
            return AutoCadOperations.TryGetValue(job.Operation, out var title) ? ("autocad", title, "AutoCAD") : null;
        return null;
    }

    /// <summary>Registers a leased job before its first status; only user-meaningful jobs are journaled.</summary>
    public void Admit(ConnectorJobEnvelope job)
    {
        if (string.IsNullOrWhiteSpace(job?.RequestId) || Classify(job) is not { } kind) return;
        lock (_gate)
        {
            _admitted[job.RequestId] = kind;
            while (_admitted.Count > Capacity * 4) _admitted.Remove(_admitted.Keys.First());
        }
    }

    /// <summary>Applies one status of the shared runner: converter jobs and admitted jobs only.</summary>
    public void Observe(ConnectorJobStatusEnvelope status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (string.IsNullOrWhiteSpace(status.RequestId)) return;
        var now = _time.GetUtcNow();
        var at = status.UpdatedAtUtc == default ? now : new DateTimeOffset(DateTime.SpecifyKind(status.UpdatedAtUtc, DateTimeKind.Utc));
        bool terminal;
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => entry.Id == status.RequestId);
            if (index < 0 && !IsConverter(status) && !_admitted.ContainsKey(status.RequestId)) return;
            var entry = index >= 0 ? _entries[index] : Create(status, at, _admitted.GetValueOrDefault(status.RequestId));
            entry = Apply(entry, status, at);
            if (index >= 0) _entries[index] = entry; else _entries.Insert(0, entry);
            terminal = entry.IsFinished;
            Trim();
        }
        if (terminal) Save();
        Changed?.Invoke();
    }

    /// <summary>Starts a local operation; its owner reports steps and the outcome.</summary>
    public ILocalJobProgress BeginLocal(string kind, string title, string? subject = null, string? firstStep = null)
    {
        var id = "local-" + Guid.NewGuid().ToString("N");
        var now = _time.GetUtcNow();
        var steps = string.IsNullOrWhiteSpace(firstStep) ? Array.Empty<JobJournalStep>() : new[] { new JobJournalStep(Clip(firstStep)!, "run") };
        var entry = new JobJournalEntry(id, kind, Clip(title) ?? "Задание", Clip(subject), "local", null, now, null, "running", null, null, steps);
        lock (_gate)
        {
            _entries.Insert(0, entry);
            Trim();
        }
        Save();
        Changed?.Invoke();
        return new LocalProgress(this, id);
    }

    /// <summary>«Очистить завершённые»: removes finished entries; running and queued ones stay.</summary>
    public bool RemoveFinished()
    {
        int removed;
        lock (_gate) removed = _entries.RemoveAll(entry => entry.IsFinished);
        if (removed == 0) return false;
        Save();
        Changed?.Invoke();
        return true;
    }

    private void Update(string id, Func<JobJournalEntry, JobJournalEntry> change, bool save)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => entry.Id == id);
            if (index < 0 || _entries[index].IsFinished) return;
            _entries[index] = change(_entries[index]);
        }
        if (save) Save();
        Changed?.Invoke();
    }

    private static string ExecutorOf(ConnectorJobStatusEnvelope status) =>
        (status.ExecutorId ?? ConnectorExecutorIds.FromProvider(status.Provider)).Trim().ToLowerInvariant();

    private static bool IsConverter(ConnectorJobStatusEnvelope status) => ExecutorOf(status).StartsWith("converter.", StringComparison.Ordinal);

    private static JobJournalEntry Create(ConnectorJobStatusEnvelope status, DateTimeOffset at, (string Kind, string Title, string? Target) admitted)
    {
        var executor = ExecutorOf(status);
        var (kind, title, target) = executor switch
        {
            "converter.ifc-optimize" => ("ifc", "Оптимизация IFC", (string?)null),
            "converter.ifc-analyze" => ("ifc", "Анализ IFC", null),
            "converter.fbx-glb" => ("fbx", "FBX → GLB", null),
            _ => admitted.Kind is null ? ("other", "Задание Платформы", null) : admitted
        };
        var source = status.Scope?.ScopeKind switch
        {
            ConnectorScopeKind.Project => "platform",
            ConnectorScopeKind.DeviceLocal => "local",
            _ => kind is "ifc" or "fbx" ? "local" : "platform"
        };
        var subject = kind is "ifc" or "fbx" ? null : ModuleLabels.GetValueOrDefault(status.ModuleId ?? string.Empty);
        return new JobJournalEntry(status.RequestId, kind, title, subject, source, target, at, null, "queued", null, null,
            Array.Empty<JobJournalStep>());
    }

    private static JobJournalEntry Apply(JobJournalEntry entry, ConnectorJobStatusEnvelope status, DateTimeOffset at)
    {
        var steps = entry.Steps.ToList();
        var platform = entry.Source == "platform";
        switch (status.Status)
        {
            case JobStatus.Queued:
                if (steps.Count == 0) steps.Add(new JobJournalStep("В очереди", "run"));
                return entry with { Status = "queued", Steps = steps, FinishedAtUtc = null, Error = null };
            case JobStatus.Picked:
                CloseRunning(steps, "ok");
                if (!steps.Any(step => step.Label is "Получено из Платформы" or "Принято к выполнению"))
                    steps.Add(new JobJournalStep(platform ? "Получено из Платформы" : "Принято к выполнению", "ok",
                        at.ToLocalTime().ToString("HH:mm")));
                return entry with { Status = "running", Steps = Bound(steps), FinishedAtUtc = null, Error = null };
            case JobStatus.Running:
            {
                var label = StepText(status.Message) ?? (steps.Any(step => step.State == "run") ? null : "Выполнение");
                if (label is not null && !steps.Any(step => step.State == "run" && step.Label == label))
                {
                    CloseRunning(steps, "ok");
                    steps.Add(new JobJournalStep(label, "run"));
                }
                var progress = status.Progress is { } value ? Math.Clamp(value, 0, 100) : entry.Progress;
                return entry with { Status = "running", Progress = progress, Steps = Bound(steps), FinishedAtUtc = null, Error = null };
            }
            case JobStatus.Success:
                CloseRunning(steps, "ok");
                steps.Add(new JobJournalStep("Готово", "ok", StepText(status.Message)));
                return entry with { Status = "ok", Progress = 100, FinishedAtUtc = at, Steps = Bound(steps) };
            case JobStatus.Cancelled:
                CloseRunning(steps, "wait");
                steps.Add(new JobJournalStep("Отменено", "wait"));
                return entry with { Status = "cancelled", FinishedAtUtc = at, Steps = Bound(steps) };
            default:
            {
                CloseRunning(steps, "err");
                var error = ErrorText(status);
                if (!steps.Any(step => step.State == "err")) steps.Add(new JobJournalStep("Ошибка", "err"));
                return entry with { Status = "error", FinishedAtUtc = at, Error = error, Steps = Bound(steps) };
            }
        }
    }

    private static string ErrorText(ConnectorJobStatusEnvelope status)
    {
        if (status.Status == JobStatus.Timeout) return "Задание не завершилось за отведённое время.";
        if (status.ErrorCode is { } code && ErrorTexts.TryGetValue(code, out var text)) return text;
        if (status.Status == JobStatus.Interrupted) return ErrorTexts["CONNECTOR_EXECUTION_INTERRUPTED_REVIEW_REQUIRED"];
        // Only the executor's own Russian user text; never a technical message that can carry paths or details.
        return StepText(status.Message) ?? "Задание завершилось с ошибкой. Подробности — в журнале коннектора.";
    }

    /// <summary>
    /// A user-facing Russian text; anything technical is refused (null): no Russian at all, a path, a URL,
    /// an assignment, control characters or a Latin word outside the product vocabulary (an exception type,
    /// a key, an English message). Version-like words with digits (r1, 2025.0) are allowed.
    /// </summary>
    internal static string? StepText(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var text = message.Trim();
        if (!text.Any(character => character is >= 'А' and <= 'я' or 'ё' or 'Ё')) return null;
        if (text.IndexOfAny(['\\', '/', '=', '{', '}', '<', '>', '|']) >= 0 || text.Contains("://", StringComparison.Ordinal) ||
            text.Any(char.IsControl)) return null;
        var word = new System.Text.StringBuilder();
        foreach (var character in text + " ")
        {
            if (char.IsAsciiLetterOrDigit(character) || character == '_') { word.Append(character); continue; }
            if (word.Length > 0)
            {
                var token = word.ToString();
                word.Clear();
                if (token.Any(char.IsAsciiLetter) && !token.Any(char.IsAsciiDigit) &&
                    !token.Split('_').All(part => part.Length == 0 || LatinWords.Contains(part))) return null;
            }
        }
        return Clip(text);
    }

    /// <summary>The failure text kept in the journal: the user text when it passes <see cref="StepText"/>, else a fixed line.</summary>
    internal static string FailureMessage(string? message) => StepText(message) ?? FailureText;

    private static void CloseRunning(List<JobJournalStep> steps, string state)
    {
        for (var i = 0; i < steps.Count; i++)
            if (steps[i].State == "run") steps[i] = steps[i] with { State = state };
    }

    private static List<JobJournalStep> Bound(List<JobJournalStep> steps) =>
        steps.Count <= MaximumSteps ? steps : steps.Skip(steps.Count - MaximumSteps).ToList();

    private static string? Clip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= MaximumText ? value : value[..(MaximumText - 1)] + "…";
    }

    private void Trim()
    {
        while (_entries.Count > Capacity)
        {
            var index = _entries.FindLastIndex(entry => entry.IsFinished);
            _entries.RemoveAt(index >= 0 ? index : _entries.Count - 1);
        }
    }

    private void Load()
    {
        if (_filePath is null || !File.Exists(_filePath)) return;
        try
        {
            var info = new FileInfo(_filePath);
            if (info.Length > 4 * 1024 * 1024) return;
            var stored = JsonSerializer.Deserialize<List<JobJournalEntry>>(File.ReadAllText(_filePath), Json) ?? new();
            var now = _time.GetUtcNow();
            foreach (var entry in stored.Where(Valid).Take(Capacity))
                // A job that was running when the connector stopped did not finish here.
                _entries.Add(entry.IsFinished ? entry : entry with
                {
                    Status = "error", FinishedAtUtc = now, Error = "Работа прервана: коннектор был закрыт.",
                    Steps = entry.Steps.Select(step => step.State == "run" ? step with { State = "err" } : step).ToArray()
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _log?.Invoke($"Журнал заданий не прочитан ({ex.GetType().Name}); начат новый.");
        }
    }

    private static bool Valid(JobJournalEntry? entry) =>
        entry is { Id.Length: > 0 and <= 128, Title.Length: > 0, Steps: not null } &&
        entry.Kind is "tekla" or "autocad" or "agr" or "ifc" or "fbx" or "standard" or "sharing" or "patch" or "other" &&
        entry.Source is "platform" or "local";

    private void Save()
    {
        if (_filePath is null) return;
        // One writer at a time: the snapshot is taken inside the save lock, so the last write is the newest state.
        lock (_saveGate)
        try
        {
            JobJournalEntry[] snapshot;
            lock (_gate) snapshot = _entries.ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Json));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Журнал заданий не сохранён ({ex.GetType().Name}).");
        }
    }

    private sealed class LocalProgress(ConnectorJobJournal journal, string id) : ILocalJobProgress
    {
        public void Step(string label, string? meta = null, int? progress = null)
        {
            var text = Clip(label);
            if (text is null) return;
            journal.Update(id, entry =>
            {
                var steps = entry.Steps.ToList();
                var running = steps.FindIndex(step => step.State == "run");
                if (running >= 0 && steps[running].Label == text) steps[running] = steps[running] with { Meta = Clip(meta) };
                else
                {
                    CloseRunning(steps, "ok");
                    steps.Add(new JobJournalStep(text, "run", Clip(meta)));
                }
                return entry with { Steps = Bound(steps), Progress = progress is { } value ? Math.Clamp(value, 0, 100) : entry.Progress };
            }, save: false);
        }

        public void Detail(string meta) => journal.Update(id, entry =>
        {
            var steps = entry.Steps.ToList();
            var running = steps.FindLastIndex(step => step.State == "run");
            if (running < 0) return entry;
            steps[running] = steps[running] with { Meta = Clip(meta) };
            return entry with { Steps = steps };
        }, save: false);

        public void Succeed(string? meta = null) => Finish("ok", null, new JobJournalStep("Готово", "ok", StepText(meta)));

        // Callers may pass an exception message; only a user text reaches jobs.json and the page.
        public void Fail(string message) => Finish("error", FailureMessage(message), null);

        public void Cancel() => Finish("cancelled", null, new JobJournalStep("Отменено", "wait"));

        public void Discard()
        {
            int removed;
            lock (journal._gate) removed = journal._entries.RemoveAll(entry => entry.Id == id && !entry.IsFinished);
            if (removed == 0) return;
            journal.Save();
            journal.Changed?.Invoke();
        }

        private void Finish(string status, string? error, JobJournalStep? last)
        {
            var at = journal._time.GetUtcNow();
            journal.Update(id, entry =>
            {
                var steps = entry.Steps.ToList();
                CloseRunning(steps, status == "error" ? "err" : status == "ok" ? "ok" : "wait");
                if (last is not null) steps.Add(last);
                return entry with
                {
                    Status = status, Error = error, FinishedAtUtc = at, Steps = Bound(steps),
                    Progress = status == "ok" ? 100 : entry.Progress
                };
            }, save: true);
        }
    }
}
