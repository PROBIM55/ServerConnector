namespace Platform.Bridge.Geometry.Dto;

/// <summary>
/// Сегмент поперечного уклона палубы для topMode=DECK_SLOPES:
/// L — длина участка по поперечной оси (Z), S — уклон в промилле
/// (rise/run × 1000, положительный = верх палубы поднимается от системной
/// оси балки).
/// </summary>
public readonly record struct SlopeSeg(double L, double S);
