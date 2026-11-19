using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Connector.Updates;

/// <summary>The installer named by a verified release description.</summary>
public sealed record ReleaseInstaller(string FileName, long Size, string Sha256);

/// <summary>A release description (schema 2) whose exact bytes were verified with the pinned key.</summary>
public sealed record ReleaseDescription(
    string Channel,
    ReleaseVersion Version,
    DateTimeOffset PublishedAtUtc,
    bool Paused,
    ReleaseVersion MinimumVersion,
    ReleaseInstaller Installer,
    IReadOnlyList<string> Notes);

/// <summary>Contract constants of the update catalog (§3.5 of the update design).</summary>
public static class ReleaseCatalogContract
{
    public const int SchemaVersion = 2;
    public const string ApplicationId = "Structura.PlatformConnector";
    public const string DefaultChannel = "stable";
    public const int MaximumDescriptionBytes = 64 * 1024;
    public const int SignatureBytes = 64;
    /// <summary>Upper bound of an installer the catalog may announce (the 1.1.x installers are about 400 MB).</summary>
    public const long MaximumInstallerBytes = 2L * 1024 * 1024 * 1024;
    public const int MaximumNotes = 20;
    public const int MaximumNoteLength = 300;

    private static readonly Regex ChannelPattern = new("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromMilliseconds(100));

    public static bool IsValidChannel(string? channel) => channel is not null && ChannelPattern.IsMatch(channel);

    public static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    /// <summary>The only installer file name a release may carry: <c>PlatformConnector-&lt;version&gt;.exe</c>.</summary>
    public static string InstallerFileName(ReleaseVersion version) => "PlatformConnector-" + version + ".exe";
}

/// <summary>
/// Verifies a release description: signature over the exact received bytes first, then a strict
/// schema-2 parse (unknown, missing and duplicate properties are refused, every value is typed and bounded).
/// Whether the verified release should be installed is decided by <see cref="ReleasePolicy"/>.
/// </summary>
public sealed class ReleaseDescriptionVerifier : IDisposable
{
    private static readonly string[] RootProperties =
        ["schemaVersion", "applicationId", "channel", "version", "publishedAtUtc", "paused", "minimumVersion", "installer", "notes"];
    private static readonly string[] InstallerProperties = ["fileName", "size", "sha256"];
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 4
    };

