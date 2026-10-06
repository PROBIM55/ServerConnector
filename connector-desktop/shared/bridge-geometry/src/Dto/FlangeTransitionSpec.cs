namespace Platform.Bridge.Geometry.Dto;

/// <summary>
/// Сводная спецификация перехода между двумя соседними сегментами пояса:
/// объединяет геометрию переходов по ширине и толщине в одну зону
/// Start..End на оси балки X. Используется для построения helper-points
/// при создании трапециевидных ContourPlate.
/// </summary>
/// <remarks>
/// BoundaryIndex — индекс стыка в исходном SegLwt[] (= i при переходе segs[i] → segs[i+1]).
/// Theoretical — кумулятивная X-координата стыка "на бумаге".
/// Start..End — фактическая зона перехода после учёта ratio.
/// WidthOffset — сдвиг центра ширины-перехода, если толщина меняется одновременно.
/// WidthChanged / ThicknessChanged — флаги типа перехода.
/// ToNextForWide — true если следующая плита (segs[i+1]) шире/толще, и переход
/// "съедает" её начало; false если предыдущая (segs[i]) шире/толще.
/// </remarks>
public readonly record struct FlangeTransitionSpec(
    int BoundaryIndex,
    double Theoretical,
    double Start,
    double End,
    double WidthOffset,
    bool WidthChanged,
    bool ThicknessChanged,
    bool ToNextForWide);
