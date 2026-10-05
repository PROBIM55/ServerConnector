#nullable enable

using System;
using System.Collections.Generic;
using Platform.Bridge.Geometry.Geometry3d;

namespace TeklaBridge.CrossMembers;

public sealed class CrossMemberPayloadV1
{
    public string Id { get; set; } = "";
    public string LineId { get; set; } = "";
    public string? Kind { get; set; }
    public string? TypeId { get; set; }
    public string? TypeLabel { get; set; }
    public string? PatternId { get; set; }
    public string? Source { get; set; }
    public double? Station { get; set; }
    public double? SkewDeg { get; set; }
    public CrossMemberPlateSet Plates { get; set; } = new();
    public List<CrossMemberCutoutDto> Cutouts { get; set; } = new();
    public List<CrossMemberSpliceDto> Splices { get; set; } = new();

    public IEnumerable<CrossMemberPlateDto> AllPlates()
    {
        foreach (var plate in Plates.Web) yield return plate;
        foreach (var plate in Plates.Flange) yield return plate;
        foreach (var plate in Plates.SpliceCover) yield return plate;
    }
}

public sealed class CrossMemberPlateSet
{
    public List<CrossMemberPlateDto> Web { get; set; } = new();
    public List<CrossMemberPlateDto> Flange { get; set; } = new();
    public List<CrossMemberPlateDto> SpliceCover { get; set; } = new();
}

public sealed class CrossMemberPlateDto
{
    public string Id { get; set; } = "";
    public List<CrossMemberPointDto> Outline { get; set; } = new();
    public double Thickness { get; set; }
    public string? MaterialId { get; set; }

    public List<Vec3> ToContour()
    {
        var points = new List<Vec3>(Outline.Count);
        foreach (var point in Outline)
            points.Add(point.ToVec3());
        return points;
    }
}

public sealed class CrossMemberPointDto
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    public Vec3 ToVec3() => new(X, Y, Z);
}

public sealed class CrossMemberCutoutDto
{
    public string Id { get; set; } = "";
}

public sealed class CrossMemberSpliceDto
{
    public string Id { get; set; } = "";
    public List<CrossMemberBoltDto> Bolts { get; set; } = new();
}

public sealed class CrossMemberBoltDto
{
    public string Id { get; set; } = "";
}
