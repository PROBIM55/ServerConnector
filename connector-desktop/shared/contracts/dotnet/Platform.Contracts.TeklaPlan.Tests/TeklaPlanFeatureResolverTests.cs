using System.Text.Json;
using Platform.Contracts.TeklaPlan;
using Xunit;

namespace Platform.Contracts.TeklaPlan.Tests;

public sealed class TeklaPlanFeatureResolverTests
{
    [Theory]
    [InlineData("V", 1, 16d)]
    [InlineData("X", 2, 8d)]
    [InlineData("K", 2, 8d)]
    public void Ts_plan_roundtrip_prepares_complete_features_and_linked_weld(string type, int cutCount, double depth)
    {
        var plan = Load(type);
        plan = TeklaPlanJson.Deserialize(JsonSerializer.Serialize(plan, TeklaPlanJson.Options));
        var assembly = Command(plan, "create-assembly");
        var weld = Command(plan, "create-weld");
        var feature = TeklaPlanFeatureResolver.ResolveFeatureCommands(plan, assembly, "prep");
        var payload = TeklaPlanPayloads.ParseCreateWeld(weld);

        Assert.True(feature.Success, Messages(feature.Diagnostics));
        Assert.True(payload.Success, Messages(payload.Diagnostics));
        Assert.Equal(cutCount, feature.Value!.Count);
        var cutIds = feature.Value.Select(cut => cut.CommandId).ToArray();
        Assert.Equal(cutIds[0], payload.Value!.EdgePreparationCommandId);
        Assert.Equal(cutIds, TeklaPlanFeatureResolver.ResolveWeldPreparation(plan, weld, payload.Value)
            .Value!.Select(cut => cut.CommandId));
        Assert.Equal(cutCount, feature.Value.Select(cut => TeklaPlanOwnershipIdentity.Create(plan, cut)).Distinct().Count());
        Assert.All(feature.Value, cut =>
        {
            Assert.Contains(cut.CommandId, assembly.DependsOn);
            Assert.Contains(cut.CommandId, weld.DependsOn);
            Assert.Equal("prep", cut.Ownership.SourceId);
            Assert.Equal("bridge/contact/prep", cut.Ownership.StableKey);
            Assert.Equal("web-owner", cut.Owner.EntityId);
        });
        if (cutCount == 2) Assert.Contains(cutIds[0], feature.Value[1].DependsOn);

        for (var index = 0; index < cutCount; index++)
        {
            var cut = TeklaPlanPayloads.ParseApplyBooleanCut(feature.Value[index]);
            Assert.True(cut.Success, Messages(cut.Diagnostics));
            var prism = cut.Value!.Cutter;
            Assert.Equal("web", cut.Value.Target.ElementId);
            Assert.Equal("through-contour", prism.Kind);
            Assert.Equal("positive", prism.ExtrusionSide);
            Assert.Equal(100d, prism.ThicknessMm);
            Assert.Equal(3, prism.Contour.Vertices.Count);
            Assert.Equal(0d, prism.Contour.Vertices[0].Point.X);
            Assert.Equal(0d, prism.Contour.Vertices[0].Point.Y);
            Assert.Equal(depth * Math.Tan(Math.PI / 6), prism.Contour.Vertices[1].Point.X, 10);
            Assert.Equal(0d, prism.Contour.Vertices[1].Point.Y);
            Assert.Equal(0d, prism.Contour.Vertices[2].Point.X);
            Assert.Equal(depth, prism.Contour.Vertices[2].Point.Y);
            Assert.All(prism.Contour.Edges, edge => Assert.Equal("line", edge.Kind));
            var sign = index == 0 ? 1 : -1;
            Vector(prism.Plane.Origin, 120 + (index == 0 ? 80 : 0) + sign * 3 * Math.Sqrt(3),
                -70 + (index == 0 ? 60 : 0) - sign * 4 * Math.Sqrt(3), 45 + sign * 5);
            Vector(prism.Plane.AxisX, -0.3, 0.4, Math.Sqrt(0.75));
            Vector(prism.Plane.AxisY, -sign * 0.3 * Math.Sqrt(3), sign * 0.4 * Math.Sqrt(3), -sign * 0.5);
            Vector(prism.Plane.AxisZ, -sign * 0.8, -sign * 0.6, 0);
            Vector(new TeklaVector3(prism.Plane.Origin.X + prism.Plane.AxisZ.X * prism.ThicknessMm,
                    prism.Plane.Origin.Y + prism.Plane.AxisZ.Y * prism.ThicknessMm, prism.Plane.Origin.Z),
                120 + (index == 0 ? 0 : 80) + sign * 3 * Math.Sqrt(3),
                -70 + (index == 0 ? 0 : 60) - sign * 4 * Math.Sqrt(3), 45 + sign * 5);
        }
        var prepared = Prepare(plan);
        Assert.True(prepared.Prepared, Messages(prepared.Diagnostics));
        Assert.Equal(plan.Commands.Select(command => command.CommandId), prepared.Bindings.Select(binding => binding.CommandId));
    }

