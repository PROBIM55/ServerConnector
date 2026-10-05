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
    public sealed class TeklaCreateBeamExecutor : ITeklaNativeCommandExecutor
    {
        private const string ExecutorId = "tekla-native.create-beam.v1";
        private const string ComponentType = "ConstructiveBeamV1";

        public TeklaPlanExecutorRegistration Registration { get; } = new(
            ExecutorId,
            "create-beam",
            new[] { "beam", "udaStamp" },
            applyReady: true);

        public ITeklaPreparedMutation<TeklaNativeCommandReadback> Prepare(
            Model model,
            TeklaPlanDocument plan,
            TeklaPlanCommand command,
            string operationId)
        {
            var parsed = TeklaPlanPayloads.ParseCreateBeam(command);
            if (!parsed.Success || parsed.Value is null)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_BEAM_PAYLOAD_INVALID",
                    string.Join(" ", parsed.Diagnostics.Select(static item => item.Message)));
            }

            var externalObjectId = TeklaPlanOwnershipIdentity.Create(plan, command);
            var existing = FindExisting(model, externalObjectId);
            return new PreparedBeamMutation(
                model,
                plan,
                command,
                parsed.Value,
                operationId,
                externalObjectId,
                existing);
        }

        private static Beam? FindExisting(Model model, string externalObjectId)
        {
            var matches = new List<Beam>();
            var enumerator = model.GetModelObjectSelector()
                .GetAllObjectsWithType(ModelObject.ModelObjectEnum.BEAM);
            while (enumerator.MoveNext())
            {
                if (enumerator.Current is not Beam beam) continue;
                var candidate = string.Empty;
                if (!beam.GetUserProperty(StructuraServiceUdas.ExternalObjectId, ref candidate)) continue;
                if (string.Equals(candidate, externalObjectId, StringComparison.Ordinal)) matches.Add(beam);
            }

            if (matches.Count > 1)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_NATIVE_IDENTITY_DUPLICATE",
                    $"Straight Beam has {matches.Count} instances with ownership id '{externalObjectId}'.");
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

        private sealed class PreparedBeamMutation : ITeklaPreparedMutation<TeklaNativeCommandReadback>
        {
            private const double Tolerance = 1e-3;
            private readonly Model _model;
            private readonly TeklaPlanDocument _plan;
            private readonly TeklaPlanCommand _command;
            private readonly TeklaCreateBeamPayload _payload;
            private readonly string _operationId;
            private readonly string _externalObjectId;
            private readonly Beam _beam;
            private readonly BeamSnapshot? _snapshot;
            private readonly bool _created;
            private readonly BeamPosition _position;
            private readonly string _profile;
            private readonly string _material;
            private bool _inserted;
            private bool _applied;

            public PreparedBeamMutation(
                Model model,
                TeklaPlanDocument plan,
                TeklaPlanCommand command,
                TeklaCreateBeamPayload payload,
                string operationId,
                string externalObjectId,
                Beam? existing)
            {
                _model = model;
                _plan = plan;
                _command = command;
                _payload = payload;
                _operationId = operationId;
                _externalObjectId = externalObjectId;
                _created = existing is null;
                _beam = existing ?? new Beam();
                _snapshot = existing is null ? null : BeamSnapshot.Capture(existing);
                _position = BeamPosition.From(payload.Placement);
                _profile = ResolveProfile(payload.Profile);
                _material = ResolveMaterial(payload.Material);
            }

            public string CommandId => _command.CommandId;

            public void Apply()
            {
                try
                {
                    ConfigureBeam();
                    if (_created)
                    {
                        if (!_beam.Insert())
                            throw Failure("TEKLA_PLAN_BEAM_INSERT_FAILED", "Beam.Insert() returned false.");
                        _inserted = true;
                    }

                    StampOwnership();
                    if (!_beam.Modify())
                        throw Failure("TEKLA_PLAN_BEAM_MODIFY_FAILED", "Beam.Modify() returned false.");
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
                    throw Failure("TEKLA_PLAN_BEAM_NOT_APPLIED", "Readback was requested before apply completed.");

                VerifyPoint("start", _beam.StartPoint, _payload.Start);
                VerifyPoint("end", _beam.EndPoint, _payload.End);
                VerifyEqual("profile", _beam.Profile.ProfileString, _profile);
                VerifyEqual("material", _beam.Material.MaterialString, _material);
                VerifyPosition();

                var externalObjectId = ReadStringUda(StructuraServiceUdas.ExternalObjectId);
                var componentType = ReadStringUda(StructuraServiceUdas.ComponentType);
                var operationId = ReadStringUda(StructuraServiceUdas.LastOperationId);
                var schemaVersion = ReadIntUda(StructuraServiceUdas.SchemaVersion);
                VerifyEqual("ownership id", externalObjectId, _externalObjectId);
                VerifyEqual("component type", componentType, ComponentType);
                VerifyEqual("last operation id", operationId, _operationId);
                if (schemaVersion != _plan.SchemaVersion)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native schema version does not match the plan.");

                var guid = _model.GetGUIDByIdentifier(_beam.Identifier) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(guid) || _beam.Identifier.ID <= 0)
                    throw Failure("TEKLA_PLAN_READBACK_IDENTITY_MISSING", "Tekla did not return a persistent Beam identity.");

                return new TeklaNativeCommandReadback
                {
                    CommandId = _command.CommandId,
                    CommandKind = _command.Kind,
                    Action = _created ? "created" : "modified",
                    ExternalObjectId = _externalObjectId,
                    TeklaGuid = guid,
                    TeklaId = _beam.Identifier.ID,
                    Start = PointArray(_beam.StartPoint),
                    End = PointArray(_beam.EndPoint),
                    Profile = _beam.Profile.ProfileString,
                    Material = _beam.Material.MaterialString,
                    Plane = _beam.Position.Plane.ToString(),
                    PlaneOffsetMm = _beam.Position.PlaneOffset,
                    Depth = _beam.Position.Depth.ToString(),
                    DepthOffsetMm = _beam.Position.DepthOffset,
                    Rotation = _beam.Position.Rotation.ToString(),
                    RotationOffsetDeg = _beam.Position.RotationOffset,
                    ComponentType = componentType,
                    SchemaVersion = schemaVersion,
                    LastOperationId = operationId,
                };
            }

            public void Restore()
            {
                if (_created)
                {
                    if (_inserted && !_beam.Delete())
                        throw Failure("TEKLA_PLAN_BEAM_ROLLBACK_DELETE_FAILED", "Beam.Delete() returned false.");
                    _inserted = false;
                    _applied = false;
                    return;
                }

                if (_snapshot is null)
                    throw Failure("TEKLA_PLAN_BEAM_ROLLBACK_STATE_MISSING", "Existing Beam snapshot is missing.");
                _snapshot.Restore(_beam);
                if (!_beam.Modify())
                    throw Failure("TEKLA_PLAN_BEAM_ROLLBACK_MODIFY_FAILED", "Beam.Modify() returned false during rollback.");
                _applied = false;
            }

            private void ConfigureBeam()
            {
                _beam.StartPoint = Point(_payload.Start);
                _beam.EndPoint = Point(_payload.End);
                _beam.Profile.ProfileString = _profile;
                _beam.Material.MaterialString = _material;
                _beam.Position.Plane = _position.Plane;
                _beam.Position.PlaneOffset = _position.PlaneOffsetMm;
                _beam.Position.Depth = _position.Depth;
                _beam.Position.DepthOffset = _position.DepthOffsetMm;
                _beam.Position.Rotation = Position.RotationEnum.FRONT;
                _beam.Position.RotationOffset = _position.RotationDeg;
            }

            private void StampOwnership()
            {
                RequireUda(_beam.SetUserProperty(StructuraServiceUdas.ExternalObjectId, _externalObjectId), StructuraServiceUdas.ExternalObjectId);
                RequireUda(_beam.SetUserProperty(StructuraServiceUdas.ComponentType, ComponentType), StructuraServiceUdas.ComponentType);
                RequireUda(_beam.SetUserProperty(StructuraServiceUdas.SchemaVersion, _plan.SchemaVersion), StructuraServiceUdas.SchemaVersion);
                RequireUda(_beam.SetUserProperty(StructuraServiceUdas.LastOperationId, _operationId), StructuraServiceUdas.LastOperationId);
            }

            private void RequireUda(bool result, string name)
            {
                if (!result)
                    throw Failure("TEKLA_PLAN_UDA_STAMP_FAILED", $"SetUserProperty('{name}') returned false.");
            }

            private void RestorePartialApply()
            {
                try
                {
                    if (_created)
                    {
                        if (_inserted) _beam.Delete();
                        _inserted = false;
                    }
                    else if (_snapshot is not null)
                    {
                        _snapshot.Restore(_beam);
                        _beam.Modify();
                    }
                }
                finally
                {
                    _applied = false;
                }
            }

            private void VerifyPosition()
            {
                if (_beam.Position.Plane != _position.Plane ||
                    Math.Abs(_beam.Position.PlaneOffset - _position.PlaneOffsetMm) > Tolerance ||
                    _beam.Position.Depth != _position.Depth ||
                    Math.Abs(_beam.Position.DepthOffset - _position.DepthOffsetMm) > Tolerance ||
                    _beam.Position.Rotation != Position.RotationEnum.FRONT ||
                    Math.Abs(_beam.Position.RotationOffset - _position.RotationDeg) > Tolerance)
                {
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native Beam placement does not match the plan.");
                }
            }

            private void VerifyPoint(string role, Point actual, TeklaVector3 expected)
            {
                if (Math.Abs(actual.X - expected.X) > Tolerance ||
                    Math.Abs(actual.Y - expected.Y) > Tolerance ||
                    Math.Abs(actual.Z - expected.Z) > Tolerance)
                {
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native Beam {role} point does not match the plan.");
                }
            }

            private void VerifyEqual(string role, string actual, string expected)
            {
                if (!string.Equals(actual?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native Beam {role} does not match the plan.");
            }

            private string ReadStringUda(string name)
            {
                var value = string.Empty;
                if (!_beam.GetUserProperty(name, ref value))
                    throw Failure("TEKLA_PLAN_READBACK_UDA_MISSING", $"Native Beam UDA '{name}' is missing.");
                return value;
            }

            private int ReadIntUda(string name)
            {
                var value = 0;
                if (!_beam.GetUserProperty(name, ref value))
                    throw Failure("TEKLA_PLAN_READBACK_UDA_MISSING", $"Native Beam UDA '{name}' is missing.");
                return value;
            }

            private TeklaNativeExecutionException Failure(string code, string message)
                => new(code, $"Command '{_command.CommandId}': {message}");
        }

        private sealed class BeamSnapshot
        {
            private readonly Point _start;
            private readonly Point _end;
            private readonly string _profile;
            private readonly string _material;
            private readonly Position.PlaneEnum _plane;
            private readonly double _planeOffset;
            private readonly Position.DepthEnum _depth;
            private readonly double _depthOffset;
            private readonly Position.RotationEnum _rotation;
            private readonly double _rotationOffset;
            private readonly string _externalObjectId;
            private readonly string _componentType;
            private readonly int _schemaVersion;
            private readonly string _lastOperationId;

            private BeamSnapshot(Beam beam)
            {
                _start = new Point(beam.StartPoint.X, beam.StartPoint.Y, beam.StartPoint.Z);
                _end = new Point(beam.EndPoint.X, beam.EndPoint.Y, beam.EndPoint.Z);
                _profile = beam.Profile.ProfileString;
                _material = beam.Material.MaterialString;
                _plane = beam.Position.Plane;
                _planeOffset = beam.Position.PlaneOffset;
                _depth = beam.Position.Depth;
                _depthOffset = beam.Position.DepthOffset;
                _rotation = beam.Position.Rotation;
                _rotationOffset = beam.Position.RotationOffset;
                _externalObjectId = ReadString(beam, StructuraServiceUdas.ExternalObjectId);
                _componentType = ReadString(beam, StructuraServiceUdas.ComponentType);
                _schemaVersion = ReadInt(beam, StructuraServiceUdas.SchemaVersion);
                _lastOperationId = ReadString(beam, StructuraServiceUdas.LastOperationId);
            }

            public static BeamSnapshot Capture(Beam beam) => new(beam);

            public void Restore(Beam beam)
            {
                beam.StartPoint = new Point(_start.X, _start.Y, _start.Z);
                beam.EndPoint = new Point(_end.X, _end.Y, _end.Z);
                beam.Profile.ProfileString = _profile;
                beam.Material.MaterialString = _material;
                beam.Position.Plane = _plane;
                beam.Position.PlaneOffset = _planeOffset;
                beam.Position.Depth = _depth;
                beam.Position.DepthOffset = _depthOffset;
                beam.Position.Rotation = _rotation;
                beam.Position.RotationOffset = _rotationOffset;
                beam.SetUserProperty(StructuraServiceUdas.ExternalObjectId, _externalObjectId);
                beam.SetUserProperty(StructuraServiceUdas.ComponentType, _componentType);
                beam.SetUserProperty(StructuraServiceUdas.SchemaVersion, _schemaVersion);
                beam.SetUserProperty(StructuraServiceUdas.LastOperationId, _lastOperationId);
            }

            private static string ReadString(Beam beam, string name)
            {
                var value = string.Empty;
                try { beam.GetUserProperty(name, ref value); } catch { }
                return value;
            }

            private static int ReadInt(Beam beam, string name)
            {
                var value = 0;
                try { beam.GetUserProperty(name, ref value); } catch { }
                return value;
            }
        }

        private sealed class BeamPosition
        {
            public Position.PlaneEnum Plane { get; private set; } = Position.PlaneEnum.MIDDLE;
            public double PlaneOffsetMm { get; private set; }
            public Position.DepthEnum Depth { get; private set; } = Position.DepthEnum.MIDDLE;
            public double DepthOffsetMm { get; private set; }
            public double RotationDeg { get; private set; }

            public static BeamPosition From(TeklaSectionPlacementSpec? placement)
            {
                var result = new BeamPosition();
                if (placement is null) return result;
                result.PlaneOffsetMm = placement.OffsetXmm ?? 0;
                result.DepthOffsetMm = placement.OffsetYmm ?? 0;
                result.RotationDeg = placement.RotationDeg ?? 0;
                switch (placement.Anchor)
                {
                    case "left": result.Plane = Position.PlaneEnum.LEFT; break;
                    case "right": result.Plane = Position.PlaneEnum.RIGHT; break;
                    case "top": result.Depth = Position.DepthEnum.FRONT; break;
                    case "bottom": result.Depth = Position.DepthEnum.BEHIND; break;
                }
                return result;
            }
        }

        private static string ResolveProfile(TeklaProfileSpec profile)
        {
            if (string.Equals(profile.Kind, "catalog", StringComparison.Ordinal)) return profile.Name!;
            return "PL" + Number(profile.ThicknessMm!.Value) + "*" + Number(profile.WidthMm!.Value);
        }

        private static string ResolveMaterial(TeklaMaterialSpec material)
            => !string.IsNullOrWhiteSpace(material.Grade)
                ? material.Grade!
                : !string.IsNullOrWhiteSpace(material.Name)
                    ? material.Name!
                    : material.Id;

        private static string Number(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);
        private static Point Point(TeklaVector3 value) => new(value.X, value.Y, value.Z);
        private static double[] PointArray(Point value) => new[] { value.X, value.Y, value.Z };
    }
}
