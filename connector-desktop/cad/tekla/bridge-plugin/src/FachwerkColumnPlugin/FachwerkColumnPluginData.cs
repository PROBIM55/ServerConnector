#nullable disable

using Tekla.Structures.Plugins;

namespace Structura.Tekla.Fachwerk;

/// <summary>
/// Persistent attributes of one native Fachwerk column component.
/// The profile key resolves a local, deployed catalogue; it is never fetched
/// from Platform while Tekla is running the component.
/// </summary>
public sealed class FachwerkColumnPluginData
{
    [StructuresField("fk_external_id")]
    public string ExternalObjectId;

    [StructuresField("fk_profile_key")]
    public string ProfileKey = "СФ1";

    [StructuresField("fk_mark")]
    public string Mark = "СФ1";

    [StructuresField("fk_catalog_path")]
    public string CatalogPath;

    [StructuresField("fk_material")]
    public string Material = "C355-5";

    [StructuresField("fk_class")]
    public string ClassName = "3";

    [StructuresField("fk_rotation_deg")]
    public double RotationDeg;

    [StructuresField("fk_bevel_profile")]
    public string BevelProfile = "TRI_A14*14";

    [StructuresField("fk_break_1_z")]
    public double Break1Elevation;

    [StructuresField("fk_break_1_mode")]
    public string Break1Mode = "ALL_FOUR";

    [StructuresField("fk_break_2_z")]
    public double Break2Elevation;

    [StructuresField("fk_break_2_mode")]
    public string Break2Mode = "ALL_FOUR";

    [StructuresField("fk_break_3_z")]
    public double Break3Elevation;

    [StructuresField("fk_break_3_mode")]
    public string Break3Mode = "ALL_FOUR";

    [StructuresField("fk_break_4_z")]
    public double Break4Elevation;

    [StructuresField("fk_break_4_mode")]
    public string Break4Mode = "ALL_FOUR";

    [StructuresField("fk_break_5_z")]
    public double Break5Elevation;

    [StructuresField("fk_break_5_mode")]
    public string Break5Mode = "ALL_FOUR";

    [StructuresField("fk_break_6_z")]
    public double Break6Elevation;

    [StructuresField("fk_break_6_mode")]
    public string Break6Mode = "ALL_FOUR";

    [StructuresField("fk_break_7_z")]
    public double Break7Elevation;

    [StructuresField("fk_break_7_mode")]
    public string Break7Mode = "ALL_FOUR";

    [StructuresField("fk_break_8_z")]
    public double Break8Elevation;

    [StructuresField("fk_break_8_mode")]
    public string Break8Mode = "ALL_FOUR";

    [StructuresField("fk_fl_lo_offs")]
    public string FlangeLowerOffsets;

    [StructuresField("fk_fl_hi_offs")]
    public string FlangeUpperOffsets;

    [StructuresField("fk_web_lo_offs")]
    public string WebLowerOffsets;

    [StructuresField("fk_web_hi_offs")]
    public string WebUpperOffsets;

    [StructuresField("fk_offset_schema")]
    public string OffsetSchema;

    [StructuresField("fk_break_1_fl_lo")]
    public double Break1FlangeLowerOffset;

    [StructuresField("fk_break_1_fl_hi")]
    public double Break1FlangeUpperOffset;

    [StructuresField("fk_break_1_web_lo")]
    public double Break1WebLowerOffset;

    [StructuresField("fk_break_1_web_hi")]
    public double Break1WebUpperOffset;

    [StructuresField("fk_break_2_fl_lo")]
    public double Break2FlangeLowerOffset;

    [StructuresField("fk_break_2_fl_hi")]
    public double Break2FlangeUpperOffset;

    [StructuresField("fk_break_2_web_lo")]
    public double Break2WebLowerOffset;

    [StructuresField("fk_break_2_web_hi")]
    public double Break2WebUpperOffset;

    [StructuresField("fk_break_3_fl_lo")]
    public double Break3FlangeLowerOffset;

    [StructuresField("fk_break_3_fl_hi")]
    public double Break3FlangeUpperOffset;

    [StructuresField("fk_break_3_web_lo")]
    public double Break3WebLowerOffset;

    [StructuresField("fk_break_3_web_hi")]
    public double Break3WebUpperOffset;

    [StructuresField("fk_break_4_fl_lo")]
    public double Break4FlangeLowerOffset;

    [StructuresField("fk_break_4_fl_hi")]
    public double Break4FlangeUpperOffset;

    [StructuresField("fk_break_4_web_lo")]
    public double Break4WebLowerOffset;

    [StructuresField("fk_break_4_web_hi")]
    public double Break4WebUpperOffset;

    [StructuresField("fk_break_5_fl_lo")]
    public double Break5FlangeLowerOffset;

    [StructuresField("fk_break_5_fl_hi")]
    public double Break5FlangeUpperOffset;

    [StructuresField("fk_break_5_web_lo")]
    public double Break5WebLowerOffset;

    [StructuresField("fk_break_5_web_hi")]
    public double Break5WebUpperOffset;

    [StructuresField("fk_break_6_fl_lo")]
    public double Break6FlangeLowerOffset;

    [StructuresField("fk_break_6_fl_hi")]
    public double Break6FlangeUpperOffset;

    [StructuresField("fk_break_6_web_lo")]
    public double Break6WebLowerOffset;

    [StructuresField("fk_break_6_web_hi")]
    public double Break6WebUpperOffset;

    [StructuresField("fk_break_7_fl_lo")]
    public double Break7FlangeLowerOffset;

    [StructuresField("fk_break_7_fl_hi")]
    public double Break7FlangeUpperOffset;

    [StructuresField("fk_break_7_web_lo")]
    public double Break7WebLowerOffset;

    [StructuresField("fk_break_7_web_hi")]
    public double Break7WebUpperOffset;

    [StructuresField("fk_break_8_fl_lo")]
    public double Break8FlangeLowerOffset;

    [StructuresField("fk_break_8_fl_hi")]
    public double Break8FlangeUpperOffset;

    [StructuresField("fk_break_8_web_lo")]
    public double Break8WebLowerOffset;

    [StructuresField("fk_break_8_web_hi")]
    public double Break8WebUpperOffset;

    [StructuresField("fk_stiff_enabled")]
    public string StiffenerEnabled = "YES";

    [StructuresField("fk_stiff_profile")]
    public string StiffenerProfile = "PL8";

    [StructuresField("fk_stiff_material")]
    public string StiffenerMaterial = "C355-5";

    [StructuresField("fk_stiff_chamfer")]
    public double StiffenerChamfer = 20.0;

    [StructuresField("fk_stiff_levels")]
    public string StiffenerElevations;

    [StructuresField("fk_stiff_mode")]
    public string StiffenerPlacementMode = "ELEVATIONS";

    [StructuresField("fk_stiff_spacing")]
    public double StiffenerSpacing = 1500.0;

    [StructuresField("fk_stiff_distances")]
    public string StiffenerDistances = "1500";

    [StructuresField("fk_stiff_inner_gap")]
    public double StiffenerInnerFlangeGap = 20.0;

    [StructuresField("fk_stiff_alt_gap")]
    public string StiffenerAlternateFlangeGap = "NO";
}
