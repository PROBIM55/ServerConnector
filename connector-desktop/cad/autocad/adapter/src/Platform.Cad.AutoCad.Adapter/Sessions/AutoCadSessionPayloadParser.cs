using System.Text.Json;

namespace Platform.Cad.AutoCad.Adapter.Sessions;

/// <summary>
/// Парсинг payload session-задач (mode=session). Pure: JsonElement → typed request,
/// все ошибки — текстом (адаптер мапит в AUTOCAD_SESSION_PAYLOAD_INVALID).
///
/// Контракт payload:
/// {
///   "mode": "session",
///   "sessionKey": "acad-12345",            // optional
///   "action": "ping" | "drawEntities" | "eraseByHandles" | "zoomExtents",
///   "entities": [                          // для drawEntities
///     { "type": "line", "x1":0,"y1":0,"z1":0, "x2":1000,"y2":0,"z2":0 },
///     { "type": "polyline", "points": [[0,0],[1000,0],[1000,500]], "closed": true },
///     { "type": "circle", "cx":500,"cy":250,"cz":0, "r":100 },
///     { "type": "text", "x":0,"y":-300,"z":0, "height":250, "value":"Platform" }
///   ],
///   "handles": ["2A4","2A5"]               // для eraseByHandles
/// }
/// </summary>
public static class AutoCadSessionPayloadParser
{
    public const string SessionModeValue = "session";

