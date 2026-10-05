using System.Text.Json;

namespace Connector.JobModules;

internal sealed record ConverterJobPayload(string InputPath, string OutputDirectory, string Profile)
{
    public static bool TryRead(JsonElement payload, out ConverterJobPayload? value, out string? error)
    {
        value = null;
        error = null;
        if (payload.ValueKind != JsonValueKind.Object
            || !TryString(payload, "inputPath", out var inputPath)
            || !TryString(payload, "outputDirectory", out var outputDirectory)
            || !TryString(payload, "profile", out var profile))
        {
            error = "Payload must contain non-empty inputPath, outputDirectory, and profile strings.";
            return false;
        }

        try
        {
            value = new ConverterJobPayload(Path.GetFullPath(inputPath), Path.GetFullPath(outputDirectory), profile);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Payload path is invalid: {ex.Message}";
            return false;
        }
    }

    private static bool TryString(JsonElement payload, string name, out string value)
    {
        value = string.Empty;
        return payload.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = property.GetString()!.Trim());
    }
}
