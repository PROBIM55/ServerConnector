using System.Text.Json;
using System.Text.Json.Nodes;
using Platform.Contracts.TeklaPlan;
using Xunit;

namespace Platform.Contracts.TeklaPlan.Tests;

public sealed class TeklaDevelopedPlateReadbackTests
{
    [Fact]
    public void Final_chamfered_stock_passes_after_all_apply_without_disabling_raw_checks()
    {
        var fixture = Load();
        var command = fixture.Plan.Commands[0];
        var developed = Developed(fixture);
        var end = developed.StationFrame.Stations.Last();
        var rawVolume = TeklaDevelopedPlateReadback.MeasureVolume(fixture.RawFaces);
        var finalVolume = TeklaDevelopedPlateReadback.MeasureVolume(fixture.FinalFaces);
        Assert.Equal(12_000_000, rawVolume, 5);
        Assert.Equal(11_800_000, finalVolume, 5);
        Assert.Equal(200_000, TeklaDevelopedPlateReadback.ExpectedRemovedVolume(developed), 5);
        Width(developed, 2000, -50, 50);
        Width(developed, 1950, -100, 100);
        Width(developed, 1900, -150, 150);
        Assert.Empty(TeklaDevelopedPlateReadback.ValidateEndSection(command, developed, end, Vertices(fixture.FinalFaces)));
        var stock = new TeklaDevelopedPlateSpec
        {
            StockWidthMm = developed.StockWidthMm, ThicknessMm = developed.ThicknessMm,
            StationFrame = developed.StationFrame, TransverseOffsetMm = developed.TransverseOffsetMm,
        };
        Assert.Empty(TeklaDevelopedPlateReadback.ValidateEndSection(command, stock, end, Vertices(fixture.RawFaces)));
        Assert.Contains(TeklaDevelopedPlateReadback.ValidateEndSection(command, stock, end, Vertices(fixture.FinalFaces)),
            diagnostic => diagnostic.Code == "TEKLA_PLAN_READBACK_MISMATCH");

        IReadOnlyList<TeklaSolidFaceReadback> current = fixture.RawFaces;
        var events = new List<string>();
        var mutations = new[]
        {
            new Mutation("stock", () => events.Add("stock.apply"), () =>
            {
                events.Add("stock.readback");
                Assert.Equal(fixture.FinalFaces, current);
                Assert.Empty(TeklaDevelopedPlateReadback.ValidateEndSection(command, developed, end, Vertices(current)));
                Assert.Empty(TeklaDevelopedPlateReadback.ValidateVolumes(command, developed, rawVolume, TeklaDevelopedPlateReadback.MeasureVolume(current)));
            }),
            new Mutation("cut-1", () => events.Add("cut-1.apply")),
            new Mutation("cut-2", () => { events.Add("cut-2.apply"); current = fixture.FinalFaces; }),
        };
        TeklaPlanTransaction.Execute(mutations, () => events.Add("commit"));
        Assert.Equal(new[] { "stock.apply", "cut-1.apply", "cut-2.apply", "stock.readback", "commit" }, events);
        Assert.True(Prepare(fixture.Plan).Prepared);
    }

    [Theory]
    [InlineData("plane")]
    [InlineData("side")]
    [InlineData("thickness")]
    public void Final_readback_rejects_wrong_plane_signed_side_and_thickness(string defect)
    {
        var fixture = Load();
        var developed = Developed(fixture);
        var changed = Transform(fixture.FinalFaces, point => defect switch
        {
            "plane" => new TeklaVector3(point.X + 10, point.Y, point.Z),
            "side" => new TeklaVector3(point.X, point.Y, point.Z + 10),
            _ => new TeklaVector3(point.X, point.Y, point.Z * 0.5),
        });
        var diagnostics = TeklaDevelopedPlateReadback.ValidateEndSection(fixture.Plan.Commands[0], developed,
            developed.StationFrame.Stations.Last(), Vertices(changed));
        Assert.NotEmpty(diagnostics);
        // Translation preserves the volume, so these failures cannot be replaced by a volume-only check.
        if (defect != "thickness") Assert.Equal(11_800_000, TeklaDevelopedPlateReadback.MeasureVolume(changed), 5);
    }

