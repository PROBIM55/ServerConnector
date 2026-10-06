#nullable disable

using Tekla.Structures.Plugins;

namespace Bridge.TeklaPlugin;

/// <summary>
/// Контракт payload-полей BridgeGirderPlugin — 27 значений хранящихся как
/// UDA на BaseComponent. Имена tb_* — public API. NB: namespace и declarations
/// сохранены строго идентичные декомпилированному прежнему плагину
/// (Bridge.TeklaPlugin.BridgeGirderPluginData) — для бинарной совместимости
/// Tekla deserialization, которая может использовать reflection с проверкой
/// типов полей. Никаких nullable annotations (`?`) — Tekla SDK может
/// неправильно reagировать на NullableAttribute.
/// </summary>
public class BridgeGirderPluginData
{
    [StructuresField("tb_start")]
    public string Start;

    [StructuresField("tb_end")]
    public string End;

    [StructuresField("tb_startSkew")]
    public string StartSkewDeg;

    [StructuresField("tb_endSkew")]
    public string EndSkewDeg;

    [StructuresField("tb_placementMode")]
    public string PlacementMode;

    [StructuresField("tb_axisSourceObjectId")]
    public string AxisSourceObjectId;

    [StructuresField("tb_h")]
    public double H;

    [StructuresField("tb_heightMode")]
    public string HeightMode;

    [StructuresField("tb_bottomRef")]
    public string BottomRef;

    [StructuresField("tb_topRef")]
    public string TopRef;

    [StructuresField("tb_stressZone")]
    public string StressZone;

    [StructuresField("tb_flange")]
    public string FlangeSegs;

    [StructuresField("tb_web")]
    public string WebSegs;

    [StructuresField("tb_topMode")]
    public string TopMode;

    [StructuresField("tb_topData")]
    public string TopData;

    [StructuresField("tb_mat")]
    public string Material;

    [StructuresField("tb_name")]
    public string BridgeName;

    [StructuresField("tb_ribSection")]
    public string RibSection;

    [StructuresField("tb_ribH")]
    public string RibH;

    [StructuresField("tb_ribT")]
    public string RibT;

    [StructuresField("tb_ribBothSides")]
    public string RibBothSides;

    [StructuresField("tb_ribStartLeft")]
    public string RibStartLeft;

    [StructuresField("tb_ribLengthLeft")]
    public string RibLengthLeft;

    [StructuresField("tb_ribStartRight")]
    public string RibStartRight;

    [StructuresField("tb_ribLengthRight")]
    public string RibLengthRight;

    [StructuresField("tb_ribStepsLeft")]
    public string RibStepsLeft;

    [StructuresField("tb_ribStepsRight")]
    public string RibStepsRight;

    [StructuresField("tb_webLongRibs")]
    public string WebLongRibs;

    [StructuresField("tb_webTransRibs")]
    public string WebTransRibs;

    // Коробчатое сечение (box girder): если "1"/"true"/"YES" — вместо
    // одной стенки по центру строим две по бокам. Расстояние между ними
    // сверху и снизу задаётся следующими полями. Если bottom == top —
    // стенки вертикальные; если bottom < top — трапециевидное сечение
    // (стенки сходятся к низу). Все остальные элементы (пояса, палуба,
    // ребра) работают по обычной логике, web-ribs создаются на каждой
    // стенке.
    [StructuresField("tb_box")]
    public string Box;

    [StructuresField("tb_boxWidthTop")]
    public string BoxWidthTop;

    [StructuresField("tb_boxWidthBottom")]
    public string BoxWidthBottom;

    // Угол наклона стенки от вертикали (градусы). 0 = вертикальные.
    // Положительный → top стенки в +Ey direction (см. 042 §П-8). Применяется
    // и к I-girder (одна стенка), и к box (обе стенки симметрично при
    // wallTiltLinked=true).
    [StructuresField("tb_wallTiltDeg")]
    public string WallTiltDeg;

    // Per-wall tilt для box при wallTiltLinked=false. Если поле пустое —
    // fallback на wallTiltDeg (backward compat и linked-режим).
    [StructuresField("tb_wallTiltDegLeft")]
    public string WallTiltDegLeft;

    [StructuresField("tb_wallTiltDegRight")]
    public string WallTiltDegRight;

    // Свес нижнего пояса за стенку с каждой стороны (мм). Ширина нижнего
    // пояса = boxWidthBottom + 2 * bottomOverhang.
    [StructuresField("tb_bottomOverhang")]
    public string BottomOverhang;

    // Свес верхнего пояса за стенку (только BOX_TOP_FLANGE).
    [StructuresField("tb_topOverhang")]
    public string TopOverhang;

    // Ориентация поясов (042 §3 / П-3). HORIZONTAL (default) — плита
    // параллельна горизонту. PERPENDICULAR_TO_WALL — плита поворачивается
    // на wallTiltRad вокруг Ex (только для I-girder; для box игнорируется
    // на стороне BridgeCommands).
    //
    // ВАЖНО: имена tb_* UDA Tekla имеют практический лимит ~19-21 символов.
    // Полные имена tb_bottomFlangeOrientation (26) / tb_topFlangeOrientation (23)
    // НЕ персистились — adapter SetAttribute проходил, но плагин Get/Reflection
    // читал empty. Сокращены до tb_botFlangeOri / tb_topFlangeOri (15).
    [StructuresField("tb_botFlangeOri")]
    public string BottomFlangeOrientation;

    [StructuresField("tb_topFlangeOri")]
    public string TopFlangeOrientation;

    // I_TWIN_ORTHO_DECK: две независимые двутавровые с общей ортотропной плитой.
    // Twin='1' → плагин строит две I-girder копии (нижний пояс + стенка +
    // рёбра) на ±twinWebSpacing/2 в Y; ортотропка одна общая.
    [StructuresField("tb_twin")]
    public string Twin;

    [StructuresField("tb_twinSpacing")]
    public string TwinWebSpacing;

    // BOX_SPLIT_TOP_FLANGES: коробка с 2 отдельными верхними поясами (один
    // на стенку, плиты не соединены). Применяется только при box=1 +
    // TOP_FLANGE topMode. BridgeCommands разместит top flange на yTop
    // каждой стенки.
    [StructuresField("tb_boxSplitTop")]
    public string BoxSplitTop;

    // Box rotation: поворот всей коробки (стенки, пояса, рёбра) вокруг
    // продольной оси Ex на заданный угол. Применяется только для box-вариантов
    // (BOX_*). Реализовано через BeamFrame.WithRoll — все placement
    // автоматически идут по повёрнутым Ey/Ez.
    [StructuresField("tb_boxRotDeg")]
    public string BoxRotationDeg;

    // Per-wall web ribs (только для box mode). Если задан — переопределяет
    // общий webLongRibs/webTransRibs для соответствующей стенки.
    [StructuresField("tb_webLongRibsLeft")]
    public string WebLongRibsLeft;

    [StructuresField("tb_webLongRibsRight")]
    public string WebLongRibsRight;

    [StructuresField("tb_webTransRibsLeft")]
    public string WebTransRibsLeft;

    [StructuresField("tb_webTransRibsRight")]
    public string WebTransRibsRight;
}
