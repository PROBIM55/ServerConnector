#nullable disable

using Tekla.Structures.Plugins;

namespace Bridge.TeklaPlugin;

/// <summary>
/// Sister-плагин — те же 27 полей. См.
/// <see cref="BridgeGirderPluginData"/>.
/// </summary>
public class BridgeMvpPluginData
{
    [StructuresField("tb_start")]
    public string Start;

    [StructuresField("tb_end")]
    public string End;

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
}