    [Fact]
    public void Additional_internal_material_removal_fails_even_with_correct_end_bounds()
    {
        var fixture = Load();
        var developed = Developed(fixture);
        var cavity = Transform(fixture.RawFaces, p => new TeklaVector3(p.X / 200 + 500, p.Y / 30, p.Z / 2));
        foreach (var face in cavity) face.Normal = new TeklaVector3(-face.Normal.X, -face.Normal.Y, -face.Normal.Z);
        var overcut = fixture.FinalFaces.Concat(cavity).ToArray();
        Assert.Empty(TeklaDevelopedPlateReadback.ValidateEndSection(fixture.Plan.Commands[0], developed,
            developed.StationFrame.Stations.Last(), Vertices(overcut)));
        var raw = TeklaDevelopedPlateReadback.MeasureVolume(fixture.RawFaces);
        var final = TeklaDevelopedPlateReadback.MeasureVolume(overcut);
        Assert.Equal(11_799_000, final, 5);
        Assert.Contains(TeklaDevelopedPlateReadback.ValidateVolumes(fixture.Plan.Commands[0], developed, raw, final),
            diagnostic => diagnostic.Code == "TEKLA_PLAN_READBACK_MISMATCH");
        Assert.NotEmpty(TeklaDevelopedPlateReadback.ValidateVolumes(fixture.Plan.Commands[0], developed, raw, raw));
    }

    [Fact]
    public void Readback_is_invariant_under_rigid_placement_and_loop_winding()
    {
        var fixture = Load();
        var developed = Developed(fixture);
        TeklaVector3 Rotate(TeklaVector3 p) => new(p.Z, p.X, p.Y);
        TeklaVector3 Place(TeklaVector3 p) => new(p.Z + 12000, p.X - 4200, p.Y + 789);
        var faces = Transform(fixture.FinalFaces, Place);
        for (var i = 0; i < faces.Length; i++)
        {
            faces[i].Normal = Rotate(faces[i].Normal);
            faces[i].Loops = faces[i].Loops.Select(loop => (IReadOnlyList<TeklaVector3>)loop.Reverse().ToArray()).ToArray();
        }
        foreach (var station in developed.StationFrame.Stations)
        {
            station.Frame.Origin = Place(station.Frame.Origin);
            station.Frame.AxisX = Rotate(station.Frame.AxisX);
            station.Frame.AxisY = Rotate(station.Frame.AxisY);
            station.Frame.AxisZ = Rotate(station.Frame.AxisZ);
        }
        Assert.Equal(11_800_000, TeklaDevelopedPlateReadback.MeasureVolume(faces), 5);
        Assert.Empty(TeklaDevelopedPlateReadback.ValidateEndSection(fixture.Plan.Commands[0], developed,
            developed.StationFrame.Stations.Last(), Vertices(faces)));
    }

    [Theory]
    [InlineData("plane")]
    [InlineData("side")]
    [InlineData("thickness")]
    [InlineData("target")]
    [InlineData("dependency")]
    public void Preflight_rejects_cut_contract_drift(string defect)
    {
        var fixture = Load();
        var cut = fixture.Plan.Commands.First(command => command.Kind == "apply-boolean-cut");
        var cutter = JsonNode.Parse(cut.Payload["cutter"].GetRawText())!;
        if (defect == "plane") cutter["plane"]!["origin"]![0] = 10;
        if (defect == "side") cutter["extrusionSide"] = "positive";
        if (defect == "thickness") cutter["thicknessMm"] = 10;
        if (defect == "target") cut.Payload["target"] = JsonSerializer.SerializeToElement(new { elementId = "other", role = "part" });
        if (defect == "dependency") cut.DependsOn = Array.Empty<string>();
        cut.Payload["cutter"] = JsonSerializer.SerializeToElement(cutter);
        var result = Prepare(fixture.Plan);
        Assert.False(result.Prepared);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TEKLA_PLAN_DEVELOPED_READBACK_CUTS_INVALID");
        Assert.DoesNotContain(result.Bindings, binding => binding.CommandId == fixture.Plan.Commands[0].CommandId);
    }

