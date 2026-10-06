// Schema v1 для BridgeGirder — typed mirror of BridgeGirderSchemaV1.json.
// Источник — Phase 0 spike (cad/tekla/bridge-desktop/spike/Phase0Spike/) +
// docs/tekla/BRIDGE_GIRDER_COMPONENT_CONTRACT.md.
//
// 27 UDA полей plugin'а BridgeGirderPlugin (Bridge.TeklaPlugin namespace).
// Все tb_* атрибуты захардкожены — это контракт plugin DLL'и, который
// меняется только при выпуске новой schemaVersion и новой версии DLL.

#nullable enable

using System.Collections.Generic;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;

namespace Platform.Bridge.Desktop.Tekla.Components.BridgeGirder
{
    public static class BridgeGirderSchemaV1
    {
        public const string ComponentType = "BridgeGirder";
        public const int SchemaVersion = 1;
        public const string TeklaPluginName = "BridgeGirderPlugin";

        // BaseComponent.PLUGIN_OBJECT_NUMBER — задокументированная константа для
        // PluginBase-производных. Хардкодим значение чтобы не тащить Tekla SDK
        // dependency в схему-уровень: реальная Tekla'ская константа проверена в Phase 0.
        public const int TeklaPluginNumber = -100000;

        public static readonly ComponentSchema Instance = Build();

