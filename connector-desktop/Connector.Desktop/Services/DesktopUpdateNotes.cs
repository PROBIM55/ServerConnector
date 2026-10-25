namespace Connector.Desktop.Services;

/// <summary>
/// The pending application update as the page shows it: version, «Что нового» lines from the update manifest
/// (empty when the manifest has none) and the time of the last successful check.
/// </summary>
public sealed record DesktopUpdateInfo(string? Version, string? Notes, DateTimeOffset? CheckedAtUtc, bool CheckFailed)
{
    public static readonly DesktopUpdateInfo None = new(null, null, null, false);
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
            var line = raw.Trim().TrimStart('-', '*', '•', '#', ' ', '\t').Trim();
            if (line.Length == 0 || line.Any(char.IsControl) || line.Contains("://", StringComparison.Ordinal)) continue;
            lines.Add(line.Length <= MaximumLineLength ? line : line[..(MaximumLineLength - 1)] + "…");
            if (lines.Count == MaximumLines) break;
        }
        return lines;
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
