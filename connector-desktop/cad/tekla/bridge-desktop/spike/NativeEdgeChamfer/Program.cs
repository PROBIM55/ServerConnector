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

namespace NativeEdgeChamfer
{
    internal static class Program
    {
        private const double CoordinateOffset = 628000.0;
        private const double Tolerance = 0.25;

        private static int Main()
        {
            var reportDirectory = Path.Combine(Path.GetTempPath(), "NativeEdgeChamfer");
            Directory.CreateDirectory(reportDirectory);
            using var report = new StreamWriter(Path.Combine(reportDirectory, "report.txt"), append: false) { AutoFlush = true };
            Model? model = null;
            TransformationPlane? previousPlane = null;
            ContourPlate? plate = null;
            EdgeChamfer? treatment = null;

            void Log(string message)
            {
                var line = $"[{DateTime.UtcNow:O}] {message}";
                Console.WriteLine(line);
                report.WriteLine(line);
            }

            try
            {
                model = new Model();
                Require(model.GetConnectionStatus(), "Tekla 2025 model is not connected");
                var info = model.GetInfo();
                Log($"MODEL name={info.ModelName} path={info.ModelPath}");
                previousPlane = model.GetWorkPlaneHandler().GetCurrentTransformationPlane();
                Require(model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane()), "activate global work plane");

                plate = CreatePlate();
                Require(plate.Insert(), "ContourPlate.Insert");
                Require(model.CommitChanges(), "commit plate");
                var plateGuid = model.GetGUIDByIdentifier(plate.Identifier);
                Require(!string.IsNullOrWhiteSpace(plateGuid), "persistent ContourPlate GUID");

                var first = P(0, 0, 10);
                var second = P(1600, 0, 10);
                treatment = CreateTreatment(plate, first, second, Chamfer.ChamferTypeEnum.CHAMFER_LINE, 24, 16);
                Require(treatment.Insert(), "EdgeChamfer.Insert");
                Require(model.CommitChanges(), "commit CHAMFER_LINE");
                var treatmentGuid = model.GetGUIDByIdentifier(treatment.Identifier);
                Require(!string.IsNullOrWhiteSpace(treatmentGuid), "persistent EdgeChamfer GUID");
                var lineReadback = ReadTreatment(model, treatmentGuid);
                VerifyTreatment(model, lineReadback, plateGuid, first, second, Chamfer.ChamferTypeEnum.CHAMFER_LINE, 24, 16);
                Log($"STEP line=true treatmentGuid={treatmentGuid} fatherGuid={plateGuid}");

                var roundRejected = false;
                lineReadback.Chamfer = new Chamfer
                {
                    Type = Chamfer.ChamferTypeEnum.CHAMFER_ROUNDING,
                    X = 18,
                    Y = 18,
                };
                try
                {
                    lineReadback.Modify();
                }
                catch (ArgumentException)
                {
                    roundRejected = true;
                }
                Require(roundRejected, "CHAMFER_ROUNDING unexpectedly accepted by EdgeChamfer");
                lineReadback = ReadTreatment(model, treatmentGuid);
                VerifyTreatment(model, lineReadback, plateGuid, first, second, Chamfer.ChamferTypeEnum.CHAMFER_LINE, 24, 16);
                Log("STEP roundUnsupported=true");

                lineReadback.Chamfer = new Chamfer
                {
                    Type = Chamfer.ChamferTypeEnum.CHAMFER_LINE,
                    X = 18,
                    Y = 12,
                };
                Require(lineReadback.Modify(), "EdgeChamfer.Modify CHAMFER_LINE");
                Require(model.CommitChanges(), "commit modified CHAMFER_LINE");
                var modifiedReadback = ReadTreatment(model, treatmentGuid);
                VerifyTreatment(model, modifiedReadback, plateGuid, first, second, Chamfer.ChamferTypeEnum.CHAMFER_LINE, 18, 12);
                Require(string.Equals(model.GetGUIDByIdentifier(modifiedReadback.Identifier), treatmentGuid, StringComparison.OrdinalIgnoreCase), "EdgeChamfer GUID changed after Modify");
                Log("STEP modifyLine=true guidPreserved=true");

                Require(modifiedReadback.Delete(), "positive EdgeChamfer.Delete");
                Require(model.CommitChanges(), "commit EdgeChamfer.Delete");
                Require(model.SelectModelObject(model.GetIdentifierByGUID(treatmentGuid)) is null, "EdgeChamfer still exists after Delete");
                treatment = null;

                first = P(0, 0, -10);
                second = P(1600, 0, -10);
                treatment = CreateTreatment(plate, first, second, Chamfer.ChamferTypeEnum.CHAMFER_LINE, 14, 10);
                Require(treatment.Insert(), "negative EdgeChamfer.Insert");
                Require(model.CommitChanges(), "commit negative CHAMFER_LINE");
                var negativeGuid = model.GetGUIDByIdentifier(treatment.Identifier);
                Require(!string.IsNullOrWhiteSpace(negativeGuid), "persistent negative EdgeChamfer GUID");
                var negativeReadback = ReadTreatment(model, negativeGuid);
                VerifyTreatment(model, negativeReadback, plateGuid, first, second, Chamfer.ChamferTypeEnum.CHAMFER_LINE, 14, 10);
                Log("STEP negativeSide=true");

                Require(negativeReadback.Delete(), "negative EdgeChamfer.Delete");
                Require(model.CommitChanges(), "commit negative EdgeChamfer.Delete");
                Require(model.SelectModelObject(model.GetIdentifierByGUID(negativeGuid)) is null, "negative EdgeChamfer still exists after Delete");
                treatment = null;
                Log("STEP rollbackDelete=true");

                RunExecutorProbe(model, Log);

                Log("RESULT capability=edgeChamfer supported=true exactEndpoints=true positiveSide=true negativeSide=true line=true roundUnsupported=true guidPreserved=true rollbackDelete=true executorUpsert=true executorRollback=true");
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
                    if (treatment is not null)
                    {
                        try
                        {
                            var guid = model.GetGUIDByIdentifier(treatment.Identifier);
                            var selected = string.IsNullOrWhiteSpace(guid) ? treatment : model.SelectModelObject(model.GetIdentifierByGUID(guid));
                            if (selected is not null) selected.Delete();
                        }
                        catch (Exception exception) { Log($"CLEANUP_WARN EdgeChamfer: {exception.Message}"); }
                    }
                    if (plate is not null)
                    {
                        try
                        {
                            var guid = model.GetGUIDByIdentifier(plate.Identifier);
                            var selected = string.IsNullOrWhiteSpace(guid) ? plate : model.SelectModelObject(model.GetIdentifierByGUID(guid));
                            if (selected is not null) selected.Delete();
                            model.CommitChanges();
                        }
                        catch (Exception exception) { Log($"CLEANUP_WARN ContourPlate: {exception.Message}"); }
                    }
                    if (previousPlane is not null)
                    {
                        try { model.GetWorkPlaneHandler().SetCurrentTransformationPlane(previousPlane); }
                        catch (Exception exception) { Log($"CLEANUP_WARN work plane: {exception.Message}"); }
                    }
                }
            }
        }

        private static void RunExecutorProbe(Model model, Action<string> log)
        {
            const string baseCommandId = "element:edge-treatment-target:create-contour-plate";
            var initialPlan = ExecutorPlan(28, 17);
            var baseCommand = initialPlan.Commands.Single(command => command.CommandId == baseCommandId);
            var treatmentCommand = initialPlan.Commands.Single(command => command.Kind == "apply-edge-treatment");
            var baseOwnershipId = TeklaPlanOwnershipIdentity.Create(initialPlan, baseCommand);
            var treatmentOwnershipId = TeklaPlanOwnershipIdentity.Create(initialPlan, treatmentCommand);
            DeleteOwnedProbeObjects(model, treatmentOwnershipId, baseOwnershipId, log);

            var plateExecutor = new TeklaCreateContourPlateExecutor();
            var treatmentExecutor = new TeklaApplyEdgeTreatmentExecutor();
            var createBase = plateExecutor.Prepare(model, initialPlan, baseCommand, "edge-treatment-spike-create");
            var createTreatment = treatmentExecutor.Prepare(model, initialPlan, treatmentCommand, "edge-treatment-spike-create");
            var created = TeklaPlanTransaction.Execute(
                new ITeklaPreparedMutation<TeklaNativeCommandReadback>[] { createBase, createTreatment },
                () => Require(model.CommitChanges(), "executor create transaction commit"));
            var baseReadback = created.Single(item => item.CommandId == baseCommandId);
            var treatmentReadback = created.Single(item => item.CommandId == treatmentCommand.CommandId);
            Require(string.Equals(baseReadback.TeklaGuid, treatmentReadback.TargetTeklaGuid, StringComparison.OrdinalIgnoreCase), "executor target GUID matches base plate");
            VerifyExecutorReadback(treatmentReadback, "positive", 28, 17);

            var modifiedPlan = ExecutorPlan(19, 11);
            var modifiedBaseCommand = modifiedPlan.Commands.Single(command => command.CommandId == baseCommandId);
            var modifiedTreatmentCommand = modifiedPlan.Commands.Single(command => command.Kind == "apply-edge-treatment");
            var modifyBase = plateExecutor.Prepare(model, modifiedPlan, modifiedBaseCommand, "edge-treatment-spike-modify");
            var modifyTreatment = treatmentExecutor.Prepare(model, modifiedPlan, modifiedTreatmentCommand, "edge-treatment-spike-modify");
            var modified = TeklaPlanTransaction.Execute(
                new ITeklaPreparedMutation<TeklaNativeCommandReadback>[] { modifyBase, modifyTreatment },
                () => Require(model.CommitChanges(), "executor modify transaction commit"));
            var modifiedBaseReadback = modified.Single(item => item.CommandId == baseCommandId);
            var modifiedTreatmentReadback = modified.Single(item => item.CommandId == treatmentCommand.CommandId);
            Require(string.Equals(baseReadback.TeklaGuid, modifiedBaseReadback.TeklaGuid, StringComparison.OrdinalIgnoreCase), "executor upsert preserves target GUID");
            Require(string.Equals(treatmentReadback.TeklaGuid, modifiedTreatmentReadback.TeklaGuid, StringComparison.OrdinalIgnoreCase), "executor upsert preserves EdgeChamfer GUID");
            VerifyExecutorReadback(modifiedTreatmentReadback, "positive", 19, 11);

            var rollbackPlan = ExecutorPlan(9, 7);
            var rollbackCommand = rollbackPlan.Commands.Single(command => command.Kind == "apply-edge-treatment");
            var rollbackMutation = treatmentExecutor.Prepare(model, rollbackPlan, rollbackCommand, "edge-treatment-spike-rollback");
            rollbackMutation.Apply();
            Require(model.CommitChanges(), "executor rollback probe apply commit");
            VerifyExecutorReadback(rollbackMutation.Readback(), "positive", 9, 7);
            rollbackMutation.Restore();
            Require(model.CommitChanges(), "executor rollback probe restore commit");

            var restored = ReadTreatment(model, modifiedTreatmentReadback.TeklaGuid);
            VerifyTreatment(
                model,
                restored,
                modifiedBaseReadback.TeklaGuid,
                P(3000, 0, 10),
                P(4400, 0, 10),
                Chamfer.ChamferTypeEnum.CHAMFER_LINE,
                19,
                11);

            DeleteOwnedProbeObjects(model, treatmentOwnershipId, baseOwnershipId, log);
            log($"EXECUTOR targetGuid={baseReadback.TeklaGuid} edgeGuid={treatmentReadback.TeklaGuid} exactEdge=outer-e1 side=positive upsert=true rollback=true");
        }

        private static TeklaPlanDocument ExecutorPlan(double sizeMm, double secondarySizeMm)
        {
            const string baseCommandId = "element:edge-treatment-target:create-contour-plate";
            var baseCommand = new TeklaPlanCommand
            {
                CommandId = baseCommandId,
                Kind = "create-contour-plate",
                Phase = "base-parts",
                Source = new TeklaPlanSourceRef
                {
                    Kind = "element",
                    Id = "edge-treatment-target",
                    StableKey = "edge-treatment-spike/target",
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
                            new { id = "outer-v3", point = new[] { 1400d, 800d } },
                            new { id = "outer-v4", point = new[] { 0d, 800d } },
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
            var treatmentCommand = new TeklaPlanCommand
            {
                CommandId = "feature:outer-e1-positive:apply-edge-treatment",
                Kind = "apply-edge-treatment",
                Phase = "edge-treatments",
                Source = new TeklaPlanSourceRef
                {
                    Kind = "feature",
                    Id = "outer-e1-positive-treatment",
                    StableKey = "edge-treatment-spike/outer-e1/positive",
                    Role = "edge-treatment",
                    SourceLayer = "generated",
                },
                DependsOn = new[] { baseCommandId },
                Ownership = new TeklaOwnershipStamp { Namespace = TeklaPlanContract.OwnershipNamespace },
                Payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["target"] = JsonSerializer.SerializeToElement(new { elementId = "edge-treatment-target", role = "part" }),
                    ["targetTopology"] = JsonSerializer.SerializeToElement(new { kind = "contour-edge", contourId = "outer", edgeId = "outer-e1", side = "positive" }),
                    ["treatmentType"] = JsonSerializer.SerializeToElement("chamfer"),
                    ["sizeMm"] = JsonSerializer.SerializeToElement(sizeMm),
                    ["secondarySizeMm"] = JsonSerializer.SerializeToElement(secondarySizeMm),
                },
            };
            return new TeklaPlanDocument
            {
                SchemaVersion = TeklaPlanContract.SchemaVersion,
                Source = new TeklaPlanSource
                {
                    Address = new ConstructiveAddress
                    {
                        ProjectId = "edge-treatment-spike",
                        ModuleId = "bridge",
                        ModuleVariantId = "native-edge-treatment",
                        RevisionId = "live",
                    },
                    GenerationId = "live",
                    SourceHash = "edge-treatment-spike-source",
                    ConstructiveContentHash = "edge-treatment-spike-constructive",
                },
                Commands = new[] { baseCommand, treatmentCommand },
            };
        }

        private static void VerifyExecutorReadback(
            TeklaNativeCommandReadback readback,
            string side,
            double sizeMm,
            double secondarySizeMm)
        {
            Require(readback.TargetContourId == "outer", "executor contour readback mismatch");
            Require(readback.TargetEdgeId == "outer-e1", "executor edge readback mismatch");
            Require(readback.TargetEdgeSide == side, "executor physical side readback mismatch");
            Require(readback.EdgeTreatmentType == "chamfer", "executor treatment type readback mismatch");
            Require(Math.Abs(readback.EdgeTreatmentSizeMm - sizeMm) <= Tolerance, "executor treatment size readback mismatch");
            Require(readback.EdgeTreatmentSecondarySizeMm.HasValue && Math.Abs(readback.EdgeTreatmentSecondarySizeMm.Value - secondarySizeMm) <= Tolerance, "executor secondary treatment size readback mismatch");
        }

        private static void DeleteOwnedProbeObjects(
            Model model,
            string treatmentOwnershipId,
            string baseOwnershipId,
            Action<string> log)
        {
            var deleted = 0;
            var all = model.GetModelObjectSelector()
                .GetAllObjectsWithType(ModelObject.ModelObjectEnum.EDGE_CHAMFER);
            while (all.MoveNext())
            {
                if (all.Current is not EdgeChamfer treatment) continue;
                var ownershipId = string.Empty;
                if (!treatment.GetUserProperty(StructuraServiceUdas.ExternalObjectId, ref ownershipId) ||
                    !string.Equals(ownershipId, treatmentOwnershipId, StringComparison.Ordinal)) continue;
                Require(treatment.Delete(), "executor stale EdgeChamfer.Delete");
                deleted++;
            }

            var plates = model.GetModelObjectSelector().GetAllObjectsWithType(ModelObject.ModelObjectEnum.CONTOURPLATE);
            while (plates.MoveNext())
            {
                if (plates.Current is not ContourPlate plate) continue;
                var ownershipId = string.Empty;
                if (!plate.GetUserProperty(StructuraServiceUdas.ExternalObjectId, ref ownershipId) ||
                    !string.Equals(ownershipId, baseOwnershipId, StringComparison.Ordinal)) continue;
                Require(plate.Delete(), "executor stale ContourPlate.Delete");
                deleted++;
            }

            if (deleted == 0) return;
            Require(model.CommitChanges(), "executor stale cleanup commit");
            log($"EXECUTOR cleanup staleOwnedObjects={deleted}");
        }

        private static ContourPlate CreatePlate()
        {
            var plate = new ContourPlate
            {
                Name = "STRUCTURA EDGE CHAMFER SPIKE",
                Class = "99",
            };
            plate.Profile.ProfileString = "PL20";
            plate.Material.MaterialString = "S355";
            plate.Position.Depth = Position.DepthEnum.MIDDLE;
            var contour = new Contour();
            foreach (var point in new[] { P(0, 0, 0), P(1600, 0, 0), P(1600, 900, 0), P(0, 900, 0) })
            {
                contour.AddContourPoint(new ContourPoint(point, new Chamfer { Type = Chamfer.ChamferTypeEnum.CHAMFER_NONE }));
            }
            plate.Contour = contour;
            return plate;
        }

        private static EdgeChamfer CreateTreatment(
            ContourPlate plate,
            Point first,
            Point second,
            Chamfer.ChamferTypeEnum type,
            double sizeX,
            double sizeY)
            => new(first, second)
            {
                Father = plate,
                FirstChamferEndType = EdgeChamfer.ChamferEndTypeEnum.FULL,
                SecondChamferEndType = EdgeChamfer.ChamferEndTypeEnum.FULL,
                Chamfer = new Chamfer { Type = type, X = sizeX, Y = sizeY },
                Name = "STRUCTURA EDGE CHAMFER SPIKE",
            };

        private static EdgeChamfer ReadTreatment(Model model, string guid)
        {
            var treatment = model.SelectModelObject(model.GetIdentifierByGUID(guid)) as EdgeChamfer;
            Require(treatment is not null && treatment.Select(), "cannot select EdgeChamfer for readback");
            return treatment!;
        }

        private static void VerifyTreatment(
            Model model,
            EdgeChamfer treatment,
            string expectedFatherGuid,
            Point expectedFirst,
            Point expectedSecond,
            Chamfer.ChamferTypeEnum expectedType,
            double expectedX,
            double expectedY)
        {
            var father = treatment.Father ?? throw new InvalidOperationException("EdgeChamfer father is missing");
            Require(string.Equals(model.GetGUIDByIdentifier(father.Identifier), expectedFatherGuid, StringComparison.OrdinalIgnoreCase), "EdgeChamfer father GUID mismatch");
            Require(Distance(treatment.FirstEnd, expectedFirst) <= Tolerance, "EdgeChamfer first endpoint mismatch");
            Require(Distance(treatment.SecondEnd, expectedSecond) <= Tolerance, "EdgeChamfer second endpoint mismatch");
            Require(treatment.FirstChamferEndType == EdgeChamfer.ChamferEndTypeEnum.FULL, "first end type mismatch");
            Require(treatment.SecondChamferEndType == EdgeChamfer.ChamferEndTypeEnum.FULL, "second end type mismatch");
            var chamfer = treatment.Chamfer ?? throw new InvalidOperationException("EdgeChamfer chamfer values are missing");
            Require(chamfer.Type == expectedType, "chamfer type mismatch");
            Require(Math.Abs(chamfer.X - expectedX) <= Tolerance, "chamfer X mismatch");
            Require(Math.Abs(chamfer.Y - expectedY) <= Tolerance, "chamfer Y mismatch");
        }

        private static double Distance(Point first, Point second)
        {
            var dx = first.X - second.X;
            var dy = first.Y - second.Y;
            var dz = first.Z - second.Z;
            return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        private static Point P(double x, double y, double z) => new(CoordinateOffset + x, y, z);

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
