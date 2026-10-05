using System.Text.Json;
using Platform.Contracts.Geometry;
using Xunit;

namespace Platform.Contracts.Geometry.Tests;

public sealed class RoundTripTests
{
    [Fact]
    public void Vec3SerializesAsJsonArray()
    {
        var v = new Vec3(1, 2, 3);
        var json = JsonSerializer.Serialize(v, GeometryJson.Options);
        Assert.Equal("[1,2,3]", json);

        var back = JsonSerializer.Deserialize<Vec3>(json, GeometryJson.Options);
        Assert.Equal(v, back);
    }

    [Fact]
    public void TransformRoundTrip()
    {
        var t = Transform.Identity;
        var json = JsonSerializer.Serialize(t, GeometryJson.Options);
        Assert.Contains("\"position\":[0,0,0]", json);

        var back = JsonSerializer.Deserialize<Transform>(json, GeometryJson.Options);
        Assert.Equal(t, back);
    }

    [Fact]
    public void BoxSolidDiscriminatedRoundTrip()
    {
        SolidNode box = new Box(new Vec3(1, 1, 1), Transform.Identity);
        var json = JsonSerializer.Serialize(box, GeometryJson.Options);
        Assert.Contains("\"kind\":\"box\"", json);

        var back = JsonSerializer.Deserialize<SolidNode>(json, GeometryJson.Options);
        Assert.IsType<Box>(back);
        Assert.Equal(box, back);
    }

    [Fact]
    public void BooleanCompositeDiscriminatesNestedSolids()
    {
        SolidNode tree = new Boolean3(
            BooleanOp.Union,
            new Box(new Vec3(1, 1, 1), Transform.Identity),
            new Cylinder(0.5, 2, Transform.Identity));

        var json = JsonSerializer.Serialize(tree, GeometryJson.Options);
        Assert.Contains("\"kind\":\"boolean\"", json);
        Assert.Contains("\"kind\":\"box\"", json);
        Assert.Contains("\"kind\":\"cylinder\"", json);
        // BooleanOp -> camel-case enum
        Assert.Contains("\"op\":\"union\"", json);

        var back = JsonSerializer.Deserialize<SolidNode>(json, GeometryJson.Options);
        Assert.IsType<Boolean3>(back);
        Assert.Equal(tree, back);
    }

    [Fact]
    public void NamedSolidPreservesIdAndProps()
    {
        SolidNode named = new NamedSolid(
            Id: "bridge-girder.42.flange/top",
            Node: new Box(new Vec3(8, 0.05, 1.2), Transform.Identity),
            Material: new MaterialRef("steel"),
            Props: new Dictionary<string, object?> { ["layer"] = "beam-main" });

        var json = JsonSerializer.Serialize(named, GeometryJson.Options);
        Assert.Contains("\"kind\":\"named\"", json);
        Assert.Contains("\"id\":\"bridge-girder.42.flange/top\"", json);

        var back = JsonSerializer.Deserialize<SolidNode>(json, GeometryJson.Options);
        var n = Assert.IsType<NamedSolid>(back);
        Assert.Equal("bridge-girder.42.flange/top", n.Id);
        Assert.IsType<Box>(n.Node);
    }

    [Fact]
    public void GraphTopLevelRoundTripMatchesTsCamelCase()
    {
        var graph = new GeometryGraph(
            SchemaVersion: 1,
            Units: Units.M,
            WorldUp: WorldUp.Y,
            Materials: new Dictionary<string, Material>
            {
                ["steel"] = new Material("steel", "Steel S235"),
            },
            Roots: new SolidNode[]
            {
                new Sweep(
                    Profile: new RectProfile(0.5, 0.3),
                    Path: new Polyline(
                        Points: new[] { new Vec3(0, 0, 0), new Vec3(10, 0, 0) },
                        Closed: false),
                    Transform: Transform.Identity),
            });

        var json = JsonSerializer.Serialize(graph, GeometryJson.Options);
        Assert.Contains("\"schemaVersion\":1", json);
        Assert.Contains("\"units\":\"m\"", json);
        Assert.Contains("\"worldUp\":\"y\"", json);
        Assert.Contains("\"kind\":\"sweep\"", json);
        Assert.Contains("\"kind\":\"polyline\"", json);

        var back = JsonSerializer.Deserialize<GeometryGraph>(json, GeometryJson.Options);
        Assert.NotNull(back);
        Assert.Equal(1, back!.SchemaVersion);
        Assert.Equal(Units.M, back.Units);
        Assert.Single(back.Roots);
    }

    [Fact]
    public void ParsesTypeScriptShapedFixture()
    {
        // Этот фрагмент имитирует то, что TS-сторона эмитит, чтобы убедиться
        // что C# читает schema без переноса полей в snake_case или иначе.
        const string fixture = """
        {
          "schemaVersion": 1,
          "units": "m",
          "worldUp": "y",
          "materials": {
            "steel": { "id": "steel", "name": "Steel S235" }
          },
          "roots": [
            {
              "kind": "named",
              "id": "bridge-girder.42.web",
              "node": {
                "kind": "extrude",
                "profile": { "kind": "rect", "width": 0.5, "height": 1.2 },
                "direction": [1, 0, 0],
                "length": 8,
                "transform": {
                  "position": [0, 0, 0],
                  "rotation": [0, 0, 0],
                  "scale": [1, 1, 1]
                }
              }
            }
          ]
        }
        """;

        var graph = JsonSerializer.Deserialize<GeometryGraph>(fixture, GeometryJson.Options);
        Assert.NotNull(graph);
        Assert.Equal(1, graph!.SchemaVersion);
        var named = Assert.IsType<NamedSolid>(graph.Roots[0]);
        Assert.Equal("bridge-girder.42.web", named.Id);
        var ex = Assert.IsType<Extrude>(named.Node);
        Assert.Equal(0.5, ((RectProfile)ex.Profile).Width);
        Assert.Equal(8, ex.Length);
    }
}
