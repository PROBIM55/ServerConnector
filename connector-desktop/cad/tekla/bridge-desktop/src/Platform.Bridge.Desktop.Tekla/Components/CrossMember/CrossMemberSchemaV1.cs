// Schema v1 для CrossMember — desktop-side mirror of the web
// cross-members/tekla.ts payload. Desktop валидирует контракт и передаёт
// данные CrossMemberPlugin, который materializes v1 web/flange/splice-cover
// plates in Tekla.

#nullable enable

using System.Collections.Generic;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;

namespace Platform.Bridge.Desktop.Tekla.Components.CrossMember
{
    public static class CrossMemberSchemaV1
    {
        public const string ComponentType = "CrossMember";
        public const int SchemaVersion = 1;
        public const string TeklaPluginName = "CrossMemberPlugin";
        public const int TeklaPluginNumber = -100000; // BaseComponent.PLUGIN_OBJECT_NUMBER

        public static readonly ComponentSchema Instance = Build();

        private static ComponentSchema Build() => new()
        {
            ComponentType = ComponentType,
            SchemaVersion = SchemaVersion,
            TeklaPluginName = TeklaPluginName,
            TeklaPluginNumber = TeklaPluginNumber,
            Fields = new List<ComponentField>
            {
                // identity / payload envelope
                new() { Name = "projectId",           TeklaAttribute = "tb_projectId",    Type = ComponentFieldType.String,  Required = true },
                new() { Name = "lineId",              TeklaAttribute = "tb_lineId",       Type = ComponentFieldType.String,  Required = true },
                new() { Name = "units",               TeklaAttribute = "tb_units",        Type = ComponentFieldType.Enum,    Required = true,  Default = "mm",
                        AllowedValues = new[] { "mm" } },
                new() { Name = "worldUp",             TeklaAttribute = "tb_worldUp",      Type = ComponentFieldType.Enum,    Required = true,  Default = "z",
                        AllowedValues = new[] { "z" } },
                new() { Name = "exportSchemaVersion", TeklaAttribute = "tb_expSchemaVer", Type = ComponentFieldType.Integer, Required = true,  Default = 1, Min = 1, Max = 1 },
                new() { Name = "sourceHash",          TeklaAttribute = "tb_sourceHash",   Type = ComponentFieldType.String,  Required = true },
                new() { Name = "payloadJson",         TeklaAttribute = "tb_payloadJson",  Type = ComponentFieldType.String,  Required = true,
                        Description = "CrossMemberExportComponent JSON, units=mm" },
                new() { Name = "payloadPath",         TeklaAttribute = "tb_payloadPath",  Type = ComponentFieldType.String,  Required = false,
                        Description = "Bridge.Desktop local UTF-8 payload cache path; plugin prefers it over tb_payloadJson when present." },

                // cheap validation counters mirrored from payloadJson
                new() { Name = "hostCount",             TeklaAttribute = "tb_hostCount",     Type = ComponentFieldType.Integer, Required = true, Min = 2 },
                new() { Name = "segmentCount",          TeklaAttribute = "tb_segmentCount",  Type = ComponentFieldType.Integer, Required = true, Min = 1 },
                new() { Name = "webPlateCount",         TeklaAttribute = "tb_webCount",      Type = ComponentFieldType.Integer, Required = true, Min = 0 },
                new() { Name = "flangePlateCount",      TeklaAttribute = "tb_flangeCount",   Type = ComponentFieldType.Integer, Required = true, Min = 0 },
                new() { Name = "spliceCoverPlateCount", TeklaAttribute = "tb_spliceCovCnt",  Type = ComponentFieldType.Integer, Required = true, Min = 0 },
                new() { Name = "cutoutCount",           TeklaAttribute = "tb_cutoutCount",   Type = ComponentFieldType.Integer, Required = true, Min = 0 },
                new() { Name = "spliceCount",           TeklaAttribute = "tb_spliceCount",   Type = ComponentFieldType.Integer, Required = true, Min = 0 },
                new() { Name = "boltCount",             TeklaAttribute = "tb_boltCount",     Type = ComponentFieldType.Integer, Required = true, Min = 0 },

                // user-facing classification
                new() { Name = "kind",      TeklaAttribute = "tb_kind",      Type = ComponentFieldType.Enum,   Required = false,
                        AllowedValues = new[] { "floorbeam", "jacking" } },
                new() { Name = "typeId",    TeklaAttribute = "tb_typeId",    Type = ComponentFieldType.String, Required = false },
                new() { Name = "typeLabel", TeklaAttribute = "tb_typeLabel", Type = ComponentFieldType.String, Required = false },
                new() { Name = "patternId", TeklaAttribute = "tb_patternId", Type = ComponentFieldType.String, Required = false },
                new() { Name = "source",    TeklaAttribute = "tb_source",    Type = ComponentFieldType.Enum,   Required = false,
                        AllowedValues = new[] { "pattern", "exception" } },
                new() { Name = "station",   TeklaAttribute = "tb_station",   Type = ComponentFieldType.Double, Required = false },
                new() { Name = "skewDeg",   TeklaAttribute = "tb_skewDeg",   Type = ComponentFieldType.Double, Required = false },
            }
        };
    }
}
