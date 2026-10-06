// DTO для входящих /component/upsert|modify|delete|read запросов.
// JSON shape определён в docs/TEKLA_BRIDGE_DESKTOP_PLAN.md §2.1.

#nullable enable

using System.Collections.Generic;

namespace Platform.Bridge.Desktop.Tekla.ComponentRuntime
{
    public sealed class ComponentOperationRequest
    {
        public string Operation { get; set; } = "";          // upsert / modify / delete / read
        public string Provider { get; set; } = "tekla";      // adapter dispatcher key
        public string ComponentType { get; set; } = "";      // e.g. "BridgeGirder"
        public int SchemaVersion { get; set; }               // 1, 2, …
        public string ExternalObjectId { get; set; } = "";   // web-side stable id
        public string IdempotencyKey { get; set; } = "";     // job-id; used by IdempotencyStore
        public ComponentTarget? Target { get; set; }         // for modify/delete/read
        public ComponentPlacement? Placement { get; set; }   // for upsert/insert
        public Dictionary<string, object?> Parameters { get; set; } = new();
    }

    public sealed class ComponentTarget
    {
        /// <summary>Stable persistent GUID of the Tekla component (string form, Tekla 2025 API).</summary>
        public string? TeklaComponentGuid { get; set; }

        /// <summary>Runtime-only Tekla Identifier.ID; used as a hint only.</summary>
        public int? TeklaComponentId { get; set; }
    }

    public sealed class ComponentPlacement
    {
        /// <summary>One of: <c>axis</c> (start+end points), <c>axisObject</c> (existing model object id).</summary>
        public string Kind { get; set; } = "axis";

        public PointDto? Start { get; set; }
        public PointDto? End { get; set; }

        /// <summary>For Kind="axisObject": Tekla object id of the axis line/beam.</summary>
        public int? AxisObjectId { get; set; }
    }

    public sealed class PointDto
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }
}
