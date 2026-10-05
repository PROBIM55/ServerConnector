// Per-adapter заявление о поддерживаемых операциях. Возвращается в /capabilities.

#nullable enable

using System.Collections.Generic;

namespace Platform.Bridge.Desktop.Tekla.ComponentRuntime
{
    public sealed class ComponentCapabilities
    {
        public string ComponentType { get; set; } = "";
        public int SchemaVersion { get; set; }

        /// <summary>Subset of: "insert", "modify", "upsert", "delete", "read".</summary>
        public List<string> Operations { get; set; } = new();

        /// <summary>True если требуемый Tekla plugin/component DLL установлен на ПК.</summary>
        public bool TeklaPluginInstalled { get; set; }

        /// <summary>Human-readable причина если TeklaPluginInstalled=false.</summary>
        public string? Reason { get; set; }
    }
}
