#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;
using Platform.Contracts.TeklaPlan;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.ConstructiveRuntime
{
    /// <summary>
    /// Applies a deterministic planar cutter to an exact constructive Part.
    /// Tekla copies the operative shape into BooleanPart during Insert(), so an
    /// update is a transactional replacement with a complete rollback snapshot.
    /// </summary>
    public sealed class TeklaApplyBooleanCutExecutor : ITeklaNativeCommandExecutor
    {
        private const string ExecutorId = "tekla-native.apply-boolean-cut.v1";
        private const string ComponentType = "ConstructiveBooleanCutV1";

        public TeklaPlanExecutorRegistration Registration { get; } = new(
            ExecutorId,
            "apply-boolean-cut",
            new[] { "booleanPart", "udaStamp" },
            applyReady: true);

        public ITeklaPreparedMutation<TeklaNativeCommandReadback> Prepare(
            Model model,
            TeklaPlanDocument plan,
            TeklaPlanCommand command,
            string operationId)
        {
            var parsed = TeklaPlanPayloads.ParseApplyBooleanCut(command);
            if (!parsed.Success || parsed.Value is null)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_BOOLEAN_CUT_PAYLOAD_INVALID",
                    string.Join(" ", parsed.Diagnostics.Select(static item => item.Message)));
            }
            if (parsed.Value.Cutter.Contour.Edges.Any(static edge => edge.Kind != "line"))
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_BOOLEAN_CUT_ARCS_UNSUPPORTED",
                    "Native BooleanPart contour cutter does not support arc edges.");
            }

            var target = TeklaPlanNativeTargetResolver.ResolvePartCommand(plan, command, parsed.Value.Target);
            var externalObjectId = TeklaPlanOwnershipIdentity.Create(plan, command);
            return new PreparedBooleanCutMutation(
                model,
                plan,
                command,
                parsed.Value,
                operationId,
                externalObjectId,
                target.ExternalObjectId,
                FindExisting(model, externalObjectId));
        }

        private static BooleanPart? FindExisting(Model model, string externalObjectId)
        {
            var matches = new List<BooleanPart>();
            var enumerator = model.GetModelObjectSelector().GetAllObjects();
            while (enumerator.MoveNext())
            {
                if (enumerator.Current is not BooleanPart booleanPart) continue;
                var candidate = string.Empty;
                if (!booleanPart.GetUserProperty(StructuraServiceUdas.ExternalObjectId, ref candidate)) continue;
                if (string.Equals(candidate, externalObjectId, StringComparison.Ordinal)) matches.Add(booleanPart);
            }
            if (matches.Count > 1)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_NATIVE_IDENTITY_DUPLICATE",
                    $"BooleanPart has {matches.Count} instances with ownership id '{externalObjectId}'.");
            }

            var existing = matches.SingleOrDefault();
            if (existing is null) return null;
            var componentType = string.Empty;
            existing.GetUserProperty(StructuraServiceUdas.ComponentType, ref componentType);
            if (!string.Equals(componentType, ComponentType, StringComparison.Ordinal))
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_NATIVE_OWNERSHIP_CONFLICT",
                    $"Ownership id '{externalObjectId}' belongs to component type '{componentType}', not '{ComponentType}'.");
            }
            return existing;
        }

        private sealed class PreparedBooleanCutMutation : ITeklaPreparedMutation<TeklaNativeCommandReadback>
        {
            private const double Tolerance = 1e-3;
            private readonly Model _model;
            private readonly TeklaPlanDocument _plan;
            private readonly TeklaPlanCommand _command;
            private readonly TeklaApplyBooleanCutPayload _payload;
            private readonly string _operationId;
            private readonly string _externalObjectId;
            private readonly string _targetExternalObjectId;
            private readonly BooleanPart? _existing;
            private readonly BooleanSnapshot? _snapshot;
            private readonly IReadOnlyList<Point> _expectedPoints;
            private BooleanPart? _appliedBoolean;
            private bool _existingDeleted;
            private bool _applied;

            public PreparedBooleanCutMutation(
                Model model,
                TeklaPlanDocument plan,
                TeklaPlanCommand command,
                TeklaApplyBooleanCutPayload payload,
                string operationId,
                string externalObjectId,
                string targetExternalObjectId,
                BooleanPart? existing)
            {
                _model = model;
                _plan = plan;
                _command = command;
                _payload = payload;
                _operationId = operationId;
                _externalObjectId = externalObjectId;
                _targetExternalObjectId = targetExternalObjectId;
                _existing = existing;
                _snapshot = existing is null ? null : BooleanSnapshot.Capture(existing);
                _expectedPoints = BuildCanonicalGlobalContour(payload.Cutter);
            }

            public string CommandId => _command.CommandId;

            public void Apply()
            {
                try
                {
                    var target = TeklaPlanNativeTargetResolver.FindPart(_model, _command, _targetExternalObjectId);
                    if (_existing is not null)
                    {
                        if (!_existing.Delete())
                            throw Failure("TEKLA_PLAN_BOOLEAN_CUT_REPLACE_DELETE_FAILED", "Existing BooleanPart.Delete() returned false.");
                        _existingDeleted = true;
                    }

                    _appliedBoolean = CreateBoolean(target, CutterSnapshot.FromPayload(_payload.Cutter, target));
                    StampOwnership(_appliedBoolean, _externalObjectId, ComponentType, _plan.SchemaVersion, _operationId);
                    if (!_appliedBoolean.Modify())
                        throw Failure("TEKLA_PLAN_BOOLEAN_CUT_MODIFY_FAILED", "BooleanPart.Modify() returned false after ownership stamping.");
                    _applied = true;
                }
                catch
                {
                    RestorePartialApply();
                    throw;
                }
            }

            public TeklaNativeCommandReadback Readback()
            {
                if (!_applied || _appliedBoolean is null)
                    throw Failure("TEKLA_PLAN_BOOLEAN_CUT_NOT_APPLIED", "Readback was requested before apply completed.");

                _appliedBoolean.Select();
                if (_appliedBoolean.Type != BooleanPart.BooleanTypeEnum.BOOLEAN_CUT)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native BooleanPart type is not BOOLEAN_CUT.");
                var father = _appliedBoolean.Father as Part ??
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native BooleanPart has no Part father.");
                var targetExternalObjectId = ReadStringUda(father, StructuraServiceUdas.ExternalObjectId, "target");
                VerifyEqual("target ownership id", targetExternalObjectId, _targetExternalObjectId);

                var operative = _appliedBoolean.OperativePart as ContourPlate ??
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native BooleanPart operative shape is not a ContourPlate.");
                operative.Select();
                var actualPoints = ReadContourPoints(operative);
                if (actualPoints.Count != _expectedPoints.Count)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native BooleanPart cutter point count does not match the plan.");
                for (var index = 0; index < actualPoints.Count; index++)
                    VerifyPoint($"cutter[{index}]", actualPoints[index], _expectedPoints[index]);

                var externalObjectId = ReadStringUda(_appliedBoolean, StructuraServiceUdas.ExternalObjectId, "boolean cut");
                var componentType = ReadStringUda(_appliedBoolean, StructuraServiceUdas.ComponentType, "boolean cut");
                var lastOperationId = ReadStringUda(_appliedBoolean, StructuraServiceUdas.LastOperationId, "boolean cut");
                var schemaVersion = ReadIntUda(_appliedBoolean, StructuraServiceUdas.SchemaVersion);
                VerifyEqual("ownership id", externalObjectId, _externalObjectId);
                VerifyEqual("component type", componentType, ComponentType);
                VerifyEqual("last operation id", lastOperationId, _operationId);
                if (schemaVersion != _plan.SchemaVersion)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native schema version does not match the plan.");

                var guid = _model.GetGUIDByIdentifier(_appliedBoolean.Identifier) ?? string.Empty;
                var targetGuid = _model.GetGUIDByIdentifier(father.Identifier) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(guid) || _appliedBoolean.Identifier.ID <= 0)
                    throw Failure("TEKLA_PLAN_READBACK_IDENTITY_MISSING", "Tekla did not return a persistent BooleanPart identity.");
                if (string.IsNullOrWhiteSpace(targetGuid) || father.Identifier.ID <= 0)
                    throw Failure("TEKLA_PLAN_READBACK_IDENTITY_MISSING", "Tekla did not return a persistent target Part identity.");

                return new TeklaNativeCommandReadback
                {
                    CommandId = _command.CommandId,
                    CommandKind = _command.Kind,
                    Action = _snapshot is null ? "created" : "replaced",
                    ExternalObjectId = externalObjectId,
                    TeklaGuid = guid,
                    TeklaId = _appliedBoolean.Identifier.ID,
                    TargetExternalObjectId = targetExternalObjectId,
                    TargetTeklaGuid = targetGuid,
                    Contour = actualPoints.Select(static point => PointArray(point)).ToArray(),
                    ThicknessMm = _payload.Cutter.ThicknessMm,
                    ExtrusionSide = _payload.Cutter.ExtrusionSide,
                    CutterKind = _payload.Cutter.Kind,
                    ComponentType = componentType,
                    SchemaVersion = schemaVersion,
                    LastOperationId = lastOperationId,
                };
            }

            public void Restore()
            {
                DeleteApplied();
                RestoreExisting();
                _applied = false;
            }

            private void RestorePartialApply()
            {
                try
                {
                    DeleteApplied();
                    RestoreExisting();
                }
                finally
                {
                    _applied = false;
                }
            }

            private void DeleteApplied()
            {
                if (_appliedBoolean is null) return;
                if (!_appliedBoolean.Delete())
                    throw Failure("TEKLA_PLAN_BOOLEAN_CUT_ROLLBACK_DELETE_FAILED", "BooleanPart.Delete() returned false during rollback.");
                _appliedBoolean = null;
            }

            private void RestoreExisting()
            {
                if (!_existingDeleted) return;
                if (_snapshot is null)
                    throw Failure("TEKLA_PLAN_BOOLEAN_CUT_ROLLBACK_STATE_MISSING", "Existing BooleanPart snapshot is missing.");
                _snapshot.Restore();
                _existingDeleted = false;
            }

            private void VerifyPoint(string role, ContourPoint actual, Point expected)
            {
                if (Math.Abs(actual.X - expected.X) > Tolerance ||
                    Math.Abs(actual.Y - expected.Y) > Tolerance ||
                    Math.Abs(actual.Z - expected.Z) > Tolerance)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native BooleanPart {role} point does not match the plan.");
            }

            private void VerifyEqual(string role, string actual, string expected)
            {
                if (!string.Equals(actual?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native BooleanPart {role} does not match the plan.");
            }

            private TeklaNativeExecutionException Failure(string code, string message)
                => new(code, $"Command '{_command.CommandId}': {message}");
        }

        private sealed class BooleanSnapshot
        {
            private readonly Part _father;
            private readonly BooleanPart.BooleanTypeEnum _type;
            private readonly CutterSnapshot _cutter;
            private readonly string _externalObjectId;
            private readonly string _componentType;
            private readonly int _schemaVersion;
            private readonly string _lastOperationId;

            private BooleanSnapshot(BooleanPart booleanPart)
            {
                booleanPart.Select();
                _father = booleanPart.Father as Part ??
                    throw new TeklaNativeExecutionException(
                        "TEKLA_PLAN_BOOLEAN_CUT_EXISTING_FATHER_MISSING",
                        "Existing Constructive BooleanPart has no Part father.");
                _type = booleanPart.Type;
                _cutter = CutterSnapshot.Capture(booleanPart.OperativePart as ContourPlate ??
                    throw new TeklaNativeExecutionException(
                        "TEKLA_PLAN_BOOLEAN_CUT_EXISTING_CUTTER_UNSUPPORTED",
                        "Existing Constructive BooleanPart operative shape is not a ContourPlate."));
                _externalObjectId = ReadString(booleanPart, StructuraServiceUdas.ExternalObjectId);
                _componentType = ReadString(booleanPart, StructuraServiceUdas.ComponentType);
                _schemaVersion = ReadInt(booleanPart, StructuraServiceUdas.SchemaVersion);
                _lastOperationId = ReadString(booleanPart, StructuraServiceUdas.LastOperationId);
            }

            public static BooleanSnapshot Capture(BooleanPart booleanPart) => new(booleanPart);

            public void Restore()
            {
                var restored = CreateBoolean(_father, _cutter, _type);
                StampOwnership(restored, _externalObjectId, _componentType, _schemaVersion, _lastOperationId);
                if (!restored.Modify())
                    throw new TeklaNativeExecutionException(
                        "TEKLA_PLAN_BOOLEAN_CUT_ROLLBACK_MODIFY_FAILED",
                        "Restored BooleanPart.Modify() returned false during rollback.");
            }
        }

        private sealed class CutterSnapshot
        {
            private readonly IReadOnlyList<ContourPointSnapshot> _points;
            private readonly string _name;
            private readonly string _finish;
            private readonly string _className;
            private readonly string _profile;
            private readonly string _material;
            private readonly Position.DepthEnum _depth;
            private readonly double _depthOffset;

            private CutterSnapshot(
                IReadOnlyList<ContourPointSnapshot> points,
                string name,
                string finish,
                string className,
                string profile,
                string material,
                Position.DepthEnum depth,
                double depthOffset)
            {
                _points = points;
                _name = name;
                _finish = finish;
                _className = className;
                _profile = profile;
                _material = material;
                _depth = depth;
                _depthOffset = depthOffset;
            }

            public static CutterSnapshot FromPayload(TeklaBooleanCutGeometryPayload cutter, Part father)
                => new(
                    BuildCanonicalGlobalContour(cutter)
                        .Select(static point => ContourPointSnapshot.None(point))
                        .ToArray(),
                    "Constructive Boolean cutter",
                    string.Empty,
                    BooleanPart.BooleanOperativeClassName,
                    "PL" + Number(cutter.ThicknessMm),
                    ResolveMaterial(father),
                    Position.DepthEnum.MIDDLE,
                    0);

            public static CutterSnapshot Capture(ContourPlate cutter)
            {
                cutter.Select();
                return new CutterSnapshot(
                    ReadContourPoints(cutter).Select(static point => ContourPointSnapshot.Capture(point)).ToArray(),
                    cutter.Name,
                    cutter.Finish,
                    cutter.Class,
                    cutter.Profile.ProfileString,
                    cutter.Material.MaterialString,
                    cutter.Position.Depth,
                    cutter.Position.DepthOffset);
            }

            public ContourPlate Create()
            {
                var contour = new Contour();
                foreach (var point in _points) contour.AddContourPoint(point.Create());
                return new ContourPlate
                {
                    Name = _name,
                    Finish = _finish,
                    Class = BooleanPart.BooleanOperativeClassName,
                    Contour = contour,
                    Position = { Depth = _depth, DepthOffset = _depthOffset },
                    Profile = { ProfileString = _profile },
                    Material = { MaterialString = _material },
                };
            }
        }

        private sealed class ContourPointSnapshot
        {
            private readonly Point _point;
            private readonly Chamfer.ChamferTypeEnum _type;
            private readonly double _x;
            private readonly double _y;
            private readonly double _dz1;
            private readonly double _dz2;

            private ContourPointSnapshot(Point point, Chamfer chamfer)
            {
                _point = Clone(point);
                _type = chamfer.Type;
                _x = chamfer.X;
                _y = chamfer.Y;
                _dz1 = chamfer.DZ1;
                _dz2 = chamfer.DZ2;
            }

            public static ContourPointSnapshot None(Point point)
                => new(point, new Chamfer { Type = Chamfer.ChamferTypeEnum.CHAMFER_NONE });

            public static ContourPointSnapshot Capture(ContourPoint point)
                => new(point, point.Chamfer ?? new Chamfer { Type = Chamfer.ChamferTypeEnum.CHAMFER_NONE });

            public ContourPoint Create()
                => new(Clone(_point), new Chamfer { Type = _type, X = _x, Y = _y, DZ1 = _dz1, DZ2 = _dz2 });
        }

        private static BooleanPart CreateBoolean(
            Part father,
            CutterSnapshot cutterSnapshot,
            BooleanPart.BooleanTypeEnum type = BooleanPart.BooleanTypeEnum.BOOLEAN_CUT)
        {
            var cutter = cutterSnapshot.Create();
            if (!cutter.Insert())
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_BOOLEAN_CUTTER_INSERT_FAILED",
                    "Boolean cutter ContourPlate.Insert() returned false.");
            try
            {
                var booleanPart = new BooleanPart { Father = father, Type = type };
                if (!booleanPart.SetOperativePart(cutter))
                    throw new TeklaNativeExecutionException(
                        "TEKLA_PLAN_BOOLEAN_CUTTER_ASSIGN_FAILED",
                        "BooleanPart.SetOperativePart() returned false.");
                if (!booleanPart.Insert())
                    throw new TeklaNativeExecutionException(
                        "TEKLA_PLAN_BOOLEAN_CUT_INSERT_FAILED",
                        "BooleanPart.Insert() returned false.");
                return booleanPart;
            }
            finally
            {
                try { cutter.Delete(); } catch { }
            }
        }

        private static void StampOwnership(
            BooleanPart booleanPart,
            string externalObjectId,
            string componentType,
            int schemaVersion,
            string operationId)
        {
            RequireUda(booleanPart.SetUserProperty(StructuraServiceUdas.ExternalObjectId, externalObjectId), StructuraServiceUdas.ExternalObjectId);
            RequireUda(booleanPart.SetUserProperty(StructuraServiceUdas.ComponentType, componentType), StructuraServiceUdas.ComponentType);
            RequireUda(booleanPart.SetUserProperty(StructuraServiceUdas.SchemaVersion, schemaVersion), StructuraServiceUdas.SchemaVersion);
            RequireUda(booleanPart.SetUserProperty(StructuraServiceUdas.LastOperationId, operationId), StructuraServiceUdas.LastOperationId);
        }

        private static IReadOnlyList<Point> BuildCanonicalGlobalContour(TeklaBooleanCutGeometryPayload cutter)
        {
            var vertexById = cutter.Contour.Vertices.ToDictionary(static vertex => vertex.Id, StringComparer.Ordinal);
            var ordered = cutter.Contour.Edges.Select(edge => vertexById[edge.StartVertexId].Point).ToList();
            var signedArea2 = 0d;
            for (var index = 0; index < ordered.Count; index++)
            {
                var next = ordered[(index + 1) % ordered.Count];
                signedArea2 += (ordered[index].X * next.Y) - (next.X * ordered[index].Y);
            }
            if (signedArea2 < 0) ordered.Reverse();

            var normalOffset = cutter.ExtrusionSide switch
            {
                "positive" => cutter.ThicknessMm / 2,
                "negative" => -cutter.ThicknessMm / 2,
                _ => 0,
            };
            return ordered.Select(point => new Point(
                cutter.Plane.Origin.X + (cutter.Plane.AxisX.X * point.X) + (cutter.Plane.AxisY.X * point.Y) + (cutter.Plane.AxisZ.X * normalOffset),
                cutter.Plane.Origin.Y + (cutter.Plane.AxisX.Y * point.X) + (cutter.Plane.AxisY.Y * point.Y) + (cutter.Plane.AxisZ.Y * normalOffset),
                cutter.Plane.Origin.Z + (cutter.Plane.AxisX.Z * point.X) + (cutter.Plane.AxisY.Z * point.Y) + (cutter.Plane.AxisZ.Z * normalOffset)))
                .ToArray();
        }

        private static IReadOnlyList<ContourPoint> ReadContourPoints(ContourPlate plate)
            => plate.Contour?.ContourPoints?.OfType<ContourPoint>().ToArray() ?? Array.Empty<ContourPoint>();

        private static string ResolveMaterial(Part father)
        {
            var material = father.Material?.MaterialString;
            return !string.IsNullOrWhiteSpace(material) ? material! : "S235JR";
        }

        private static string ReadString(ModelObject target, string name)
        {
            var value = string.Empty;
            try { target.GetUserProperty(name, ref value); } catch { }
            return value;
        }

        private static int ReadInt(ModelObject target, string name)
        {
            var value = 0;
            try { target.GetUserProperty(name, ref value); } catch { }
            return value;
        }

        private static string ReadStringUda(ModelObject target, string name, string role)
        {
            var value = string.Empty;
            if (!target.GetUserProperty(name, ref value))
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_READBACK_UDA_MISSING",
                    $"Native {role} UDA '{name}' is missing.");
            return value;
        }

        private static int ReadIntUda(ModelObject target, string name)
        {
            var value = 0;
            if (!target.GetUserProperty(name, ref value))
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_READBACK_UDA_MISSING",
                    $"Native BooleanPart UDA '{name}' is missing.");
            return value;
        }

        private static void RequireUda(bool result, string name)
        {
            if (!result)
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_UDA_STAMP_FAILED",
                    $"SetUserProperty('{name}') returned false.");
        }

        private static string Number(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);
        private static Point Clone(Point point) => new(point.X, point.Y, point.Z);
        private static double[] PointArray(Point point) => new[] { point.X, point.Y, point.Z };
    }
}
