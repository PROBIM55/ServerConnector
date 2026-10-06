// Schema формат — описывает поля компонента. См. plan §3.3 + Phase 0
// BridgeGirderSchemaV1.json. Адаптеры предоставляют схему через
// ITeklaComponentAdapter.GetSchema(), runtime использует её для:
//   - Validate(request) — отвергает поля не из schema
//   - Read — возвращает только schema-задекларированные значения
//   - Capabilities — сообщает что можно

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Platform.Bridge.Desktop.Tekla.ComponentRuntime
{
    public sealed class ComponentSchema
    {
        public string ComponentType { get; init; } = "";
        public int SchemaVersion { get; init; }
        public string TeklaPluginName { get; init; } = "";
        public int TeklaPluginNumber { get; init; }
        public IReadOnlyList<ComponentField> Fields { get; init; } = Array.Empty<ComponentField>();

        public ComponentField? GetField(string webName)
            => Fields.FirstOrDefault(f => string.Equals(f.Name, webName, StringComparison.Ordinal));
    }

    public sealed class ComponentField
    {
        public string Name { get; init; } = "";              // web-side key
        public string TeklaAttribute { get; init; } = "";    // tb_xxx
        public ComponentFieldType Type { get; init; }
        public bool Required { get; init; }
        public object? Default { get; init; }
        public IReadOnlyList<string>? AllowedValues { get; init; }
        public double? Min { get; init; }
        public double? Max { get; init; }
        public string? Description { get; init; }

        /// <summary>Validate raw value against this field; returns failure reason or null on OK.</summary>
        public string? Validate(object? rawValue)
        {
            // Required check
            if (rawValue is null || (rawValue is string s0 && string.IsNullOrWhiteSpace(s0)))
            {
                return Required ? $"required" : null;
            }

            switch (Type)
            {
                case ComponentFieldType.Double:
                {
                    if (!TryParseDouble(rawValue, out var d))
                        return $"expected double, got '{rawValue}'";
                    if (Min.HasValue && d < Min.Value) return $"below min {Min.Value}";
                    if (Max.HasValue && d > Max.Value) return $"above max {Max.Value}";
                    return null;
                }
                case ComponentFieldType.Integer:
                {
                    if (!TryParseInt(rawValue, out var i))
                        return $"expected integer, got '{rawValue}'";
                    if (Min.HasValue && i < Min.Value) return $"below min {Min.Value}";
                    if (Max.HasValue && i > Max.Value) return $"above max {Max.Value}";
                    return null;
                }
                case ComponentFieldType.Boolean:
                {
                    if (!TryParseBool(rawValue, out _))
                        return $"expected boolean, got '{rawValue}'";
                    return null;
                }
                case ComponentFieldType.String:
                case ComponentFieldType.Enum:
                {
                    var str = rawValue.ToString() ?? "";
                    if (Type == ComponentFieldType.Enum && AllowedValues is { Count: > 0 }
                        && !AllowedValues.Contains(str, StringComparer.Ordinal))
                    {
                        return $"value '{str}' is not one of [{string.Join(",", AllowedValues)}]";
                    }
                    return null;
                }
                default:
                    return $"unknown field type {Type}";
            }
        }

        public static bool TryParseDouble(object value, out double result)
        {
            switch (value)
            {
                case double d: result = d; return true;
                case float f: result = f; return true;
                case int i: result = i; return true;
                case long l: result = l; return true;
                case string s:
                    return double.TryParse(s.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
                case JsonElement je:
                    if (je.ValueKind == JsonValueKind.Number) return je.TryGetDouble(out result);
                    if (je.ValueKind == JsonValueKind.String)
                        return double.TryParse((je.GetString() ?? "").Replace(',', '.'),
                            NumberStyles.Any, CultureInfo.InvariantCulture, out result);
                    result = 0; return false;
                default:
                    result = 0;
                    return false;
            }
        }

        public static bool TryParseInt(object value, out int result)
        {
            switch (value)
            {
                case int i: result = i; return true;
                case long l when l >= int.MinValue && l <= int.MaxValue: result = (int)l; return true;
                case double d when d >= int.MinValue && d <= int.MaxValue && Math.Truncate(d) == d:
                    result = (int)d; return true;
                case string s: return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
                case JsonElement je:
                    if (je.ValueKind == JsonValueKind.Number) return je.TryGetInt32(out result);
                    if (je.ValueKind == JsonValueKind.String)
                        return int.TryParse(je.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
                    result = 0; return false;
                default: result = 0; return false;
            }
        }

        public static bool TryParseBool(object value, out bool result)
        {
            switch (value)
            {
                case bool b: result = b; return true;
                case int i: result = i != 0; return true;
                case string s:
                    if (string.Equals(s, "1", StringComparison.Ordinal) || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase))
                    { result = true; return true; }
                    if (string.Equals(s, "0", StringComparison.Ordinal) || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase))
                    { result = false; return true; }
                    result = false; return false;
                case JsonElement je:
                    if (je.ValueKind == JsonValueKind.True)  { result = true;  return true; }
                    if (je.ValueKind == JsonValueKind.False) { result = false; return true; }
                    if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var iVal))
                    { result = iVal != 0; return true; }
                    if (je.ValueKind == JsonValueKind.String)
                        return TryParseBool(je.GetString() ?? "", out result);
                    result = false; return false;
                default: result = false; return false;
            }
        }
    }

    public enum ComponentFieldType
    {
        String,
        Enum,
        Double,
        Integer,
        Boolean,
    }
}
