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
    public sealed class TeklaApplyEdgeTreatmentExecutor : ITeklaNativeCommandExecutor
    {
        private const string ExecutorId = "tekla-native.apply-edge-treatment.v1";
        private const string ComponentType = "ConstructiveEdgeChamferV1";

        public TeklaPlanExecutorRegistration Registration { get; } = new(
            ExecutorId,
            "apply-edge-treatment",
            new[] { "edgeChamfer", "udaStamp" },
            applyReady: true);

        public ITeklaPreparedMutation<TeklaNativeCommandReadback> Prepare(
            Model model,
            TeklaPlanDocument plan,
            TeklaPlanCommand command,
            string operationId)
        {
            var parsed = TeklaPlanPayloads.ParseApplyEdgeTreatment(command);
            if (!parsed.Success || parsed.Value is null)
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_EDGE_TREATMENT_PAYLOAD_INVALID",
                    string.Join(" ", parsed.Diagnostics.Select(static item => item.Message)));
            }

            var target = TeklaPlanNativeTargetResolver.ResolvePartCommand(plan, command, parsed.Value.Target);
            if (!string.Equals(target.Command.Kind, "create-contour-plate", StringComparison.Ordinal))
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_EDGE_TREATMENT_TARGET_KIND_UNSUPPORTED",
                    $"Native EdgeChamfer requires create-contour-plate, actual target is '{target.Command.Kind}'.");
            }

            var basePayload = TeklaPlanPayloads.ParseCreateContourPlate(target.Command);
            if (!basePayload.Success || basePayload.Value is null)
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_EDGE_TREATMENT_BASE_PAYLOAD_INVALID",
                    string.Join(" ", basePayload.Diagnostics.Select(static item => item.Message)));
            }

            TeklaPhysicalContourEdgeGeometry edge;
            try
            {
                edge = TeklaContourPlateGeometry.BuildPhysicalContourEdge(
                    basePayload.Value,
                    parsed.Value.TargetTopology.ContourId,
                    parsed.Value.TargetTopology.EdgeId,
                    parsed.Value.TargetTopology.Side);
            }
            catch (InvalidOperationException exception)
            {
                throw Failure(command, "TEKLA_PLAN_EDGE_TREATMENT_EDGE_UNRESOLVED", exception.Message);
            }

            var externalObjectId = TeklaPlanOwnershipIdentity.Create(plan, command);
            return new PreparedEdgeTreatmentMutation(
                model,
                plan,
                command,
                parsed.Value,
                operationId,
                externalObjectId,
                target.ExternalObjectId,
                edge,
                FindExisting(model, externalObjectId));
        }

        private static EdgeChamfer? FindExisting(Model model, string externalObjectId)
        {
            var matches = new List<EdgeChamfer>();
            var enumerator = model.GetModelObjectSelector()
                .GetAllObjectsWithType(ModelObject.ModelObjectEnum.EDGE_CHAMFER);
            while (enumerator.MoveNext())
            {
                if (enumerator.Current is not EdgeChamfer treatment) continue;
                var candidate = string.Empty;
                if (!treatment.GetUserProperty(StructuraServiceUdas.ExternalObjectId, ref candidate)) continue;
                if (string.Equals(candidate, externalObjectId, StringComparison.Ordinal)) matches.Add(treatment);
            }

            if (matches.Count > 1)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_NATIVE_IDENTITY_DUPLICATE",
                    $"EdgeChamfer has {matches.Count} instances with ownership id '{externalObjectId}'.");
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

        private sealed class PreparedEdgeTreatmentMutation : ITeklaPreparedMutation<TeklaNativeCommandReadback>
        {
            private const double Tolerance = 1e-3;
            private readonly Model _model;
            private readonly TeklaPlanDocument _plan;
            private readonly TeklaPlanCommand _command;
            private readonly TeklaApplyEdgeTreatmentPayload _payload;
            private readonly string _operationId;
            private readonly string _externalObjectId;
            private readonly string _targetExternalObjectId;
            private readonly TeklaPhysicalContourEdgeGeometry _edge;
            private readonly EdgeChamfer _treatment;
            private readonly EdgeChamferSnapshot? _snapshot;
            private readonly bool _created;
            private bool _inserted;
            private bool _applied;

            public PreparedEdgeTreatmentMutation(
                Model model,
                TeklaPlanDocument plan,
                TeklaPlanCommand command,
                TeklaApplyEdgeTreatmentPayload payload,
                string operationId,
                string externalObjectId,
                string targetExternalObjectId,
                TeklaPhysicalContourEdgeGeometry edge,
                EdgeChamfer? existing)
            {
                _model = model;
                _plan = plan;
                _command = command;
                _payload = payload;
                _operationId = operationId;
                _externalObjectId = externalObjectId;
                _targetExternalObjectId = targetExternalObjectId;
                _edge = edge;
                _created = existing is null;
                _treatment = existing ?? new EdgeChamfer(Clone(edge.Start), Clone(edge.End));
                _snapshot = existing is null ? null : EdgeChamferSnapshot.Capture(existing);
            }

            public string CommandId => _command.CommandId;

            public void Apply()
            {
                try
                {
                    var target = TeklaPlanNativeTargetResolver.FindPart(
                        _model,
                        _command,
                        _targetExternalObjectId) as ContourPlate ??
                        throw Failure("TEKLA_PLAN_EDGE_TREATMENT_TARGET_NATIVE_TYPE_INVALID", "Resolved native target is not a ContourPlate.");
                    Configure(target);
                    if (_created)
                    {
                        if (!_treatment.Insert())
                            throw Failure("TEKLA_PLAN_EDGE_TREATMENT_INSERT_FAILED", "EdgeChamfer.Insert() returned false.");
                        _inserted = true;
                    }

                    StampOwnership();
                    if (!_treatment.Modify())
                        throw Failure("TEKLA_PLAN_EDGE_TREATMENT_MODIFY_FAILED", "EdgeChamfer.Modify() returned false.");
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
                if (!_applied)
                    throw Failure("TEKLA_PLAN_EDGE_TREATMENT_NOT_APPLIED", "Readback was requested before apply completed.");
                if (!_treatment.Select())
                    throw Failure("TEKLA_PLAN_EDGE_TREATMENT_READBACK_SELECT_FAILED", "EdgeChamfer.Select() returned false.");

                var father = _treatment.Father as ContourPlate ??
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native EdgeChamfer has no ContourPlate father.");
                var targetExternalObjectId = ReadStringUda(father, StructuraServiceUdas.ExternalObjectId, "target");
                VerifyEqual("target ownership id", targetExternalObjectId, _targetExternalObjectId);
                VerifyPoint("first endpoint", _treatment.FirstEnd, _edge.Start);
                VerifyPoint("second endpoint", _treatment.SecondEnd, _edge.End);
                if (_treatment.FirstChamferEndType != EdgeChamfer.ChamferEndTypeEnum.FULL ||
                    _treatment.SecondChamferEndType != EdgeChamfer.ChamferEndTypeEnum.FULL)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native EdgeChamfer endpoint modes do not match the plan.");
                var chamfer = _treatment.Chamfer ??
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native EdgeChamfer has no chamfer values.");
                var expectedY = _payload.SecondarySizeMm ?? _payload.SizeMm;
                if (chamfer.Type != Chamfer.ChamferTypeEnum.CHAMFER_LINE ||
                    Math.Abs(chamfer.X - _payload.SizeMm) > Tolerance ||
                    Math.Abs(chamfer.Y - expectedY) > Tolerance)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native EdgeChamfer type or dimensions do not match the plan.");

                var externalObjectId = ReadStringUda(_treatment, StructuraServiceUdas.ExternalObjectId, "edge treatment");
                var componentType = ReadStringUda(_treatment, StructuraServiceUdas.ComponentType, "edge treatment");
                var lastOperationId = ReadStringUda(_treatment, StructuraServiceUdas.LastOperationId, "edge treatment");
                var schemaVersion = ReadIntUda(_treatment, StructuraServiceUdas.SchemaVersion);
                VerifyEqual("ownership id", externalObjectId, _externalObjectId);
                VerifyEqual("component type", componentType, ComponentType);
                VerifyEqual("last operation id", lastOperationId, _operationId);
                if (schemaVersion != _plan.SchemaVersion)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native schema version does not match the plan.");

                var guid = _model.GetGUIDByIdentifier(_treatment.Identifier) ?? string.Empty;
                var targetGuid = _model.GetGUIDByIdentifier(father.Identifier) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(guid) || _treatment.Identifier.ID <= 0)
                    throw Failure("TEKLA_PLAN_READBACK_IDENTITY_MISSING", "Tekla did not return a persistent EdgeChamfer identity.");
                if (string.IsNullOrWhiteSpace(targetGuid) || father.Identifier.ID <= 0)
                    throw Failure("TEKLA_PLAN_READBACK_IDENTITY_MISSING", "Tekla did not return a persistent target ContourPlate identity.");

                return new TeklaNativeCommandReadback
                {
                    CommandId = _command.CommandId,
                    CommandKind = _command.Kind,
                    Action = _created ? "created" : "modified",
                    ExternalObjectId = externalObjectId,
                    TeklaGuid = guid,
                    TeklaId = _treatment.Identifier.ID,
                    TargetExternalObjectId = targetExternalObjectId,
                    TargetTeklaGuid = targetGuid,
                    TargetContourId = _payload.TargetTopology.ContourId,
                    TargetEdgeId = _payload.TargetTopology.EdgeId,
                    TargetEdgeSide = _payload.TargetTopology.Side,
                    EdgeTreatmentType = _payload.TreatmentType,
                    EdgeTreatmentSizeMm = chamfer.X,
                    EdgeTreatmentSecondarySizeMm = chamfer.Y,
                    Start = PointArray(_treatment.FirstEnd),
                    End = PointArray(_treatment.SecondEnd),
                    ComponentType = componentType,
                    SchemaVersion = schemaVersion,
                    LastOperationId = lastOperationId,
                };
            }

            public void Restore()
            {
                if (_created)
                {
                    if (_inserted && !_treatment.Delete())
                        throw Failure("TEKLA_PLAN_EDGE_TREATMENT_ROLLBACK_DELETE_FAILED", "EdgeChamfer.Delete() returned false.");
                    _inserted = false;
                    _applied = false;
                    return;
                }

                if (_snapshot is null)
                    throw Failure("TEKLA_PLAN_EDGE_TREATMENT_ROLLBACK_STATE_MISSING", "Existing EdgeChamfer snapshot is missing.");
                _snapshot.Restore(_treatment);
                if (!_treatment.Modify())
                    throw Failure("TEKLA_PLAN_EDGE_TREATMENT_ROLLBACK_MODIFY_FAILED", "EdgeChamfer.Modify() returned false during rollback.");
                _applied = false;
            }

            private void Configure(ContourPlate target)
            {
                _treatment.Father = target;
                _treatment.FirstEnd = Clone(_edge.Start);
                _treatment.SecondEnd = Clone(_edge.End);
                _treatment.FirstChamferEndType = EdgeChamfer.ChamferEndTypeEnum.FULL;
                _treatment.SecondChamferEndType = EdgeChamfer.ChamferEndTypeEnum.FULL;
                _treatment.Chamfer = new Chamfer
                {
                    Type = Chamfer.ChamferTypeEnum.CHAMFER_LINE,
                    X = _payload.SizeMm,
                    Y = _payload.SecondarySizeMm ?? _payload.SizeMm,
                };
                _treatment.Name = "STRUCTURA EDGE CHAMFER";
            }

            private void StampOwnership()
            {
                RequireUda(_treatment.SetUserProperty(StructuraServiceUdas.ExternalObjectId, _externalObjectId), StructuraServiceUdas.ExternalObjectId);
                RequireUda(_treatment.SetUserProperty(StructuraServiceUdas.ComponentType, ComponentType), StructuraServiceUdas.ComponentType);
                RequireUda(_treatment.SetUserProperty(StructuraServiceUdas.SchemaVersion, _plan.SchemaVersion), StructuraServiceUdas.SchemaVersion);
                RequireUda(_treatment.SetUserProperty(StructuraServiceUdas.LastOperationId, _operationId), StructuraServiceUdas.LastOperationId);
            }

            private void RestorePartialApply()
            {
                try
                {
                    if (_created)
                    {
                        if (_inserted) _treatment.Delete();
                        _inserted = false;
                    }
                    else if (_snapshot is not null)
                    {
                        _snapshot.Restore(_treatment);
                        _treatment.Modify();
                    }
                }
                finally
                {
                    _applied = false;
                }
            }

            private void VerifyPoint(string role, Point actual, Point expected)
            {
                if (Distance(actual, expected) > Tolerance)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native EdgeChamfer {role} does not match the exact physical edge.");
            }

            private void VerifyEqual(string role, string actual, string expected)
            {
                if (!string.Equals(actual?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native EdgeChamfer {role} does not match the plan.");
            }

            private string ReadStringUda(ModelObject target, string name, string role)
            {
                var value = string.Empty;
                if (!target.GetUserProperty(name, ref value))
                    throw Failure("TEKLA_PLAN_READBACK_UDA_MISSING", $"Native {role} UDA '{name}' is missing.");
                return value;
            }

            private int ReadIntUda(ModelObject target, string name)
            {
                var value = 0;
                if (!target.GetUserProperty(name, ref value))
                    throw Failure("TEKLA_PLAN_READBACK_UDA_MISSING", $"Native EdgeChamfer UDA '{name}' is missing.");
                return value;
            }

            private void RequireUda(bool result, string name)
            {
                if (!result)
                    throw Failure("TEKLA_PLAN_UDA_STAMP_FAILED", $"SetUserProperty('{name}') returned false.");
            }

            private TeklaNativeExecutionException Failure(string code, string message)
                => new(code, $"Command '{_command.CommandId}': {message}");
        }

        private sealed class EdgeChamferSnapshot
        {
            private readonly ModelObject _father;
            private readonly Point _firstEnd;
            private readonly Point _secondEnd;
            private readonly EdgeChamfer.ChamferEndTypeEnum _firstEndType;
            private readonly EdgeChamfer.ChamferEndTypeEnum _secondEndType;
            private readonly Chamfer _chamfer;
            private readonly string _name;
            private readonly string _externalObjectId;
            private readonly string _componentType;
            private readonly int _schemaVersion;
            private readonly string _lastOperationId;

            private EdgeChamferSnapshot(EdgeChamfer treatment)
            {
                treatment.Select();
                _father = treatment.Father ?? throw new TeklaNativeExecutionException("TEKLA_PLAN_EDGE_TREATMENT_EXISTING_FATHER_MISSING", "Existing EdgeChamfer has no father.");
                _firstEnd = Clone(treatment.FirstEnd);
                _secondEnd = Clone(treatment.SecondEnd);
                _firstEndType = treatment.FirstChamferEndType;
                _secondEndType = treatment.SecondChamferEndType;
                _chamfer = Clone(treatment.Chamfer);
                _name = treatment.Name;
                _externalObjectId = ReadString(treatment, StructuraServiceUdas.ExternalObjectId);
                _componentType = ReadString(treatment, StructuraServiceUdas.ComponentType);
                _schemaVersion = ReadInt(treatment, StructuraServiceUdas.SchemaVersion);
                _lastOperationId = ReadString(treatment, StructuraServiceUdas.LastOperationId);
            }

            public static EdgeChamferSnapshot Capture(EdgeChamfer treatment) => new(treatment);

            public void Restore(EdgeChamfer treatment)
            {
                treatment.Father = _father;
                treatment.FirstEnd = Clone(_firstEnd);
                treatment.SecondEnd = Clone(_secondEnd);
                treatment.FirstChamferEndType = _firstEndType;
                treatment.SecondChamferEndType = _secondEndType;
                treatment.Chamfer = Clone(_chamfer);
                treatment.Name = _name;
                treatment.SetUserProperty(StructuraServiceUdas.ExternalObjectId, _externalObjectId);
                treatment.SetUserProperty(StructuraServiceUdas.ComponentType, _componentType);
                treatment.SetUserProperty(StructuraServiceUdas.SchemaVersion, _schemaVersion);
                treatment.SetUserProperty(StructuraServiceUdas.LastOperationId, _lastOperationId);
            }

            private static string ReadString(ModelObject target, string name)
            {
                var value = string.Empty;
                target.GetUserProperty(name, ref value);
                return value;
            }

            private static int ReadInt(ModelObject target, string name)
            {
                var value = 0;
                target.GetUserProperty(name, ref value);
                return value;
            }
        }

        private static Chamfer Clone(Chamfer? source)
            => new()
            {
                Type = source?.Type ?? Chamfer.ChamferTypeEnum.CHAMFER_NONE,
                X = source?.X ?? 0,
                Y = source?.Y ?? 0,
                DZ1 = source?.DZ1 ?? 0,
                DZ2 = source?.DZ2 ?? 0,
            };

        private static Point Clone(Point source) => new(source.X, source.Y, source.Z);
        private static double[] PointArray(Point source) => new[] { source.X, source.Y, source.Z };
        private static double Distance(Point first, Point second)
        {
            var dx = first.X - second.X;
            var dy = first.Y - second.Y;
            var dz = first.Z - second.Z;
            return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        private static TeklaNativeExecutionException Failure(TeklaPlanCommand command, string code, string message)
            => new(code, $"Command '{command.CommandId}': {message}");
    }
}
