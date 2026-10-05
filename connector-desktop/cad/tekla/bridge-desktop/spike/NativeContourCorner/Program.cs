using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;
using Platform.Bridge.Desktop.Tekla.ConstructiveRuntime;
using Platform.Contracts.TeklaPlan;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace NativeContourCorner
{
    internal static class Program
    {
        private const double CoordinateOffset = 620000.0;
        private const double Tolerance = 0.25;

        private static int Main()
        {
            var reportDirectory = Path.Combine(Path.GetTempPath(), "NativeContourCorner");
            Directory.CreateDirectory(reportDirectory);
            var reportPath = Path.Combine(reportDirectory, "report.txt");
            using var report = new StreamWriter(reportPath, append: false) { AutoFlush = true };
            Model? model = null;
            TransformationPlane? previousPlane = null;
            ContourPlate? plate = null;

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

                var info = model.GetInfo();
                Log($"MODEL name={info.ModelName} path={info.ModelPath}");
                previousPlane = model.GetWorkPlaneHandler().GetCurrentTransformationPlane();
                Require(model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane()), "activate global work plane");

                plate = CreatePlate();
                Require(plate.Insert(), "ContourPlate.Insert");
                Require(model.CommitChanges(), "commit plate");
                var guid = model.GetGUIDByIdentifier(plate.Identifier);
                Require(!string.IsNullOrWhiteSpace(guid), "persistent ContourPlate GUID");

                ApplyCorner(plate, 1, Chamfer.ChamferTypeEnum.CHAMFER_LINE, 160, 90);
                Require(plate.Modify(), "apply CHAMFER_LINE");
                Require(model.CommitChanges(), "commit CHAMFER_LINE");
                var lineReadback = ReadPlate(model, guid);
                VerifyCorner(lineReadback, 1, Chamfer.ChamferTypeEnum.CHAMFER_LINE, 160, 90);
                VerifyGuid(model, lineReadback, guid);
                Log("STEP line=true sizeX=160 sizeY=90 guidPreserved=true");

                ApplyCorner(lineReadback, 1, Chamfer.ChamferTypeEnum.CHAMFER_ROUNDING, 125, 125);
                Require(lineReadback.Modify(), "apply CHAMFER_ROUNDING");
                Require(model.CommitChanges(), "commit CHAMFER_ROUNDING");
                var roundReadback = ReadPlate(model, guid);
                VerifyCorner(roundReadback, 1, Chamfer.ChamferTypeEnum.CHAMFER_ROUNDING, 125, 125);
                VerifyGuid(model, roundReadback, guid);
                Log("STEP round=true radius=125 guidPreserved=true");

                ApplyCorner(roundReadback, 1, Chamfer.ChamferTypeEnum.CHAMFER_NONE, 0, 0);
                Require(roundReadback.Modify(), "restore CHAMFER_NONE");
                Require(model.CommitChanges(), "commit CHAMFER_NONE");
                var restored = ReadPlate(model, guid);
                VerifyCorner(restored, 1, Chamfer.ChamferTypeEnum.CHAMFER_NONE, 0, 0);
                VerifyGuid(model, restored, guid);
                Log("STEP rollback=true guidPreserved=true");

                RunExecutorProbe(model, Log);

                Log("RESULT capability=contourChamfer supported=true line=true round=true guidPreserved=true rollback=true executorUpsert=true executorRollback=true");
                return 0;
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
                    try { model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane()); }
                    catch { }
                    if (plate is not null)
                    {
                        try
                        {
                            var guid = model.GetGUIDByIdentifier(plate.Identifier);
                            var selected = string.IsNullOrWhiteSpace(guid) ? plate : model.SelectModelObject(model.GetIdentifierByGUID(guid));
                            if (selected is not null && !selected.Delete()) Log("CLEANUP_WARN ContourPlate.Delete returned false.");
                            model.CommitChanges();
                        }
                        catch (Exception exception) { Log($"CLEANUP_WARN {exception.Message}"); }
                    }
                    if (previousPlane is not null)
                    {
                        try { model.GetWorkPlaneHandler().SetCurrentTransformationPlane(previousPlane); }
                        catch (Exception exception) { Log($"CLEANUP_WARN work plane restore failed: {exception.Message}"); }
                    }
                }
            }
        }

        private static void RunExecutorProbe(Model model, Action<string> log)
        {
            const string baseCommandId = "element:contour-corner-target:create-contour-plate";
            var initialPlan = ExecutorPlan("chamfer", 150, 85);
            var baseCommand = initialPlan.Commands.Single(command => command.CommandId == baseCommandId);
            var cornerCommand = initialPlan.Commands.Single(command => command.Kind == "apply-contour-corner");
            var ownershipId = TeklaPlanOwnershipIdentity.Create(initialPlan, baseCommand);
            DeleteOwnedProbePlate(model, ownershipId, log);

            var plateExecutor = new TeklaCreateContourPlateExecutor();
            var cornerExecutor = new TeklaApplyContourCornerExecutor();
            var createMutation = plateExecutor.Prepare(model, initialPlan, baseCommand, "contour-corner-spike-create");
            var cornerMutation = cornerExecutor.Prepare(model, initialPlan, cornerCommand, "contour-corner-spike-create");
            var createdReadback = TeklaPlanTransaction.Execute(
                new ITeklaPreparedMutation<TeklaNativeCommandReadback>[] { createMutation, cornerMutation },
                () => Require(model.CommitChanges(), "executor create transaction commit"));
            var baseReadback = createdReadback.Single(item => item.CommandId == baseCommandId);
            var cornerReadback = createdReadback.Single(item => item.CommandId == cornerCommand.CommandId);
            Require(string.Equals(baseReadback.TeklaGuid, cornerReadback.TargetTeklaGuid, StringComparison.OrdinalIgnoreCase), "executor target GUID matches base plate");
            VerifyExecutorCorner(model, baseReadback.TeklaGuid, Chamfer.ChamferTypeEnum.CHAMFER_LINE, 150, 85);

            var modifiedPlan = ExecutorPlan("round", 120, 120);
            var modifiedBaseCommand = modifiedPlan.Commands.Single(command => command.CommandId == baseCommandId);
            var modifiedCornerCommand = modifiedPlan.Commands.Single(command => command.Kind == "apply-contour-corner");
            var modifyBase = plateExecutor.Prepare(model, modifiedPlan, modifiedBaseCommand, "contour-corner-spike-modify");
            var modifyCorner = cornerExecutor.Prepare(model, modifiedPlan, modifiedCornerCommand, "contour-corner-spike-modify");
            var modifiedReadback = TeklaPlanTransaction.Execute(
                new ITeklaPreparedMutation<TeklaNativeCommandReadback>[] { modifyBase, modifyCorner },
                () => Require(model.CommitChanges(), "executor modify transaction commit"));
            var modifiedBaseReadback = modifiedReadback.Single(item => item.CommandId == baseCommandId);
            Require(string.Equals(baseReadback.TeklaGuid, modifiedBaseReadback.TeklaGuid, StringComparison.OrdinalIgnoreCase), "executor upsert preserves base GUID");
            VerifyExecutorCorner(model, baseReadback.TeklaGuid, Chamfer.ChamferTypeEnum.CHAMFER_ROUNDING, 120, 120);

            var rollbackPlan = ExecutorPlan("chamfer", 75, 45);
            var rollbackCommand = rollbackPlan.Commands.Single(command => command.Kind == "apply-contour-corner");
            var rollbackMutation = cornerExecutor.Prepare(model, rollbackPlan, rollbackCommand, "contour-corner-spike-rollback");
            rollbackMutation.Apply();
            Require(model.CommitChanges(), "executor rollback probe apply commit");
            rollbackMutation.Readback();
            VerifyExecutorCorner(model, baseReadback.TeklaGuid, Chamfer.ChamferTypeEnum.CHAMFER_LINE, 75, 45);
            rollbackMutation.Restore();
            Require(model.CommitChanges(), "executor rollback probe restore commit");
            VerifyExecutorCorner(model, baseReadback.TeklaGuid, Chamfer.ChamferTypeEnum.CHAMFER_ROUNDING, 120, 120);

            var selected = model.SelectModelObject(model.GetIdentifierByGUID(baseReadback.TeklaGuid));
            Require(selected is not null && selected.Delete(), "executor cleanup target plate");
            Require(model.CommitChanges(), "executor cleanup commit");
            log($"EXECUTOR targetGuid={baseReadback.TeklaGuid} exactVertex=outer-v2 upsert=true rollback=true");
        }

        private static TeklaPlanDocument ExecutorPlan(string cornerType, double sizeXmm, double sizeYmm)
        {
            const string baseCommandId = "element:contour-corner-target:create-contour-plate";
            var baseCommand = new TeklaPlanCommand
            {
                CommandId = baseCommandId,
                Kind = "create-contour-plate",
                Phase = "base-parts",
                Source = new TeklaPlanSourceRef
                {
                    Kind = "element",
                    Id = "contour-corner-target",
                    StableKey = "contour-corner-spike/target",
                    Role = "part",
                    SourceLayer = "generated",
                },
                Ownership = new TeklaOwnershipStamp { Namespace = TeklaPlanContract.OwnershipNamespace },
                Payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["plane"] = JsonSerializer.SerializeToElement(new
                    {
                        origin = new[] { CoordinateOffset + 3000, 0d, 0d },
                        axisX = new[] { 1d, 0d, 0d },
                        axisY = new[] { 0d, 1d, 0d },
                        axisZ = new[] { 0d, 0d, 1d },
                    }),
                    ["contour"] = JsonSerializer.SerializeToElement(new
                    {
                        id = "outer",
                        vertices = new[]
                        {
                            new { id = "outer-v1", point = new[] { 0d, 0d } },
                            new { id = "outer-v2", point = new[] { 1400d, 0d } },
                            new { id = "outer-v3", point = new[] { 1400d, 900d } },
                            new { id = "outer-v4", point = new[] { 0d, 900d } },
                        },
                        edges = new[]
                        {
                            new { id = "outer-e1", kind = "line", startVertexId = "outer-v1", endVertexId = "outer-v2" },
                            new { id = "outer-e2", kind = "line", startVertexId = "outer-v2", endVertexId = "outer-v3" },
                            new { id = "outer-e3", kind = "line", startVertexId = "outer-v3", endVertexId = "outer-v4" },
                            new { id = "outer-e4", kind = "line", startVertexId = "outer-v4", endVertexId = "outer-v1" },
                        },
                    }),
                    ["thicknessMm"] = JsonSerializer.SerializeToElement(20d),
                    ["extrusionSide"] = JsonSerializer.SerializeToElement("symmetric"),
                    ["material"] = JsonSerializer.SerializeToElement(new { id = "S355", grade = "S355" }),
                },
            };
            var cornerCommand = new TeklaPlanCommand
            {
                CommandId = "feature:outer-v2:apply-contour-corner",
                Kind = "apply-contour-corner",
                Phase = "edge-treatments",
                Source = new TeklaPlanSourceRef
                {
                    Kind = "feature",
                    Id = "outer-v2-corner",
                    StableKey = "contour-corner-spike/outer-v2",
                    Role = "corner-treatment",
                    SourceLayer = "generated",
                },
                DependsOn = new[] { baseCommandId },
                Ownership = new TeklaOwnershipStamp { Namespace = TeklaPlanContract.OwnershipNamespace },
                Payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["target"] = JsonSerializer.SerializeToElement(new { elementId = "contour-corner-target", role = "part" }),
                    ["targetTopology"] = JsonSerializer.SerializeToElement(new { kind = "contour-vertex", contourId = "outer", vertexId = "outer-v2" }),
                    ["cornerType"] = JsonSerializer.SerializeToElement(cornerType),
                    ["sizeXmm"] = JsonSerializer.SerializeToElement(sizeXmm),
                    ["sizeYmm"] = JsonSerializer.SerializeToElement(sizeYmm),
                },
            };
            return new TeklaPlanDocument
            {
                SchemaVersion = TeklaPlanContract.SchemaVersion,
                Source = new TeklaPlanSource
                {
                    Address = new ConstructiveAddress
                    {
                        ProjectId = "contour-corner-spike",
                        ModuleId = "bridge",
                        ModuleVariantId = "native-contour-corner",
                        RevisionId = "live",
                    },
                    GenerationId = "live",
                    SourceHash = "contour-corner-spike-source",
                    ConstructiveContentHash = "contour-corner-spike-constructive",
                },
                Commands = new[] { baseCommand, cornerCommand },
            };
        }

        private static void DeleteOwnedProbePlate(Model model, string ownershipId, Action<string> log)
        {
            var enumerator = model.GetModelObjectSelector()
                .GetAllObjectsWithType(ModelObject.ModelObjectEnum.CONTOURPLATE);
            var deleted = 0;
            while (enumerator.MoveNext())
            {
                if (enumerator.Current is not ContourPlate candidate) continue;
                var externalObjectId = string.Empty;
                if (!candidate.GetUserProperty(StructuraServiceUdas.ExternalObjectId, ref externalObjectId) ||
                    !string.Equals(externalObjectId, ownershipId, StringComparison.Ordinal)) continue;
                Require(candidate.Delete(), "executor stale ContourPlate.Delete");
                deleted++;
            }
            if (deleted == 0) return;
            Require(model.CommitChanges(), "executor stale cleanup commit");
            log($"EXECUTOR cleanup staleOwnedObjects={deleted}");
        }

        private static void VerifyExecutorCorner(
            Model model,
            string guid,
            Chamfer.ChamferTypeEnum type,
            double sizeX,
            double sizeY)
        {
            var plate = ReadPlate(model, guid);
            VerifyCorner(plate, 1, type, sizeX, sizeY);
        }

        private static ContourPlate CreatePlate()
        {
            var plate = new ContourPlate
            {
                Name = "STRUCTURA CONTOUR CORNER SPIKE",
                Class = "99",
            };
            plate.Profile.ProfileString = "PL20";
            plate.Material.MaterialString = "S355";
            plate.Position.Depth = Position.DepthEnum.MIDDLE;
            plate.Contour = BuildContour(new[]
            {
                Snapshot.Create(P(0, 0, 0)),
                Snapshot.Create(P(1400, 0, 0)),
                Snapshot.Create(P(1400, 900, 0)),
                Snapshot.Create(P(0, 900, 0)),
            });
            return plate;
        }

        private static void ApplyCorner(
            ContourPlate plate,
            int targetIndex,
            Chamfer.ChamferTypeEnum type,
            double sizeX,
            double sizeY)
        {
            var points = ReadPoints(plate).Select(Snapshot.Capture).ToArray();
            Require(points.Length == 4, $"expected 4 contour points, actual={points.Length}");
            points[targetIndex] = points[targetIndex].WithChamfer(type, sizeX, sizeY);
            plate.Contour = BuildContour(points);
        }

        private static Contour BuildContour(IEnumerable<Snapshot> snapshots)
        {
            var contour = new Contour();
            foreach (var snapshot in snapshots) contour.AddContourPoint(snapshot.CreatePoint());
            return contour;
        }

        private static ContourPlate ReadPlate(Model model, string guid)
        {
            var plate = model.SelectModelObject(model.GetIdentifierByGUID(guid)) as ContourPlate;
            if (plate is null || !plate.Select()) throw new InvalidOperationException("Cannot select ContourPlate for readback.");
            return plate;
        }

        private static void VerifyCorner(
            ContourPlate plate,
            int targetIndex,
            Chamfer.ChamferTypeEnum type,
            double sizeX,
            double sizeY)
        {
            var points = ReadPoints(plate);
            Require(points.Count == 4, $"readback expected 4 contour points, actual={points.Count}");
            for (var index = 0; index < points.Count; index++)
            {
                var chamfer = points[index].Chamfer ?? new Chamfer { Type = Chamfer.ChamferTypeEnum.CHAMFER_NONE };
                var expectedType = index == targetIndex ? type : Chamfer.ChamferTypeEnum.CHAMFER_NONE;
                Require(chamfer.Type == expectedType, $"point={index} type expected={expectedType} actual={chamfer.Type}");
                if (index != targetIndex) continue;
                Require(Math.Abs(chamfer.X - sizeX) <= Tolerance, $"point={index} X expected={sizeX} actual={chamfer.X}");
                Require(Math.Abs(chamfer.Y - sizeY) <= Tolerance, $"point={index} Y expected={sizeY} actual={chamfer.Y}");
            }
        }

        private static void VerifyGuid(Model model, ContourPlate plate, string expected)
            => Require(string.Equals(model.GetGUIDByIdentifier(plate.Identifier), expected, StringComparison.OrdinalIgnoreCase), "ContourPlate GUID changed after Modify");

        private static IReadOnlyList<ContourPoint> ReadPoints(ContourPlate plate)
            => plate.Contour?.ContourPoints?.OfType<ContourPoint>().ToArray() ?? Array.Empty<ContourPoint>();

        private static Point P(double x, double y, double z)
            => new(CoordinateOffset + x, y, z);

        private static Point Clone(Point point) => new(point.X, point.Y, point.Z);

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class Snapshot
        {
            private Snapshot(Point point, Chamfer.ChamferTypeEnum type, double x, double y, double dz1, double dz2)
            {
                Point = Clone(point);
                Type = type;
                X = x;
                Y = y;
                Dz1 = dz1;
                Dz2 = dz2;
            }

            private Point Point { get; }
            private Chamfer.ChamferTypeEnum Type { get; }
            private double X { get; }
            private double Y { get; }
            private double Dz1 { get; }
            private double Dz2 { get; }

            public static Snapshot Create(Point point)
                => new(point, Chamfer.ChamferTypeEnum.CHAMFER_NONE, 0, 0, 0, 0);

            public static Snapshot Capture(ContourPoint point)
                => new(
                    point,
                    point.Chamfer?.Type ?? Chamfer.ChamferTypeEnum.CHAMFER_NONE,
                    point.Chamfer?.X ?? 0,
                    point.Chamfer?.Y ?? 0,
                    point.Chamfer?.DZ1 ?? 0,
                    point.Chamfer?.DZ2 ?? 0);

            public Snapshot WithChamfer(Chamfer.ChamferTypeEnum type, double x, double y)
                => new(Point, type, x, y, Dz1, Dz2);

            public ContourPoint CreatePoint()
                => new(Clone(Point), new Chamfer { Type = Type, X = X, Y = Y, DZ1 = Dz1, DZ2 = Dz2 });
        }
    }
}
