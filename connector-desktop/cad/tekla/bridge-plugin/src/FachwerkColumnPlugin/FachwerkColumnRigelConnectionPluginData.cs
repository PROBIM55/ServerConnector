#nullable disable

using Tekla.Structures.Plugins;

namespace Structura.Tekla.Fachwerk;

public sealed class FachwerkColumnRigelConnectionPluginData
{
    [StructuresField("fkrc_penetration")]
    public double UpperPenetration;

    [StructuresField("fkrc_bevel_enabled")]
    public string BevelEnabled;

    [StructuresField("fkrc_bevel_angle")]
    public double BevelAngle;

    [StructuresField("fkrc_bevel_root")]
    public double BevelRootFace;

    [StructuresField("fkrc_if_angle")]
    public double InnerFlangeBevelAngle;

    [StructuresField("fkrc_if_root")]
    public double InnerFlangeBevelRootFace;

    [StructuresField("fkrc_lw_angle")]
    public double LeftWebBevelAngle;

    [StructuresField("fkrc_lw_root")]
    public double LeftWebBevelRootFace;

    [StructuresField("fkrc_rw_angle")]
    public double RightWebBevelAngle;

    [StructuresField("fkrc_rw_root")]
    public double RightWebBevelRootFace;
}