    /// <summary>Роутер: payload.mode == "session" → session-путь, иначе legacy batch.</summary>
    public static bool IsSessionMode(JsonElement payload)
    {
        return payload.ValueKind == JsonValueKind.Object &&
               payload.TryGetProperty("mode", out var mode) &&
               mode.ValueKind == JsonValueKind.String &&
               string.Equals(mode.GetString(), SessionModeValue, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParse(JsonElement payload, out AutoCadSessionRequest? request, out string? error)
    {
        request = null;
        error = null;

        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "payload must be a JSON object.";
            return false;
        }

        if (!TryGetString(payload, "action", out var actionText))
        {
            error = "payload.action is required for mode=session (ping | drawEntities | eraseByHandles | zoomExtents).";
            return false;
        }

        AutoCadSessionAction action;
        switch (actionText.Trim().ToLowerInvariant())
        {
            case "ping": action = AutoCadSessionAction.Ping; break;
            case "drawentities": action = AutoCadSessionAction.DrawEntities; break;
            case "erasebyhandles": action = AutoCadSessionAction.EraseByHandles; break;
            case "zoomextents": action = AutoCadSessionAction.ZoomExtents; break;
            default:
                error = $"payload.action '{actionText}' is not supported (ping | drawEntities | eraseByHandles | zoomExtents).";
                return false;
        }

        var sessionKey = TryGetString(payload, "sessionKey", out var key) ? key.Trim() : null;

        var entities = new List<AutoCadEntitySpec>();
        if (action == AutoCadSessionAction.DrawEntities)
        {
            if (!payload.TryGetProperty("entities", out var entitiesElement) ||
                entitiesElement.ValueKind != JsonValueKind.Array ||
                entitiesElement.GetArrayLength() == 0)
            {
                error = "payload.entities must be a non-empty array for action=drawEntities.";
                return false;
            }

            var index = 0;
            foreach (var entityElement in entitiesElement.EnumerateArray())
            {
                if (!TryParseEntity(entityElement, index, out var spec, out error))
                {
                    return false;
                }

                entities.Add(spec!);
                index++;
            }
        }

        var handles = new List<string>();
        if (action == AutoCadSessionAction.EraseByHandles)
        {
            if (!payload.TryGetProperty("handles", out var handlesElement) ||
                handlesElement.ValueKind != JsonValueKind.Array ||
                handlesElement.GetArrayLength() == 0)
            {
                error = "payload.handles must be a non-empty array of strings for action=eraseByHandles.";
                return false;
            }

            foreach (var handleElement in handlesElement.EnumerateArray())
            {
                if (handleElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(handleElement.GetString()))
                {
                    error = "payload.handles must contain non-empty strings.";
                    return false;
                }

                handles.Add(handleElement.GetString()!.Trim());
            }
        }

        request = new AutoCadSessionRequest(action, string.IsNullOrWhiteSpace(sessionKey) ? null : sessionKey, entities, handles);
        return true;
    }

    private static bool TryParseEntity(JsonElement element, int index, out AutoCadEntitySpec? spec, out string? error)
    {
        spec = null;
        error = null;

        if (element.ValueKind != JsonValueKind.Object || !TryGetString(element, "type", out var type))
        {
            error = $"entities[{index}]: each entity must be an object with a string 'type'.";
            return false;
        }

        switch (type.Trim().ToLowerInvariant())
        {
            case "line":
            {
                if (!TryGetDouble(element, "x1", out var x1) || !TryGetDouble(element, "y1", out var y1) ||
                    !TryGetDouble(element, "x2", out var x2) || !TryGetDouble(element, "y2", out var y2))
                {
                    error = $"entities[{index}] (line): x1, y1, x2, y2 are required numbers (z1/z2 optional).";
                    return false;
                }

                var z1 = TryGetDouble(element, "z1", out var pz1) ? pz1 : 0d;
                var z2 = TryGetDouble(element, "z2", out var pz2) ? pz2 : 0d;
                spec = new AutoCadLineSpec(x1, y1, z1, x2, y2, z2);
                return true;
            }

            case "polyline":
            {
                if (!element.TryGetProperty("points", out var pointsElement) ||
                    pointsElement.ValueKind != JsonValueKind.Array ||
                    pointsElement.GetArrayLength() < 2)
                {
                    error = $"entities[{index}] (polyline): points must be an array of at least 2 points.";
                    return false;
                }

                var points = new List<IReadOnlyList<double>>();
                var pointIndex = 0;
                foreach (var pointElement in pointsElement.EnumerateArray())
                {
                    if (pointElement.ValueKind != JsonValueKind.Array ||
                        pointElement.GetArrayLength() is not (2 or 3))
                    {
                        error = $"entities[{index}] (polyline): points[{pointIndex}] must be [x,y] or [x,y,z].";
                        return false;
                    }

                    var coords = new List<double>();
                    foreach (var coordElement in pointElement.EnumerateArray())
                    {
                        if (coordElement.ValueKind != JsonValueKind.Number)
                        {
                            error = $"entities[{index}] (polyline): points[{pointIndex}] must contain numbers only.";
                            return false;
                        }

                        coords.Add(coordElement.GetDouble());
                    }

                    points.Add(coords);
                    pointIndex++;
                }

                var closed = element.TryGetProperty("closed", out var closedElement) &&
                             closedElement.ValueKind == JsonValueKind.True;
                spec = new AutoCadPolylineSpec(points, closed);
                return true;
            }

            case "circle":
            {
                if (!TryGetDouble(element, "cx", out var cx) || !TryGetDouble(element, "cy", out var cy) ||
                    !TryGetDouble(element, "r", out var r))
                {
                    error = $"entities[{index}] (circle): cx, cy, r are required numbers (cz optional).";
                    return false;
                }

                if (r <= 0)
                {
                    error = $"entities[{index}] (circle): r must be > 0.";
                    return false;
                }

                var cz = TryGetDouble(element, "cz", out var pcz) ? pcz : 0d;
                spec = new AutoCadCircleSpec(cx, cy, cz, r);
                return true;
            }

            case "text":
            {
                if (!TryGetDouble(element, "x", out var x) || !TryGetDouble(element, "y", out var y) ||
                    !TryGetDouble(element, "height", out var height))
                {
                    error = $"entities[{index}] (text): x, y, height are required numbers (z optional).";
                    return false;
                }

                if (height <= 0)
                {
                    error = $"entities[{index}] (text): height must be > 0.";
                    return false;
                }

                if (!TryGetString(element, "value", out var value))
                {
                    error = $"entities[{index}] (text): value is a required non-empty string.";
                    return false;
                }

                var z = TryGetDouble(element, "z", out var pz) ? pz : 0d;
                spec = new AutoCadTextSpec(x, y, z, height, value);
                return true;
            }

            default:
                error = $"entities[{index}]: type '{type}' is not supported (line | polyline | circle | text).";
                return false;
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number)
        {
            value = property.GetDouble();
            return true;
        }

        value = 0;
        return false;
    }
}
