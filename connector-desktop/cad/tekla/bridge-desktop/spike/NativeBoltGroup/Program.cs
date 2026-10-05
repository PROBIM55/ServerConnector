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

namespace NativeBoltGroup
{
    internal static class Program
    {
        private const double CoordinateOffset = 485000.0;
        private const double Tolerance = 0.25;

        private static int Main()
        {
            var reportDirectory = Path.Combine(Path.GetTempPath(), "NativeBoltGroup");
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

                var cases = new[]
                {
                    ProbeCase.Create("single-no-holes", P(0, 0, 0), V(0, 0, 1), V(1, 0, 0), "single", false),
                    ProbeCase.Create("linear-site", P(5000, 0, 1000), V(0, 1, 0), V(0, 0, 1), "linear", true),
                    ProbeCase.Create("grid-shop", P(10000, 0, 1500), V(1, 2, 3), V(2, -1, 0), "grid", true),
                    ProbeCase.Create("points-site", P(15000, 0, 2500), V(2, -1, 3), V(1, 2, 0), "points", true),
                };

                foreach (var probeCase in cases)
                {
                    Require(model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane()), "activate global before plates");
                    var target = CreatePlate(probeCase, -12, "TARGET");
                    var participant = CreatePlate(probeCase, 12, "PARTICIPANT");
                    Require(target.Insert(), $"case={probeCase.Id} target insert");
                    Require(participant.Insert(), $"case={probeCase.Id} participant insert");
                    created.Add(target);
                    created.Add(participant);
                    Require(model.CommitChanges(), $"case={probeCase.Id} commit plates");

                    var bolts = CreateBoltGroup(probeCase, target, participant, model);
                    Require(Insert(bolts), $"case={probeCase.Id} bolt group insert");
                    created.Add(bolts);
                    Require(model.CommitChanges(), $"case={probeCase.Id} commit bolts");

                    Require(model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane()), "activate global readback plane");
                    var guid = model.GetGUIDByIdentifier(bolts.Identifier);
                    var readback = model.SelectModelObject(model.GetIdentifierByGUID(guid)) as BoltGroup;
                    if (readback is null || !Select(readback))
                        throw new InvalidOperationException($"case={probeCase.Id} cannot select inserted bolt group.");
                    VerifyReadback(probeCase, target, participant, readback, model, Log);
                }

