using System.Globalization;
using System.Text.Json;
using Connector.Updates;

namespace Connector.Updater;

internal sealed record SessionRecord(int SessionId, string UserSid);

/// <summary>How often the installer of one exact (version, sha256) failed verification.</summary>
internal sealed record HashMismatchRecord(ReleaseVersion Version, string Sha256, int Count);

/// <summary>The verified release being brought onto this machine.</summary>
internal sealed class UpdateTarget
{
    public required ReleaseVersion Version { get; init; }
    public required string Channel { get; init; }
    public required ReleaseInstaller Installer { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public DateTimeOffset? ReadyAtUtc { get; set; }
    /// <summary>Failed installation attempts of this version.</summary>
    public int Failures { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
}

/// <summary>Written before the installer starts; it is what the next service instance (possibly the new version) resolves.</summary>
internal sealed class PendingInstall
{
    public required ReleaseVersion Version { get; init; }
    public ReleaseVersion? PreviousVersion { get; init; }
    public required int Attempt { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required IReadOnlyList<SessionRecord> Sessions { get; init; }
    public int? InstallerProcessId { get; set; }
    public DateTimeOffset? InstallerStartedAtUtc { get; set; }
    public int? ExitCode { get; set; }
    /// <summary>The attempt was already counted as failed (installer past the absolute limit), but is still awaited.</summary>
    public bool FailureCounted { get; set; }
}

/// <summary>Service state in the SYSTEM-only state directory.</summary>
internal sealed class UpdaterState
{
    public DateTimeOffset? LastCheckAtUtc { get; set; }
    public DateTimeOffset? NextCheckAtUtc { get; set; }
    public int ConsecutiveCheckFailures { get; set; }
    /// <summary>Broken downloads in a row; drives the growing retry pause, never the installation attempts.</summary>
    public int ConsecutiveDownloadFailures { get; set; }
    /// <summary>Failure of the check itself (network, signature…): shown, the phase is not changed by it.</summary>
    public string? CheckErrorCode { get; set; }
    /// <summary>Failure of bringing the target onto the machine (download, installation, refused release): phase «failed».</summary>
    public string? FailureCode { get; set; }
    public UpdateTarget? Target { get; set; }
    public List<ReleaseVersion> ProblemVersions { get; } = new();
    public PendingInstall? Pending { get; set; }
    public HashMismatchRecord? HashMismatch { get; set; }
    public CompletedUpdate? LastUpdate { get; set; }
}

internal static class UpdaterStateCodec
{
    private const int SchemaVersion = 1;
    private const int MaximumBytes = 256 * 1024;

    public static byte[] Serialize(UpdaterState state)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", SchemaVersion);
            Time(w, "lastCheckAtUtc", state.LastCheckAtUtc);
            Time(w, "nextCheckAtUtc", state.NextCheckAtUtc);
            w.WriteNumber("consecutiveCheckFailures", state.ConsecutiveCheckFailures);
            w.WriteNumber("consecutiveDownloadFailures", state.ConsecutiveDownloadFailures);
            Text(w, "checkErrorCode", state.CheckErrorCode);
            Text(w, "failureCode", state.FailureCode);
            if (state.Target is { } t)
            {
                w.WriteStartObject("target");
                w.WriteString("version", t.Version.ToString());
                w.WriteString("channel", t.Channel);
                w.WriteString("fileName", t.Installer.FileName);
                w.WriteNumber("size", t.Installer.Size);
                w.WriteString("sha256", t.Installer.Sha256);
                Notes(w, "notes", t.Notes);
                Time(w, "readyAtUtc", t.ReadyAtUtc);
                w.WriteNumber("failures", t.Failures);
                Time(w, "nextAttemptAtUtc", t.NextAttemptAtUtc);
                w.WriteEndObject();
            }
            else w.WriteNull("target");
            w.WriteStartArray("problemVersions");
            foreach (var version in state.ProblemVersions) w.WriteStringValue(version.ToString());
            w.WriteEndArray();
            if (state.Pending is { } p)
            {
                w.WriteStartObject("pending");
                w.WriteString("version", p.Version.ToString());
                Text(w, "previousVersion", p.PreviousVersion?.ToString());
                w.WriteNumber("attempt", p.Attempt);
                Time(w, "startedAtUtc", p.StartedAtUtc);
                w.WriteStartArray("sessions");
                foreach (var session in p.Sessions)
                {
                    w.WriteStartObject();
                    w.WriteNumber("sessionId", session.SessionId);
                    w.WriteString("userSid", session.UserSid);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                if (p.InstallerProcessId is { } pid) w.WriteNumber("installerProcessId", pid); else w.WriteNull("installerProcessId");
                Time(w, "installerStartedAtUtc", p.InstallerStartedAtUtc);
                if (p.ExitCode is { } code) w.WriteNumber("exitCode", code); else w.WriteNull("exitCode");
                w.WriteBoolean("failureCounted", p.FailureCounted);
                w.WriteEndObject();
            }
            else w.WriteNull("pending");
            if (state.HashMismatch is { } mismatch)
            {
                w.WriteStartObject("hashMismatch");
                w.WriteString("version", mismatch.Version.ToString());
                w.WriteString("sha256", mismatch.Sha256);
                w.WriteNumber("count", mismatch.Count);
                w.WriteEndObject();
            }
            else w.WriteNull("hashMismatch");
            if (state.LastUpdate is { } last)
            {
                w.WriteStartObject("lastUpdate");
                w.WriteString("version", last.Version);
                Time(w, "completedAtUtc", last.CompletedAtUtc);
                Notes(w, "notes", last.Notes);
                w.WriteEndObject();
            }
            else w.WriteNull("lastUpdate");
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>Null for an unreadable or foreign file; the caller starts from a clean state then.</summary>
    public static UpdaterState? TryDeserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 6 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Int(root, "schemaVersion") != SchemaVersion) return null;
            var state = new UpdaterState
            {
                LastCheckAtUtc = Time(root, "lastCheckAtUtc"),
                NextCheckAtUtc = Time(root, "nextCheckAtUtc"),
                ConsecutiveCheckFailures = Math.Clamp(Int(root, "consecutiveCheckFailures") ?? 0, 0, 1000),
                ConsecutiveDownloadFailures = Math.Clamp(Int(root, "consecutiveDownloadFailures") ?? 0, 0, 1000),
                CheckErrorCode = Code(root, "checkErrorCode"),
                FailureCode = Code(root, "failureCode")
            };
            if (root.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.Object)
            {
                if (!ReleaseVersion.TryParse(Str(t, "version"), out var version) || Str(t, "channel") is not { } channel ||
                    !ReleaseCatalogContract.IsValidChannel(channel) ||
                    Str(t, "fileName") is not { } fileName || fileName != ReleaseCatalogContract.InstallerFileName(version) ||
                    Long(t, "size") is not { } size || size <= 0 || size > ReleaseCatalogContract.MaximumInstallerBytes ||
                    Str(t, "sha256") is not { } sha || !ReleaseCatalogContract.IsSha256Hex(sha))
                    return null;
                state.Target = new UpdateTarget
                {
                    Version = version, Channel = channel, Installer = new ReleaseInstaller(fileName, size, sha.ToLowerInvariant()),
                    Notes = NotesOf(t, "notes"), ReadyAtUtc = Time(t, "readyAtUtc"),
                    Failures = Math.Clamp(Int(t, "failures") ?? 0, 0, 100), NextAttemptAtUtc = Time(t, "nextAttemptAtUtc")
                };
            }
            if (root.TryGetProperty("problemVersions", out var problems) && problems.ValueKind == JsonValueKind.Array)
                foreach (var item in problems.EnumerateArray().Take(64))
                    if (item.ValueKind == JsonValueKind.String && ReleaseVersion.TryParse(item.GetString(), out var problem))
                        state.ProblemVersions.Add(problem);
            if (root.TryGetProperty("pending", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                if (!ReleaseVersion.TryParse(Str(p, "version"), out var version) || Time(p, "startedAtUtc") is not { } started) return null;
                var sessions = new List<SessionRecord>();
                if (p.TryGetProperty("sessions", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var item in list.EnumerateArray().Take(64))
                        if (item.ValueKind == JsonValueKind.Object && Int(item, "sessionId") is { } id and >= 0 &&
                            Str(item, "userSid") is { } sid && UpdateSignalFiles.IsSid(sid))
                            sessions.Add(new SessionRecord(id, sid));
                state.Pending = new PendingInstall
                {
                    Version = version,
                    PreviousVersion = ReleaseVersion.TryParse(Str(p, "previousVersion"), out var previous) ? previous : null,
                    Attempt = Math.Clamp(Int(p, "attempt") ?? 1, 1, 100),
                    StartedAtUtc = started,
                    Sessions = sessions,
                    InstallerProcessId = Int(p, "installerProcessId"),
                    InstallerStartedAtUtc = Time(p, "installerStartedAtUtc"),
                    ExitCode = Int(p, "exitCode"),
                    FailureCounted = p.TryGetProperty("failureCounted", out var counted) && counted.ValueKind == JsonValueKind.True
                };
            }
            if (root.TryGetProperty("hashMismatch", out var hm) && hm.ValueKind == JsonValueKind.Object &&
                ReleaseVersion.TryParse(Str(hm, "version"), out var hmVersion) && Str(hm, "sha256") is { } hmSha &&
                ReleaseCatalogContract.IsSha256Hex(hmSha) && Int(hm, "count") is { } hmCount and > 0)
                state.HashMismatch = new HashMismatchRecord(hmVersion, hmSha.ToLowerInvariant(), Math.Min(hmCount, 1000));
            if (root.TryGetProperty("lastUpdate", out var last) && last.ValueKind == JsonValueKind.Object &&
                ReleaseVersion.TryParse(Str(last, "version"), out var lastVersion) && Time(last, "completedAtUtc") is { } completed)
                state.LastUpdate = new CompletedUpdate(lastVersion.ToString(), completed, NotesOf(last, "notes"));
            return state;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Time(Utf8JsonWriter w, string name, DateTimeOffset? value)
    {
        if (value is { } time) w.WriteString(name, time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        else w.WriteNull(name);
    }

    private static void Text(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteString(name, value);
    }

    private static void Notes(Utf8JsonWriter w, string name, IReadOnlyList<string> notes)
    {
        w.WriteStartArray(name);
        foreach (var note in notes.Take(ReleaseCatalogContract.MaximumNotes)) w.WriteStringValue(note);
        w.WriteEndArray();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        Str(e, name) is { } text && DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t.ToUniversalTime() : null;

    private static string? Code(JsonElement e, string name) => Str(e, name) is { } code && UpdateErrorCodes.IsKnown(code) ? code : null;

    private static IReadOnlyList<string> NotesOf(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var notes) || notes.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return notes.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(line => line.Length is > 0 and <= ReleaseCatalogContract.MaximumNoteLength && !line.Any(char.IsControl))
            .Take(ReleaseCatalogContract.MaximumNotes)
            .ToArray();
    }
}
