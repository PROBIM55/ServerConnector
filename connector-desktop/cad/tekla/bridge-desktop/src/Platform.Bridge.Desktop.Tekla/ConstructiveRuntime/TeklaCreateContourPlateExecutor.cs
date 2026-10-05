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
    /// Native executor for a simple, line-only planar plate. The contour is
    /// canonicalized against the payload plane normal before it reaches Tekla.
    /// </summary>
    public sealed class TeklaCreateContourPlateExecutor : ITeklaNativeCommandExecutor
    {
        private const string ExecutorId = "tekla-native.create-contour-plate.v1";
        private const string ComponentType = "ConstructiveContourPlateV1";

        public TeklaPlanExecutorRegistration Registration { get; } = new(
            ExecutorId,
            "create-contour-plate",
            new[] { "contourPlate", "udaStamp" },
            applyReady: true);

        public ITeklaPreparedMutation<TeklaNativeCommandReadback> Prepare(
            Model model,
            TeklaPlanDocument plan,
            TeklaPlanCommand command,
            string operationId)
        {
            var parsed = TeklaPlanPayloads.ParseCreateContourPlate(command);
            if (!parsed.Success || parsed.Value is null)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_CONTOUR_PLATE_PAYLOAD_INVALID",
                    string.Join(" ", parsed.Diagnostics.Select(static item => item.Message)));
            }
            if (parsed.Value.Contour.Edges.Any(static edge => edge.Kind != "line"))
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_CONTOUR_PLATE_ARCS_UNSUPPORTED",
                    "Native simple ContourPlate executor does not support arc edges.");
            }

            var externalObjectId = TeklaPlanOwnershipIdentity.Create(plan, command);
            var existing = FindExisting(model, externalObjectId);
            return new PreparedContourPlateMutation(
                model,
                plan,
                command,
                parsed.Value,
                operationId,
                externalObjectId,
                existing);
        }

        private static ContourPlate? FindExisting(Model model, string externalObjectId)
        {
            var matches = new List<ContourPlate>();
            var enumerator = model.GetModelObjectSelector()
                .GetAllObjectsWithType(ModelObject.ModelObjectEnum.CONTOURPLATE);
            while (enumerator.MoveNext())
            {
                if (enumerator.Current is not ContourPlate plate) continue;
                var candidate = string.Empty;
                if (!plate.GetUserProperty(StructuraServiceUdas.ExternalObjectId, ref candidate)) continue;
                if (string.Equals(candidate, externalObjectId, StringComparison.Ordinal)) matches.Add(plate);
            }

            if (matches.Count > 1)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_NATIVE_IDENTITY_DUPLICATE",
                    $"ContourPlate has {matches.Count} instances with ownership id '{externalObjectId}'.");
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

        private sealed class PreparedContourPlateMutation : ITeklaPreparedMutation<TeklaNativeCommandReadback>
        {
            private const double Tolerance = 1e-3;
            private readonly Model _model;
            private readonly TeklaPlanDocument _plan;
            private readonly TeklaPlanCommand _command;
            private readonly TeklaCreateContourPlatePayload _payload;
            private readonly string _operationId;
            private readonly string _externalObjectId;
            private readonly ContourPlate _plate;
            private readonly ContourPlateSnapshot? _snapshot;
            private readonly bool _created;
            private readonly IReadOnlyList<Point> _expectedPoints;
            private readonly string _profile;
            private readonly string _material;
            private bool _inserted;
            private bool _applied;

            public PreparedContourPlateMutation(
                Model model,
                TeklaPlanDocument plan,
                TeklaPlanCommand command,
                TeklaCreateContourPlatePayload payload,
                string operationId,
                string externalObjectId,
                ContourPlate? existing)
            {
                _model = model;
                _plan = plan;
                _command = command;
                _payload = payload;
                _operationId = operationId;
                _externalObjectId = externalObjectId;
                _created = existing is null;
                _plate = existing ?? new ContourPlate();
                _snapshot = existing is null ? null : ContourPlateSnapshot.Capture(existing);
                _expectedPoints = TeklaContourPlateGeometry
                    .BuildCanonicalGlobalContour(payload)
                    .Points;
                _profile = "PL" + Number(payload.ThicknessMm);
                _material = ResolveMaterial(payload.Material);
            }

            public string CommandId => _command.CommandId;

            public void Apply()
            {
                try
                {
                    ConfigurePlate();
                    if (_created)
                    {
                        if (!_plate.Insert())
                            throw Failure("TEKLA_PLAN_CONTOUR_PLATE_INSERT_FAILED", "ContourPlate.Insert() returned false.");
                        _inserted = true;
                    }

                    StampOwnership();
                    if (!_plate.Modify())
                        throw Failure("TEKLA_PLAN_CONTOUR_PLATE_MODIFY_FAILED", "ContourPlate.Modify() returned false.");
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
                    throw Failure("TEKLA_PLAN_CONTOUR_PLATE_NOT_APPLIED", "Readback was requested before apply completed.");

                var actualPoints = ReadContourPoints(_plate);
                if (actualPoints.Count != _expectedPoints.Count)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native ContourPlate point count does not match the plan.");
                for (var index = 0; index < actualPoints.Count; index++)
                {
                    VerifyPoint($"contour[{index}]", actualPoints[index], _expectedPoints[index]);
                }
                VerifyEqual("profile", _plate.Profile.ProfileString, _profile);
                VerifyEqual("material", _plate.Material.MaterialString, _material);
                if (_plate.Position.Depth != Position.DepthEnum.MIDDLE || Math.Abs(_plate.Position.DepthOffset) > Tolerance)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native ContourPlate depth placement does not match its canonical middle plane.");

                var externalObjectId = ReadStringUda(StructuraServiceUdas.ExternalObjectId);
                var componentType = ReadStringUda(StructuraServiceUdas.ComponentType);
                var operationId = ReadStringUda(StructuraServiceUdas.LastOperationId);
                var schemaVersion = ReadIntUda(StructuraServiceUdas.SchemaVersion);
                VerifyEqual("ownership id", externalObjectId, _externalObjectId);
                VerifyEqual("component type", componentType, ComponentType);
                VerifyEqual("last operation id", operationId, _operationId);
                if (schemaVersion != _plan.SchemaVersion)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native schema version does not match the plan.");

                var guid = _model.GetGUIDByIdentifier(_plate.Identifier) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(guid) || _plate.Identifier.ID <= 0)
                    throw Failure("TEKLA_PLAN_READBACK_IDENTITY_MISSING", "Tekla did not return a persistent ContourPlate identity.");

                return new TeklaNativeCommandReadback
                {
                    CommandId = _command.CommandId,
                    CommandKind = _command.Kind,
                    Action = _created ? "created" : "modified",
                    ExternalObjectId = _externalObjectId,
                    TeklaGuid = guid,
                    TeklaId = _plate.Identifier.ID,
                    Contour = actualPoints.Select(static point => PointArray(point)).ToArray(),
                    ThicknessMm = _payload.ThicknessMm,
                    ExtrusionSide = _payload.ExtrusionSide,
                    Profile = _plate.Profile.ProfileString,
                    Material = _plate.Material.MaterialString,
                    Depth = _plate.Position.Depth.ToString(),
                    DepthOffsetMm = _plate.Position.DepthOffset,
                    ComponentType = componentType,
                    SchemaVersion = schemaVersion,
                    LastOperationId = operationId,
                };
            }

            public void Restore()
            {
                if (_created)
                {
                    if (_inserted && !_plate.Delete())
                        throw Failure("TEKLA_PLAN_CONTOUR_PLATE_ROLLBACK_DELETE_FAILED", "ContourPlate.Delete() returned false.");
                    _inserted = false;
                    _applied = false;
                    return;
                }

                if (_snapshot is null)
                    throw Failure("TEKLA_PLAN_CONTOUR_PLATE_ROLLBACK_STATE_MISSING", "Existing ContourPlate snapshot is missing.");
                _snapshot.Restore(_plate);
                if (!_plate.Modify())
                    throw Failure("TEKLA_PLAN_CONTOUR_PLATE_ROLLBACK_MODIFY_FAILED", "ContourPlate.Modify() returned false during rollback.");
                _applied = false;
            }

            private void ConfigurePlate()
            {
                var contour = new Contour();
                foreach (var point in _expectedPoints)
                {
                    contour.AddContourPoint(new ContourPoint(
                        Clone(point),
                        new Chamfer { Type = Chamfer.ChamferTypeEnum.CHAMFER_NONE }));
                }
                _plate.Contour = contour;
                _plate.Profile.ProfileString = _profile;
                _plate.Material.MaterialString = _material;
                _plate.Position.Depth = Position.DepthEnum.MIDDLE;
                _plate.Position.DepthOffset = 0;
            }

            private void StampOwnership()
            {
                RequireUda(_plate.SetUserProperty(StructuraServiceUdas.ExternalObjectId, _externalObjectId), StructuraServiceUdas.ExternalObjectId);
                RequireUda(_plate.SetUserProperty(StructuraServiceUdas.ComponentType, ComponentType), StructuraServiceUdas.ComponentType);
                RequireUda(_plate.SetUserProperty(StructuraServiceUdas.SchemaVersion, _plan.SchemaVersion), StructuraServiceUdas.SchemaVersion);
                RequireUda(_plate.SetUserProperty(StructuraServiceUdas.LastOperationId, _operationId), StructuraServiceUdas.LastOperationId);
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
                        if (_inserted) _plate.Delete();
                        _inserted = false;
                    }
                    else if (_snapshot is not null)
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

            private void VerifyPoint(string role, Point actual, Point expected)
            {
                if (Math.Abs(actual.X - expected.X) > Tolerance ||
                    Math.Abs(actual.Y - expected.Y) > Tolerance ||
                    Math.Abs(actual.Z - expected.Z) > Tolerance)
                {
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native ContourPlate {role} point does not match the plan.");
                }
            }

            private void VerifyEqual(string role, string actual, string expected)
            {
                if (!string.Equals(actual?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native ContourPlate {role} does not match the plan.");
            }

            private string ReadStringUda(string name)
            {
                var value = string.Empty;
                if (!_plate.GetUserProperty(name, ref value))
                    throw Failure("TEKLA_PLAN_READBACK_UDA_MISSING", $"Native ContourPlate UDA '{name}' is missing.");
                return value;
            }

            private int ReadIntUda(string name)
            {
                var value = 0;
                if (!_plate.GetUserProperty(name, ref value))
                    throw Failure("TEKLA_PLAN_READBACK_UDA_MISSING", $"Native ContourPlate UDA '{name}' is missing.");
                return value;
            }

            private TeklaNativeExecutionException Failure(string code, string message)
                => new(code, $"Command '{_command.CommandId}': {message}");
        }

        private sealed class ContourPlateSnapshot
        {
            private readonly IReadOnlyList<ContourPointSnapshot> _points;
            private readonly string _name;
            private readonly string _finish;
            private readonly string _className;
            private readonly string _profile;
            private readonly string _material;
            private readonly Position.DepthEnum _depth;
            private readonly double _depthOffset;
            private readonly string _externalObjectId;
            private readonly string _componentType;
            private readonly int _schemaVersion;
            private readonly string _lastOperationId;

            private ContourPlateSnapshot(ContourPlate plate)
            {
                _points = ReadContourPoints(plate).Select(static point => ContourPointSnapshot.Capture(point)).ToArray();
                _name = plate.Name;
                _finish = plate.Finish;
                _className = plate.Class;
                _profile = plate.Profile.ProfileString;
                _material = plate.Material.MaterialString;
                _depth = plate.Position.Depth;
                _depthOffset = plate.Position.DepthOffset;
                _externalObjectId = ReadString(plate, StructuraServiceUdas.ExternalObjectId);
                _componentType = ReadString(plate, StructuraServiceUdas.ComponentType);
                _schemaVersion = ReadInt(plate, StructuraServiceUdas.SchemaVersion);
                _lastOperationId = ReadString(plate, StructuraServiceUdas.LastOperationId);
            }

            public static ContourPlateSnapshot Capture(ContourPlate plate) => new(plate);

            public void Restore(ContourPlate plate)
            {
                var contour = new Contour();
                foreach (var point in _points) contour.AddContourPoint(point.Create());
                plate.Contour = contour;
                plate.Name = _name;
                plate.Finish = _finish;
                plate.Class = _className;
                plate.Profile.ProfileString = _profile;
                plate.Material.MaterialString = _material;
                plate.Position.Depth = _depth;
                plate.Position.DepthOffset = _depthOffset;
                plate.SetUserProperty(StructuraServiceUdas.ExternalObjectId, _externalObjectId);
                plate.SetUserProperty(StructuraServiceUdas.ComponentType, _componentType);
                plate.SetUserProperty(StructuraServiceUdas.SchemaVersion, _schemaVersion);
                plate.SetUserProperty(StructuraServiceUdas.LastOperationId, _lastOperationId);
            }

            private static string ReadString(ContourPlate plate, string name)
            {
                var value = string.Empty;
                try { plate.GetUserProperty(name, ref value); } catch { }
                return value;
            }

            private static int ReadInt(ContourPlate plate, string name)
            {
                var value = 0;
                try { plate.GetUserProperty(name, ref value); } catch { }
                return value;
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

            public static ContourPointSnapshot Capture(ContourPoint point) => new(point);

            public ContourPoint Create()
                => new(
                    Clone(_point),
                    new Chamfer { Type = _type, X = _x, Y = _y, DZ1 = _dz1, DZ2 = _dz2 });
        }

        private static IReadOnlyList<ContourPoint> ReadContourPoints(ContourPlate plate)
            => plate.Contour?.ContourPoints?.OfType<ContourPoint>().ToArray() ?? Array.Empty<ContourPoint>();

        private static string ResolveMaterial(TeklaMaterialSpec material)
            => !string.IsNullOrWhiteSpace(material.Grade)
                ? material.Grade!
                : !string.IsNullOrWhiteSpace(material.Name)
                    ? material.Name!
                    : material.Id;

        private static string Number(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);
        private static Point Clone(Point point) => new(point.X, point.Y, point.Z);
        private static double[] PointArray(Point point) => new[] { point.X, point.Y, point.Z };
    }
}
