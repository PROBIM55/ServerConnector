using Connector.Updates;

namespace Connector.Desktop.Services;

/// <summary>
/// What the page shows about application updates. The application never downloads or installs anything:
/// it only reads the status of the machine update service (<c>update-status.json</c>) and turns it into
/// display facts — one of the five states below, or «unavailable» when the service does not work.
/// </summary>
public sealed record DesktopUpdateState(
    string Kind,
    string Text,
    string? Version,
    int? Percent,
    string? ErrorCode,
    IReadOnlyList<string> Notes,
    DateTimeOffset? CheckedAtUtc,
    bool ServiceAvailable)
{
    /// <summary>«Installing» is believed only while the service heartbeat (≤ 1 min while installing) is this fresh.</summary>
    public static readonly TimeSpan InstallingFreshness = TimeSpan.FromMinutes(3);
    /// <summary>Without any heartbeat for this long (it is written at least every 15 minutes) the service is considered broken.</summary>
    public static readonly TimeSpan ServiceSilenceLimit = TimeSpan.FromDays(1);

    public static readonly DesktopUpdateState Unknown = Unavailable("Служба обновления не работает — новые версии не устанавливаются.");

    /// <summary>A newer version is known to the service (available, downloading, ready, installing or retrying).</summary>
    public bool HasNewerVersion => Version is not null && Kind is "available" or "downloading" or "installing" or "failed";

    /// <summary>The application closes itself for the update only in this state.</summary>
    public bool CloseForInstallation => Kind == "installing";

    private static DesktopUpdateState Unavailable(string text) =>
        new("unavailable", text + " Скачайте и установите коннектор вручную: " + UpdateLocations.ManualDownloadUrl,
            null, null, null, Array.Empty<string>(), null, false);

    /// <param name="runningVersion">Version of this application (x.y.z).</param>
    /// <param name="serviceRunning">State of the Windows service: true running, false installed but not running or missing, null unknown.</param>
    public static DesktopUpdateState From(UpdateStatus? status, DateTimeOffset now, string? runningVersion = null, bool? serviceRunning = null)
    {
        if (serviceRunning == false) return Unknown;
        if (status is null || now - status.WrittenAtUtc > ServiceSilenceLimit) return Unknown;
        var version = DesktopUpdateNotes.SafeVersion(status.AvailableVersion);
        // Unknown own version or unparsable offered version: never treated as newer (the window is not closed).
        var newer = version is not null && ReleaseVersion.TryParse(runningVersion, out var running) &&
                    ReleaseVersion.TryParse(version, out var offered) && offered > running;
        var notes = status.AvailableNotes.Select(DesktopUpdateNotes.SafeLine).OfType<string>().Take(8).ToArray();
        var code = status.LastErrorCode;
        switch (status.Phase)
        {
            case UpdatePhase.Installing:
                // A stale «installing» (service stopped, crashed or quarantined mid-way) must never close the application.
                if (now - status.WrittenAtUtc > InstallingFreshness)
                    return Unavailable("Служба обновления не отвечает — установка не завершилась.");
                if (!newer) return new("current", "Установлена актуальная версия.", null, null, null, Array.Empty<string>(), status.CheckedAtUtc, true);
                return new("installing", "Устанавливается" + (version is null ? "" : " версия " + version) + ", приложение перезапустится.",
                    version, null, null, notes, status.CheckedAtUtc, true);
            case UpdatePhase.Downloading:
                return new("downloading", "Загружается " + (version ?? "новая версия") + ", " + (status.DownloadPercent ?? 0) + " %.",
                    version, status.DownloadPercent ?? 0, null, notes, status.CheckedAtUtc, true);
            case UpdatePhase.Failed:
                return new("failed", "Не удалось обновить (" + (code ?? "ошибка") + ") — повторим автоматически.",
                    version, null, code, notes, status.CheckedAtUtc, true);
        }
        if (version is not null)
            return new("available", "Доступна версия " + version + " — установится автоматически.", version, null, code, notes, status.CheckedAtUtc, true);
        // A failed check (network, catalog) without a known newer version: nothing is wrong with this installation.
        return code is not null && code != UpdateErrorCodes.DownloadBusy
            ? new("current", "Не удалось проверить обновления (" + code + ") — повторим автоматически.", null, null, code, Array.Empty<string>(), status.CheckedAtUtc, true)
            : new("current", "Установлена актуальная версия.", null, null, null, Array.Empty<string>(), status.CheckedAtUtc, true);
    }
}

public static class DesktopUpdateNotes
{
    private const int MaximumLines = 8;
    private const int MaximumLineLength = 200;

    /// <summary>Splits release notes into short list lines; markdown bullets and headings are dropped, links are not kept.</summary>
    public static IReadOnlyList<string> Lines(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return Array.Empty<string>();
        var lines = new List<string>();
        foreach (var raw in notes.Replace("\r\n", "\n").Split('\n'))
        {
            if (SafeLine(raw) is not { } line) continue;
            lines.Add(line);
            if (lines.Count == MaximumLines) break;
        }
        return lines;
    }

    /// <summary>One display line: bullets trimmed, no control characters, no links, bounded length.</summary>
    public static string? SafeLine(string? raw)
    {
        var line = (raw ?? "").Trim().TrimStart('-', '*', '•', '#', ' ', '\t').Trim();
        if (line.Length == 0 || line.Any(char.IsControl) || line.Contains("://", StringComparison.Ordinal)) return null;
        return line.Length <= MaximumLineLength ? line : line[..(MaximumLineLength - 1)] + "…";
    }

    /// <summary>A version string the page may show: digits, dots and a short pre-release suffix only.</summary>
    public static string? SafeVersion(string? version)
    {
        var value = version?.Trim();
        return value is { Length: > 0 and <= 32 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-')
            ? value
            : null;
    }
}