    private static readonly string[] UtcFormats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];

    private readonly ECDsa _key;

    /// <summary>Production verifier: the key compiled into the product, nothing else.</summary>
    public static ReleaseDescriptionVerifier CreatePinned() => new(PinnedUpdateKey.Load());

    /// <summary>Tests construct the verifier with their own key; production code has no path to this constructor.</summary>
    internal ReleaseDescriptionVerifier(ECDsa key)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
        PinnedUpdateKey.RequireP256(_key);
    }

    public ReleaseDescription Verify(ReadOnlySpan<byte> description, ReadOnlySpan<byte> signature, string requestedChannel)
    {
        if (!ReleaseCatalogContract.IsValidChannel(requestedChannel))
            throw new UpdateRejectedException(UpdateErrorCodes.ChannelInvalid);
        if (description.Length == 0 || description.Length > ReleaseCatalogContract.MaximumDescriptionBytes)
            throw new UpdateRejectedException(UpdateErrorCodes.DescriptionTooLarge);
        if (signature.Length != ReleaseCatalogContract.SignatureBytes ||
            !_key.VerifyData(description, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new UpdateRejectedException(UpdateErrorCodes.SignatureInvalid);

        // Authentic bytes from here on; still parsed as strictly as if they were not.
        JsonDocument document;
        try { document = JsonDocument.Parse(description.ToArray(), ParseOptions); }
        catch (JsonException ex) { throw new UpdateRejectedException(UpdateErrorCodes.DescriptionInvalid, ex); }
        using (document)
        {
            var root = document.RootElement;
            RequireExactProperties(root, RootProperties);
            if (RequireInt64(root, "schemaVersion") != ReleaseCatalogContract.SchemaVersion ||
                !string.Equals(RequireString(root, "applicationId"), ReleaseCatalogContract.ApplicationId, StringComparison.Ordinal))
                throw Invalid();

            var channel = RequireString(root, "channel");
            if (!ReleaseCatalogContract.IsValidChannel(channel)) throw Invalid();
            // A pilot release must never be accepted by a stable client and vice versa.
            if (!string.Equals(channel, requestedChannel, StringComparison.Ordinal))
                throw new UpdateRejectedException(UpdateErrorCodes.ChannelMismatch);

            if (!ReleaseVersion.TryParse(RequireString(root, "version"), out var version) ||
                !ReleaseVersion.TryParse(RequireString(root, "minimumVersion"), out var minimum) || minimum > version)
                throw Invalid();

            var published = ParseUtc(RequireString(root, "publishedAtUtc"));
            var paused = RequireBoolean(root, "paused");

            var installerElement = root.GetProperty("installer");
            RequireExactProperties(installerElement, InstallerProperties);
            var fileName = RequireString(installerElement, "fileName");
            if (!string.Equals(fileName, ReleaseCatalogContract.InstallerFileName(version), StringComparison.Ordinal))
                throw Invalid();
            var size = RequireInt64(installerElement, "size");
            if (size <= 0 || size > ReleaseCatalogContract.MaximumInstallerBytes) throw Invalid();
            var sha256 = RequireString(installerElement, "sha256");
            if (!ReleaseCatalogContract.IsSha256Hex(sha256)) throw Invalid();

            var notesElement = root.GetProperty("notes");
            if (notesElement.ValueKind != JsonValueKind.Array || notesElement.GetArrayLength() > ReleaseCatalogContract.MaximumNotes)
                throw Invalid();
            var notes = new List<string>();
            foreach (var item in notesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) throw Invalid();
                string note;
                try { note = item.GetString()!; }
                catch (InvalidOperationException) { throw Invalid(); } // lone surrogate in an escape
                if (note.Length is 0 or > ReleaseCatalogContract.MaximumNoteLength || note.Any(char.IsControl)) throw Invalid();
                notes.Add(note);
            }

            return new ReleaseDescription(channel, version, published, paused, minimum,
                new ReleaseInstaller(fileName, size, sha256.ToLowerInvariant()), notes);
        }
    }

    public void Dispose() => _key.Dispose();

    private static UpdateRejectedException Invalid() => new(UpdateErrorCodes.DescriptionInvalid);

    private static void RequireExactProperties(JsonElement element, string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            // JsonDocument keeps duplicates; the first one would silently win in GetProperty.
            if (!seen.Add(property.Name) || Array.IndexOf(expected, property.Name) < 0) throw Invalid();
        }
        if (seen.Count != expected.Length) throw Invalid();
    }

    private static string RequireString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        return value.GetString()!;
    }

    private static long RequireInt64(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) throw Invalid();
        // 2.0 and 2e0 parse as integers too; only the plain spelling is accepted.
        var raw = value.GetRawText();
        if (raw.Any(character => character is < '0' or > '9')) throw Invalid();
        return number;
    }

    private static bool RequireBoolean(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid()
        };
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        if (!DateTimeOffset.TryParseExact(value, UtcFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            throw Invalid();
        return parsed;
    }
}

/// <summary>What to do with a verified release, given the installed version and the versions that already failed.</summary>
public enum ReleaseDecision
{
    Install,
    UpToDate,
    Paused,
    BelowMinimumVersion,
    MarkedProblem
}

public static class ReleasePolicy
{
    public static ReleaseDecision Decide(ReleaseDescription release, ReleaseVersion installed, IReadOnlyCollection<ReleaseVersion> problemVersions)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(problemVersions);
        // Strictly newer only: an old (still validly signed) description can never roll a client back.
        if (release.Version <= installed) return ReleaseDecision.UpToDate;
        if (release.Paused) return ReleaseDecision.Paused;
        // minimumVersion is the oldest installation this release may be applied to automatically.
        if (installed < release.MinimumVersion) return ReleaseDecision.BelowMinimumVersion;
        if (problemVersions.Contains(release.Version)) return ReleaseDecision.MarkedProblem;
        return ReleaseDecision.Install;
    }
}
