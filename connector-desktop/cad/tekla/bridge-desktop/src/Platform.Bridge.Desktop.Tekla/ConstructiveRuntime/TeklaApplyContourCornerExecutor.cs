#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;
using Platform.Contracts.TeklaPlan;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.ConstructiveRuntime
{
    public sealed class TeklaApplyContourCornerExecutor : ITeklaNativeCommandExecutor
    {
        private const string ExecutorId = "tekla-native.apply-contour-corner.v1";

        public TeklaPlanExecutorRegistration Registration { get; } = new(
            ExecutorId,
            "apply-contour-corner",
            new[] { "contourChamfer", "udaStamp" },
            applyReady: true);

        public ITeklaPreparedMutation<TeklaNativeCommandReadback> Prepare(
            Model model,
            TeklaPlanDocument plan,
            TeklaPlanCommand command,
            string operationId)
        {
            var parsed = TeklaPlanPayloads.ParseApplyContourCorner(command);
            if (!parsed.Success || parsed.Value is null)
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_CONTOUR_CORNER_PAYLOAD_INVALID",
                    string.Join(" ", parsed.Diagnostics.Select(static item => item.Message)));
            }

            var target = TeklaPlanNativeTargetResolver.ResolvePartCommand(plan, command, parsed.Value.Target);
            if (!string.Equals(target.Command.Kind, "create-contour-plate", StringComparison.Ordinal))
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_CONTOUR_CORNER_TARGET_KIND_UNSUPPORTED",
                    $"Native contour corner requires create-contour-plate, actual target is '{target.Command.Kind}'.");
            }

            var basePayload = TeklaPlanPayloads.ParseCreateContourPlate(target.Command);
            if (!basePayload.Success || basePayload.Value is null)
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_CONTOUR_CORNER_BASE_PAYLOAD_INVALID",
                    string.Join(" ", basePayload.Diagnostics.Select(static item => item.Message)));
            }
            if (!string.Equals(
                    parsed.Value.TargetTopology.ContourId,
                    basePayload.Value.Contour.Id,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_CONTOUR_CORNER_CONTOUR_MISMATCH",
                    $"Contour '{parsed.Value.TargetTopology.ContourId}' is not the outer contour '{basePayload.Value.Contour.Id}' materialized by the target plate.");
            }

            var canonical = TeklaContourPlateGeometry.BuildCanonicalGlobalContour(basePayload.Value);
            var targetIndex = canonical.VertexIds
                .Select((vertexId, index) => new { vertexId, index })
                .Where(item => string.Equals(
                    item.vertexId,
                    parsed.Value.TargetTopology.VertexId,
                    StringComparison.Ordinal))
                .Select(static item => item.index)
                .ToArray();
            if (targetIndex.Length != 1)
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_CONTOUR_CORNER_VERTEX_UNRESOLVED",
                    $"Vertex '{parsed.Value.TargetTopology.VertexId}' has {targetIndex.Length} exact matches in canonical contour '{basePayload.Value.Contour.Id}'.");
            }

            return new PreparedContourCornerMutation(
                model,
                plan,
                command,
                parsed.Value,
                operationId,
                TeklaPlanOwnershipIdentity.Create(plan, command),
                target.ExternalObjectId,
                canonical,
                targetIndex[0]);
        }

        private sealed class PreparedContourCornerMutation : ITeklaPreparedMutation<TeklaNativeCommandReadback>
        {
            private const double Tolerance = 1e-3;
            private readonly Model _model;
            private readonly TeklaPlanDocument _plan;
            private readonly TeklaPlanCommand _command;
            private readonly TeklaApplyContourCornerPayload _payload;
            private readonly string _operationId;
            private readonly string _featureExternalObjectId;
            private readonly string _targetExternalObjectId;
            private readonly TeklaCanonicalContourGeometry _canonical;
            private readonly int _targetIndex;
            private ContourPlate? _plate;
            private ContourPlateSnapshot? _snapshot;
            private bool _applied;

            public PreparedContourCornerMutation(
                Model model,
                TeklaPlanDocument plan,
                TeklaPlanCommand command,
                TeklaApplyContourCornerPayload payload,
                string operationId,
                string featureExternalObjectId,
                string targetExternalObjectId,
                TeklaCanonicalContourGeometry canonical,
                int targetIndex)
            {
                _model = model;
                _plan = plan;
                _command = command;
                _payload = payload;
                _operationId = operationId;
                _featureExternalObjectId = featureExternalObjectId;
                _targetExternalObjectId = targetExternalObjectId;
                _canonical = canonical;
                _targetIndex = targetIndex;
            }

            public string CommandId => _command.CommandId;

            public void Apply()
            {
                try
                {
                    _plate = TeklaPlanNativeTargetResolver.FindPart(
                        _model,
                        _command,
                        _targetExternalObjectId) as ContourPlate ??
                        throw Failure(
                            _command,
                            "TEKLA_PLAN_CONTOUR_CORNER_TARGET_NATIVE_TYPE_INVALID",
                            "Resolved native target is not a ContourPlate.");
                    if (!_plate.Select())
                    {
                        throw Failure(
                            _command,
                            "TEKLA_PLAN_CONTOUR_CORNER_TARGET_SELECT_FAILED",
                            "ContourPlate.Select() returned false.");
                    }

                    VerifyBaseContour(_plate);
                    _snapshot = ContourPlateSnapshot.Capture(_plate);
                    var points = ReadContourPoints(_plate)
                        .Select(static point => ContourPointSnapshot.Capture(point))
                        .ToArray();
                    points[_targetIndex] = points[_targetIndex].WithChamfer(
                        CornerType(_payload.CornerType),
                        _payload.SizeXmm,
                        _payload.SizeYmm ?? _payload.SizeXmm);
                    _plate.Contour = BuildContour(points);
                    RequireUda(
                        _plate.SetUserProperty(StructuraServiceUdas.LastOperationId, _operationId),
                        StructuraServiceUdas.LastOperationId);
                    if (!_plate.Modify())
                    {
                        throw Failure(
                            _command,
                            "TEKLA_PLAN_CONTOUR_CORNER_MODIFY_FAILED",
                            "ContourPlate.Modify() returned false.");
                    }
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
                if (!_applied || _plate is null)
                {
                    throw Failure(
                        _command,
                        "TEKLA_PLAN_CONTOUR_CORNER_NOT_APPLIED",
                        "Readback was requested before apply completed.");
                }
                if (!_plate.Select())
                {
                    throw Failure(
                        _command,
                        "TEKLA_PLAN_CONTOUR_CORNER_READBACK_SELECT_FAILED",
                        "ContourPlate.Select() returned false during readback.");
                }

                VerifyBaseContour(_plate);
                var points = ReadContourPoints(_plate);
                var chamfer = points[_targetIndex].Chamfer ?? new Chamfer
                {
                    Type = Chamfer.ChamferTypeEnum.CHAMFER_NONE,
                };
                var expectedType = CornerType(_payload.CornerType);
                var expectedY = _payload.SizeYmm ?? _payload.SizeXmm;
                if (chamfer.Type != expectedType ||
                    Math.Abs(chamfer.X - _payload.SizeXmm) > Tolerance ||
                    Math.Abs(chamfer.Y - expectedY) > Tolerance)
                {
                    throw Failure(
                        _command,
                        "TEKLA_PLAN_READBACK_MISMATCH",
                        $"Native contour vertex '{_payload.TargetTopology.VertexId}' treatment does not match the plan.");
                }

                var targetExternalObjectId = ReadStringUda(
                    _plate,
                    StructuraServiceUdas.ExternalObjectId,
                    "target ownership");
                VerifyEqual("target ownership id", targetExternalObjectId, _targetExternalObjectId);
                var lastOperationId = ReadStringUda(
                    _plate,
                    StructuraServiceUdas.LastOperationId,
                    "last operation");
                VerifyEqual("last operation id", lastOperationId, _operationId);
                var componentType = ReadOptionalStringUda(_plate, StructuraServiceUdas.ComponentType);
                var schemaVersion = ReadIntUda(_plate, StructuraServiceUdas.SchemaVersion);
                if (schemaVersion != _plan.SchemaVersion)
                {
                    throw Failure(
                        _command,
                        "TEKLA_PLAN_READBACK_MISMATCH",
                        "Native schema version does not match the plan.");
                }

                var targetGuid = _model.GetGUIDByIdentifier(_plate.Identifier) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(targetGuid) || _plate.Identifier.ID <= 0)
                {
                    throw Failure(
                        _command,
                        "TEKLA_PLAN_READBACK_IDENTITY_MISSING",
                        "Tekla did not return a persistent ContourPlate identity.");
                }

                return new TeklaNativeCommandReadback
                {
                    CommandId = _command.CommandId,
                    CommandKind = _command.Kind,
                    Action = "modified",
                    ExternalObjectId = _featureExternalObjectId,
                    TeklaGuid = targetGuid,
                    TeklaId = _plate.Identifier.ID,
                    TargetExternalObjectId = targetExternalObjectId,
                    TargetTeklaGuid = targetGuid,
                    TargetContourId = _payload.TargetTopology.ContourId,
                    TargetVertexId = _payload.TargetTopology.VertexId,
                    CornerType = _payload.CornerType,
                    CornerSizeXmm = chamfer.X,
                    CornerSizeYmm = chamfer.Y,
                    ComponentType = componentType,
                    SchemaVersion = schemaVersion,
                    LastOperationId = lastOperationId,
                };
            }

            public void Restore()
            {
                if (_plate is null || _snapshot is null)
                {
                    throw Failure(
                        _command,
                        "TEKLA_PLAN_CONTOUR_CORNER_ROLLBACK_STATE_MISSING",
                        "ContourPlate rollback snapshot is missing.");
                }
                _snapshot.Restore(_plate);
                if (!_plate.Modify())
                {
                    throw Failure(
                        _command,
                        "TEKLA_PLAN_CONTOUR_CORNER_ROLLBACK_MODIFY_FAILED",
                        "ContourPlate.Modify() returned false during rollback.");
                }
                _applied = false;
            }

            private void RestorePartialApply()
            {
                try
                {
                    if (_plate is not null && _snapshot is not null)
                    {
                        _snapshot.Restore(_plate);
                        _plate.Modify();
                    }
                }
                finally
                {
                    _applied = false;
                }
            }

            private void VerifyBaseContour(ContourPlate plate)
            {
                var points = ReadContourPoints(plate);
                if (points.Count != _canonical.Points.Count)
                {
                    throw Failure(
                        _command,
                        "TEKLA_PLAN_CONTOUR_CORNER_TARGET_TOPOLOGY_DRIFT",
                        $"Native point count {points.Count} does not match canonical contour point count {_canonical.Points.Count}.");
                }
                for (var index = 0; index < points.Count; index++)
                {
                    if (Distance(points[index], _canonical.Points[index]) > Tolerance)
                    {
                        throw Failure(
                            _command,
                            "TEKLA_PLAN_CONTOUR_CORNER_TARGET_TOPOLOGY_DRIFT",
                            $"Native contour point {index} no longer matches canonical vertex '{_canonical.VertexIds[index]}'.");
                    }
                }
            }

            private void RequireUda(bool result, string name)
            {
                if (!result)
                {
                    throw Failure(
                        _command,
                        "TEKLA_PLAN_UDA_STAMP_FAILED",
                        $"SetUserProperty('{name}') returned false.");
                }
            }

            private void VerifyEqual(string role, string actual, string expected)
            {
                if (!string.Equals(actual?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw Failure(
                        _command,
                        "TEKLA_PLAN_READBACK_MISMATCH",
                        $"Native ContourPlate {role} does not match the plan.");
                }
            }
        }

        private sealed class ContourPlateSnapshot
        {
            private readonly IReadOnlyList<ContourPointSnapshot> _points;
            private readonly string _lastOperationId;

            private ContourPlateSnapshot(ContourPlate plate)
            {
                _points = ReadContourPoints(plate)
                    .Select(static point => ContourPointSnapshot.Capture(point))
                    .ToArray();
                _lastOperationId = ReadOptionalStringUda(plate, StructuraServiceUdas.LastOperationId);
            }

            public static ContourPlateSnapshot Capture(ContourPlate plate) => new(plate);

            public void Restore(ContourPlate plate)
            {
                plate.Contour = BuildContour(_points);
                plate.SetUserProperty(StructuraServiceUdas.LastOperationId, _lastOperationId);
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

            private ContourPointSnapshot(ContourPoint point)
            {
                _point = Clone(point);
                _type = point.Chamfer?.Type ?? Chamfer.ChamferTypeEnum.CHAMFER_NONE;
                _x = point.Chamfer?.X ?? 0;
                _y = point.Chamfer?.Y ?? 0;
                _dz1 = point.Chamfer?.DZ1 ?? 0;
                _dz2 = point.Chamfer?.DZ2 ?? 0;
            }

            private ContourPointSnapshot(
                Point point,
                Chamfer.ChamferTypeEnum type,
                double x,
                double y,
                double dz1,
                double dz2)
            {
                _point = Clone(point);
                _type = type;
                _x = x;
                _y = y;
                _dz1 = dz1;
                _dz2 = dz2;
            }

            public static ContourPointSnapshot Capture(ContourPoint point) => new(point);

            public ContourPointSnapshot WithChamfer(
                Chamfer.ChamferTypeEnum type,
                double x,
                double y)
                => new(_point, type, x, y, _dz1, _dz2);

            public ContourPoint Create()
                => new(
                    Clone(_point),
                    new Chamfer { Type = _type, X = _x, Y = _y, DZ1 = _dz1, DZ2 = _dz2 });
        }

        private static Contour BuildContour(IEnumerable<ContourPointSnapshot> points)
        {
            var contour = new Contour();
            foreach (var point in points) contour.AddContourPoint(point.Create());
            return contour;
        }

        private static IReadOnlyList<ContourPoint> ReadContourPoints(ContourPlate plate)
            => plate.Contour?.ContourPoints?.OfType<ContourPoint>().ToArray() ?? Array.Empty<ContourPoint>();

        private static Chamfer.ChamferTypeEnum CornerType(string cornerType)
            => string.Equals(cornerType, "round", StringComparison.Ordinal)
                ? Chamfer.ChamferTypeEnum.CHAMFER_ROUNDING
                : string.Equals(cornerType, "cove", StringComparison.Ordinal)
                    ? Chamfer.ChamferTypeEnum.CHAMFER_ARC_POINT
                    : Chamfer.ChamferTypeEnum.CHAMFER_LINE;

        private static double Distance(Point left, Point right)
        {
            var dx = left.X - right.X;
            var dy = left.Y - right.Y;
            var dz = left.Z - right.Z;
            return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        private static string ReadStringUda(ModelObject target, string name, string role)
        {
            var value = string.Empty;
            if (!target.GetUserProperty(name, ref value) || string.IsNullOrWhiteSpace(value))
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_READBACK_MISMATCH",
                    $"Native ContourPlate {role} UDA '{name}' is missing.");
            return value;
        }

        private static string ReadOptionalStringUda(ModelObject target, string name)
        {
            var value = string.Empty;
            try { target.GetUserProperty(name, ref value); } catch { }
            return value;
        }

        private static int ReadIntUda(ModelObject target, string name)
        {
            var value = 0;
            if (!target.GetUserProperty(name, ref value))
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_READBACK_MISMATCH",
                    $"Native ContourPlate integer UDA '{name}' is missing.");
            return value;
        }

        private static Point Clone(Point point) => new(point.X, point.Y, point.Z);

        private static TeklaNativeExecutionException Failure(
            TeklaPlanCommand command,
            string code,
            string message)
            => new(code, $"Command '{command.CommandId}': {message}");
    }
}
