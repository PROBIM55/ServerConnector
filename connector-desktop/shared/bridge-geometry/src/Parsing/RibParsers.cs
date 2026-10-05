using System;
using System.Collections.Generic;
using Platform.Bridge.Geometry.Dto;

namespace Platform.Bridge.Geometry.Parsing;

/// <summary>
/// Парсеры payload-форматов web-ribs: продольные стиффенеры (long ribs)
/// и поперечные (trans ribs). Формат: специфика разделена через ';',
/// поля внутри спецификации через ','. Подробности — в декомпиляции
/// прежнего плагина (TeklaBridge.exe :2100-2162):
/// <code>
/// webLongRibs:  side,bothSides,offset,start,length,height,thickness
/// webTransRibs: side,bothSides,step,height,thickness
/// </code>
/// Невалидные спецификации (неверная арность, отрицательные значения)
/// тихо пропускаются — поведение прежнего плагина для совместимости.
/// </summary>
public static class RibParsers
{
    /// <summary>
    /// Парсит "side,bothSides,offset,start,length,height,thickness[,orientation];..."
    /// в массив WebLongRibSpec. Пустая строка / null → пустой массив.
    /// 8-е поле (orientation: 'P'/'H' или 'perpendicular'/'horizontal') —
    /// опционально. По умолчанию Perpendicular. Невалидные специи
    /// пропускаются без exception.
    /// </summary>
    public static WebLongRibSpec[] ParseWebLongRibs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<WebLongRibSpec>();
        var items = raw!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<WebLongRibSpec>(items.Length);
        foreach (var item in items)
        {
            var fields = item.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 7 || fields.Length > 9) continue;
            if (!Parsers.TryParseDoubleLenient(fields[2], out var offset)) continue;
            if (!Parsers.TryParseDoubleLenient(fields[3], out var start)) continue;
            if (!Parsers.TryParseDoubleLenient(fields[4], out var length)) continue;
            if (!Parsers.TryParseDoubleLenient(fields[5], out var height)) continue;
            if (!Parsers.TryParseDoubleLenient(fields[6], out var thickness)) continue;
            if (offset <= 0 || length <= 0 || height <= 0 || thickness <= 0) continue;

            var orientation = RibOrientation.Perpendicular;
            if (fields.Length >= 8)
            {
                var token = fields[7].Trim();
                if (token.Equals("H", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("horizontal", StringComparison.OrdinalIgnoreCase))
                {
                    orientation = RibOrientation.Horizontal;
                }
            }

            // 9-е поле — участки разбивки по длине (Фаза 2): "L:H:T~L:H:T~...".
            var segments = fields.Length >= 9 ? ParseRibSegments(fields[8]) : null;

            result.Add(new WebLongRibSpec(
                Side: Normalize.NormalizeSide(fields[0]),
                BothSides: Parsers.ParseBool(fields[1]),
                Offset: offset,
                Start: start,
                Length: length,
                Height: height,
                Thickness: thickness,
                Orientation: orientation,
                Segments: segments));
        }
        return result.ToArray();
    }

    /// <summary>
    /// Парсит участки ребра "L:H:T~L:H:T~..." (Фаза 2). H/T ≤ 0 допустимы
    /// (ResolveRibSections подставит номинал ребра). Длина L ≤ 0 пропускается.
    /// Пустой/невалидный токен → null (однородное ребро).
    /// </summary>
    private static IReadOnlyList<RibSegment>? ParseRibSegments(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split(new[] { '~' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<RibSegment>(parts.Length);
        foreach (var part in parts)
        {
            var f = part.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length != 3) continue;
            if (!Parsers.TryParseDoubleLenient(f[0], out var l)) continue;
            if (!Parsers.TryParseDoubleLenient(f[1], out var h)) continue;
            if (!Parsers.TryParseDoubleLenient(f[2], out var t)) continue;
            if (l <= 0) continue;
            list.Add(new RibSegment(l, h, t));
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// Парсит "side,bothSides,step,height,thickness;..." в массив
    /// WebTransRibSpec. Те же правила фильтрации, что и для long ribs.
    /// </summary>
    public static WebTransRibSpec[] ParseWebTransRibs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<WebTransRibSpec>();
        var items = raw!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<WebTransRibSpec>(items.Length);
        foreach (var item in items)
        {
            var fields = item.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 5) continue;
            if (!Parsers.TryParseDoubleLenient(fields[2], out var step)) continue;
            if (!Parsers.TryParseDoubleLenient(fields[3], out var height)) continue;
            if (!Parsers.TryParseDoubleLenient(fields[4], out var thickness)) continue;
            if (step <= 0 || height <= 0 || thickness <= 0) continue;

            result.Add(new WebTransRibSpec(
                Side: Normalize.NormalizeSide(fields[0]),
                BothSides: Parsers.ParseBool(fields[1]),
                Step: step,
                Height: height,
                Thickness: thickness));
        }
        return result.ToArray();
    }
}