    [Fact]
    public void Missing_final_intent_is_blocked_but_legacy_uncut_stock_remains_valid()
    {
        var fixture = Load();
        var command = fixture.Plan.Commands[0];
        var developed = JsonNode.Parse(command.Payload["developedPlate"].GetRawText())!.AsObject();
        developed.Remove("developedContour");
        command.Payload["developedPlate"] = JsonSerializer.SerializeToElement(developed);
        Assert.Contains(Prepare(fixture.Plan).Diagnostics, d => d.Code == "TEKLA_PLAN_DEVELOPED_READBACK_EVIDENCE_MISSING");
        fixture.Plan.Commands = new[] { command };
        fixture.Plan.Mappings[0].Strategy = "native-single";
        fixture.Plan.Mappings[0].CommandIds = new[] { command.CommandId };
        fixture.Plan.Mappings[0].ExpectedNativeKinds = new[] { "PolyBeam" };
        Assert.True(Prepare(fixture.Plan).Prepared);
        Width(Developed(fixture), 2000, -150, 150);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("123")]
    public void Malformed_final_contour_fails_closed(string json)
    {
        var fixture = Load();
        var command = fixture.Plan.Commands[0];
        var developed = JsonNode.Parse(command.Payload["developedPlate"].GetRawText())!;
        developed["developedContour"] = JsonNode.Parse(json);
        command.Payload["developedPlate"] = JsonSerializer.SerializeToElement(developed);
        Assert.Contains(Prepare(fixture.Plan).Diagnostics, d => d.Code == "TEKLA_PLAN_DEVELOPED_READBACK_CONTOUR_INVALID");
    }

    [Fact]
    public void Actual_22_station_cut_plan_roundtrips_and_prepares()
    {
        var fixture = Load("developed-plate-actual");
        fixture.Plan = TeklaPlanJson.Deserialize(JsonSerializer.Serialize(fixture.Plan, TeklaPlanJson.Options));
        var developed = Developed(fixture);
        Assert.Equal(22, developed.StationFrame.Stations.Count);
        Assert.Equal(44, developed.DevelopedContour!.Vertices.Count);
        Assert.Equal(2722.4898131438995, developed.StationFrame.Stations.Last().StationMm, 6);
        Assert.Equal(200_000, TeklaDevelopedPlateReadback.ExpectedRemovedVolume(developed), 5);
        Width(developed, developed.StationFrame.Stations.Last().StationMm, -50, 50);
        var result = Prepare(fixture.Plan);
        Assert.True(result.Prepared, string.Join("\n", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    private static void Width(TeklaDevelopedPlateSpec developed, double station, double low, double high)
    {
        var width = TeklaDevelopedPlateReadback.WidthAt(developed, station);
        Assert.Equal(low, width.X, 6);
        Assert.Equal(high, width.Y, 6);
    }
    private static TeklaVector3[] Vertices(IEnumerable<TeklaSolidFaceReadback> faces) => faces.SelectMany(face => face.Loops.SelectMany(loop => loop)).ToArray();
    private static TeklaSolidFaceReadback[] Transform(IEnumerable<TeklaSolidFaceReadback> faces, Func<TeklaVector3, TeklaVector3> transform)
        => faces.Select(face => new TeklaSolidFaceReadback { Normal = face.Normal, Loops = face.Loops.Select(loop => (IReadOnlyList<TeklaVector3>)loop.Select(transform).ToArray()).ToArray() }).ToArray();
    private static Fixture Load(string name = "developed-plate-processed")
        => JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")), TeklaPlanJson.Options)!;
    private static TeklaDevelopedPlateSpec Developed(Fixture fixture) => TeklaPlanPayloads.ParseCreatePolyBeam(fixture.Plan.Commands[0]).Value!.DevelopedPlate!;
    private static TeklaPlanPreparationResult Prepare(TeklaPlanDocument plan)
    {
        var registry = new TeklaPlanExecutorRegistry(new[]
        {
            new TeklaPlanExecutorRegistration("test-polybeam", "create-poly-beam", new[] { "polyBeam", "developedPlate", "developedPlatePolyBeamStationFrameV1", "udaStamp" }, true),
            new TeklaPlanExecutorRegistration("test-cut", "apply-boolean-cut", new[] { "booleanPart", "udaStamp" }, true),
        });
        return TeklaPlanPreparer.Prepare(plan, registry.CreateRuntimeCapabilities("native", "1", "2025.0"), registry);
    }
    public sealed class Fixture
    {
        public TeklaPlanDocument Plan { get; set; } = new();
        public TeklaSolidFaceReadback[] RawFaces { get; set; } = Array.Empty<TeklaSolidFaceReadback>();
        public TeklaSolidFaceReadback[] FinalFaces { get; set; } = Array.Empty<TeklaSolidFaceReadback>();
    }
    private sealed class Mutation : ITeklaPreparedMutation<string>
    {
        private readonly Action _apply;
        private readonly Action? _read;
        public Mutation(string id, Action apply, Action? read = null) { CommandId = id; _apply = apply; _read = read; }
        public string CommandId { get; }
        public void Apply() => _apply();
        public string Readback() { _read?.Invoke(); return CommandId; }
        public void Restore() => throw new InvalidOperationException("Valid processed readback must not roll back.");
    }
}