    [Theory]
    [InlineData("create-assembly")]
    [InlineData("create-weld")]
    public void Missing_second_cut_dependency_is_rejected_by_resolver_and_preflight(string kind)
    {
        var plan = Load("X");
        var operation = Command(plan, kind);
        var secondCut = Cuts(plan)[1];
        operation.DependsOn = operation.DependsOn.Where(id => id != secondCut.CommandId).ToArray();

        Assert.Contains(TeklaPlanFeatureResolver.ValidateReferences(plan, operation),
            item => item.Code == "TEKLA_PLAN_NATIVE_FEATURE_DEPENDENCY_MISSING");
        AssertRejected(plan, operation, "TEKLA_PLAN_NATIVE_FEATURE_DEPENDENCY_MISSING");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("partial")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-mapping")]
    [InlineData("strategy")]
    [InlineData("source-key")]
    [InlineData("ownership-key")]
    public void Composite_resolution_does_not_hide_incomplete_or_ambiguous_mapping(string corruption)
    {
        var plan = Load("X");
        var mapping = plan.Mappings.Single(item => item.Source.Id == "prep");
        if (corruption == "missing") plan.Mappings = plan.Mappings.Where(item => item != mapping).ToArray();
        if (corruption == "partial") mapping.CommandIds = mapping.CommandIds.Take(1).ToArray();
        if (corruption == "duplicate-id") mapping.CommandIds[1] = mapping.CommandIds[0];
        if (corruption == "duplicate-mapping") plan.Mappings = plan.Mappings.Append(mapping).ToArray();
        if (corruption == "strategy") mapping.Strategy = "native-single";
        if (corruption == "source-key") Cuts(plan)[1].Source.StableKey = "other-source";
        if (corruption == "ownership-key") mapping.OwnershipStableKey = "other-source";

        var assembly = Command(plan, "create-assembly");
        Assert.False(TeklaPlanFeatureResolver.ResolveFeatureCommands(plan, assembly, "prep").Success);
        AssertRejected(plan, assembly, "TEKLA_PLAN_NATIVE_FEATURE_MAPPING_INVALID");
    }

    [Fact]
    public void Single_features_and_second_command_anchors_resolve_in_plan_order()
    {
        var plan = Load("K");
        var assembly = Command(plan, "create-assembly");
        var weld = Command(plan, "create-weld");
        Assert.Same(weld, Assert.Single(TeklaPlanFeatureResolver.ResolveFeatureCommands(plan, assembly, "weld").Value!));
        plan.Mappings.Single(item => item.Source.Id == "prep").CommandIds = Cuts(plan).Reverse().Select(item => item.CommandId).ToArray();
        weld.Payload["edgePreparationCommandId"] = JsonSerializer.SerializeToElement(Cuts(plan)[1].CommandId);
        var payload = TeklaPlanPayloads.ParseCreateWeld(weld).Value!;
        Assert.Equal(Cuts(plan), TeklaPlanFeatureResolver.ResolveWeldPreparation(plan, weld, payload).Value);
        Assert.True(Prepare(plan).Prepared);
    }

    [Theory]
    [InlineData("missing", "TEKLA_PLAN_WELD_PREPARATION_REFERENCE_INVALID")]
    [InlineData("element", "TEKLA_PLAN_WELD_PREPARATION_REFERENCE_INVALID")]
    [InlineData("non-cut", "TEKLA_PLAN_WELD_PREPARATION_COMMAND_UNSUPPORTED")]
    [InlineData("target", "TEKLA_PLAN_WELD_PREPARATION_TARGET_MISMATCH")]
    [InlineData("mixed-targets", "TEKLA_PLAN_WELD_PREPARATION_TARGET_MISMATCH")]
    public void Weld_link_must_resolve_to_complete_cuts_on_one_weld_part(string corruption, string code)
    {
        var plan = Load("X");
        var weld = Command(plan, "create-weld");
        if (corruption == "missing") weld.Payload["edgePreparationCommandId"] = JsonSerializer.SerializeToElement("absent");
        if (corruption == "element") weld.Payload["edgePreparationCommandId"] = JsonSerializer.SerializeToElement(plan.Commands[0].CommandId);
        if (corruption == "non-cut") Cuts(plan)[1].Kind = "apply-edge-treatment";
        if (corruption is "target" or "mixed-targets")
            Cuts(plan)[1].Payload["target"] = JsonSerializer.SerializeToElement(new
            {
                elementId = corruption == "target" ? "unrelated-part" : "support", role = "part",
            });
        AssertRejected(plan, weld, code);
    }

    [Fact]
    public void Weld_link_can_prepare_secondary_part_and_unlinked_weld_stays_supported()
    {
        var plan = Load("V");
        var weld = Command(plan, "create-weld");
        weld.Payload["target"] = JsonSerializer.SerializeToElement(new { elementId = "support", role = "part" });
        weld.Payload["participants"] = JsonSerializer.SerializeToElement(new[] { new { elementId = "web", role = "part" } });
        Assert.True(Prepare(plan).Prepared);

        weld.Payload.Remove("edgePreparationCommandId");
        Assert.Null(TeklaPlanPayloads.ParseCreateWeld(weld).Value!.EdgePreparationCommandId);
        Assert.True(Prepare(plan).Prepared);
    }

    [Fact]
    public void Assembly_missing_feature_stays_blocked()
    {
        var plan = Load("V");
        var assembly = Command(plan, "create-assembly");
        assembly.Payload["featureIds"] = JsonSerializer.SerializeToElement(new[] { "absent" });
        AssertRejected(plan, assembly, "TEKLA_PLAN_NATIVE_FEATURE_COMMAND_MISSING");
    }

    [Fact]
    public void Linked_weld_does_not_bypass_boolean_capability()
    {
        var plan = Load("X");
        plan.Capabilities["booleanPart"] = false;
        var result = Prepare(plan);
        Assert.False(result.Prepared);
        Assert.Contains(result.Diagnostics, item => item.Code == "TEKLA_PLAN_CAPABILITY_NOT_DECLARED");
    }

    private static TeklaPlanDocument Load(string type)
        => TeklaPlanJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"edge-preparation-{type}.json")));

    private static TeklaPlanCommand Command(TeklaPlanDocument plan, string kind) => plan.Commands.Single(item => item.Kind == kind);
    private static TeklaPlanCommand[] Cuts(TeklaPlanDocument plan) => plan.Commands.Where(item => item.Source.Id == "prep").ToArray();
    private static string Messages(IEnumerable<TeklaPlanValidationDiagnostic> diagnostics)
        => string.Join(Environment.NewLine, diagnostics.Select(item => $"{item.Code}: {item.Message}"));

    private static void Vector(TeklaVector3 value, double x, double y, double z)
    {
        Assert.Equal(x, value.X, 10);
        Assert.Equal(y, value.Y, 10);
        Assert.Equal(z, value.Z, 10);
    }

    private static void AssertRejected(TeklaPlanDocument plan, TeklaPlanCommand operation, string code)
    {
        var result = Prepare(plan);
        Assert.False(result.Prepared);
        Assert.Contains(result.Diagnostics, item => item.Code == code && item.CommandId == operation.CommandId);
        Assert.DoesNotContain(result.Bindings, item => item.CommandId == operation.CommandId);
    }

    private static TeklaPlanPreparationResult Prepare(TeklaPlanDocument plan)
    {
        var registry = new TeklaPlanExecutorRegistry(new[]
        {
            (Kind: "create-contour-plate", Capability: "contourPlate"),
            (Kind: "apply-boolean-cut", Capability: "booleanPart"),
            (Kind: "create-weld", Capability: "weld"),
            (Kind: "create-assembly", Capability: "assembly"),
        }.Select(item => new TeklaPlanExecutorRegistration($"contract-test.{item.Kind}", item.Kind,
            new[] { item.Capability, "udaStamp" }, applyReady: true)));
        return TeklaPlanPreparer.Prepare(plan, registry.CreateRuntimeCapabilities("tekla-native", "1.0.0", "2025.0"), registry);
    }
}
