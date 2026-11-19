using Xunit;
using System.Text.Json;
using Connector.Desktop.Services;
using Platform.Connector.Core;

namespace Connector.Desktop.Tests;

public sealed class ConnectorJobJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "connector-journal-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_root, "jobs.json");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ConnectorJobStatusEnvelope Status(string id, JobStatus status, string? message = null, int? progress = null,
        string? errorCode = null, string executor = ConnectorExecutorIds.Tekla, ExecutionScope? scope = null) =>
        new(1, id, "device", "bridge", CadProvider.Tekla, status, DateTime.UtcNow, message, progress, errorCode,
            ExecutorId: executor, Scope: scope ?? ExecutionScope.Project(ConnectorProductId.Platform, "company", "project"));

    private static ConnectorJobEnvelope Job(string id, string? endpoint = "/constructive/tekla-plan/apply",
        string executor = ConnectorExecutorIds.Tekla, CadProvider provider = CadProvider.Tekla, JobOperation operation = JobOperation.Apply) =>
        new(1, id, "bridge", provider, operation, DateTime.UtcNow,
            endpoint is null ? JsonSerializer.SerializeToElement(new { }) : JsonSerializer.SerializeToElement(new { endpoint, body = new { secret = "payload" } }),
            ExecutorId: executor, Scope: ExecutionScope.Project(ConnectorProductId.Platform, "company", "project"));

    private static ConnectorJobJournal Admitted(string? path, params string[] ids)
    {
        var journal = new ConnectorJobJournal(path);
        foreach (var id in ids) journal.Admit(Job(id));
        return journal;
    }

    [Fact]
    public void Platform_job_becomes_one_entry_with_steps_progress_and_source()
    {
        var journal = Admitted(FilePath, "r1");
        journal.Observe(Status("r1", JobStatus.Picked, "Job picked by connector."));
        journal.Observe(Status("r1", JobStatus.Running, "Job execution started.", 5));
        journal.Observe(Status("r1", JobStatus.Running, "Применение в Tekla", 62));

        var entry = Assert.Single(journal.Entries);
        Assert.Equal(("tekla", "platform", "Tekla", "running", 62), (entry.Kind, entry.Source, entry.Target, entry.Status, entry.Progress));
        Assert.Equal(("Применение плана Tekla", "Мосты"), (entry.Title, entry.Subject));
        Assert.Equal(["Получено из Платформы", "Выполнение", "Применение в Tekla"], entry.Steps.Select(step => step.Label));
        Assert.Equal(["ok", "ok", "run"], entry.Steps.Select(step => step.State));
        Assert.DoesNotContain("payload", JsonSerializer.Serialize(journal.Entries), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_user_operations_are_journaled_technical_requests_are_not()
    {
        var journal = new ConnectorJobJournal(null);
        foreach (var endpoint in new[] { "/health", "/capabilities", "/component/read", "/selection", "/model/select", "/pick/axis",
                     "/expert-attributes/ifc-attribution/scan" })
            journal.Admit(Job("t" + endpoint, endpoint, operation: JobOperation.Read));
        journal.Admit(Job("cad-read", null, ConnectorExecutorIds.AutoCad, CadProvider.AutoCad, JobOperation.Read));
        journal.Admit(Job("cad-health", null, ConnectorExecutorIds.AutoCad, CadProvider.AutoCad, JobOperation.Healthcheck));
        journal.Admit(Job("cad-build", null, ConnectorExecutorIds.AutoCad, CadProvider.AutoCad, JobOperation.Build));
        journal.Admit(Job("agr", null, "compute.agr.blender"));
        journal.Admit(Job("prepare", "/constructive/tekla-plan/prepare"));
        foreach (var id in new[] { "t/health", "t/capabilities", "t/component/read", "t/selection", "t/model/select", "t/pick/axis",
                     "t/expert-attributes/ifc-attribution/scan", "cad-read", "cad-health", "never-leased", "cad-build", "agr", "prepare" })
            journal.Observe(Status(id, JobStatus.Success));

        Assert.Equal(["prepare", "agr", "cad-build"], journal.Entries.Select(entry => entry.Id));
        Assert.Equal(["Подготовка плана Tekla", "Публикация АГР", "Построение в AutoCAD"], journal.Entries.Select(entry => entry.Title));
        Assert.Null(ConnectorJobJournal.Classify(Job("x", "/component/read")));
    }

    [Fact]
    public void Technical_messages_never_reach_the_journal_and_errors_get_user_texts()
    {
        var journal = Admitted(FilePath, "r1", "r2", "r3");
        journal.Observe(Status("r1", JobStatus.Running, "Unhandled executor exception: C:\\secret\\path token=abc"));
        journal.Observe(Status("r1", JobStatus.Error, "Unhandled executor exception: token=abc", errorCode: "UNHANDLED_EXCEPTION"));
        journal.Observe(Status("r2", JobStatus.Error, "Bridge.Desktop did not respond", errorCode: "BRIDGE_DESKTOP_UNREACHABLE"));
        journal.Observe(Status("r3", JobStatus.Error, "Ошибка: Object reference not set to an instance"));

        var json = JsonSerializer.Serialize(journal.Entries);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Object reference", json, StringComparison.Ordinal);
        Assert.Equal("Tekla не отвечает. Откройте модель в Tekla Structures и повторите задание.", journal.Entries[1].Error);
        Assert.Equal("Задание завершилось с ошибкой. Подробности — в журнале коннектора.", journal.Entries[2].Error);
    }

    [Fact]
    public void A_local_failure_keeps_only_a_user_text_whatever_the_caller_passes()
    {
        var journal = new ConnectorJobJournal(FilePath);
        journal.BeginLocal("standard", "Обновление стандарта Tekla").Fail("Не удалось: Access to the path 'C:\\Company' is denied.");
        journal.BeginLocal("sharing", "Настройка Model Sharing").Fail("System.IO.IOException: The process cannot access the file");
        journal.BeginLocal("patch", "Патч IFC-экспорта").Fail("Закройте Tekla Structures и повторите действие.");
        var sync = journal.BeginLocal("standard", "Обновление стандарта Tekla");
        sync.Succeed("Папка фирмы: актуально (r1)");

        var stored = File.ReadAllText(FilePath);
        Assert.DoesNotContain("Access", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("IOException", stored, StringComparison.Ordinal);
        Assert.Equal("Действие не выполнено. Подробности доступны в журнале коннектора.", journal.Entries[3].Error);
        Assert.Equal("Действие не выполнено. Подробности доступны в журнале коннектора.", journal.Entries[2].Error);
        Assert.Equal("Закройте Tekla Structures и повторите действие.", journal.Entries[1].Error);
        Assert.Equal("Папка фирмы: актуально (r1)", journal.Entries[0].Steps[^1].Meta);
    }

    [Fact]
    public void A_resumed_job_drops_the_previous_outcome()
    {
        var journal = Admitted(null, "r1");
        journal.Observe(Status("r1", JobStatus.Interrupted, errorCode: "CONNECTOR_EXECUTION_INTERRUPTED_REVIEW_REQUIRED"));
        Assert.NotNull(journal.Entries[0].Error);

        journal.Observe(Status("r1", JobStatus.Running, progress: 20));

        var entry = Assert.Single(journal.Entries);
        Assert.Equal(("running", (string?)null, (DateTimeOffset?)null), (entry.Status, entry.Error, entry.FinishedAtUtc));
    }

    [Fact]
    public void Local_converter_jobs_are_local_and_operations_report_their_own_steps()
    {
        var journal = new ConnectorJobJournal(FilePath);
        journal.Observe(Status("c1", JobStatus.Success, executor: "converter.ifc-optimize", scope: ExecutionScope.DeviceLocal(ConnectorProductId.Structura)));
        var sync = journal.BeginLocal("standard", "Обновление стандарта Tekla", "Папка фирмы", "Сверка версии");
        sync.Step("Копирование", "C:\\Company\\TeklaFirm");
        sync.Succeed();

        var (local, converter) = (journal.Entries[0], journal.Entries[1]);
        Assert.Equal(("ifc", "local", "ok"), (converter.Kind, converter.Source, converter.Status));
        Assert.Equal(("standard", "local", "ok"), (local.Kind, local.Source, local.Status));
        Assert.Equal(["Сверка версии", "Копирование", "Готово"], local.Steps.Select(step => step.Label));
        Assert.NotNull(local.FinishedAtUtc);
    }

    [Fact]
    public void History_is_bounded_persisted_and_an_interrupted_job_is_closed_on_restart()
    {
        var journal = new ConnectorJobJournal(FilePath);
        for (var i = 0; i < ConnectorJobJournal.Capacity + 5; i++)
            journal.Observe(Status("r" + i, JobStatus.Success, executor: "converter.fbx-glb"));
        var running = journal.BeginLocal("patch", "Патч IFC-экспорта");
        running.Step("Замена файлов");

        var reloaded = new ConnectorJobJournal(FilePath);
        Assert.Equal(ConnectorJobJournal.Capacity, reloaded.Entries.Count);
        var interrupted = reloaded.Entries.First(entry => entry.Kind == "patch");
        Assert.Equal("error", interrupted.Status);
        Assert.DoesNotContain(interrupted.Steps, step => step.State == "run");

        Assert.True(reloaded.RemoveFinished());
        Assert.Empty(reloaded.Entries);
    }

    [Fact]
    public async Task Concurrent_saves_leave_one_complete_file()
    {
        var journal = new ConnectorJobJournal(FilePath);
        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() =>
            journal.Observe(Status("c" + i, JobStatus.Success, executor: "converter.ifc-optimize")))));

        Assert.False(File.Exists(FilePath + ".tmp"));
        Assert.Equal(64, new ConnectorJobJournal(FilePath).Entries.Count);
    }

    [Fact]
    public void Clearing_keeps_running_jobs()
    {
        var journal = Admitted(null, "done", "run");
        journal.Observe(Status("done", JobStatus.Success));
        journal.Observe(Status("run", JobStatus.Running, progress: 30));

        journal.RemoveFinished();

        Assert.Equal("run", Assert.Single(journal.Entries).Id);
    }
}

