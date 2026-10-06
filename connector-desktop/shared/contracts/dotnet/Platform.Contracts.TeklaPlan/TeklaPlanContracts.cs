#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Contracts.TeklaPlan
{
    public static class TeklaPlanContract
    {
        public const int SchemaVersion = 1;
        public const string OwnershipNamespace = "bimplatforma";

        public static readonly string[] CapabilityNames =
        {
            "beam", "polyBeam", "polyBeamArcSegments", "developedPlate", "developedPlatePolyBeamStationFrameV1", "contourPlate", "contourArcEdges",
            "loftedPlate", "fitting", "booleanPart", "roundHole", "slottedHole",
            "boltGroup", "weld", "polygonWeld", "contourChamfer", "edgeChamfer",
            "bend", "reinforcement", "assembly", "udaStamp",
        };
    }

    public sealed class TeklaPlanDocument
    {
        public int SchemaVersion { get; set; }
        public TeklaPlanSource Source { get; set; } = new();
        public TeklaPlanTarget Target { get; set; } = new();
        public Dictionary<string, bool> Capabilities { get; set; } = new(StringComparer.Ordinal);
        public bool Executable { get; set; }
        public TeklaPlanCommand[] Commands { get; set; } = Array.Empty<TeklaPlanCommand>();
        public TeklaPlanMapping[] Mappings { get; set; } = Array.Empty<TeklaPlanMapping>();
        public TeklaPlanDiagnostic[] Diagnostics { get; set; } = Array.Empty<TeklaPlanDiagnostic>();
    }

    public sealed class TeklaPlanSource
    {
        public ConstructiveAddress Address { get; set; } = new();
        public int ConstructiveSchemaVersion { get; set; }
        public string GenerationId { get; set; } = string.Empty;
        public string SourceHash { get; set; } = string.Empty;
        public string ConstructiveContentHash { get; set; } = string.Empty;
    }

    public sealed class ConstructiveAddress
    {
        public string ProjectId { get; set; } = string.Empty;
        public string? ScenarioId { get; set; }
        public string ModuleId { get; set; } = string.Empty;
        public string ModuleVariantId { get; set; } = string.Empty;
        public string RevisionId { get; set; } = string.Empty;
    }

    public sealed class TeklaPlanTarget
    {
        public string AdapterId { get; set; } = string.Empty;
        public string AdapterVersion { get; set; } = string.Empty;
        public string? TeklaVersion { get; set; }
    }

    public sealed class TeklaPlanSourceRef
    {
        public string Kind { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string StableKey { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string SourceLayer { get; set; } = string.Empty;
    }

    public sealed class ConstructiveOwner
    {
        public string ModuleId { get; set; } = string.Empty;
        public string EntityId { get; set; } = string.Empty;
        public string[]? PartPath { get; set; }
    }

    public sealed class ConstructiveRevisionProvenance
    {
        public string RevisionId { get; set; } = string.Empty;
        public string SourceHash { get; set; } = string.Empty;
        public string AdapterId { get; set; } = string.Empty;
        public string AdapterVersion { get; set; } = string.Empty;
        public string[]? SourcePath { get; set; }
    }

    public sealed class TeklaOwnershipStamp
    {
        public string Namespace { get; set; } = string.Empty;
        public string DocumentSourceHash { get; set; } = string.Empty;
        public string GenerationId { get; set; } = string.Empty;
        public string SourceKind { get; set; } = string.Empty;
        public string SourceId { get; set; } = string.Empty;
        public string StableKey { get; set; } = string.Empty;
        public string ModuleId { get; set; } = string.Empty;
        public string ModuleVariantId { get; set; } = string.Empty;
        public string RevisionId { get; set; } = string.Empty;
    }

    public sealed class TeklaPlanCommand
    {
        public string CommandId { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Phase { get; set; } = string.Empty;
        public TeklaPlanSourceRef Source { get; set; } = new();
        public ConstructiveOwner Owner { get; set; } = new();
        public ConstructiveRevisionProvenance RevisionProvenance { get; set; } = new();
        public string[] DependsOn { get; set; } = Array.Empty<string>();
        public TeklaOwnershipStamp Ownership { get; set; } = new();

        [JsonExtensionData]
        public Dictionary<string, JsonElement> Payload { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed class TeklaPlanMapping
    {
        public TeklaPlanSourceRef Source { get; set; } = new();
        public string Strategy { get; set; } = string.Empty;
        public string[] CommandIds { get; set; } = Array.Empty<string>();
        public string[] ExpectedNativeKinds { get; set; } = Array.Empty<string>();
        public string OwnershipStableKey { get; set; } = string.Empty;
    }

    public sealed class TeklaPlanDiagnostic
    {
        public string Severity { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public TeklaPlanSourceRef? Source { get; set; }
        public string? CommandId { get; set; }
        public string? RequiredCapability { get; set; }
    }

    public sealed class TeklaRuntimeCapabilities
    {
        public string AdapterId { get; set; } = string.Empty;
        public string AdapterVersion { get; set; } = string.Empty;
        public string TeklaVersion { get; set; } = string.Empty;
        public IReadOnlyDictionary<string, bool> Flags { get; set; } = new Dictionary<string, bool>();
    }

    public static class TeklaPlanJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static TeklaPlanDocument Deserialize(string json)
        {
            return JsonSerializer.Deserialize<TeklaPlanDocument>(json, Options)
                ?? throw new JsonException("TeklaPlan body is empty.");
        }
    }
}
