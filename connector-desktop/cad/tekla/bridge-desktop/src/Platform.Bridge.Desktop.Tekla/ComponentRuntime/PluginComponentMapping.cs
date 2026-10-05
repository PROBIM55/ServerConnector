// Generic schema-driven маппинг web parameters → Tekla.SetAttribute calls.
// Используется ВСЕМИ адаптерами (BridgeGirder, Pier, …) — никакой business
// logic, чисто проход по schema.
//
// Сюда же укладываются 3 service UDA (STRUCTURA_*) — они НЕ часть schema,
// а recovery channel (см. plan §5.3 + StructuraServiceUdas).
//
// Phase 10: extracted from BridgeGirderTeklaMapping (rename + move) чтобы
// демонстрировать generic-нейтральность архитектуры — Pier и любой будущий
// PluginBase-производный component используют этот же helper без копирования.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.ComponentRuntime
{
    public static class PluginComponentMapping
    {
        /// <summary>
        /// Записывает schema-задекларированные поля (tb_* plugin attributes) на
        /// Tekla component'е через BaseComponent.SetAttribute. Это другой
        /// механизм чем SetUserProperty: plugin читает [StructuresField] именно
        /// через SetAttribute. Возвращает (appliedFieldNames, skippedFieldNames).
        /// Skipped — поля присутствующие в request.Parameters, но не в schema:
        /// runtime их игнорирует и сообщает наверх для диагностики.
        /// </summary>
        public static (List<string> Applied, List<string> Skipped) ApplyParameters(
            BaseComponent componentObj,
            ComponentSchema schema,
            ComponentOperationRequest request)
        {
            var applied = new List<string>();
            var skipped = new List<string>();

            // Pass 1: для каждого schema-field, если значение в request есть —
            // выставить, иначе оставить (Default подставит сама Tekla при insert
            // согласно plugin'у).
            foreach (var field in schema.Fields)
            {
                if (!request.Parameters.TryGetValue(field.Name, out var value))
                    continue;

                if (value is null) continue;

                SetAttributeTyped(componentObj, field, value);
                applied.Add(field.Name);
            }

            // Pass 2: skipped — поля, попавшие в payload но не покрытые схемой.
            foreach (var key in request.Parameters.Keys)
            {
                if (schema.GetField(key) is null)
                    skipped.Add(key);
            }

            return (applied, skipped);
        }

        /// <summary>
        /// Записать service UDA — recovery channel — через SetUserProperty.
        /// Это стандартный Tekla UDA mechanism (не plugin-specific), что
        /// позволяет читать через стандартные tools (Inquire dialog, ttp_files).
        /// При insert пишутся все 4; при modify только LastOperationId, остальные
        /// уже зафиксированы при первом insert и не должны меняться.
        /// </summary>
        public static void ApplyServiceUdas(
            ModelObject componentObj,
            ComponentOperationRequest request,
            bool isInsert)
        {
            componentObj.SetUserProperty(StructuraServiceUdas.LastOperationId, request.IdempotencyKey ?? "");

            if (isInsert)
            {
                componentObj.SetUserProperty(StructuraServiceUdas.ExternalObjectId, request.ExternalObjectId ?? "");
                componentObj.SetUserProperty(StructuraServiceUdas.ComponentType, request.ComponentType ?? "");
                componentObj.SetUserProperty(StructuraServiceUdas.SchemaVersion, request.SchemaVersion);
            }
        }

        private static void SetAttributeTyped(BaseComponent obj, ComponentField field, object value)
        {
            switch (field.Type)
            {
                case ComponentFieldType.Double:
                {
                    if (ComponentField.TryParseDouble(value, out var d))
                        obj.SetAttribute(field.TeklaAttribute, d);
                    break;
                }
                case ComponentFieldType.Integer:
                {
                    if (ComponentField.TryParseInt(value, out var i))
                        obj.SetAttribute(field.TeklaAttribute, i);
                    break;
                }
                case ComponentFieldType.Boolean:
                {
                    // Plugin ожидает boolean'ы как строки "0"/"1" (см. tb_ribBothSides
                    // в схеме BridgeGirder). Sparse but consistent с тем как plugin
                    // эти поля парсит.
                    if (ComponentField.TryParseBool(value, out var b))
                        obj.SetAttribute(field.TeklaAttribute, b ? "1" : "0");
                    break;
                }
                case ComponentFieldType.String:
                case ComponentFieldType.Enum:
                {
                    var s = value switch
                    {
                        string str => str,
                        JsonElement je => JsonElementToString(je),
                        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                        _ => value.ToString() ?? "",
                    };
                    obj.SetAttribute(field.TeklaAttribute, s);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unsupported ComponentFieldType: {field.Type}");
            }
        }

        private static string JsonElementToString(JsonElement je) => je.ValueKind switch
        {
            JsonValueKind.String => je.GetString() ?? "",
            JsonValueKind.Number => je.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            JsonValueKind.Null => "",
            _ => je.GetRawText(),
        };

        /// <summary>
        /// Чтение schema-задекларированных tb_* полей из Tekla через
        /// BaseComponent.GetAttribute. Возвращает dictionary с типами в native
        /// form (double / int / bool-as-string / string).
        /// </summary>
        public static Dictionary<string, object?> ReadParameters(BaseComponent componentObj, ComponentSchema schema)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var field in schema.Fields)
            {
                result[field.Name] = ReadOne(componentObj, field);
            }
            return result;
        }

        private static object? ReadOne(BaseComponent obj, ComponentField field)
        {
            switch (field.Type)
            {
                case ComponentFieldType.Double:
                {
                    double v = 0.0;
                    obj.GetAttribute(field.TeklaAttribute, ref v);
                    return v;
                }
                case ComponentFieldType.Integer:
                {
                    int v = 0;
                    obj.GetAttribute(field.TeklaAttribute, ref v);
                    return v;
                }
                case ComponentFieldType.Boolean:
                case ComponentFieldType.String:
                case ComponentFieldType.Enum:
                {
                    string v = "";
                    obj.GetAttribute(field.TeklaAttribute, ref v);
                    return v;
                }
                default:
                    return null;
            }
        }
    }
}
