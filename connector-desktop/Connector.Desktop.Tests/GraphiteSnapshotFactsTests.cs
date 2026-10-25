using System.Text.Json;
using Connector.Desktop.Features.GraphiteWeb;
using Connector.Desktop.Models;
using Connector.Desktop.Services;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class GraphiteSnapshotFactsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, Web);

    private static JobJournalEntry Entry(string id, string kind, string source, string status, string? error = null) =>
        new(id, kind, "Задание", "Мосты", source, kind == "tekla" ? "Tekla" : null, DateTimeOffset.UtcNow.AddMinutes(-5),
            status is "ok" or "error" ? DateTimeOffset.UtcNow : null, status, 40, error, [new JobJournalStep("Выполнение", "run")]);

    private static ConverterJobFacts Converter(string id, string state) =>
        new(id, "ifc", "optimize", state, 100, "structura", "Модель.ifc", "Модель.optimized.ifc", true, true, true, 4_718_592, 3_250_585);

    [Fact]
    public void Jobs_merge_the_journal_with_converter_facts_newest_first()
    {
        var jobs = Json(GraphiteSnapshotFacts.Jobs(
            [Entry("run", "tekla", "platform", "running"), Entry("c1", "ifc", "local", "ok"), Entry("gone", "ifc", "local", "ok")],
            [Converter("old", "Готово"), Converter("c1", "Готово")]));

        Assert.Equal(["run", "c1", "old"], jobs.EnumerateArray().Select(job => job.GetProperty("id").GetString()));
        var tekla = jobs[0];
        Assert.Equal(("platform", "Tekla", "running", 40), (tekla.GetProperty("source").GetString(), tekla.GetProperty("target").GetString(),
            tekla.GetProperty("status").GetString(), tekla.GetProperty("progress").GetInt32()));
        Assert.Equal("Выполнение", tekla.GetProperty("steps")[0].GetProperty("label").GetString());
        var converter = jobs[1];
        Assert.Equal("Модель.ifc", converter.GetProperty("subject").GetString());
        Assert.Equal(3_250_585, converter.GetProperty("outputBytes").GetInt64());
        Assert.True(converter.GetProperty("hasValidation").GetBoolean());
        Assert.Equal(JsonValueKind.Null, jobs[2].GetProperty("startedAt").ValueKind);
    }

    [Fact]
    public void Tekla_standard_sections_carry_full_paths_and_the_available_revision()
    {
        var settings = new AppSettings
        {
            TeklaStandardInstalledVersion = "2026.03.0", TeklaStandardInstalledRevision = "r1",
            TeklaStandardTargetVersion = "2026.03.0", TeklaStandardTargetRevision = "r1",
            TeklaStandardLastCheckUtc = DateTimeOffset.Parse("2026-10-25T21:40:00Z"),
            TeklaExtensionsInstalledVersion = "2026.03.0", TeklaExtensionsInstalledRevision = "r1",
            TeklaExtensionsTargetVersion = "2026.04.0", TeklaExtensionsTargetRevision = "r1b",
            TeklaLibrariesLocalPath = ""
        };
        var std = Json(GraphiteSnapshotFacts.TeklaStandard(settings, new Dictionary<string, string> { ["lib-path"] = @"D:\Libraries" }));

        Assert.Equal("github.com/PROBIM55/tekla-firm-standard", std.GetProperty("source").GetString());
        var sections = std.GetProperty("sections");
        Assert.Equal(@"C:\Company\TeklaFirm", sections[0].GetProperty("path").GetString());
        Assert.Equal("2026.03.0 r1", sections[0].GetProperty("installed").GetString());
        Assert.False(sections[0].GetProperty("updateAvailable").GetBoolean());
        Assert.Equal("2026.04.0 r1b", sections[1].GetProperty("available").GetString());
        Assert.True(sections[1].GetProperty("updateAvailable").GetBoolean());
        Assert.Equal(@"D:\Libraries", sections[2].GetProperty("path").GetString());
        Assert.Equal("2026.04.0", GraphiteSnapshotFacts.Join("2026.04.0", ""));
        Assert.Equal("2026.04.0 r1", GraphiteSnapshotFacts.Join("2026.04.0 r1", "r1"));
    }

    [Fact]
    public void Update_shows_the_version_and_changes_only_when_one_is_pending()
    {
        var info = new DesktopUpdateInfo("1.1.13", "- Первое\n- Второе", DateTimeOffset.UtcNow, false);
        var pending = Json(GraphiteSnapshotFacts.Update(info, available: true));
        Assert.Equal("1.1.13", pending.GetProperty("version").GetString());
        Assert.Equal(["Первое", "Второе"], pending.GetProperty("notes").EnumerateArray().Select(line => line.GetString()));
        var none = Json(GraphiteSnapshotFacts.Update(info, available: false));
        Assert.Equal(JsonValueKind.Null, none.GetProperty("version").ValueKind);
        Assert.Equal(0, none.GetProperty("notes").GetArrayLength());
    }

    [Fact]
    public void Tekla_status_reads_version_and_open_model_from_the_bridge_capabilities()
    {
        var result = JsonSerializer.SerializeToElement(new
        {
            provider = "tekla",
            response = new
            {
                bridgeVersion = "1.4.2+0f3c99b1",
                tekla = new { connected = true, version = "2025.0", modelName = "Иртыш ПС4.1", modelPath = @"C:\Models\Иртыш" }
            }
        });
        var status = Connector.Platform.PlatformTeklaStatus.FromCapabilities(result);
        Assert.Equal(new Connector.Platform.PlatformTeklaStatus(true, "2025.0", "Иртыш ПС4.1", "1.4.2"), status);

        var closed = JsonSerializer.SerializeToElement(new { response = new { tekla = new { connected = false, version = "2025.0", modelName = "Старая" } } });
        Assert.Null(Connector.Platform.PlatformTeklaStatus.FromCapabilities(closed).ModelName);
        Assert.False(Connector.Platform.PlatformTeklaStatus.FromCapabilities(null).Connected);
    }

    [Fact]
    public void Confirmation_exposes_its_text_and_random_id_only()
    {
        using var broker = new PageConfirmationBroker();
        _ = broker.RequestAsync(new PageConfirmation("Отключить диск Z:?", "Текст", ["Пункт"], "Примечание", "Отключить", Danger: true));
        var confirmation = Json(GraphiteSnapshotFacts.Confirmation(broker.Current)!);

        Assert.Equal(broker.Current!.Id, confirmation.GetProperty("id").GetString());
        Assert.True(confirmation.GetProperty("danger").GetBoolean());
        Assert.Equal("Отмена", confirmation.GetProperty("cancel").GetString());
        Assert.Equal(["id", "title", "text", "items", "note", "primary", "danger", "cancel"],
            confirmation.EnumerateObject().Select(property => property.Name));
        Assert.Null(GraphiteSnapshotFacts.Confirmation(null));
    }
}
