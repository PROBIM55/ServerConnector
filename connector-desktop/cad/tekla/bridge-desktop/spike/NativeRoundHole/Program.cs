using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;
using Platform.Bridge.Desktop.Tekla.ConstructiveRuntime;
using Platform.Contracts.TeklaPlan;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace NativeRoundHole
{
    internal static class Program
    {
        private const double CoordinateOffset = 450000.0;
        private const double Tolerance = 0.25;

        private static int Main()
        {
            var reportDirectory = Path.Combine(Path.GetTempPath(), "NativeRoundHole");
            Directory.CreateDirectory(reportDirectory);
            var reportPath = Path.Combine(reportDirectory, "report.txt");
            using var report = new StreamWriter(reportPath, append: false) { AutoFlush = true };
            var created = new List<ModelObject>();
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

                var info = model.GetInfo();
                Log($"MODEL name={info.ModelName} path={info.ModelPath}");
                previousPlane = model.GetWorkPlaneHandler().GetCurrentTransformationPlane();
                Require(model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane()), "activate global work plane");

                var cases = new[]
                {
                    ProbeCase.Create("horizontal-through", P(0, 0, 0), V(0, 0, 1), null),
                    ProbeCase.Create("vertical-blind", P(5000, 0, 1000), V(0, 1, 0), 18.0),
                    ProbeCase.Create("spatial-through", P(10000, 0, 1500), V(1, 2, 3), null),
                };

                foreach (var probeCase in cases)
                {
                    Require(model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane()), "restore global before plate insert");
                    var plate = CreatePlate(probeCase);
                    Require(plate.Insert(), $"case={probeCase.Id} ContourPlate.Insert");
                    created.Add(plate);
                    Require(model.CommitChanges(), $"case={probeCase.Id} commit plate");

                    var bolt = CreateRoundHole(probeCase, plate, model);
                    Require(bolt.Insert(), $"case={probeCase.Id} BoltArray.Insert");
                    created.Add(bolt);
                    Require(model.CommitChanges(), $"case={probeCase.Id} commit hole");

                    Require(model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane()), "activate global readback work plane");
                    var guid = model.GetGUIDByIdentifier(bolt.Identifier);
                    var readback = model.SelectModelObject(model.GetIdentifierByGUID(guid)) as BoltArray;
                    if (readback is null || !readback.Select())
                        throw new InvalidOperationException($"case={probeCase.Id} cannot select inserted BoltArray for readback.");

                    VerifyReadback(probeCase, plate, readback, model, Log);
                }

                RunExecutorProbe(model, created, Log);

                Log($"RESULT capability=roundHole supported=true cases={cases.Length} executorUpsert=true executorRollback=true");
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
                    foreach (var item in created.AsEnumerable().Reverse())
                    {
                        try
                        {
                            if (!item.Delete()) Log($"CLEANUP_WARN type={item.GetType().Name} id={item.Identifier.ID} delete returned false.");
                        }
                        catch (Exception exception)
                        {
                            Log($"CLEANUP_WARN type={item.GetType().Name} id={item.Identifier.ID} {exception.Message}");
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

        private static void RunExecutorProbe(Model model, ICollection<ModelObject> created, Action<string> log)
        {
            log("EXECUTOR step=begin");
            Require(model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane()), "executor activate global work plane");
            var center = P(15000, 0, 2000);
            var targetCase = ProbeCase.Create("executor", center, V(1, -2, 3), null);
            log("EXECUTOR step=build-plan");
            var plan = ExecutorPlan(center, targetCase.AxisZ, 24);
            var baseCommand = plan.Commands.Single(command => command.Kind == "create-contour-plate");
            var holeCommand = plan.Commands.Single(command => command.Kind == "create-hole");
            var targetOwnershipId = TeklaPlanOwnershipIdentity.Create(plan, baseCommand);
            var holeOwnershipId = TeklaPlanOwnershipIdentity.Create(plan, holeCommand);
            DeleteOwnedProbeObjects(model, targetOwnershipId, holeOwnershipId, log);

            log("EXECUTOR step=create-target");
            var target = CreatePlate(targetCase);
            Require(target.Insert(), "executor ContourPlate.Insert");
            created.Add(target);
            Require(model.CommitChanges(), "executor commit target");
            log("EXECUTOR step=stamp-target");
            Require(target.SetUserProperty(StructuraServiceUdas.ExternalObjectId, targetOwnershipId), "executor target ownership UDA");
            Require(target.Modify(), "executor target ContourPlate.Modify");
            Require(model.CommitChanges(), "executor commit target ownership");

            log("EXECUTOR step=prepare-create");
            var executor = new TeklaCreateHoleExecutor();
            var createMutation = executor.Prepare(model, plan, holeCommand, "round-hole-spike-create");
            log("EXECUTOR step=apply-create");
            createMutation.Apply();
            Require(model.CommitChanges(), "executor commit create");
            log("EXECUTOR step=readback-create");
            var createdReadback = createMutation.Readback();
            if (createdReadback.Action != "created" || Math.Abs(createdReadback.HoleDiameterMm - 24) > Tolerance)
                throw new InvalidOperationException("executor create readback mismatch.");

            log("EXECUTOR step=prepare-modify");
            var modifiedPlan = ExecutorPlan(center, targetCase.AxisZ, 30);
            var modifiedCommand = modifiedPlan.Commands.Single(command => command.Kind == "create-hole");
            var modifyMutation = executor.Prepare(model, modifiedPlan, modifiedCommand, "round-hole-spike-modify");
            log("EXECUTOR step=apply-modify");
            modifyMutation.Apply();
            Require(model.CommitChanges(), "executor commit modify");
            log("EXECUTOR step=readback-modify");
            var modifiedReadback = modifyMutation.Readback();
            if (modifiedReadback.Action != "modified" ||
                !string.Equals(modifiedReadback.TeklaGuid, createdReadback.TeklaGuid, StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(modifiedReadback.HoleDiameterMm - 30) > Tolerance)
                throw new InvalidOperationException("executor idempotent modify readback mismatch.");

            log("EXECUTOR step=restore-modify");
            modifyMutation.Restore();
            Require(model.CommitChanges(), "executor commit modify rollback");
            var restored = model.SelectModelObject(model.GetIdentifierByGUID(createdReadback.TeklaGuid)) as BoltArray;
            if (restored is null || !restored.Select() || Math.Abs(restored.BoltSize - 24) > Tolerance)
                throw new InvalidOperationException("executor modify rollback did not restore the original hole.");

            log("EXECUTOR step=restore-create");
            createMutation.Restore();
            Require(model.CommitChanges(), "executor commit create rollback");
            if (model.SelectModelObject(model.GetIdentifierByGUID(createdReadback.TeklaGuid)) is BoltArray)
                throw new InvalidOperationException("executor create rollback did not delete the round hole.");

            log($"EXECUTOR createGuid={createdReadback.TeklaGuid} upsertGuid={modifiedReadback.TeklaGuid} restoredDiameter=24 deleted=true");
        }

        private static void DeleteOwnedProbeObjects(
            Model model,
            string targetOwnershipId,
            string holeOwnershipId,
            Action<string> log)
        {
            var selector = model.GetModelObjectSelector();
            var deleted = 0;
            foreach (var objectType in new[]
            {
                ModelObject.ModelObjectEnum.BOLT_ARRAY,
                ModelObject.ModelObjectEnum.CONTOURPLATE,
            })
            {
                var enumerator = selector.GetAllObjectsWithType(objectType);
                while (enumerator.MoveNext())
                {
                    if (enumerator.Current is not ModelObject candidate) continue;
                    var externalObjectId = string.Empty;
                    if (!candidate.GetUserProperty(StructuraServiceUdas.ExternalObjectId, ref externalObjectId)) continue;
                    if (!string.Equals(externalObjectId, targetOwnershipId, StringComparison.Ordinal) &&
                        !string.Equals(externalObjectId, holeOwnershipId, StringComparison.Ordinal)) continue;
                    Require(candidate.Delete(), $"executor stale {candidate.GetType().Name}.Delete");
                    deleted++;
                }
            }

            if (deleted <= 0) return;
            Require(model.CommitChanges(), "executor commit stale probe cleanup");
            log($"EXECUTOR cleanup staleOwnedObjects={deleted}");
        }

        private static TeklaPlanDocument ExecutorPlan(Point center, Vector axis, double diameterMm)
        {
            const string baseCommandId = "element:round-hole-target:create-contour-plate";
            return new TeklaPlanDocument
            {
                SchemaVersion = TeklaPlanContract.SchemaVersion,
                Source = new TeklaPlanSource
                {
                    Address = new ConstructiveAddress
                    {
                        ProjectId = "round-hole-spike",
                        ModuleId = "bridge",
                        ModuleVariantId = "native-round-hole",
                        RevisionId = "live",
                    },
                    GenerationId = "live",
                    SourceHash = "round-hole-spike-source",
                    ConstructiveContentHash = "round-hole-spike-constructive",
                },
                Commands = new[]
                {
                    new TeklaPlanCommand
                    {
                        CommandId = baseCommandId,
                        Kind = "create-contour-plate",
                        Phase = "base-parts",
                        Source = new TeklaPlanSourceRef
                        {
                            Kind = "element",
                            Id = "round-hole-target",
                            StableKey = "round-hole-spike/target",
                            Role = "part",
                            SourceLayer = "generated",
                        },
                        Ownership = new TeklaOwnershipStamp { Namespace = TeklaPlanContract.OwnershipNamespace },
                    },
                    new TeklaPlanCommand
                    {
                        CommandId = "feature:round-hole:create-hole",
                        Kind = "create-hole",
                        Phase = "holes-and-bolts",
                        Source = new TeklaPlanSourceRef
                        {
                            Kind = "feature",
                            Id = "round-hole",
                            StableKey = "round-hole-spike/hole",
                            Role = "hole",
                            SourceLayer = "generated",
                        },
                        DependsOn = new[] { baseCommandId },
                        Ownership = new TeklaOwnershipStamp { Namespace = TeklaPlanContract.OwnershipNamespace },
                        Payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        {
                            ["target"] = JsonSerializer.SerializeToElement(new { elementId = "round-hole-target", role = "part" }),
                            ["center"] = JsonSerializer.SerializeToElement(new[] { center.X, center.Y, center.Z }),
                            ["axis"] = JsonSerializer.SerializeToElement(new[] { axis.X, axis.Y, axis.Z }),
                            ["diameterMm"] = JsonSerializer.SerializeToElement(diameterMm),
                            ["holeType"] = JsonSerializer.SerializeToElement("round"),
                        },
                    },
                },
            };
        }

        private static ContourPlate CreatePlate(ProbeCase probeCase)
        {
            var contour = new Contour();
            foreach (var point in new[]
            {
                Add(probeCase.Center, Add(Scale(probeCase.AxisX, -1000), Scale(probeCase.AxisY, -700))),
                Add(probeCase.Center, Add(Scale(probeCase.AxisX, 1000), Scale(probeCase.AxisY, -700))),
                Add(probeCase.Center, Add(Scale(probeCase.AxisX, 1000), Scale(probeCase.AxisY, 700))),
                Add(probeCase.Center, Add(Scale(probeCase.AxisX, -1000), Scale(probeCase.AxisY, 700))),
            })
            {
                contour.AddContourPoint(new ContourPoint(point, new Chamfer { Type = Chamfer.ChamferTypeEnum.CHAMFER_NONE }));
            }

            var plate = new ContourPlate
            {
                Contour = contour,
                Name = "STRUCTURA_ROUND_HOLE_SMOKE_TARGET",
                Class = "99",
            };
            plate.Profile.ProfileString = "PL40";
            plate.Material.MaterialString = "S355";
            plate.Position.Depth = Position.DepthEnum.MIDDLE;
            return plate;
        }

        private static BoltArray CreateRoundHole(ProbeCase probeCase, Part target, Model model)
        {
            var transformation = new TransformationPlane(new CoordinateSystem(
                probeCase.Center,
                probeCase.AxisX,
                probeCase.AxisY));
            Require(model.GetWorkPlaneHandler().SetCurrentTransformationPlane(transformation), $"case={probeCase.Id} activate hole work plane");

            var holes = new BoltArray
            {
                PartToBeBolted = target,
                PartToBoltTo = target,
                FirstPosition = new Point(0, 0, 0),
                SecondPosition = new Point(100, 0, 0),
                BoltSize = probeCase.DiameterMm,
                BoltStandard = "7798",
                BoltType = BoltGroup.BoltTypeEnum.BOLT_TYPE_SITE,
                ThreadInMaterial = BoltGroup.BoltThreadInMaterialEnum.THREAD_IN_MATERIAL_YES,
                Length = 0,
                CutLength = 200,
                ExtraLength = 0,
                Tolerance = 0,
                HoleType = BoltGroup.BoltHoleTypeEnum.HOLE_TYPE_SLOTTED,
                PlainHoleType = probeCase.DepthMm.HasValue
                    ? BoltGroup.BoltPlainHoleTypeEnum.HOLE_TYPE_BLIND
                    : BoltGroup.BoltPlainHoleTypeEnum.HOLE_TYPE_THROUGH,
                BlindHoleDepth = probeCase.DepthMm ?? 0,
                SlottedHoleX = 0,
                SlottedHoleY = 0,
                SlotOffsetX = 0,
                SlotOffsetY = 0,
                RotateSlots = BoltGroup.BoltRotateSlotsEnum.ROTATE_SLOTS_PARALLEL,
                Washer1 = false,
                Washer2 = false,
                Washer3 = false,
                Nut1 = false,
                Nut2 = false,
                Bolt = false,
                Hole1 = true,
                Hole2 = false,
                Hole3 = false,
                Hole4 = false,
                Hole5 = false,
                ConnectAssemblies = false,
            };
            holes.Position.Plane = Position.PlaneEnum.MIDDLE;
            holes.Position.Depth = Position.DepthEnum.MIDDLE;
            holes.Position.Rotation = Position.RotationEnum.FRONT;
            holes.AddBoltDistX(0);
            holes.AddBoltDistY(0);
            return holes;
        }

        private static void VerifyReadback(
            ProbeCase probeCase,
            Part target,
            BoltArray holes,
            Model model,
            Action<string> log)
        {
            if (holes.PartToBeBolted is not Part first || !SameObject(first, target) ||
                holes.PartToBoltTo is not Part second || !SameObject(second, target))
                throw new InvalidOperationException($"case={probeCase.Id} target part readback mismatch.");
            if (holes.Bolt || Math.Abs(holes.BoltSize - probeCase.DiameterMm) > Tolerance || Math.Abs(holes.Tolerance) > Tolerance)
                throw new InvalidOperationException($"case={probeCase.Id} hole-only diameter readback mismatch.");
            var expectedPlainType = probeCase.DepthMm.HasValue
                ? BoltGroup.BoltPlainHoleTypeEnum.HOLE_TYPE_BLIND
                : BoltGroup.BoltPlainHoleTypeEnum.HOLE_TYPE_THROUGH;
            if (holes.PlainHoleType != expectedPlainType ||
                Math.Abs(holes.BlindHoleDepth - (probeCase.DepthMm ?? 0)) > Tolerance)
                throw new InvalidOperationException($"case={probeCase.Id} through/blind readback mismatch.");
            if (holes.GetBoltDistXCount() != 1 || holes.GetBoltDistYCount() != 1 ||
                Math.Abs(holes.GetBoltDistX(0)) > Tolerance || Math.Abs(holes.GetBoltDistY(0)) > Tolerance)
                throw new InvalidOperationException($"case={probeCase.Id} single-hole pattern readback mismatch.");

            var system = holes.GetCoordinateSystem();
            var actualAxis = Normalize(Cross(system.AxisX, system.AxisY));
            if (Dot(actualAxis, probeCase.AxisZ) < 1 - 1e-6)
                throw new InvalidOperationException($"case={probeCase.Id} hole axis readback mismatch expected={F(probeCase.AxisZ)} actual={F(actualAxis)}.");
            if (Distance(system.Origin, probeCase.Center) > Tolerance)
                throw new InvalidOperationException($"case={probeCase.Id} hole center readback mismatch expected={F(probeCase.Center)} actual={F(system.Origin)}.");

            var positions = holes.BoltPositions.Cast<Point>().ToArray();
            if (positions.Length != 1 || Distance(positions[0], probeCase.Center) > Tolerance)
                throw new InvalidOperationException($"case={probeCase.Id} BoltPositions center readback mismatch.");

            var guid = model.GetGUIDByIdentifier(holes.Identifier) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(guid) || holes.Identifier.ID <= 0)
                throw new InvalidOperationException($"case={probeCase.Id} persistent identity is missing.");
            log($"CASE id={probeCase.Id} guid={guid} center={F(system.Origin)} axis={F(actualAxis)} diameter={holes.BoltSize:0.###} plainType={holes.PlainHoleType} depth={holes.BlindHoleDepth:0.###}");
        }

        private static bool SameObject(ModelObject left, ModelObject right)
            => left.Identifier.ID == right.Identifier.ID;

        private static void Require(bool result, string role)
        {
            if (!result) throw new InvalidOperationException($"{role} returned false.");
        }

        private static Point P(double x, double y, double z) => new(CoordinateOffset + x, y, z);
        private static Vector V(double x, double y, double z) => new(x, y, z);
        private static Point Add(Point point, Vector vector) => new(point.X + vector.X, point.Y + vector.Y, point.Z + vector.Z);
        private static Vector Add(Vector left, Vector right) => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
        private static Vector Scale(Vector value, double factor) => new(value.X * factor, value.Y * factor, value.Z * factor);
        private static double Dot(Vector left, Vector right) => (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);
        private static Vector Cross(Vector left, Vector right) => new(
            (left.Y * right.Z) - (left.Z * right.Y),
            (left.Z * right.X) - (left.X * right.Z),
            (left.X * right.Y) - (left.Y * right.X));
        private static Vector Normalize(Vector value)
        {
            var length = Math.Sqrt(Dot(value, value));
            if (length <= 1e-12) throw new InvalidOperationException("Cannot normalize a zero vector.");
            return Scale(value, 1 / length);
        }
        private static double Distance(Point left, Point right)
        {
            var dx = left.X - right.X;
            var dy = left.Y - right.Y;
            var dz = left.Z - right.Z;
            return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }
        private static string F(Point point) => string.Format(CultureInfo.InvariantCulture, "({0:0.###},{1:0.###},{2:0.###})", point.X, point.Y, point.Z);
        private static string F(Vector vector) => string.Format(CultureInfo.InvariantCulture, "({0:0.###},{1:0.###},{2:0.###})", vector.X, vector.Y, vector.Z);

        private sealed class ProbeCase
        {
            private ProbeCase(string id, Point center, Vector axisX, Vector axisY, Vector axisZ, double? depthMm)
            {
                Id = id;
                Center = center;
                AxisX = axisX;
                AxisY = axisY;
                AxisZ = axisZ;
                DepthMm = depthMm;
            }

            public string Id { get; }
            public Point Center { get; }
            public Vector AxisX { get; }
            public Vector AxisY { get; }
            public Vector AxisZ { get; }
            public double DiameterMm { get; } = 24;
            public double? DepthMm { get; }

            public static ProbeCase Create(string id, Point center, Vector axis, double? depthMm)
            {
                var axisZ = Normalize(axis);
                var seed = Math.Abs(axisZ.Z) < 0.9 ? V(0, 0, 1) : V(0, 1, 0);
                var axisX = Normalize(Cross(seed, axisZ));
                var axisY = Normalize(Cross(axisZ, axisX));
                return new ProbeCase(id, center, axisX, axisY, axisZ, depthMm);
            }
        }
    }
}
