using System;
using System.Collections.Generic;
using System.Globalization;
using Platform.Bridge.Geometry.Dto;

namespace Platform.Bridge.Geometry.Parsing;

/// <summary>
/// Парсеры plugin-payload форматов. Захарденные разделители (`|`, `,`, `^`,
/// `;`, `=`/`:`) — это контракт command-line dispatch'а плагина, менять
/// нельзя без миграции вёрстки .inp + клиентов. ParseLwtSegments и
/// ParseDeckSlopes бросают <see cref="FormatException"/> с явным label-ом
/// сегмента — Step4/Step5 ловит и пишет в result.txt.
/// </summary>
public static class Parsers
{
    /// <summary>
    /// Разбор "L,W,T|L,W,T|..." в массив SegLwt. Кидает FormatException
    /// с label-ом если сегмент содержит ≠3 полей. Не валидирует знак/диапазон
    /// значений — это делает <see cref="SegmentValidation"/> или вызывающая
    /// логика (AllPositive).
    /// </summary>
    public static SegLwt[] ParseLwtSegments(string raw, string label)
    {
        if (raw is null)
            throw new FormatException(label + " segment string is null");

        var items = raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new SegLwt[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            var parts = items[i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
                throw new FormatException(label + " segment must be L,W,T");
            result[i] = new SegLwt(
                ParseDouble(parts[0]),
                ParseDouble(parts[1]),
                ParseDouble(parts[2]));
        }
        return result;
    }

    /// <summary>
    /// Разбор "L,S|L,S|..." в массив SlopeSeg (используется внутри
    /// <see cref="ParseDeckSlopes"/>).
    /// </summary>
    public static SlopeSeg[] ParseSlopeSegs(string raw)
    {
        if (raw is null)
            throw new FormatException("slope segment string is null");

        var items = raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new SlopeSeg[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            var parts = items[i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                throw new FormatException("slope segment must be L,S");
            result[i] = new SlopeSeg(ParseDouble(parts[0]), ParseDouble(parts[1]));
        }
        return result;
    }

    /// <summary>
    /// Strict-парсинг double в invariant culture. Не делает фоллбэк на ','
    /// (для этого есть <see cref="TryParseDoubleLenient"/>) — потому что
    /// payload-формат использует ',' как разделитель полей.
    /// </summary>
    public static double ParseDouble(string s)
        => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>
    /// Лениентый парсинг double: заменяет ',' на '.' (для отдельных
    /// именованных аргументов где запятая может прийти от Windows-locale).
    /// Возвращает false на null/empty/нечисле.
    /// </summary>
    public static bool TryParseDoubleLenient(string? raw, out double value)
        => double.TryParse(
            (raw ?? string.Empty).Replace(',', '.'),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out value);

    /// <summary>
    /// Разбор "a,b" в (a, b). Используется для topMode=DECK ("deckW,deckT").
    /// </summary>
    public static (double Item1, double Item2) ParsePair(string raw, string label)
    {
        if (raw is null)
            throw new FormatException(label + " is null");
        var parts = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new FormatException(label + " expected 2 values");
        return (ParseDouble(parts[0]), ParseDouble(parts[1]));
    }

    /// <summary>
    /// Разбор topData для topMode=DECK_SLOPES: "deckT^L1,s1|L2,s2|...^R1,s1|..."
    /// Разделитель `^` отделяет (deckT) от (левые сегменты уклона) и
    /// (правые сегменты уклона).
    /// </summary>
    public static (double DeckT, SlopeSeg[] Left, SlopeSeg[] Right) ParseDeckSlopes(string raw)
    {
        if (raw is null)
            throw new FormatException("DECK_SLOPES raw is null");
        // StringSplitOptions.None — чтобы пустой "deckT^^" дал три части
        // (deckT, "", ""), а не две.
        var parts = raw.Split(new[] { '^' }, StringSplitOptions.None);
        if (parts.Length != 3)
            throw new FormatException("DECK_SLOPES format: deckT^L1,s1|...^R1,s1|...");
        return (ParseDouble(parts[0]), ParseSlopeSegs(parts[1]), ParseSlopeSegs(parts[2]));
    }

    /// <summary>
    /// Разбор массива длин шагов: "1500|1500|1500" → [1500,1500,1500].
    /// Пустая/whitespace строка → пустой массив (для optional ribStepsLeft).
    /// </summary>
    public static double[] ParseDoubleSteps(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<double>();
        var items = raw!.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new double[items.Length];
        for (var i = 0; i < items.Length; i++)
            result[i] = ParseDouble(items[i]);
        return result;
    }

    /// <summary>
    /// Найти именованный аргумент в командной строке плагина. Поддерживает
    /// `key=value` и `key:value` разделители, key регистронезависим.
    /// Возвращает null если не найден или формат неверен.
    /// </summary>
    public static string? ParseNamedArg(IReadOnlyList<string>? parts, string key, int startIndex)
    {
        if (parts is null || parts.Count <= startIndex)
            return null;

        for (var i = startIndex; i < parts.Count; i++)
        {
            var raw = parts[i];
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var trimmed = raw.Trim();
            if (!trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                continue;
            var eq = trimmed.IndexOf('=');
            if (eq < 0)
                eq = trimmed.IndexOf(':');
            if (eq < 0 || eq >= trimmed.Length - 1)
                return null;
            return trimmed.Substring(eq + 1).Trim();
        }
        return null;
    }

    /// <summary>
    /// Именованный double-аргумент с fallback. См. <see cref="ParseNamedArg"/>.
    /// </summary>
    public static double ParseNamedDouble(IReadOnlyList<string>? parts, string key, int startIndex, double fallback)
    {
        var s = ParseNamedArg(parts, key, startIndex);
        if (string.IsNullOrWhiteSpace(s))
            return fallback;
        return TryParseDoubleLenient(s, out var v) ? v : fallback;
    }

    /// <summary>
    /// Парсинг bool-like значения. Принимает "1", "true", "yes", "y" в любом
    /// регистре как true; всё остальное — false. Null/whitespace → false.
    /// </summary>
    public static bool ParseBool(string? raw)
    {
        var t = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return t == "1" || t == "true" || t == "yes" || t == "y";
    }

    /// <summary>
    /// Извлечь componentId из payload (формат "cmpid=NNN" или
    /// "component_id=NNN", разделитель = или :). hasComponentIdArg=true
    /// сигнализирует что вызов из modify-режима с известным
    /// идентификатором BaseComponent.
    /// </summary>
    public static int TryParseComponentId(IReadOnlyList<string>? parts, out bool hasComponentIdArg)
    {
        hasComponentIdArg = false;
        if (parts is null || parts.Count <= 12)
            return 0;

        for (var i = 12; i < parts.Count; i++)
        {
            var raw = parts[i];
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var trimmed = raw.Trim();
            var lower = trimmed.ToLowerInvariant();
            if (!lower.StartsWith("cmpid=") && !lower.StartsWith("cmpid:")
                && !lower.StartsWith("component_id=") && !lower.StartsWith("component_id:"))
                continue;
            var eq = trimmed.IndexOf('=');
            if (eq < 0)
                eq = trimmed.IndexOf(':');
            if (eq < 0 || eq >= trimmed.Length - 1)
                continue;
            hasComponentIdArg = true;
            var value = trimmed.Substring(eq + 1).Trim();
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
        }
        return 0;
    }
}
