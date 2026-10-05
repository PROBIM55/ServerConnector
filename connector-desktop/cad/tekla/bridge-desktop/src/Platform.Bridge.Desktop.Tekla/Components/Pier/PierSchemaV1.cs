// Phase 10: второй типизированный component для проверки что архитектура
// generic, а не hardcoded под BridgeGirder. Pier — мостовая опора.
//
// Plugin name "PierPlugin" — гипотетический. Если real DLL не установлена в
// Tekla на ПК, GetCapabilities() сообщит teklaPluginInstalled=false и попытка
// вставки вернёт TEKLA_COMPONENT_INSERT_FAILED. Доказательство generic-нейтр-
// альности: Phase 10 регистрирует это в registry без правок ComponentRuntime/,
// HTTP layer'a, server'а или connector'а.

#nullable enable

using System.Collections.Generic;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;

namespace Platform.Bridge.Desktop.Tekla.Components.Pier
{
    public static class PierSchemaV1
    {
        public const string ComponentType = "Pier";
        public const int SchemaVersion = 1;
        public const string TeklaPluginName = "PierPlugin";
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
                // Геометрия опоры
                new() { Name = "height",     TeklaAttribute = "tb_height",    Type = ComponentFieldType.Double, Required = true,  Default = 5000.0, Min = 500, Max = 50000, Description = "pier height, mm" },
                new() { Name = "diameter",   TeklaAttribute = "tb_diameter",  Type = ComponentFieldType.Double, Required = true,  Default = 1500.0, Min = 100, Max = 10000, Description = "round pier diameter (mm) — used when baseShape=ROUND" },
                new() { Name = "sideA",      TeklaAttribute = "tb_sideA",     Type = ComponentFieldType.Double, Required = false, Default = 1500.0, Min = 100, Max = 10000, Description = "square pier side A (mm) — used when baseShape=SQUARE" },
                new() { Name = "sideB",      TeklaAttribute = "tb_sideB",     Type = ComponentFieldType.Double, Required = false, Default = 1500.0, Min = 100, Max = 10000, Description = "square pier side B (mm) — used when baseShape=SQUARE" },
                new() { Name = "baseShape",  TeklaAttribute = "tb_baseShape", Type = ComponentFieldType.Enum,   Required = true,  Default = "ROUND",
                        AllowedValues = new[] { "ROUND", "SQUARE" } },

                // Материалы
                new() { Name = "material",   TeklaAttribute = "tb_mat",       Type = ComponentFieldType.String, Required = true,  Default = "C30/37" },
                new() { Name = "pierName",   TeklaAttribute = "tb_name",      Type = ComponentFieldType.String, Required = false, Default = "PIER" },

                // Положение по верху (отметка верха опоры в координатах модели)
                new() { Name = "topElev",    TeklaAttribute = "tb_topElev",   Type = ComponentFieldType.Double, Required = false, Default = 0.0,    Description = "absolute Z of pier top, mm" },
            }
        };
    }
}
