using System.Text;
using System.Text.Json;

namespace Connector.Desktop.Features.GraphiteWeb;

/// <summary>
/// Strict, side-effect-free validation for the native/web message boundary.
/// Command allowlisting remains the responsibility of the command dispatcher supplied to GraphiteWebView.Configure.
/// </summary>
public static class GraphiteRpcProtocol
{
    public const int SchemaVersion = 1;
    public const int MaximumRequestBytes = 64 * 1024;

    private const int MaximumCommandLength = 64;
    private const string InvalidRequestError = "Некорректный запрос.";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16
    };

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32
    };

    public static bool TryParseRequest(
        string? json,
        out GraphiteRpcRequest? request,
        out string error)
    {
        request = null;
        error = InvalidRequestError;

        if (string.IsNullOrEmpty(json) || json.Length > MaximumRequestBytes)
        {
            return false;
        }

        if (Encoding.UTF8.GetByteCount(json) > MaximumRequestBytes)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, DocumentOptions);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            JsonElement schemaElement = default;
            JsonElement idElement = default;
            JsonElement commandElement = default;
            JsonElement payloadElement = default;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    return false;
                }

                switch (property.Name)
                {
                    case "schemaVersion":
                        schemaElement = property.Value;
                        break;
                    case "id":
                        idElement = property.Value;
                        break;
                    case "command":
                        commandElement = property.Value;
                        break;
                    case "payload":
                        payloadElement = property.Value;
                        break;
                    default:
                        // Unknown envelope types and fields are intentionally rejected.
                        return false;
                }
            }

            if (seen.Count != 4 ||
                schemaElement.ValueKind != JsonValueKind.Number ||
                !schemaElement.TryGetInt32(out int schemaVersion) ||
                schemaVersion != SchemaVersion ||
                idElement.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(idElement.GetString(), "D", out Guid id) ||
                commandElement.ValueKind != JsonValueKind.String ||
                payloadElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string? command = commandElement.GetString();
            if (!IsValidCommandName(command))
            {
                return false;
            }

            request = new GraphiteRpcRequest(schemaVersion, id, command!, payloadElement.Clone());
            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static string SerializeResponse(Guid id, bool ok, object? result, string? error)
        => JsonSerializer.Serialize(
            new
            {
                schemaVersion = SchemaVersion,
                id = id.ToString("D"),
                ok,
                result = ok ? result : null,
                error = ok ? null : error
            },
            SerializerOptions);

    internal static string SerializeSnapshot(object snapshot)
        => JsonSerializer.Serialize(
            new
            {
                schemaVersion = SchemaVersion,
                @event = "snapshot",
                payload = snapshot
            },
            SerializerOptions);

    private static bool IsValidCommandName(string? command)
    {
        if (string.IsNullOrEmpty(command) || command.Length > MaximumCommandLength)
        {
            return false;
        }

        if (!IsLowerAsciiLetterOrDigit(command[0]))
        {
            return false;
        }

        for (int index = 1; index < command.Length; index++)
        {
            char value = command[index];
            if (!IsLowerAsciiLetterOrDigit(value) && value != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowerAsciiLetterOrDigit(char value)
        => value is >= 'a' and <= 'z' or >= '0' and <= '9';
}
