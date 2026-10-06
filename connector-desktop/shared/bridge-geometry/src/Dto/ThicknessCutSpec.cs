namespace Platform.Bridge.Geometry.Dto;

/// <summary>
/// Бевельный срез на стыке двух плит пояса разной толщины.
/// Theoretical — кумулятивная координата X стыка "на бумаге".
/// Start..End — фактический физический размах среза на более толстой плите.
/// Decrease=true означает что текущая плита толще следующей (срез на текущей,
/// толщина уменьшается по ходу X); false — следующая плита толще.
/// WidthOffset — сдвиг среза, если на том же стыке меняется и ширина:
/// разница (widthRatio*ΔW/2 − thkRatio*ΔT). TargetDetailIndex — индекс плиты
/// в списке DetailBoundary[], к которой применяется срез.
/// </summary>
public readonly record struct ThicknessCutSpec(
    int BoundaryIndex,
    bool Decrease,
    double Theoretical,
    double Start,
    double End,
    double ThickT,
    double ThinT,
    double WidthOffset,
    int TargetDetailIndex);
