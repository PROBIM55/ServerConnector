using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Connector.Updates;

public enum UpdatePhase
{
    Idle,
    Downloading,
    Ready,
    Installing,
    Failed
}

/// <summary>The last completed automatic update, shown once by the application after its restart.</summary>
public sealed record CompletedUpdate(string Version, DateTimeOffset CompletedAtUtc, IReadOnlyList<string> Notes);

/// <summary>
/// The only thing the service tells the application (<c>update-status.json</c>, readable by users,
/// writable only by SYSTEM and administrators). Display facts only: versions, phase, percent, times, a code.
/// </summary>
public sealed record UpdateStatus(
    string? InstalledVersion,
    string? AvailableVersion,
    UpdatePhase Phase,
    int? DownloadPercent,
    DateTimeOffset? ScheduledInstallAtUtc,
    string? LastErrorCode,
    DateTimeOffset? CheckedAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    IReadOnlyList<string> AvailableNotes,
    CompletedUpdate? LastUpdate,
    DateTimeOffset WrittenAtUtc)
{
    public const int SchemaVersion = 1;
    public const int MaximumBytes = 64 * 1024;
}

public static class UpdateStatusCodec
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 4
    };

    public static byte[] Serialize(UpdateStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", UpdateStatus.SchemaVersion);
            WriteOptional(writer, "installedVersion", status.InstalledVersion);
            WriteOptional(writer, "availableVersion", status.AvailableVersion);
            writer.WriteString("phase", PhaseName(status.Phase));
            if (status.DownloadPercent is { } percent) writer.WriteNumber("downloadPercent", Math.Clamp(percent, 0, 100));
            else writer.WriteNull("downloadPercent");
            WriteOptional(writer, "scheduledInstallAtUtc", status.ScheduledInstallAtUtc);
            WriteOptional(writer, "lastErrorCode", UpdateErrorCodes.IsKnown(status.LastErrorCode) ? status.LastErrorCode : null);
            WriteOptional(writer, "checkedAtUtc", status.CheckedAtUtc);
            WriteOptional(writer, "nextAttemptAtUtc", status.NextAttemptAtUtc);
            WriteNotes(writer, "availableNotes", status.AvailableNotes);
            if (status.LastUpdate is { } last)
            {
                writer.WriteStartObject("lastUpdate");
                writer.WriteString("version", last.Version);
                writer.WriteString("completedAtUtc", Utc(last.CompletedAtUtc));
                WriteNotes(writer, "notes", last.Notes);
                writer.WriteEndObject();
            }
            else writer.WriteNull("lastUpdate");
            writer.WriteString("writtenAtUtc", Utc(status.WrittenAtUtc));
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>Reads a status file; anything malformed, oversized or of another schema yields null.</summary>
    public static UpdateStatus? TryDeserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > UpdateStatus.MaximumBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), ReadOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) || schemaVersion != UpdateStatus.SchemaVersion)
                return null;
            if (!TryPhase(OptionalString(root, "phase"), out var phase)) return null;
            int? percent = root.TryGetProperty("downloadPercent", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var value)
                ? Math.Clamp(value, 0, 100) : null;
            CompletedUpdate? last = null;
            if (root.TryGetProperty("lastUpdate", out var lastElement) && lastElement.ValueKind == JsonValueKind.Object &&
                Version(OptionalString(lastElement, "version")) is { } lastVersion &&
                OptionalTime(lastElement, "completedAtUtc") is { } completed)
                last = new CompletedUpdate(lastVersion, completed, Notes(lastElement, "notes"));
            return new UpdateStatus(
                Version(OptionalString(root, "installedVersion")),
                Version(OptionalString(root, "availableVersion")),
                phase,
                percent,
                OptionalTime(root, "scheduledInstallAtUtc"),
                OptionalString(root, "lastErrorCode") is { } code && UpdateErrorCodes.IsKnown(code) ? code : null,
                OptionalTime(root, "checkedAtUtc"),
                OptionalTime(root, "nextAttemptAtUtc"),
                Notes(root, "availableNotes"),
                last,
                OptionalTime(root, "writtenAtUtc") ?? DateTimeOffset.MinValue);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string PhaseName(UpdatePhase phase) => phase switch
    {
        UpdatePhase.Idle => "idle",
        UpdatePhase.Downloading => "downloading",
        UpdatePhase.Ready => "ready",
        UpdatePhase.Installing => "installing",
        UpdatePhase.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };

    private static bool TryPhase(string? value, out UpdatePhase phase)
    {
        phase = value switch
        {
            "idle" => UpdatePhase.Idle,
            "downloading" => UpdatePhase.Downloading,
            "ready" => UpdatePhase.Ready,
            "installing" => UpdatePhase.Installing,
            "failed" => UpdatePhase.Failed,
            _ => (UpdatePhase)(-1)
        };
        return (int)phase >= 0;
    }

    private static string? Version(string? value) => ReleaseVersion.TryParse(value, out var version) ? version.ToString() : null;

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? OptionalTime(JsonElement element, string name) =>
        OptionalString(element, name) is { } text &&
        DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private static IReadOnlyList<string> Notes(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var notes) || notes.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var lines = new List<string>();
        foreach (var item in notes.EnumerateArray())
        {
            if (lines.Count == ReleaseCatalogContract.MaximumNotes) break;
            if (item.ValueKind != JsonValueKind.String) continue;
            var line = item.GetString()!;
            if (line.Length is > 0 and <= ReleaseCatalogContract.MaximumNoteLength && !line.Any(char.IsControl)) lines.Add(line);
        }
        return lines;
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } time) writer.WriteString(name, Utc(time));
        else writer.WriteNull(name);
    }

    private static void WriteNotes(Utf8JsonWriter writer, string name, IReadOnlyList<string> notes)
    {
        writer.WriteStartArray(name);
        foreach (var note in notes.Take(ReleaseCatalogContract.MaximumNotes)) writer.WriteStringValue(note);
        writer.WriteEndArray();
    }

    internal static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>