        private static ComponentSchema Build() => new()
        {
            ComponentType = ComponentType,
            SchemaVersion = SchemaVersion,
            TeklaPluginName = TeklaPluginName,
            TeklaPluginNumber = TeklaPluginNumber,
            Fields = new List<ComponentField>
            {
                // axis / placement
                new() { Name = "start",              TeklaAttribute = "tb_start",              Type = ComponentFieldType.String, Required = true,  Description = "axis start, 'x,y,z' mm" },
                new() { Name = "end",                TeklaAttribute = "tb_end",                Type = ComponentFieldType.String, Required = true,  Description = "axis end, 'x,y,z' mm" },
                new() { Name = "startSkewDeg",       TeklaAttribute = "tb_startSkew",          Type = ComponentFieldType.String, Required = false, Default = "0" },
                new() { Name = "endSkewDeg",         TeklaAttribute = "tb_endSkew",            Type = ComponentFieldType.String, Required = false, Default = "0" },
                new() { Name = "placementMode",      TeklaAttribute = "tb_placementMode",      Type = ComponentFieldType.Enum,   Required = true,  Default = "MANUAL_COORDS",
                        AllowedValues = new[] { "MANUAL_COORDS", "TEKLA_PICK_POINTS", "MODEL_AXIS" } },
                new() { Name = "axisSourceObjectId", TeklaAttribute = "tb_axisSourceObjectId", Type = ComponentFieldType.String, Required = false, Default = "0" },

                // height
                new() { Name = "h",                  TeklaAttribute = "tb_h",                  Type = ComponentFieldType.Double, Required = true,  Default = 2000.0, Min = 100, Max = 10000 },
                new() { Name = "heightMode",         TeklaAttribute = "tb_heightMode",         Type = ComponentFieldType.Enum,   Required = true,  Default = "TO_TOP",
                        AllowedValues = new[] { "TO_TOP", "TO_BOTTOM" } },
                new() { Name = "bottomRef",          TeklaAttribute = "tb_bottomRef",          Type = ComponentFieldType.Enum,   Required = true,  Default = "TOP_LOCKED",
                        AllowedValues = new[] { "TOP_LOCKED", "BOTTOM_LOCKED" } },
                new() { Name = "topRef",             TeklaAttribute = "tb_topRef",             Type = ComponentFieldType.Enum,   Required = true,  Default = "TOP_LOCKED",
                        AllowedValues = new[] { "TOP_LOCKED", "BOTTOM_LOCKED" } },
                new() { Name = "stressZone",         TeklaAttribute = "tb_stressZone",         Type = ComponentFieldType.Enum,   Required = true,  Default = "BOTTOM_TENSION",
                        AllowedValues = new[] { "BOTTOM_TENSION", "TOP_TENSION" } },

                // segments
                new() { Name = "flange",             TeklaAttribute = "tb_flange",             Type = ComponentFieldType.String, Required = true,  Description = "L,W,T|L,W,T|... (mm,mm,mm)" },
                new() { Name = "web",                TeklaAttribute = "tb_web",                Type = ComponentFieldType.String, Required = true,  Description = "L,H,T|L,H,T|... (mm,mm,mm)" },
                new() { Name = "topMode",            TeklaAttribute = "tb_topMode",            Type = ComponentFieldType.Enum,   Required = true,  Default = "TOP_FLANGE",
                        // Plugin принимает TOP_FLANGE / DECK / DECK_SLOPES (подтверждено
                        // plugin-trace из %TEMP%\bridge_plugin_trace.txt: "topMode must be
                        // TOP_FLANGE or DECK or DECK_SLOPES"). Phase 0 контракт ошибочно
                        // указывал ORTHOTROPIC_DECK — он не валиден.
                        AllowedValues = new[] { "TOP_FLANGE", "DECK", "DECK_SLOPES" } },
                new() { Name = "topData",            TeklaAttribute = "tb_topData",            Type = ComponentFieldType.String, Required = false, Description = "L,W,T|... when topMode=TOP_FLANGE" },

                // material / name
                new() { Name = "material",           TeklaAttribute = "tb_mat",                Type = ComponentFieldType.String, Required = true,  Default = "S355" },
                new() { Name = "bridgeName",         TeklaAttribute = "tb_name",               Type = ComponentFieldType.String, Required = false, Default = "BRIDGE_COMPONENT" },

                // ribs (deck)
                new() { Name = "ribSection",         TeklaAttribute = "tb_ribSection",         Type = ComponentFieldType.String, Required = false, Default = "PLATE" },
                new() { Name = "ribH",               TeklaAttribute = "tb_ribH",               Type = ComponentFieldType.String, Required = false, Default = "300" },
                new() { Name = "ribT",               TeklaAttribute = "tb_ribT",               Type = ComponentFieldType.String, Required = false, Default = "12" },
                new() { Name = "ribBothSides",       TeklaAttribute = "tb_ribBothSides",       Type = ComponentFieldType.Enum,   Required = false, Default = "0",
                        AllowedValues = new[] { "0", "1" } },
                new() { Name = "ribStartLeft",       TeklaAttribute = "tb_ribStartLeft",       Type = ComponentFieldType.String, Required = false, Default = "0" },
                new() { Name = "ribLengthLeft",      TeklaAttribute = "tb_ribLengthLeft",      Type = ComponentFieldType.String, Required = false },
                new() { Name = "ribStartRight",      TeklaAttribute = "tb_ribStartRight",      Type = ComponentFieldType.String, Required = false, Default = "0" },
                new() { Name = "ribLengthRight",     TeklaAttribute = "tb_ribLengthRight",     Type = ComponentFieldType.String, Required = false },
                new() { Name = "ribStepsLeft",       TeklaAttribute = "tb_ribStepsLeft",       Type = ComponentFieldType.String, Required = false, Default = "1500|1500|1500" },
                new() { Name = "ribStepsRight",      TeklaAttribute = "tb_ribStepsRight",      Type = ComponentFieldType.String, Required = false, Default = "1500|1500|1500" },

                // web ribs
                new() { Name = "webLongRibs",        TeklaAttribute = "tb_webLongRibs",        Type = ComponentFieldType.String, Required = false, Default = "" },
                new() { Name = "webTransRibs",       TeklaAttribute = "tb_webTransRibs",       Type = ComponentFieldType.String, Required = false, Default = "" },

                // box-section: если box="1" вместо одной стенки по центру
                // строятся две по бокам с расстоянием boxWidthTop сверху и
                // boxWidthBottom снизу. boxWidthTop=boxWidthBottom → вертикальные
                // стенки, < → наклонные внутрь (трапеция).
                new() { Name = "box",                TeklaAttribute = "tb_box",                Type = ComponentFieldType.String, Required = false, Default = "0",
                        AllowedValues = new[] { "0", "1" } },
                new() { Name = "boxWidthTop",        TeklaAttribute = "tb_boxWidthTop",        Type = ComponentFieldType.String, Required = false, Default = "0" },
                new() { Name = "boxWidthBottom",     TeklaAttribute = "tb_boxWidthBottom",     Type = ComponentFieldType.String, Required = false, Default = "0" },
                new() { Name = "wallTiltDeg",        TeklaAttribute = "tb_wallTiltDeg",        Type = ComponentFieldType.String, Required = false, Default = "0" },
                new() { Name = "wallTiltDegLeft",    TeklaAttribute = "tb_wallTiltDegLeft",    Type = ComponentFieldType.String, Required = false, Default = "" },
                new() { Name = "wallTiltDegRight",   TeklaAttribute = "tb_wallTiltDegRight",   Type = ComponentFieldType.String, Required = false, Default = "" },
                new() { Name = "bottomOverhang",     TeklaAttribute = "tb_bottomOverhang",     Type = ComponentFieldType.String, Required = false, Default = "0" },
                new() { Name = "topOverhang",        TeklaAttribute = "tb_topOverhang",        Type = ComponentFieldType.String, Required = false, Default = "0" },
                // tb_* UDA имеют практический лимит длины (~19-21 символов); полные имена
                // tb_bottomFlangeOrientation/tb_topFlangeOrientation не персистились
                // (SetAttribute проходил, но GetAttribute возвращал empty). Сокращены.
                new() { Name = "bottomFlangeOrientation", TeklaAttribute = "tb_botFlangeOri", Type = ComponentFieldType.Enum, Required = false, Default = "HORIZONTAL",
                        AllowedValues = new[] { "HORIZONTAL", "PERPENDICULAR_TO_WALL" } },
                new() { Name = "topFlangeOrientation", TeklaAttribute = "tb_topFlangeOri", Type = ComponentFieldType.Enum, Required = false, Default = "HORIZONTAL",
                        AllowedValues = new[] { "HORIZONTAL", "PERPENDICULAR_TO_WALL" } },
                // I_TWIN_ORTHO_DECK: две независимые двутавровые. Имена UDA
                // короткие из-за Tekla лимита длины (см. tb_botFlangeOri выше).
                new() { Name = "twin", TeklaAttribute = "tb_twin", Type = ComponentFieldType.String, Required = false, Default = "0",
                        AllowedValues = new[] { "0", "1" } },
                new() { Name = "twinWebSpacing", TeklaAttribute = "tb_twinSpacing", Type = ComponentFieldType.String, Required = false, Default = "0" },
                new() { Name = "boxSplitTop", TeklaAttribute = "tb_boxSplitTop", Type = ComponentFieldType.String, Required = false, Default = "0",
                        AllowedValues = new[] { "0", "1" } },
                new() { Name = "boxRotationDeg", TeklaAttribute = "tb_boxRotDeg", Type = ComponentFieldType.String, Required = false, Default = "0" },
                new() { Name = "webLongRibsLeft",    TeklaAttribute = "tb_webLongRibsLeft",    Type = ComponentFieldType.String, Required = false, Default = "" },
                new() { Name = "webLongRibsRight",   TeklaAttribute = "tb_webLongRibsRight",   Type = ComponentFieldType.String, Required = false, Default = "" },
                new() { Name = "webTransRibsLeft",   TeklaAttribute = "tb_webTransRibsLeft",   Type = ComponentFieldType.String, Required = false, Default = "" },
                new() { Name = "webTransRibsRight",  TeklaAttribute = "tb_webTransRibsRight",  Type = ComponentFieldType.String, Required = false, Default = "" },
            }
        };
    }
}
