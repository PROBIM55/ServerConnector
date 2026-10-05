using System.Text.Json;

namespace Platform.Connector.Compute.Agr;

public sealed record AgrBlenderRuntimeManifest(
    int SchemaVersion,
    string RuntimeId,
    string Version,
    string Platform,
    Uri ArchiveUrl,
    string ArchiveFileName,
    long ArchiveSizeBytes,
    string ArchiveSha256,
    string ExecutableRelativePath,
    string WorkerProtocolVersion)
{
    public const string ExpectedPlatform = "windows-x64";

    public static AgrBlenderRuntimeManifest Load(string manifestPath)
    {
        using var stream = File.OpenRead(manifestPath);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var manifest = new AgrBlenderRuntimeManifest(
            ReadInt(root, "schemaVersion"),
            ReadString(root, "runtimeId"),
            ReadString(root, "version"),
            ReadString(root, "platform"),
            new Uri(ReadString(root, "archiveUrl"), UriKind.Absolute),
            ReadString(root, "archiveFileName"),
            ReadLong(root, "archiveSizeBytes"),
            ReadString(root, "archiveSha256").ToLowerInvariant(),
            ReadString(root, "executableRelativePath"),
            ReadString(root, "workerProtocolVersion"));

        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("Unsupported Blender runtime manifest schema.");
        if (!string.Equals(Version, AgrComputeOptions.RuntimeVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Expected Blender {AgrComputeOptions.RuntimeVersion}, got {Version}.");
        }
        if (!string.Equals(Platform, ExpectedPlatform, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported Blender runtime platform: {Platform}.");
        }
        if (!string.Equals(WorkerProtocolVersion, AgrComputeOptions.WorkerProtocolVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Blender runtime and AGR worker protocol versions do not match.");
        }
        if (!string.Equals(ArchiveUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Blender runtime archive must use HTTPS.");
        }
        if (!string.Equals(ArchiveUrl.Host, "download.blender.org", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Blender runtime archive must come from download.blender.org.");
        }
        if (ArchiveSizeBytes <= 0) throw new InvalidDataException("Runtime archive size must be positive.");
        if (ArchiveSha256.Length != 64 || ArchiveSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Runtime archive SHA-256 is invalid.");
        }
        ValidateRelativePath(ArchiveFileName, "archive file name");
        ValidateRelativePath(ExecutableRelativePath, "runtime executable path");
    }

    private static void ValidateRelativePath(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            throw new InvalidDataException($"Blender {label} must be relative.");
        }

        var normalized = value.Replace('\\', '/');
        if (normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"Blender {label} contains an unsafe segment.");
        }
    }

    private static string ReadString(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? throw new InvalidDataException($"Runtime manifest property {property} is empty.")
            : throw new InvalidDataException($"Runtime manifest property {property} is required.");

    private static int ReadInt(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.TryGetInt32(out var result)
            ? result
            : throw new InvalidDataException($"Runtime manifest property {property} must be an integer.");

    private static long ReadLong(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.TryGetInt64(out var result)
            ? result
            : throw new InvalidDataException($"Runtime manifest property {property} must be an integer.");
}