/// The two flag files a user's application may write into <see cref="UpdateLocations.Signals"/>:
/// <c>busy-&lt;user SID&gt;.flag</c> («занят до») and <c>check-&lt;user SID&gt;.flag</c> («проверь сейчас»).
/// Content is one UTC timestamp, nothing else; the service reads at most <see cref="MaximumBytes"/>.
/// </summary>
public static class UpdateSignalFiles
{
    public const int MaximumBytes = 1024;
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static string BusyFileName(string userSid) => "busy-" + RequireSid(userSid) + ".flag";
    public static string CheckFileName(string userSid) => "check-" + RequireSid(userSid) + ".flag";

    public static byte[] Encode(DateTimeOffset timestamp) =>
        Encoding.ASCII.GetBytes(timestamp.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));

    /// <summary>
    /// Parses exactly one timestamp (optionally followed by CR/LF). Anything else — other text, a BOM,
    /// several values, more than <see cref="MaximumBytes"/> — is treated as no signal.
    /// </summary>
    public static DateTimeOffset? TryDecode(ReadOnlySpan<byte> content)
    {
        if (content.Length is 0 or > MaximumBytes) return null;
        var end = content.Length;
        while (end > 0 && content[end - 1] is (byte)'\r' or (byte)'\n') end--;
        if (end != 20) return null;
        Span<char> text = stackalloc char[20];
        for (var index = 0; index < 20; index++)
        {
            var b = content[index];
            if (b is < 0x20 or > 0x7E) return null;
            text[index] = (char)b;
        }
        return DateTimeOffset.TryParseExact(text, TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>A Windows SID in its canonical string form, limited so a file name stays short.</summary>
    public static bool IsSid(string? value)
    {
        if (value is null || value.Length is < 7 or > 184 || !value.StartsWith("S-1-", StringComparison.Ordinal)) return false;
        var previousDash = false;
        for (var index = 4; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '-')
            {
                if (previousDash) return false;
                previousDash = true;
            }
            else if (character is >= '0' and <= '9') previousDash = false;
            else return false;
        }
        return !previousDash;
    }

    private static string RequireSid(string value) =>
        IsSid(value) ? value : throw new ArgumentException("A canonical SID is required.", nameof(value));
}
