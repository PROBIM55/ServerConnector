using System;

namespace Platform.Bridge.Geometry.Parsing;

/// <summary>
/// Нормализация перечислимых полей plugin-payload: маппинг строки из
/// command line в типизированный enum. Контракт идентичен поведению
/// прежнего плагина: невалидные значения → fallback (по historical
/// default'у), не throw.
/// </summary>
public static class Normalize
{
    /// <summary>
    /// Маппинг строки в <see cref="StressZone"/>. "TOP_TENSION" → TopTension,
    /// "BOTTOM_TENSION" → BottomTension, всё остальное (включая null/garbage)
    /// → BottomTension (historical default — соответствует нижней балке
    /// сжато-растянутой схемы по умолчанию).
    /// </summary>
    public static StressZone NormalizeStressZone(string? raw)
    {
        var t = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return t == "TOP_TENSION" ? StressZone.TopTension : StressZone.BottomTension;
    }

    /// <summary>
    /// Маппинг строки в <see cref="FlangeRef"/>. "BOTTOM_LOCKED" →
    /// BottomLocked, "TOP_LOCKED" → TopLocked, любое другое → fallback.
    /// fallback="TOP_LOCKED" — historical default.
    /// </summary>
    public static FlangeRef NormalizeFlangeRef(string? raw, FlangeRef fallback = FlangeRef.TopLocked)
    {
        var t = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return t switch
        {
            "BOTTOM_LOCKED" => FlangeRef.BottomLocked,
            "TOP_LOCKED" => FlangeRef.TopLocked,
            _ => fallback,
        };
    }

    /// <summary>
    /// Маппинг строки в <see cref="TopMode"/>. Принимает только три
    /// валидные значения; невалидные значения → throw (в отличие от
    /// StressZone, тут fallback опасен — недостающий topData приведёт к
    /// крашу геометрии).
    /// </summary>
    public static TopMode NormalizeTopMode(string? raw)
    {
        var t = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return t switch
        {
            "TOP_FLANGE" => TopMode.TopFlange,
            "DECK" => TopMode.Deck,
            "DECK_SLOPES" => TopMode.DeckSlopes,
            _ => throw new FormatException("topMode must be TOP_FLANGE or DECK or DECK_SLOPES"),
        };
    }

    /// <summary>
    /// Маппинг строки в <see cref="HeightMode"/>. "TO_BOTTOM" → ToBottom,
    /// иначе (включая null/garbage) → ToTop (historical default).
    /// </summary>
    public static HeightMode NormalizeHeightMode(string? raw)
    {
        var t = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return t == "TO_BOTTOM" ? HeightMode.ToBottom : HeightMode.ToTop;
    }

    /// <summary>
    /// Нормализация side для рёбер: "right"/"R" → "right", всё остальное
    /// → "left" (historical default). Возвращает lowercase строку, потому
    /// что side используется как часть имени плиты в Tekla.
    /// </summary>
    public static string NormalizeSide(string? raw)
    {
        var t = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return t == "right" || t == "r" ? "right" : "left";
    }
}
