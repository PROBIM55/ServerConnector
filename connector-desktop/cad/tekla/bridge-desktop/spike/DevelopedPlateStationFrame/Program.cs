using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Solid;

namespace DevelopedPlateStationFrame
{
    internal static class Program
    {
        private const double CoordinateOffset = 350000.0;

        private static int Main()
        {
            var reportDirectory = Path.Combine(Path.GetTempPath(), "DevelopedPlateStationFrame");
            Directory.CreateDirectory(reportDirectory);
            var reportPath = Path.Combine(reportDirectory, "report.txt");
            using var report = new StreamWriter(reportPath, append: false) { AutoFlush = true };
            var created = new List<PolyBeam>();
            Model? model = null;
            TransformationPlane? previousPlane = null;

            void Log(string message)
            {
                var line = $"[{DateTime.UtcNow:O}] {message}";
                Console.WriteLine(line);
                report.WriteLine(line);
            }

            try
            {
                model = new Model();
                if (!model.GetConnectionStatus())
                {
                    Log("ERROR Tekla 2025 model is not connected.");
                    return 10;
                }

                var modelInfo = model.GetInfo();
                Log($"MODEL name={modelInfo.ModelName} path={modelInfo.ModelPath}");
                LogExistingPolyBeams(model, Log);
                previousPlane = model.GetWorkPlaneHandler().GetCurrentTransformationPlane();
                if (!model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane()))
                {
                    Log("ERROR cannot activate global transformation plane.");
                    return 11;
                }

                var cases = new[]
                {
                    new ProbeCase("arc-point", new[]
                    {
                        P(0, -5000, 0), P(1200, -5000, 1400), P(2600, -5000, 0),
                    }, arcPointIndices: new[] { 1 }),
                    new ProbeCase("arc-point-existing-shape", new[]
                    {
                        P(0, -7000, 0), P(5.432, -7182.043, -349.952), P(7.174, -6868.051, -513.262),
                    }, arcPointIndices: new[] { 1 }),
                    new ProbeCase("rounded-u", new[]
                    {
                        P(0, -2500, 0), P(0, -2500, 1600), P(2400, -2500, 1600), P(2400, -2500, 0),
                    }, roundingIndices: new[] { 1, 2 }),
                    new ProbeCase("u-positive", new[]
                    {
                        P(0, 0, 0), P(0, 0, 1600), P(2400, 0, 1600), P(2400, 0, 0),
                    }),
                    new ProbeCase("u-reversed", new[]
                    {
                        P(2400, 5000, 0), P(2400, 5000, 1600), P(0, 5000, 1600), P(0, 5000, 0),
                    }),
                    new ProbeCase("spatial", new[]
                    {
                        P(0, 10000, 0), P(0, 10000, 1600), P(2200, 10600, 1800), P(2600, 11600, 700),
                    }),
                    ProbeCase.InStationFrame(
                        "station-frame-u",
                        origin: P(0, 15000, 0),
                        widthAxis: Unit(new Vector(1, 1, 0)),
                        thicknessAxis: Unit(new Vector(-1, 1, 0)),
                        localPoints: new[]
                        {
                            new Point(0, 0, 0),
                            new Point(0, 0, 1600),
                            new Point(2400, 0, 1600),
                            new Point(2400, 0, 0),
                        }),
                };

                var readableProbeCount = 0;

                foreach (var probeCase in cases)
                {
                    var probePlane = probeCase.CoordinateSystem is null
                        ? new TransformationPlane()
                        : new TransformationPlane(probeCase.CoordinateSystem);
                    if (!model.GetWorkPlaneHandler().SetCurrentTransformationPlane(probePlane))
                    {
                        Log($"ERROR case={probeCase.Id} cannot activate probe transformation plane.");
                        return 12;
                    }
                    var polyBeam = CreatePolyBeam(probeCase);
                    if (!polyBeam.Insert())
                    {
                        Log($"ERROR case={probeCase.Id} PolyBeam.Insert returned false.");
                        return 20;
                    }
                    created.Add(polyBeam);
                    if (!polyBeam.Modify())
                    {
                        Log($"ERROR case={probeCase.Id} PolyBeam.Modify after insert returned false.");
                        return 23;
                    }
                    if (!model.CommitChanges())
                    {
                        Log($"ERROR case={probeCase.Id} CommitChanges after insert returned false.");
                        return 21;
                    }
                    if (!model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane()))
                    {
                        Log($"ERROR case={probeCase.Id} cannot restore global transformation plane for readback.");
                        return 13;
                    }

                    var guid = model.GetGUIDByIdentifier(polyBeam.Identifier);
                    var roundtrip = model.SelectModelObject(model.GetIdentifierByGUID(guid)) as PolyBeam;
                    if (roundtrip is null || !roundtrip.Select())
                    {
                        Log($"ERROR case={probeCase.Id} cannot select inserted PolyBeam for readback.");
                        return 22;
                    }

                    var contour = ReadContour(roundtrip);
                    var centerLine = roundtrip.GetCenterLinePolycurve();
                    var solid = roundtrip.GetSolid();
                    Thread.Sleep(250);
                    var systems = roundtrip.GetPolybeamCoordinateSystems();
                    if (systems.Count > 0) readableProbeCount++;
                    Log($"CASE id={probeCase.Id} contourCount={contour.Count} systemCount={systems.Count}");
                    Log($"  CENTERLINE type={centerLine?.GetType().AssemblyQualifiedName ?? "null"} value={centerLine?.ToString() ?? "null"}");
                    Log($"  SOLID min={F(solid.MinimumPoint)} max={F(solid.MaximumPoint)}");
                    if (probeCase.CoordinateSystem is not null)
                    {
                        LogProjection(solid, probeCase.CoordinateSystem, probeCase.ProfileWidthMm, probeCase.ProfileThicknessMm, Log);
                    }
                    for (var index = 0; index < contour.Count; index++)
                    {
                        var contourPoint = roundtrip.Contour.ContourPoints[index] as ContourPoint;
                        Log($"  NODE index={index} point={F(contour[index])} chamfer={contourPoint?.Chamfer.Type.ToString() ?? "null"} x={contourPoint?.Chamfer.X.ToString("0.###", CultureInfo.InvariantCulture) ?? "null"} y={contourPoint?.Chamfer.Y.ToString("0.###", CultureInfo.InvariantCulture) ?? "null"}");
                    }
                    for (var index = 0; index < systems.Count; index++)
                    {
                        var item = systems[index];
                        if (item is CoordinateSystem system)
                        {
                            Log($"  FRAME index={index} origin={F(system.Origin)} axisX={F(system.AxisX)} axisY={F(system.AxisY)} axisZ={F(Cross(system.AxisX, system.AxisY))}");
                            continue;
                        }

                        var type = item?.GetType();
                        var members = type is null
                            ? "none"
                            : string.Join(",", type.GetMembers().Select(static member => member.Name).Distinct().OrderBy(static name => name));
                        Log($"  FRAME_UNREADABLE index={index} type={type?.AssemblyQualifiedName ?? "null"} value={item ?? "null"} members={members}");
                    }
                }

                if (readableProbeCount != cases.Length)
                {
                    Log($"RESULT capability=developedPlatePolyBeamStationFrameV1 supported=false readableCases={readableProbeCount} totalCases={cases.Length}");
                    Log("BLOCKED Tekla did not expose native PolyBeam coordinate systems for every newly inserted probe. The production capability must remain disabled.");
                    return 30;
                }

                Log($"RESULT capability=developedPlatePolyBeamStationFrameV1 supported=unverified-readable-only readableCases={readableProbeCount} totalCases={cases.Length}");
                Log("BLOCKED Coordinate systems are readable, but signed station-to-frame correspondence still requires deterministic verification before enabling the production capability.");
                return 31;
            }
            catch (Exception exception)
            {
                Log($"FATAL {exception.GetType().Name}: {exception.Message}");
                Log(exception.ToString());
                return 99;
            }
            finally
            {
                if (model is not null)
                {
                    foreach (var polyBeam in created.AsEnumerable().Reverse())
                    {
                        try
                        {
                            if (!polyBeam.Delete()) Log($"CLEANUP_WARN id={polyBeam.Identifier.ID} delete returned false.");
                        }
                        catch (Exception exception)
                        {
                            Log($"CLEANUP_WARN id={polyBeam.Identifier.ID} {exception.Message}");
                        }
                    }
                    if (created.Count > 0)
                    {
                        try { model.CommitChanges(); }
                        catch (Exception exception) { Log($"CLEANUP_WARN commit failed: {exception.Message}"); }
                    }
                    if (previousPlane is not null)
                    {
                        try { model.GetWorkPlaneHandler().SetCurrentTransformationPlane(previousPlane); }
                        catch (Exception exception) { Log($"CLEANUP_WARN work plane restore failed: {exception.Message}"); }
                    }
                }
            }
        }