                RunExecutorProbe(model, created, Log);
                Log($"RESULT capability=boltGroup supported=true cases={cases.Length} arrayPatterns=single,linear,grid pointsPattern=BoltXYList executorUpsert=true executorRollback=true");
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
                Log($"REPORT {reportPath}");
            }
        }

        private static void RunExecutorProbe(Model model, ICollection<ModelObject> created, Action<string> log)
        {
            log("EXECUTOR step=begin");
            Require(model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane()), "executor activate global work plane");

            var gridCase = ProbeCase.Create("executor-grid", P(22000, 0, 2500), V(1, -2, 3), V(2, 1, 0), "grid", true);
            RunExecutorGridProbe(model, created, gridCase, log);

            var pointsCase = ProbeCase.Create("executor-points", P(27500, 0, 3500), V(2, 1, 3), V(-1, 2, 0), "points", true);
            RunExecutorPointsProbe(model, created, pointsCase, log);
        }

        private static void RunExecutorGridProbe(
            Model model,
            ICollection<ModelObject> created,
            ProbeCase probeCase,
            Action<string> log)
        {
            const string caseId = "grid";
            var plan = ExecutorPlan(
                caseId,
                probeCase,
                new { kind = "grid", countX = 3, countY = 2, spacingXmm = 110d, spacingYmm = 85d },
                diameterMm: 24,
                toleranceMm: 2,
                boltType: "shop",
                createHoles: true);
            var targetCommand = plan.Commands.Single(command => command.Source.Id == $"bolt-{caseId}-target");
            var participantCommand = plan.Commands.Single(command => command.Source.Id == $"bolt-{caseId}-participant");
            var boltCommand = plan.Commands.Single(command => command.Kind == "create-bolt-group");
            var ownershipIds = new[]
            {
                TeklaPlanOwnershipIdentity.Create(plan, targetCommand),
                TeklaPlanOwnershipIdentity.Create(plan, participantCommand),
                TeklaPlanOwnershipIdentity.Create(plan, boltCommand),
            };
            DeleteOwnedProbeObjects(model, ownershipIds, log);
            CreateExecutorParts(model, created, probeCase, ownershipIds[0], ownershipIds[1]);

            var executor = new TeklaCreateBoltGroupExecutor();
            log("EXECUTOR grid step=prepare-create");
            var createMutation = executor.Prepare(model, plan, boltCommand, "bolt-group-spike-grid-create");
            createMutation.Apply();
            Require(model.CommitChanges(), "executor grid commit create");
            var createdReadback = createMutation.Readback();
            RequireReadback(createdReadback, "created", "grid", 6, 24, 2, "shop", true);

            var modifiedPlan = ExecutorPlan(
                caseId,
                probeCase,
                new { kind = "grid", countX = 4, countY = 3, spacingXmm = 95d, spacingYmm = 70d },
                diameterMm: 30,
                toleranceMm: 3,
                boltType: "site",
                createHoles: false);
            var modifiedCommand = modifiedPlan.Commands.Single(command => command.Kind == "create-bolt-group");
            log("EXECUTOR grid step=prepare-modify");
            var modifyMutation = executor.Prepare(model, modifiedPlan, modifiedCommand, "bolt-group-spike-grid-modify");
            modifyMutation.Apply();
            Require(model.CommitChanges(), "executor grid commit modify");
            var modifiedReadback = modifyMutation.Readback();
            RequireReadback(modifiedReadback, "modified", "grid", 12, 30, 3, "site", false);
            if (!string.Equals(modifiedReadback.TeklaGuid, createdReadback.TeklaGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("executor grid modify replaced the native bolt-group identity.");

            log("EXECUTOR grid step=rollback-modify");
            modifyMutation.Restore();
            Require(model.CommitChanges(), "executor grid commit modify rollback");
            var restored = model.SelectModelObject(model.GetIdentifierByGUID(createdReadback.TeklaGuid)) as BoltArray;
            if (restored is null || !restored.Select() || Math.Abs(restored.BoltSize - 24) > Tolerance ||
                restored.BoltPositions.Cast<Point>().Count() != 6 || !restored.Hole1 || !restored.Hole2)
            {
                throw new InvalidOperationException("executor grid rollback did not restore the original native state.");
            }

            log("EXECUTOR grid step=reject-length");
            var lengthPlan = ExecutorPlan(
                caseId,
                probeCase,
                new { kind = "grid", countX = 3, countY = 2, spacingXmm = 110d, spacingYmm = 85d },
                24,
                2,
                "shop",
                true,
                80);
            var lengthCommand = lengthPlan.Commands.Single(command => command.Kind == "create-bolt-group");
            ExpectFailure(
                () => executor.Prepare(model, lengthPlan, lengthCommand, "bolt-group-spike-length"),
                "TEKLA_PLAN_BOLT_LENGTH_UNSUPPORTED");

            log("EXECUTOR grid step=rollback-create");
            createMutation.Restore();
            Require(model.CommitChanges(), "executor grid commit create rollback");
            if (model.SelectModelObject(model.GetIdentifierByGUID(createdReadback.TeklaGuid)) is BoltGroup)
                throw new InvalidOperationException("executor grid create rollback did not delete the bolt group.");

            log($"EXECUTOR grid createGuid={createdReadback.TeklaGuid} upsertGuid={modifiedReadback.TeklaGuid} restored=true deleted=true");
        }

        private static void RunExecutorPointsProbe(
            Model model,
            ICollection<ModelObject> created,
            ProbeCase probeCase,
            Action<string> log)
        {
            const string caseId = "points";
            var points = new[] { new[] { 0d, 0d }, new[] { 135d, 40d }, new[] { 60d, 150d } };
            var plan = ExecutorPlan(caseId, probeCase, new { kind = "points", points }, 20, 1.5, "site", true);
            var targetCommand = plan.Commands.Single(command => command.Source.Id == $"bolt-{caseId}-target");
            var participantCommand = plan.Commands.Single(command => command.Source.Id == $"bolt-{caseId}-participant");
            var boltCommand = plan.Commands.Single(command => command.Kind == "create-bolt-group");
            var ownershipIds = new[]
            {
                TeklaPlanOwnershipIdentity.Create(plan, targetCommand),
                TeklaPlanOwnershipIdentity.Create(plan, participantCommand),
                TeklaPlanOwnershipIdentity.Create(plan, boltCommand),
            };
            DeleteOwnedProbeObjects(model, ownershipIds, log);
            CreateExecutorParts(model, created, probeCase, ownershipIds[0], ownershipIds[1]);

            var executor = new TeklaCreateBoltGroupExecutor();
            log("EXECUTOR points step=prepare-create");
            var createMutation = executor.Prepare(model, plan, boltCommand, "bolt-group-spike-points-create");
            createMutation.Apply();
            Require(model.CommitChanges(), "executor points commit create");
            var createdReadback = createMutation.Readback();
            RequireReadback(createdReadback, "created", "points", 3, 20, 1.5, "site", true);

            log("EXECUTOR points step=idempotent-modify");
            var modifyMutation = executor.Prepare(model, plan, boltCommand, "bolt-group-spike-points-modify");
            modifyMutation.Apply();
            Require(model.CommitChanges(), "executor points commit idempotent modify");
            var modifiedReadback = modifyMutation.Readback();
            RequireReadback(modifiedReadback, "modified", "points", 3, 20, 1.5, "site", true);
            if (!string.Equals(modifiedReadback.TeklaGuid, createdReadback.TeklaGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("executor points modify replaced the native bolt-group identity.");
            modifyMutation.Restore();
            Require(model.CommitChanges(), "executor points commit modify rollback");

            log("EXECUTOR points step=reject-point-change");
            var changedPlan = ExecutorPlan(
                caseId,
                probeCase,
                new { kind = "points", points = new[] { new[] { 0d, 0d }, new[] { 150d, 45d }, new[] { 80d, 170d } } },
                20,
                1.5,
                "site",
                true);
            var changedCommand = changedPlan.Commands.Single(command => command.Kind == "create-bolt-group");
            var changedMutation = executor.Prepare(model, changedPlan, changedCommand, "bolt-group-spike-points-change");
            ExpectFailure(changedMutation.Apply, "TEKLA_PLAN_BOLT_POINTS_MODIFY_UNSUPPORTED");
            Require(model.CommitChanges(), "executor points commit rejected change rollback");

            createMutation.Restore();
            Require(model.CommitChanges(), "executor points commit create rollback");
            if (model.SelectModelObject(model.GetIdentifierByGUID(createdReadback.TeklaGuid)) is BoltGroup)
                throw new InvalidOperationException("executor points create rollback did not delete the bolt group.");

            log($"EXECUTOR points createGuid={createdReadback.TeklaGuid} idempotentGuid={modifiedReadback.TeklaGuid} changedPointsRejected=true deleted=true");
        }

        private static void CreateExecutorParts(
            Model model,
            ICollection<ModelObject> created,
            ProbeCase probeCase,
            string targetOwnershipId,
            string participantOwnershipId)
        {
            Require(model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane()), "executor activate global before parts");
            var target = CreatePlate(probeCase, -12, "EXECUTOR_TARGET");
            var participant = CreatePlate(probeCase, 12, "EXECUTOR_PARTICIPANT");
            Require(target.Insert(), "executor target insert");
            Require(participant.Insert(), "executor participant insert");
            created.Add(target);
            created.Add(participant);
            Require(model.CommitChanges(), "executor commit parts");
            Require(target.SetUserProperty(StructuraServiceUdas.ExternalObjectId, targetOwnershipId), "executor target ownership UDA");
            Require(participant.SetUserProperty(StructuraServiceUdas.ExternalObjectId, participantOwnershipId), "executor participant ownership UDA");
            Require(target.Modify(), "executor target ownership modify");
            Require(participant.Modify(), "executor participant ownership modify");
            Require(model.CommitChanges(), "executor commit part ownership");
        }

        private static TeklaPlanDocument ExecutorPlan(
            string caseId,
            ProbeCase probeCase,
            object pattern,
            double diameterMm,
            double toleranceMm,
            string boltType,
            bool createHoles,
            double? lengthMm = null)
        {
            var targetCommandId = $"element:bolt-{caseId}-target:create-contour-plate";
            var participantCommandId = $"element:bolt-{caseId}-participant:create-contour-plate";
            var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["target"] = JsonSerializer.SerializeToElement(new { elementId = $"bolt-{caseId}-target", role = "part" }),
                ["participants"] = JsonSerializer.SerializeToElement(new[] { new { elementId = $"bolt-{caseId}-participant", role = "part" } }),
                ["frame"] = JsonSerializer.SerializeToElement(new
                {
                    origin = new[] { probeCase.Origin.X, probeCase.Origin.Y, probeCase.Origin.Z },
                    axisX = new[] { probeCase.AxisX.X, probeCase.AxisX.Y, probeCase.AxisX.Z },
                    axisY = new[] { probeCase.AxisY.X, probeCase.AxisY.Y, probeCase.AxisY.Z },
                    axisZ = new[] { probeCase.AxisZ.X, probeCase.AxisZ.Y, probeCase.AxisZ.Z },
                }),
                ["boltStandard"] = JsonSerializer.SerializeToElement("7990"),
                ["diameterMm"] = JsonSerializer.SerializeToElement(diameterMm),
                ["toleranceMm"] = JsonSerializer.SerializeToElement(toleranceMm),
                ["boltType"] = JsonSerializer.SerializeToElement(boltType),
                ["pattern"] = JsonSerializer.SerializeToElement(pattern),
                ["createHoles"] = JsonSerializer.SerializeToElement(createHoles),
            };
            if (lengthMm.HasValue) payload["lengthMm"] = JsonSerializer.SerializeToElement(lengthMm.Value);

            return new TeklaPlanDocument
            {
                SchemaVersion = TeklaPlanContract.SchemaVersion,
                Source = new TeklaPlanSource
                {
                    Address = new ConstructiveAddress
                    {
                        ProjectId = "bolt-group-spike",
                        ModuleId = "bridge",
                        ModuleVariantId = $"native-bolt-group-{caseId}",
                        RevisionId = "live",
                    },
                    GenerationId = "live",
                    SourceHash = "bolt-group-spike-source",
                    ConstructiveContentHash = "bolt-group-spike-constructive",
                },
                Commands = new[]
                {
                    BasePartCommand(targetCommandId, $"bolt-{caseId}-target", $"bolt-group-spike/{caseId}/target"),
                    BasePartCommand(participantCommandId, $"bolt-{caseId}-participant", $"bolt-group-spike/{caseId}/participant"),
                    new TeklaPlanCommand
                    {
                        CommandId = $"feature:bolt-{caseId}:create-bolt-group",
                        Kind = "create-bolt-group",
                        Phase = "holes-and-bolts",
                        Source = new TeklaPlanSourceRef
                        {
                            Kind = "feature",
                            Id = $"bolt-{caseId}",
                            StableKey = $"bolt-group-spike/{caseId}/bolt-group",
                            Role = "bolt-group",
                            SourceLayer = "generated",
                        },
                        DependsOn = new[] { targetCommandId, participantCommandId },
                        Ownership = new TeklaOwnershipStamp { Namespace = TeklaPlanContract.OwnershipNamespace },
                        Payload = payload,
                    },
                },
            };
        }

        private static TeklaPlanCommand BasePartCommand(string commandId, string elementId, string stableKey)
            => new()
            {
                CommandId = commandId,
                Kind = "create-contour-plate",
                Phase = "base-parts",
                Source = new TeklaPlanSourceRef
                {
                    Kind = "element",
                    Id = elementId,
                    StableKey = stableKey,
                    Role = "part",
                    SourceLayer = "generated",
                },
                Ownership = new TeklaOwnershipStamp { Namespace = TeklaPlanContract.OwnershipNamespace },
            };

        private static void RequireReadback(
            TeklaNativeCommandReadback readback,
            string action,
            string patternKind,
            int positionCount,
            double diameterMm,
            double toleranceMm,
            string boltType,
            bool createsHoles)
        {
            if (!string.Equals(readback.Action, action, StringComparison.Ordinal) ||
                !string.Equals(readback.BoltPatternKind, patternKind, StringComparison.Ordinal) ||
                readback.BoltPositions.Length != positionCount ||
                Math.Abs(readback.BoltDiameterMm - diameterMm) > Tolerance ||
                Math.Abs(readback.BoltToleranceMm - toleranceMm) > Tolerance ||
                !string.Equals(readback.BoltType, boltType, StringComparison.Ordinal) ||
                readback.BoltCreatesHoles != createsHoles ||
                readback.BoltLengthMm.HasValue ||
                string.IsNullOrWhiteSpace(readback.TeklaGuid))
            {
                throw new InvalidOperationException($"executor {patternKind} {action} readback mismatch.");
            }
        }

        private static void ExpectFailure(Action action, string expectedCode)
        {
            try
            {
                action();
            }
            catch (TeklaNativeExecutionException exception) when (string.Equals(exception.ErrorCode, expectedCode, StringComparison.Ordinal))
            {
                return;
            }
            throw new InvalidOperationException($"Expected native diagnostic '{expectedCode}'.");
        }

        private static void DeleteOwnedProbeObjects(Model model, IReadOnlyCollection<string> ownershipIds, Action<string> log)
        {
            var deleted = 0;
            foreach (var objectType in new[]
            {
                ModelObject.ModelObjectEnum.BOLT_ARRAY,
                ModelObject.ModelObjectEnum.BOLT_XYLIST,
                ModelObject.ModelObjectEnum.CONTOURPLATE,
            })
            {
                var enumerator = model.GetModelObjectSelector().GetAllObjectsWithType(objectType);
                while (enumerator.MoveNext())
                {
                    if (enumerator.Current is not ModelObject candidate) continue;
                    var externalObjectId = string.Empty;
                    if (!candidate.GetUserProperty(StructuraServiceUdas.ExternalObjectId, ref externalObjectId) ||
                        !ownershipIds.Contains(externalObjectId)) continue;
                    Require(candidate.Delete(), $"executor stale {candidate.GetType().Name}.Delete");
                    deleted++;
                }
            }
            if (deleted == 0) return;
            Require(model.CommitChanges(), "executor commit stale cleanup");
            log($"EXECUTOR cleanup staleOwnedObjects={deleted}");
        }

        private static ContourPlate CreatePlate(ProbeCase probeCase, double normalOffset, string role)
        {
            var center = Add(probeCase.Origin, Scale(probeCase.AxisZ, normalOffset));
            var contour = new Contour();
            foreach (var point in new[]
            {
                Add(center, Add(Scale(probeCase.AxisX, -900), Scale(probeCase.AxisY, -650))),
                Add(center, Add(Scale(probeCase.AxisX, 900), Scale(probeCase.AxisY, -650))),
                Add(center, Add(Scale(probeCase.AxisX, 900), Scale(probeCase.AxisY, 650))),
                Add(center, Add(Scale(probeCase.AxisX, -900), Scale(probeCase.AxisY, 650))),
            })
            {
                contour.AddContourPoint(new ContourPoint(point, new Chamfer { Type = Chamfer.ChamferTypeEnum.CHAMFER_NONE }));
            }
            var plate = new ContourPlate
            {
                Contour = contour,
                Name = $"STRUCTURA_BOLT_GROUP_SMOKE_{role}",
                Class = "99",
            };
            plate.Profile.ProfileString = "PL20";
            plate.Material.MaterialString = "S355";
            plate.Position.Depth = Position.DepthEnum.MIDDLE;
            return plate;
        }

        private static BoltGroup CreateBoltGroup(ProbeCase probeCase, Part target, Part participant, Model model)
        {
            Require(model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane(new CoordinateSystem(
                probeCase.Origin,
                probeCase.AxisX,
                probeCase.AxisY))), $"case={probeCase.Id} activate bolt work plane");

            BoltGroup bolts;
            if (probeCase.PatternKind == "points")
            {
                var list = new BoltXYList();
                foreach (var point in probeCase.LocalPoints)
                {
                    Require(list.AddBoltDistX(point.X), $"case={probeCase.Id} add point X");
                    Require(list.AddBoltDistY(point.Y), $"case={probeCase.Id} add point Y");
                }
                bolts = list;
            }
            else
            {
                var array = new BoltArray();
                foreach (var distance in probeCase.DistancesX)
                    Require(array.AddBoltDistX(distance), $"case={probeCase.Id} add X distance");
                foreach (var distance in probeCase.DistancesY)
                    Require(array.AddBoltDistY(distance), $"case={probeCase.Id} add Y distance");
                bolts = array;
            }

            bolts.PartToBeBolted = target;
            bolts.PartToBoltTo = participant;
            bolts.FirstPosition = new Point(0, 0, 0);
            bolts.SecondPosition = new Point(100, 0, 0);
            bolts.BoltSize = 24;
            bolts.BoltStandard = "7798";
            bolts.BoltType = probeCase.BoltType;
            bolts.ThreadInMaterial = BoltGroup.BoltThreadInMaterialEnum.THREAD_IN_MATERIAL_YES;
            bolts.Length = 80;
            bolts.CutLength = 200;
            bolts.ExtraLength = 0;
            bolts.Tolerance = 2;
            bolts.HoleType = BoltGroup.BoltHoleTypeEnum.HOLE_TYPE_SLOTTED;
            bolts.PlainHoleType = BoltGroup.BoltPlainHoleTypeEnum.HOLE_TYPE_THROUGH;
            bolts.SlottedHoleX = 0;
            bolts.SlottedHoleY = 0;
            bolts.SlotOffsetX = 0;
            bolts.SlotOffsetY = 0;
            bolts.RotateSlots = BoltGroup.BoltRotateSlotsEnum.ROTATE_SLOTS_PARALLEL;
            bolts.Washer1 = true;
            bolts.Washer2 = false;
            bolts.Washer3 = false;
            bolts.Nut1 = true;
            bolts.Nut2 = false;
            bolts.Bolt = true;
            bolts.Hole1 = probeCase.CreateHoles;
            bolts.Hole2 = probeCase.CreateHoles;
            bolts.Hole3 = false;
            bolts.Hole4 = false;
            bolts.Hole5 = false;
            bolts.ConnectAssemblies = false;
            bolts.Position.Plane = Position.PlaneEnum.MIDDLE;
            bolts.Position.Depth = Position.DepthEnum.MIDDLE;
            bolts.Position.Rotation = Position.RotationEnum.FRONT;
            return bolts;
        }

        private static void VerifyReadback(
            ProbeCase probeCase,
            Part target,
            Part participant,
            BoltGroup bolts,
            Model model,
            Action<string> log)
        {
            if (bolts.PartToBeBolted is not Part first || !SameObject(first, target) ||
                bolts.PartToBoltTo is not Part second || !SameObject(second, participant))
                throw new InvalidOperationException($"case={probeCase.Id} part references mismatch.");
            if (!bolts.Bolt || bolts.Hole1 != probeCase.CreateHoles || bolts.Hole2 != probeCase.CreateHoles ||
                bolts.Hole3 || bolts.Hole4 || bolts.Hole5)
                throw new InvalidOperationException($"case={probeCase.Id} bolt/hole flags mismatch.");
            if (Math.Abs(bolts.BoltSize - 24) > Tolerance || Math.Abs(bolts.Length) > Tolerance ||
                Math.Abs(bolts.Tolerance - 2) > Tolerance || bolts.BoltStandard != "7798" ||
                bolts.BoltType != probeCase.BoltType)
                throw new InvalidOperationException(
                    $"case={probeCase.Id} native dimensions or catalog properties mismatch: " +
                    $"size={bolts.BoltSize:R}, length={bolts.Length:R}, tolerance={bolts.Tolerance:R}, " +
                    $"standard={bolts.BoltStandard}, type={bolts.BoltType}; " +
                    $"expected size=24, Tekla-normalized length=0, tolerance=2, standard=7798, type={probeCase.BoltType}.");
            if ((probeCase.PatternKind == "points" && bolts is not BoltXYList) ||
                (probeCase.PatternKind != "points" && bolts is not BoltArray))
                throw new InvalidOperationException($"case={probeCase.Id} native pattern type mismatch.");

            var positions = bolts.BoltPositions.Cast<Point>().ToArray();
            var expected = probeCase.ExpectedWorldPositions().ToArray();
            if (positions.Length != expected.Length)
                throw new InvalidOperationException($"case={probeCase.Id} expected {expected.Length} positions, got {positions.Length}.");
            foreach (var point in expected)
            {
                if (!positions.Any(actual => Distance(actual, point) <= Tolerance))
                    throw new InvalidOperationException($"case={probeCase.Id} missing expected position {F(point)}; actual={string.Join(",", positions.Select(F))}.");
            }

            var system = bolts.GetCoordinateSystem();
            var axisX = Normalize(system.AxisX);
            var axisZ = Normalize(Cross(system.AxisX, system.AxisY));
            if (Distance(system.Origin, probeCase.Origin) > Tolerance ||
                Dot(axisX, probeCase.AxisX) < 1 - 1e-6 ||
                Dot(axisZ, probeCase.AxisZ) < 1 - 1e-6)
                throw new InvalidOperationException($"case={probeCase.Id} frame readback mismatch.");

            var guid = model.GetGUIDByIdentifier(bolts.Identifier) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(guid) || bolts.Identifier.ID <= 0)
                throw new InvalidOperationException($"case={probeCase.Id} persistent identity is missing.");
            log($"CASE id={probeCase.Id} native={bolts.GetType().Name} guid={guid} positions={positions.Length} holes={probeCase.CreateHoles} boltType={bolts.BoltType} frameOrigin={F(system.Origin)} axis={F(axisZ)}");
        }

        private static bool Insert(BoltGroup bolts)
            => bolts switch
            {
                BoltArray array => array.Insert(),
                BoltXYList list => list.Insert(),
                _ => false,
            };

        private static bool Select(BoltGroup bolts)
            => bolts switch
            {
                BoltArray array => array.Select(),
                BoltXYList list => list.Select(),
                _ => false,
            };

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
            private ProbeCase(
                string id,
                Point origin,
                Vector axisX,
                Vector axisY,
                Vector axisZ,
                string patternKind,
                bool createHoles,
                BoltGroup.BoltTypeEnum boltType,
                IReadOnlyList<double> distancesX,
                IReadOnlyList<double> distancesY,
                IReadOnlyList<LocalPoint> localPoints)
            {
                Id = id;
                Origin = origin;
                AxisX = axisX;
                AxisY = axisY;
                AxisZ = axisZ;
                PatternKind = patternKind;
                CreateHoles = createHoles;
                BoltType = boltType;
                DistancesX = distancesX;
                DistancesY = distancesY;
                LocalPoints = localPoints;
            }

            public string Id { get; }
            public Point Origin { get; }
            public Vector AxisX { get; }
            public Vector AxisY { get; }
            public Vector AxisZ { get; }
            public string PatternKind { get; }
            public bool CreateHoles { get; }
            public BoltGroup.BoltTypeEnum BoltType { get; }
            public IReadOnlyList<double> DistancesX { get; }
            public IReadOnlyList<double> DistancesY { get; }
            public IReadOnlyList<LocalPoint> LocalPoints { get; }

            public IEnumerable<Point> ExpectedWorldPositions()
            {
                foreach (var point in LocalPoints)
                    yield return Add(Origin, Add(Scale(AxisX, point.X), Scale(AxisY, point.Y)));
            }

            public static ProbeCase Create(
                string id,
                Point origin,
                Vector axis,
                Vector axisXSeed,
                string patternKind,
                bool createHoles)
            {
                var axisZ = Normalize(axis);
                var projectedX = Add(axisXSeed, Scale(axisZ, -Dot(axisXSeed, axisZ)));
                var axisX = Normalize(projectedX);
                var axisY = Normalize(Cross(axisZ, axisX));
                var boltType = patternKind == "grid"
                    ? BoltGroup.BoltTypeEnum.BOLT_TYPE_WORKSHOP
                    : BoltGroup.BoltTypeEnum.BOLT_TYPE_SITE;
                return patternKind switch
                {
                    "single" => new ProbeCase(id, origin, axisX, axisY, axisZ, patternKind, createHoles, boltType,
                        new[] { 0d }, new[] { 0d }, new[] { new LocalPoint(0, 0) }),
                    "linear" => new ProbeCase(id, origin, axisX, axisY, axisZ, patternKind, createHoles, boltType,
                        new[] { 0d, 120d, 120d }, new[] { 0d },
                        new[] { new LocalPoint(0, 0), new LocalPoint(120, 0), new LocalPoint(240, 0) }),
                    "grid" => new ProbeCase(id, origin, axisX, axisY, axisZ, patternKind, createHoles, boltType,
                        new[] { 0d, 110d, 110d }, new[] { 0d, 85d },
                        new[]
                        {
                            new LocalPoint(0, -42.5), new LocalPoint(110, -42.5), new LocalPoint(220, -42.5),
                            new LocalPoint(0, 42.5), new LocalPoint(110, 42.5), new LocalPoint(220, 42.5),
                        }),
                    _ => new ProbeCase(id, origin, axisX, axisY, axisZ, patternKind, createHoles, boltType,
                        Array.Empty<double>(), Array.Empty<double>(),
                        new[] { new LocalPoint(0, 0), new LocalPoint(135, 40), new LocalPoint(60, 150) }),
                };
            }
        }

        private sealed class LocalPoint
        {
            public LocalPoint(double x, double y)
            {
                X = x;
                Y = y;
            }

            public double X { get; }
            public double Y { get; }
        }
    }
}
