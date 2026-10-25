using System.Globalization;
using Connector.Desktop.Models;
using Connector.Desktop.Services;

namespace Connector.Desktop.Features.GraphiteWeb;

/// <summary>A converter job of the workspace in display form (file names and sizes only, never full paths).</summary>
public sealed record ConverterJobFacts(
    string Id,
    string Kind,
    string Operation,
    string State,
    int? Progress,
    string Mode,
    string? InputName,
    string? OutputName,
    bool HasResult,
    bool HasReport,
    bool HasValidation,
    long? InputBytes,
    long? OutputBytes);

/// <summary>
/// Pure builders of the snapshot parts the page shows: the unified job journal, the Tekla standard sections,
/// the pending update and the page confirmation. Each emits display facts only — no token, certificate,
/// credential, payload or exception text — and is unit-tested apart from the desktop composition.
/// </summary>
public static class GraphiteSnapshotFacts
{
    public const string TeklaStandardSource = "github.com/PROBIM55/tekla-firm-standard";

    /// <summary>
    /// One list, newest first: journal entries (Platform jobs and local operations) enriched with the converter
    /// facts of the same request, followed by restored converter jobs that predate the journal.
    /// </summary>
    public static object[] Jobs(IReadOnlyList<JobJournalEntry> journal, IReadOnlyList<ConverterJobFacts> converters)
    {
        var byId = converters.GroupBy(job => job.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var result = new List<object>(journal.Count + converters.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in journal)
        {
            byId.TryGetValue(entry.Id, out var converter);
            // A converter job removed from the workspace («Очистить завершённые») is not shown again by its journal entry.
            if (converter is null && entry.Kind is "ifc" or "fbx" && entry.IsFinished) continue;
            seen.Add(entry.Id);
            result.Add(Job(entry, converter));
        }
        foreach (var converter in converters.Reverse())
            if (seen.Add(converter.Id)) result.Add(Job(null, converter));
        return result.ToArray();
    }

    private static object Job(JobJournalEntry? entry, ConverterJobFacts? converter)
    {
        var status = converter is not null ? ConverterStatus(converter.State) : entry!.Status;
        return new
        {
            id = entry?.Id ?? converter!.Id,
            kind = converter?.Kind ?? entry!.Kind,
            source = entry?.Source ?? "local",
            title = entry?.Title ?? (converter!.Kind == "fbx" ? "FBX → GLB" : converter.Operation == "analyze" ? "Анализ IFC" : "Оптимизация IFC"),
            subject = converter?.InputName ?? entry?.Subject,
            target = entry?.Target,
            startedAt = entry?.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            finishedAt = entry?.FinishedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            status,
            progress = Math.Clamp(converter?.Progress ?? entry?.Progress ?? (status == "ok" ? 100 : 0), 0, 100),
            error = status == "error" ? entry?.Error ?? (converter is not null ? "Конвертация не выполнена. Подробности — в отчёте или журнале." : null) : null,
            steps = (entry?.Steps ?? Array.Empty<JobJournalStep>()).Select(step => new { label = step.Label, state = step.State, meta = step.Meta }).ToArray(),
            mode = converter?.Mode,
            operation = converter?.Operation,
            output = converter?.OutputName ?? "",
            hasResult = converter?.HasResult == true,
            hasReport = converter?.HasReport == true,
            hasValidation = converter?.HasValidation == true,
            inputBytes = converter?.InputBytes,
            outputBytes = converter?.OutputBytes
        };
    }

    /// <summary>Workspace states of a converter job in the journal vocabulary.</summary>
    public static string ConverterStatus(string state) => state switch
    {
        "Готово" => "ok",
        "Отменено" => "cancelled",
        "Ошибка" or "Превышено время" or "Прервано" => "error",
        "В очереди" => "queued",
        "Принято исполнителем" or "Выполняется" => "running",
        _ => "queued"
    };