        private static PolyBeam CreatePolyBeam(ProbeCase probeCase)
        {
            var contour = new Contour();
            for (var index = 0; index < probeCase.Points.Count; index++)
            {
                var chamfer = new Chamfer { Type = Chamfer.ChamferTypeEnum.CHAMFER_NONE };
                if (probeCase.ArcPointIndices.Contains(index))
                {
                    chamfer.Type = Chamfer.ChamferTypeEnum.CHAMFER_ARC_POINT;
                }
                else if (probeCase.RoundingIndices.Contains(index))
                {
                    chamfer.Type = Chamfer.ChamferTypeEnum.CHAMFER_ROUNDING;
                    chamfer.X = 250.0;
                    chamfer.Y = 250.0;
                }
                contour.AddContourPoint(new ContourPoint(probeCase.Points[index], chamfer));
            }

            var polyBeam = new PolyBeam(PolyBeam.PolyBeamTypeEnum.BEAM)
            {
                Contour = contour,
                Name = "STRUCTURA_STATION_FRAME_SMOKE",
                Class = "99",
            };
            polyBeam.Profile.ProfileString = "PL20*200";
            polyBeam.Material.MaterialString = "S355";
            polyBeam.Position.Plane = Position.PlaneEnum.MIDDLE;
            polyBeam.Position.Depth = Position.DepthEnum.MIDDLE;
            polyBeam.Position.Rotation = Position.RotationEnum.FRONT;
            return polyBeam;
        }

