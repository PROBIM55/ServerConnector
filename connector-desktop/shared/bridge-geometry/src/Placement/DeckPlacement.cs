using System.Collections.Generic;
using Platform.Bridge.Geometry.Dto;

namespace Platform.Bridge.Geometry.Placement;

/// <summary>
/// Pure-builders для палубных плит (topMode=DECK или DECK_SLOPES).
/// DECK_SLOPES: палуба моделируется как набор инклинированных полос
/// слева и справа от центра — каждая SlopeSeg задаёт ширину и уклон
/// (в промилле). Mirrors decomp CreateDeckWithCrossSlopes :1909-1942.
/// </summary>
public static class DeckPlacement
{
    /// <summary>
    /// Построить список инклинированных палубных полос для DECK_SLOPES.
    /// Полосы идут от центра балки (Y=0) сначала влево по leftSlopes
    /// (накопительно вычитая длину сегмента из Y), затем вправо по rightSlopes.
    /// </summary>
    public static IReadOnlyList<DeckStripSpec> BuildSlopedStrips(
        IReadOnlyList<SlopeSeg> leftSlopes,
        IReadOnlyList<SlopeSeg> rightSlopes,
        double deckThickness,
        SystemFrame system,
        string namePrefix)
    {
        var result = new List<DeckStripSpec>(leftSlopes.Count + rightSlopes.Count);
        var topBaseline = system.TopBaselineY;

        // Left side: y decreases от 0 в отрицательную сторону.
        var yCursor = 0.0;
        var zCursor = topBaseline;
        for (var i = 0; i < leftSlopes.Count; i++)
        {
            var seg = leftSlopes[i];
            var yNext = yCursor - seg.L;
            var zNext = zCursor + seg.L * seg.S / 1000.0;
            result.Add(new DeckStripSpec(
                YStart: yCursor, ZStart: zCursor,
                YEnd: yNext, ZEnd: zNext,
                Thickness: deckThickness,
                Name: $"{namePrefix}_DECK_L{i + 1:D2}",
                ClassId: "2"));
            yCursor = yNext;
            zCursor = zNext;
        }

        // Right side: y increases от 0 в положительную сторону.
        yCursor = 0.0;
        zCursor = topBaseline;
        for (var i = 0; i < rightSlopes.Count; i++)
        {
            var seg = rightSlopes[i];
            var yNext = yCursor + seg.L;
            var zNext = zCursor + seg.L * seg.S / 1000.0;
            result.Add(new DeckStripSpec(
                YStart: yCursor, ZStart: zCursor,
                YEnd: yNext, ZEnd: zNext,
                Thickness: deckThickness,
                Name: $"{namePrefix}_DECK_R{i + 1:D2}",
                ClassId: "2"));
            yCursor = yNext;
            zCursor = zNext;
        }

        return result;
    }
}

/// <summary>
/// Описание одной палубной полосы — горизонтальная (DECK) или
/// инклинированная (DECK_SLOPES). Контур плиты строится в плоскости
/// (ey, ez): две точки на левой грани (Y=YStart, Z=ZStart) и две на
/// правой (Y=YEnd, Z=ZEnd), плюс расширение на весь axisLength
/// вдоль ex.
/// </summary>
public readonly record struct DeckStripSpec(
    double YStart,
    double ZStart,
    double YEnd,
    double ZEnd,
    double Thickness,
    string Name,
    string ClassId);
