using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;

namespace Connector.AgrConversion;

/// <summary>
/// Паспорт части из geojson (поля — как в эталоне B1 <c>batch/ref/&lt;часть&gt;.passport.json</c>): свойства объекта без
/// imageBase64, геометрия, прочие поля объекта (Glasses), превью отдельно, точка МСК-77 [x — восток, y — север].
/// </summary>
public static class AgrGeoJsonReader
{
    public const string ImageKey = "imageBase64";

    public static AgrPassport Read(string part, IReadOnlyList<string> geojsonPaths)
    {
        var pass = new AgrPassport { Part = part };
        bool crsSeen = false;
        foreach (var path in geojsonPaths)
        {
            string file = Path.GetFileName(path);
            pass.GeojsonFiles.Add(file);
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (JsonException ex)
            {
                throw new AgrReadException(file, $"geojson {file} не читается: это не JSON.", ex);
            }
            if (root is not JsonObject obj)
            {
                throw new InvalidDataException($"{file}: корень geojson не объект");
            }
            foreach (var kv in obj)
            {
                if (kv.Key == "features") continue;
                if (kv.Key == "crs") crsSeen = true;
                pass.Collection[kv.Key] = kv.Value?.DeepClone();
            }
            if (obj["features"] is not JsonArray features)
            {
                continue;
            }
            for (int i = 0; i < features.Count; i++)
            {
                if (features[i] is not JsonObject f) continue;
                var props = new JsonObject();
                string? image = null;
                if (f["properties"] is JsonObject src)
                {
                    foreach (var kv in src)
                    {
                        if (kv.Key == ImageKey)
                        {
                            image = kv.Value is JsonValue v && v.TryGetValue(out string? s) ? s : null;
                            continue;
                        }
                        props[kv.Key] = kv.Value?.DeepClone();
                    }
                }
                var extra = new JsonObject();
                foreach (var kv in f)
                {
                    if (kv.Key is "type" or "properties" or "geometry") continue;
                    extra[kv.Key] = kv.Value?.DeepClone();
                }
                var feature = new AgrPassportFeature
                {
                    Source = file,
                    Index = i,
                    Type = f["type"] is JsonValue tv && tv.TryGetValue(out string? t) ? t : null,
                    Properties = props,
                    Geometry = f["geometry"]?.DeepClone(),
                    FeatureExtra = extra,
                    Preview = image == null ? null : DecodePreview(part, image),
                };
                pass.Features.Add(feature);
                if (pass.Point == null && TryPoint(feature.Geometry, out var pt))
                {
                    pass.Point = pt;
                }
                if (extra["Glasses"] is JsonArray glasses)
                {
                    foreach (var g in glasses)
                    {
                        if (g is not JsonObject go) continue;
                        foreach (var kv in go)
                        {
                            if (kv.Value is JsonObject props2)
                            {
                                pass.Glasses[kv.Key] = (JsonObject)props2.DeepClone();
                            }
                        }
                    }
                }
            }
        }
        pass.PointCrsNote = crsSeen
            ? "МСК-77 по описанию пакета; в geojson задан crs — проверить"
            : "МСК-77 по описанию пакета; в geojson crs не задан";
        return pass;
    }

    /// <summary>Первая координата точки — x (восток), вторая — y (север), третья (если есть) — высота.</summary>
    public static bool TryPoint(JsonNode? geometry, out double[] point)
    {
        point = Array.Empty<double>();
        if (geometry is not JsonObject g || g["type"] is not JsonValue tv || !tv.TryGetValue(out string? type)
            || type != "Point" || g["coordinates"] is not JsonArray c || c.Count < 2)
        {
            return false;
        }
        var list = new List<double>();
        foreach (var n in c)
        {
            if (n is JsonValue v && v.TryGetValue(out double d)) list.Add(d);
            else return false;
        }
        point = list.ToArray();
        return true;
    }

    static AgrPreview DecodePreview(string part, string base64)
    {
        byte[] data;
        try
        {
            data = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            data = Array.Empty<byte>();
        }
        string format = data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 ? "jpg"
            : data.Length >= 4 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G' ? "png"
            : "bin";
        int[]? size = null;
        if (format != "bin")
        {
            try
            {
                using var ms = new MemoryStream(data, writable: false);
                var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                size = new[] { dec.Frames[0].PixelWidth, dec.Frames[0].PixelHeight };
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException)
            {
                size = null;
            }
        }
        return new AgrPreview
        {
            File = string.Create(CultureInfo.InvariantCulture, $"{part}_preview.{format}"),
            Bytes = data.Length,
            Format = format,
            Size = size,
            Base64Chars = base64.Length,
            Data = data,
        };
    }

    /// <summary>h_relief паспорта (общая отметка частей) или null.</summary>
    public static double? HRelief(AgrPassport pass)
    {
        foreach (var f in pass.Features)
        {
            if (f.Properties["h_relief"] is JsonValue v && v.TryGetValue(out double d))
            {
                return d;
            }
        }
        return null;
    }
}