        private static void LogExistingPolyBeams(Model model, Action<string> log)
        {
            var enumerator = model.GetModelObjectSelector().GetAllObjectsWithType(ModelObject.ModelObjectEnum.POLYBEAM);
            var count = 0;
            var readable = 0;
            while (enumerator.MoveNext() && count < 20)
            {
                if (enumerator.Current is not PolyBeam polyBeam) continue;
                count++;
                try
                {
                    if (!polyBeam.Select())
                    {
                        log($"EXISTING index={count - 1} id={polyBeam.Identifier.ID} select=false");
                        continue;
                    }
                    var systems = polyBeam.GetPolybeamCoordinateSystems();
                    if (systems.Count > 0) readable++;
                    log($"EXISTING index={count - 1} id={polyBeam.Identifier.ID} type={polyBeam.Type} profile={polyBeam.Profile.ProfileString} contourCount={polyBeam.Contour.ContourPoints.Count} systemCount={systems.Count}");
                    if (systems.Count > 0 && readable <= 2)
                    {
                        for (var pointIndex = 0; pointIndex < polyBeam.Contour.ContourPoints.Count; pointIndex++)
                        {
                            if (polyBeam.Contour.ContourPoints[pointIndex] is not ContourPoint point) continue;
                            log($"  EXISTING_NODE index={pointIndex} point={F(point)} chamfer={point.Chamfer.Type} x={point.Chamfer.X:0.###} y={point.Chamfer.Y:0.###} dz1={point.Chamfer.DZ1:0.###} dz2={point.Chamfer.DZ2:0.###}");
                        }
                    }
                }
                catch (Exception exception)
                {
                    log($"EXISTING index={count - 1} id={polyBeam.Identifier.ID} error={exception.Message}");
                }
            }
            log($"EXISTING_SUMMARY inspected={count} withSystems={readable}");
        }

        private static IReadOnlyList<Point> ReadContour(PolyBeam polyBeam)
        {
            var result = new List<Point>();
            foreach (var item in polyBeam.Contour.ContourPoints)
            {
                if (item is Point point) result.Add(point);
            }
            return result;
        }

