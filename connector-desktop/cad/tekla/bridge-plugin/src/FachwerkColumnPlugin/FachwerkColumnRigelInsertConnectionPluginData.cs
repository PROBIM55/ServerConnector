#nullable disable

using Tekla.Structures.Plugins;

namespace Structura.Tekla.Fachwerk;

public sealed class FachwerkColumnRigelInsertConnectionPluginData
{
    [StructuresField("fkri_side")]
    public string InsertSide = "LEFT";

    [StructuresField("fkri_up_ctrl")]
    public string UpperControlFlange = "INNER";

    [StructuresField("fkri_lo_ctrl")]
    public string LowerControlFlange = "INNER";

    [StructuresField("fkri_up_of_dep")]
    public string UpperOuterFlangeDepth = "AUTO";

    [StructuresField("fkri_up_if_dep")]
    public string UpperInnerFlangeDepth = "AUTO";

    [StructuresField("fkri_up_w_dep")]
    public string UpperWebDepth = "AUTO";

    [StructuresField("fkri_lo_of_dep")]
    public string LowerOuterFlangeDepth = "AUTO";

    [StructuresField("fkri_lo_if_dep")]
    public string LowerInnerFlangeDepth = "AUTO";

    [StructuresField("fkri_lo_w_dep")]
    public string LowerWebDepth = "AUTO";

    [StructuresField("fkri_fl_h")]
    public double MinimumFlangeHeight = 250.0;

    [StructuresField("fkri_web_add")]
    public double WebExtraHeight = 150.0;

    [StructuresField("fkri_overlap")]
    public double PartOverlap = 30.0;

    [StructuresField("fkri_insf_ang")]
    public double InsertFlangeAngle = 40.0;

    [StructuresField("fkri_partf_ang")]
    public double PartFlangeAngle = 40.0;

    [StructuresField("fkri_insw_ang")]
    public double InsertWebAngle = 40.0;

    [StructuresField("fkri_partw_ang")]
    public double PartWebAngle = 40.0;

    [StructuresField("fkri_direct_ang")]
    public double DirectWebAngle = 40.0;

    [StructuresField("fkri_root")]
    public double RootFace = 2.0;
}
