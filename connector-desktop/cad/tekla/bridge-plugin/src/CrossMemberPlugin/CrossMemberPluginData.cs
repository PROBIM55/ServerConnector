#nullable disable

using Tekla.Structures.Plugins;

namespace Bridge.TeklaPlugin;

public class CrossMemberPluginData
{
    [StructuresField("tb_projectId")]
    public string ProjectId;

    [StructuresField("tb_lineId")]
    public string LineId;

    [StructuresField("tb_units")]
    public string Units;

    [StructuresField("tb_worldUp")]
    public string WorldUp;

    [StructuresField("tb_expSchemaVer")]
    public int ExportSchemaVersion;

    [StructuresField("tb_sourceHash")]
    public string SourceHash;

    [StructuresField("tb_payloadJson")]
    public string PayloadJson;

    [StructuresField("tb_payloadPath")]
    public string PayloadPath;

    [StructuresField("tb_hostCount")]
    public int HostCount;

    [StructuresField("tb_segmentCount")]
    public int SegmentCount;

    [StructuresField("tb_webCount")]
    public int WebPlateCount;

    [StructuresField("tb_flangeCount")]
    public int FlangePlateCount;

    [StructuresField("tb_spliceCovCnt")]
    public int SpliceCoverPlateCount;

    [StructuresField("tb_cutoutCount")]
    public int CutoutCount;

    [StructuresField("tb_spliceCount")]
    public int SpliceCount;

    [StructuresField("tb_boltCount")]
    public int BoltCount;

    [StructuresField("tb_kind")]
    public string Kind;

    [StructuresField("tb_typeId")]
    public string TypeId;

    [StructuresField("tb_typeLabel")]
    public string TypeLabel;

    [StructuresField("tb_patternId")]
    public string PatternId;

    [StructuresField("tb_source")]
    public string Source;

    [StructuresField("tb_station")]
    public double Station;

    [StructuresField("tb_skewDeg")]
    public double SkewDeg;
}