public sealed class PageConfirmationBrokerTests
{
    private static PageConfirmation Request(string title) => new(title, "Текст", ["Пункт"], null, "Да", Danger: true);

    [Fact]
    public async Task Only_the_current_random_id_answers_and_only_once()
    {
        using var broker = new PageConfirmationBroker();
        var answer = broker.RequestAsync(Request("Отключить диск Z:?"));
        var current = Assert.IsType<PendingPageConfirmation>(broker.Current);
        Assert.Equal(32, current.Id.Length);

        Assert.False(broker.Answer("not-the-id", true));
        Assert.False(answer.IsCompleted);
        Assert.True(broker.Answer(current.Id, true));
        Assert.False(broker.Answer(current.Id, false));
        Assert.True(await answer);
        Assert.Null(broker.Current);
    }

    [Fact]
    public async Task Requests_are_served_in_order_and_disposal_declines_the_rest()
    {
        var broker = new PageConfirmationBroker();
        var first = broker.RequestAsync(Request("Первое"));
        var second = broker.RequestAsync(Request("Второе"));
        Assert.Equal("Первое", broker.Current!.Request.Title);
        broker.Answer(broker.Current.Id, false);
        Assert.False(await first);
        Assert.Equal("Второе", broker.Current!.Request.Title);

        broker.Dispose();
        Assert.False(await second);
        Assert.False(await broker.RequestAsync(Request("После закрытия")));
    }

    [Fact]
    public async Task Cancellation_declines_the_waiting_request()
    {
        using var broker = new PageConfirmationBroker();
        using var cancellation = new CancellationTokenSource();
        var answer = broker.RequestAsync(Request("Вопрос"), cancellation.Token);
        cancellation.Cancel();
        Assert.False(await answer);
        Assert.Null(broker.Current);
    }
}
