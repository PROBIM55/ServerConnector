using System.Text.Json;
using Platform.Contracts.TeklaPlan;
using Xunit;

namespace Platform.Contracts.TeklaPlan.Tests;

public sealed class TeklaPlanPreparerTests
{
    [Fact]
    public void Empty_registry_is_deny_by_default()
    {
        var registry = TeklaPlanExecutorRegistry.Empty;
        var runtime = registry.CreateRuntimeCapabilities("tekla-native", "1.0.0", "2025.0");

        var result = TeklaPlanPreparer.Prepare(CreatePlan(1), runtime, registry);

        Assert.False(result.Prepared);
        Assert.False(runtime.Flags["beam"]);
        Assert.False(runtime.Flags["udaStamp"]);
        Assert.Contains(result.Diagnostics, item => item.Code == "TEKLA_PLAN_EXECUTOR_UNAVAILABLE");
    }

    [Fact]
    public void Apply_ready_registration_enables_only_owned_capabilities()
    {
        var registry = BeamRegistry();
        var runtime = registry.CreateRuntimeCapabilities("tekla-native", "1.0.0", "2025.0");

        Assert.True(runtime.Flags["beam"]);
        Assert.True(runtime.Flags["udaStamp"]);
        Assert.False(runtime.Flags["polyBeam"]);
    }

