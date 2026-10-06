namespace Platform.Bridge.Geometry;

/// <summary>
/// Stress-zone определяет соотношения 1:N для переходов (taper ratio)
/// на нижнем и верхнем поясах. Растянутый пояс требует пологого 1:8
/// перехода (для усталостного ресурса сварных швов), сжатый — 1:4.
/// </summary>
public enum StressZone
{
    BottomTension,
    TopTension,
}

public static class StressZoneExtensions
{
    /// <summary>
    /// Возвращает (bottomRatio, topRatio) для конкретной зоны.
    /// BOTTOM_TENSION → (8.0, 4.0); TOP_TENSION → (4.0, 8.0).
    /// Значения захардкожены в логике плагина — это нормативный СП-уклон.
    /// </summary>
    public static (double BottomRatio, double TopRatio) GetRatios(this StressZone zone)
        => zone switch
        {
            StressZone.BottomTension => (8.0, 4.0),
            StressZone.TopTension => (4.0, 8.0),
            _ => (8.0, 4.0),
        };
}