        private static void LogProjection(
            Tekla.Structures.Model.Solid solid,
            CoordinateSystem system,
            double expectedWidthMm,
            double expectedThicknessMm,
            Action<string> log)
        {
            var points = ReadSolidVertices(solid);
            var widthAxis = Unit(system.AxisY);
            var thicknessAxis = Unit(new Vector(-system.AxisX.X, -system.AxisX.Y, -system.AxisX.Z));
            var width = Extent(points, system.Origin, widthAxis);
            var thickness = Extent(points, system.Origin, thicknessAxis);
            log($"  PROJECTION width={width:0.###} expected={expectedWidthMm:0.###} thickness={thickness:0.###} expected={expectedThicknessMm:0.###} vertices={points.Count}");
        }

        private static IReadOnlyList<Point> ReadSolidVertices(Tekla.Structures.Model.Solid solid)
        {
            var result = new List<Point>();
            var faces = solid.GetFaceEnumerator();
            while (faces.MoveNext())
            {
                if (faces.Current is not Face face) continue;
                var loops = face.GetLoopEnumerator();
                while (loops.MoveNext())
                {
                    if (loops.Current is not Loop loop) continue;
                    var vertices = loop.GetVertexEnumerator();
                    while (vertices.MoveNext())
                    {
                        if (vertices.Current is Point point) result.Add(point);
                    }
                }
            }
            return result;
        }

        private static double Extent(IReadOnlyList<Point> points, Point origin, Vector axis)
        {
            var projections = points.Select(point =>
                ((point.X - origin.X) * axis.X) +
                ((point.Y - origin.Y) * axis.Y) +
                ((point.Z - origin.Z) * axis.Z)).ToArray();
            return projections.Max() - projections.Min();
        }

        private static Point P(double x, double y, double z) => new(CoordinateOffset + x, y, z);

        private static Vector Cross(Vector left, Vector right) => new(
            left.Y * right.Z - left.Z * right.Y,
            left.Z * right.X - left.X * right.Z,
            left.X * right.Y - left.Y * right.X);

        private static Vector Unit(Vector value)
        {
            var length = Math.Sqrt((value.X * value.X) + (value.Y * value.Y) + (value.Z * value.Z));
            return new Vector(value.X / length, value.Y / length, value.Z / length);
        }

        private static string F(Point point) => string.Format(
            CultureInfo.InvariantCulture,
            "({0:0.###},{1:0.###},{2:0.###})",
            point.X,
            point.Y,
            point.Z);

        private sealed class ProbeCase
        {
            public ProbeCase(
                string id,
                IReadOnlyList<Point> points,
                IReadOnlyList<int>? arcPointIndices = null,
                IReadOnlyList<int>? roundingIndices = null,
                CoordinateSystem? coordinateSystem = null,
                double profileWidthMm = 200,
                double profileThicknessMm = 20)
            {
                Id = id;
                Points = points;
                ArcPointIndices = arcPointIndices ?? Array.Empty<int>();
                RoundingIndices = roundingIndices ?? Array.Empty<int>();
                CoordinateSystem = coordinateSystem;
                ProfileWidthMm = profileWidthMm;
                ProfileThicknessMm = profileThicknessMm;
            }

            public string Id { get; }
            public IReadOnlyList<Point> Points { get; }
            public IReadOnlyList<int> ArcPointIndices { get; }
            public IReadOnlyList<int> RoundingIndices { get; }
            public CoordinateSystem? CoordinateSystem { get; }
            public double ProfileWidthMm { get; }
            public double ProfileThicknessMm { get; }

            public static ProbeCase InStationFrame(
                string id,
                Point origin,
                Vector widthAxis,
                Vector thicknessAxis,
                IReadOnlyList<Point> localPoints)
            {
                var coordinateSystem = new CoordinateSystem(
                    origin,
                    new Vector(-thicknessAxis.X, -thicknessAxis.Y, -thicknessAxis.Z),
                    widthAxis);
                return new ProbeCase(id, localPoints, coordinateSystem: coordinateSystem);
            }
        }
    }
}