    [Fact]
    public void Prepare_binds_each_command_to_the_exact_executor_in_plan_order()
    {
        var registry = BeamRegistry();
        var runtime = registry.CreateRuntimeCapabilities("tekla-native", "1.0.0", "2025.0");

        var result = TeklaPlanPreparer.Prepare(CreatePlan(2), runtime, registry);

        Assert.True(result.Prepared, string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Message)));
        Assert.Equal(new[] { "command/beam-1", "command/beam-2" }, result.Bindings.Select(item => item.CommandId));
        Assert.All(result.Bindings, item => Assert.Equal("native-beam-v1", item.ExecutorId));
    }

    [Fact]
    public void Duplicate_command_executor_is_rejected()
    {
        var first = BeamRegistration("beam-one");
        var second = BeamRegistration("beam-two");

        var error = Assert.Throws<ArgumentException>(() => new TeklaPlanExecutorRegistry(new[] { first, second }));

        Assert.Contains("more than one executor", error.Message);
    }

    [Fact]
    public void Apply_ready_executor_must_own_required_capability_and_stamp()
    {
        var withoutBeam = new TeklaPlanExecutorRegistration(
            "invalid-beam",
            "create-beam",
            new[] { "udaStamp" },
            applyReady: true);
        var withoutStamp = new TeklaPlanExecutorRegistration(
            "invalid-stamp",
            "create-beam",
            new[] { "beam" },
            applyReady: true);

        Assert.Throws<ArgumentException>(() => new TeklaPlanExecutorRegistry(new[] { withoutBeam }));
        Assert.Throws<ArgumentException>(() => new TeklaPlanExecutorRegistry(new[] { withoutStamp }));
    }

    [Fact]
    public void Non_ready_registration_cannot_advertise_or_bind()
    {
        var registry = new TeklaPlanExecutorRegistry(new[]
        {
            new TeklaPlanExecutorRegistration(
                "beam-spike",
                "create-beam",
                new[] { "beam", "udaStamp" },
                applyReady: false),
        });
        var runtime = registry.CreateRuntimeCapabilities("tekla-native", "1.0.0", "2025.0");

        Assert.False(runtime.Flags["beam"]);
        Assert.False(registry.TryResolve("create-beam", out _));
    }

    [Fact]
    public void Create_beam_payload_is_parsed_without_Tekla_API()
    {
        var command = CreateCommand(1);

        var result = TeklaPlanPayloads.ParseCreateBeam(command);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal(0, result.Value!.Start.X);
        Assert.Equal(1000, result.Value.End.X);
        Assert.Equal("catalog", result.Value.Profile.Kind);
        Assert.Equal("HEA300", result.Value.Profile.Name);
        Assert.Equal("S355", result.Value.Material.Id);
    }

    [Fact]
    public void Create_beam_plate_profile_and_placement_are_typed()
    {
        var command = CreateCommand(1);
        command.Payload["profile"] = JsonSerializer.SerializeToElement(new
        {
            kind = "plate",
            thicknessMm = 20d,
            widthMm = 300d,
        });
        command.Payload["placement"] = JsonSerializer.SerializeToElement(new
        {
            anchor = "top",
            offsetXmm = 15d,
            offsetYmm = -25d,
            rotationDeg = 12.5d,
        });

        var result = TeklaPlanPayloads.ParseCreateBeam(command);

        Assert.True(result.Success);
        Assert.Equal(20, result.Value!.Profile.ThicknessMm);
        Assert.Equal(300, result.Value.Profile.WidthMm);
        Assert.Equal("top", result.Value.Placement!.Anchor);
        Assert.Equal(12.5, result.Value.Placement.RotationDeg);
    }

    [Fact]
    public void Create_beam_rejects_zero_length_and_non_finite_coordinates()
    {
        var zeroLength = CreateCommand(1);
        zeroLength.Payload["end"] = zeroLength.Payload["start"];
        var nonFinite = CreateCommand(2);
        nonFinite.Payload["start"] = JsonSerializer.SerializeToElement(new object[] { 0d, "NaN", 0d });

        var zeroLengthResult = TeklaPlanPayloads.ParseCreateBeam(zeroLength);
        var nonFiniteResult = TeklaPlanPayloads.ParseCreateBeam(nonFinite);

        Assert.False(zeroLengthResult.Success);
        Assert.Contains(zeroLengthResult.Diagnostics, item => item.Code == "TEKLA_PLAN_BEAM_ZERO_LENGTH");
        Assert.False(nonFiniteResult.Success);
        Assert.Contains(nonFiniteResult.Diagnostics, item => item.Code == "TEKLA_PLAN_VECTOR_INVALID");
    }

    [Fact]
    public void Preflight_does_not_bind_an_invalid_native_payload()
    {
        var plan = CreatePlan(1);
        plan.Commands[0].Payload["profile"] = JsonSerializer.SerializeToElement(new
        {
            kind = "plate",
            thicknessMm = -20d,
            widthMm = 300d,
        });
        var registry = BeamRegistry();
        var runtime = registry.CreateRuntimeCapabilities("tekla-native", "1.0.0", "2025.0");

        var result = TeklaPlanPreparer.Prepare(plan, runtime, registry);

        Assert.False(result.Prepared);
        Assert.Empty(result.Bindings);
        Assert.Contains(result.Diagnostics, item => item.Code == "TEKLA_PLAN_PROFILE_INVALID");
    }

    private static TeklaPlanExecutorRegistry BeamRegistry()
        => new(new[] { BeamRegistration("native-beam-v1") });

    private static TeklaPlanExecutorRegistration BeamRegistration(string executorId)
        => new(
            executorId,
            "create-beam",
            new[] { "beam", "udaStamp" },
            applyReady: true);

    private static TeklaPlanDocument CreatePlan(int commandCount)
    {
        var commands = Enumerable.Range(1, commandCount).Select(CreateCommand).ToArray();
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
            Capabilities = TeklaPlanContract.CapabilityNames.ToDictionary(name => name, _ => true, StringComparer.Ordinal),
            Executable = true,
            Commands = commands,
            Mappings = commands.Select(command => new TeklaPlanMapping
            {
                Source = command.Source,
                Strategy = "native-single",
                CommandIds = new[] { command.CommandId },
                ExpectedNativeKinds = new[] { "Beam" },
                OwnershipStableKey = command.Source.StableKey,
            }).ToArray(),
        };
    }

    private static TeklaPlanCommand CreateCommand(int index)
    {
        var source = new TeklaPlanSourceRef
        {
            Kind = "element",
            Id = $"beam-{index}",
            StableKey = $"element/beam-{index}",
            Role = "part",
            SourceLayer = "generated",
        };
        return new TeklaPlanCommand
        {
            CommandId = $"command/beam-{index}",
            Kind = "create-beam",
            Phase = "base-parts",
            Source = source,
            Owner = new ConstructiveOwner { ModuleId = "bridge", EntityId = source.Id },
            RevisionProvenance = new ConstructiveRevisionProvenance
            {
                RevisionId = "revision-1",
                SourceHash = "source-hash",
                AdapterId = "bridge",
                AdapterVersion = "1.0.0",
            },
            Ownership = new TeklaOwnershipStamp
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
            },
            Payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["start"] = JsonSerializer.SerializeToElement(new[] { 0d, index * 100d, 0d }),
                ["end"] = JsonSerializer.SerializeToElement(new[] { 1000d, index * 100d, 0d }),
                ["profile"] = JsonSerializer.SerializeToElement(new { kind = "catalog", name = "HEA300" }),
                ["material"] = JsonSerializer.SerializeToElement(new { id = "S355", grade = "S355" }),
            },
        };
    }
}
