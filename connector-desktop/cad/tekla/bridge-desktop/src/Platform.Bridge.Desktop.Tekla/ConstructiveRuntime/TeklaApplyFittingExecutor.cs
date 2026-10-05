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
    public sealed class TeklaApplyFittingExecutor : ITeklaNativeCommandExecutor
    {
        private const string ExecutorId = "tekla-native.apply-fitting.v1";
        private const string ComponentType = "ConstructiveFittingV1";

        public TeklaPlanExecutorRegistration Registration { get; } = new(
            ExecutorId,
            "apply-fitting",
            new[] { "fitting", "udaStamp" },
            applyReady: true);

        public ITeklaPreparedMutation<TeklaNativeCommandReadback> Prepare(
            Model model,
            TeklaPlanDocument plan,
            TeklaPlanCommand command,
            string operationId)
        {
            var parsed = TeklaPlanPayloads.ParseApplyFitting(command);
            if (!parsed.Success || parsed.Value is null)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_FITTING_PAYLOAD_INVALID",
                    string.Join(" ", parsed.Diagnostics.Select(static item => item.Message)));
            }

            var target = TeklaPlanNativeTargetResolver.ResolvePartCommand(plan, command, parsed.Value.Target);
            var externalObjectId = TeklaPlanOwnershipIdentity.Create(plan, command);
            var existing = FindExisting(model, externalObjectId);
            return new PreparedFittingMutation(
                model,
                plan,
                command,
                parsed.Value,
                operationId,
                externalObjectId,
                target.ExternalObjectId,
                existing);
        }

        private static Fitting? FindExisting(Model model, string externalObjectId)
        {
            var matches = new List<Fitting>();
            var enumerator = model.GetModelObjectSelector().GetAllObjects();
            while (enumerator.MoveNext())
            {
                if (enumerator.Current is not Fitting fitting) continue;
                var candidate = string.Empty;
                if (!fitting.GetUserProperty(StructuraServiceUdas.ExternalObjectId, ref candidate)) continue;
                if (string.Equals(candidate, externalObjectId, StringComparison.Ordinal)) matches.Add(fitting);
            }

            if (matches.Count > 1)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_NATIVE_IDENTITY_DUPLICATE",
                    $"Fitting has {matches.Count} instances with ownership id '{externalObjectId}'.");
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

        private sealed class PreparedFittingMutation : ITeklaPreparedMutation<TeklaNativeCommandReadback>
        {
            private const double Tolerance = 1e-3;
            private readonly Model _model;
            private readonly TeklaPlanDocument _plan;
            private readonly TeklaPlanCommand _command;
            private readonly TeklaApplyFittingPayload _payload;
            private readonly string _operationId;
            private readonly string _externalObjectId;
            private readonly string _targetExternalObjectId;
            private readonly Fitting _fitting;
            private readonly FittingSnapshot? _snapshot;
            private readonly bool _created;
            private bool _inserted;
            private bool _applied;

            public PreparedFittingMutation(
                Model model,
                TeklaPlanDocument plan,
                TeklaPlanCommand command,
                TeklaApplyFittingPayload payload,
                string operationId,
                string externalObjectId,
                string targetExternalObjectId,
                Fitting? existing)
            {
                _model = model;
                _plan = plan;
                _command = command;
                _payload = payload;
                _operationId = operationId;
                _externalObjectId = externalObjectId;
                _targetExternalObjectId = targetExternalObjectId;
                _created = existing is null;
                _fitting = existing ?? new Fitting();
                _snapshot = existing is null ? null : FittingSnapshot.Capture(existing);
            }

            public string CommandId => _command.CommandId;

            public void Apply()
            {
                try
                {
                    var target = TeklaPlanNativeTargetResolver.FindPart(_model, _command, _targetExternalObjectId);
                    _fitting.Father = target;
                    _fitting.Plane = CreatePlane(_payload);
                    if (_created)
                    {
                        if (!_fitting.Insert())
                            throw Failure("TEKLA_PLAN_FITTING_INSERT_FAILED", "Fitting.Insert() returned false.");
                        _inserted = true;
                    }

                    StampOwnership();
                    if (!_fitting.Modify())
                        throw Failure("TEKLA_PLAN_FITTING_MODIFY_FAILED", "Fitting.Modify() returned false.");
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
                    throw Failure("TEKLA_PLAN_FITTING_NOT_APPLIED", "Readback was requested before apply completed.");

                var father = _fitting.Father as Part ??
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native Fitting has no Part father.");
                var targetExternalObjectId = ReadStringUda(father, StructuraServiceUdas.ExternalObjectId, "target");
                VerifyEqual("target ownership id", targetExternalObjectId, _targetExternalObjectId);
                VerifyPlane(_fitting.Plane);

                var externalObjectId = ReadStringUda(_fitting, StructuraServiceUdas.ExternalObjectId, "fitting");
                var componentType = ReadStringUda(_fitting, StructuraServiceUdas.ComponentType, "fitting");
                var operationId = ReadStringUda(_fitting, StructuraServiceUdas.LastOperationId, "fitting");
                var schemaVersion = ReadIntUda(_fitting, StructuraServiceUdas.SchemaVersion);
                VerifyEqual("ownership id", externalObjectId, _externalObjectId);
                VerifyEqual("component type", componentType, ComponentType);
                VerifyEqual("last operation id", operationId, _operationId);
                if (schemaVersion != _plan.SchemaVersion)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native schema version does not match the plan.");

                var guid = _model.GetGUIDByIdentifier(_fitting.Identifier) ?? string.Empty;
                var targetGuid = _model.GetGUIDByIdentifier(father.Identifier) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(guid) || _fitting.Identifier.ID <= 0)
                    throw Failure("TEKLA_PLAN_READBACK_IDENTITY_MISSING", "Tekla did not return a persistent Fitting identity.");
                if (string.IsNullOrWhiteSpace(targetGuid) || father.Identifier.ID <= 0)
                    throw Failure("TEKLA_PLAN_READBACK_IDENTITY_MISSING", "Tekla did not return a persistent target Part identity.");

                return new TeklaNativeCommandReadback
                {
                    CommandId = _command.CommandId,
                    CommandKind = _command.Kind,
                    Action = _created ? "created" : "modified",
                    ExternalObjectId = _externalObjectId,
                    TeklaGuid = guid,
                    TeklaId = _fitting.Identifier.ID,
                    TargetExternalObjectId = targetExternalObjectId,
                    TargetTeklaGuid = targetGuid,
                    PlaneOrigin = PointArray(_fitting.Plane.Origin),
                    PlaneAxisX = VectorArray(_fitting.Plane.AxisX),
                    PlaneAxisY = VectorArray(_fitting.Plane.AxisY),
                    KeepSide = _payload.KeepSide,
                    ComponentType = componentType,
                    SchemaVersion = schemaVersion,
                    LastOperationId = operationId,
                };
            }

            public void Restore()
            {
                if (_created)
                {
                    if (_inserted && !_fitting.Delete())
                        throw Failure("TEKLA_PLAN_FITTING_ROLLBACK_DELETE_FAILED", "Fitting.Delete() returned false.");
                    _inserted = false;
                    _applied = false;
                    return;
                }

                if (_snapshot is null)
                    throw Failure("TEKLA_PLAN_FITTING_ROLLBACK_STATE_MISSING", "Existing Fitting snapshot is missing.");
                _snapshot.Restore(_fitting);
                if (!_fitting.Modify())
                    throw Failure("TEKLA_PLAN_FITTING_ROLLBACK_MODIFY_FAILED", "Fitting.Modify() returned false during rollback.");
                _applied = false;
            }

            private void StampOwnership()
            {
                RequireUda(_fitting.SetUserProperty(StructuraServiceUdas.ExternalObjectId, _externalObjectId), StructuraServiceUdas.ExternalObjectId);
                RequireUda(_fitting.SetUserProperty(StructuraServiceUdas.ComponentType, ComponentType), StructuraServiceUdas.ComponentType);
                RequireUda(_fitting.SetUserProperty(StructuraServiceUdas.SchemaVersion, _plan.SchemaVersion), StructuraServiceUdas.SchemaVersion);
                RequireUda(_fitting.SetUserProperty(StructuraServiceUdas.LastOperationId, _operationId), StructuraServiceUdas.LastOperationId);
            }

            private void RestorePartialApply()
            {
                try
                {
                    if (_created)
                    {
                        if (_inserted) _fitting.Delete();
                        _inserted = false;
                    }
                    else if (_snapshot is not null)
                    {
                        _snapshot.Restore(_fitting);
                        _fitting.Modify();
                    }
                }
                finally
                {
                    _applied = false;
                }
            }

            private void VerifyPlane(Plane actual)
            {
                VerifyPoint("plane origin", actual.Origin, _payload.Plane.Origin);
                VerifyVector("plane axisX", actual.AxisX, _payload.Plane.AxisX);
                var expectedAxisY = _payload.KeepSide == "positive"
                    ? _payload.Plane.AxisY
                    : new TeklaVector3(-_payload.Plane.AxisY.X, -_payload.Plane.AxisY.Y, -_payload.Plane.AxisY.Z);
                VerifyVector("plane axisY", actual.AxisY, expectedAxisY);
            }

            private void VerifyPoint(string role, Point actual, TeklaVector3 expected)
            {
                if (Math.Abs(actual.X - expected.X) > Tolerance ||
                    Math.Abs(actual.Y - expected.Y) > Tolerance ||
                    Math.Abs(actual.Z - expected.Z) > Tolerance)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native Fitting {role} does not match the plan.");
            }

            private void VerifyVector(string role, Vector actual, TeklaVector3 expected)
            {
                if (Math.Abs(actual.X - expected.X) > Tolerance ||
                    Math.Abs(actual.Y - expected.Y) > Tolerance ||
                    Math.Abs(actual.Z - expected.Z) > Tolerance)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native Fitting {role} does not match the plan.");
            }

            private void VerifyEqual(string role, string actual, string expected)
            {
                if (!string.Equals(actual?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native Fitting {role} does not match the plan.");
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
                    throw Failure("TEKLA_PLAN_READBACK_UDA_MISSING", $"Native Fitting UDA '{name}' is missing.");
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

        private sealed class FittingSnapshot
        {
            private readonly Part _father;
            private readonly Plane _plane;
            private readonly string _externalObjectId;
            private readonly string _componentType;
            private readonly int _schemaVersion;
            private readonly string _lastOperationId;

            private FittingSnapshot(Fitting fitting)
            {
                _father = fitting.Father as Part ??
                    throw new TeklaNativeExecutionException(
                        "TEKLA_PLAN_FITTING_EXISTING_FATHER_MISSING",
                        "Existing Constructive Fitting has no Part father.");
                _plane = Clone(fitting.Plane);
                _externalObjectId = ReadString(fitting, StructuraServiceUdas.ExternalObjectId);
                _componentType = ReadString(fitting, StructuraServiceUdas.ComponentType);
                _schemaVersion = ReadInt(fitting, StructuraServiceUdas.SchemaVersion);
                _lastOperationId = ReadString(fitting, StructuraServiceUdas.LastOperationId);
            }

            public static FittingSnapshot Capture(Fitting fitting) => new(fitting);

            public void Restore(Fitting fitting)
            {
                fitting.Father = _father;
                fitting.Plane = Clone(_plane);
                fitting.SetUserProperty(StructuraServiceUdas.ExternalObjectId, _externalObjectId);
                fitting.SetUserProperty(StructuraServiceUdas.ComponentType, _componentType);
                fitting.SetUserProperty(StructuraServiceUdas.SchemaVersion, _schemaVersion);
                fitting.SetUserProperty(StructuraServiceUdas.LastOperationId, _lastOperationId);
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
        }

        private static Plane CreatePlane(TeklaApplyFittingPayload payload)
        {
            var axisY = payload.KeepSide == "positive"
                ? payload.Plane.AxisY
                : new TeklaVector3(-payload.Plane.AxisY.X, -payload.Plane.AxisY.Y, -payload.Plane.AxisY.Z);
            return new Plane
            {
                Origin = Point(payload.Plane.Origin),
                AxisX = Vector(payload.Plane.AxisX),
                AxisY = Vector(axisY),
            };
        }

        private static Plane Clone(Plane source) => new()
        {
            Origin = new Point(source.Origin.X, source.Origin.Y, source.Origin.Z),
            AxisX = new Vector(source.AxisX.X, source.AxisX.Y, source.AxisX.Z),
            AxisY = new Vector(source.AxisY.X, source.AxisY.Y, source.AxisY.Z),
        };

        private static Point Point(TeklaVector3 value) => new(value.X, value.Y, value.Z);
        private static Vector Vector(TeklaVector3 value) => new(value.X, value.Y, value.Z);
        private static double[] PointArray(Point value) => new[] { value.X, value.Y, value.Z };
        private static double[] VectorArray(Vector value) => new[] { value.X, value.Y, value.Z };
    }
}
