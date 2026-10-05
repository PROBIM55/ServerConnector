#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Tekla.Structures.Model;
using TeklaBridge.TeklaShim;

namespace TeklaBridge.CrossMembers;

public static class CrossMemberCommands
{
    private const string DefaultMaterial = "S355";

    public static int CreateCrossMemberV1(
        Model model,
        string payloadJson,
        string resultPath,
        int componentId = 0)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                File.WriteAllText(resultPath, "ERROR: payloadJson is required");
                return 1;
            }

            var payload = ParsePayload(payloadJson);
            if (payload is null || string.IsNullOrWhiteSpace(payload.LineId))
            {
                File.WriteAllText(resultPath, "ERROR: invalid CrossMember payload; lineId is required");
                return 1;
            }

            var namePrefix = NamePrefix(payload);
            var deleted = componentId > 0
                ? PartCleanup.DeleteByFatherComponent(model, componentId)
                : PartCleanup.DeleteByNamePrefix(model, namePrefix);

            var created = 0;
            var skipped = 0;
            foreach (var entry in EnumeratePlates(payload))
            {
                var plate = entry.Plate;
                var contour = plate.ToContour();
                if (contour.Count < 3 || plate.Thickness <= 0)
                {
                    skipped++;
                    continue;
                }

                var part = PlatePartFactory.CreateContour(
                    contour,
                    plate.Thickness,
                    string.IsNullOrWhiteSpace(plate.MaterialId) ? DefaultMaterial : plate.MaterialId!,
                    PartName(namePrefix, entry.Role, created + skipped + 1),
                    PartClass(entry.Role));
                if (part is null) skipped++;
                else created++;
            }

            if (!model.CommitChanges())
            {
                File.WriteAllText(resultPath, "ERROR: CommitChanges failed");
                return 1;
            }

            var boltCount = payload.Splices.Sum(splice => splice.Bolts.Count);
            File.WriteAllText(
                resultPath,
                string.Join(
                    ";",
                    "OK",
                    "lineId=" + payload.LineId,
                    "createdPlates=" + created.ToString(CultureInfo.InvariantCulture),
                    "skippedPlates=" + skipped.ToString(CultureInfo.InvariantCulture),
                    "deleted=" + deleted.ToString(CultureInfo.InvariantCulture),
                    "cutoutsDeferred=" + payload.Cutouts.Count.ToString(CultureInfo.InvariantCulture),
                    "boltsDeferred=" + boltCount.ToString(CultureInfo.InvariantCulture)));
            return created > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, "ERROR: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static CrossMemberPayloadV1? ParsePayload(string payloadJson)
    {
        var serializer = new JavaScriptSerializer
        {
            MaxJsonLength = Math.Max(payloadJson.Length * 2, 1024 * 1024),
        };
        var root = AsDictionary(serializer.DeserializeObject(payloadJson));
        if (root is null) return null;

        return new CrossMemberPayloadV1
        {
            Id = StringValue(root, "id"),
            LineId = StringValue(root, "lineId"),
            Kind = OptionalString(root, "kind"),
            TypeId = OptionalString(root, "typeId"),
            TypeLabel = OptionalString(root, "typeLabel"),
            PatternId = OptionalString(root, "patternId"),
            Source = OptionalString(root, "source"),
            Station = OptionalDouble(root, "station"),
            SkewDeg = OptionalDouble(root, "skewDeg"),
            Plates = ParsePlateSet(AsDictionary(Value(root, "plates"))),
            Cutouts = ParseCutouts(ListValue(root, "cutouts")),
            Splices = ParseSplices(ListValue(root, "splices")),
        };
    }

    private static CrossMemberPlateSet ParsePlateSet(Dictionary<string, object?>? dict)
    {
        if (dict is null) return new CrossMemberPlateSet();
        return new CrossMemberPlateSet
        {
            Web = ParsePlates(ListValue(dict, "web")),
            Flange = ParsePlates(ListValue(dict, "flange")),
            SpliceCover = ParsePlates(ListValue(dict, "spliceCover")),
        };
    }

    private static List<CrossMemberPlateDto> ParsePlates(IEnumerable<object?> items)
    {
        var plates = new List<CrossMemberPlateDto>();
        foreach (var item in items)
        {
            var dict = AsDictionary(item);
            if (dict is null) continue;
            plates.Add(new CrossMemberPlateDto
            {
                Id = StringValue(dict, "id"),
                Thickness = DoubleValue(dict, "thickness"),
                MaterialId = OptionalString(dict, "materialId"),
                Outline = ParsePoints(ListValue(dict, "outline")),
            });
        }
        return plates;
    }

    private static List<CrossMemberPointDto> ParsePoints(IEnumerable<object?> items)
    {
        var points = new List<CrossMemberPointDto>();
        foreach (var item in items)
        {
            var dict = AsDictionary(item);
            if (dict is null) continue;
            points.Add(new CrossMemberPointDto
            {
                X = DoubleValue(dict, "x"),
                Y = DoubleValue(dict, "y"),
                Z = DoubleValue(dict, "z"),
            });
        }
        return points;
    }

    private static List<CrossMemberCutoutDto> ParseCutouts(IEnumerable<object?> items)
    {
        var cutouts = new List<CrossMemberCutoutDto>();
        foreach (var item in items)
        {
            var dict = AsDictionary(item);
            if (dict is null) continue;
            cutouts.Add(new CrossMemberCutoutDto { Id = StringValue(dict, "id") });
        }
        return cutouts;
    }

    private static List<CrossMemberSpliceDto> ParseSplices(IEnumerable<object?> items)
    {
        var splices = new List<CrossMemberSpliceDto>();
        foreach (var item in items)
        {
            var dict = AsDictionary(item);
            if (dict is null) continue;
            splices.Add(new CrossMemberSpliceDto
            {
                Id = StringValue(dict, "id"),
                Bolts = ParseBolts(ListValue(dict, "bolts")),
            });
        }
        return splices;
    }

    private static List<CrossMemberBoltDto> ParseBolts(IEnumerable<object?> items)
    {
        var bolts = new List<CrossMemberBoltDto>();
        foreach (var item in items)
        {
            var dict = AsDictionary(item);
            if (dict is null) continue;
            bolts.Add(new CrossMemberBoltDto { Id = StringValue(dict, "id") });
        }
        return bolts;
    }

    private static Dictionary<string, object?>? AsDictionary(object? value)
    {
        if (value is Dictionary<string, object?> nullableDict) return nullableDict;
        if (value is Dictionary<string, object> dict)
            return dict.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
        return null;
    }

    private static IEnumerable<object?> ListValue(Dictionary<string, object?> dict, string key)
    {
        var value = Value(dict, key);
        if (value is null) return Array.Empty<object?>();
        if (value is object?[] array) return array;
        if (value is IEnumerable enumerable && value is not string)
        {
            var items = new List<object?>();
            foreach (var item in enumerable) items.Add(item);
            return items;
        }
        return Array.Empty<object?>();
    }

    private static object? Value(Dictionary<string, object?> dict, string key)
        => dict.TryGetValue(key, out var value) ? value : null;

    private static string StringValue(Dictionary<string, object?> dict, string key)
        => Value(dict, key)?.ToString() ?? "";

    private static string? OptionalString(Dictionary<string, object?> dict, string key)
        => Value(dict, key)?.ToString();

    private static double DoubleValue(Dictionary<string, object?> dict, string key)
        => TryDouble(Value(dict, key), out var value) ? value : 0.0;

    private static double? OptionalDouble(Dictionary<string, object?> dict, string key)
        => TryDouble(Value(dict, key), out var value) ? value : null;

    private static bool TryDouble(object? value, out double result)
    {
        switch (value)
        {
            case null:
                result = 0;
                return false;
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case decimal m:
                result = (double)m;
                return true;
            default:
                return double.TryParse(
                    value.ToString()?.Replace(',', '.'),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out result);
        }
    }

    private static IEnumerable<(string Role, CrossMemberPlateDto Plate)> EnumeratePlates(CrossMemberPayloadV1 payload)
    {
        foreach (var plate in payload.Plates.Web) yield return ("WEB", plate);
        foreach (var plate in payload.Plates.Flange) yield return ("FLANGE", plate);
        foreach (var plate in payload.Plates.SpliceCover) yield return ("SPLICE", plate);
    }

    private static string NamePrefix(CrossMemberPayloadV1 payload)
        => "CM_" + Sanitize(payload.LineId);

    private static string PartName(string prefix, string role, int index)
        => $"{prefix}_{role}_{index.ToString("00", CultureInfo.InvariantCulture)}";

    private static string PartClass(string role) => role switch
    {
        "WEB" => "4",
        "FLANGE" => "2",
        "SPLICE" => "6",
        _ => "5",
    };

    private static string Sanitize(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        var sanitized = new string(chars);
        return sanitized.Length <= 40 ? sanitized : sanitized.Substring(0, 40);
    }
}
