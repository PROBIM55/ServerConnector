using System.Text.Json;
using Platform.Contracts.TeklaPlan;
using Xunit;

namespace Platform.Contracts.TeklaPlan.Tests;

public sealed class TeklaPlanValidatorTests
{
    [Fact]
    public void Accepts_complete_native_beam_plan()
    {
        var plan = CreatePlan();

        var result = TeklaPlanValidator.Validate(plan, CreateRuntime());

        Assert.True(result.Valid, string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Message)));
    }

    [Fact]
    public void Rejects_capability_not_available_in_runtime()
    {
        var plan = CreatePlan();
        var runtime = CreateRuntime();
        runtime.Flags = Capabilities(enabled: true, disabled: "beam");

        var result = TeklaPlanValidator.Validate(plan, runtime);

        Assert.Contains(result.Diagnostics, item => item.Code == "TEKLA_RUNTIME_CAPABILITY_UNAVAILABLE");
    }

    [Theory]
    [InlineData("bend", true, false)]
    [InlineData("developedPlatePolyBeamStationFrameV1", false, true)]
    public void PolyBeam_requires_specialized_capabilities_for_extended_geometry(
        string unavailableCapability,
        bool includeBend,
        bool includeDevelopedPlate)
    {
        var plan = CreatePlan();
        ConfigurePolyBeam(plan.Commands[0], includeBend, includeDevelopedPlate);
        plan.Mappings[0].ExpectedNativeKinds = new[] { "PolyBeam" };
        var runtime = CreateRuntime();
        runtime.Flags = Capabilities(enabled: true, disabled: unavailableCapability);

        var result = TeklaPlanValidator.Validate(plan, runtime);

        Assert.Contains(
            result.Diagnostics,
            item => item.Code == "TEKLA_RUNTIME_CAPABILITY_UNAVAILABLE" &&
                    item.Message.Contains(unavailableCapability, StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_ownership_stamp_from_another_revision()
    {
        var plan = CreatePlan();
        plan.Commands[0].Ownership.RevisionId = "revision-other";

        var result = TeklaPlanValidator.Validate(plan, CreateRuntime());

        Assert.Contains(result.Diagnostics, item => item.Code == "TEKLA_PLAN_OWNERSHIP_INVALID");
    }

    [Fact]
    public void Rejects_dependency_that_appears_after_its_consumer()
    {
        var plan = CreatePlan();
        var first = plan.Commands[0];
        var second = CloneCommand(first, "command/beam-2", "beam-2", "element/beam-2");
        first.DependsOn = new[] { second.CommandId };
        plan.Commands = new[] { first, second };
        plan.Mappings = new[] { Mapping(first), Mapping(second) };

        var result = TeklaPlanValidator.Validate(plan, CreateRuntime());

        Assert.Contains(result.Diagnostics, item => item.Code == "TEKLA_PLAN_DEPENDENCY_ORDER_INVALID");
    }

    [Fact]
    public void Finds_forbidden_render_geometry_anywhere_in_payload()
    {
        using var json = JsonDocument.Parse("{\"payload\":{\"inlineGeometry\":{\"triangles\":[]}}}");

        Assert.True(TeklaPlanValidator.ContainsForbiddenGeometry(json.RootElement));
    }

    [Fact]
    public void Deserializer_preserves_command_specific_payload()
    {
        var json = JsonSerializer.Serialize(CreatePlan(), TeklaPlanJson.Options);

        var restored = TeklaPlanJson.Deserialize(json);

        Assert.True(restored.Commands[0].Payload.ContainsKey("profile"));
        Assert.Equal("PL20*200", restored.Commands[0].Payload["profile"].GetString());
    }

    private static TeklaPlanDocument CreatePlan()
    {
        var source = Source("beam-1", "element/beam-1");
        var command = new TeklaPlanCommand
        {
            CommandId = "command/beam-1",
            Kind = "create-beam",
            Phase = "base-parts",
            Source = source,
            Owner = new ConstructiveOwner { ModuleId = "bridge", EntityId = "beam-1" },
            RevisionProvenance = new ConstructiveRevisionProvenance
            {
                RevisionId = "revision-1",
                SourceHash = "source-hash",
                AdapterId = "bridge",
                AdapterVersion = "1.0.0",
            },
            Ownership = Ownership(source),
            Payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["start"] = JsonSerializer.SerializeToElement(new[] { 0d, 0d, 0d }),
                ["end"] = JsonSerializer.SerializeToElement(new[] { 1000d, 0d, 0d }),
                ["profile"] = JsonSerializer.SerializeToElement("PL20*200"),
                ["material"] = JsonSerializer.SerializeToElement("S355"),
            },
        };
        return new TeklaPlanDocument
        {
            SchemaVersion = TeklaPlanContract.SchemaVersion,
            Source = new TeklaPlanSource
            {
                Address = new ConstructiveAddress
                {
                    ProjectId = "project-1",
                    ModuleId = "bridge",
                    ModuleVariantId = "variant-1",
                    RevisionId = "revision-1",
                },
                ConstructiveSchemaVersion = 1,
                GenerationId = "generation-1",
                SourceHash = "source-hash",
                ConstructiveContentHash = "sha256:" + new string('a', 64),
            },
            Target = new TeklaPlanTarget
            {
                AdapterId = "tekla-native",
                AdapterVersion = "1.0.0",
                TeklaVersion = "2025.0",
            },
            Capabilities = Capabilities(enabled: true),
            Executable = true,
            Commands = new[] { command },
            Mappings = new[] { Mapping(command) },
        };
    }

    private static TeklaRuntimeCapabilities CreateRuntime() => new()
    {
        AdapterId = "tekla-native",
        AdapterVersion = "1.0.0",
        TeklaVersion = "2025.0",
        Flags = Capabilities(enabled: true),
    };

    private static Dictionary<string, bool> Capabilities(bool enabled, string? disabled = null)
    {
        var values = TeklaPlanContract.CapabilityNames.ToDictionary(name => name, _ => enabled, StringComparer.Ordinal);
        if (disabled is not null) values[disabled] = false;
        return values;
    }

    private static TeklaPlanSourceRef Source(string id, string stableKey) => new()
    {
        Kind = "element",
        Id = id,
        StableKey = stableKey,
        Role = "part",
        SourceLayer = "generated",
    };

    private static void ConfigurePolyBeam(
        TeklaPlanCommand command,
        bool includeBend,
        bool includeDevelopedPlate)
    {
        command.Kind = "create-poly-beam";
        command.Payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["path"] = JsonSerializer.SerializeToElement(new
            {
                id = "path-1",
                closed = false,
                nodes = new[]
                {
                    new { id = "n1", point = new[] { 0d, 0d, 0d } },
                    new { id = "n2", point = new[] { 1000d, 0d, 0d } },
                    new { id = "n3", point = new[] { 1000d, 1000d, 0d } },
                },
                segments = new[]
                {
                    new { id = "s1", kind = "line", startNodeId = "n1", endNodeId = "n2" },
                    new { id = "s2", kind = "line", startNodeId = "n2", endNodeId = "n3" },
                },
            }),
            ["profile"] = JsonSerializer.SerializeToElement(new { kind = "plate", thicknessMm = 20d, widthMm = 300d }),
            ["material"] = JsonSerializer.SerializeToElement(new { id = "S355", name = "S355", grade = "S355" }),
        };
        if (includeBend)
            command.Payload["bends"] = JsonSerializer.SerializeToElement(new[] { new { pathNodeId = "n2", radiusMm = 250d } });
        if (includeDevelopedPlate)
            command.Payload["developedPlate"] = DevelopedPlate();
    }

    private static JsonElement DevelopedPlate()
    {
        var diagonal = Math.Sqrt(0.5d);
        return JsonSerializer.SerializeToElement(new
        {
            thicknessMm = 20d,
            stockWidthMm = 300d,
            transverseOffsetMm = 0d,
            stationFrame = new
            {
                version = 1,
                stations = new[]
                {
                    new
                    {
                        id = "station-0",
                        spineNodeId = "n1",
                        stationMm = 0d,
                        frame = new { origin = new[] { 0d, 0d, 0d }, axisX = new[] { 0d, 1d, 0d }, axisY = new[] { 0d, 0d, 1d }, axisZ = new[] { 1d, 0d, 0d } },
                    },
                    new
                    {
                        id = "station-1000",
                        spineNodeId = "n2",
                        stationMm = 1000d,
                        frame = new { origin = new[] { 1000d, 0d, 0d }, axisX = new[] { -diagonal, diagonal, 0d }, axisY = new[] { 0d, 0d, 1d }, axisZ = new[] { diagonal, diagonal, 0d } },
                    },
                    new
                    {
                        id = "station-2000",
                        spineNodeId = "n3",
                        stationMm = 2000d,
                        frame = new { origin = new[] { 1000d, 1000d, 0d }, axisX = new[] { -1d, 0d, 0d }, axisY = new[] { 0d, 0d, 1d }, axisZ = new[] { 0d, 1d, 0d } },
                    },
                },
            },
        });
    }

    private static TeklaOwnershipStamp Ownership(TeklaPlanSourceRef source) => new()
    {
        Namespace = TeklaPlanContract.OwnershipNamespace,
        DocumentSourceHash = "source-hash",
        GenerationId = "generation-1",
        SourceKind = source.Kind,
        SourceId = source.Id,
        StableKey = source.StableKey,
        ModuleId = "bridge",
        ModuleVariantId = "variant-1",
        RevisionId = "revision-1",
    };

    private static TeklaPlanCommand CloneCommand(TeklaPlanCommand template, string commandId, string sourceId, string stableKey)
    {
        var source = Source(sourceId, stableKey);
        return new TeklaPlanCommand
        {
            CommandId = commandId,
            Kind = template.Kind,
            Phase = template.Phase,
            Source = source,
            Owner = new ConstructiveOwner { ModuleId = "bridge", EntityId = sourceId },
            RevisionProvenance = template.RevisionProvenance,
            Ownership = Ownership(source),
            Payload = new Dictionary<string, JsonElement>(template.Payload, StringComparer.Ordinal),
        };
    }

    private static TeklaPlanMapping Mapping(TeklaPlanCommand command) => new()
    {
        Source = command.Source,
        Strategy = "native-single",
        CommandIds = new[] { command.CommandId },
        ExpectedNativeKinds = new[] { "Beam" },
        OwnershipStableKey = command.Source.StableKey,
    };
}