    /// <summary>
    /// Sections of the Tekla firm standard with their full local folders, installed and available revisions
    /// (the latter read from the public manifest by the last check) and the time of that check.
    /// </summary>
    public static object TeklaStandard(AppSettings settings, IReadOnlyDictionary<string, string> picked)
    {
        object Section(string id, string field, string? saved, string installedVersion, string installedRevision,
            string targetVersion, string targetRevision, DateTimeOffset? checkedAt, string lastError)
        {
            var path = picked.TryGetValue(field, out var chosen) && !string.IsNullOrWhiteSpace(chosen) ? chosen : saved;
            var update = !string.IsNullOrWhiteSpace(targetRevision) &&
                         (string.IsNullOrWhiteSpace(installedRevision) ||
                          !string.Equals(installedRevision.Trim(), targetRevision.Trim(), StringComparison.OrdinalIgnoreCase));
            return new
            {
                id,
                path = string.IsNullOrWhiteSpace(path) ? null : path.Trim(),
                installed = Join(installedVersion, installedRevision),
                available = update ? Join(targetVersion, targetRevision) : null,
                updateAvailable = update,
                checkedAt = checkedAt?.ToString("O", CultureInfo.InvariantCulture),
                failed = !string.IsNullOrWhiteSpace(lastError)
            };
        }

        var sections = new[]
        {
            Section("xs", "xs-path", settings.TeklaStandardLocalPath, settings.TeklaStandardInstalledVersion,
                settings.TeklaStandardInstalledRevision, settings.TeklaStandardTargetVersion, settings.TeklaStandardTargetRevision,
                settings.TeklaStandardLastCheckUtc, settings.TeklaStandardLastError),
            Section("ext", "ext-path", settings.TeklaExtensionsLocalPath, settings.TeklaExtensionsInstalledVersion,
                settings.TeklaExtensionsInstalledRevision, settings.TeklaExtensionsTargetVersion, settings.TeklaExtensionsTargetRevision,
                settings.TeklaExtensionsLastCheckUtc, settings.TeklaExtensionsLastError),
            Section("lib", "lib-path", settings.TeklaLibrariesLocalPath, settings.TeklaLibrariesInstalledVersion,
                settings.TeklaLibrariesInstalledRevision, settings.TeklaLibrariesTargetVersion, settings.TeklaLibrariesTargetRevision,
                settings.TeklaLibrariesLastCheckUtc, settings.TeklaLibrariesLastError)
        };
        return new { source = TeklaStandardSource, sections };
    }

    /// <summary>«2026.03.0 r1»: the manifest version with its revision, or only one of them when the other is unknown.</summary>
    public static string? Join(string? version, string? revision)
    {
        var v = version?.Trim();
        var r = revision?.Trim();
        if (string.IsNullOrEmpty(v)) return string.IsNullOrEmpty(r) ? null : r;
        if (string.IsNullOrEmpty(r) || v.EndsWith(" " + r, StringComparison.OrdinalIgnoreCase)) return v;
        return v + " " + r;
    }

    /// <summary>Available version and «Что нового» of the pending update; the list is empty when the manifest has none.</summary>
    public static object Update(DesktopUpdateInfo info, bool available) => new
    {
        available,
        version = available ? DesktopUpdateNotes.SafeVersion(info.Version) : null,
        notes = available ? DesktopUpdateNotes.Lines(info.Notes).ToArray() : Array.Empty<string>(),
        checkedAt = info.CheckedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
        checkFailed = info.CheckFailed
    };

    public static object? Confirmation(PendingPageConfirmation? pending) => pending is null ? null : new
    {
        id = pending.Id,
        title = pending.Request.Title,
        text = pending.Request.Text,
        items = pending.Request.Items.ToArray(),
        note = pending.Request.Note,
        primary = pending.Request.Primary,
        danger = pending.Request.Danger,
        cancel = pending.Request.Cancel
    };
}
