#nullable disable

using Tekla.Structures.Plugins;

namespace Structura.Tekla.Fachwerk;

public sealed class FachwerkLowerRigelNodePluginData
{
    [StructuresField("fklr_gap")]
    public double JointGap = 25.0;

    [StructuresField("fklr_diameter")]
    public double TubeDiameter = 530.0;

    [StructuresField("fklr_plate_profile")]
    public string PlateProfile = "PL25";

    [StructuresField("fklr_material")]
    public string Material = "C355-5";

    [StructuresField("fklr_class")]
    public string ClassName = "20";

    [StructuresField("fklr_plate_width")]
    public double TransitionPlateWidth = 350.0;

    [StructuresField("fklr_left_length")]
    public double LeftTransitionLength = 500.0;

    // Kept as a hidden compatibility mirror for components inserted before
    // both transition plates started using one common length.
    [StructuresField("fklr_right_length")]
    public double RightTransitionLength = 500.0;

    [StructuresField("fklr_auto_len")]
    public string AutomaticLengthEnabled = "NO";

    [StructuresField("fklr_bottom_cap")]
    public string BottomClosureEnabled = "NO";

    [StructuresField("fklr_outer_cut")]
    public string OuterFlangeTubeCutEnabled = "YES";

    [StructuresField("fklr_axis_corr")]
    public string AxisCorrectionEnabled = "NO";

    [StructuresField("fklr_control_line")]
    public string ControlLineEnabled = "YES";
}
